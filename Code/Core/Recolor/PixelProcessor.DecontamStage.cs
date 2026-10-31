// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Diagnostics;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 2 つ目「デコンタミ」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。AA 境界の画素を α 分解して新色で再合成した色を作る(<see cref="ZoneState.AaMask"/>・
        /// <see cref="ZoneState.Decontaminated"/>。設定が無効なら null のまま)。
        /// </summary>
        private static class DecontamStage
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
                bool useDecontamination = run.UseDecontamination;
                int decontaminationRadius = run.DecontaminationRadius;
                var rb = run.Buf;
                var debug = run.Debug;
                var _phaseTicks = run.PhaseTicks;
                var _sub = run.Sub;
                long _tp = run.Tp;
                var zone = z.Zone;
                ulong* zoneMask = z.ZoneMask;
                bool zMixMode = z.MixMode;
                float* strength = z.Strength;
                bool hasPostBox = z.Box.Has;
                int ppMinX = z.Box.MinX, ppMinY = z.Box.MinY, ppMaxX = z.Box.MaxX, ppMaxY = z.Box.MaxY;

                // 3b. AA 境界の α 分解（オプション）：strength が 0 < s < interiorThreshold の
                //     ピクセルを「α×FG + (1-α)×BG」と見て元テクスチャの合成を逆算し、
                //     新色で再合成する。halo（薄汚れた中間色）を構造的に除去する。
                bool* aaMask = null;
                Color32* decontaminatedPixels = null;
                if (useDecontamination)
                {
                    aaMask = rb.DecontamAaMask;
                    decontaminatedPixels = rb.DecontamPixels;
                    // 内部固め(上)が無彩ターゲットの内部を均一化したので、旧 AA フィデリティ修正の
                    // interior_threshold=1.01(全画素 α 再合成)は不要(むしろ内部を背景色で再合成して
                    // 段差を復活させる)。常に通常閾値で AA 縁だけをデコンタミする。
                    float effInteriorThreshold = DecontaminationInteriorThreshold;
                    // 除外マスク画素は strength=0 だが「背景」ではない(サンプル同色の保護パーツで
                    // あり得る)。BG ドナーに入れると推定色がサンプル色で汚染され、マスク境界の外側に
                    // 誤色の点ノイズを塗るため、位置を渡してドナーから隠す(マスク中立化)。
                    bool* deconExcluded = null;
                    if (commonMask != null || zoneMask != null)
                    {
                        deconExcluded = rb.MaskExcludedOrRent(len);
                        // デコンタミが除外フラグを読むのは BG ドナー範囲(後段 bbox ± radius)だけ
                        // なので、そこだけ埋める。範囲外は読まれない=出力ビット不変
                        // (バッファはゾーン間で使い回すが、各ゾーンが自分の読む範囲を必ず埋める)。
                        int exY0 = Mathf.Max(0, ppMinY - decontaminationRadius);
                        int exY1 = Mathf.Min(h - 1, ppMaxY + decontaminationRadius);
                        int exX0 = Mathf.Max(0, ppMinX - decontaminationRadius);
                        int exX1 = Mathf.Min(w - 1, ppMaxX + decontaminationRadius);
                        if (hasPostBox)
                            FillExcludedRect(deconExcluded, w, exX0, exY0, exX1, exY1,
                                maskColOf, maskRowBase, commonMask, zoneMask, po);
                    }
                    // 後段 bbox(ppMin/Max)を渡してデコンタミを実マッチ範囲に限定する。α 分解が
                    // 触るのは 0<strength<threshold の画素だけ=定義上この bbox 内なので出力ビット不変。
                    _sub.Mark(SpDecontamExcl);
                    DecontaminateAaBoundary(originalPixels, strength, w, h,
                        zone.sampleColor, zone.targetColor,
                        decontaminationRadius, effInteriorThreshold,
                        aaMask, decontaminatedPixels, hasPostBox, cancellationToken,
                        deconExcluded, ppMinX, ppMinY, ppMaxX, ppMaxY,
                        solidifyOnly: zMixMode);
                    _sub.Mark(SpDecontam);
                    debug?.RecordDecontamination(zone.id, ToManaged(aaMask, len), w, h);
                }

                _phaseTicks[PhDecontam] += Stopwatch.GetTimestamp() - _tp; _tp = Stopwatch.GetTimestamp();

                z.AaMask = aaMask;
                z.Decontaminated = decontaminatedPixels;
                run.Tp = _tp;
            }
        }
    }
}
