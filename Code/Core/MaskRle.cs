// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// マスク bool 配列の永続化コーデック(RLE + Base64)。
    /// UI(MaskPaintView)と headless 検証の両方から使うため Core に置く。
    /// フォーマット: "R:" プレフィックス + Base64(4byte W + 4byte H + 1byte 開始値 + uint32[] ランレングス列)。
    /// プレフィックスなしは旧 bitpack フォーマット(1bit/px)として後方互換デコードする。
    /// </summary>
    internal static class MaskRle
    {
        // マスク一辺の上限。Unity 2022 のテクスチャ最大辺(16384)に合わせる。マスクはテクスチャ
        // 解像度以下なのでこれを超えるヘッダは正規の Encode 出力ではあり得ない。
        // ヘッダの幅×高さだけを信じて bool[] を確保すると、数十バイトのプリセット JSON で
        // 約 2GB の確保を要求できる(共有プリセットを開いた瞬間に OOM / 長時間フリーズ)。
        internal const int MaxSide = 16384;

        // RLE フォーマットのプレフィックス。これが無い文字列は旧 bitpack として読む。
        private const string RlePrefix = "R:";

        // これ未満の画素数のマスクは並列にしない(スレッドの起動の方が高い)。MaskSnapshot.Pack と同じ値。
        private const int EncodeParallelMinPixels = 1 << 18;

        // bool 8 個(1 個 1 バイトの 0/1)が全部 true のときの ulong。
        private const ulong EightTrue = 0x0101010101010101UL;

        /// <summary>bool 配列を RLE 圧縮 + Base64 文字列にエンコード。</summary>
        public static string Encode(bool[] mask, int w, int h)
        {
            if (mask == null || mask.Length == 0) return "";
            return EncodeCore(mask, w, h, out _);
        }

        /// <summary>
        /// <see cref="Encode"/> と同じ文字列を返す。ただし true の画素が 1 つも無いマスクは null
        /// (保存形式に書かないマスク。<see cref="MaskStateCodec"/>)。全 false かを別の走査で先に調べずに済む。
        /// </summary>
        public static string EncodeIfAny(bool[] mask, int w, int h)
        {
            if (mask == null || mask.Length == 0) return null;
            string encoded = EncodeCore(mask, w, h, out bool any);
            return any ? encoded : null;
        }

        // マスクは編集のたび(ブラシの押し始めと離した時・AI 提案の反映・ゾーン操作・自動保存)に全部を
        // 保存形式へ書き戻すので、ここの時間がそのまま操作の遅れになる。1 画素ずつ数える書き方は
        // Unity の Mono で 4K 1 枚 17〜45ms かかっていた(8K は約 4 倍)。
        // 同じ値が 8 画素続く所は ulong 1 回の比較で飛ばし、大きいマスクは区間に分けて並列にラン列を作ってから
        // 継ぎ目をつなぐ。出力は 1 画素ずつ数えたときと同じバイト列。
        private static unsafe string EncodeCore(bool[] mask, int w, int h, out bool any)
        {
            int len = mask.Length;
            int parts = len >= EncodeParallelMinPixels ? PixelProcessor.GetMaxParallelism() : 1;
            int per = (len + parts - 1) / parts;
            parts = (len + per - 1) / per;   // 空の区間を作らない
            var runs = new List<uint>[parts];
            var firsts = new bool[parts];
            fixed (bool* p0 = mask)
            {
                if (parts == 1)
                {
                    runs[0] = ScanRuns((byte*)p0, 0, len, out firsts[0]);
                }
                else
                {
                    var ip = (System.IntPtr)p0;   // ラムダはポインタを捕まえられない
                    Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parts }, k =>
                    {
                        int s = k * per;
                        runs[k] = ScanRuns((byte*)ip, s, System.Math.Min(len, s + per), out firsts[k]);
                    });
                }
            }

            // 継ぎ目: 前の区間の最後のランと同じ値で始まる区間は、最初のランを前のランへ足す。
            int total = 0;
            bool lastVal = false;
            for (int k = 0; k < parts; k++)
            {
                int c = runs[k].Count;
                total += (k > 0 && firsts[k] == lastVal) ? c - 1 : c;
                lastVal = LastRunValue(firsts[k], c);
            }
            any = firsts[0] || total > 1;   // 全 false ⇔ false から始まるランが 1 本だけ

            byte[] bytes = new byte[9 + total * 4];
            fixed (byte* b = bytes)
            {
                // 寸法・ラン長は BitConverter と同じマシンのバイト順で書く(Decode は BitConverter で読む)。
                *(int*)b = w;
                *(int*)(b + 4) = h;
                b[8] = firsts[0] ? (byte)1 : (byte)0;
                uint* dst = (uint*)(b + 9);
                int n = 0;
                lastVal = false;
                for (int k = 0; k < parts; k++)
                {
                    var r = runs[k];
                    int j = 0;
                    if (k > 0 && firsts[k] == lastVal) { dst[n - 1] += r[0]; j = 1; }
                    for (; j < r.Count; j++) dst[n++] = r[j];
                    lastVal = LastRunValue(firsts[k], r.Count);
                }
            }
            return RlePrefix + System.Convert.ToBase64String(bytes);
        }

        // first から始まって交互に変わる count 本のランの、最後のランの値。
        private static bool LastRunValue(bool first, int count) => first ^ ((count & 1) == 0);

        // 画素 [s, e) のラン長を並べる(first = 先頭の値)。0 以外のバイトを true として読む。
        private static unsafe List<uint> ScanRuns(byte* p, int s, int e, out bool first)
        {
            var runs = new List<uint>();
            bool cur = p[s] != 0;
            first = cur;
            uint count = 0;
            int i = s;
            while (i < e)
            {
                ulong same = cur ? EightTrue : 0UL;
                while (i + 8 <= e && *(ulong*)(p + i) == same) { i += 8; count += 8; }
                // 値の変わり目を含む 8 画素(と末尾の端数)は 1 画素ずつ見る。
                int lim = System.Math.Min(e, i + 8);
                for (; i < lim; i++)
                {
                    bool v = p[i] != 0;
                    if (v != cur) { runs.Add(count); cur = v; count = 0; }
                    count++;
                }
            }
            runs.Add(count);
            return runs;
        }

        /// <summary>
        /// RLE / 旧 bitpack 共通のヘッダ(4byte W + 4byte H)を読み、寸法を検証して画素数を返す。
        /// w / h は検証の前に代入する(失敗時も読めた値が残る)。呼び出し側で bytes.Length >= 9 を確認済みであること。
        /// </summary>
        private static bool TryReadHeader(byte[] bytes, out int w, out int h, out int len)
        {
            len = 0;
            w = System.BitConverter.ToInt32(bytes, 0);
            h = System.BitConverter.ToInt32(bytes, 4);
            if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) return false;
            // w * h の int オーバーフローを弾く。w=h=65536 だと len=0 になり(旧 bitpack では長さ検査も素通りし)、
            // 破損データに対して「成功・空マスク」を黙って返していた（レビュー §4 中）。
            // (MaxSide 上限により実際には到達しないが、上限を緩めたときの安全網として残す)
            long lenLong = (long)w * h;
            if (lenLong > int.MaxValue) return false;
            len = (int)lenLong;
            return true;
        }

        /// <summary>
        /// <see cref="Decode"/> と同じ文字列を、1 画素 1 ビットに詰めた形(<see cref="MaskSnapshot"/> と同じ並び:
        /// 画素 i は語 i &gt;&gt; 6 の i &amp; 63 ビット目。画素数より後ろのビットは 0)へ直接展開する。
        /// <c>MaskSnapshot.Pack(Decode(...))</c> と同じ配列になる。bool[] を経由しないので、作る配列は 1/8 の大きさで済む
        /// (8K で 64MB → 8MB。非破壊ビルド・シーンのプレビューはマスクごとにこれを作る)。失敗の条件も Decode と同じで、
        /// そのときは null を返す。
        /// </summary>
        public static unsafe ulong[] DecodePacked(string encoded, out int w, out int h)
        {
            if (!TryParse(encoded, out byte[] bytes, out bool rle, out w, out h, out int len)) return null;
            var bits = MaskSnapshot.NewBits(len);
            fixed (ulong* p = bits) FillBits(bytes, rle, len, p);
            return bits;
        }

        /// <summary>
        /// <see cref="DecodePacked"/> と同じ展開を、ヒープの外のマスク(<see cref="NativeBitMask"/>。参照 1)へ行う。
        /// 失敗したときは null。
        /// </summary>
        public static unsafe NativeBitMask DecodeBits(string encoded)
        {
            if (!TryParse(encoded, out byte[] bytes, out bool rle, out int w, out int h, out int len)) return null;
            var mask = NativeBitMask.Allocate(w, h);
            FillBits(bytes, rle, len, mask.Bits);
            return mask;
        }

        /// <summary>
        /// ヒープの外のマスクを、<see cref="Encode"/> が同じ画素の bool[] から作るのと同じ文字列にする(bool[] を経由しない)。
        /// 同じ値が続く語(64 画素)は 1 回の比較で飛ばす。
        /// </summary>
        public static unsafe string EncodeBits(NativeBitMask mask)
        {
            if (mask == null) return "";
            return EncodeBitsCore(mask.Bits, mask.Length, mask.Width, mask.Height, out _);
        }

        /// <summary><see cref="EncodeBits"/> と同じ文字列。ただし立っている画素が無ければ null(<see cref="EncodeIfAny"/> と同じ)。</summary>
        public static unsafe string EncodeBitsIfAny(NativeBitMask mask)
        {
            if (mask == null) return null;
            string encoded = EncodeBitsCore(mask.Bits, mask.Length, mask.Width, mask.Height, out bool any);
            return any ? encoded : null;
        }

        private static unsafe string EncodeBitsCore(ulong* bits, int len, int w, int h, out bool any)
        {
            var runs = new List<uint>();
            bool first = (bits[0] & 1UL) != 0UL;
            bool cur = first;
            for (int pos = 0; pos < len; cur = !cur)
            {
                int next = NextChange(bits, len, pos, cur);
                runs.Add((uint)(next - pos));
                pos = next;
            }
            any = first || runs.Count > 1;   // 全 false ⇔ false から始まるランが 1 本だけ
            byte[] bytes = new byte[9 + runs.Count * 4];
            fixed (byte* b = bytes)
            {
                // EncodeCore と同じ並び(寸法・ラン長はマシンのバイト順)。
                *(int*)b = w;
                *(int*)(b + 4) = h;
                b[8] = first ? (byte)1 : (byte)0;
                uint* dst = (uint*)(b + 9);
                for (int k = 0; k < runs.Count; k++) dst[k] = runs[k];
            }
            return RlePrefix + System.Convert.ToBase64String(bytes);
        }

        // pos から先で、値が cur でない最初の画素(無ければ len)。
        private static unsafe int NextChange(ulong* bits, int len, int pos, bool cur)
        {
            ulong flip = cur ? ~0UL : 0UL;
            int wi = pos >> 6, words = (len + 63) >> 6;
            ulong v = (bits[wi] ^ flip) & (~0UL << (pos & 63));
            while (v == 0UL)
            {
                if (++wi >= words) return len;
                v = bits[wi] ^ flip;
            }
            return System.Math.Min(len, (wi << 6) + TrailingZeros(v));
        }

        // 0 でない v の、下から数えて 0 が続くビットの数(Unity の Mono には BitOperations が無い)。
        private static int TrailingZeros(ulong v)
        {
            int n = 0;
            if ((v & 0xFFFFFFFFUL) == 0UL) { n += 32; v >>= 32; }
            if ((v & 0xFFFFUL) == 0UL) { n += 16; v >>= 16; }
            if ((v & 0xFFUL) == 0UL) { n += 8; v >>= 8; }
            if ((v & 0xFUL) == 0UL) { n += 4; v >>= 4; }
            if ((v & 0x3UL) == 0UL) { n += 2; v >>= 2; }
            if ((v & 0x1UL) == 0UL) n += 1;
            return n;
        }

        /// <summary>
        /// 保存形式の文字列(RLE / 旧 bitpack)を読み、寸法を確かめる。RLE はラン列が全画素を覆うことまで、旧 bitpack は
        /// 長さが足りることまで、確保の前に確かめる(<see cref="Decode"/> と同じ条件)。
        /// </summary>
        private static bool TryParse(string encoded, out byte[] bytes, out bool rle, out int w, out int h, out int len)
        {
            bytes = null;
            w = h = len = 0;
            rle = false;
            if (string.IsNullOrEmpty(encoded)) return false;
            rle = encoded.StartsWith(RlePrefix, System.StringComparison.Ordinal);
            try
            {
                bytes = System.Convert.FromBase64String(rle ? encoded.Substring(RlePrefix.Length) : encoded);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(rle ? $"[Iroca] Mask decode failed: {ex.Message}"
                                     : $"[Iroca] Mask decode (legacy bitpack) failed: {ex.Message}");
                return false;
            }
            if (bytes.Length < 9) return false;
            if (!TryReadHeader(bytes, out w, out h, out len)) return false;
            if (!rle) return bytes.Length >= 8 + (len + 7) / 8;
            long covered = 0;
            for (int bi = 9; bi + 4 <= bytes.Length && covered < len; bi += 4)
                covered += System.BitConverter.ToUInt32(bytes, bi);
            return covered >= len;
        }

        // TryParse が通した中身を、0 で始まる len 画素ぶんの詰めた形へ書く。
        // 旧 bitpack は画素 i がバイト 8 + i/8 の i%8 ビット目なので、8 バイトずつ下位から並べればそのまま語になる。
        // 画素数より後ろのビット(最後のバイトの余り)は落とす。
        private static unsafe void FillBits(byte[] bytes, bool rle, int len, ulong* bits)
        {
            if (!rle)
            {
                int dataBytes = (len + 7) / 8;
                for (int k = 0; k < dataBytes; k++)
                    bits[k >> 3] |= (ulong)bytes[8 + k] << ((k & 7) << 3);
                int rest = len & 63;
                if (rest != 0) bits[((len + 63) >> 6) - 1] &= (1UL << rest) - 1UL;
                return;
            }
            bool curVal = bytes[8] != 0;
            int pos = 0;
            int byteIdx = 9;
            while (pos < len && byteIdx + 4 <= bytes.Length)
            {
                uint run = System.BitConverter.ToUInt32(bytes, byteIdx);
                byteIdx += 4;
                int end = (int)System.Math.Min((long)pos + run, len);
                if (curVal && end > pos) SetBits(bits, pos, end);
                pos = end;
                curVal = !curVal;
            }
        }

        // 詰めた形のビット [s, e)(s < e)を立てる。
        private static unsafe void SetBits(ulong* bits, int s, int e)
        {
            int w0 = s >> 6, w1 = (e - 1) >> 6;
            ulong head = ~0UL << (s & 63);
            ulong tail = ~0UL >> (63 - ((e - 1) & 63));
            if (w0 == w1)
            {
                bits[w0] |= head & tail;
                return;
            }
            bits[w0] |= head;
            for (int k = w0 + 1; k < w1; k++) bits[k] = ~0UL;
            bits[w1] |= tail;
        }

        /// <summary>Encode の逆。デコード失敗時は null を返す。</summary>
        public static bool[] Decode(string encoded, out int w, out int h)
        {
            w = 0; h = 0;
            if (string.IsNullOrEmpty(encoded)) return null;

            if (encoded.StartsWith(RlePrefix, System.StringComparison.Ordinal))
            {
                try
                {
                    byte[] bytes = System.Convert.FromBase64String(encoded.Substring(RlePrefix.Length));
                    if (bytes.Length < 9) return null;
                    if (!TryReadHeader(bytes, out w, out h, out int len)) return null;
                    // 確保前にラン列を走査し、全画素分を埋め切れる(= 切断データでない)ことを確かめる。
                    // 従来は確保してから埋め、足りなければ null にしていたため、切断データでも
                    // 上限いっぱいの配列を一度確保していた。検査は確保より桁違いに安い。
                    long covered = 0;
                    for (int bi = 9; bi + 4 <= bytes.Length && covered < len; bi += 4)
                        covered += System.BitConverter.ToUInt32(bytes, bi);
                    if (covered < len) return null;
                    bool curVal = bytes[8] != 0;
                    bool[] mask = new bool[len];
                    int pos = 0;
                    int byteIdx = 9;
                    while (pos < len && byteIdx + 4 <= bytes.Length)
                    {
                        uint run = System.BitConverter.ToUInt32(bytes, byteIdx);
                        byteIdx += 4;
                        int end = (int)System.Math.Min((long)pos + run, len);
                        // 新しい配列は false で始まるので false のランは書かない。true のランはまとめて埋める
                        // (1 画素ずつ書くと Undo・テクスチャの読み込み・NDMF のたびに 4K 1 枚 8〜19ms かかっていた)。
                        if (curVal && end > pos) new System.Span<bool>(mask, pos, end - pos).Fill(true);
                        pos = end;
                        curVal = !curVal;
                    }
                    // Encode は必ず全画素分のランを書く。ここで埋め切れていない = 切断データ。
                    // 「成功・途中まで正しいマスク」として返すと、欠けた部分が「除外なし」に化けて
                    // ユーザーが守ったつもりの画素が変換される。
                    if (pos < len) return null;
                    return mask;
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Iroca] Mask decode failed: {ex.Message}");
                    return null;
                }
            }

            // 旧 bitpack フォーマット（後方互換）
            try
            {
                byte[] packed = System.Convert.FromBase64String(encoded);
                if (packed.Length < 9) return null;
                if (!TryReadHeader(packed, out w, out h, out int len)) return null;
                if (packed.Length < 8 + (len + 7) / 8) return null;
                bool[] mask = new bool[len];
                for (int i = 0; i < len; i++)
                    mask[i] = (packed[8 + i / 8] & (1 << (i % 8))) != 0;
                return mask;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[Iroca] Mask decode (legacy bitpack) failed: {ex.Message}");
                return null;
            }
        }
    }
}
