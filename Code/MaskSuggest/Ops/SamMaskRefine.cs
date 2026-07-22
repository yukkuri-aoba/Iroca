// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using UnityEngine;

namespace Iroca
{
    /// <summary>
    /// AI マスク提案の境界色スナップ(純計算)。
    ///
    /// SAM のマスクは 256² の低解像度ロジット由来のため、元寸では 1 セル =
    /// texLong/256 px(4096² で 16px)の粒度しかなく、境界が階段状に ±半セル程度
    /// はみ出す。ここでは境界の不確実帯(幅=解像度から構造的に導出)に限り、
    /// 「帯の内側の確信領域 / 外側の確信領域」の局所平均色(RGBA)への近さで画素を
    /// 再分類し、境界を実テクスチャの色エッジへ吸着させる。
    /// 統計は対象テクスチャ自身から局所的に導出し、特定の色・座標・素材への
    /// 依存はない(デコンタミの局所ドナー統計と同じ思想)。
    /// </summary>
    internal static class SamMaskRefine
    {
        /// <summary>確信領域の平均色に必要な最小画素数(これ未満の側があれば再分類しない)。</summary>
        const int MinSamples = 16;

        /// <summary>
        /// 統計不足時に広げる近傍グリッド半径の上限(セル単位)。d はテクスチャ解像度に
        /// 比例するため、この上限もテクスチャサイズに応じて実 px 幅が自動的にスケールする。
        /// </summary>
        const int MaxWindowRadius = 8;

        // ─────────────────── AA 遷移帯のマスク包含(IncludeAaTransition) ───────────────────
        // SnapBoundary は境界を「内側/外側の等距離点」(混合率 ≈50%)に置き、多数決平滑が
        // 階段の角を ±1px 削る。除外(保護)マスクとしては、パーツ色が目に見えて混ざる画素が
        // 外側に取り残されると、そこだけ再着色されて点ノイズになる(実測: 実 SAM 提案で
        // 最外周 1px の混合率 50% 前後の画素が漏れ、ドット化)。ここでは境界外側の帯に限り、
        // 「局所外側平均色 → 局所内側平均色」の線分への射影で実混合(AA)画素を判定し、
        // 混合率が下限を超えるものをマスクへ含める。ExtendFringe の strand ゲートと異なり
        // 境界の向きに依存せず、統計は対象テクスチャ自身から局所導出する。

        /// <summary>マスクへ含める混合率(パーツ色比率)の下限。これ未満はほぼ背景で、
        /// 再着色されても変化が知覚しきい未満に留まる。</summary>
        const float AaBlendMin = 0.10f;

        /// <summary>実混合とみなす線分残差の上限(線分長に対する比の 2 乗)。
        /// これ超は別色(隣接する別パーツ等)であり、混色の前提が崩れるため含めない。</summary>
        const float AaResidFracSq = 0.35f * 0.35f;

        /// <summary>混合軸が定義できる最小コントラスト(RGBA 距離 2 乗)。
        /// 内外の平均色がこれより近い境界は視覚的に既に継ぎ目が無く、包含の益もない。</summary>
        const float AaMinContrastSq = 24f * 24f;

        /// <summary>
        /// 包含の反復上限。パーツ縁の soft skirt(ぼかし縁)が帯幅 d を超えて伸びる場合、
        /// 1 回の包含では途中までしか覆えないため、境界を進めながら固定点まで繰り返す
        /// (追加ゼロで早期終了)。skirt は実測で 2d 前後まで、3 回で十分に収束する。
        /// </summary>
        const int AaMaxPasses = 3;

        // ─────────────────── 房外郭への境界拡張(ExtendFringe) ───────────────────
        // SAM のマスクは房(細い frayed strands)を無視して滑らかに切る。房 strands は
        // render 対象(strand 間の gap は非表示)なので、「局所背景色から遠い outside 画素」を
        // 連結成長させて房を先端まで覆う。gate: strand-like(横に背景が隣接する細い構造)のみ育て、
        // solid な部品間境界・AA・文字は弾く。統計は対象テクスチャ自身から局所導出(色/座標非依存)。

