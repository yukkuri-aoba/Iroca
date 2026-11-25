// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// デコーダ出力(low_res_logits [4,256,256] + iou_predictions [4])から提案マスクを作る純計算。
    /// SamPredictor の後処理と同値:
    ///   bilinear 256→1024(align_corners=False) → リサイズ実寸 (newH,newW) で切り出し →
    ///   bilinear で元寸 → 閾値 0 で二値化。最後に下原点(GetPixels32 順)へ反転して返す。
    /// チャンネル選択は <see cref="SelectChannels"/> が正(妥当性判定 → 粒度規則の 2 段)。
    /// </summary>
    internal static unsafe partial class SamMaskPostprocess {
        /// <summary>デコーダ低解像度マスクの一辺。</summary>
        public const int LowRes = 256;

        /// <summary>スパイクで確定した背景洪水マスクの棄却しきい(キャンバス面積比)。</summary>
        public const float DefaultFloodRejectFrac = 0.40f;

        /// <summary>
        /// stability score のロジットオフセット(SAM 標準値)。mask_threshold=0 に対し ±1 の
        /// 二値化を比べる。
        /// </summary>
        public const float StabilityOffset = 1.0f;

        /// <summary>
        /// 候補を「安定」とみなす stability score の下限。
        /// GT 15 被写体 × クリック 3 位置(45 件)の実測で 0.70〜0.90 の間は結果が動かない
        /// 平坦域なので、その中央を採る(テクスチャ個別の調整値ではない)。
        /// </summary>
        public const float StabilityThreshold = 0.80f;

        internal sealed class Result {
            /// <summary>
            /// 下原点 texW*texH の提案マスク(Mono のヒープの外。この結果が参照を 1 つ持ち、受け取った側が引き取る。
            /// 0.4.0 の計画の B4)。
            /// </summary>
            public NativeBools mask;
            /// <summary>採用チャンネル(1..3)。</summary>
            public int channel;
            /// <summary>採用チャンネルの予測 IoU スコア。</summary>
            public float score;
            /// <summary>キャンバス面積比(低解像度での近似)。</summary>
            public float areaFrac;
            /// <summary>全チャンネルが floodRejectFrac 以上 = 背景に流れた可能性。</summary>
            public bool floodWarning;
        }

        /// <summary>
        /// 拡大前のチャンネル選択結果。低解像度ロジットだけを見るので安価
        /// (高価な <see cref="UpscaleChannel"/> を呼ぶ前に、どれを拡大するか決められる)。
        /// </summary>
        internal struct Selection {
            /// <summary>採用チャンネル(1..3)。</summary>
            public int channel;
            /// <summary>採用チャンネルの予測 IoU スコア。</summary>
            public float score;
            /// <summary>採用チャンネルのキャンバス面積比(低解像度での近似)。</summary>
            public float areaFrac;
            /// <summary>妥当な候補が 1 つも無かった = 背景に流れた可能性。</summary>
            public bool floodWarning;
        }

        /// <summary>
        /// 低解像度ロジットからチャンネルを選ぶ(拡大なし)。
        ///
        /// 妥当性は 2 条件:
        ///   (1) 面積比が floodRejectFrac 未満かつ 0 でない(背景洪水・空候補の棄却)
        ///   (2) stability score = |logit &gt; +1| / |logit &gt; -1| が StabilityThreshold 以上
        ///       (SAM 標準の安定度。背景へにじんだ候補はロジットが 0 付近に寝るため
        ///        この比が落ちる。面積規則がそういう候補を掴むのを防ぐ)
        /// 採用は粒度規則: 自動 = 面積最大 / 細かい = 面積最小 / 大きい = 面積最大。
        /// 「大きい」だけは (2) を課さない — 自動より大きく取りたいという明示指示なので、
        /// 安定度で切ると自動と同じ答えになり選択肢として機能しないため。
        /// 同面積はスコアで決着。妥当候補が全滅したら最小面積を警告付きで返す。
        ///
        /// 自動が「予測スコア最大」でないのは、SAM の iou_predictions が自然画像の物体
        /// としての自信であってアトラスのパーツ境界の正しさではないため(実測: 3 候補中の
        /// 最良を選べたのは 5/15、規則差し替えで平均 IoU 0.233 → 0.308)。
        ///
        /// 棄却した仮説(GT 15 被写体 × クリック 3 位置で実測、いずれも採用せず):
        ///   - 「クリック画素を含む」を妥当性に足す … 全粒度で悪化(自動 0.308 → 0.277)。
        ///     最良候補でもクリック直上のロジットが 0 をわずかに割ることがあり、
        ///     その 1 点で候補ごと落とすと代わりに悪い候補が上がる。
        ///   - ズーム発火判定を最小面積候補で行う … 主スイートで悪化(0.308 → 0.296)。
        ///     自動が正しく大きい領域を選んだケースでクロップが対象を切ってしまう。
        ///     小パーツを 1 クリックで取る用途は「細かい」+ ズームが担当する(実測 IoU 0.96-0.98)。
        /// </summary>
        public static Selection SelectChannels(float[] logits, float[] scores, int texW, int texH,
                                               float floodRejectFrac = DefaultFloodRejectFrac,
                                               MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto) {
            MeasureChannels(logits, texW, texH, out float[] area, out float[] stability);

            bool InFloodRange(int c) => area[c] > 0f && area[c] < floodRejectFrac;
            bool IsStable(int c) => InFloodRange(c) && stability[c] >= StabilityThreshold;

            // 母集団: 安定候補 → (全滅なら)面積だけ通った候補。
            bool anyStable = IsStable(1) || IsStable(2) || IsStable(3);
            bool anyInRange = InFloodRange(1) || InFloodRange(2) || InFloodRange(3);
            bool warn = !anyInRange;

            int chosen;
            if (warn) {
                // 全滅: 最小面積のチャンネルを警告付きで提示(スパイクでは無駄撃ち扱いだが、
                // 製品はユーザーが見て捨てられるので情報を残す)
                chosen = PickExtreme(_ => true, area, scores, smallest: true);
            } else {
                // 「大きい」は安定ゲートを外す(上記の理由)。
                bool useStable = granularity != MaskSuggestGranularity.Coarse && anyStable;
                var pool = useStable ? (System.Func<int, bool>)IsStable : InFloodRange;
                chosen = PickExtreme(pool, area, scores,
                                     smallest: granularity == MaskSuggestGranularity.Fine);
            }

            return new Selection {
                channel = chosen,
                score = scores[chosen],
                areaFrac = area[chosen],
                floodWarning = warn,
            };
        }

        /// <summary>
        /// multimask の各チャンネル(1..3)の面積比(キャンバスの有効域に対する比)と安定度を低解像度ロジットで測る
        /// (添字 0 は使わない)。<see cref="SelectChannels"/> と <see cref="SelectAndUpscaleByColor"/> が共有する。
        /// </summary>
        static void MeasureChannels(float[] logits, int texW, int texH, out float[] area, out float[] stability) {
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            // 低解像度空間での有効域(パディング除去相当)。1024→256 は 1/4。
            float lw = newW * (LowRes / (float)SamImageOps.InputSize);
            float lh = newH * (LowRes / (float)SamImageOps.InputSize);
            int lwCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lw));
            int lhCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lh));

            area = new float[4];
            stability = new float[4];
            for (int c = 1; c < 4; c++) {
                int cOff = c * LowRes * LowRes;
                int count = 0, hi = 0, lo = 0;
                for (int y = 0; y < lhCeil; y++) {
                    int row = cOff + y * LowRes;
                    for (int x = 0; x < lwCeil; x++) {
                        float v = logits[row + x];
                        if (v > 0f) {
                            count++;
                        }
                        if (v > StabilityOffset) {
                            hi++;
                        }
                        if (v > -StabilityOffset) {
                            lo++;
                        }
                    }
                }
                area[c] = count / (lw * lh);
                stability[c] = lo > 0 ? hi / (float)lo : 0f;
            }
        }

        /// <summary>母集団 pool の中で面積が最小/最大のチャンネル(同面積はスコアで決着)。</summary>
        static int PickExtreme(System.Func<int, bool> pool, float[] area, float[] scores, bool smallest) {
            int best = -1;
            for (int c = 1; c < 4; c++) {
                if (!pool(c)) {
                    continue;
                }
                if (best < 0) { best = c; continue; }
                bool better = area[c] != area[best]
                    ? (smallest ? area[c] < area[best] : area[c] > area[best])
                    : scores[c] > scores[best];
                if (better) {
                    best = c;
                }
            }
            if (best >= 0) {
                return best;
            }
            // pool が空(呼び出し側が保証するが念のため): 面積最小へ退避。
            best = 1;
            for (int c = 2; c < 4; c++) {
                if (area[c] < area[best]) {
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// logits: [4,256,256] 平坦配列(上原点・パディング込みキャンバス空間)。scores: [4]。
        /// pixelsBottomUp(texW*texH 画素)を渡すと、拡大後に精密化(SamMaskRefine の 4 段: 境界の再分類 → 房の拡張 →
        /// AA 遷移帯の包含 → 細い描き込みの地はがし)で、低解像度由来の階段状はみ出しを実テクスチャの色エッジへ合わせる
        /// (null なら精密化しない)。
        /// granularity: 採用規則(<see cref="SelectChannels"/> が正)。
        /// </summary>
        public static Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
                                              float floodRejectFrac,
                                              Color32* pixelsBottomUp,
                                              MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto,
                                              System.Threading.CancellationToken token = default) {
            var sel = SelectChannels(logits, scores, texW, texH, floodRejectFrac, granularity);
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            var mask = UpscaleChannel(logits, sel.channel, texW, texH, newW, newH, token);
            try {
                if (pixelsBottomUp != null) {
                    RefineInPlace(mask.Ptr, pixelsBottomUp, texW, texH, token);
                }
            } catch {
                mask.Release();
                throw;
            }
            return new Result {
                mask = mask,
                channel = sel.channel,
                score = sel.score,
                areaFrac = sel.areaFrac,
                floodWarning = sel.floodWarning,
            };
        }

        /// <summary>
        /// 拡大済みマスク(texW*texH 個)への精密化 4 段(境界色スナップ → 房外郭拡張 → AA 遷移包含 → 細い描き込みの合わせ直し)を
        /// この順で適用する。SelectAndUpscale(pixelsBottomUp 付き) と同値になる唯一の
        /// 実行順の単一ソース。粗マスクを後から精密化する遅延実行(ズーム不発時)もここを通す。
        /// </summary>
        public static void RefineInPlace(bool* maskBottomUp, Color32* pixelsBottomUp,
                                         int texW, int texH,
                                         System.Threading.CancellationToken token = default) {
            if (maskBottomUp == null || pixelsBottomUp == null) {
                return;
            }
            // 提案が画像の一部なら、外接矩形に余白を足した矩形だけで 4 段を回す(全画像で回したのとビット同一。
            // 根拠は TryDeriveRefineCropRect)。4K で全画像を回すと、提案が小さくても 1 回 1〜2 秒かかる。
            if (SamMaskRefine.TryDeriveRefineCropRect(maskBottomUp, texW, texH, token,
                    out bool empty, out int rx0, out int ry0, out int rw, out int rh)) {
                RefineCropped(maskBottomUp, pixelsBottomUp, texW, texH, rx0, ry0, rw, rh, token);
                return;
            }
            if (empty) {
                return;   // マスクが空なら 4 段とも何も変えない
            }
            RefineStages(maskBottomUp, pixelsBottomUp, texW, texH, texW, texH, token);
        }

        // 4 段をこの順で回す。sizeW・sizeH は帯幅などの定数を決める寸法(切り出しのときは全画像の寸法)。
        private static void RefineStages(bool* mask, Color32* px, int w, int h, int sizeW, int sizeH,
                                         System.Threading.CancellationToken token) {
            SamMaskRefine.SnapBoundary(mask, px, w, h, token, sizeW, sizeH);
            token.ThrowIfCancellationRequested();
            // 房(細い frayed strands)を実テクスチャ信号で外郭まで拡張(SAM の滑らかな境界が
            // 切り落とす房を救済)。房が無い部位ではほとんど成長しない。
            SamMaskRefine.ExtendFringe(mask, px, w, h, token, sizeW, sizeH);
            token.ThrowIfCancellationRequested();
            // 境界外側の AA 遷移(パーツ色の実混合)を包含する最終仕上げ。スナップ境界は
            // 混合率 ≈50% 点に乗るため、外側に残る混合画素が再着色で点ノイズになるのを防ぐ。
            SamMaskRefine.IncludeAaTransition(mask, px, w, h, token,
                dOverride: SamMaskRefine.BandHalfWidth(sizeW, sizeH));
            token.ThrowIfCancellationRequested();
            // SAM の解像度より細い描き込み(ファスナーの歯・細い線)の提案を、地(陰影を含む)を外して描き込み
            // そのものへ合わせ、SAM が端で切った続きを足す。最後に置くのは、房の拡張は地色より暗い落ち影を
            // 「地色から遠い外側」として、AA 遷移包含は暗い縁取りと落ち影の間を「混色」として足すため。
            SamMaskRefine.PeelThinSpill(mask, px, w, h, token, sizeW, sizeH);
        }

        // 矩形 (rx0, ry0, rw, rh) を切り出して 4 段を回し、矩形の行をマスクへ写し戻す。矩形の外は 4 段とも変えない。
        // Snap と Peel は画素を外すこともあるので、足した画素だけでなく行をそのまま写す。
        private static void RefineCropped(bool* mask, Color32* px, int w, int h,
                                          int rx0, int ry0, int rw, int rh,
                                          System.Threading.CancellationToken token) {
            bool* cropMask = WorkPools.Bool.Rent(rw * rh);
            Color32* cropPx = WorkPools.Rgba32.Rent(rw * rh);
            try {
                Parallel.For(0, rh, SamMaskRefine.MakeParallelOptions(token), cy => {
                    int src = (ry0 + cy) * w + rx0;
                    new System.ReadOnlySpan<bool>(mask + src, rw).CopyTo(new System.Span<bool>(cropMask + cy * rw, rw));
                    new System.ReadOnlySpan<Color32>(px + src, rw).CopyTo(new System.Span<Color32>(cropPx + cy * rw, rw));
                });
                RefineStages(cropMask, cropPx, rw, rh, w, h, token);
                for (int cy = 0; cy < rh; cy++) {
                    System.Buffer.MemoryCopy(cropMask + (long)cy * rw, mask + (long)(ry0 + cy) * w + rx0, rw, rw);
                }
            } finally {
                // 中断中はプールの Return が見て、貸し出し中のまま残す(借りた側は見ない)。
                WorkPools.Bool.Return(cropMask);
                WorkPools.Rgba32.Return(cropPx);
            }
        }

        /// <summary>
        /// 1 チャンネルの 256² ロジットを SamPredictor と同じ 2 段 bilinear で元寸へ拡大し、
        /// 閾値 0 で二値化して下原点のマスクを返す(Mono のヒープの外。参照 1)。1024² の中間は作業配列のプールから借りる。
        /// </summary>
        internal static NativeBools UpscaleChannel(float[] logits, int channel, int texW, int texH,
                                                   int newW, int newH,
                                                   System.Threading.CancellationToken token = default) {
            float* up = WorkPools.Float.Rent(SamImageOps.InputSize * SamImageOps.InputSize);
            try {
                return UpscaleChannel(logits, channel, texW, texH, newW, newH, up, token);
            } finally {
                // 中断中はプールの Return が見て、貸し出し中のまま残す(借りた側は見ない)。
                WorkPools.Float.Return(up);
            }
        }

        private static NativeBools UpscaleChannel(float[] logits, int channel, int texW, int texH,
                                                  int newW, int newH, float* up,
                                                  System.Threading.CancellationToken token) {
            int cOff = channel * LowRes * LowRes;

            // 正になりうる所だけ補間する(出力はビット同一)。
            // 補間は 2 段とも a + (b - a) * f(0 ≤ f ≤ 1)を横・縦に重ねた形で、しきい値は `> 0`。
            // 「> 0 でない値」(0 以下・-inf・NaN)どうしをこの式で混ぜても > 0 にならない:
            //   有限で a ≤ 0・b ≤ 0 なら、b ≥ a のとき b - a ≤ -a なので丸めても fl(b - a) ≤ -a、fl(fl(b - a)·f) ≤ -a、
            //   a + それ ≤ 0(丸めは単調で、-a・0 は表せる)。b < a のときは足すのが 0 以下なので和は a 以下。
            //   -inf・NaN を含むと結果は -inf か NaN(inf·0・inf - inf は NaN)で、どちらも > 0 でない。
            // よって、補間に使う標本がどれも > 0 でない画素は、2 段とも > 0 でない(= false)。
            // 256 の段で > 0 の標本の外接矩形を求め、それに標本が掛かる 1024 の画素の範囲(U)、U に標本が掛かる元の
            // 寸法の画素の範囲(T)を順に求める。T の外は false を書き、T の中は以前と同じ式で計算する。T の中の画素が
            // 読む 1024 の画素(範囲 C。U を高々 1 画素広げたもの)は、以前と同じ式・同じ入力で計算するので同じ値になる。
            // 範囲を決める添字は、補間で使う添字と同じ式で求める。
            const int S = SamImageOps.InputSize;
            int lx0 = LowRes, ly0 = LowRes, lx1 = -1, ly1 = -1;
            for (int y = 0; y < LowRes; y++) {
                int row = cOff + y * LowRes;
                for (int x = 0; x < LowRes; x++) {
                    if (logits[row + x] > 0f) {
                        if (x < lx0) {
                            lx0 = x;
                        }
                        if (x > lx1) {
                            lx1 = x;
                        }
                        if (y < ly0) {
                            ly0 = y;
                        }
                        if (y > ly1) {
                            ly1 = y;
                        }
                    }
                }
            }

            // 256 から 1024 の補間の添字。縦も同じ式なので、縦の範囲(下の U の行)もこの表で決める
            // (補間そのものの縦の添字は、以前のとおり行の中で求める)。
            float scale1 = LowRes / (float)S;
            var x0s = new int[S]; var x1s = new int[S]; var fxs = new float[S];
            for (int x = 0; x < S; x++) {
                float sx = (x + 0.5f) * scale1 - 0.5f;
                if (sx < 0f) {
                    sx = 0f;
                }
                if (sx > LowRes - 1) {
                    sx = LowRes - 1;
                }
                int x0 = (int)sx;
                x0s[x] = x0; x1s[x] = Mathf.Min(x0 + 1, LowRes - 1); fxs[x] = sx - x0;
            }
            // 有効領域から元テクスチャ寸法への補間の添字。
            float scaleX = newW / (float)texW, scaleY = newH / (float)texH;
            var cx0 = new int[texW]; var cx1 = new int[texW]; var cfx = new float[texW];
            for (int x = 0; x < texW; x++) {
                float sx = (x + 0.5f) * scaleX - 0.5f;
                if (sx < 0f) {
                    sx = 0f;
                }
                if (sx > newW - 1) {
                    sx = newW - 1;
                }
                int x0 = (int)sx;
                cx0[x] = x0; cx1[x] = Mathf.Min(x0 + 1, newW - 1); cfx[x] = sx - x0;
            }
            var cy0 = new int[texH]; var cy1 = new int[texH];
            for (int y = 0; y < texH; y++) {
                float sy = (y + 0.5f) * scaleY - 0.5f;
                if (sy < 0f) {
                    sy = 0f;
                }
                if (sy > newH - 1) {
                    sy = newH - 1;
                }
                int y0 = (int)sy;
                cy0[y] = y0; cy1[y] = Mathf.Min(y0 + 1, newH - 1);
            }

            // U: 標本の組 [lo, hi] が > 0 の範囲に掛かる 1024 の画素。T: 標本の組が U に掛かる元の寸法の画素。
            // 掛からない画素は組の 2 つが同じ側の外にある(lo ≤ hi)ので、標本はどれも範囲の外。
            // 添字の計算が 2 か所(範囲を決める表と、補間する行のラムダ)にあり、Mono は float の式の途中を倍精度のまま
            // 持ち回ることがあるので、端で 1 ずれても範囲が足りるように U・T・C を 1 ずつ広げる(画像・キャンバスの端で
            // クランプ)。広げた分も以前と同じ式で計算するだけなので、出力は変わらない。
            int ux0 = S, ux1 = -1, uy0 = S, uy1 = -1;
            if (lx1 >= 0) {
                SpanTouching(x0s, x1s, S, lx0, lx1, out ux0, out ux1);
                SpanTouching(x0s, x1s, S, ly0, ly1, out uy0, out uy1);
            }
            int tx0 = texW, tx1 = -1, ty0 = texH, ty1 = -1;
            if (ux1 >= 0 && uy1 >= 0) {
                ux0 = Mathf.Max(0, ux0 - 1); ux1 = Mathf.Min(S - 1, ux1 + 1);
                uy0 = Mathf.Max(0, uy0 - 1); uy1 = Mathf.Min(S - 1, uy1 + 1);
                SpanTouching(cx0, cx1, texW, ux0, ux1, out tx0, out tx1);
                SpanTouching(cy0, cy1, texH, uy0, uy1, out ty0, out ty1);
            }
            bool anyT = tx1 >= 0 && ty1 >= 0;

            // C: T の中の画素が読む 1024 の画素の範囲(T・C とも 1 ずつ広げる)。
            int ex0 = S, ex1 = -1, ey0 = S, ey1 = -1;
            if (anyT) {
                tx0 = Mathf.Max(0, tx0 - 1); tx1 = Mathf.Min(texW - 1, tx1 + 1);
                ty0 = Mathf.Max(0, ty0 - 1); ty1 = Mathf.Min(texH - 1, ty1 + 1);
                for (int x = tx0; x <= tx1; x++) {
                    ex0 = Mathf.Min(ex0, cx0[x]); ex1 = Mathf.Max(ex1, cx1[x]);
                }
                for (int y = ty0; y <= ty1; y++) {
                    ey0 = Mathf.Min(ey0, cy0[y]); ey1 = Mathf.Max(ey1, cy1[y]);
                }
                ex0 = Mathf.Max(0, ex0 - 1); ex1 = Mathf.Min(S - 1, ex1 + 1);
                ey0 = Mathf.Max(0, ey0 - 1); ey1 = Mathf.Min(S - 1, ey1 + 1);
            }

            // 行ごとに独立な bilinear(書き込みは自行のみ)なので行並列で決定的。C の外は書かない(後段も読まない)。
            var po = SamMaskRefine.MakeParallelOptions(token);
            if (anyT) {
                int cx0C = ex0, cx1C = ex1;   // ラムダ用の写し
                Parallel.For(ey0, ey1 + 1, po, y => {
                    float sy = (y + 0.5f) * scale1 - 0.5f;
                    if (sy < 0f) {
                        sy = 0f;
                    }
                    if (sy > LowRes - 1) {
                        sy = LowRes - 1;
                    }
                    int y0 = (int)sy; int y1 = Mathf.Min(y0 + 1, LowRes - 1);
                    float fy = sy - y0;
                    int r0 = cOff + y0 * LowRes, r1 = cOff + y1 * LowRes, dr = y * S;
                    for (int x = cx0C; x <= cx1C; x++) {
                        float a = logits[r0 + x0s[x]] + (logits[r0 + x1s[x]] - logits[r0 + x0s[x]]) * fxs[x];
                        float b = logits[r1 + x0s[x]] + (logits[r1 + x1s[x]] - logits[r1 + x0s[x]]) * fxs[x];
                        up[dr + x] = a + (b - a) * fy;
                    }
                });
            }

            // 有効領域を元テクスチャ寸法へ補間し、下原点へ戻す(全画素に書く。T の外は false)。
            var result = NativeBools.AllocateUninitialized(texW, texH);
            try {
                bool* mask = result.Ptr;
                int ttx0 = tx0, ttx1 = tx1, tty0 = ty0, tty1 = ty1;   // ラムダ用の写し
                Parallel.For(0, texH, po, y => {
                    int dstRow = (texH - 1 - y) * texW; // 上原点 y → 下原点行
                    if (!anyT || y < tty0 || y > tty1) {
                        new System.Span<bool>(mask + dstRow, texW).Clear();
                        return;
                    }
                    float sy = (y + 0.5f) * scaleY - 0.5f;
                    if (sy < 0f) {
                        sy = 0f;
                    }
                    if (sy > newH - 1) {
                        sy = newH - 1;
                    }
                    int y0 = (int)sy; int y1 = Mathf.Min(y0 + 1, newH - 1);
                    float fy = sy - y0;
                    int r0 = y0 * S, r1 = y1 * S;
                    if (ttx0 > 0) {
                        new System.Span<bool>(mask + dstRow, ttx0).Clear();
                    }
                    if (ttx1 < texW - 1) {
                        new System.Span<bool>(mask + dstRow + ttx1 + 1, texW - 1 - ttx1).Clear();
                    }
                    for (int x = ttx0; x <= ttx1; x++) {
                        float a = up[r0 + cx0[x]] + (up[r0 + cx1[x]] - up[r0 + cx0[x]]) * cfx[x];
                        float b = up[r1 + cx0[x]] + (up[r1 + cx1[x]] - up[r1 + cx0[x]]) * cfx[x];
                        mask[dstRow + x] = (a + (b - a) * fy) > 0f;
                    }
                });
            } catch {
                // 拡大の途中の取り消し(Parallel.For の OperationCanceledException)でも、作ったマスクを返す。まだ誰にも
                // 渡していないので、持ち主はここだけ(4K で 16MB がファイナライザまで残っていた)。プールの Return と違い、
                // 持ち主の解放は中断中かを見ない。
                result.Release();
                throw;
            }
            return result;
        }

        /// <summary>
        /// 補間の標本の組 [lo[i], hi[i]] が範囲 [a, b] に掛かる i の最小と最大(無ければ first = n・last = -1)。
        /// </summary>
        static void SpanTouching(int[] lo, int[] hi, int n, int a, int b, out int first, out int last) {
            first = n;
            last = -1;
            for (int i = 0; i < n; i++) {
                if (lo[i] <= b && hi[i] >= a) {
                    if (i < first) {
                        first = i;
                    }
                    last = i;
                }
            }
        }
    }
}
