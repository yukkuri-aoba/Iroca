// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Threading;

namespace Iroca
{
    /// <summary>
    /// 選択結果(マスクを掛ける直前の strength=「生の選択」と後段の bbox)をゾーン別にキャッシュする。
    /// 「ターゲット色/再着色パラメータだけ変えた」プレビュー再生成では、選択フェーズ
    /// (Match/Highlight/FloodFill/穴埋め/境界/ブラー)の結果は不変なので、再計算せず
    /// キャッシュした strength を復元し、マスクを掛け直して再着色フェーズ以降だけを走らせる(=「1回の変更ごと」の高速化)。
    ///
    /// キーは **選択に影響する入力だけ** から作る(<see cref="PixelProcessor.BuildSelectionKey"/>)。
    /// ターゲット色・valueBlend・出力彩度・シャドウ脱彩・wash・再着色アンカーは含めない(=これらの
    /// 変更ではヒットして選択を再利用する)。tolerance・サンプル色・edgeSoftness・各しきいなどが
    /// 変われば別キー=ミス=再計算。曖昧なものは安全側で「選択影響」に含める(ミスが増えるだけ)。
    /// マスクの中身はキーに入れない。含めるマスクは保存する strength に効かない(掛けるのは復元の後)。
    /// 除外マスク(共通・ゾーン)は保存時の配列を持ち、違っていれば呼び出し側の判定
    /// (<see cref="MaskReuseCheck"/>)で使えるかを決める。共通マスクを 1 筆塗るたびに全ゾーンが外れないようにするため。
    ///
    /// strength は作業配列のプールから借りた領域で後段が破壊的に書き換えるため、Store では必ずコピーを取る。
    /// 復元結果が「同一入力でフル計算した strength」と完全一致するので出力はビット不変。
    /// テクスチャ/寸法変更時は呼び出し側(PreviewView)が Clear する。
    ///
    /// strength のコピーは Mono のヒープの外(<see cref="NativeFloatBuffer"/>)に置く(1 ゾーン 4K で 64MB・8K で 256MB)。
    /// Mono のヒープは縮まないので、ヒープに置くとテクスチャを替えてもウィンドウを閉じても OS へ戻らない。
    /// 配列は外へ渡さず、中身を写す。写すのは lock の外(64〜256MB を写す間、メインスレッドの Clear・RetainOnly を
    /// 待たせない)で、写している間は写す側が強さの参照を 1 つ持つ(NativeBuffer と同じ参照の数)。辞書から外すときは
    /// 辞書の分の参照を返すので、写している途中なら、写し終えて返した時点で解放される。
    /// Clear・Dispose の後に、それより前に始まった Store が終わっても保存しない(世代で見分ける)。Clear の後に始まった
    /// Store でも、取り消した計算のもの(Clear の前の画素で作った選択)は保存しない(Store に渡す ct で見分ける)。
    /// ウィンドウを閉じるときは <see cref="Dispose"/> で全部解放し、以後の Store は保存しない。
    /// </summary>
    internal sealed unsafe class SelectionCache : IDisposable
    {
        /// <summary>
        /// 保存時の除外マスク(共通・ゾーン。null は除外なし)のままでも、今の除外マスクで計算し直したのと同じ選択に
        /// なるか。true なら保存した選択を使う。
        /// </summary>
        internal delegate bool MaskReuseCheck(ulong* storedCommon, ulong* storedZone);

        /// <summary>後段(穴埋め・境界回復・ブラー・デコンタミ・統計)が走査する範囲。余白を足した後の値で、Has=false は空。</summary>
        internal struct PostBox
        {
            public bool Has;
            public int MinX, MinY, MaxX, MaxY;
        }

        private sealed class Entry
        {
            public string Key;
            public NativeFloatBuffer Strength;   // マスクを掛ける直前の strength のコピー(len=w*h)。辞書が参照を 1 つ持つ
            public PostBox Box;
            // この選択が正しい除外マスク(MaskSnapshot の層。参照を 1 つ持つ。編集側は書く前に写すので中身は変わらない)。
            // 判定を通って使い回したら、そのときのマスクに差し替える(次の判定の差分を小さくする)。
            public NativeBitMask Common, ZoneMask;
            public int MaskW, MaskH;   // Common・ZoneMask の寸法(マスクの寸法は選択の鍵に入るので、使い回しても変わらない)
            public int W, H;
        }

        private readonly Dictionary<string, Entry> _byZone = new Dictionary<string, Entry>();
        // 計算はバックグラウンドスレッドで走り、Clear・RetainOnly・Dispose はメインスレッドから来る。
        // 辞書・世代はこの lock の中でだけ触る。写す側が参照を足すのも lock の中(辞書にある間に足す)。
        private readonly object _gate = new object();
        private int _epoch;            // Clear・Dispose のたびに進める
        private bool _disposed;
        private int _maskReuseCount;

        /// <summary>除外マスクが保存時と違っていたが、判定を通って使い回した回数(検証用)。</summary>
        internal int MaskReuseCount => Volatile.Read(ref _maskReuseCount);

