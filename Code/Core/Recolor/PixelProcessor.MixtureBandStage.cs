// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 4 つ目「混色帯」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。再着色する範囲(強度が <see cref="RecolorMinStrength"/> を超える画素の矩形)を求め、
        /// 混色帯モードのゾーンでは境界の帯を解析して被覆率と塗る色を書き、範囲を帯まで広げる
        /// (<see cref="ZoneState.RcMinX"/> ほか・<see cref="ZoneState.MixAlpha"/>。帯が無ければ null)。
        /// </summary>
        private static class MixtureBandStage
        {
            internal static void Run(RunContext run, ZoneState z)
            {
                // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
                int w = run.W, h = run.H, len = run.Len;
                Color32* originalPixels = run.Original;
                var po = run.Po;
                var cancellationToken = run.Ct;
                ulong* commonMask = run.CommonBits;
                int[] maskColOf = run.MaskColOf, maskRowBase = run.MaskRowBase;
                int decontaminationRadius = run.DecontaminationRadius;
                var rb = run.Buf;
                var _sub = run.Sub;
                var zone = z.Zone;
                ulong* zoneMask = z.ZoneMask;
                bool zMixMode = z.MixMode;
                bool* includedPx = z.IncludedPx;
                var strengthForRecolor = z.Strength;
                Color32* decontaminatedPixels = z.Decontaminated;
                var rcParams = z.Params;
                float zRegLmid = z.RegLmid;
                float* zRegMidMap = z.RegMidMap;
                float* zRegUpMap = z.RegUpMap;

                // bbox 制限(P2-7): recolor ループは先頭で s<=0.001 を skip し近傍読みをしないため、
                // strength>0.001 の bbox だけを走査すれば出力はビット不変。マッチ領域が小さいゾーン
                // (ロゴ等)で OkLab 再着色の per-pixel コストを実マッチ範囲に限定する。bbox 走査は
                // 軽い比較 1 パスで、recolor の重い per-pixel コスト削減が上回る。
                // 走査自体は共通の並列 bbox ヘルパへ寄せる(同条件の逐次コピーだった)。
                TryComputeStrengthBBox(strengthForRecolor, w, h, RecolorMinStrength,
                    out int rcMinX, out int rcMinY, out int rcMaxX, out int rcMaxY, cancellationToken);
                _sub.Mark(SpRecolorBBox);
                // 混色帯(選択境界の AA・にじみ)の解析。境界クリーンアップ ON かつ有彩サンプルのゾーンでは、
                // 境界の画素を「被覆率ぶんだけ隣の素材の変化を足す」合成の式で塗る(PixelProcessor.MixtureBand.cs
                // の AnalyzeMixtureBand)。対象の画素は mixAlpha ≥ 0、色は decontaminatedPixels に入り、
                // 下のループが読む。無彩サンプル(グレーモード相当)は色度で被覆率を測れないので従来の経路。
                float* mixAlphaLocal = null;
                if (zMixMode && rcMaxX >= 0)
                {
                    float* mixAlpha = rb.MixAlphaOrRent(len);
                    bool* mixExcluded = null;
                    if (commonMask != null || zoneMask != null)
                    {
                        // 解析が読むのは bbox ± (2·探索半径 + 1)。その範囲の除外フラグを埋める。
                        mixExcluded = rb.MaskExcludedOrRent(len);
                        int mm = 2 * Mathf.Max(MixBandRadius + 1, decontaminationRadius) + 1;
                        int my0 = Mathf.Max(0, rcMinY - mm), my1 = Mathf.Min(h - 1, rcMaxY + mm);
                        int mx0 = Mathf.Max(0, rcMinX - mm), mx1 = Mathf.Min(w - 1, rcMaxX + mm);
                        FillExcludedRect(mixExcluded, w, mx0, my0, mx1, my1,
                            maskColOf, maskRowBase, commonMask, zoneMask, po);
                    }
                    AnalyzeMixtureBand(originalPixels, strengthForRecolor, w, h, decontaminationRadius,
                        in rcParams, zRegMidMap, zRegUpMap, zRegLmid,
                        MostChromaticSample(zone), includedPx,
                        decontaminatedPixels, mixAlpha,
                        rcMinX, rcMinY, rcMaxX, rcMaxY,
                        out int mixMinX, out int mixMinY, out int mixMaxX, out int mixMaxY,
                        mixExcluded, cancellationToken);
                    // 帯は選択の外側にも伸びるので、ループの範囲を広げる。
                    rcMinX = mixMinX; rcMinY = mixMinY; rcMaxX = mixMaxX; rcMaxY = mixMaxY;
                    mixAlphaLocal = mixAlpha;
                }
                _sub.Mark(SpMixBand);

                z.RcMinX = rcMinX; z.RcMinY = rcMinY; z.RcMaxX = rcMaxX; z.RcMaxY = rcMaxY;
                z.MixAlpha = mixAlphaLocal;
            }
        }
    }
}
