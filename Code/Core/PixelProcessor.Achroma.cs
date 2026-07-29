// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca
{
    // PixelProcessor: 無彩(achroma)再着色サポート(定数・中性リジェクト・内部固め・成分別 L 統計)。
    internal static partial class PixelProcessor
    {
        // ───────── 無彩(achroma)パス: 無彩サンプル / 極端無彩ターゲットの再着色破綻対策 ─────────
        // 通常の再着色は sample 彩度 sC を「分母・基準」に使う前提(mag=oC/sC, 彩度ゲート
        // chroma_frac=oC/(sC·FULL_FRAC))。サンプルが無彩(白/黒/灰)だと前提が崩れ、白い地色→黒のような変換で
        // (1) 彩度ゲートが白を明るく残す＝まだら (2) 2区間リマップが sL より明るい画素を 1.0 へ拡張
        // (3) mag=oC/sC が微小彩度ノイズを増幅＝脚色 が起きる。サンプルが無彩 or ターゲットが極端
        // 無彩(白/黒)のとき、L=マッチ領域 L レンジを target ヘッドルームへ収める順序保存リマップ /
        // 彩度=uniform target chroma へ achroma_weight で連続ブレンドする。有彩×有彩では weight=0 で
        // 従来式とバイト不変。不変条件: 単調・順序保存・gain≤1(増幅禁止)。
        private const float AchromaSampleC  = 0.06f;  // sample OkLab chroma がこれ未満で無彩扱い(→1)
        private const float AchromaTargetC  = 0.06f;  // target OkLab chroma がこれ未満で無彩扱い
        private const float AchromaRangeGain = 1.0f;  // [旧] レンジリマップ出力幅 = 元幅 × min(gain,1)。form 版へ移行。
        // 形(立体感)維持版: 成分の地色基準を target 側 offset に置き、偏差を gain 倍して陰影を知覚可能に拡張。
        // rangeRemap = center + (oL−regLmid)·gain。gain は per-pixel 偏差 (oL−regLmid) を一律に倍すため、
        // 入力 L の**高周波(質感/圧縮ノイズ)まで gain 倍**されブロックノイズになる。害が最大化するのは
        // 「明るい地の模様を暗いターゲットへ」のような、偏差の増幅幅が最も広がる無彩変換で、旧 gain=2.5
        // では出力の局所分散が入力の約 1.8 倍まで膨れ、元テクスチャに無いブロックノイズとして視認できた。
        // (高周波成分だけを分離して増幅する平滑化案は不採用。マスク無しで背景と連結・混入するケースで
        // 平滑化の基準 oLsmooth が背景色に汚染され、成分の陰影相関そのものを壊すため。)
        // gain=1.5 では pointwise 線形が保たれるので陰影相関は不変(相関はスケール不変で gain に依らない)、
        // 陰影の P90 コントラストも維持したまま、局所分散比が約 1.4 倍まで下がり視認域を下回る。
        private const float AchromaFormGain = 1.5f;    // 基準からの偏差の増幅率(知覚補償。ノイズ増幅を抑えるため 2.5→1.5)
        private const float AchromaFormOffset = 0.16f; // 地色基準を置く target 側 offset(黒=0+, 白=1-)
        // 成分の地色基準に使う L パーセンタイル。中央値(0.5)だと、ゆるい/広いマスクで対象より暗い
        // 隣接色の縁が成分に混入したとき基準が下振れし、同じ素材のはずの模様が 1 つずつ違う明るさに
        // 仕上がる。高め(0.8)にすると暗い混入に頑健で「素材本来の地色レベル」に揃い、並んだ同型の
        // 模様が均一になる。
        private const float AchromaRefPercentile = 0.80f;
        private const float AchromaRegionCoreThr = 0.5f; // 領域 L レンジを取る strength 下限

        // 彩度整合ゲート(緩和マッチのグレーモード分岐用)の定数は ColorZone.ChromaGate* を共有する
        // (二重定義で乖離しないよう一本化)。緩和マッチ(穴埋め/境界回復)のグレー分岐は主経路と同じ
        // 相対彩度床で純白(中性)を排除する。

        // 有彩サンプル→無彩極端ターゲット(有彩色→白/黒等)の「中性画素リジェクト・フロア」定数。
        // 有彩サンプルはマッチ距離が hue 支配で彩度差を過小評価し、明るい中性画素(無彩の UV 背景等)を
        // 巻き込む。無彩ターゲットのときだけ、サンプル彩度の相対床 sS·Frac 未満の画素を strength から
        // 除去する(高彩度コア近傍 Radius px は保護=対象自身の脱彩した陰影/AA縁を守る)。値は既存定数を流用。
        private const float AchromaNeutralRejectWeightMin = 0.5f; // 白↔黒の極端無彩ターゲットでのみ作動
        private const float NeutralRejectActiveSourceSat = 0.40f; // サンプルがこの彩度以上(=有彩)でのみ作動
        // サンプル彩度 sS·frac 未満を「中性(=無彩の背景)」とみなして弾く。小さめの値にすることで、
        // 中性背景(彩度≈0)だけを落とし、模様の上で淡く抜けた対象色(彩度がサンプルの数%まで落ちた
        // 画素)は残す。大きすぎる(0.30 規模)と淡い対象色まで巻き添えで弾き、模様上の対象色が変換
        // されず放置される。小さすぎる(<0.05)と中性背景を取りこぼす。
        private const float NeutralRejectFloorFrac = 0.07f;
        // ソフト床のフェード開始 = floor·RampFrac。これ未満(中性の背景)は完全に弾き、floor 以上は
        // 完全保持、間は連続フェードで境界の二値ノイズを消す。
        private const float NeutralRejectRampFrac = 0.5f;
        private const int NeutralRejectProtectRadius = 2;         // 高彩度コアからこの px 以内は保護

        /// <summary>
        /// 有彩サンプル→無彩極端ターゲット時の中性画素リジェクト・フロア。サンプル彩度の相対床
        /// (sS·NeutralRejectFloorFrac)未満の画素のうち、高彩度マッチコアから NeutralRejectProtectRadius
        /// px より遠いものの strength を 0 にする。hue 支配距離で巻き込んだ明るい中性背景(白UV背景等)を
        /// 落としつつ、コア近傍の脱彩した陰影/AA縁は保護する(彩度だけでは両者を区別できないため空間距離で
        /// 分離する)。呼び出し側で achromaWeight/サンプル彩度を gate。
        /// </summary>
        private static void RejectNeutralForAchromaTarget(float[] strength, float[] pixS, int w, int h, float sS, CancellationToken ct = default)
        {
            const float matchThr = 0.05f;
            float floor = sS * NeutralRejectFloorFrac;
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            bool[] cur = s_boolPool.Rent(len);
            bool[] nxt = s_boolPool.Rent(len);
            try
            {
                // 高彩度マッチコア(マッチ済み かつ 彩度>=床)。
                for (int i = 0; i < len; i++) cur[i] = strength[i] > matchThr && pixS[i] >= floor;
                // 4 近傍 dilation を ProtectRadius 回(コア近傍を保護領域に広げる)。画像端外は false。
                for (int it = 0; it < NeutralRejectProtectRadius; it++)
                {
                    var curL = cur; var nxtL = nxt;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            bool on = curL[i]
                                || (x > 0 && curL[i - 1])
                                || (x < w - 1 && curL[i + 1])
                                || (y > 0 && curL[i - w])
                                || (y < h - 1 && curL[i + w]);
                            nxtL[i] = on;
                        }
                    });
                    var tmp = cur; cur = nxt; nxt = tmp;
                }
                var protectedCore = cur;
                var strengthL = strength;
                var pixSL = pixS;
                // ソフトな床: floor で二値カットすると、対象色が淡く抜けていく階調(彩度が床付近で
                // 揺らぐ帯)で再着色/非再着色が斑に混在しジャギーノイズになる。floorLo..floor を連続
                // フェードにして境界を滑らかにする。中性の背景(彩度<=floorLo)は w=0 で完全に弾く。
                float floorLo = floor * NeutralRejectRampFrac;
                float rampSpan = Mathf.Max(floor - floorLo, 1e-5f);
                Parallel.For(0, len, po, i =>
                {
                    if (protectedCore[i]) return;
                    float wgt = Mathf.Clamp01((pixSL[i] - floorLo) / rampSpan);
                    if (wgt < 1f) strengthL[i] *= wgt;
                });
            }
            finally
            {
                s_boolPool.Return(cur);
                s_boolPool.Return(nxt);
            }
        }

        /// <summary>
        /// 無彩パスの内部固め: マッチ領域(strength&gt;matchThr)を erodePx だけ侵食した「内部」の
        /// strength を full(=strength→1 へ achromaWeight 比でフェード)に固める。AA 縁(侵食で
        /// 除いた帯)は元の taper を保つ。極端な無彩ターゲット(白↔黒)で、明るい画素ほど弱く
        /// マッチして元色が残る「中央の段差」を消すための前処理。陰影は後段 recolor の achroma
        /// レンジリマップ(gain≤1)が担う。
        /// </summary>
        private static void SolidifyAchromaInterior(float[] strength, int w, int h, float achromaWeight, CancellationToken ct = default)
        {
            const float matchThr = 0.05f;
            const int erodePx = 2;
            int len = w * h;
            var po = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            bool[] cur = s_boolPool.Rent(len);
            bool[] nxt = s_boolPool.Rent(len);
            try
            {
                for (int i = 0; i < len; i++) cur[i] = strength[i] > matchThr;
                // 4 近傍 erosion を erodePx 回。画像端の外は「非マッチ」とみなす(scipy 既定と同じ)。
                for (int it = 0; it < erodePx; it++)
                {
                    var curL = cur; var nxtL = nxt;
                    Parallel.For(0, h, po, y =>
                    {
                        int rowOff = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = rowOff + x;
                            bool keep = curL[i]
                                && x > 0 && curL[i - 1]
                                && x < w - 1 && curL[i + 1]
                                && y > 0 && curL[i - w]
                                && y < h - 1 && curL[i + w];
                            nxtL[i] = keep;
                        }
                    });
                    var tmp = cur; cur = nxt; nxt = tmp;
                }
                var interior = cur;
                var strengthL = strength;
                Parallel.For(0, len, po, i =>
                {
                    if (interior[i]) strengthL[i] = strengthL[i] + (1f - strengthL[i]) * achromaWeight;
                });
            }
            finally
            {
                s_boolPool.Return(cur);
                s_boolPool.Return(nxt);
            }
        }

        /// <summary>
        /// 無彩パス/AA フィデリティ用: 「無彩サンプル / 極端無彩ターゲット」の重み(0..1)を sample/target
        /// 色から求める。有彩サンプル×有彩ターゲットで 0。
        /// </summary>
        private static float ComputeAchromaWeight(Color sample, Color target)
        {
            RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            RgbToOklab(target.r, target.g, target.b, out float tL, out float ta, out float tb);
            float sC = Mathf.Sqrt(sa * sa + sb * sb);
            float tC = Mathf.Sqrt(ta * ta + tb * tb);
            float achromaSample = Mathf.Clamp01(1f - sC / AchromaSampleC);
            float targetExtremeness = 1f - 4f * tL * (1f - tL);
            float targetAchroma = Mathf.Clamp01(1f - tC / AchromaTargetC);
            return Mathf.Max(achromaSample, targetExtremeness * targetAchroma);
        }

        /// <summary>
        /// 中性背景リジェクト(選択保護)用の無彩重み。**明度に依存しない**。
        /// ComputeAchromaWeight は collapse_blend に targetExtremeness=(1-4tL(1-tL)) を掛けるため、
        /// 中明度グレー(tL≈0.5)で重みが≈0 に落ち、灰色ターゲットでは中性背景リジェクトが発動せず
        /// 「白い背景が灰色になる」破綻が出る(黒/白は extremeness≈1 で発動)。選択保護に必要なのは
        /// 『ターゲットが無彩か』だけで明度は無関係なので、extremeness を外し彩度のみで判定する。
        /// </summary>
        private static float ComputeAchromaSelectWeight(Color sample, Color target)
        {
            RgbToOklab(sample.r, sample.g, sample.b, out _, out float sa, out float sb);
            RgbToOklab(target.r, target.g, target.b, out _, out float ta, out float tb);
            float sC = Mathf.Sqrt(sa * sa + sb * sb);
            float tC = Mathf.Sqrt(ta * ta + tb * tb);
            float achromaSample = Mathf.Clamp01(1f - sC / AchromaSampleC);
            float targetAchroma = Mathf.Clamp01(1f - tC / AchromaTargetC);
            return Mathf.Max(achromaSample, targetAchroma);
        }

        /// <summary>
        /// 無彩パスの形維持リマップ用: マッチ領域を 4 近傍連結成分に分け、各成分の OkLab L の
        /// **地色基準(AchromaRefPercentile=P80)** をその成分の全画素へ配る per-pixel マップを返す。
        /// center 基準を **成分ごとに局所化**することで、ゆるいマスクが明るい背景(L≈1.0)を巻き込んだ
        /// とき、全体中央値が背景側へ引きずられ、本来の対象(背景よりわずかに暗いだけの明るい地色)が
        /// ベタ黒へ潰れる不具合を防ぐ。基準に中央値でなく高パーセンタイル(P80)を使うのは、対象より
        /// 暗い隣接色の縁が成分に混入しても基準が下振れせず「素材本来の地色レベル」に揃い、並んだ
        /// 同型の模様が均一に仕上がるため。各成分は自分の地色を基準に再着色されるので、巻き込まれた
        /// 背景と対象の模様はそれぞれ自分の陰影を保ったまま target 側へ写る。
        /// strength>thr の画素のみ連結対象。マッチ無しは null。
        /// </summary>
        private static float[] BuildComponentMedianLMap(
            Color32[] px, float[] strength, int w, int h, float thr, CancellationToken ct = default)
        {
            int len = w * h;
            var bboxPo = new ParallelOptions { MaxDegreeOfParallelism = GetMaxParallelism(), CancellationToken = ct };
            // 対象画素の bbox(連結予測子と同条件)。連結成分アンカリングと同じ共通版を使う。
            if (!TryComputeMatchedBBox(strength, px, w, h, thr,
                    out int minX, out int minY, out int maxX, out int maxY, ct))
                return null;

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            int bwh = bw * bh;
            // label / 探索スタックはプールから借りる(従来は呼び出しごとに new int[bw*bh])。
            int[] label = s_intPool.Rent(bwh);
            int[] stack = s_intPool.Rent(bwh);
            try
            {
            Array.Clear(label, 0, bwh);   // Create プールの Rent はゼロ初期化しない
            var hists = new List<int[]>();   // hists[lab-1] = 成分の L ヒストグラム(256bin)
            var sizes = new List<int>();

            bool Matched(int gi) => strength[gi] > thr && px[gi].a >= 128;

            for (int ly = 0; ly < bh; ly++)
            {
                // 逐次ラベリングは数百 ms 級になり得るので、行ごとにキャンセルを見て
                // ドラッグ中の旧ジョブが CPU を焼き続けないようにする(数値ロジックは不変)。
                if ((ly & 63) == 0) ct.ThrowIfCancellationRequested();
                int lrb = ly * bw;
                int grb = (ly + minY) * w + minX;
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = lrb + lx;
                    if (label[li] != 0) continue;
                    if (!Matched(grb + lx)) continue;

                    int lab = hists.Count + 1;
                    var hist = new int[256];
                    int size = 0;
                    label[li] = lab;
                    int sp = 0;
                    // スタック要素は (y<<16)|x のパック座標(除算なしで隣接と大域 index を出すため)。
                    // 探索順は BFS→DFS に変わるが、成分の分割・採番・ヒストグラム(整数カウント)は
                    // いずれも探索順に依存しないので結果は同一。
                    stack[sp++] = (ly << 16) | lx;
                    while (sp > 0)
                    {
                        int packed = stack[--sp];
                        int cx = packed & 0xFFFF, cy = packed >> 16;
                        int ci = cy * bw + cx;
                        int gi = (cy + minY) * w + (cx + minX);
                        RgbToOklab(px[gi].r, px[gi].g, px[gi].b, out float L, out _, out _);
                        hist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                        size++;
                        if (cx > 0 && label[ci - 1] == 0 && Matched(gi - 1))
                        { label[ci - 1] = lab; stack[sp++] = (cy << 16) | (cx - 1); }
                        if (cx < bw - 1 && label[ci + 1] == 0 && Matched(gi + 1))
                        { label[ci + 1] = lab; stack[sp++] = (cy << 16) | (cx + 1); }
                        if (cy > 0 && label[ci - bw] == 0 && Matched(gi - w))
                        { label[ci - bw] = lab; stack[sp++] = ((cy - 1) << 16) | cx; }
                        if (cy < bh - 1 && label[ci + bw] == 0 && Matched(gi + w))
                        { label[ci + bw] = lab; stack[sp++] = ((cy + 1) << 16) | cx; }
                    }
                    hists.Add(hist);
                    sizes.Add(size);
                }
            }

            var med = new float[hists.Count];
            for (int c = 0; c < hists.Count; c++)
                med[c] = HistValueAtPercentile(hists[c], sizes[c], AchromaRefPercentile, 1f);

            // ラベル → 代表 L の配り直し。行ごとに書き込み先が独立なので並列化しても同一結果。
            var map = new float[len];
            Parallel.For(0, bh, bboxPo, ly =>
            {
                int rb = (ly + minY) * w;
                int lrb = ly * bw;
                for (int lx = 0; lx < bw; lx++)
                {
                    int lab = label[lrb + lx];
                    if (lab != 0) map[rb + (lx + minX)] = med[lab - 1];
                }
            });
            return map;
            }
            finally
            {
                s_intPool.Return(stack);
                s_intPool.Return(label);
            }
        }

        /// <summary>
        /// 無彩パスのレンジリマップ用: マッチ領域の OkLab L の (P05, P95, 中央値) を求める。
        /// core 画素(strength>=AchromaRegionCoreThr かつ α>=128)が少なすぎる場合は strength>0 へ
        /// フォールバック。マッチ画素が無ければ false(呼び出し側は achroma パスをスキップ)。
        /// 特定色/座標非依存の領域統計のみ(脚色しない不変条件)。percentile はヒストグラム離散化のため
        /// 厳密値と ≤1/255 の差を許容する。
        /// </summary>
        private static bool TryComputeRegionLRange(
            Color32[] px, float[] strength, out float lo, out float hi, out float mid,
            CancellationToken ct = default)
        {
            lo = 0f; hi = 1f; mid = 0.5f;
            int len = px.Length;
            var hist = new int[256];
            int count = 0;
            float thr = AchromaRegionCoreThr;
            for (int pass = 0; pass < 2; pass++)
            {
                Array.Clear(hist, 0, hist.Length);
                float passThr = thr;   // ラムダは ref ローカルを捕獲できないのでパス毎に固定する
                count = AccumulateHistParallel(len, hist, (from, to, localHist) =>
                {
                    int n = 0;
                    for (int i = from; i < to; i++)
                    {
                        if (strength[i] < passThr || px[i].a < 128) continue;
                        RgbToOklab(px[i].r, px[i].g, px[i].b, out float L, out _, out _);
                        localHist[Mathf.Clamp((int)(L * 255f), 0, 255)]++;
                        n++;
                    }
                    return n;
                }, ct);
                if (count >= 50 || pass == 1) break;
                thr = 1e-4f;   // フォールバック: strength>0 の全マッチ画素
            }
            if (count < 1) return false;
            lo  = HistValueAtPercentile(hist, count, 0.05f, 1f);
            hi  = HistValueAtPercentile(hist, count, 0.95f, 1f);
            mid = HistValueAtPercentile(hist, count, 0.50f, 1f);
            return true;
        }
    }
}
