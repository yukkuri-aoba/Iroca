// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    internal static unsafe partial class PixelProcessor
    {
        /// <summary>
        /// 再着色の段の 5 つ目「合成」(0.4.0 の計画の 3-7b で ProcessPixelsArrayCore から中身を書き換えずに
        /// 切り出した)。再着色の範囲の画素ごとに、再着色の核(<see cref="RecolorPixel"/>。色の表があれば色ごとの表か
        /// スレッドごとのメモで使い回す)が出した色を、強度・占有率・デコンタミ・混色帯に従って出力へ重ねる。
        /// </summary>
        private static class ComposeStage
        {
            internal static void Run(RunContext run, ZoneState z)
            {
                // ProcessPixelsArrayCore と同じ名前のローカルへ展開する(切り出した本体をそのまま読めるように)。
                int w = run.W, h = run.H, len = run.Len;
                Color32* pixels = run.Pixels;
                Color32* originalPixels = run.Original;
                var po = run.Po;
                var palette = run.Palette;
                float* pixV = run.PixV;
                int* pixIdx = run.PixIdx;
                var debug = run.Debug;
                var _sub = run.Sub;
                var zone = z.Zone;
                float* strength = z.Strength;
                var strengthForRecolor = strength;
                var aaMaskLocal = z.AaMask;
                var decontaminatedLocal = z.Decontaminated;
                var claimedLocal = run.Buf.Claimed;
                var rcParams = z.Params;
                float zRegLmid = z.RegLmid;
                float* zRegMidMap = z.RegMidMap;
                float* zRegUpMap = z.RegUpMap;
                int rcMinX = z.RcMinX, rcMinY = z.RcMinY, rcMaxX = z.RcMaxX, rcMaxY = z.RcMaxY;
                float* mixAlphaLocal = z.MixAlpha;

                // 再着色色は画素の色(RGBA と色から求めた V)とゾーン定数だけで決まる。成分別 L マップ
                // (無彩パス)を使わないゾーンでは色ごとに 1 回だけ求めて配る。表の大きさが再着色の
                // 範囲より大きい(小さなロゴ等)ときは画素ごとに求めたほうが安いので使わない。
                Color32[] palRecolored = null;
                if (palette != null && zRegMidMap == null && rcMaxX >= 0
                    && (long)palette.Count * 2 <= (long)(rcMaxX - rcMinX + 1) * (rcMaxY - rcMinY + 1))
                {
                    int pc = palette.Count;
                    var outRc = new Color32[pc];
                    Color32[] pCol = palette.Colors;
                    float* pV = palette.V;
                    var rcParamsLocal = rcParams;
                    float lmid = zRegLmid;
                    ForEachPaletteChunk(pc, po, (k0, k1) =>
                    {
                        for (int k = k0; k < k1; k++)
                        {
                            Color32 c = pCol[k];
                            outRc[k] = RecolorPixel(c.r, c.g, c.b, pV[k], c.a / 255f, in rcParamsLocal, lmid, AchromaFormOffset);
                        }
                    });
                    palRecolored = outRc;
                }
                int* palIdxRecolor = palette != null ? palette.Index : null;
                // 色ごとの表を作れないゾーン(成分別 L マップあり)は、スレッドごとのメモで使い回す。
                int recolorMemoEpoch = palette != null && palRecolored == null && rcMaxX >= 0
                    ? NextRecolorMemoEpoch() : 0;
                // rcMaxX<0 はマッチ画素なし → 全画素 continue で何もしないのと同じ(出力不変)。
                if (rcMaxX >= 0)
                Parallel.For(rcMinY, rcMaxY + 1, po, y =>
                {
                    int rowOff = y * w;
                    for (int x = rcMinX; x <= rcMaxX; x++)
                    {
                        int i = rowOff + x;
                        float s = strengthForRecolor[i];
                        float maRaw = mixAlphaLocal != null ? mixAlphaLocal[i] : -1f;
                        // 解析が「素材そのもの」と判定した弱い選択の画素: 全強度で再着色する。
                        if (maRaw >= MixAsMaterial) { s = 1f; maRaw = -1f; }
                        bool mix = maRaw >= 0f;
                        if (s <= RecolorMinStrength && !mix) continue;
                        // 上位(リスト上位)ゾーンが既に占有した分を差し引いた実効強度 es。
                        // 残り(room)が無ければこのゾーンは適用しない(= 上位が排他)。
                        float room = 1f - claimedLocal[i];
                        if (room <= 0.001f) continue;
                        if (mix)
                        {
                            // 混色帯: 被覆率 α のぶんだけ、隣の素材の変化(decontaminatedLocal − 元)を足す。
                            // 占有も α だけ。残り(1−α)は背景のぶんで、下位ゾーンが自分の被覆率ぶんを重ねられる。
                            float ma = maRaw;
                            float a = ma < room ? ma : room;
                            if (a > 0f)
                            {
                                Color32 mo = originalPixels[i], mt = decontaminatedLocal[i];
                                if (a >= ma && claimedLocal[i] <= 0.0001f)
                                    pixels[i] = mt;
                                else
                                {
                                    float k = a / ma;
                                    Color32 cur = pixels[i];
                                    pixels[i] = new Color32(
                                        (byte)Mathf.Clamp(Mathf.RoundToInt(cur.r + (mt.r - mo.r) * k), 0, 255),
                                        (byte)Mathf.Clamp(Mathf.RoundToInt(cur.g + (mt.g - mo.g) * k), 0, 255),
                                        (byte)Mathf.Clamp(Mathf.RoundToInt(cur.b + (mt.b - mo.b) * k), 0, 255),
                                        cur.a);
                                }
                                claimedLocal[i] = Mathf.Min(1f, claimedLocal[i] + a);
                            }
                            continue;
                        }
                        float es = s < room ? s : room;
                        bool topMost = claimedLocal[i] <= 0.0001f;
                        if (aaMaskLocal != null && aaMaskLocal[i])
                        {
                            // AA pixel: use decontaminated value (overrides standard mix)。
                            // 最上位(room=1)なら従来どおり完全置換。下位なら残り分だけ被せる。
                            // デコンタミ値は合成済みエッジ色なので、このエッジ画素は上位として占有する。
                            pixels[i] = room >= 0.999f
                                ? decontaminatedLocal[i]
                                : Color32.Lerp(pixels[i], decontaminatedLocal[i], room);
                            claimedLocal[i] = 1f;
                            continue;
                        }
                        Color32 op = originalPixels[i];
                        float alpha = op.a / 255f;
                        bool hasMapMid = zRegMidMap != null && zRegMidMap[i] > 0f;
                        float regMid = hasMapMid ? zRegMidMap[i] : zRegLmid;
                        float regUp = hasMapMid && zRegUpMap != null ? zRegUpMap[i] : AchromaFormOffset;
                        Color32 recolored = palRecolored != null
                            ? palRecolored[palIdxRecolor[i]]
                            : recolorMemoEpoch != 0
                                ? RecolorPixelMemo(recolorMemoEpoch, palIdxRecolor[i], regMid, regUp,
                                    op, pixV[HsvSlot(pixIdx, i)], alpha, in rcParams)
                                : RecolorPixel(
                                    op.r, op.g, op.b,
                                    pixV[HsvSlot(pixIdx, i)], alpha,
                                    in rcParams,
                                    regMid, regUp);
                        if (topMost)
                        {
                            // 最上位の寄与(claimed≈0)。es=s なので従来挙動と完全一致し、
                            // 単一ゾーン/非重複ピクセルの出力はバイト単位で不変。
                            pixels[i] = es >= 0.999f ? recolored : Color32.Lerp(pixels[i], recolored, es);
                        }
                        else
                        {
                            // 下位ゾーン: front-to-back over。透明な残り領域へ es 分だけ色を充填する。
                            // pixels[i] += (recolored - original) * es （上位が置いた色は保持される）。
                            Color32 cur = pixels[i];
                            pixels[i] = new Color32(
                                (byte)Mathf.Clamp(Mathf.RoundToInt(cur.r + (recolored.r - op.r) * es), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(cur.g + (recolored.g - op.g) * es), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(cur.b + (recolored.b - op.b) * es), 0, 255),
                                (byte)Mathf.Clamp(Mathf.RoundToInt(cur.a + (recolored.a - op.a) * es), 0, 255));
                        }
                        claimedLocal[i] = es >= 1f ? 1f : Mathf.Min(1f, claimedLocal[i] + es);
                    }
                });
                _sub.Mark(SpRecolorLoop);
                debug?.RecordStage(zone.id, DebugStages.Recolor, ToManaged(strength, len), w, h);
            }
        }
    }
}