        /// <summary>色距離のしきい(局所背景色からこの距離²を超えたら生地/strand とみなす)。</summary>
        const float FringeColorThresh = 26f;

        /// <summary>
        /// mask(下原点 w*h)の境界を、房 strands を覆うように実テクスチャの信号で外郭まで拡張する(in-place)。
        /// SnapBoundary の後段で呼ぶ。房が無い部位ではほとんど成長しない(precision 影響 &lt;=0.004)。
        /// </summary>
        public static void ExtendFringe(bool[] mask, Color32[] pixelsBottomUp, int w, int h)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h ||
                pixelsBottomUp.Length < w * h) return;

            int maxDim = Mathf.Max(w, h);
            // グリッド幅 d は SnapBoundary と同一(低解像度セルの 3/4)。reach/strand 幅は解像度比例
            // (4096² で d=12 / reach=50 / strandHalf=10)。
            int d = Mathf.Max(2, Mathf.CeilToInt(maxDim / (float)SamMaskPostprocess.LowRes * 0.75f));
            int reach = Mathf.Max(d, Mathf.RoundToInt(maxDim / 82f));
            int strandHalf = Mathf.Max(2, Mathf.RoundToInt(maxDim / 410f));
            float thr2 = FringeColorThresh * FringeColorThresh;

            // mask 外画素→最近 mask までの L1 距離(within/conf_out 判定に使う)
            var distOut = DistanceToOpposite(mask, w, h, inside: false);