        /// <summary>
        /// 除外マスクなしで保存したものを引く(<see cref="TryCopy"/> の配列版)。キャッシュにあれば true を返し、
        /// strengthDst があればそこへ strength を写す(長さ w*h 以上)。strengthDst が null なら有無だけを見る。
        /// </summary>
        public bool TryGet(string zoneId, string key, int w, int h, float[] strengthDst = null)
        {
            if (strengthDst == null) return TryCopy(zoneId, key, w, h, null, null, null, null, out _);
            if (strengthDst.Length < w * h) throw new ArgumentException("strengthDst が w*h より短い", nameof(strengthDst));
            fixed (float* p = strengthDst) return TryCopy(zoneId, key, w, h, p, null, null, null, out _);
        }

        /// <summary>
        /// キャッシュにあれば true と box を返し、strengthDst があればそこへ strength を写す(長さ w*h 以上)。
        /// strengthDst が null なら有無だけを見る。除外マスク(common・zoneMask)が保存時の配列と違えば、reusable が
        /// true を返したときだけ使う(reusable が null なら使わない)。reusable は lock の外で呼ぶ。
        /// </summary>
        public bool TryCopy(string zoneId, string key, int w, int h, float* strengthDst,
            NativeBitMask common, NativeBitMask zoneMask, MaskReuseCheck reusable, out PostBox box)
        {
            box = default;
            if (string.IsNullOrEmpty(zoneId)) return false;
            Entry e;
            NativeBitMask storedCommon, storedZone;
            lock (_gate)
            {
                if (!_byZone.TryGetValue(zoneId, out e) || e.Key != key || e.W != w || e.H != h) return false;
                // 判定は lock の外なので、その間に辞書から外されても読めるよう参照を足す。
                storedCommon = e.Common?.AddRef();
                storedZone = e.ZoneMask?.AddRef();
                e.Strength.AddRef();
            }
            bool reused = false;
            try
            {
                if (!ReferenceEquals(storedCommon, common) || !ReferenceEquals(storedZone, zoneMask))
                {
                    if (reusable == null || !reusable(NativeBitMask.BitsOf(storedCommon), NativeBitMask.BitsOf(storedZone))) return false;
                    reused = true;
                }
                if (strengthDst != null) e.Strength.CopyTo(strengthDst, w * h);
            }
            finally
            {
                if (reused)
                {
                    lock (_gate)
                    {
                        if (_byZone.TryGetValue(zoneId, out var cur) && ReferenceEquals(cur, e))
                        {
                            ReleaseMasks(e);
                            e.Common = common?.AddRef();
                            e.ZoneMask = zoneMask?.AddRef();
                        }
                    }
                }
                storedCommon?.Release();
                storedZone?.Release();
                // 中断中に最後の参照だった場合は NativeBuffer が解放せずにアンロードまで持つ。
                e.Strength.Release();
            }
            if (reused) Interlocked.Increment(ref _maskReuseCount);
            box = e.Box;   // 作った後は変えない項目
            return true;
        }

        /// <summary>除外マスクなし・bbox なしで保存する(<see cref="StoreFrom"/> の配列版)。</summary>
        public void Store(string zoneId, string key, float[] strength, int w, int h,
            CancellationToken ct = default)
        {
            if (strength.Length < w * h) throw new ArgumentException("strength が w*h より短い", nameof(strength));
            fixed (float* p = strength) StoreFrom(zoneId, key, p, default, null, null, 0, 0, w, h, ct);
        }

        /// <summary>
        /// strength(長さ w*h 以上)を写して保存する。common・zoneMask は、この選択を計算したときの除外マスク
        /// (MaskSnapshot の層。参照を足して持つ)で、寸法は maskW×maskH。ct が取り消されていたら保存しない: 取り消した計算(テクスチャの
        /// 読み直しの前の画素で作った選択)を、Clear の後に保存させないため。呼び出し側は Clear の前に必ず取り消す。
        /// </summary>
        public void StoreFrom(string zoneId, string key, float* strength,
            in PostBox box, NativeBitMask common, NativeBitMask zoneMask, int maskW, int maskH, int w, int h,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            int len = w * h;
            int epoch;
            NativeFloatBuffer buf = null;
            lock (_gate)
            {
                if (_disposed || ct.IsCancellationRequested) return;
                epoch = _epoch;
                // 同じゾーンの前のエントリを外す。誰も写していなくて(参照が辞書の分だけ)寸法が同じなら、辞書の分の
                // 参照ごと領域を使い回す。辞書から外した後は誰も参照を足さない(足すのは辞書にある間だけ)。
                if (_byZone.TryGetValue(zoneId, out var old))
                {
                    _byZone.Remove(zoneId);
                    ReleaseMasks(old);
                    if (old.Strength.RefCount == 1 && old.Strength.Length == len) buf = old.Strength;
                    else old.Strength.Release();
                }
            }
            bool published = false;
            Entry pending = null;
            try
            {
                buf ??= new NativeFloatBuffer(len);
                buf.CopyFrom(strength, len);
                var entry = new Entry
                {
                    Key = key, Strength = buf, Box = box,
                    Common = common?.AddRef(), ZoneMask = zoneMask?.AddRef(), MaskW = maskW, MaskH = maskH, W = w, H = h,
                };
                pending = entry;
                lock (_gate)
                {
                    // 取り消しは lock の外でも起きるが、呼び出し側は「取り消し → Clear」の順なので、Clear の後に
                    // ここへ来た計算は必ず取り消し済みとして見える。
                    if (_disposed || epoch != _epoch || ct.IsCancellationRequested) return;
                    // 写している間に別の Store が同じゾーンへ入れていたら、そちらを外す(後から来た方を残す)。
                    // 外すのは差し替えた後(差し替えで例外が出ても、解放済みの項目を辞書に残さない)。
                    _byZone.TryGetValue(zoneId, out var other);
                    _byZone[zoneId] = entry;
                    published = true;
                    if (other != null) Detach(other);
                }
            }
            finally
            {
                // 載せなかった領域を返す(中断中は NativeBuffer が解放せずにアンロードまで持つ。並列で写している途中の
                // 領域を、ほかのスレッドがまだ書いていることがある)。
                if (!published)
                {
                    buf?.Release();
                    if (pending != null) ReleaseMasks(pending);
                }
            }
        }

