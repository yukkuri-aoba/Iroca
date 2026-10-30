// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.IO;

namespace Iroca
{
    /// <summary>
    /// 編集中のマスクの履歴(0.4.0 の計画のフェーズ 5)。Undo に載るのは文書の版の番号(<see cref="IrocaEditingDocument.maskRevision"/>)
    /// だけで、マスクの中身はここがヒープの外に RLE で持つ。以前は全層の Base64 の文字列を、ストロークのたびに Mono のヒープで
    /// 作り直して文書ごと Undo に記録し、スライダーの Undo でも読み直していた。
    /// <para>
    /// 変わっていない層は前の版と同じ塊を共有するので、1 ストロークで増えるのは塗った層の分だけ。
    /// 再読み込み(Play の前後も)で Undo の記録が残るかは Unity の版で違う(2022.3 は文書の記録を捨て、Unity 6 は残す。
    /// <c>UndoReloadScenario</c> で確かめた)。残る版でも戻せるよう、再読み込みの前にファイルへ退避して読み戻す
    /// (<see cref="SaveTo"/> / <see cref="LoadFrom"/>)。メインスレッド専用。
    /// </para>
    /// </summary>
    internal sealed class MaskHistory
    {
        // 履歴が持つ量の上限。超えたら古い版から手放す(手放した版へは戻せない。その旨を知らせる)。細かい塗りの多い 4K のマスクで
        // 1 ストローク約 0.5MB(UndoBench の多い場合)なので、上限まで約 500 回。ふつうのマスクでは 1 回 10KB ほどで届かない。
        private const long DefaultMaxBytes = 256L * 1024 * 1024;

        // 新しく作る版が前の版を押し出さないよう、上限を超えても残す版の数(いまの版と、その 1 つ前)。
        private const int MinVersionsKept = 2;

        private sealed class Entry
        {
            public string ZoneId;       // null = 全ゾーン共通の除外
            public bool Include;
            public NativeBuffer Rle;
        }

        private sealed class Version
        {
            public long Revision;
            public int Width, Height;
            public readonly List<Entry> Entries = new List<Entry>();   // 並びは層を足した順(共通・除外・含める)
        }

        // 退避のファイルの見出し(同じエディタの中で書いて読むだけなので、形を変えたら版を上げて読まない)。
        private const int SpillMagic = 0x53484D49;   // "IMHS"
        private const int SpillFormat = 1;
        private const int SpillChunkBytes = 64 * 1024;

        private readonly List<Version> _versions = new List<Version>();   // 番号の昇順
        private readonly long _maxBytes;
        private long _bytes;
        private long _lastRevision;

        internal MaskHistory(long maxBytes = DefaultMaxBytes) => _maxBytes = maxBytes;

        /// <summary>持っている版の数。</summary>
        public int Count => _versions.Count;

        /// <summary>持っている RLE の量(共有している塊は 1 回だけ数える)。</summary>
        public long Bytes => _bytes;

        public bool Contains(long revision) => Find(revision) != null;

        /// <summary>
        /// 今のマスクを新しい版にして、番号を返す。<paramref name="baseRevision"/> はいまの版で、それより新しい版(Undo で戻した先の
        /// やり直しの分)はもう戻れないので手放す。中身が <paramref name="baseRevision"/> の版と同じ層は同じ塊を共有する。
        /// 全部 0 の層は載せない(保存形式と同じ)。
        /// </summary>
        public long Capture(MaskWorkspace masks, long baseRevision)
        {
            while (_versions.Count > 0 && _versions[_versions.Count - 1].Revision > baseRevision)
            {
                Drop(_versions[_versions.Count - 1]);
                _versions.RemoveAt(_versions.Count - 1);
            }

            var basis = Find(baseRevision);
            // 番号は時刻から作る。再読み込みで履歴が空になっても、前の番号と重ならない。
            _lastRevision = Math.Max(DateTime.UtcNow.Ticks, _lastRevision + 1);
            var version = new Version { Revision = _lastRevision, Width = masks.Width, Height = masks.Height };
            AddEntry(version, null, false, masks.Common, basis);
            foreach (var kv in masks.Layers(false)) AddEntry(version, kv.Key, false, kv.Value, basis);
            foreach (var kv in masks.Layers(true)) AddEntry(version, kv.Key, true, kv.Value, basis);
            _versions.Add(version);

            while (_bytes > _maxBytes && _versions.Count > MinVersionsKept)
            {
                Drop(_versions[0]);
                _versions.RemoveAt(0);
            }
            return version.Revision;
        }