            // 局所背景色 = mask 直外の確信領域(distOut>d)を粗グリッド集計 → 各画素 5x5 グリッド窓合算平均。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sum = new long[gw * gh * 3];
            var cnt = new int[gw * gh];
            for (int y = 0; y < h; y++)
            {
                int gRow = (y / d) * gw, row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] || distOut[i] <= d) continue; // 確信背景のみ
                    int g = gRow + x / d, o = g * 3;
                    var c = pixelsBottomUp[i];
                    sum[o] += c.r; sum[o + 1] += c.g; sum[o + 2] += c.b; cnt[g]++;
                }
            }

            // far[i]: 局所背景から色が遠い(生地/strand)。near は strand-like gate 用に横合算する。
            var far = new bool[w * h];
            var nearBg = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int gy = y / d, row = y * w;
                int gy0 = Mathf.Max(0, gy - 2), gy1 = Mathf.Min(gh - 1, gy + 2);
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    int gx = x / d;
                    int gx0 = Mathf.Max(0, gx - 2), gx1 = Mathf.Min(gw - 1, gx + 2);
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    for (int yy = gy0; yy <= gy1; yy++)
                    {
                        int gr = yy * gw;
                        for (int xx = gx0; xx <= gx1; xx++)
                        {
                            int g = gr + xx, o = g * 3;
                            sr += sum[o]; sg += sum[o + 1]; sb += sum[o + 2]; n += cnt[g];
                        }
                    }
                    if (n < MinSamples) { nearBg[i] = true; continue; } // bg 不明 → 育てない側に倒す
                    var c = pixelsBottomUp[i];
                    double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n;
                    double dr = c.r - mr, dg = c.g - mg, db = c.b - mb;
                    if (dr * dr + dg * dg + db * db > thr2) far[i] = true;
                    else nearBg[i] = true;
                }
            }

            // strand-like gate: 横 ±strandHalf 内に nearBg がある far 画素のみ育てる
            // (縦 strand は左右に背景 → 通す。solid 縁/文字は横に背景無し → 弾く)。
            var growZone = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] || !far[i] || distOut[i] > reach) continue;
                    int x0 = Mathf.Max(0, x - strandHalf), x1 = Mathf.Min(w - 1, x + strandHalf);
                    bool horizNear = false;
                    for (int xx = x0; xx <= x1; xx++)
                        if (nearBg[row + xx]) { horizNear = true; break; }
                    if (horizNear) growZone[i] = true;
                }
            }

            // grow_zone を mask 境界から 4 連結でフラッドして房を覆う(順序非依存の fixpoint)。
            var queue = new System.Collections.Generic.Queue<int>();
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (!growZone[i]) continue;
                    // 4 近傍に mask があれば種
                    if ((x > 0 && mask[i - 1]) || (x < w - 1 && mask[i + 1]) ||
                        (y > 0 && mask[i - w]) || (y < h - 1 && mask[i + w]))
                    {
                        mask[i] = true; growZone[i] = false; queue.Enqueue(i);
                    }
                }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(); int x = i % w, y = i / w;
                if (x > 0 && growZone[i - 1]) { mask[i - 1] = true; growZone[i - 1] = false; queue.Enqueue(i - 1); }
                if (x < w - 1 && growZone[i + 1]) { mask[i + 1] = true; growZone[i + 1] = false; queue.Enqueue(i + 1); }
                if (y > 0 && growZone[i - w]) { mask[i - w] = true; growZone[i - w] = false; queue.Enqueue(i - w); }
                if (y < h - 1 && growZone[i + w]) { mask[i + w] = true; growZone[i + w] = false; queue.Enqueue(i + w); }
            }
        }

        /// <summary>
        /// mask(下原点 w*h)の境界帯を pixels の色統計で再分類する(in-place)。
        /// </summary>
        public static void SnapBoundary(bool[] mask, Color32[] pixelsBottomUp, int w, int h)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h ||
                pixelsBottomUp.Length < w * h) return;

            // 不確実帯の半幅: SAM 低解像度セルの 3/4(±半セルの理論誤差+マージン)。
            int d = Mathf.Max(2, Mathf.CeilToInt(
                Mathf.Max(w, h) / (float)SamMaskPostprocess.LowRes * 0.75f));

            // L1 距離変換で「境界からの深さ」を測る(セパラブル2パス・O(N))
            var distIn = DistanceToOpposite(mask, w, h, inside: true);   // mask 内→外境界までの距離
            var distOut = DistanceToOpposite(mask, w, h, inside: false); // mask 外→内境界までの距離

            // 再分類は元マスクのスナップショットに対して行う(書き換え順序への依存を排除し、
            // NumPy リファレンスと決定的に一致させるため)。
            var mask0 = (bool[])mask.Clone();

            // 確信領域の局所色統計を粗グリッド(ストライド d)で集計。
            // 帯画素は近傍グリッド(半径 2d 相当)の合算平均と比較する。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sumIn = new long[gw * gh * 4];
            var sumOut = new long[gw * gh * 4];
            var cntIn = new int[gw * gh];
            var cntOut = new int[gw * gh];
            for (int y = 0; y < h; y++)
            {
                int gRow = (y / d) * gw;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    bool confIn = mask0[i] && distIn[i] > d;
                    bool confOut = !mask0[i] && distOut[i] > d;
                    if (!confIn && !confOut) continue;
                    int g = gRow + x / d;
                    var c = pixelsBottomUp[i];
                    if (confIn)
                    {
                        int o = g * 4;
                        sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; sumIn[o + 3] += c.a;
                        cntIn[g]++;
                    }
                    else
                    {
                        int o = g * 4;
                        sumOut[o] += c.r; sumOut[o + 1] += c.g; sumOut[o + 2] += c.b; sumOut[o + 3] += c.a;
                        cntOut[g]++;
                    }
                }
            }

            // 帯画素の再分類。元の mask を読みながら書き換えると統計自体は粗グリッド由来なので
            // 影響しない(確信領域は帯外で不変)。
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int gy = y / d;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) continue;

                    // 近傍半径2セル(5x5相当)から開始し、片側でも統計不足なら半径を広げて
                    // 再集計する。先細りウェッジ等、局所幅が帯より狭い形状では片側の確信領域が
                    // 直近に無いことがあるため(過去は即座に諦めて SAM の粗い判定を残していた)。
                    int gx = x / d;
                    long ir = 0, ig = 0, ib = 0, ia = 0, or_ = 0, og = 0, ob = 0, oa = 0;
                    int ic = 0, oc = 0;
                    for (int radius = 2; radius <= MaxWindowRadius; radius += 2)
                    {
                        ir = ig = ib = ia = or_ = og = ob = oa = 0; ic = 0; oc = 0;
                        int gy0 = Mathf.Max(0, gy - radius), gy1 = Mathf.Min(gh - 1, gy + radius);
                        int gx0 = Mathf.Max(0, gx - radius), gx1 = Mathf.Min(gw - 1, gx + radius);
                        for (int yy = gy0; yy <= gy1; yy++)
                        {
                            int gRow = yy * gw;
                            for (int xx = gx0; xx <= gx1; xx++)
                            {
                                int g = gRow + xx;
                                int o = g * 4;
                                if (cntIn[g] > 0)
                                {
                                    ir += sumIn[o]; ig += sumIn[o + 1]; ib += sumIn[o + 2]; ia += sumIn[o + 3];
                                    ic += cntIn[g];
                                }
                                if (cntOut[g] > 0)
                                {
                                    or_ += sumOut[o]; og += sumOut[o + 1]; ob += sumOut[o + 2]; oa += sumOut[o + 3];
                                    oc += cntOut[g];
                                }
                            }
                        }
                        if (ic >= MinSamples && oc >= MinSamples) break;
                    }
                    if (ic < MinSamples || oc < MinSamples) continue; // 統計不足 → SAM の判定を維持

                    var c = pixelsBottomUp[i];
                    double dIn = Dist2(c, ir, ig, ib, ia, ic);
                    double dOut = Dist2(c, or_, og, ob, oa, oc);
                    if (dIn == dOut) continue;
                    mask[i] = dIn < dOut;
                }
            }

            // AA 境界画素(地色と背景の中間色)は二値分類がどちらへ転ぶか不安定で
            // ±1px の点状ノイズになる。帯内のみ 3x3 多数決で平滑化して点滅を除去する
            // (帯外は不変なので形状は保たれる)。
            MajoritySmoothBand(mask, mask0, distIn, distOut, d, w, h);
        }

        /// <summary>
        /// mask(下原点 w*h)の境界外側の AA 遷移帯(内側色と外側色の実混合画素)をマスクへ
        /// 含める(in-place)。SnapBoundary → ExtendFringe の後段で呼ぶ最終仕上げ。
        /// 除外(保護)マスクの意味論では「パーツ色が目に見えて混ざる画素」を取り残すと
        /// そこだけ再着色されて点ノイズになるため、混合率 AaBlendMin 以上の実混合を包含する。
        /// </summary>
        public static void IncludeAaTransition(bool[] mask, Color32[] pixelsBottomUp, int w, int h)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h ||
                pixelsBottomUp.Length < w * h) return;

            int d = Mathf.Max(2, Mathf.CeilToInt(
                Mathf.Max(w, h) / (float)SamMaskPostprocess.LowRes * 0.75f));

            // soft skirt が帯幅を超える場合に境界を進めながら吸収する(追加ゼロで早期終了)。
            for (int pass = 0; pass < AaMaxPasses; pass++)
                if (IncludeAaTransitionPass(mask, pixelsBottomUp, w, h, d) == 0)
                    break;

            // フェーズ2: 境界 1px リングの局所ブレンド吸収。フェーズ1 の面統計は soft skirt
            // (ぼかし縁)が近傍にある素材で外側平均が skirt 色に汚染され、階段の角に残る
            // 遷移画素(隣接マスク色と背景の 1px 混合)の混合率を 0 と誤評価して取り残す
            // (実測: 各階段角に 1 画素、再着色で点ノイズ化)。ここでは面統計を使わず、
            // 画素自身の「隣接マスク画素 m ⇄ 反対側の画素 q」を両端とする局所線分で
            // p = α·m + (1-α)·q の実混合判定を行う(統計汚染と無縁・向き非依存)。
            for (int pass = 0; pass < AaMaxPasses; pass++)
                if (AbsorbEdgeBlendPass(mask, pixelsBottomUp, w, h) == 0)
                    break;
        }

        /// <summary>
        /// 境界に接する外側画素を、隣接マスク画素 m と反対側画素 q の局所線分で実混合判定して
        /// マスクへ吸収する 1 パス。追加した画素数を返す。読みはパス開始時のスナップショット、
        /// 書きは追加のみ(決定的・順序非依存)。別色の構造(輪郭線等)は残差ゲートで残る。
        /// </summary>
        static int AbsorbEdgeBlendPass(bool[] mask, Color32[] pixelsBottomUp, int w, int h)
        {
            var mask0 = (bool[])mask.Clone();
            // 8 方向(反対方向は符号反転で得る)
            int[] ex = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] ey = { 0, 0, 1, -1, 1, -1, 1, -1 };
            int added = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask0[i]) continue;
                    for (int k = 0; k < 8; k++)
                    {
                        int mx2 = x + ex[k], my2 = y + ey[k];
                        int qx = x - ex[k], qy = y - ey[k];
                        if (mx2 < 0 || mx2 >= w || my2 < 0 || my2 >= h) continue;
                        if (qx < 0 || qx >= w || qy < 0 || qy >= h) continue;
                        int mi = my2 * w + mx2, qi = qy * w + qx;
                        if (!mask0[mi] || mask0[qi]) continue;

                        var cm = pixelsBottomUp[mi];
                        var cq = pixelsBottomUp[qi];
                        var cp = pixelsBottomUp[i];
                        double dR = cm.r - (double)cq.r, dG = cm.g - (double)cq.g,
                               dB = cm.b - (double)cq.b, dA = cm.a - (double)cq.a;
                        double dirSq = dR * dR + dG * dG + dB * dB + dA * dA;
                        if (dirSq < AaMinContrastSq) continue; // 平坦(m≈q) → 混合が定義できない

                        double pR = cp.r - (double)cq.r, pG = cp.g - (double)cq.g,
                               pB = cp.b - (double)cq.b, pA = cp.a - (double)cq.a;
                        double t = (pR * dR + pG * dG + pB * dB + pA * dA) / dirSq;
                        if (t < AaBlendMin) continue;          // ほぼ q(背景側) → 吸収しない
                        double residSq = pR * pR + pG * pG + pB * pB + pA * pA - t * t * dirSq;
                        if (residSq > AaResidFracSq * dirSq) continue; // 別色 → 吸収しない
                        mask[i] = true;
                        added++;
                        break;
                    }
                }
            }
            return added;
        }

        /// <summary>IncludeAaTransition の 1 パス。追加した画素数を返す。</summary>
        static int IncludeAaTransitionPass(bool[] mask, Color32[] pixelsBottomUp, int w, int h, int d)
        {
            var distIn = DistanceToOpposite(mask, w, h, inside: true);
            var distOut = DistanceToOpposite(mask, w, h, inside: false);

            // 確信領域の局所色統計(SnapBoundary と同じ粗グリッド集計・現マスク基準)。
            // 外側統計は近傍(d 超)と遠方(2d 超)の 2 系統を持ち、判定はユニオン(どちらかの
            // 軸で実混合なら包含)にする。近傍軸だけだと、パーツ縁の soft skirt(ぼかし縁)が
            // 帯幅を超えて伸びる素材で外側平均が skirt 自身に汚染され、skirt 画素の混合率が
            // 0 に見えて取り残される(実測: 遠方軸のみへの置換は逆に隣接パーツ汚染で悪化。
            // 軸の原点=外側平均の近傍にある画素は t≈0 で弾かれる構造のため、ユニオンは
            // どちらかの軸が汚染されても誤包含になりにくい)。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sumIn = new long[gw * gh * 4];
            var sumOutNear = new long[gw * gh * 4];
            var sumOutFar = new long[gw * gh * 4];
            var cntIn = new int[gw * gh];
            var cntOutNear = new int[gw * gh];
            var cntOutFar = new int[gw * gh];
            int farDist = 2 * d;
            for (int y = 0; y < h; y++)
            {
                int gRow = (y / d) * gw;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    bool inConf = mask[i] && distIn[i] > d;
                    bool outNear = !mask[i] && distOut[i] > d;
                    if (!inConf && !outNear) continue;
                    int g = gRow + x / d;
                    var c = pixelsBottomUp[i];
                    int o = g * 4;
                    if (inConf)
                    {
                        sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; sumIn[o + 3] += c.a;
                        cntIn[g]++;
                    }
                    else
                    {
                        sumOutNear[o] += c.r; sumOutNear[o + 1] += c.g;
                        sumOutNear[o + 2] += c.b; sumOutNear[o + 3] += c.a;
                        cntOutNear[g]++;
                        if (distOut[i] > farDist)
                        {
                            sumOutFar[o] += c.r; sumOutFar[o + 1] += c.g;
                            sumOutFar[o + 2] += c.b; sumOutFar[o + 3] += c.a;
                            cntOutFar[g]++;
                        }
                    }
                }
            }

            // 判定はパス開始時のマスク由来の distOut に対して行い、書き込みは追加のみ
            // (決定的・順序非依存。統計は確信領域=帯外なので追加書き込みの影響を受けない)。
            int added = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int gy = y / d;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] || distOut[i] > d) continue; // 境界外側の帯のみ

                    int gx = x / d;
                    // 内側統計(共通)と、近傍/遠方の外側統計を半径段階拡大で収集
                    long ir = 0, ig = 0, ib = 0, ia = 0;
                    long nr = 0, ng = 0, nb = 0, na = 0, fr = 0, fg = 0, fb = 0, fa = 0;
                    int ic = 0, nc = 0, fc = 0;
                    for (int radius = 2; radius <= MaxWindowRadius; radius += 2)
                    {
                        ir = ig = ib = ia = 0; ic = 0;
                        nr = ng = nb = na = 0; nc = 0;
                        fr = fg = fb = fa = 0; fc = 0;
                        int gy0 = Mathf.Max(0, gy - radius), gy1 = Mathf.Min(gh - 1, gy + radius);
                        int gx0 = Mathf.Max(0, gx - radius), gx1 = Mathf.Min(gw - 1, gx + radius);
                        for (int yy = gy0; yy <= gy1; yy++)
                        {
                            int gRow = yy * gw;
                            for (int xx = gx0; xx <= gx1; xx++)
                            {
                                int g = gRow + xx;
                                int o = g * 4;
                                if (cntIn[g] > 0)
                                {
                                    ir += sumIn[o]; ig += sumIn[o + 1]; ib += sumIn[o + 2]; ia += sumIn[o + 3];
                                    ic += cntIn[g];
                                }
                                if (cntOutNear[g] > 0)
                                {
                                    nr += sumOutNear[o]; ng += sumOutNear[o + 1];
                                    nb += sumOutNear[o + 2]; na += sumOutNear[o + 3];
                                    nc += cntOutNear[g];
                                }
                                if (cntOutFar[g] > 0)
                                {
                                    fr += sumOutFar[o]; fg += sumOutFar[o + 1];
                                    fb += sumOutFar[o + 2]; fa += sumOutFar[o + 3];
                                    fc += cntOutFar[g];
                                }
                            }
                        }
                        if (ic >= MinSamples && nc >= MinSamples) break;
                    }
                    if (ic < MinSamples) continue; // 内側統計不足 → 触らない

                    var c = pixelsBottomUp[i];
                    double inR = ir / (double)ic, inG = ig / (double)ic,
                           inB = ib / (double)ic, inA = ia / (double)ic;
                    if (IsAaBlend(c, inR, inG, inB, inA, nr, ng, nb, na, nc) ||
                        IsAaBlend(c, inR, inG, inB, inA, fr, fg, fb, fa, fc))
                    {
                        mask[i] = true;
                        added++;
                    }
                }
            }
            return added;
        }

        /// <summary>
        /// 画素 c が「外側平均色 → 内側平均色」の線分上の実混合(混合率 AaBlendMin 以上)かを
        /// 判定する。外側統計が不足していれば false(その軸では判定しない)。
        /// </summary>
        static bool IsAaBlend(Color32 c, double inR, double inG, double inB, double inA,
                              long or_, long og, long ob, long oa, int oc)
        {
            if (oc < MinSamples) return false;
            double outR = or_ / (double)oc, outG = og / (double)oc,
                   outB = ob / (double)oc, outA = oa / (double)oc;
            double dR = inR - outR, dG = inG - outG, dB = inB - outB, dA = inA - outA;
            double dirSq = dR * dR + dG * dG + dB * dB + dA * dA;
            if (dirSq < AaMinContrastSq) return false; // 低コントラスト境界 → 混合軸が無意味

            double pR = c.r - outR, pG = c.g - outG, pB = c.b - outB, pA = c.a - outA;
            double t = (pR * dR + pG * dG + pB * dB + pA * dA) / dirSq;
            if (t < AaBlendMin) return false;          // ほぼ背景 → 含めない
            double residSq = pR * pR + pG * pG + pB * pB + pA * pA - t * t * dirSq;
            return residSq <= AaResidFracSq * dirSq;   // 線分から外れる別色は含めない
        }

        /// <summary>帯内画素を 3x3 多数決(5/9 以上)で平滑化する。読みはスナップ結果の
        /// スナップショット、書きは mask(決定的・順序非依存)。</summary>
        static void MajoritySmoothBand(bool[] mask, bool[] mask0, int[] distIn, int[] distOut,
                                       int d, int w, int h)
        {
            var snapped = (bool[])mask.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    bool inBand = mask0[i] ? distIn[i] <= d : distOut[i] <= d;
                    if (!inBand) continue;
                    int n = 0;
                    if (snapped[i - w - 1]) n++;
                    if (snapped[i - w]) n++;
                    if (snapped[i - w + 1]) n++;
                    if (snapped[i - 1]) n++;
                    if (snapped[i]) n++;
                    if (snapped[i + 1]) n++;
                    if (snapped[i + w - 1]) n++;
                    if (snapped[i + w]) n++;
                    if (snapped[i + w + 1]) n++;
                    mask[i] = n >= 5;
                }
            }
        }

        static double Dist2(Color32 c, long sr, long sg, long sb, long sa, int n)
        {
            double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n, ma = sa / (double)n;
            double dr = c.r - mr, dg = c.g - mg, db = c.b - mb, da = c.a - ma;
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>
        /// inside=true: mask 内の各画素について最も近い mask 外画素までの L1 距離(外は 0)。
        /// inside=false: 逆。セパラブル 2 パスの city-block 距離変換。
        /// </summary>
        internal static int[] DistanceToOpposite(bool[] mask, int w, int h, bool inside)
        {
            const int Inf = 1 << 28;
            var dist = new int[w * h];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = (mask[i] == inside) ? Inf : 0;

            // forward (左・下 = 配列順)
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x > 0 && dist[i - 1] + 1 < best) best = dist[i - 1] + 1;
                    if (y > 0 && dist[i - w] + 1 < best) best = dist[i - w] + 1;
                    dist[i] = best;
                }
            }
            // backward (右・上)
            for (int y = h - 1; y >= 0; y--)
            {
                int row = y * w;
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (dist[i] == 0) continue;
                    int best = dist[i];
                    if (x < w - 1 && dist[i + 1] + 1 < best) best = dist[i + 1] + 1;
                    if (y < h - 1 && dist[i + w] + 1 < best) best = dist[i + w] + 1;
                    dist[i] = best;
                }
            }
            return dist;
        }
    }
}