        /// <summary>
        /// ゾーンの最後に保存した選択の強さのうち、画素 indices の値を dst へ写す。キーは問わない(表示中の
        /// プレビューを作った選択か、作り直しの途中ならその新しいほう)。寸法が w×h でなければ false。
        /// 右クリックの宛先を、クリックした場所を塗っているゾーンに合わせるのに使う(PreviewView.ContextMenu)。
        /// lock の中で読むので、Store が同じ領域へ書いている最中の値は読まない(Store は辞書から外してから書く)。
        /// 辞書にあるエントリは lock の中では解放されない(解放は Detach で、これも lock の中)。
        /// 保存しているのは除外マスクを掛ける前の選択なので、表示中の選択に合わせて除外の画素は 0 にして返す
        /// (含めるマスクは掛けない。表示では除外の後に掛ける)。
        /// </summary>
        public bool TryReadLatest(string zoneId, int w, int h, int[] indices, float[] dst)
        {
            if (string.IsNullOrEmpty(zoneId) || indices == null || dst == null) return false;
            lock (_gate)
            {
                if (_disposed || !_byZone.TryGetValue(zoneId, out var e) || e.W != w || e.H != h || e.Strength == null)
                    return false;
                e.Strength.ReadAt(indices, dst);
                if ((e.Common != null || e.ZoneMask != null) && e.MaskW > 0 && e.MaskH > 0)
                {
                    // 座標の対応は PixelProcessor.BuildMaskIndexMap と同じ
                    for (int k = 0; k < indices.Length; k++)
                    {
                        int x = indices[k] % w, y = indices[k] / w;
                        int mi = Math.Min(Math.Max(y * e.MaskH / h, 0), e.MaskH - 1) * e.MaskW
                               + Math.Min(Math.Max(x * e.MaskW / w, 0), e.MaskW - 1);
                        if (MaskSnapshot.GetBit(NativeBitMask.BitsOf(e.Common), mi) || MaskSnapshot.GetBit(NativeBitMask.BitsOf(e.ZoneMask), mi)) dst[k] = 0f;
                    }
                }
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _epoch++;
                foreach (var e in _byZone.Values) Detach(e);
                _byZone.Clear();
            }
        }

        /// <summary>liveZoneIds に含まれない zoneId のエントリを一括破棄する。
        /// ゾーン削除後もエントリ(4K で 64MB)が恒久残留するのを防ぐ。
        /// プレビュー生成のたびにセッションの全ゾーン id を渡して呼ぶ。</summary>
        public void RetainOnly(ICollection<string> liveZoneIds)
        {
            if (liveZoneIds == null) return;
            lock (_gate)
            {
                if (_byZone.Count == 0) return;
                List<string> stale = null;
                foreach (var id in _byZone.Keys)
                    if (!liveZoneIds.Contains(id)) (stale ??= new List<string>()).Add(id);
                if (stale == null) return;
                foreach (var id in stale)
                {
                    Detach(_byZone[id]);
                    _byZone.Remove(id);
                }
            }
        }

        /// <summary>全部解放し、以後は保存しない(ウィンドウを閉じるとき・リロードの前)。写している最中の分は、写し終えた時点で解放する。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _epoch++;
                foreach (var e in _byZone.Values) Detach(e);
                _byZone.Clear();
            }
        }

        // 辞書から外したエントリの、辞書の分の参照を返す(_gate の中で呼ぶ)。写している途中の読み手がいれば、
        // その読み手が返した時点で解放される。
        private static void Detach(Entry e)
        {
            ReleaseMasks(e);
            e.Strength.Release();
        }

        private static void ReleaseMasks(Entry e)
        {
            e.Common?.Release();
            e.ZoneMask?.Release();
            e.Common = e.ZoneMask = null;
        }
    }
}