        private void AddEntry(Version version, string zoneId, bool include, NativeBitMask mask, Version basis)
        {
            var rle = MaskRle.EncodeBitsNative(mask);
            if (rle == null) return;
            var same = FindEntry(basis, zoneId, include);
            if (same != null && SameBytes(same.Rle, rle))
            {
                rle.Release();
                rle = same.Rle.AddRef();
            }
            else
                _bytes += rle.Bytes;
            version.Entries.Add(new Entry { ZoneId = zoneId, Include = include, Rle = rle });
        }

        /// <summary>
        /// マスクをその版の中身に置き換える。その版を持っていなければ false(マスクは変えない)。中身が同じ層は展開し直さず、
        /// 今のものをそのまま使う。寸法が無効な版(まだマスクが無かったとき)なら層を全部外す(寸法はそのまま)。
        /// </summary>
        public bool TryRestore(long revision, MaskWorkspace masks)
        {
            var version = Find(revision);
            if (version == null) return false;
            if (version.Width <= 0 || version.Height <= 0)
            {
                masks.Clear();
                return true;
            }

            bool sameSize = masks.Width == version.Width && masks.Height == version.Height;
            var restored = new NativeBitMask[version.Entries.Count];
            for (int i = 0; i < restored.Length; i++)
            {
                var e = version.Entries[i];
                var current = !sameSize ? null : e.ZoneId == null ? masks.Common : masks.Layer(e.Include, e.ZoneId);
                restored[i] = current != null && MaskRle.MatchesBits(current, e.Rle)
                    ? current.AddRef()
                    : MaskRle.DecodeBitsNative(e.Rle);
            }
            masks.Reset(version.Width, version.Height);
            for (int i = 0; i < restored.Length; i++)
            {
                var e = version.Entries[i];
                if (e.ZoneId == null) masks.SetCommon(restored[i]);
                else masks.SetLayer(e.Include, e.ZoneId, restored[i]);
            }
            return true;
        }

        /// <summary>全部手放す(窓を閉じたとき)。</summary>
        public void Clear()
        {
            foreach (var v in _versions) Drop(v);
            _versions.Clear();
            _bytes = 0;
        }

        // ─────────────── 再読み込みをまたぐ ───────────────

        /// <summary>
        /// 履歴をファイルへ書く(再読み込みの直前)。共有している塊は 1 回だけ書く。書けなければ投げる(呼び出し側がファイルを消す)。
        /// </summary>
        public unsafe void SaveTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var blobs = new List<NativeBuffer>();
            var indexOf = new Dictionary<NativeBuffer, int>();
            foreach (var v in _versions)
                foreach (var e in v.Entries)
                    if (!indexOf.ContainsKey(e.Rle))
                    {
                        indexOf[e.Rle] = blobs.Count;
                        blobs.Add(e.Rle);
                    }

            using (var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
            {
                w.Write(SpillMagic);
                w.Write(SpillFormat);
                w.Write(_lastRevision);
                w.Write(blobs.Count);
                var chunk = new byte[SpillChunkBytes];
                foreach (var blob in blobs)
                {
                    w.Write(blob.Bytes);
                    for (long off = 0; off < blob.Bytes; off += chunk.Length)
                    {
                        int n = (int)Math.Min(chunk.Length, blob.Bytes - off);
                        System.Runtime.InteropServices.Marshal.Copy((IntPtr)((byte*)blob.Ptr + off), chunk, 0, n);
                        w.Write(chunk, 0, n);
                    }
                }
                w.Write(_versions.Count);
                foreach (var v in _versions)
                {
                    w.Write(v.Revision);
                    w.Write(v.Width);
                    w.Write(v.Height);
                    w.Write(v.Entries.Count);
                    foreach (var e in v.Entries)
                    {
                        w.Write(e.ZoneId ?? "");
                        w.Write(e.Include);
                        w.Write(indexOf[e.Rle]);
                    }
                }
            }
        }

