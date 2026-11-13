// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    // ZoneAutoTuner: 閉ループ検証(実マッチャーで導出パラメータの有害設定を検出し安全側へ倒す)。
    internal static unsafe partial class ZoneAutoTuner {
        // ─────────────────── 閉ループ検証（実マッチャーによる選択シミュレーション） ───────────────────
        // 導出パラメータを ColorZone.GetMatchScoresPrecomputedHSV（出荷されるマッチャーそのもの）に
        // 通して選択を模擬し、統計で有害な設定を検出して安全側へ倒す。距離分布ベースの導出
        // (TryDerive*) は本番マッチの免除経路(ハイライト距離免除・ハイライト復元)を再現しておらず、
        // 「導出 tolerance では拾わないはずの領域」が本番で選択される乖離があるため、最終判定は実マッチャーで
        // 検証する(導出側が持つ簡略モデルではなく、出荷される実装そのものを唯一の正とするための措置)。

        // highlightRecovery 成長テスト:
        // 自動 ON 判定(MergeAnalyzed のハイライト候補数)は「パーツ境界の AA 画素(背景との混色)」を
        // スペキュラと区別できないため、白背景テクスチャではほぼ常に ON になる。ON が有害なのは
        // ハイライト系経路が「サンプルより明るい同色相の別素材(肌など)」を丸ごと拾うときで、
        // その害は選択画素の増加率として直接測れる:
        //   growth = (ハイライト系経路のみで加わり得る画素数) / (通常マッチの画素数)
        // 増加側の経路は 2 つあり、両方を数える:
        //   1. ハイライト復元マッチ(highlightPotential>0) — 彩度を見ない緩い距離
        //   2. ハイライト帯拡張(GrowHighlightBand の候補条件) — sample→白 軸近傍の明るい画素へ
        //      コア連結 BFS で strength=1 を伝播する。肌のような「明るい同色相の隣接素材」は
        //      軸残差が小さく候補化し、境界 AA を橋にして全面が塗られる(実測でこちらが主経路)。
        // 本物のスペキュラはパーツ面積の数%(実測例: 手描きの光沢で +1.0%、合成スペキュラで +2〜3%)、
        // 別素材の巻き込みは数十%以上(実測例: 服の隣の肌を丸ごと拾って +80%)なので明確に分離できる。
        // 面積比というテクスチャ統計のみで決まり、特定色・キャラへの依存は無い。
        //
        // 証拠(AI 提案のセグメント = クリックしたパーツ)があるときは、セグメントの内側で増える画素を数えない。
        // 守りたいのは「別素材への巻き込み」で、それはセグメントの外で起きる。内側の増加はそのパーツ自身の
        // ツヤ・白へ抜けるグラデーションで、ハイライト回復が拾うべきもの。全画面で数えると、毛先が白へ抜ける
        // 髪(パーツの 4 割がツヤ)のような大きなツヤを巻き込みと区別できず、ハイライト回復を切って
        // 白へ抜ける途中に硬い継ぎ目を作っていた(2026-10-08 合成シーン tips_white)。
        private const float HighlightGrowthMaxFrac = 0.15f;
        private const int VerifyMinBaseSelected = 64;

        /// <summary>
        /// 導出 tolerance での基礎選択(グリッドのセルごとに strength > 0 か。不透明・除外マスクの外だけ)。
        /// 3 つの検証はどれも導出 tolerance で同じマッチャー設定の選択を数える(strength は highlightRecovery に
        /// 依らず、変わるのは highlightPot だけ)ので、最初に数えた検証の結果を残りが使う(同じ値なので結果は同じ)。
        /// </summary>
        private sealed class BaseSelection {
            public float tolerance;
            public bool* sel;   // hsv.gw * hsv.gh
            public int count;
        }

        // ─────────────────── 実マッチャーで数える(格子の回り方はここ 1 か所) ───────────────────
        // 閉ループ検証と、証拠つきの全画面選択は、どれも「HSV 格子のセルのうち、不透明で除外マスクの外のもの」を
        // 母集団にして、実マッチャーの結果を数える。母集団の決め方が数える所ごとに食い違うと、検証の間で使い回す
        // 基礎選択(BaseSelection)が合わなくなるので、格子の回り方と母集団の判定は CountOnGrid の 1 つに置き、
        // 数える所ごとの違い(どの照合に通して何を数えるか)だけを visit に書く。
        //
        // セルごとの判定は他のセルに依らず(照合は作ってから書き換えない ColorZone.Matcher を読むだけ)、集計は
        // 整数の数え上げとセルごとのフラグだけなので、格子の行(証拠の画素の列は区間)ごとに並列に数え、行ごとの数を
        // 最後に順に足せば逐次と同じ値になる(整数の和は順序に依らない)。
        // 逐次のままだと自動調整 1 回のうち 4096² で 1〜1.5 秒をこのループが占めていた(2026-10-17 実測)。
        // 取り消しは格子(HsvGrid.ct)のものを渡す(以前は渡しておらず、取り消してもループが最後まで回っていた)。
        private static ParallelOptions MatcherLoopOptions(HsvGrid hsv) =>
            CoreParallelism.Options(hsv.ct);

        private static int SumCounts(int[] counts) {
            int s = 0;
            for (int i = 0; i < counts.Length; i++) {
                s += counts[i];
            }
            return s;
        }

        /// <summary>数える対象の 1 セル(HSV 格子の 1 点。元画像では (x, y) の画素)。</summary>
        private struct SimCell {
            public int cell;      // 格子の添字(gy * gw + gx)
            public int gx, gy;
            public int x, y;      // 元画像の座標
            public Color32 c;
            public Color col;     // c を 0〜1 にした色(α は 1)。照合へ渡す
            public float h, s, v;
        }

        /// <summary>数えた数(何を数えるかは呼ぶ側が決める。使わない欄は 0 のまま)。</summary>
        private struct Tally {
            public int a, b, c;
        }

        private delegate void SimVisitor(in SimCell p, ref Tally row);

        /// <summary>
        /// HSV 格子を行ごとに並列に回り、母集団のセル(不透明・除外マスクの外)ごとに <paramref name="visit"/> を呼んで、
        /// 行ごとの数えを足して返す。<paramref name="visit"/> は別々の行から同時に呼ばれるので、書いてよいのは渡された
        /// 数えと、自分のセルの印だけ。
        /// </summary>
        /// <param name="only">null でなければ、印の付いたセルだけを見る(格子の大きさ)。</param>
        /// <param name="except">null でなければ、印の付いたセルを飛ばす(格子の大きさ)。</param>
        private static Tally CountOnGrid(Color32* pixels, int w, int h, bool* excluded, int maskW, int maskH,
            HsvGrid hsv, bool* only, bool* except, SimVisitor visit) {
            int stride = hsv.stride, gw = hsv.gw;
            var ml = GridMaskLookup.Build(excluded, maskW, maskH, w, h, stride, gw, hsv.gh);
            var rows = new Tally[hsv.gh];
            Parallel.For(0, hsv.gh, MatcherLoopOptions(hsv), gy => {
                SimCell p = default;
                p.gy = gy;
                p.y = gy * stride;
                int rowStart = p.y * w;
                int grow = gy * gw;
                Tally n = default;
                for (int x = 0, gx = 0; x < w; x += stride, gx++) {
                    int cell = grow + gx;
                    if ((only != null && !only[cell]) || (except != null && except[cell])) {
                        continue;
                    }
                    Color32 c = pixels[rowStart + x];
                    if (c.a < PixelProcessor.OpaqueAlphaMin) {
                        continue;
                    }
                    if (ml != null && excluded[ml.Row[gy] + ml.Col[gx]]) {
                        continue;
                    }
                    p.cell = cell;
                    p.gx = gx;
                    p.x = x;
                    p.c = c;
                    p.col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
                    p.h = hsv.h[cell];
                    p.s = hsv.s[cell];
                    p.v = hsv.v[cell];
                    visit(in p, ref n);
                }
                rows[gy] = n;
            });
            Tally sum = default;
            for (int i = 0; i < rows.Length; i++) {
                sum.a += rows[i].a;
                sum.b += rows[i].b;
                sum.c += rows[i].c;
            }
            return sum;
        }

        /// <summary>照合に通して、選択されるか(strength &gt; 0)。</summary>
        private static bool Selects(ColorZone.Matcher matcher, in SimCell p, int w, int h) {
            matcher.GetMatchScoresPrecomputedHSV(p.h, p.s, p.v, p.col, p.x, p.y, w, h, out float strength, out _, out _);
            return strength > 0f;
        }

        /// <summary>証拠の画素 1 つを照合に通して、選択されるか。<paramref name="col"/> は照合へ渡した色(0〜1・α は 1)。</summary>
        private static bool Selects(ColorZone.Matcher matcher, HsvGrid hsv, Color32* pixels, int w, int h,
            GridPt p, out Color col) {
            Color32 c = pixels[p.y * w + p.x];
            col = new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
            matcher.GetMatchScoresPrecomputedHSV(hsv.h[p.gi], hsv.s[p.gi], hsv.v[p.gi], col,
                p.x, p.y, w, h, out float strength, out _, out _);
            return strength > 0f;
        }

        private static void VerifyHighlightRecoveryGrowth(Color32* pixels, int w, int h,
            ColorZone zone, bool* excluded, int maskW, int maskH, HsvGrid hsv,
            bool* evidence, int evW, int evH, BaseSelection baseOut, ref TuneResult result) {
            var sim = BuildSimZone(zone, result);
            var matcher = new ColorZone.Matcher(sim);
            bool* selGrid = hsv.scratch.Bool(hsv.gw * hsv.gh, clear: true);

            // 帯拡張(GrowHighlightBand)の候補条件は同じ判定(PixelProcessor.IsHighlightBandCandidate)を呼ぶ。
            // 連結性(コア連結 BFS)は模擬しない=候補は全て加算される上界。境界 AA が常に
            // 橋になるため実運用でもほぼ全候補へ到達し、上界と実際の差は小さい。
            Color.RGBToHSV(zone.sampleColor, out float bSH, out float bSS, out float bSV);
            bool bandActive = zone.highlightBandExpand && bSS >= PixelProcessor.HlBandMinSampleSat;
            float sR = zone.sampleColor.r, sG = zone.sampleColor.g, sB = zone.sampleColor.b;
            float dR = 1f - sR, dG = 1f - sG, dB = 1f - sB;   // sample → 白 方向
            float dsq = dR * dR + dG * dG + dB * dB;
            if (dsq < 1e-6f) {
                bandActive = false;
            }
            float hueCap = ColorZone.HighlightHueCap(result.tolerance);
            float bandSatFloor = bSS * PixelProcessor.HlBandMinSatFrac;

            var evl = GridMaskLookup.Build(evidence, evW, evH, w, h, hsv.stride, hsv.gw, hsv.gh);   // 証拠が無ければ null
            // a = 基礎選択、b = ハイライト系の経路だけで加わり得る数
            Tally n = CountOnGrid(pixels, w, h, excluded, maskW, maskH, hsv, null, null, (in SimCell p, ref Tally row) => {
                matcher.GetMatchScoresPrecomputedHSV(p.h, p.s, p.v, p.col, p.x, p.y, w, h,
                    out float strength, out float hlPot, out _);
                if (strength > 0f) {
                    row.a++;
                    selGrid[p.cell] = true;
                    return;
                }
                // セグメントの内側の増加はパーツ自身のツヤなので数えない(上の説明)。
                // 表(GridMaskLookup)はマスクの値を最近傍で引くだけなので、ここでは「証拠の内側か」を引くのに使う。
                if (evl != null && evidence[evl.Row[p.gy] + evl.Col[p.gx]]) {
                    return;
                }
                if (hlPot > 0f) {
                    row.b++;
                    return;
                }
                // 帯拡張の候補条件(GrowHighlightBand と同じ判定)
                if (bandActive && PixelProcessor.IsHighlightBandCandidate(p.h, p.s, p.v, p.c,
                        bSH, bSS, bSV, sR, sG, sB, dR, dG, dB, dsq, hueCap, bandSatFloor)) {
                    row.b++;
                }
            });
            int baseSel = n.a, hlOnly = n.b;
            baseOut.tolerance = sim.tolerance;
            baseOut.sel = selGrid;
            baseOut.count = baseSel;
            if (baseSel >= VerifyMinBaseSelected && hlOnly > baseSel * HighlightGrowthMaxFrac) {
                result.highlightRecovery = false;
            }
        }

        // 明部距離免除(bright forgiveness)の過剰検出:
        // 本番マッチは「サンプルより明るく同色相」の画素の距離を最大 70% 免除する
        // (ColorZone.CalculateHybridDistance)。本物のハイライト芯を救う仕組みだが、
        // 「サンプルより明るい同色相の別素材」(服の隣の肌が典型)も同じ色信号を持つため、
        // 距離分布から導出した tolerance では拾わないはずの領域が本番で丸ごと選択される。
        // トーン免除は zone パラメータではないので直接は切れない。そこで
        //   forgivenOnly = (免除ありで選択) − (免除なしで選択)
        // を実マッチャーで数え、免除だけで加わる画素が基礎選択に対して大きすぎるときは、
        // tolerance を段階的に下げて免除経由の巻き込みを打ち切る。本物のハイライト芯は
        // パーツ面積の数%なので発火しない。下げ幅は「サンプル近傍クラスタ(証拠)の被覆率」を
        // ガードし、パーツ本体の取りこぼしが出る手前で止める(拒否系優先・recall ガード付き)。
        private const float BrightForgiveMaxFrac = 0.15f;  // forgivenOnly/baseSel がこれ超で過剰と判定
        private const float EvidenceKeepFrac = 0.97f;  // tol 縮小時に維持すべき証拠被覆率(導出時比)
        private static readonly float[] TolShrinkSteps = { 0.8f, 0.65f, 0.5f };

        private static void VerifyBrightForgivenessOvershoot(Color32* pixels, int w, int h,
            ColorZone zone, bool* excluded, int maskW, int maskH, HsvGrid hsv, BaseSelection baseSel,
            ref TuneResult result) {
            var sim = BuildSimZone(zone, result);
            sim.highlightRecovery = false;  // ハイライト復元は成長テスト側で扱う(基底マッチのみ測る)
            // 免除ありと免除なしは、同じゾーンから別々の照合を作って数える(下の Eval。免除の有無以外は同じ設定)。
            // near 窓に入るか(0=未判定, 1=入る, 2=入らない)。サンプルの窓だけで決まり tolerance に依らないので、
            // tolerance を変えて数え直すときもセルごとに 1 回だけ判定する。
            byte* nearState = hsv.scratch.Byte(hsv.gw * hsv.gh, clear: true);
            float derived = result.tolerance;

            // (selWith, selWithout, evidenceCovered) を tolerance ごとに実マッチャーで数える。
            // 免除ありの選択は、導出 tolerance なら基礎選択(baseSel)そのもの。まだ誰も数えていなければここで数えて残す。
            void Eval(float tol, out int selWith, out int selWithout, out int evCov) {
                sim.tolerance = tol;
                var withForgive = new ColorZone.Matcher(sim);
                var withoutForgive = new ColorZone.Matcher(sim, disableBrightForgiveness: true);
                bool* known = baseSel.sel != null && baseSel.tolerance == tol ? baseSel.sel : null;
                bool* record = known == null && baseSel.sel == null && tol == derived
                    ? hsv.scratch.Bool(hsv.gw * hsv.gh, clear: true) : null;
                var caches = SampleWindows(sim);
                // a = 免除ありで選択、b = 免除なしでも選択、c = そのうち証拠(near 窓の中)。
                // 基礎選択が分かっていれば、その印の付いたセルだけを見る(印の無いセルは未選択: 不透明でない・除外も含む)。
                Tally n = CountOnGrid(pixels, w, h, excluded, maskW, maskH, hsv, known, null, (in SimCell p, ref Tally row) => {
                    if (known == null && !Selects(withForgive, p, w, h)) {
                        return;
                    }
                    row.a++;
                    if (record != null) {
                        record[p.cell] = true;
                    }
                    if (Selects(withoutForgive, p, w, h)) {
                        row.b++;
                    }
                    // 証拠 = いずれかのサンプルの near 窓に入る有彩画素(クラスタと同一条件)。
                    // 被覆率は「証拠のうち選択された画素」で recall の代理を測る。
                    if (nearState[p.cell] == 0) {
                        nearState[p.cell] = InAnyNearWindow(caches, p.h, p.s, p.v) ? (byte)1 : (byte)2;
                    }
                    if (nearState[p.cell] == 1) {
                        row.c++;
                    }
                });
                if (record != null) {
                    baseSel.tolerance = tol;
                    baseSel.sel = record;
                    baseSel.count = n.a;
                }
                selWith = n.a; selWithout = n.b; evCov = n.c;
            }

            Eval(derived, out int selW0, out int selWo0, out int evCov0);
            int forgivenOnly = selW0 - selWo0;
            if (selWo0 < VerifyMinBaseSelected) {
                return;                    // 基礎選択が小さすぎる
            }
            if (forgivenOnly <= selWo0 * BrightForgiveMaxFrac) {
                return;     // 免除は正常範囲
            }

            // 免除経由の巻き込みが過剰 → tolerance を段階的に下げ、証拠被覆率を守れる範囲で
            // 免除超過が閾値内に収まる最初の(=最大の) tolerance を採る。
            foreach (float frac in TolShrinkSteps) {
                float t = Mathf.Max(derived * frac, ForeignLowFloor);
                Eval(t, out int selW, out int selWo, out int evCov);
                if (evCov < evCov0 * EvidenceKeepFrac) {
                    break;              // これ以上はパーツを削る
                }
                if (selWo >= VerifyMinBaseSelected
                    && (selW - selWo) <= selWo * BrightForgiveMaxFrac) {
                    result.tolerance = t;
                    return;
                }
                if (t <= ForeignLowFloor) {
                    break;
                }
            }
        }

        // 明部同色相テール(塗りツヤ/スペキュラ)の取りこぼし救済:
        // スタイライズド塗りのツヤは「色相を僅かにずらし軽く脱彩した明部」(実測例: hd≈0.06,
        // |dS|≈0.22, |dV|≈0.35)として near 窓のすぐ外に分布するため、距離分布 P95 に一切現れず、
        // 導出 tolerance は原理的に届かない(明部距離免除を効かせても僅かに tolerance 超過で全滅する)。
        // そこで実マッチャーで tolerance を段階拡張し、「追加で選択される画素」が
        //   (1) 基礎選択に対して小面積(本物のツヤはパーツ面積の数%。明るい同色相の別素材=肌などは
        //       数十%に達するため面積比で分離できる)
        //   (2) 圧倒的に「サンプルより明るい・同色相・無彩寄りでない・トーン連結域の延長上」
        //       (ツヤのシグネチャ。隣接色ブリードは同色相でなく、白背景の巻き込みは相対彩度床
        //       sS*ChromaClusterSatFrac を割るため棄却。同色相・軽脱彩でもツヤと区別できない
        //       「V の空谷の向こうの明るい別素材」(暗い革地に対する明るいクリーム生地の色双子)は、
        //       V 連結領域ゲートの上端 vConnHiBin を僅かに超えるだけの画素に限る条件で棄却する。
        //       シェーディングが V 連続というトーン抽出と同じ仮定で、ツヤは連結域の明端に接するが
        //       別素材は谷の向こうに孤立する。副作用として V が段状に量子化されたベタ塗りパーツの
        //       明段は拡張対象から外れるが、これは色統計上「別パーツの明段」と同型で原理的に区別
        //       できないため、安全側(拡張しない)に倒す)
        // の両方を満たす最大ステップだけを受け入れる。シグネチャ不合格のステップは採用しない(打ち切りは
        // しない: 累積判定なので、より広いステップで追加集合全体が合格すればそれを採る)。面積超過は
        // 単調増加なので、そこで打ち切る。判定はすべてテクスチャ統計(面積比・サンプル相対の色信号)のみで、
        // 特定キャラ・色・座標には依存しない。
        // 発動しない条件(呼び出し側でスキップ): 無彩サンプル / foreign 打ち切り発火(隣接同色相パーツを
        // 検出済み) / 免除過剰で tolerance 縮小済み / hlRec 成長テストで別素材巻き込みを検出済み /
        // V 連結範囲が確定できなかった。
        private const float SheenGrowMaxAddedFrac = 0.05f;  // 追加画素/基礎選択(累積)の上限
        private const float SheenSigMinFrac = 0.90f;  // 追加画素中のツヤシグネチャ率の下限
        private const int SheenMinAddedCount = 24;     // これ未満の追加は証拠不足として採用しない
        private const int SheenVConnMarginBins = 2;      // 連結域上端から許す V 余白(連結歩行の橋渡し幅と同じ)
        private static readonly float[] SheenTolGrowSteps = { 1.15f, 1.3f, 1.5f };

        private static void VerifyBrightSheenRecall(Color32* pixels, int w, int h,
            ColorZone zone, bool* excluded, int maskW, int maskH, HsvGrid hsv, int vConnHiBin,
            BaseSelection known, ref TuneResult result) {
            float derived = result.tolerance;
            if (derived >= ChromaTolMax) {
                return; // 既に上限=伸ばす余地なし
            }

            var sim = BuildSimZone(zone, result);
            sim.highlightRecovery = false; // 基底マッチのみ測る(ハイライト系は成長テスト側の管轄)
            var matcher = new ColorZone.Matcher(sim);

            Color.RGBToHSV(zone.sampleColor, out float sH, out float sS, out float sV);
            float satSigFloor = sS * ChromaClusterSatFrac;
            // ツヤとして許す V 上限 = トーン連結域の上端 + 余白。連結域が明端まで届いている
            // パーツ(素直な連続シェーディング)では 1.0 超になり実質無制限。
            float sheenVMax = (vConnHiBin + 1 + SheenVConnMarginBins) / (float)AutoToneValueBins;

            // ── 基礎選択(導出 tolerance)のビットマスク ──
            // 先の検証が同じ tolerance で数えていればそれを使う(読むだけ)。
            bool reuse = known.sel != null && known.tolerance == derived;
            bool* baseSel = reuse ? known.sel : hsv.scratch.Bool(hsv.gw * hsv.gh, clear: true);
            int baseCount = reuse ? known.count : 0;
            if (!reuse) {
                baseCount = CountOnGrid(pixels, w, h, excluded, maskW, maskH, hsv, null, null, (in SimCell p, ref Tally row) => {
                    if (Selects(matcher, p, w, h)) {
                        baseSel[p.cell] = true;
                        row.a++;
                    }
                }).a;
            }
            if (baseCount < VerifyMinBaseSelected) {
                return;
            }

            // ── 段階拡張: 追加画素の面積とシグネチャを実マッチャーで判定 ──
            float accepted = derived;
            foreach (float mult in SheenTolGrowSteps) {
                float t = Mathf.Min(derived * mult, ChromaTolMax);
                if (t <= accepted) {
                    break; // 上限到達でこれ以上伸ばせない
                }
                sim.tolerance = t;
                var grown = new ColorZone.Matcher(sim);
                // 基礎選択の外だけを見る。a = 追加で選択される数、b = そのうちツヤのシグネチャに合う数
                Tally n = CountOnGrid(pixels, w, h, excluded, maskW, maskH, hsv, null, baseSel, (in SimCell p, ref Tally row) => {
                    if (!Selects(grown, p, w, h)) {
                        return;
                    }
                    row.a++;
                    float hd = Mathf.Abs(p.h - sH);
                    if (hd > 0.5f) {
                        hd = 1f - hd;
                    }
                    if (p.v > sV && p.v < sheenVMax
                        && hd < ColorZone.ForgivenessHueGate && p.s >= satSigFloor) {
                        row.b++;
                    }
                });
                int added = n.a, sig = n.b;
                if (added > baseCount * SheenGrowMaxAddedFrac) {
                    break; // 面積超過(単調)→打ち切り
                }
                if (added >= SheenMinAddedCount && sig >= added * SheenSigMinFrac) {
                    accepted = t; // このステップの累積追加集合はツヤとして合格
                }
                if (t >= ChromaTolMax) {
                    break; // 上限で評価済み=以降のステップは同値
                }
            }
            if (accepted > derived) {
                result.tolerance = accepted;
            }
        }

        // 証拠判定用: 主サンプルと内部サンプルの HSV を前計算する(near 窓の判定は InAnyNearWindow)。
        private static SampleHSV[] SampleWindows(ColorZone sim) {
            var extras = sim.extraSamples ?? new List<Color>();
            return BuildSampleHSVs(sim.sampleColor, extras);
        }

        private static bool InAnyNearWindow(SampleHSV[] samples, float pH, float pS, float pV) {
            for (int si = 0; si < samples.Length; si++) {
                var sm = samples[si];
                if (!InNearWindow(HueDistance(pH, sm.h), pS, pV, sm.s, sm.v)) {
                    continue;
                }
                if (pS < sm.s * ChromaClusterSatFrac) {
                    continue;
                }
                return true;
            }
            return false;
        }

        // 導出パラメータを適用したシミュレーション用ゾーンを作る（zone 本体は変更しない）。
        // highlightRecovery は常に ON にし、呼び出し側が strength / highlightPotential を
        // 分けて集計できるようにする。
        private static ColorZone BuildSimZone(ColorZone zone, in TuneResult result) {
            var sim = zone.Clone();
            sim.enabled = true;
            sim.mode = SelectionMode.ColorPick;
            sim.tolerance = result.tolerance;
            sim.saturationStrictness = result.saturationStrictness;
            sim.saturationGuard = result.saturationGuard;
            sim.chromaThreshold = result.chromaThreshold;
            sim.highlightRecovery = true;
            sim.edgeSoftness = result.edgeSoftness;
            sim.shadowForgivenessSatMin = result.shadowForgivenessSatMin;
            sim.shadowValueFloor = result.shadowValueFloor;
            sim.partSatCeiling = result.partSatCeiling;
            sim.partHueBand = result.partHueBand;
            sim.chromaCeiling = result.chromaCeiling;
            sim.holeFillMargin = result.holeFillMargin;
            sim.extraSamples = result.autoSamples ?? new List<Color>();
            return sim;
        }
    }
}
