// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// 右クリックの「ここも塗る」(粒度 自動)で、AI の範囲を「含める」へ足す前に、範囲を押した所の色に近い画素へ絞るかを決めて絞る。
    /// 窓(MaskSuggestController)・自動化(AutomationMaskEdits)・ハーネスの --script が、範囲をマスクの寸法へ写す前にここを通る
    /// (宛先のゾーンの選択の強さの読み方だけが違う。窓はプレビューの選択の控えに今のマスクを重ねて読み(<see cref="WithMaskContract"/>)、
    /// ほかは今のマスクでゾーンの選択を回す(<see cref="NarrowBySelection"/>)。どちらもマスクの契約を掛けた後の強さで数える)。
    /// <para>
    /// 絞るのは次のどちらか。どちらでもなければ範囲をそのまま足す(色の照合が取りこぼしたその部品を指している: 星柄の布の明るい雲・
    /// 宝石の明暗・瞳。色の距離では「別の物」と「同じ物の明暗」を分けられない。dev_safe の 2026-11-22 の診断)。
    /// </para>
    /// <list type="number">
    /// <item>押した所(5×5)がもう宛先のゾーンで塗れている。範囲に入る色の違う所は、ゾーンが色で分けて塗らなかった所なので足さない
    /// (2026-11-22 ユーザー判断: 黒い文字の上で押しても下地は塗らない)。</item>
    /// <item>押した所は塗れていないが、足すことになる画素(範囲のうち、まだそのゾーンで塗れていない画素)のうち、押した所の色に近い画素の
    /// 割合が <see cref="ZoneAutoTuner.NormOutlierFrac"/> 未満(押した色が少数派)。大きな面に乗った小さな柄を押すと、AI の範囲は
    /// 面ごと取ることがあり、面まで塗られていた(2026-11-23 ユーザー判断: 押した色に近い所だけを足す)。割合を範囲の全部でなく、
    /// まだ塗れていない画素で測るのが要点: 大半がもう塗れている布の中の明るい雲を押した例は、範囲の全部で測ると少数派に入り、
    /// 雲の芯が塗りむらになる。</item>
    /// </list>
    /// <para>
    /// 利用者が大きさを選んだ「小さめ・大きめの部分で」は絞らない(色の違う所まで足したいときの道。影で暗い歯と白い歯のように、
    /// 同じ部品の明暗が色で分かれる細部を足す使い方も残る)。近さは押した所(5×5 の中央値)からの OkLab の楕円
    /// (明るさの差は広く <see cref="PaintHereNearLightness"/>・色味の差は狭く <see cref="PaintHereNearColor"/>)。「塗れている」は
    /// 選択の強さが半分以上(<see cref="MaskRegionEdit.PixelPainted"/>・<see cref="MaskRegionEdit.ZonePaintsAt"/>)。
    /// </para>
    /// <para>
    /// メモリ: 絞った範囲は範囲と同じ寸法の <see cref="NativeBools"/>(ヒープの外)。強さは区切って読む(管理配列は
    /// <see cref="ReadChunk"/> 個まで)。ゾーンの選択を回すときの強さは w*h の float をヒープの外に借りて、返す前に放す。
    /// </para>
    /// </summary>
    internal static unsafe class PaintHereNarrowing {
        /// <summary>
        /// 範囲のうち右クリックした所の色と同じとみなす色味の幅(OkLab の a・b 平面の距離。楕円の短い軸)。色味の違う別の部品
        /// (地と文字・瞳の筋・紋章)を外す幅。AI 提案の離れた塊を外す段の幅(SamMaskRefine.ForeignNearColor)とは目的が違う。
        /// 値の根拠は docs/algorithm-constants.md。
        /// </summary>
        internal const float PaintHereNearColor = 0.16f;

        /// <summary>
        /// 同じく、右クリックした所と同じとみなす明るさの幅(OkLab の L の差。楕円の長い軸)。アニメ調の塗りの陰影は主に明るさで付く
        /// (セル影・乗算の影・金属の明暗)ので、色味の幅より広く取る。有限にして、色味が同じでも明るさの大きく違う所(白地の黒い文字)は
        /// 外す。判定は (dL / 明るさの幅)² + (da² + db²) / 色味の幅² ≤ 1。値の根拠は docs/algorithm-constants.md。
        /// </summary>
        internal const float PaintHereNearLightness = 0.36f;

        /// <summary>範囲の画素の強さを 1 回に読む数(読む並びを小さく保つ。出力は変えない)。</summary>
        internal const int ReadChunk = 1 << 16;

        /// <summary>
        /// 宛先のゾーンの選択の強さを読む。indices の先頭 count 個の画素(範囲と同じ寸法・下原点の添字)の強さを strength へ書く。
        /// indices と strength の長さは同じで、count より後ろは呼ぶ側が count - 1 番目の画素で埋めてある(並び全体を読む実装でよい)。
        /// 読めなければ false。
        /// </summary>
        internal delegate bool StrengthReader(int[] indices, float[] strength, int count);

        /// <summary>絞った理由。</summary>
        internal enum Reason {
            /// <summary>絞らない(範囲をそのまま足す)。</summary>
            None,
            /// <summary>押した所がもう塗れていた。</summary>
            ClickPainted,
            /// <summary>押した色が、足すことになる画素の少数派だった。</summary>
            Minority,
        }

        /// <summary><see cref="Narrow"/> の決めたこと(窓の知らせと、ハーネスの SCRIPT の行の診断)。</summary>
        internal struct Outcome {
            /// <summary>絞った理由(<see cref="Reason.None"/> = 絞らない)。</summary>
            public Reason reason;
            /// <summary>押した所(5×5)がもう宛先のゾーンで塗れていたか。</summary>
            public bool clickPainted;
            /// <summary>範囲の画素の強さを読めたか(読めなければ割合では決めない)。</summary>
            public bool measured;
            /// <summary>範囲のうち、まだ宛先のゾーンで塗れていない画素の数。</summary>
            public int unpainted;
            /// <summary>そのうち、押した所の色に近い画素の数。</summary>
            public int unpaintedNear;
            /// <summary>絞って範囲から外した画素の数(絞らなければ 0)。</summary>
            public int dropped;

            /// <summary>絞ったか。</summary>
            public bool Narrowed => reason != Reason.None;

            /// <summary>まだ塗れていない画素のうち、押した所の色に近い画素の割合(塗れていない画素が無ければ 1)。</summary>
            public float NearFraction => unpainted > 0 ? unpaintedNear / (float)unpainted : 1f;

            /// <summary>
            /// 絞った結果、色の近い所はもう全部塗れていて、足す所が無い(窓は「色の違う所は足していない」と知らせる)。
            /// </summary>
            public bool NothingLeftToAdd => Narrowed && dropped > 0 && measured && unpaintedNear == 0;
        }

        /// <summary>絞り込みを考える操作か(「ここも塗る」で、粒度が自動)。</summary>
        public static bool Applies(MaskRegionOp op, bool sizeAuto) => op == MaskRegionOp.PaintHere && sizeAuto;

        /// <summary>
        /// 範囲 region(AI の提案。px と同じ寸法・下原点)を、必要なら押した所 (u, v)(UV・下原点)の色に近い画素へ絞って返す。
        /// 絞ったら新しく作った範囲を返し(owned = true。呼び出し側が Release)、絞らない・絞っても同じなら region をそのまま返す
        /// (owned = false)。region は書き換えない。sizeAuto = 粒度が自動か。read = 宛先のゾーンの選択の強さ(null・読めないなら絞らない)。
        /// </summary>
        public static NativeBools Narrow(NativeBools region, NativePixels px, float u, float v, MaskRegionOp op, bool sizeAuto,
                                         StrengthReader read, out bool owned, out Outcome outcome,
                                         CancellationToken ct = default) {
            owned = false;
            outcome = default;
            if (region == null || !Applies(op, sizeAuto) || px == null || read == null
                || px.Width != region.Width || px.Height != region.Height) {
                return region;
            }
            int w = region.Width, h = region.Height;
            var probe = MaskRegionEdit.PaintedProbeIndices(u, v, w, h);
            var probeStrength = new float[probe.Length];
            if (!read(probe, probeStrength, probe.Length)) {
                return region;
            }
            outcome.clickPainted = MaskRegionEdit.ZonePaintsAt(probeStrength, probe.Length);
            // 押した画素(AI 提案のクリックと同じ丸め。SamProposalSteps.Stage1)
            int cx = System.Math.Min(System.Math.Max((int)(u * w), 0), w - 1);
            int cy = System.Math.Min(System.Math.Max((int)(v * h), 0), h - 1);
            var near = region.Clone();
            try {
                int dropped = KeepNearClickColor(near.Ptr, px.Ptr, w, h, cx, cy, ct);
                outcome.measured = CountUnpainted(region, near, read, out int unpainted, out int unpaintedNear);
                outcome.unpainted = unpainted;
                outcome.unpaintedNear = unpaintedNear;
                if (outcome.clickPainted) {
                    outcome.reason = Reason.ClickPainted;
                } else if (outcome.measured && unpainted > 0 && outcome.NearFraction < ZoneAutoTuner.NormOutlierFrac) {
                    outcome.reason = Reason.Minority;
                }
                if (outcome.reason == Reason.None || dropped == 0) {
                    return region;
                }
                outcome.dropped = dropped;
                owned = true;
                var narrowed = near;
                near = null;
                return narrowed;
            } finally {
                near?.Release();
            }
        }

        /// <summary>
        /// <see cref="Narrow"/> の、宛先のゾーンの選択の強さを、今のマスク(masks)でゾーンの選択を製品の道
        /// (<see cref="PixelProcessor.SimulateSelection"/>)に通して求める版(自動化とハーネス。窓はプレビューの選択の控えから読む)。
        /// 強さは w*h の float をヒープの外に借り、返す前に放す。ゾーンが無い・マスクの寸法が px と違うときは絞らない。
        /// 作業配列をプールから借りるので、製品では計算の間(NativeMemoryLifecycle.WorkStarted 〜 WorkEnded)に呼ぶ。
        /// </summary>
        public static NativeBools NarrowBySelection(NativeBools region, NativePixels px, float u, float v, MaskRegionOp op,
                                                    bool sizeAuto, ColorZone zone, in RecolorSettings settings,
                                                    MaskSnapshot masks, out bool owned, out Outcome outcome,
                                                    CancellationToken ct = default) {
            owned = false;
            outcome = default;
            if (region == null || !Applies(op, sizeAuto) || zone == null || px == null
                || (masks != null && (masks.width != px.Width || masks.height != px.Height))) {
                return region;
            }
            long n = (long)px.Width * px.Height;
            var buf = new NativeBuffer(n * sizeof(float), NativeUsage.Selection);
            try {
                PixelProcessor.SimulateSelection(px.Ptr, px.Width, px.Height, zone.Clone(), in settings, masks, null,
                    (float*)buf.Ptr, ct);
                return Narrow(region, px, u, v, op, sizeAuto, ReaderOver(buf, n), out owned, out outcome, ct);
            } finally {
                buf.Release();
            }
        }

        /// <summary>
        /// 選択の控え(<see cref="SelectionCache.TryReadLatest"/>。保存した選択の強さに除外を掛けて返すが、含めるは足さない)を読む
        /// read に、masks の宛先ゾーン zoneId のマスクの契約を重ね、<see cref="NarrowBySelection"/>(<see cref="PixelProcessor.SimulateSelection"/>
        /// の最後の強さ)と同じ「塗れている／いない」にする(窓の読み方。自動化・ハーネスと数え方をそろえる)。規則は製品の
        /// マスクの契約と同じ: 除外(共通 ∪ ゾーン)の画素は 0、含める画素(除外でない)は 1。それ以外は控えの値のまま。
        /// 画素の添字は選択の寸法 w×h(下原点)で、マスクの画素との対応は PixelProcessor.BuildMaskIndexMap と同じ式
        /// (マスクの解像度が違ってもよい)。masks が null・ゾーンのマスクが 1 つも無いなら read をそのまま返す。
        /// masks は返した reader を使い終わるまで呼び出し側が持つ(参照を足さない)。
        /// </summary>
        public static StrengthReader WithMaskContract(StrengthReader read, MaskSnapshot masks, string zoneId, int w, int h) {
            if (read == null || masks == null || masks.width <= 0 || masks.height <= 0 || w <= 0 || h <= 0) {
                return read;
            }
            NativeBitMask common = masks.common, exclude = null, include = null;
            if (!string.IsNullOrEmpty(zoneId)) {
                masks.zones?.TryGetValue(zoneId, out exclude);
                masks.zoneIncludes?.TryGetValue(zoneId, out include);
            }
            if (common == null && exclude == null && include == null) {
                return read;
            }
            int mw = masks.width, mh = masks.height;
            return (indices, strength, count) => {
                if (!read(indices, strength, count)) {
                    return false;
                }
                ulong* c = common != null ? common.Bits : null;
                ulong* e = exclude != null ? exclude.Bits : null;
                ulong* n = include != null ? include.Bits : null;
                for (int i = 0; i < count; i++) {
                    int k = indices[i];
                    int x = k % w, y = k / w;
                    int mx = (int)System.Math.Min(System.Math.Max((long)x * mw / w, 0L), mw - 1);
                    int my = (int)System.Math.Min(System.Math.Max((long)y * mh / h, 0L), mh - 1);
                    int m = my * mw + mx;
                    if (MaskSnapshot.GetBit(c, m) || MaskSnapshot.GetBit(e, m)) {
                        strength[i] = 0f;
                    } else if (MaskSnapshot.GetBit(n, m)) {
                        strength[i] = 1f;
                    }
                }
                return true;
            };
        }

        // w*h の強さ(ヒープの外の float の並び)を読む。添字が外れたら投げる。
        private static StrengthReader ReaderOver(NativeBuffer strength, long n) => (indices, dst, count) => {
            float* s = (float*)strength.Ptr;
            for (int i = 0; i < count; i++) {
                int k = indices[i];
                if (k < 0 || k >= n) {
                    throw new System.ArgumentOutOfRangeException(nameof(indices));
                }
                dst[i] = s[k];
            }
            return true;
        };

        // 範囲の画素を区切って強さを読み、まだ塗れていない画素と、そのうち near(押した色に近い所だけに絞った範囲)に残る画素を数える。
        // 読めなかったら false(数は途中まで)。
        private static bool CountUnpainted(NativeBools region, NativeBools near, StrengthReader read,
                                           out int unpainted, out int unpaintedNear) {
            unpainted = 0;
            unpaintedNear = 0;
            var indices = new int[ReadChunk];
            var strength = new float[ReadChunk];
            bool* r = region.Ptr;
            bool* nr = near.Ptr;
            int n = region.Length, k = 0;
            for (int i = 0; i < n; i++) {
                if (!r[i]) {
                    continue;
                }
                indices[k++] = i;
                if (k == ReadChunk) {
                    if (!Tally(read, indices, strength, k, nr, ref unpainted, ref unpaintedNear)) {
                        return false;
                    }
                    k = 0;
                }
            }
            return k == 0 || Tally(read, indices, strength, k, nr, ref unpainted, ref unpaintedNear);
        }

        private static bool Tally(StrengthReader read, int[] indices, float[] strength, int count, bool* near,
                                  ref int unpainted, ref int unpaintedNear) {
            for (int i = count; i < indices.Length; i++) {
                indices[i] = indices[count - 1];
            }
            if (!read(indices, strength, count)) {
                return false;
            }
            for (int i = 0; i < count; i++) {
                if (MaskRegionEdit.PixelPainted(strength[i])) {
                    continue;
                }
                unpainted++;
                if (near[indices[i]]) {
                    unpaintedNear++;
                }
            }
            return true;
        }

        /// <summary>
        /// mask(下原点 w*h)のうち、クリック (clickX, clickY)(下原点)の色(周り 5×5 の中央値。<see cref="OkLab.MedianAround"/>)から
        /// 離れた画素を外す(in-place)。外した画素数を返す。近さは OkLab の楕円 (dL / <see cref="PaintHereNearLightness"/>)² +
        /// (da² + db²) / <see cref="PaintHereNearColor"/>² ≤ 1(明るさの差は広く、色味の差は狭く見る)。画素ごとに見る(つながりは見ない)。
        /// </summary>
        public static int KeepNearClickColor(bool* mask, Color32* pixelsBottomUp, int w, int h, int clickX, int clickY,
                                             CancellationToken ct = default) {
            if (mask == null || pixelsBottomUp == null || (uint)clickX >= (uint)w || (uint)clickY >= (uint)h) {
                return 0;
            }
            OkLab.MedianAround(pixelsBottomUp, w, h, clickX, clickY, MaskRegionEdit.PaintedProbeRadius,
                out float rL, out float ra, out float rb);
            float invL2 = 1f / (PaintHereNearLightness * PaintHereNearLightness);
            float invC2 = 1f / (PaintHereNearColor * PaintHereNearColor);
            var removed = new int[h];   // 行ごとに数えて整数で足す(並列でも決定的)
            Parallel.For(0, h, CoreParallelism.Options(ct), y => {
                bool* row = mask + (long)y * w;
                Color32* px = pixelsBottomUp + (long)y * w;
                int n = 0;
                for (int x = 0; x < w; x++) {
                    if (!row[x]) {
                        continue;
                    }
                    var c = px[x];
                    OkLab.RgbToOklab(c.r, c.g, c.b, out float L, out float a, out float b);
                    float dL = L - rL, da = a - ra, db = b - rb;
                    if (dL * dL * invL2 + (da * da + db * db) * invC2 > 1f) {
                        row[x] = false;
                        n++;
                    }
                }
                removed[y] = n;
            });
            int total = 0;
            for (int i = 0; i < h; i++) {
                total += removed[i];
            }
            return total;
        }
    }
}