        /// <summary>
        /// 履歴を <see cref="SaveTo"/> のファイルで置き換える。読めなければ false で、履歴は空になる(戻せないことはそこへ Undo した
        /// ときに知らせる)。中身は確かめてから持つ(壊れたラン列を展開すると範囲の外へ書く)。
        /// </summary>
        public unsafe bool LoadFrom(string path)
        {
            Clear();
            var blobs = new List<NativeBuffer>();
            bool ok = false;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var r = new BinaryReader(fs))
                {
                    if (r.ReadInt32() != SpillMagic || r.ReadInt32() != SpillFormat) return false;
                    _lastRevision = r.ReadInt64();
                    int blobCount = r.ReadInt32();
                    var chunk = new byte[SpillChunkBytes];
                    for (int i = 0; i < blobCount; i++)
                    {
                        long bytes = r.ReadInt64();
                        if (bytes <= 0 || bytes > fs.Length - fs.Position) return false;
                        var blob = new NativeBuffer(bytes, NativeUsage.Mask);
                        blobs.Add(blob);
                        for (long off = 0; off < bytes; off += chunk.Length)
                        {
                            int n = (int)Math.Min(chunk.Length, bytes - off);
                            if (r.Read(chunk, 0, n) != n) return false;
                            System.Runtime.InteropServices.Marshal.Copy(chunk, 0, (IntPtr)((byte*)blob.Ptr + off), n);
                        }
                        if (!IsValidRle(blob)) return false;
                    }
                    int versionCount = r.ReadInt32();
                    for (int i = 0; i < versionCount; i++)
                    {
                        var v = new Version { Revision = r.ReadInt64(), Width = r.ReadInt32(), Height = r.ReadInt32() };
                        _versions.Add(v);
                        int entries = r.ReadInt32();
                        for (int k = 0; k < entries; k++)
                        {
                            string zoneId = r.ReadString();
                            bool include = r.ReadBoolean();
                            int index = r.ReadInt32();
                            if (index < 0 || index >= blobs.Count) return false;
                            v.Entries.Add(new Entry { ZoneId = zoneId.Length > 0 ? zoneId : null, Include = include, Rle = blobs[index].AddRef() });
                        }
                    }
                }
                foreach (var blob in blobs)
                    if (blob.RefCount > 1) _bytes += blob.Bytes;
                ok = true;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is OverflowException
                                      || e is OutOfMemoryException || e is ArgumentException || e is FormatException)
            {
                return false;
            }
            finally
            {
                // 版が持っていない塊はここで手放す。読めなかったら版ごと手放す。
                foreach (var blob in blobs) blob.Release();
                if (!ok) Clear();
            }
        }

        // ヘッダとラン列の形(長さ・寸法の範囲・ランの合計が全画素)を確かめる。
        private static unsafe bool IsValidRle(NativeBuffer rle)
        {
            long bytes = rle.Bytes;
            if (bytes < 13 || (bytes - 9) % 4 != 0) return false;
            byte* b = (byte*)rle.Ptr;
            int w = *(int*)b, h = *(int*)(b + 4);
            if (w <= 0 || h <= 0 || w > MaskRle.MaxSide || h > MaskRle.MaxSide) return false;
            uint* run = (uint*)(b + 9);
            long runs = (bytes - 9) / 4, covered = 0;
            for (long i = 0; i < runs; i++)
            {
                if (run[i] == 0) return false;
                covered += run[i];
            }
            return covered == (long)w * h;
        }

        private void Drop(Version version)
        {
            foreach (var e in version.Entries)
            {
                if (e.Rle.RefCount == 1) _bytes -= e.Rle.Bytes;
                e.Rle.Release();
            }
        }

        private Version Find(long revision)
        {
            foreach (var v in _versions)
                if (v.Revision == revision) return v;
            return null;
        }

        private static Entry FindEntry(Version version, string zoneId, bool include)
        {
            if (version == null) return null;
            foreach (var e in version.Entries)
                if (e.Include == include && e.ZoneId == zoneId) return e;
            return null;
        }

        private static unsafe bool SameBytes(NativeBuffer a, NativeBuffer b)
        {
            if (a.Bytes != b.Bytes) return false;
            byte* pa = (byte*)a.Ptr, pb = (byte*)b.Ptr;
            long n = a.Bytes, i = 0;
            for (; i + 8 <= n; i += 8)
                if (*(ulong*)(pa + i) != *(ulong*)(pb + i)) return false;
            for (; i < n; i++)
                if (pa[i] != pb[i]) return false;
            return true;
        }
    }
}
