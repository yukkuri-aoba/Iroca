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
    /// チャンネル選択は計測プロトコル(dev_safe/ml/sam_spike.py)と同じ:
    ///   multimask チャンネル 1..3 をスコア降順に走査し、面積がキャンバス比 floodRejectFrac
    ///   未満の最良を採用。全滅時は最小面積のチャンネルを警告付きで返す(最終判断はユーザー)。
    /// </summary>
    internal static class SamMaskPostprocess
    {
        /// <summary>デコーダ低解像度マスクの一辺。</summary>
        public const int LowRes = 256;

        /// <summary>スパイクで確定した背景洪水マスクの棄却しきい(キャンバス面積比)。</summary>
        public const float DefaultFloodRejectFrac = 0.40f;

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
        /// logits: [4,256,256] 平坦配列(上原点・パディング込みキャンバス空間)。scores: [4]。
        /// pixelsBottomUp を渡すと、拡大後に境界色スナップ(SamMaskRefine)で低解像度由来の
        /// 階段状はみ出しを実テクスチャの色エッジへ吸着させる。
        /// granularity: 洪水棄却を通った候補の中からの採用規則(スコア/最小面積/最大面積)。
        /// </summary>
        public static Result SelectAndUpscale(float[] logits, float[] scores, int texW, int texH,
                                              float floodRejectFrac = DefaultFloodRejectFrac,
                                              Color32[] pixelsBottomUp = null,
                                              MaskSuggestGranularity granularity = MaskSuggestGranularity.Auto)
        {
            SamImageOps.GetResizedSize(texW, texH, out int newW, out int newH);
            // 低解像度空間での有効域(パディング除去相当)。1024→256 は 1/4。
            float lw = newW * (LowRes / (float)SamImageOps.InputSize);
            float lh = newH * (LowRes / (float)SamImageOps.InputSize);
            int lwCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lw));
            int lhCeil = Mathf.Min(LowRes, Mathf.CeilToInt(lh));

            // multimask チャンネル 1..3 の面積比を低解像度で見積もる
            var area = new float[4];
            for (int c = 1; c < 4; c++)
            {
                int cOff = c * LowRes * LowRes;
                int count = 0;
                for (int y = 0; y < lhCeil; y++)
                {
                    int row = cOff + y * LowRes;
                    for (int x = 0; x < lwCeil; x++)
                        if (logits[row + x] > 0f) count++;
                }
                area[c] = count / (lw * lh);
            }

            // 粒度規則に従い、面積が棄却しきい未満の候補から採用
            // (Auto=スコア降順 / Fine=面積昇順 / Coarse=面積降順。同値はスコアで決着)
            int chosen = -1;
            var order = new[] { 1, 2, 3 };
            switch (granularity)
            {
                case MaskSuggestGranularity.Fine:
                    System.Array.Sort(order, (a, b) =>
                        area[a] != area[b] ? area[a].CompareTo(area[b]) : scores[b].CompareTo(scores[a]));
                    break;
                case MaskSuggestGranularity.Coarse:
                    System.Array.Sort(order, (a, b) =>
                        area[a] != area[b] ? area[b].CompareTo(area[a]) : scores[b].CompareTo(scores[a]));
                    break;
                default:
                    System.Array.Sort(order, (a, b) => scores[b].CompareTo(scores[a]));
                    break;
            }
            foreach (int c in order)
            {
                // 面積 0 の空候補は採用しない(Fine の面積昇順で空マスクを掴む事故を防ぐ)
                if (area[c] > 0f && area[c] < floodRejectFrac) { chosen = c; break; }
            }
            bool warn = chosen < 0;
            if (warn)
            {
                // 全滅: 最小面積のチャンネルを警告付きで提示(スパイクでは無駄撃ち扱いだが、
                // 製品はユーザーが見て捨てられるので情報を残す)
                chosen = 1;
                for (int c = 2; c < 4; c++) if (area[c] < area[chosen]) chosen = c;
            }

            var mask = UpscaleChannel(logits, chosen, texW, texH, newW, newH);
            if (pixelsBottomUp != null)
            {
                SamMaskRefine.SnapBoundary(mask, pixelsBottomUp, texW, texH);
                // 房(細い frayed strands)を実テクスチャ信号で外郭まで拡張(SAM の滑らかな境界が
                // 切り落とす房を救済)。房が無い部位ではほとんど成長しない。
                SamMaskRefine.ExtendFringe(mask, pixelsBottomUp, texW, texH);
                // 境界外側の AA 遷移(パーツ色の実混合)を包含する最終仕上げ。スナップ境界は
                // 混合率 ≈50% 点に乗るため、外側に残る混合画素が再着色で点ノイズになるのを防ぐ。
                SamMaskRefine.IncludeAaTransition(mask, pixelsBottomUp, texW, texH);
            }
            return new Result
            {
                maskBottomUp = mask,
                channel = chosen,
                score = scores[chosen],
                areaFrac = area[chosen],
                floodWarning = warn,
            };
        }

        /// <summary>
        /// 1 チャンネルの 256² ロジットを SamPredictor と同じ 2 段 bilinear で元寸へ拡大し、
        /// 閾値 0 で二値化して下原点 bool[] を返す。
        /// </summary>
        internal static bool[] UpscaleChannel(float[] logits, int channel, int texW, int texH,
                                              int newW, int newH)
        {
            int cOff = channel * LowRes * LowRes;

            // 第 1 段: 256 → 1024(キャンバス全域、align_corners=False)
            const int S = SamImageOps.InputSize;
            var up = new float[S * S];
            float scale1 = LowRes / (float)S; // 0.25
            // 列方向の補間位置を前計算
            var x0s = new int[S]; var x1s = new int[S]; var fxs = new float[S];
            for (int x = 0; x < S; x++)
            {
                float sx = (x + 0.5f) * scale1 - 0.5f;
                if (sx < 0f) sx = 0f; if (sx > LowRes - 1) sx = LowRes - 1;
                int x0 = (int)sx;
                x0s[x] = x0; x1s[x] = Mathf.Min(x0 + 1, LowRes - 1); fxs[x] = sx - x0;
            }
            // 行ごとに独立な bilinear(書き込みは自行のみ)なので行並列で決定的
            var po = SamMaskRefine.MakeParallelOptions();
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

            // 第 2 段: [:newH,:newW] の切り出しを (texH,texW) へ bilinear → 閾値 0 → 下原点反転
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
