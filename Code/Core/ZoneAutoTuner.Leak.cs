// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // ZoneAutoTuner: 漏れの確かめ(自動調整の後の段)。自動調整を当てたゾーンの選択のうち、クリックした部品(証拠 = 最初の
    // AI 提案)の外に塗られた塊ごとに AI の提案を取り直し、「別の部品の一部だけが塗られている」塊をゾーンの除外マスクにする。
    //
    // 【解決する問題】照合の幅(許容範囲・彩度の重みなど)は色だけで決まるので、クリックした素材と似た色の陰・ツヤ・縁を持つ
    // 別の部品が、その陰・ツヤ・縁だけ塗られる(隣の黒いドレスの裾、右のスカートなど)。色の物差しでは、「同じ素材の陰が
    // 塗り残された部品」と「別の素材のツヤだけが塗られた部品」は同じ形で、分けられなかった(dev_safe の
    // autotune_gap_2026-11-14.md の 9.17〜9.20)。
    //
    // 【考え方】部品の単位は AI の提案が知っている。塊の中の 1 点で提案を取り直すと、その塊を含む部品(P2)が分かる:
    //   - P2 の半分以上が最初の提案と重なる → クリックした部品の続き。外さない。
    //   - P2 の半分以上が塗られている → 同じ色の別の部品(同じ色は全部塗る方針)。外さない。
    //   - P2 の一部だけが塗られ、塗りの境目が入力では見えない所(なだらかな陰の途中)を通る → 部品の陰・ツヤだけを
    //     巻き込んでいる。外す。境目が見える縁に沿うなら、色で分かれた部品の一部なので外さない。
    //   - ただし「自動」の提案は、細い部品の上でも服全体を返すことがある(細い金の帯の上の点で服全体。服から見ると帯は
    //     1 割しか塗られていない)。同じ点の「細かい」提案でも半分未満しか塗られていないときだけ外す。
    // 外す範囲は P2 から最初の提案を除いた所(クリックした部品は削らない)。
    // 確かめるのは、最初の提案の縁(LeakEvidenceMargin の幅)から離れた塊だけ。縁に触れて最初の提案から選択のまま続く塊は、
    // AI の提案の境目が部品の境目とずれた所(部品が地の色へ溶けていくぼかし・縁)で、別の部品への漏れではない。白い地に
    // 青い部品がぼけて溶けている所で、地の提案(画像の 2 割)の一部だけが塗られているとして部品のぼかしを外していた
    // (dev_safe の autotune_gap_2026-11-14.md の 9.22)。198 件で外して良かった塊は、どれも縁に触れていなかった。
    // クリックした部品そのもの(最初の提案)が半分も塗られていないとき(|選択 ∩ 証拠| / |証拠| < LeakCoverMax。ほかの部品で
    // 「一部だけ塗られている」とみなすのと同じ線)は確かめない。そのゾーンは部品の一部の明るさだけを選んでいるので、ほかの
    // 部品の一部だけが塗られているのも同じ選び方の続きで、漏れとは言えない(目の陰の帯だけを選んだクリックで、同じ目の別の
    // 部品の帯を外していた。9.22)。
    //
    // 【流れ】(1) FindLeakBlobs で塊と問い合わせ点を求める → (2) 呼び出し側が点ごとに AI の提案を取る(粒度「自動」と
    // 「細かい」。どちらも自動調整の証拠と同じ使い方) → (3) JudgeLeaks で判定し、除外マスクを作る。AI の提案は Core の外
    // (製品は Sentis、テストは Python の SAM)なので、Core は前後の計算だけを持つ。(2) の細かい提案が要るのは、自動の
    // 提案で外す側に入った塊(MeasureLeakProposal の NeedsFine)だけ。
    //
    // 【画素の並び】画素・選択・証拠・提案・除外マスクは、どれも w*h 要素で添字 y*w + x、行 0 = 画像の下端(処理の入口
    // PixelProcessor と自動調整の証拠と同じ下原点の並び)。問い合わせ点(LeakBlob.X, Y)もこの並びの画素の位置で、
    // UV にするなら ((X + 0.5) / w, (Y + 0.5) / h)。
    //
    // 【実測】開発側の 33 被写体 × クリック 6 か所 = 198 件(製品の流れと同じ条件。dev_safe の autotune_gap_2026-11-14.md の
    // 9.20〜9.22): 設計 IoU の平均 +0.003(開発クリック +0.003・未使用クリック +0.003)、良くなった 26・悪くなった 0、退行床の違反なし、
    // どのアバターも平均が下がらない。
    internal static unsafe partial class ZoneAutoTuner {
        // 最初の提案の縁の混色・ぼけは「外」と数えない幅(L1 距離の画素数。最初の提案をこれだけ広げた外を塊にする)。
        private const int LeakEvidenceMargin = 8;
        // 確かめる塊の数の上限(面積の大きい順)。塊ごとに AI の提案を最大 2 回取るので、待ち時間の上限でもある。
        private const int LeakMaxBlobs = 6;
        // 確かめる塊の面積の下限: この画素数と、選択の画素数のこの割合の大きい方。
        private const int LeakMinBlobArea = 400;
        private const double LeakMinBlobFrac = 0.002;
        // P2 のこの割合以上が最初の提案と重なれば、クリックした部品の続き。
        private const double LeakSamePartMin = 0.5;
        // P2 のうち塗られている割合がこれ未満なら、部品の一部だけが塗られている。
        private const double LeakCoverMax = 0.5;
        // P2 の中の塗りの境目(4 近傍の組)のうち、入力で見分けられない組(色の差 < EvCutSeamDeltaE)の割合がこれ以上なら、
        // 境目は面の途中を通る。
        private const double LeakSeamMin = 0.10;
        // 同じ点の細かい提案のうち塗られている割合がこれ未満のときだけ外す。
        private const double LeakFineCoverMax = 0.5;
        // 距離変換の列の処理を並列に分ける幅(列の数)。どう分けても結果は同じ。
        private const int LeakColumnBlock = 64;

        /// <summary>確かめる塊 1 つ(<see cref="FindLeakBlobs"/>)。</summary>
        internal struct LeakBlob {
            /// <summary>
            /// 問い合わせ点: 塊の中で、塊の外の画素(画像の外を含む)までのユークリッド距離が最大の画素。同じ距離なら
            /// 添字(y*w + x)の小さい方。並びはファイルの冒頭の説明(下原点)。
            /// </summary>
            public int X, Y;
            /// <summary>塊の画素数(8 近傍の連結)。</summary>
            public int Area;
            /// <summary>問い合わせ点から塊の外までの距離の 2 乗(画素)。</summary>
            public int DepthSq;
        }

        /// <summary>
        /// 外す判定の閾値の組(<see cref="JudgeLeaks"/>・<see cref="MeasureLeakProposal"/>)。製品は <see cref="Default"/>
        /// (台帳の定数)だけを使う。ほかの組は、テストの経路(ハーネスの --leak-judge)が閾値を掃引するためのもの。
        /// </summary>
        internal struct LeakThresholds {
            /// <summary>|P2 ∩ 選択| / |P2| がこれ未満なら「部品の一部だけが塗られている」(<see cref="LeakCoverMax"/>)。</summary>
            public double CoverMax;
            /// <summary>継ぎ目の割合がこれ以上なら「境目が面の途中を通る」(<see cref="LeakSeamMin"/>)。</summary>
            public double SeamMin;
            /// <summary>|P2f ∩ 選択| / |P2f| がこれ未満のときだけ外す(<see cref="LeakFineCoverMax"/>)。</summary>
            public double FineCoverMax;

            /// <summary>製品の閾値(台帳の定数)。</summary>
            public static LeakThresholds Default => new LeakThresholds {
                CoverMax = LeakCoverMax, SeamMin = LeakSeamMin, FineCoverMax = LeakFineCoverMax,
            };
        }

        /// <summary>塊 1 つで取った AI の提案(<see cref="JudgeLeaks"/>)。</summary>
        internal struct LeakProposal {
            /// <summary>提案のマスク(w*h 要素。true = 部品)。null = 取れなかった・取っていない。</summary>
            public bool* Mask;
            /// <summary>提案が背景まで広がった(製品の floodWarning)。部品の証拠にしない。</summary>
            public bool Flood;
        }

        internal enum LeakDecision {
            /// <summary>提案が無い・空・背景まで広がった。何もしない。</summary>
            Skip,
            /// <summary>提案の半分以上が最初の提案と重なる(クリックした部品の続き)。外さない。</summary>
            SamePart,
            /// <summary>確かめたが外さない(半分以上塗られている・境目が見える縁に沿う・細かい提案がほぼ塗られている)。</summary>
            Keep,
            /// <summary>外す(提案から最初の提案を除いた所を除外マスクへ)。</summary>
            Exclude,
        }

        /// <summary>塊 1 つの判定と、その材料(<see cref="MeasureLeakProposal"/>・<see cref="JudgeLeaks"/>)。</summary>
        internal struct LeakVerdict {
            public LeakDecision Decision;
            /// <summary>自動の提案の画素数 |P2|。</summary>
            public int ProposalPixels;
            /// <summary>|P2 ∩ 証拠| / |P2|。Skip なら NaN。</summary>
            public double Same;
            /// <summary>|P2 ∩ 選択| / |P2|。Skip なら NaN。</summary>
            public double Cover;
            /// <summary>継ぎ目の割合 = SeamPairs / max(1, OutlinePairs)。Skip なら NaN。</summary>
            public double Seam;
            /// <summary>P2 ∖ 証拠 の中で隣り合い(4 近傍)、選択が食い違う組の数と、そのうち入力の色の差が EvCutSeamDeltaE 未満の組の数。</summary>
            public int OutlinePairs, SeamPairs;
            /// <summary>自動の提案では外す側に入った(Cover &lt; LeakCoverMax かつ Seam ≥ LeakSeamMin)。細かい提案で決まる。</summary>
            public bool NeedsFine;
            /// <summary>細かい提案の画素数 |P2f|(無い・背景まで広がったなら 0)。</summary>
            public int FinePixels;
            /// <summary>|P2f ∩ 選択| / |P2f|。細かい提案が無い・空・背景まで広がったなら NaN(外さない)。</summary>
            public double FineCover;
            /// <summary>P2 ∖ 証拠 の画素数(Exclude のときに外す画素。ほかの塊と重なることがある)。</summary>
            public int ExcludablePixels;
        }

        /// <summary>
        /// 漏れの塊と問い合わせ点を求める(漏れの確かめの 1 段目。ファイルの冒頭の説明)。
        /// 塊 = 選択 ∧ ¬(証拠を L1 距離 <see cref="LeakEvidenceMargin"/> まで広げた所) の 8 近傍の連結成分のうち、面積が
        /// max(<see cref="LeakMinBlobArea"/>, 選択の画素数 × <see cref="LeakMinBlobFrac"/>) 以上で、広げた所に接しない
        /// (証拠からの L1 距離がちょうど <see cref="LeakEvidenceMargin"/> + 1 の画素を含まない)もの。証拠のうち選ばれている割合が
        /// <see cref="LeakCoverMax"/> 未満なら、塊を探さずに空の配列を返す(ファイルの冒頭の説明)。面積の大きい順
        /// (同じ面積なら、最初の画素の添字の小さい順)に最大 <see cref="LeakMaxBlobs"/> 個。無ければ空の配列。
        /// </summary>
        /// <param name="selected">ゾーンの選択(w*h 要素。<see cref="PixelProcessor.SimulateSelection"/> の selected)。</param>
        /// <param name="evidence">自動調整の証拠(w*h 要素。テクスチャと同じ寸法にしてから渡す)。</param>
        internal static LeakBlob[] FindLeakBlobs(bool* selected, bool* evidence, int w, int h, CancellationToken ct = default) {
            int len = CheckLeakArgs(selected, evidence, w, h);
            var po = CoreParallelism.Options(ct);
            long nSel = CountTrue(selected, w, h, po);
            if (nSel == 0) {
                return Array.Empty<LeakBlob>();
            }
            // クリックした部品そのものが半分も塗られていなければ確かめない(ファイルの冒頭の説明)。
            CountMaskAndSelected(evidence, selected, w, h, po, out long nEv, out long nEvSel);
            if (nEv == 0 || nEvSel < LeakCoverMax * nEv) {
                return Array.Empty<LeakBlob>();
            }
            long minArea = Math.Max(LeakMinBlobArea, (long)(LeakMinBlobFrac * nSel));

            // 証拠からの L1 距離(LeakEvidenceMargin + 2 で頭打ち。+ 1 ちょうどの画素 = 広げた所に 4 近傍で接する画素を見分ける)。
            // 塊の画素 = 選択されていて、距離が幅を超える画素。
            // 8 近傍の連結は、run の右端を 1 つ伸ばして 4 近傍のラベル付けに渡すのと同じ(上下の行の run が x で 1 つずれて
            // 接する = 斜めに隣り合うのを、伸ばした run どうしの重なりとして数える。同じ行の run どうしは 1 画素以上離れて
            // いるので、伸ばしても重ならない)。そのため下の run の右端(X1)は、実際の右端 + 1。
            RunLabels runs;
            byte* dist = WorkPools.Byte.Rent(len);
            bool distReturned = false;
            try {
                EvidenceDistanceL1(evidence, dist, w, h, LeakEvidenceMargin + 2, po);
                runs = RunLabeler.Label(h, (r, rx0, rx1) => {
                    bool* srow = selected + (long)r * w;
                    byte* drow = dist + (long)r * w;
                    int n = 0, x = 0;
                    while (x < w) {
                        if (!srow[x] || drow[x] <= LeakEvidenceMargin) {
                            x++;
                            continue;
                        }
                        int s = x;
                        while (x < w && srow[x] && drow[x] > LeakEvidenceMargin) {
                            x++;
                        }
                        if (rx0 != null) {
                            rx0[n] = s;
                            rx1[n] = x;   // 実際の右端(x - 1)より 1 つ右(8 近傍の連結のため。上の説明)
                        }
                        n++;
                    }
                    return n;
                }, po, ct, withRows: true);
            } catch {
                WorkPools.Byte.Return(dist);
                throw;
            }

            using var scratch = new WorkScratch();
            try {
                if (runs.Count == 0) {
                    return Array.Empty<LeakBlob>();
                }
                int* comp = scratch.Int(runs.Count, false);
                int nComp = RunLabeler.NumberComponents(runs, comp);
                long* area = scratch.Long(nComp, true);
                // 成分が証拠を広げた所に接するか(1 = 接する)。塊の画素を 1 回ずつ見るだけ(選択の画素数以下)。
                int* touches = scratch.Int(nComp, true);
                byte edge = (byte)(LeakEvidenceMargin + 1);
                for (int k = 0; k < runs.Count; k++) {
                    int c = comp[k];
                    area[c] += runs.X1[k] - runs.X0[k];
                    if (touches[c] != 0) {
                        continue;
                    }
                    byte* drow = dist + (long)runs.Y[k] * w;
                    for (int x = runs.X0[k], end = runs.X1[k]; x < end; x++) {
                        if (drow[x] == edge) {
                            touches[c] = 1;
                            break;
                        }
                    }
                }
                WorkPools.Byte.Return(dist);
                distReturned = true;
                // 面積の大きい順に最大 LeakMaxBlobs 個(同じ面積なら成分の番号 = 最初の画素の添字の小さい順)。
                // 証拠を広げた所に接する成分は数えない(ファイルの冒頭の説明)。
                var pick = new int[LeakMaxBlobs];
                int nPick = 0;
                for (int c = 0; c < nComp; c++) {
                    if (area[c] < minArea || touches[c] != 0) {
                        continue;
                    }
                    int at = nPick;
                    while (at > 0 && area[pick[at - 1]] < area[c]) {
                        at--;
                    }
                    if (at >= LeakMaxBlobs) {
                        continue;
                    }
                    for (int j = Math.Min(nPick, LeakMaxBlobs - 1); j > at; j--) {
                        pick[j] = pick[j - 1];
                    }
                    pick[at] = c;
                    nPick = Math.Min(nPick + 1, LeakMaxBlobs);
                }
                var blobs = new LeakBlob[nPick];
                for (int b = 0; b < nPick; b++) {
                    ct.ThrowIfCancellationRequested();
                    int c = pick[b];
                    DeepestPoint(runs, comp, c, w, po, out int x, out int y, out int depthSq);
                    blobs[b] = new LeakBlob { X = x, Y = y, Area = (int)area[c], DepthSq = depthSq };
                }
                return blobs;
            } finally {
                if (!distReturned) {
                    WorkPools.Byte.Return(dist);
                }
                runs.Dispose();
            }
        }

        /// <summary>
        /// 塊 1 つに取った自動の提案(P2)を測り、細かい提案を見る前の判定を返す(Skip・SamePart・Keep。外す側に入ったら
        /// Keep で <see cref="LeakVerdict.NeedsFine"/> が真)。製品はこれで細かい提案が要る塊だけを選んで取れる。
        /// 引数の並びはファイルの冒頭の説明(どれも w*h 要素)。thresholds は省略で製品の閾値(<see cref="LeakThresholds.Default"/>)。
        /// </summary>
        internal static LeakVerdict MeasureLeakProposal(Color32* pixels, bool* selected, bool* evidence, int w, int h,
            LeakProposal proposal, CancellationToken ct = default, LeakThresholds? thresholds = null) {
            CheckLeakArgs(selected, evidence, w, h);
            if (pixels == null) {
                throw new ArgumentNullException(nameof(pixels));
            }
            return MeasureCoarse(pixels, selected, evidence, w, h, proposal, CoreParallelism.Options(ct),
                thresholds ?? LeakThresholds.Default);
        }

        /// <summary>
        /// 漏れの確かめの判定(3 段目)。塊ごとの自動の提案(<paramref name="proposals"/>)と細かい提案
        /// (<paramref name="fineProposals"/>。null なら全部無い)から判定し、外す塊の「P2 ∖ 証拠」の和を
        /// <paramref name="excludeOut"/>(w*h 要素。全画素を書く。true = 除外)へ書く。戻り値は塊ごとの判定(proposals と同じ並び)。
        /// 外す ⇔ |P2 ∩ 選択| / |P2| &lt; <see cref="LeakCoverMax"/> かつ 継ぎ目の割合 ≥ <see cref="LeakSeamMin"/> かつ
        /// 細かい提案があって |P2f ∩ 選択| / |P2f| &lt; <see cref="LeakFineCoverMax"/>(P2 の半分以上が証拠と重なるなら外さない)。
        /// 画素の並びはファイルの冒頭の説明。数はどれも整数の和なので、並列の分け方に依らず同じ結果になる。
        /// </summary>
        /// <param name="excludedPixels">除外マスクの画素数(塊どうしの重なりは 1 回)。</param>
        /// <param name="thresholds">判定の閾値。省略で製品の閾値(<see cref="LeakThresholds.Default"/>)。テストの掃引だけが渡す。</param>
        /// <param name="coarseVerdicts">
        /// 同じ入力(同じ pixels・selected・evidence・proposals、同じ閾値)で <see cref="MeasureLeakProposal"/> が塊ごとに
        /// 返した判定(proposals と同じ並び)。渡せば自動の提案を測り直さずにこれを使う(測るのは整数の数え上げだけなので、
        /// 同じ入力なら測り直しても同じ値になる)。null なら測る。
        /// </param>
        internal static LeakVerdict[] JudgeLeaks(Color32* pixels, bool* selected, bool* evidence, int w, int h,
            LeakProposal[] proposals, LeakProposal[] fineProposals, bool* excludeOut, out int excludedPixels,
            CancellationToken ct = default, LeakThresholds? thresholds = null, LeakVerdict[] coarseVerdicts = null) {
            var th = thresholds ?? LeakThresholds.Default;
            CheckLeakArgs(selected, evidence, w, h);
            if (pixels == null) {
                throw new ArgumentNullException(nameof(pixels));
            }
            if (proposals == null) {
                throw new ArgumentNullException(nameof(proposals));
            }
            if (excludeOut == null) {
                throw new ArgumentNullException(nameof(excludeOut));
            }
            if (fineProposals != null && fineProposals.Length != proposals.Length) {
                throw new ArgumentException("fineProposals の長さが proposals と違います", nameof(fineProposals));
            }
            if (coarseVerdicts != null && coarseVerdicts.Length != proposals.Length) {
                throw new ArgumentException("coarseVerdicts の長さが proposals と違います", nameof(coarseVerdicts));
            }
            var po = CoreParallelism.Options(ct);
            var verdicts = new LeakVerdict[proposals.Length];
            for (int b = 0; b < proposals.Length; b++) {
                ct.ThrowIfCancellationRequested();
                // 測り終えた判定は値の写し(構造体)で受け、下で書き換えても渡された配列は変えない。
                var v = coarseVerdicts != null
                    ? coarseVerdicts[b]
                    : MeasureCoarse(pixels, selected, evidence, w, h, proposals[b], po, th);
                if (v.Decision == LeakDecision.Keep && fineProposals != null) {
                    var fine = fineProposals[b];
                    if (fine.Mask != null && !fine.Flood) {
                        CountMaskAndSelected(fine.Mask, selected, w, h, po, out long nf, out long nfSel);
                        v.FinePixels = (int)nf;
                        if (nf > 0) {
                            v.FineCover = nfSel / (double)nf;
                        }
                    }
                }
                if (v.NeedsFine && !double.IsNaN(v.FineCover) && v.FineCover < th.FineCoverMax) {
                    v.Decision = LeakDecision.Exclude;
                }
                verdicts[b] = v;
            }

            // 外す塊の P2 ∖ 証拠 の和。行ごとに自分の行だけを書く。
            int nEx = 0;
            for (int b = 0; b < verdicts.Length; b++) {
                if (verdicts[b].Decision == LeakDecision.Exclude) {
                    nEx++;
                }
            }
            var masks = new LeakProposal[nEx];
            for (int b = 0, k = 0; b < verdicts.Length; b++) {
                if (verdicts[b].Decision == LeakDecision.Exclude) {
                    masks[k++] = proposals[b];
                }
            }
            long total = 0;
            object gate = new object();
            Parallel.For(0, h, po, () => 0L, (y, _, local) => {
                int rowOff = y * w;
                for (int x = 0; x < w; x++) {
                    int i = rowOff + x;
                    bool e = false;
                    if (!evidence[i]) {
                        for (int k = 0; k < masks.Length; k++) {
                            if (masks[k].Mask[i]) {
                                e = true;
                                break;
                            }
                        }
                    }
                    excludeOut[i] = e;
                    if (e) {
                        local++;
                    }
                }
                return local;
            }, local => {
                lock (gate) {
                    total += local;
                }
            });
            excludedPixels = (int)total;
            return verdicts;
        }

        // 塊 1 つの自動の提案を測る(細かい提案を見る前の判定)。
        private static LeakVerdict MeasureCoarse(Color32* pixels, bool* selected, bool* evidence, int w, int h,
            LeakProposal proposal, ParallelOptions po, LeakThresholds th) {
            var v = new LeakVerdict {
                Decision = LeakDecision.Skip,
                Same = double.NaN,
                Cover = double.NaN,
                Seam = double.NaN,
                FineCover = double.NaN,
            };
            bool* p2 = proposal.Mask;
            if (p2 == null) {
                return v;
            }
            float seam2 = EvCutSeamDeltaE * EvCutSeamDeltaE;
            // [0] |P2| [1] |P2 ∩ 証拠| [2] |P2 ∩ 選択| [3] 輪郭の組 [4] 継ぎ目の組
            var total = new long[5];
            object gate = new object();
            // 数に入るのは P2 の画素 i(と、右・上の隣 j も P2 のときの組)だけなので、P2 の外接矩形の外は数に寄与しない。
            // 矩形の中だけを走査しても、どの数も同じ整数になる(隣 j は i の右・上で、j も P2 なら矩形の中。画像の端の
            // 判定は画像の座標のまま)。矩形は 8 バイトずつ空を飛ばして求める(全画素を 1 バイトずつ見るより安い)。
            if (!MaskRegionEdit.RegionBounds(p2, w, h, out int bx0, out int by0, out int bx1, out int by1)) {
                bx0 = by0 = bx1 = by1 = 0;   // 空: どの数も 0(下の走査は 1 行も回らない)
            }
            Parallel.For(by0, by1, po, () => new long[5], (y, _, local) => {
                int rowOff = y * w;
                for (int x = bx0; x < bx1; x++) {
                    int i = rowOff + x;
                    if (!p2[i]) {
                        continue;
                    }
                    local[0]++;
                    if (selected[i]) {
                        local[2]++;
                    }
                    if (evidence[i]) {
                        local[1]++;
                        continue;   // 継ぎ目を数えるのは P2 ∖ 証拠 の中だけ
                    }
                    // 右と上(添字 + w)の隣だけを見る(組を 1 回ずつ数える)。
                    for (int dir = 0; dir < 2; dir++) {
                        int j;
                        if (dir == 0) {
                            if (x + 1 >= w) {
                                continue;
                            }
                            j = i + 1;
                        } else {
                            if (y + 1 >= h) {
                                continue;
                            }
                            j = i + w;
                        }
                        if (!p2[j] || evidence[j] || selected[i] == selected[j]) {
                            continue;
                        }
                        local[3]++;
                        Color32 a = pixels[i], c = pixels[j];
                        OkLab.RgbToOklab(a.r, a.g, a.b, out float aL, out float aa, out float ab);
                        OkLab.RgbToOklab(c.r, c.g, c.b, out float cL, out float ca, out float cb);
                        float dL = aL - cL, da = aa - ca, db = ab - cb;
                        if (dL * dL + da * da + db * db < seam2) {
                            local[4]++;
                        }
                    }
                }
                return local;
            }, local => {
                lock (gate) {
                    for (int k = 0; k < total.Length; k++) {
                        total[k] += local[k];
                    }
                }
            });
            long n2 = total[0];
            v.ProposalPixels = (int)n2;
            if (n2 == 0 || proposal.Flood) {
                return v;
            }
            v.Same = total[1] / (double)n2;
            v.Cover = total[2] / (double)n2;
            v.OutlinePairs = (int)total[3];
            v.SeamPairs = (int)total[4];
            v.Seam = total[4] / (double)Math.Max(1L, total[3]);
            v.ExcludablePixels = (int)(n2 - total[1]);
            if (v.Same >= LeakSamePartMin) {
                v.Decision = LeakDecision.SamePart;
                return v;
            }
            v.Decision = LeakDecision.Keep;
            v.NeedsFine = v.Cover < th.CoverMax && v.Seam >= th.SeamMin;
            return v;
        }

        // 引数の検証。戻り値は画素数。
        private static int CheckLeakArgs(bool* selected, bool* evidence, int w, int h) {
            if (selected == null) {
                throw new ArgumentNullException(nameof(selected));
            }
            if (evidence == null) {
                throw new ArgumentNullException(nameof(evidence));
            }
            if (w <= 0 || h <= 0 || (long)w * h > int.MaxValue) {
                throw new ArgumentOutOfRangeException(nameof(w), $"invalid size: {w}x{h}");
            }
            return w * h;
        }

        // true の数(行ごとに数えて足す。整数の和なので分け方に依らない)。
        private static long CountTrue(bool* mask, int w, int h, ParallelOptions po) {
            long total = 0;
            object gate = new object();
            Parallel.For(0, h, po, () => 0L, (y, _, local) => {
                bool* row = mask + (long)y * w;
                for (int x = 0; x < w; x++) {
                    if (row[x]) {
                        local++;
                    }
                }
                return local;
            }, local => {
                lock (gate) {
                    total += local;
                }
            });
            return total;
        }

        // |mask| と |mask ∩ selected|。数えるのは mask の画素だけなので、mask の外接矩形の中だけを走査する
        // (矩形の外に mask の画素は無く、どちらの数にも寄与しない。整数の和なので分け方にも依らない)。
        private static void CountMaskAndSelected(bool* mask, bool* selected, int w, int h, ParallelOptions po,
            out long count, out long countSelected) {
            long n = 0, nSel = 0;
            object gate = new object();
            if (!MaskRegionEdit.RegionBounds(mask, w, h, out int bx0, out int by0, out int bx1, out int by1)) {
                count = 0;
                countSelected = 0;
                return;
            }
            Parallel.For(by0, by1, po, () => (n: 0L, s: 0L), (y, _, local) => {
                int rowOff = y * w;
                for (int x = bx0; x < bx1; x++) {
                    int i = rowOff + x;
                    if (mask[i]) {
                        local.n++;
                        if (selected[i]) {
                            local.s++;
                        }
                    }
                }
                return local;
            }, local => {
                lock (gate) {
                    n += local.n;
                    nSel += local.s;
                }
            });
            count = n;
            countSelected = nSel;
        }

        /// <summary>
        /// dist[i] = min(画素 i から最も近い証拠の画素までの L1 距離, cap)(証拠が無ければ cap)。cap は 254 以下。
        /// L1 距離は列と行に分けて求まる(列ごとの 1 次元の距離 → 行ごとに |dx| を足して最小)。頭打ちにしても cap 未満の値は
        /// 変わらない(cap 未満の距離を作る道の途中の値は、どれも cap 未満)。scipy の binary_dilation(十字の構造, iterations=r)
        /// が立てる画素は dist ≤ r の画素と同じ。
        /// </summary>
        private static void EvidenceDistanceL1(bool* evidence, byte* dist, int w, int h, int cap, ParallelOptions po) {
            byte capB = (byte)cap;
            int blocks = (w + LeakColumnBlock - 1) / LeakColumnBlock;
            Parallel.For(0, blocks, po, b => {
                int x0 = b * LeakColumnBlock, x1 = Math.Min(w, x0 + LeakColumnBlock);
                for (int x = x0; x < x1; x++) {
                    dist[x] = evidence[x] ? (byte)0 : capB;
                }
                for (int y = 1; y < h; y++) {
                    long row = (long)y * w;
                    for (int x = x0; x < x1; x++) {
                        long i = row + x;
                        if (evidence[i]) {
                            dist[i] = 0;
                        } else {
                            int up = dist[i - w] + 1;
                            dist[i] = (byte)(up < cap ? up : cap);
                        }
                    }
                }
                for (int y = h - 2; y >= 0; y--) {
                    long row = (long)y * w;
                    for (int x = x0; x < x1; x++) {
                        long i = row + x;
                        int down = dist[i + w] + 1;
                        if (down < dist[i]) {
                            dist[i] = (byte)down;
                        }
                    }
                }
            });
            Parallel.For(0, h, po, y => {
                byte* row = dist + (long)y * w;
                for (int x = 1; x < w; x++) {
                    int left = row[x - 1] + 1;
                    if (left < row[x]) {
                        row[x] = (byte)left;
                    }
                }
                for (int x = w - 2; x >= 0; x--) {
                    int right = row[x + 1] + 1;
                    if (right < row[x]) {
                        row[x] = (byte)right;
                    }
                }
            });
        }

        /// <summary>
        /// 成分 c の中で、成分の外の画素(画像の外を含む)までのユークリッド距離が最大の画素(同じ距離なら添字の小さい方)。
        /// 成分の外接矩形を 1 画素ずつ広げた格子(広げた所は外)で、厳密な距離変換(列ごとの 1 次元の距離 → 行ごとに放物線の
        /// 下側の包絡。Felzenszwalb & Huttenlocher)を求める。格子は作業配列のプールから借りて返す。
        /// runs の右端(X1)は実際の右端 + 1(FindLeakBlobs の説明)。
        /// </summary>
        private static void DeepestPoint(in RunLabels runs, int* comp, int c, int w, ParallelOptions po,
            out int bestX, out int bestY, out int bestDepthSq) {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int k = 0; k < runs.Count; k++) {
                if (comp[k] != c) {
                    continue;
                }
                int y = runs.Y[k];
                minX = Math.Min(minX, runs.X0[k]);
                maxX = Math.Max(maxX, runs.X1[k] - 1);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
            int gw = maxX - minX + 3, gh = maxY - minY + 3;
            int n = checked(gw * gh);
            int* g = WorkPools.Int.Rent(n);
            try {
                new Span<int>(g, n).Clear();
                for (int k = 0; k < runs.Count; k++) {
                    if (comp[k] != c) {
                        continue;
                    }
                    int* row = g + (runs.Y[k] - minY + 1) * gw;
                    for (int x = runs.X0[k], end = runs.X1[k]; x < end; x++) {
                        row[x - minX + 1] = 1;
                    }
                }
                // 列ごと: 上下で最も近い外の画素までの距離(外 = 0。上下の端の行は広げた外なので、どの列にも外がある)。
                int blocks = (gw + LeakColumnBlock - 1) / LeakColumnBlock;
                Parallel.For(0, blocks, po, b => {
                    int x0 = b * LeakColumnBlock, x1 = Math.Min(gw, x0 + LeakColumnBlock);
                    for (int y = 1; y < gh; y++) {
                        int* row = g + y * gw;
                        for (int x = x0; x < x1; x++) {
                            if (row[x] != 0) {
                                row[x] = row[x - gw] + 1;
                            }
                        }
                    }
                    for (int y = gh - 2; y >= 0; y--) {
                        int* row = g + y * gw;
                        for (int x = x0; x < x1; x++) {
                            if (row[x] != 0) {
                                int down = row[x + gw] + 1;
                                if (down < row[x]) {
                                    row[x] = down;
                                }
                            }
                        }
                    }
                });
                // 行ごと: 距離の 2 乗の最小 min_q((x - q)² + 列の距離(q)²)。行の中のいちばん深い画素(同じなら左)を覚える。
                var rowBest = new long[gh];
                var rowBestX = new int[gh];
                Parallel.For(1, gh - 1, po,
                    () => (f: new long[gw], v: new int[gw], z: new double[gw + 1]),
                    (y, _, buf) => {
                        int* row = g + y * gw;
                        long[] f = buf.f;
                        int[] v = buf.v;
                        double[] z = buf.z;
                        for (int q = 0; q < gw; q++) {
                            f[q] = (long)row[q] * row[q];
                        }
                        int k = 0;
                        v[0] = 0;
                        z[0] = double.NegativeInfinity;
                        z[1] = double.PositiveInfinity;
                        for (int q = 1; q < gw; q++) {
                            double s = Intersect(f, q, v[k]);
                            while (s <= z[k]) {
                                k--;
                                s = Intersect(f, q, v[k]);
                            }
                            k++;
                            v[k] = q;
                            z[k] = s;
                            z[k + 1] = double.PositiveInfinity;
                        }
                        long best = -1;
                        int bx = -1;
                        k = 0;
                        for (int q = 0; q < gw; q++) {
                            while (z[k + 1] < q) {
                                k++;
                            }
                            if (row[q] == 0) {
                                continue;
                            }
                            long dq = q - v[k];
                            long d = dq * dq + f[v[k]];
                            if (d > best) {
                                best = d;
                                bx = q;
                            }
                        }
                        rowBest[y] = best;
                        rowBestX[y] = bx;
                        return buf;
                    },
                    _ => { });
                long bestD = -1;
                bestX = minX;
                bestY = minY;
                for (int y = 1; y < gh - 1; y++) {
                    if (rowBest[y] > bestD) {
                        bestD = rowBest[y];
                        bestX = minX + rowBestX[y] - 1;
                        bestY = minY + y - 1;
                    }
                }
                bestDepthSq = (int)Math.Max(0L, bestD);
            } finally {
                WorkPools.Int.Return(g);
            }
        }

        // 放物線 q と p(p < q)の下側の包絡の交点の位置(Felzenszwalb & Huttenlocher の 1 次元の距離変換)。
        private static double Intersect(long[] f, int q, int p) =>
            ((f[q] + (long)q * q) - (f[p] + (long)p * p)) / (2.0 * (q - p));
    }
}
