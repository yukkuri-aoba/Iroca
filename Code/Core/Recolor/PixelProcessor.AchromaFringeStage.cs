// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 6 つ目「無彩のフチ消し」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。無彩(白↔黒)の再着色で、マッチ境界の外側に残る地色の混色画素を背景へ寄せる。
        /// 有彩のゾーンと、混色帯で縁を塗り終えたゾーンでは何もしない。
        /// </summary>
        private static class AchromaFringeStage
        {
            internal static void Run(RunContext run, ZoneState z)
            {
                // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
                int w = run.W, h = run.H, len = run.Len;
                Color32* pixels = run.Pixels;
                Color32* originalPixels = run.Original;
                var po = run.Po;
                var cancellationToken = run.Ct;
                ulong[] commonMask = run.CommonMask;
                int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
                var rb = run.Buf;
                var _phaseTicks = run.PhaseTicks;
                var _sub = run.Sub;
                long _tp = run.Tp;
                var zone = z.Zone;
                ulong[] zoneMask = z.ZoneMask;
                float zAchromaWeight = z.AchromaWeight;
                var strengthForRecolor = z.Strength;
                var claimedLocal = rb.Claimed;
                int rcMinX = z.RcMinX, rcMinY = z.RcMinY, rcMaxX = z.RcMaxX, rcMaxY = z.RcMaxY;
                float* mixAlphaLocal = z.MixAlpha;

                // 無彩(白↔黒)再着色のエッジに残る「地色の残り」フチ消し。マッチ境界の外側 2px に残る
                // 背景より明るい混色画素を α 分解で背景へ寄せ、暗い再着色色に対する明るいフチを消す。
                // 有彩(zAchromaWeight≈0)では呼ばれず完全 no-op。共有のマッチ/合成経路は変更しない。
                // 混色帯の解析が走ったゾーン(有彩サンプル)では、外側の縁もそちらが合成の式で塗り終えている。
                if (zAchromaWeight > AchromaWeightActiveMin && rcMaxX >= 0 && mixAlphaLocal == null)
                {
                    // 除外マスク画素の位置をフチ消しへ渡す。渡さないとフチ消しは
                    //   (a) 除外画素を BG ドナーに数えて BG 推定をサンプル色で汚染し
                    //   (b) 除外画素そのものへ target 混色を書き込む(マスク契約違反)
                    // という DecontaminateAaBoundary では 05ecca8 で塞いだ穴を残す。
                    // 埋める範囲はフチ消しが読む bbox±AchromaFringeExclusionMargin だけ
                    // (デコンタミ側の充填は ppMin/Max±decontaminationRadius かつ
                    //  useDecontamination 時のみなので、ここは独立に埋める必要がある)。
                    bool* fringeExcluded = null;
                    if (commonMask != null || zoneMask != null)
                    {
                        fringeExcluded = rb.MaskExcludedOrRent(len);
                        const int fm = AchromaFringeExclusionMargin;
                        int fy0 = Mathf.Max(0, rcMinY - fm), fy1 = Mathf.Min(h - 1, rcMaxY + fm);
                        int fx0 = Mathf.Max(0, rcMinX - fm), fx1 = Mathf.Min(w - 1, rcMaxX + fm);
                        FillExcludedRect(fringeExcluded, w, fx0, fy0, fx1, fy1,
                            maskColOf, maskRowBase, commonMask, zoneMask, po);
                    }
                    CleanAchromaFringe(pixels, originalPixels, strengthForRecolor, claimedLocal,
                        w, h, zone.sampleColor, zone.targetColor, rcMinX, rcMinY, rcMaxX, rcMaxY,
                        cancellationToken, fringeExcluded);
                }

                _sub.Mark(SpAchromaFringe);
                _phaseTicks[PhRecolor] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                run.Tp = _tp;
            }
        }
    }
}
