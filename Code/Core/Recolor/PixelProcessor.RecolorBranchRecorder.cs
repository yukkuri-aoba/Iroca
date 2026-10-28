// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の後に、デバッグの記録があるときだけ呼ぶ観察者(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から
        /// 中身を書き換えずに切り出した)。各画素に再着色のどの分岐が当たったかを記録する。
        /// </summary>
        private static class RecolorBranchRecorder
        {
            // Recolor 段で各ピクセルに適用されたサブブランチを記録する。
            // hot loop には分岐を増やさず、debug 有効時だけ追加の Parallel.For で
            // 上の RecolorPixel 内の条件式を再評価する。
            // 優先度: Decontaminate > Shadow > Highlight > Base。
            // shadow と highlight は条件上ほぼ排他（oV<thr と oV>sV）だが念のため shadow を優先。
            // NOTE: 実際の RecolorPixel に渡した値を使うこと。
            //   Shadow: zone.shadowDesaturation でなく zEffShadowDesat (autoRecolorAnchor 時に補正済み)
            //   Highlight: sampV でなく zWV (HighlightSampleCorrector で補正した実効 wash サンプルの V)
            internal static void Record(RunContext run, ZoneState z)
            {
                // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
                int w = run.W, h = run.H, len = run.Len;
                var po = run.Po;
                float* pixV = run.PixV;
                int* pixIdx = run.PixIdx;
                var debug = run.Debug;
                var zone = z.Zone;
                float sampS = z.SampS;
                var strengthForRecolor = z.Strength;
                bool* aaMask = z.AaMask;
                float zEffShadowDesat = z.Params.shadowDesaturation;
                float zWV = z.Params.washV;
                int rcMinX = z.RcMinX, rcMinY = z.RcMinY, rcMaxX = z.RcMaxX, rcMaxY = z.RcMaxY;
                float* mixAlphaLocal = z.MixAlpha;

                byte[] branchMap = new byte[len];
                float zoneShadowDesat = zEffShadowDesat;
                float zoneSV = zWV;
                float zoneSS = sampS;
                bool zoneApplyWash = zone.applyHighlightWash;
                var aaMaskForBranch = aaMask;
                var mixForBranch = mixAlphaLocal;
                int bx0 = rcMinX, by0 = rcMinY, bx1 = rcMaxX, by1 = rcMaxY;
                Parallel.For(0, len, po, i =>
                {
                    // 混色帯で塗った画素(選択の外側も含む)は、合成で塗る分岐として記録する。
                    // mixAlpha が有効なのは解析した矩形の中だけ。
                    if (mixForBranch != null)
                    {
                        int bx = i % w, by = i / w;
                        if (bx >= bx0 && bx <= bx1 && by >= by0 && by <= by1 && mixForBranch[i] >= 0f
                            && mixForBranch[i] < MixAsMaterial)
                        {
                            branchMap[i] = (byte)(mixForBranch[i] > 0f ? DebugBranch.Decontaminate : DebugBranch.None);
                            return;
                        }
                    }
                    if (strengthForRecolor[i] <= RecolorMinStrength)
                    {
                        branchMap[i] = (byte)DebugBranch.None;
                        return;
                    }
                    if (aaMaskForBranch != null && aaMaskForBranch[i])
                    {
                        branchMap[i] = (byte)DebugBranch.Decontaminate;
                        return;
                    }
                    float oV = pixV[HsvSlot(pixIdx, i)];
                    if (zoneShadowDesat > 0f && oV < zoneShadowDesat)
                    {
                        branchMap[i] = (byte)DebugBranch.Shadow;
                        return;
                    }
                    if (zoneApplyWash && zoneSS > 0.01f && oV > zoneSV)
                    {
                        branchMap[i] = (byte)DebugBranch.Highlight;
                        return;
                    }
                    branchMap[i] = (byte)DebugBranch.Base;
                });
                debug.RecordRecolorBranches(zone.id, branchMap, w, h);
            }
        }
    }
}
