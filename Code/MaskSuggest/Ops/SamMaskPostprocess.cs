// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// デコーダ出力(low_res_logits [4,256,256] + iou_predictions [4])から提案マスクを作る純計算。
    /// SamPredictor の後処理と同値:
    ///   bilinear 256→1024(align_corners=False) → リサイズ実寸 (newH,newW) で切り出し →
    ///   bilinear で元寸 → 閾値 0 で二値化。最後に下原点(GetPixels32 順)へ反転して返す。
    /// チャンネル選択は <see cref="SelectChannels"/> が正(妥当性判定 → 粒度規則の 2 段)。
    /// </summary>
    internal static class SamMaskPostprocess
    {
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

        internal sealed class Result
        {
            /// <summary>下原点 texW*texH の提案マスク。</summary>
            public bool[] maskBottomUp;
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
        internal struct Selection
        {
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
                                               MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto)
        {
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            // 低解像度空間での有効域(パディング除去相当)。1024→256 は 1/4。
            float lw = newW * (LowRes / (float)SamImageOps.InputSize);
            float lh = newH * (LowRes / (float)SamImageOps.InputSize);
            int lwCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lw));
            int lhCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lh));

            // multimask の各チャンネルの面積比と安定度を低解像度で測る。
            var area = new float[4];
            var stability = new float[4];
            for (int c = 1; c < 4; c++)
            {
                int cOff = c * LowRes * LowRes;
                int count = 0, hi = 0, lo = 0;
                for (int y = 0; y < lhCeil; y++)
                {
                    int row = cOff + y * LowRes;
                    for (int x = 0; x < lwCeil; x++)
                    {
                        float v = logits[row + x];
                        if (v > 0f) count++;
                        if (v > StabilityOffset) hi++;
                        if (v > -StabilityOffset) lo++;
                    }
                }
                area[c] = count / (lw * lh);
                stability[c] = lo > 0 ? hi / (float)lo : 0f;
            }

            bool InFloodRange(int c) => area[c] > 0f && area[c] < floodRejectFrac;
            bool IsStable(int c) => InFloodRange(c) && stability[c] >= StabilityThreshold;

            // 母集団: 安定候補 → (全滅なら)面積だけ通った候補。
            bool anyStable = IsStable(1) || IsStable(2) || IsStable(3);
            bool anyInRange = InFloodRange(1) || InFloodRange(2) || InFloodRange(3);
            bool warn = !anyInRange;

            int chosen;
            if (warn)
            {
                // 全滅: 最小面積のチャンネルを警告付きで提示(スパイクでは無駄撃ち扱いだが、
                // 製品はユーザーが見て捨てられるので情報を残す)
                chosen = PickExtreme(_ => true, area, scores, smallest: true);
            }
            else
            {
                // 「大きい」は安定ゲートを外す(上記の理由)。
                bool useStable = granularity != MaskSuggestGranularity.Coarse && anyStable;
                var pool = useStable ? (System.Func<int, bool>)IsStable : InFloodRange;
                chosen = PickExtreme(pool, area, scores,
                                     smallest: granularity == MaskSuggestGranularity.Fine);
            }

            return new Selection
            {
                channel = chosen,
                score = scores[chosen],
                areaFrac = area[chosen],
                floodWarning = warn,
            };
        }

        /// <summary>母集団 pool の中で面積が最小/最大のチャンネル(同面積はスコアで決着)。</summary>
        static int PickExtreme(System.Func<int, bool> pool, float[] area, float[] scores, bool smallest)
        {
            int best = -1;
            for (int c = 1; c < 4; c++)
            {
                if (!pool(c)) continue;
                if (best < 0) { best = c; continue; }
                bool better = area[c] != area[best]
                    ? (smallest ? area[c] < area[best] : area[c] > area[best])
                    : scores[c] > scores[best];
                if (better) best = c;
            }
            if (best >= 0) return best;
            // pool が空(呼び出し側が保証するが念のため): 面積最小へ退避。
            best = 1;
            for (int c = 2; c < 4; c++) if (area[c] < area[best]) best = c;
            return best;
        }

        /// <summary>
        /// logits: [4,256,256] 平坦配列(上原点・パディング込みキャンバス空間)。scores: [4]。
        /// pixelsBottomUp を渡すと、拡大後に境界色スナップ(SamMaskRefine)で低解像度由来の
        /// 階段状はみ出しを実テクスチャの色エッジへ吸着させる。
        /// granularity: 採用規則(<see cref="SelectChannels"/> が正)。
        /// </summary>
        public static Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
                                              float floodRejectFrac = DefaultFloodRejectFrac,
                                              Color32[] pixelsBottomUp = null,
                                              MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto,
                                              System.Threading.CancellationToken token = default)
        {
            var sel = SelectChannels(logits, scores, texW, texH, floodRejectFrac, granularity);
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            var mask = UpscaleChannel(logits, sel.channel, texW, texH, newW, newH, token);
            if (pixelsBottomUp != null)
                RefineInPlace(mask, pixelsBottomUp, texW, texH, token);
            return new Result
            {
                maskBottomUp = mask,
                channel = sel.channel,
                score = sel.score,
                areaFrac = sel.areaFrac,
                floodWarning = sel.floodWarning,
            };
        }

        /// <summary>
        /// 拡大済みマスクへの精密化 3 段(境界色スナップ → 房外郭拡張 → AA 遷移包含)を
        /// この順で適用する。SelectAndUpscale(pixelsBottomUp 付き) と同値になる唯一の
        /// 実行順の単一ソース。粗マスクを後から精密化する遅延実行(ズーム不発時)もここを通す。
        /// </summary>
        public static void RefineInPlace(bool[] maskBottomUp, Color32[] pixelsBottomUp,
                                         int texW, int texH,
                                         System.Threading.CancellationToken token = default)
        {
            SamMaskRefine.SnapBoundary(maskBottomUp, pixelsBottomUp, texW, texH, token);
            token.ThrowIfCancellationRequested();
            // 房(細い frayed strands)を実テクスチャ信号で外郭まで拡張(SAM の滑らかな境界が
            // 切り落とす房を救済)。房が無い部位ではほとんど成長しない。
            SamMaskRefine.ExtendFringe(maskBottomUp, pixelsBottomUp, texW, texH, token);
            token.ThrowIfCancellationRequested();
            // 境界外側の AA 遷移(パーツ色の実混合)を包含する最終仕上げ。スナップ境界は
            // 混合率 ≈50% 点に乗るため、外側に残る混合画素が再着色で点ノイズになるのを防ぐ。
            SamMaskRefine.IncludeAaTransition(maskBottomUp, pixelsBottomUp, texW, texH, token);
        }

        /// <summary>
        /// 1 チャンネルの 256² ロジットを SamPredictor と同じ 2 段 bilinear で元寸へ拡大し、
        /// 閾値 0 で二値化して下原点 bool[] を返す。
        /// </summary>
        internal static bool[] UpscaleChannel(float[] logits, int channel, int texW, int texH,
                                              int newW, int newH,
                                              System.Threading.CancellationToken token = default)
        {
            int cOff = channel * LowRes * LowRes;

            // 256 から 1024 のキャンバス全域へ補間する。
            const int S = SamImageOps.InputSize;
            var up = new float[S * S];
            float scale1 = LowRes / (float)S;
            var x0s = new int[S]; var x1s = new int[S]; var fxs = new float[S];
            for (int x = 0; x < S; x++)
            {
                float sx = (x + 0.5f) * scale1 - 0.5f;
                if (sx < 0f) sx = 0f; if (sx > LowRes - 1) sx = LowRes - 1;
                int x0 = (int)sx;
                x0s[x] = x0; x1s[x] = Mathf.Min(x0 + 1, LowRes - 1); fxs[x] = sx - x0;
            }
            // 行ごとに独立な bilinear(書き込みは自行のみ)なので行並列で決定的
            var po = SamMaskRefine.MakeParallelOptions(token);
            Parallel.For(0, S, po, y =>
            {
                float sy = (y + 0.5f) * scale1 - 0.5f;
                if (sy < 0f) sy = 0f; if (sy > LowRes - 1) sy = LowRes - 1;
                int y0 = (int)sy; int y1 = Mathf.Min(y0 + 1, LowRes - 1);
                float fy = sy - y0;
                int r0 = cOff + y0 * LowRes, r1 = cOff + y1 * LowRes, dr = y * S;
                for (int x = 0; x < S; x++)
                {
                    float a = logits[r0 + x0s[x]] + (logits[r0 + x1s[x]] - logits[r0 + x0s[x]]) * fxs[x];
                    float b = logits[r1 + x0s[x]] + (logits[r1 + x1s[x]] - logits[r1 + x0s[x]]) * fxs[x];
                    up[dr + x] = a + (b - a) * fy;
                }
            });

            // 有効領域を元テクスチャ寸法へ補間し、下原点へ戻す。
            var mask = new bool[texW * texH];
            float scaleX = newW / (float)texW, scaleY = newH / (float)texH;
            var cx0 = new int[texW]; var cx1 = new int[texW]; var cfx = new float[texW];
            for (int x = 0; x < texW; x++)
            {
                float sx = (x + 0.5f) * scaleX - 0.5f;
                if (sx < 0f) sx = 0f; if (sx > newW - 1) sx = newW - 1;
                int x0 = (int)sx;
                cx0[x] = x0; cx1[x] = Mathf.Min(x0 + 1, newW - 1); cfx[x] = sx - x0;
            }
            Parallel.For(0, texH, po, y =>
            {
                float sy = (y + 0.5f) * scaleY - 0.5f;
                if (sy < 0f) sy = 0f; if (sy > newH - 1) sy = newH - 1;
                int y0 = (int)sy; int y1 = Mathf.Min(y0 + 1, newH - 1);
                float fy = sy - y0;
                int r0 = y0 * S, r1 = y1 * S;
                int dstRow = (texH - 1 - y) * texW; // 上原点 y → 下原点行
                for (int x = 0; x < texW; x++)
                {
                    float a = up[r0 + cx0[x]] + (up[r0 + cx1[x]] - up[r0 + cx0[x]]) * cfx[x];
                    float b = up[r1 + cx0[x]] + (up[r1 + cx1[x]] - up[r1 + cx0[x]]) * cfx[x];
                    mask[dstRow + x] = (a + (b - a) * fy) > 0f;
                }
            });
            return mask;
        }
    }
}
