// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
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
    ///
    /// 並列化について: 全画素ループは Parallel.For で行分割する(4K で数秒 → コア数分の一)。
    /// 出力の決定性は「各画素の書き込み先は自分のインデックスのみ・入力は読み取り専用・
    /// スレッド間の浮動小数集約なし」で保証する(集計はグリッド行単位に分割し、同一セルへの
    /// 書き込みを単一タスクに閉じる)。結果は逐次実行とビット同一。
    /// </summary>
    internal static unsafe class SamMaskRefine
    {
        /// <summary>確信領域の平均色に必要な最小画素数(これ未満の側があれば再分類しない)。</summary>
        const int MinSamples = 16;

        /// <summary>
        /// Ops 共通の並列設定。並列度は PixelProcessor.GetMaxParallelism() と同じ規則
        /// (直書きしない。既定のスレッド数と DebugCaptureHooks.ParallelismOverride は向こうで決める)。
        /// token を渡すと Parallel.For がパーティション境界でキャンセルを観測する
        /// (破棄確定の後処理が計算用のスレッドを占有し続け、次クリックの計算と奪い合うのを防ぐ)。
        /// </summary>
        internal static ParallelOptions MakeParallelOptions(System.Threading.CancellationToken token = default) => new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism(),
        };

        /// <summary>
        /// 単一グリッドセルを色モードとして扱う最小画素数。確信領域の色は窓プール平均だと
        /// 多峰背景(白ギャップ+隣接する別パーツ等)で実在しない中間色に潰れ、素の背景画素が
        /// 「背景から遠い」と誤判定される(実測: 隣接パーツ脇のギャップへ提案が成長)。
        /// 各段の判定はセル単位モードへの最近傍距離で行い、この汚染を避ける。
        /// </summary>
        const int MinCellSamples = 4;

        /// <summary>
        /// 粗グリッドのセル単位モード(平均色)。cnt が MinCellSamples 以上のセルだけ valid にし、
        /// mean[g*channels+k] = sum/cnt(double 除算)。SnapBoundary / ExtendFringe /
        /// IncludeAaTransition の各段が共有する(配列の確保と後段の無効化は呼び出し側)。
        /// </summary>
        static void ComputeCellModes(long[] sum, int[] cnt, int channels, double[] mean, bool[] valid)
        {
            for (int g = 0; g < cnt.Length; g++)
            {
                if (cnt[g] >= MinCellSamples)
                {
                    valid[g] = true;
                    int o = g * channels;
                    for (int k = 0; k < channels; k++)
                        mean[o + k] = sum[o + k] / (double)cnt[g];
                }
            }
        }

        /// <summary>
        /// 統計不足時に広げる近傍グリッド半径の上限(セル単位)。d はテクスチャ解像度に
        /// 比例するため、この上限もテクスチャサイズに応じて実 px 幅が自動的にスケールする。
        /// </summary>
        const int MaxWindowRadius = 8;

        /// <summary>
        /// 近傍グリッドの窓の最初の半径(セル単位、5×5 セル)。SnapBoundary と IncludeAaTransition はここから
        /// 2 セルずつ <see cref="MaxWindowRadius"/> まで広げる。
        /// </summary>
        const int MinWindowRadius = 2;

        /// <summary>
        /// ExtendFringe が背景・生地の色を集める窓の半径(セル単位、5×5 セル。広げない)。
        /// TryDeriveRefineCropRect の余白の根拠(読む範囲 reach + strandHalf + この 2 倍 + d)に入る。
        /// </summary>
        const int FringeWindowRadius = 2;

        /// <summary>
        /// 境界の不確実帯の半幅 d(= 粗グリッドのストライド): SAM 低解像度セルの 3/4
        /// (±半セルの理論誤差+マージン。4096² で 12)。SnapBoundary / ExtendFringe /
        /// IncludeAaTransition と TryDeriveAaCropRect が共有する。クロップ実行が全画像実行と
        /// ビット同一である根拠は両者の d の一致なので、式はここ 1 か所にだけ置く。
        /// </summary>
        internal static int BandHalfWidth(int w, int h) =>
            Mathf.Max(2, Mathf.CeilToInt(Mathf.Max(w, h) / (float)SamMaskPostprocess.LowRes * 0.75f));

        /// <summary>ExtendFringe が境界から房を育てる最大距離(解像度比例。4096² で 50)。</summary>
        static int FringeReach(int w, int h, int d) => Mathf.Max(d, Mathf.RoundToInt(Mathf.Max(w, h) / 82f));

        /// <summary>PeelThinSpill が読み書きする矩形の、マスクの外接矩形からの余白(足す探索の届く距離と地色の窓)。</summary>
        static int PeelRectMargin(int d) => GrowReachCells * d + (MaxWindowRadius + 1) * d + 1;

        /// <summary>
        /// 精密化 4 段(<see cref="SamMaskPostprocess.RefineInPlace(bool[], Color32*, int, int, System.Threading.CancellationToken)"/>)
        /// を、マスクの外接矩形に余白を足した矩形だけで回しても全画像で回したのとビット同一になる矩形を求める。
        /// 4 段は矩形の外の画素を読まず、書き換えもしない(外はすべてマスク外のまま)。
        /// empty はマスクに true が無いこと(4 段とも何も変えない)。矩形が画像の 3/4 を超えるときは false(切り出す得が小さい)。
        ///
        /// 余白の根拠(各段が書くのは直前のマスクの外接矩形から下の範囲の中で、読む範囲も合計の中に収まる):
        /// - SnapBoundary が書くのは帯(境界から d 以内)。読むのは帯の画素の窓(最大半径 MaxWindowRadius セル)まで。
        /// - ExtendFringe が足すのは境界から reach 以内。読むのは境界から reach + strandHalf の画素と、その ±FringeWindowRadius
        ///   セルの窓、窓のセルの生地類似の判定(さらに ±FringeWindowRadius セル)まで。
        /// - IncludeAaTransition は 1 パスで境界から d 以内を AaMaxPasses 回、続けて 1px を AaMaxPasses 回足す。
        ///   読むのは帯の画素の窓(最大半径 MaxWindowRadius セル)まで。
        /// - PeelThinSpill はマスクの外接矩形に PeelRectMargin を足した矩形の中だけを読み書きする。全画像でその矩形が
        ///   画像の端で切られないなら、切り出しでも端で切られないことが要る。
        /// 合計 = d(Snap)+ reach(Fringe)+ AaMaxPasses·(d + 1)(AA)+ PeelRectMargin(Peel)で、Peel の矩形が
        /// 必ず収まる。ほかの段の読む範囲(9d・reach + strandHalf + 5d・AA の 3d + 9d)はこれより狭い。
        /// 距離変換(L1)は、マスクが矩形の中にあり、矩形の端(画像の端でない所)がマスク外なら、矩形の中で全画像と同じ値になる。
        /// セルの位置(x/d)を全画像と揃えるため、矩形の始点は d の倍数、終点はセルの終わりか画像の端にする。
        /// 帯幅などの定数は全画像の寸法から決める(各段に sizeW・sizeH として渡す)。
        /// </summary>
        internal static bool TryDeriveRefineCropRect(bool[] mask, int w, int h, System.Threading.CancellationToken token,
            out bool empty, out int rx0, out int ry0, out int rw, out int rh)
        {
            rx0 = ry0 = rw = rh = 0;
            empty = false;
            if (mask == null || mask.Length != w * h) return false;
            var rowMin = new int[h];
            var rowMax = new int[h];
            Parallel.For(0, h, MakeParallelOptions(token), y =>
            {
                int row = y * w, mn = -1, mx = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!mask[row + x]) continue;
                    if (mn < 0) mn = x;
                    mx = x;
                }
                rowMin[y] = mn;
                rowMax[y] = mx;
            });
            int bx0 = w, by0 = h, bx1 = -1, by1 = -1;
            for (int y = 0; y < h; y++)
            {
                if (rowMin[y] < 0) continue;
                if (rowMin[y] < bx0) bx0 = rowMin[y];
                if (rowMax[y] > bx1) bx1 = rowMax[y];
                if (y < by0) by0 = y;
                by1 = y;
            }
            if (bx1 < 0) { empty = true; return false; }

            int d = BandHalfWidth(w, h);
            int pad = d + FringeReach(w, h, d) + AaMaxPasses * (d + 1) + PeelRectMargin(d);
            // 負の値の割り算は 0 の側へ切り捨てるが、そのときは画像の端(0)に寄せるので同じ。
            rx0 = Mathf.Max(0, (bx0 - pad) / d * d);
            ry0 = Mathf.Max(0, (by0 - pad) / d * d);
            int rx1 = Mathf.Min(w - 1, ((bx1 + pad) / d + 1) * d - 1);
            int ry1 = Mathf.Min(h - 1, ((by1 + pad) / d + 1) * d - 1);
            rw = rx1 - rx0 + 1;
            rh = ry1 - ry0 + 1;
            return (long)rw * rh * 4 <= (long)w * h * 3;
        }

        // SnapBoundary は境界を「内側/外側の等距離点」(混合率 ≈50%)に置き、多数決平滑が
        // 階段の角を ±1px 削る。除外(保護)マスクとしては、パーツ色が目に見えて混ざる画素が
        // 外側に取り残されると、そこだけ再着色されて点ノイズになる(実測: 実 SAM 提案で
        // 最外周 1px の混合率 50% 前後の画素が漏れ、ドット化)。ここでは境界外側の帯に限り、
        // 「局所外側モード色(画素に最近傍のセル平均) → 局所内側平均色」の線分への射影で
        // 実混合(AA)画素を判定し、混合率が下限を超えるものをマスクへ含める。
        // ExtendFringe の strand ゲートと異なり境界の向きに依存せず、統計は対象テクスチャ
        // 自身から局所導出する。

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

        // SAM のマスクは房(細い frayed strands)を無視して滑らかに切る。房 strands は
        // render 対象(strand 間の gap は非表示)なので、「局所背景色から遠い outside 画素」を
        // 連結成長させて房を先端まで覆う。gate: strand-like(横に背景が隣接する細い構造)のみ育て、
        // solid な部品間境界・AA・文字は弾く。統計は対象テクスチャ自身から局所導出(色/座標非依存)。

        /// <summary>色距離のしきい(局所背景色からこの距離²を超えたら生地/strand とみなす)。</summary>
        const float FringeColorThresh = 26f;

        // SAM の解像度(4096² で 1 セル 16px)より細い構造(ファスナーの歯・縫い目・細い線の描き込み)では、
        // 提案は「構造 + 周りの地色の滲み」になり、構造の端では途中で切れる。SnapBoundary の帯の再分類は
        // 内側の確信領域(境界から d より深い画素)の色を要るので、幅がおよそ 2d 未満の提案には効かず、
        // SAM の形がそのまま残る。保護(ここは塗らない)に使うと、構造の周りの地色が帯や塊になって塗られずに
        // 残り、切れた先の構造は塗られる。そこで「画素の大半が帯にある細い連結成分」に限り、地(陰影を含む)
        // を外し、近くで切れた描き込みの続きを足す(PeelThinSpill。精密化の最後の段)。

        /// <summary>成分を「細い」とみなす、帯(境界から d 以内)にある画素の割合の下限
        /// (帯状の成分なら幅 ≲ 4d。SAM の低解像度セル 3 つぶん)。</summary>
        const float ThinBandFracMin = 0.5f;

        /// <summary>地色と見分けが付かない色の差(RGBA 距離 2 乗)。AA 混合軸の下限と同じ「視覚的に継ぎ目が無い」線。
        /// PeelThinSpill の色の物差しはすべてこれから決める。</summary>
        const double PeelSameColorSq = AaMinContrastSq;

        /// <summary>地色とはっきり違う色(RGBA 距離 2 乗。見分けが付かない線の 2 倍の距離)。
        /// 剥がすのは「地の上にはっきり描かれた細部」の周りの滲みだけ、という門の物差し。</summary>
        const double PeelDistinctSq = 4.0 * PeelSameColorSq;

        /// <summary>
        /// 剥がす門: 地色とはっきり違う画素(細部)が、成分のこの割合以上あり、かつ剥がした後に残る画素の
        /// 半分以上を占めること。
        /// ・細部がほとんど無い成分は、地と同じ色の部位(同色の細いベルト等)を SAM が形で切り分けたもの
        ///   なので触らない(色で分けられないものを分けるのがマスクの役目)。
        /// ・残りの半分以上: 地と色の近い細い部位(暗い紺の帯・布のひだ)の一部を地と取り違えて削らない
        ///   (実測 2026-10-12: この門が無いと回帰の部位提案 3 件 — 紺のスカートの下端・ピンクのベストの
        ///   ひだ — で 0.1〜0.2% 欠けた)。
        /// 剥がす量そのものには上限を置かない: まばらな歯・破線の周りの滲みは成分の半分を超える
        /// (実測: 黒地の白い線の細い提案で 51%)。
        /// </summary>
        const float PeelMinDetailFrac = 0.25f;

        /// <summary>地が滑らかに続くとみなす隣同士の色の差(RGBA 距離 2 乗。見分けが付かない線の半分の距離)。
        /// 陰影(落ち影・布の明暗)は 1px あたり数階調で変わるので地として辿れ、描き込みの縁(1px で数十階調)
        /// で止まる(実測 2026-10-12: スライダーの下の落ち影は 1px あたり 2〜4 階調、歯の縁は 60 階調以上)。</summary>
        const double PeelStepSq = PeelSameColorSq / 4.0;

        /// <summary>最も近い地の画素と同じ色とみなす差(RGBA 距離 2 乗。滑らかに続く隣同士の差の 2 倍)。見分けが
        /// 付かない線(PeelSameColorSq)より締める: 比べる相手が実在の近くの地なので、許すのは地自身の粒状の色むら
        /// (生成りの布で隣同士の差は 99.9% が距離 11 以内)とわずかな陰影だけでよい。末端の歯は本体が周りの地より
        /// わずかに明るいだけ(実測: 本体 50・周りの地 35〜37、距離 22〜26)なので、見分けが付かない線では地に入る。</summary>
        const double PeelLocalSameSq = 2.0 * PeelStepSq;

        /// <summary>描き込みの続きを足す範囲(細い成分からの距離、d 単位)。SAM が細い構造を端で切る長さは
        /// 実測で低解像度セル 4 つ前後(ファスナーの末端で 66px = 5.5d)。</summary>
        const int GrowReachCells = 6;

        /// <summary>描き込みの続きとして連ねる隙間(px)。縁取り 1px と AA 1px を挟んだ隣の要素(歯)まで。</summary>
        const int GrowGap = 2;

        /// <summary>
        /// mask(下原点 w*h)の境界を、房 strands を覆うように実テクスチャの信号で外郭まで拡張する(in-place)。
        /// SnapBoundary の後段で呼ぶ。房が無い部位ではほとんど成長しない(precision 影響 &lt;=0.004)。
        /// </summary>
        /// <summary><see cref="ExtendFringe(bool[], Color32*, int, int, System.Threading.CancellationToken)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void ExtendFringe(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default)
        {
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) return;
            fixed (Color32* p = pixelsBottomUp) ExtendFringe(mask, p, w, h, token);
        }

        /// <param name="sizeW">帯幅などの定数を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す
        /// (<see cref="TryDeriveRefineCropRect"/>)。</param>
        public static void ExtendFringe(bool[] mask, Color32* pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default,
                                        int sizeW = 0, int sizeH = 0)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h) return;
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }

            int maxDim = Mathf.Max(sizeW, sizeH);
            // グリッド幅 d は SnapBoundary と同一(低解像度セルの 3/4)。reach/strand 幅は解像度比例
            // (4096² で d=12 / reach=50 / strandHalf=10)。
            int d = BandHalfWidth(sizeW, sizeH);
            int reach = FringeReach(sizeW, sizeH, d);
            int strandHalf = Mathf.Max(2, Mathf.RoundToInt(maxDim / 410f));
            float thr2 = FringeColorThresh * FringeColorThresh;

            // 画像全体の大きさの作業配列(距離 2 本・印 3 本、4K で計 112MB)は作業配列のプールから借りる
            // (毎クリック new すると Mono のヒープが膨らみ、縮まない)。抜けるときにまとめて返す。
            using var scratch = new WorkScratch();
            var distOut = DistanceToOpposite(scratch, mask, w, h, inside: false, token);
            var distIn = DistanceToOpposite(scratch, mask, w, h, inside: true, token);
            var po = MakeParallelOptions(token);

            // 局所背景色 = mask 直外の確信領域(distOut>d)を粗グリッド集計 → 各画素 5x5 グリッド窓合算平均。
            // 内側確信領域(distIn>d)も同時に集計する(背景モードの生地類似判定に使う)。
            // 並列化はグリッド行単位: 1 タスク = 1 グリッド行(画素行 d 本)で、同一セルへの
            // 書き込みがタスク間で共有されない(long 加算の順序も行内逐次のまま=決定的)。
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sum = new long[gw * gh * 3];
            var cnt = new int[gw * gh];
            var sumIn = new long[gw * gh * 3];
            var cntIn = new int[gw * gh];
            Parallel.For(0, gh, po, gy =>
            {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x;
                        bool confOut = !mask[i] && distOut[i] > d;
                        bool confIn = mask[i] && distIn[i] > d;
                        if (!confOut && !confIn) continue;
                        int g = gRow + x / d;
                        var c = pixelsBottomUp[i];
                        if (confOut)
                        {
                            int o = g * 3;
                            sum[o] += c.r; sum[o + 1] += c.g; sum[o + 2] += c.b; cnt[g]++;
                        }
                        else
                        {
                            int o = g * 3;
                            sumIn[o] += c.r; sumIn[o + 1] += c.g; sumIn[o + 2] += c.b; cntIn[g]++;
                        }
                    }
                }
            });

            // セル単位の背景モード(MinCellSamples 以上のセルのみ)。距離判定は最近傍モードで
            // 行う: 窓プール平均だと隣接する別パーツの色が混入した瞬間に平均が実在しない
            // 中間色へずれ、素の背景画素が「背景から遠い=生地」と誤判定されて提案が
            // ギャップへ成長する(多峰背景の平均は無意味)。
            var cellMean = new double[gw * gh * 3];
            var cellValid = new bool[gw * gh];
            var inMean = new double[gw * gh * 3];
            var inValid = new bool[gw * gh];
            ComputeCellModes(sum, cnt, 3, cellMean, cellValid);
            ComputeCellModes(sumIn, cntIn, 3, inMean, inValid);

            // 生地類似の外側モードを無効化: マスク直外の確信領域には SAM が切り落とした
            // 生地(房 strands)自体が含まれ得る。その色クラスタを背景モードとして信用すると
            // 房画素が「背景に近い」と誤判定されて成長が止まる。近傍 5x5 セル内のどれかの
            // 内側モードと同色(しきい以内)の外側モードは生地の可能性が高いため除外する
            // (プール平均時代は希釈で偶然無害だった汚染の、モード化に伴う明示対処)。
            for (int gy = 0; gy < gh; gy++)
            {
                int gy0 = Mathf.Max(0, gy - FringeWindowRadius), gy1 = Mathf.Min(gh - 1, gy + FringeWindowRadius);
                for (int gx = 0; gx < gw; gx++)
                {
                    int g = gy * gw + gx;
                    if (!cellValid[g]) continue;
                    int o = g * 3;
                    int gx0 = Mathf.Max(0, gx - FringeWindowRadius), gx1 = Mathf.Min(gw - 1, gx + FringeWindowRadius);
                    bool fabricLike = false;
                    for (int yy = gy0; yy <= gy1 && !fabricLike; yy++)
                    {
                        int gr = yy * gw;
                        for (int xx = gx0; xx <= gx1; xx++)
                        {
                            int g2 = gr + xx;
                            if (!inValid[g2]) continue;
                            int o2 = g2 * 3;
                            double dr = cellMean[o] - inMean[o2],
                                   dg = cellMean[o + 1] - inMean[o2 + 1],
                                   db = cellMean[o + 2] - inMean[o2 + 2];
                            if (dr * dr + dg * dg + db * db <= thr2) { fabricLike = true; break; }
                        }
                    }
                    if (fabricLike) cellValid[g] = false;
                }
            }

            // far[i]: どの局所背景モードからも色が遠い(生地/strand)。near は strand-like gate 用に横合算する。
            // 全画素 × 5x5 セル窓の走査で ExtendFringe 最大のホットループ(4K 実測 ~3s)。
            // 読み取り専用入力から far/nearBg の自画素のみへ書くため行並列で決定的。
            //
            // 境界帯限定(出力ビット同一): この 2 配列は後段の strand-like gate でしか読まれず、
            // 読まれる位置は
            //   far[i]    … `!mask[i] && distOut[i] <= reach` の候補画素のみ
            //   nearBg[j] … その候補と同じ行で横 ±strandHalf 内の画素のみ
            // に限られる。distIn/distOut は厳密な L1 距離変換なので画素間 L1 距離に対し
            // 1-Lipschitz であり、候補 i(非 mask, distOut<=reach)と L1 距離 strandHalf 以内の j は
            //   ・非 mask なら distOut[j] <= distOut[i] + strandHalf <= reach + strandHalf
            //   ・mask なら i が非 mask なので distIn[j] <= strandHalf
            // を必ず満たす。よって下の帯の外は「読まれない」ことが厳密に言え、false のまま
            // 残しても出力は変わらない。房は境界近傍にしか出ないので実効面積は典型 1 割未満。
            int fringeBand = reach + strandHalf;
            bool* far = scratch.Bool(w * h, clear: true);
            bool* nearBg = scratch.Bool(w * h, clear: true);
            Parallel.For(0, h, po, y =>
            {
                int gy = y / d, row = y * w;
                int gy0 = Mathf.Max(0, gy - FringeWindowRadius), gy1 = Mathf.Min(gh - 1, gy + FringeWindowRadius);
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] ? distIn[i] > strandHalf : distOut[i] > fringeBand) continue;
                    int gx = x / d;
                    int gx0 = Mathf.Max(0, gx - FringeWindowRadius), gx1 = Mathf.Min(gw - 1, gx + FringeWindowRadius);
                    var c = pixelsBottomUp[i];
                    long sr = 0, sg = 0, sb = 0; int n = 0;
                    double minD = double.MaxValue;
                    for (int yy = gy0; yy <= gy1; yy++)
                    {
                        int gr = yy * gw;
                        for (int xx = gx0; xx <= gx1; xx++)
                        {
                            int g = gr + xx, o = g * 3;
                            sr += sum[o]; sg += sum[o + 1]; sb += sum[o + 2]; n += cnt[g];
                            if (!cellValid[g]) continue;
                            double dr = c.r - cellMean[o], dg = c.g - cellMean[o + 1],
                                   db = c.b - cellMean[o + 2];
                            double dd = dr * dr + dg * dg + db * db;
                            if (dd < minD) minD = dd;
                        }
                    }
                    if (n < MinSamples) { nearBg[i] = true; continue; } // bg 不明 → 育てない側に倒す
                    double dist2;
                    if (minD != double.MaxValue) dist2 = minD;
                    else
                    {
                        // モードを成すセルが無い散在サンプル → 従来のプール平均で判定
                        double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n;
                        double dr = c.r - mr, dg = c.g - mg, db = c.b - mb;
                        dist2 = dr * dr + dg * dg + db * db;
                    }
                    if (dist2 > thr2) far[i] = true;
                    else nearBg[i] = true;
                }
            });

            // strand-like gate: 横 ±strandHalf 内に nearBg がある far 画素のみ育てる
            // (縦 strand は左右に背景 → 通す。solid 縁/文字は横に背景無し → 弾く)。
            // 同じループで「mask に 4 近傍が接する growZone 画素」を行ごとの種リストへ収集する
            // (mask はこのループでは読み取り専用=判定は決定的。従来は種のために全画素をもう
            //  1 パス逐次走査しており、そのパスを丸ごと削減する)。
            bool* growZone = scratch.Bool(w * h, clear: true);
            var rowSeeds = new System.Collections.Generic.List<int>[h];
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                System.Collections.Generic.List<int> seeds = null;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] || !far[i] || distOut[i] > reach) continue;
                    int x0 = Mathf.Max(0, x - strandHalf), x1 = Mathf.Min(w - 1, x + strandHalf);
                    bool horizNear = false;
                    for (int xx = x0; xx <= x1; xx++)
                        if (nearBg[row + xx]) { horizNear = true; break; }
                    if (!horizNear) continue;
                    growZone[i] = true;
                    if ((x > 0 && mask[i - 1]) || (x < w - 1 && mask[i + 1]) ||
                        (y > 0 && mask[i - w]) || (y < h - 1 && mask[i + w]))
                        (seeds ??= new System.Collections.Generic.List<int>()).Add(x);
                }
                rowSeeds[y] = seeds;
            });

            // grow_zone を mask 境界から 4 連結でフラッドして房を覆う(順序非依存の fixpoint)。
            // 種を「元 mask に隣接する growZone」に限っても、旧実装の種スキャン中に連鎖で
            // 種化していた画素は BFS で到達するため、最終集合はビット同一。
            // キュー要素は (y<<16)|x のパック座標(デキューごとの %w・/w を除去。Unity の
            // 最大テクスチャ 16384 でも 16bit に収まる)。
            var queue = new System.Collections.Generic.Queue<int>();
            for (int y = 0; y < h; y++)
            {
                var seeds = rowSeeds[y];
                if (seeds == null) continue;
                int row = y * w;
                for (int k = 0; k < seeds.Count; k++)
                {
                    int x = seeds[k];
                    int i = row + x;
                    mask[i] = true; growZone[i] = false; queue.Enqueue((y << 16) | x);
                }
            }
            int visited = 0;
            while (queue.Count > 0)
            {
                int packed = queue.Dequeue();
                int x = packed & 0xFFFF, y = packed >> 16;
                int i = y * w + x;
                if (x > 0 && growZone[i - 1]) { mask[i - 1] = true; growZone[i - 1] = false; queue.Enqueue((y << 16) | (x - 1)); }
                if (x < w - 1 && growZone[i + 1]) { mask[i + 1] = true; growZone[i + 1] = false; queue.Enqueue((y << 16) | (x + 1)); }
                if (y > 0 && growZone[i - w]) { mask[i - w] = true; growZone[i - w] = false; queue.Enqueue(((y - 1) << 16) | x); }
                if (y < h - 1 && growZone[i + w]) { mask[i + w] = true; growZone[i + w] = false; queue.Enqueue(((y + 1) << 16) | x); }
                if ((++visited & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// mask(下原点 w*h)の境界帯を pixels の色統計で再分類する(in-place)。
        /// </summary>
        /// <summary><see cref="SnapBoundary(bool[], Color32*, int, int, System.Threading.CancellationToken)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void SnapBoundary(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default)
        {
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) return;
            fixed (Color32* p = pixelsBottomUp) SnapBoundary(mask, p, w, h, token);
        }

        /// <param name="sizeW">帯幅を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す。</param>
        public static void SnapBoundary(bool[] mask, Color32* pixelsBottomUp, int w, int h,
                                        System.Threading.CancellationToken token = default,
                                        int sizeW = 0, int sizeH = 0)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h) return;
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }

            // 不確実帯の半幅: SAM 低解像度セルの 3/4(±半セルの理論誤差+マージン)。
            int d = BandHalfWidth(sizeW, sizeH);

            // 境界からの深さはセパラブルな L1 距離変換で求める(作業配列は ExtendFringe と同じくプールから)。
            using var scratch = new WorkScratch();
            var distIn = DistanceToOpposite(scratch, mask, w, h, inside: true, token);   // mask 内→外境界までの距離
            var distOut = DistanceToOpposite(scratch, mask, w, h, inside: false, token); // mask 外→内境界までの距離

            // 再分類は元マスクのスナップショットに対して行う(書き換え順序への依存を排除し、
            // NumPy リファレンスと決定的に一致させるため)。
            bool* mask0 = scratch.CopyOf(mask, w * h);

            // 確信領域の局所色統計を粗グリッド(ストライド d)で集計。
            // 帯画素は近傍グリッド(半径 2d 相当)の合算平均と比較する。
            // 並列化はグリッド行単位(1 タスク = 画素行 d 本)でセル書き込みを共有しない=決定的。
            var po = MakeParallelOptions(token);
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sumIn = new long[gw * gh * 4];
            var sumOut = new long[gw * gh * 4];
            var cntIn = new int[gw * gh];
            var cntOut = new int[gw * gh];
            Parallel.For(0, gh, po, gy =>
            {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++)
                {
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
            });

            // セル単位モード(MinCellSamples 以上のセル)。判定はモードへの最近傍距離で行う:
            // プール平均は多峰統計(白ギャップ+隣接する別パーツ、柄の複数色)で実在しない
            // 中間色に潰れ、素の背景画素が「外側平均より内側平均に近い」と誤判定される。
            var meanIn = new double[gw * gh * 4];
            var meanOut = new double[gw * gh * 4];
            var validIn = new bool[gw * gh];
            var validOut = new bool[gw * gh];
            ComputeCellModes(sumIn, cntIn, 4, meanIn, validIn);
            ComputeCellModes(sumOut, cntOut, 4, meanOut, validOut);

            // 帯画素の再分類。元の mask を読みながら書き換えると統計自体は粗グリッド由来なので
            // 影響しない(確信領域は帯外で不変)。判定入力は mask0/統計(読み取り専用)、
            // 書き込みは自画素のみなので行並列で決定的。
            Parallel.For(0, h, po, y =>
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
                    var c = pixelsBottomUp[i];
                    long ir = 0, ig = 0, ib = 0, ia = 0, or_ = 0, og = 0, ob = 0, oa = 0;
                    int ic = 0, oc = 0;
                    double minIn = double.MaxValue, minOut = double.MaxValue;
                    for (int radius = MinWindowRadius; radius <= MaxWindowRadius; radius += 2)
                    {
                        ir = ig = ib = ia = or_ = og = ob = oa = 0; ic = 0; oc = 0;
                        minIn = minOut = double.MaxValue;
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
                                    if (validIn[g])
                                    {
                                        double dd = Dist2Mean(c, meanIn, o);
                                        if (dd < minIn) minIn = dd;
                                    }
                                }
                                if (cntOut[g] > 0)
                                {
                                    or_ += sumOut[o]; og += sumOut[o + 1]; ob += sumOut[o + 2]; oa += sumOut[o + 3];
                                    oc += cntOut[g];
                                    if (validOut[g])
                                    {
                                        double dd = Dist2Mean(c, meanOut, o);
                                        if (dd < minOut) minOut = dd;
                                    }
                                }
                            }
                        }
                        if (ic >= MinSamples && oc >= MinSamples) break;
                    }
                    if (ic < MinSamples || oc < MinSamples) continue; // 統計不足 → SAM の判定を維持

                    double dIn = minIn != double.MaxValue ? minIn : Dist2(c, ir, ig, ib, ia, ic);
                    double dOut = minOut != double.MaxValue ? minOut : Dist2(c, or_, og, ob, oa, oc);
                    if (dIn == dOut) continue;
                    mask[i] = dIn < dOut;
                }
            });

            // AA 境界画素(地色と背景の中間色)は二値分類がどちらへ転ぶか不安定で
            // ±1px の点状ノイズになる。帯内のみ 3x3 多数決で平滑化して点滅を除去する
            // (帯外は不変なので形状は保たれる)。
            MajoritySmoothBand(scratch, mask, mask0, distIn, distOut, d, w, h, token);
        }

        /// <summary>
        /// mask(下原点 w*h)の細い連結成分(画素の ThinBandFracMin 以上が境界から d 以内)を、地の上の描き込み
        /// そのものへ合わせ直す(in-place)。精密化の最後(SnapBoundary → ExtendFringe → IncludeAaTransition の後)に
        /// 呼ぶ: ExtendFringe は地色より暗い落ち影を「地色から遠い外側」として、IncludeAaTransition は暗い縁取りと
        /// 落ち影の間を「混色」として足すので、その後で地を外す。マスク解像度で AA 遷移包含をやり直したときも、
        /// その後に呼ぶ(MaskSuggestController のコミット)。
        /// 地色(下の「地色」)は、この時点のマスクから d より離れた画素(確信外側)のセル単位モード。
        ///   1. 地: 細い成分から GrowReachCells × d 離れた外周から、隣同士の色の差が PeelStepSq 以下の画素を
        ///      辿った領域。陰影(スライダーの下の落ち影など)は滑らかに続くので地に入り、描き込みの縁(1px で大きく
        ///      色が変わる)で止まる。
        ///      外周から辿れなくても、最も近い地の画素と同じ色(PeelLocalSameSq 以下)の画素は地とする(描き込みに
        ///      囲まれた地の袋)。
        ///   2. 成分から地を外す。地の上の描き込みと言えない成分(PeelMinDetailFrac の門)は触らない。
        ///   3. 外した成分の近く(GrowReachCells × d 以内)で地に届かない画素(描き込み)のうち、成分から
        ///      GrowGap px 以内の隙間で連なるものを足す。SAM が構造の端で切った続き(ファスナーの末端の歯)と、
        ///      帯の再分類が近くの別の明るい線を外側の色と取り違えて落とした画素を拾う。
        /// 細さはスナップ後の形で見る(SAM の形が幅広の塊だと、元マスクの奥は「確信の内側」として再分類され
        /// ずに残るため)。連結成分は行の連(ラン)の union-find で求める(大きな提案でも画素ごとの探索をしない)。
        /// 細い成分が無ければ、外接矩形の距離変換 1 回だけで戻る。
        /// 地の塗りつぶしと足す探索は逐次だが、対象は細い成分の周りだけで画素数は小さい。
        /// </summary>
        /// <summary><see cref="PeelThinSpill(bool[], Color32*, int, int, System.Threading.CancellationToken)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void PeelThinSpill(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                         System.Threading.CancellationToken token = default)
        {
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) return;
            fixed (Color32* p = pixelsBottomUp) PeelThinSpill(mask, p, w, h, token);
        }

        /// <param name="sizeW">帯幅を決める画像の寸法(0 なら w・h)。切り出しで回すとき全画像の寸法を渡す。</param>
        public static void PeelThinSpill(bool[] mask, Color32* pixelsBottomUp, int w, int h,
                                         System.Threading.CancellationToken token = default,
                                         int sizeW = 0, int sizeH = 0)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h) return;
            if (sizeW <= 0 || sizeH <= 0) { sizeW = w; sizeH = h; }
            var px = pixelsBottomUp;
            var po = MakeParallelOptions(token);
            int d = BandHalfWidth(sizeW, sizeH);
            int reach = GrowReachCells * d;
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;

            // マスクの外接矩形を、足す探索の届く距離と地色の窓(最大半径のセル)の分だけ広げた矩形の中で扱う。
            var rowMin = new int[h];
            var rowMax = new int[h];
            Parallel.For(0, h, po, y =>
            {
                int row = y * w, mn = -1, mx = -1;
                for (int x = 0; x < w; x++)
                {
                    if (!mask[row + x]) continue;
                    if (mn < 0) mn = x;
                    mx = x;
                }
                rowMin[y] = mn;
                rowMax[y] = mx;
            });
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                if (rowMin[y] < 0) continue;
                if (rowMin[y] < x0) x0 = rowMin[y];
                if (rowMax[y] > x1) x1 = rowMax[y];
                if (y < y0) y0 = y;
                y1 = y;
            }
            if (x1 < 0) return;
            int margin = PeelRectMargin(d);
            x0 = Mathf.Max(0, x0 - margin); y0 = Mathf.Max(0, y0 - margin);
            x1 = Mathf.Min(w - 1, x1 + margin); y1 = Mathf.Min(h - 1, y1 + margin);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            int ox = x0, oy = y0;   // ラムダ用の写し
            // 外接矩形の大きさの作業配列(提案が大きいと画像全体に近い)はプールから借りる。
            using var scratch = new WorkScratch();

            // 行ごとの連(マスク画素の横並び。終端は含まない)。
            var runCount = new int[bh];
            Parallel.For(0, bh, po, ly =>
            {
                int row = (ly + oy) * w + ox, n = 0;
                bool prev = false;
                for (int lx = 0; lx < bw; lx++)
                {
                    bool m = mask[row + lx];
                    if (m && !prev) n++;
                    prev = m;
                }
                runCount[ly] = n;
            });
            var runFirst = new int[bh + 1];
            for (int ly = 0; ly < bh; ly++) runFirst[ly + 1] = runFirst[ly] + runCount[ly];
            int nRuns = runFirst[bh];
            var runX0 = new int[nRuns];
            var runX1 = new int[nRuns];
            Parallel.For(0, bh, po, ly =>
            {
                int row = (ly + oy) * w + ox, r = runFirst[ly];
                for (int lx = 0; lx < bw;)
                {
                    if (!mask[row + lx]) { lx++; continue; }
                    int s = lx;
                    while (lx < bw && mask[row + lx]) lx++;
                    runX0[r] = s;
                    runX1[r] = lx;
                    r++;
                }
            });

            // 上下の行で横の範囲が重なる連を結ぶ(= 縦の 4 近傍連結)。
            var parent = new int[nRuns];
            for (int r = 0; r < nRuns; r++) parent[r] = r;
            for (int ly = 1; ly < bh; ly++)
            {
                int a = runFirst[ly - 1], aEnd = runFirst[ly];
                int b = runFirst[ly], bEnd = runFirst[ly + 1];
                while (a < aEnd && b < bEnd)
                {
                    if (runX0[a] < runX1[b] && runX0[b] < runX1[a])
                    {
                        int ra = FindRoot(parent, a), rb = FindRoot(parent, b);
                        if (ra != rb) parent[System.Math.Max(ra, rb)] = System.Math.Min(ra, rb);
                    }
                    if (runX1[a] < runX1[b]) a++; else b++;
                }
            }
            var runComp = new int[nRuns];
            int nComp = 1;   // 0 = マスク外
            for (int r = 0; r < nRuns; r++)
            {
                int root = FindRoot(parent, r);
                runComp[r] = root == r ? nComp++ : runComp[root];
            }

            // 細さ: 境界から d 以内の画素の割合。矩形の外はすべてマスク外なので、距離は矩形の中だけで
            // 求めれば同じ。
            bool* local = scratch.Bool(bw * bh, clear: false);   // 下で全行を写す
            Parallel.For(0, bh, po, ly =>
                new System.ReadOnlySpan<bool>(mask, (ly + oy) * w + ox, bw).CopyTo(new System.Span<bool>(local + ly * bw, bw)));
            var distIn = DistanceToOpposite(scratch, local, bw, bh, inside: true, token);
            var runBand = new int[nRuns];
            Parallel.For(0, bh, po, ly =>
            {
                for (int r = runFirst[ly]; r < runFirst[ly + 1]; r++)
                {
                    int n = 0, row = ly * bw;
                    for (int lx = runX0[r]; lx < runX1[r]; lx++)
                        if (distIn[row + lx] <= d) n++;
                    runBand[r] = n;
                }
            });
            var area = new int[nComp];
            var band = new int[nComp];
            for (int r = 0; r < nRuns; r++)
            {
                area[runComp[r]] += runX1[r] - runX0[r];
                band[runComp[r]] += runBand[r];
            }
            var thin = new bool[nComp];
            bool anyThin = false;
            for (int c = 1; c < nComp; c++)
            {
                thin[c] = band[c] >= ThinBandFracMin * area[c];
                anyThin |= thin[c];
            }
            if (!anyThin) return;
            token.ThrowIfCancellationRequested();

            // 成分の番号を画素へ(矩形内の添字。0 = マスク外)。
            int* label = scratch.Int(bw * bh, clear: true);
            Parallel.For(0, bh, po, ly =>
            {
                int row = ly * bw;
                for (int r = runFirst[ly]; r < runFirst[ly + 1]; r++)
                {
                    int c = runComp[r];
                    for (int lx = runX0[r]; lx < runX1[r]; lx++) label[row + lx] = c;
                }
            });

            // 地色: マスクから d より離れた画素(確信外側)のセル単位モード(SnapBoundary と同じ粗グリッド・同じ
            // セルの位置)。矩形の外にマスク画素は無いので、マスクからの距離は矩形の中だけで求めれば同じ。
            var distOut = DistanceToOpposite(scratch, local, bw, bh, inside: false, token);
            var sumOut = new long[gw * gh * 4];
            var cntOut = new int[gw * gh];
            int gy0r = oy / d, gy1r = (oy + bh - 1) / d;
            Parallel.For(gy0r, gy1r + 1, po, gy =>
            {
                int yStart = Mathf.Max(oy, gy * d), yEnd = Mathf.Min(oy + bh, (gy + 1) * d);
                for (int y = yStart; y < yEnd; y++)
                {
                    int lrow = (y - oy) * bw;
                    for (int lx = 0; lx < bw; lx++)
                    {
                        if (distOut[lrow + lx] <= d) continue;
                        int x = lx + ox;
                        int g = gy * gw + x / d, o = g * 4;
                        var c = px[y * w + x];
                        sumOut[o] += c.r; sumOut[o + 1] += c.g; sumOut[o + 2] += c.b; sumOut[o + 3] += c.a;
                        cntOut[g]++;
                    }
                }
            });
            var meanOut = new double[gw * gh * 4];
            var validOut = new bool[gw * gh];
            ComputeCellModes(sumOut, cntOut, 4, meanOut, validOut);

            // 扱う範囲(near): 細い成分から reach 以内(太い成分の画素は除く)。
            Parallel.For(0, bh, po, ly =>
            {
                int row = ly * bw;
                for (int lx = 0; lx < bw; lx++) local[row + lx] = thin[label[row + lx]];
            });
            var distThin = DistanceToOpposite(scratch, local, bw, bh, inside: false, token);
            bool* near = scratch.Bool(bw * bh, clear: false);   // 下で全画素に書く
            Parallel.For(0, bh, po, ly =>
            {
                int row = ly * bw;
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = row + lx;
                    near[li] = distThin[li] <= reach && (label[li] == 0 || thin[label[li]]);
                }
            });

            // はっきり違う(描き込み): 細い成分の画素のうち、最大半径(MaxWindowRadius セル)の窓のすべての地色
            // (確信外側のセルモード)との距離² が PeelDistinctSq を超えるもの。窓を広く取るのは、地色が 2 種類ある所
            // — 余白の白と隣の部位の紺 — で、近くに白しか無い画素を「はっきり違う」と数えないため。
            bool* distinctPx = scratch.Bool(bw * bh, clear: true);
            Parallel.For(0, bh, po, ly =>
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = ly * bw + lx;
                    if (label[li] == 0 || !thin[label[li]]) continue;
                    int x = lx + ox, y = ly + oy;
                    var c = px[y * w + x];
                    int gx = x / d, gy = y / d;
                    double best = double.MaxValue;
                    int wy0 = Mathf.Max(0, gy - MaxWindowRadius), wy1 = Mathf.Min(gh - 1, gy + MaxWindowRadius);
                    int wx0 = Mathf.Max(0, gx - MaxWindowRadius), wx1 = Mathf.Min(gw - 1, gx + MaxWindowRadius);
                    for (int yy = wy0; yy <= wy1 && best > PeelDistinctSq; yy++)
                        for (int xx = wx0; xx <= wx1; xx++)
                        {
                            int g = yy * gw + xx;
                            if (!validOut[g]) continue;
                            double dd = Dist2Mean(c, meanOut, g * 4);
                            if (dd < best) best = dd;
                        }
                    distinctPx[li] = best != double.MaxValue && best > PeelDistinctSq;
                }
            });
            token.ThrowIfCancellationRequested();

            // 地: 範囲の外周(細い成分から reach 離れた画素 = 描き込みから十分離れた地)から、範囲の中を
            // 隣同士の色の差が PeelStepSq 以下の画素へ辿る(4 近傍)。地色の画素を起点にしたり、地色どうしの
            // 差を緩めたりはしない: 陰影の濃淡がある所では、描き込み(裾の暗い帯の中の灰色の歯)が別の場所の
            // 地色(帯の明るい縁)と同じ色になり、末端の歯は本体が布の地色とほぼ同じ明るさになる(実測: 本体 50・
            // 周りの地 35)。滑らかさだけで辿れば、縁で地と切れている描き込みは地に入らない。
            bool* ground = scratch.Bool(bw * bh, clear: true);
            int* queue = scratch.Int(bw * bh, clear: false);   // 積んだ所だけ読む
            int head = 0, tail = 0;
            for (int li = 0; li < bw * bh; li++)
            {
                if (!near[li] || distThin[li] < reach) continue;
                ground[li] = true;
                queue[tail++] = li;
            }
            while (head < tail)
            {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                var cq = px[(qy + oy) * w + qx + ox];
                for (int k = 0; k < 4; k++)
                {
                    int nx = qx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = qy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nx >= bw || ny < 0 || ny >= bh) continue;
                    int ni = ny * bw + nx;
                    if (ground[ni] || !near[ni]) continue;
                    if (Dist2Px(px[(ny + oy) * w + nx + ox], cq) > PeelStepSq) continue;
                    ground[ni] = true;
                    queue[tail++] = ni;
                }
            }
            token.ThrowIfCancellationRequested();

            // 地には、外周から辿れない「近くの地と同じ色」の画素も足す: 各画素に、外周から辿れた地のうち最も近い
            // (4 近傍の歩数で)画素の色を割り当て、それとの差が PeelLocalSameSq 以下なら地とする。
            // 描き込みに囲まれた地の袋(柄の帯の中の生成り)は外周から滑らかには辿れないが、すぐ外の地と同じ色
            // なので地になる。末端の歯は本体が布の地色に近くても、すぐ周りの地(裾の暗い帯)より明るいので残る。
            int* nearestGround = scratch.Int(bw * bh, clear: false);   // 下で全画素に書く
            for (int li = 0; li < bw * bh; li++) nearestGround[li] = ground[li] ? li : -1;
            head = 0; tail = 0;
            for (int li = 0; li < bw * bh; li++) if (ground[li]) queue[tail++] = li;
            while (head < tail)
            {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                for (int k = 0; k < 4; k++)
                {
                    int nx = qx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                    int ny = qy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (nx < 0 || nx >= bw || ny < 0 || ny >= bh) continue;
                    int ni = ny * bw + nx;
                    if (nearestGround[ni] >= 0 || !near[ni]) continue;
                    nearestGround[ni] = nearestGround[q];
                    queue[tail++] = ni;
                }
            }
            Parallel.For(0, bh, po, ly =>
            {
                for (int lx = 0; lx < bw; lx++)
                {
                    int li = ly * bw + lx, g = nearestGround[li];
                    if (ground[li] || g < 0) continue;
                    ground[li] = Dist2Px(px[(ly + oy) * w + lx + ox], px[(g / bw + oy) * w + g % bw + ox])
                                 <= PeelLocalSameSq;
                }
            });

            // 成分ごとの門(PeelMinDetailFrac の説明参照): 地の上の描き込みが成分の一定割合を占め、
            // 地を外した後の残りの半分以上がそれであること。
            var peeled = new int[nComp];
            var distinct = new int[nComp];
            for (int li = 0; li < bw * bh; li++)
            {
                int c = label[li];
                if (c == 0 || !thin[c]) continue;
                if (ground[li]) peeled[c]++;
                else if (distinctPx[li]) distinct[c]++;
            }
            var accept = new bool[nComp];
            bool anyAccept = false;
            int largest = 1;
            for (int c = 1; c < nComp; c++)
            {
                accept[c] = thin[c] && peeled[c] > 0 && distinct[c] >= PeelMinDetailFrac * area[c]
                            && distinct[c] >= 0.5f * (area[c] - peeled[c]);
                anyAccept |= accept[c];
                if (area[c] > area[largest]) largest = c;
            }
            if (!anyAccept) return;
            // 提案のいちばん大きな成分が地の上の描き込みなら、提案全体が描き込みの提案。ほかの細い成分(SAM が
            // 離れた所に飛ばした小さな塊)は門を通さずに地を外す: 飛び地は落ち影だけの欠片や、提案の外にある
            // 同じ描き込み(もう 1 本のファスナー)の一部で、後者は地色のモードに数えられて「はっきり違う」画素が
            // 0 になる(実測: 右のファスナーの提案が左の上端に飛ばした 412px の塊)。部位の提案(いちばん大きな
            // 成分が太い)には効かない。
            if (accept[largest])
                for (int c = 1; c < nComp; c++)
                    if (thin[c] && peeled[c] > 0) accept[c] = true;

            // 地を外し、残った描き込みから GrowGap px 以内の隙間で連なる「範囲の中の地でない画素」を足す。
            head = tail = 0;
            for (int li = 0; li < bw * bh; li++)
            {
                int c = label[li];
                if (c == 0 || !accept[c]) continue;
                if (ground[li]) { mask[(li / bw + oy) * w + li % bw + ox] = false; continue; }
                queue[tail++] = li;
            }
            bool* added = local;   // 細い成分の印はもう使わないので、足した画素の印に使い回す
            new System.Span<bool>(added, bw * bh).Clear();
            while (head < tail)
            {
                int q = queue[head++];
                int qx = q % bw, qy = q / bw;
                for (int dy = -GrowGap; dy <= GrowGap; dy++)
                {
                    int ny = qy + dy;
                    if (ny < 0 || ny >= bh) continue;
                    for (int dx = -GrowGap; dx <= GrowGap; dx++)
                    {
                        int nx = qx + dx;
                        if (nx < 0 || nx >= bw) continue;
                        int ni = ny * bw + nx;
                        if (label[ni] != 0 || added[ni] || !near[ni] || ground[ni]) continue;
                        if (!InStructure(ground, near, bw, bh, nx, ny)) continue;
                        added[ni] = true;
                        mask[(ny + oy) * w + nx + ox] = true;
                        queue[tail++] = ni;
                    }
                }
            }
        }

        /// <summary>
        /// (lx, ly) の 3x3 に、範囲の中の地でない画素が自分を含めて 3 つ以上あるか(描き込みの続きとして足す画素の条件)。
        /// 地の中の孤立した粒(生成りの布の粒状の色むら。隣と少し色が離れていて地として辿れない 1〜2 画素)を
        /// 描き込みとして連ねないため。歯のような塊や 1px の線は満たす(実測 2026-10-12: この条件が無いと
        /// 民族柄の細い提案が粒を伝って周りの生成りへ広がり、地の滲みが 10,780 → 24,105 に増えた)。
        /// </summary>
        static bool InStructure(bool* ground, bool* near, int bw, int bh, int lx, int ly)
        {
            int n = 0;
            for (int dy = -1; dy <= 1; dy++)
            {
                int y = ly + dy;
                if (y < 0 || y >= bh) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = lx + dx;
                    if (x < 0 || x >= bw) continue;
                    int i = y * bw + x;
                    if (near[i] && !ground[i] && ++n >= 3) return true;
                }
            }
            return false;
        }

        /// <summary>2 画素の RGBA 距離 2 乗。</summary>
        static double Dist2Px(Color32 a, Color32 b)
        {
            double dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b, da = a.a - b.a;
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>union-find の根(経路を半分に縮める)。</summary>
        static int FindRoot(int[] parent, int r)
        {
            while (parent[r] != r)
            {
                parent[r] = parent[parent[r]];
                r = parent[r];
            }
            return r;
        }

        /// <summary>
        /// mask(下原点 w*h)の境界外側の AA 遷移帯(内側色と外側色の実混合画素)をマスクへ
        /// 含める(in-place)。SnapBoundary → ExtendFringe の後段で呼ぶ最終仕上げ。
        /// 除外(保護)マスクの意味論では「パーツ色が目に見えて混ざる画素」を取り残すと
        /// そこだけ再着色されて点ノイズになるため、混合率 AaBlendMin 以上の実混合を包含する。
        /// </summary>
        /// <summary><see cref="IncludeAaTransition(bool[], Color32*, int, int, System.Threading.CancellationToken, int)"/> の配列版(画素が w*h に足りなければ何もしない)。</summary>
        public static void IncludeAaTransition(bool[] mask, Color32[] pixelsBottomUp, int w, int h,
                                               System.Threading.CancellationToken token = default,
                                               int dOverride = 0)
        {
            if (pixelsBottomUp == null || pixelsBottomUp.Length < w * h) return;
            fixed (Color32* p = pixelsBottomUp) IncludeAaTransition(mask, p, w, h, token, dOverride);
        }

        public static void IncludeAaTransition(bool[] mask, Color32* pixelsBottomUp, int w, int h,
                                               System.Threading.CancellationToken token = default,
                                               int dOverride = 0)
        {
            if (mask == null || pixelsBottomUp == null || mask.Length != w * h) return;

            // dOverride: クロップ実行(IncludeAaTransitionCropped)が全画像実行と同じ帯幅を使う
            // ための上書き。0 なら従来どおり自寸法から導出(既存呼び出しは全て 0 = 挙動不変)。
            int d = dOverride > 0 ? dOverride : BandHalfWidth(w, h);

            // soft skirt が帯幅を超える場合に境界を進めながら吸収する(追加ゼロで早期終了)。
            for (int pass = 0; pass < AaMaxPasses; pass++)
                if (IncludeAaTransitionPass(mask, pixelsBottomUp, w, h, d, token) == 0)
                    break;

            // フェーズ2: 境界 1px リングの局所ブレンド吸収。フェーズ1 の面統計は soft skirt
            // (ぼかし縁)が近傍にある素材で外側平均が skirt 色に汚染され、階段の角に残る
            // 遷移画素(隣接マスク色と背景の 1px 混合)の混合率を 0 と誤評価して取り残す
            // (実測: 各階段角に 1 画素、再着色で点ノイズ化)。ここでは面統計を使わず、
            // 画素自身の「隣接マスク画素 m ⇄ 反対側の画素 q」を両端とする局所線分で
            // p = α·m + (1-α)·q の実混合判定を行う(統計汚染と無縁・向き非依存)。
            for (int pass = 0; pass < AaMaxPasses; pass++)
                if (AbsorbEdgeBlendPass(mask, pixelsBottomUp, w, h, token) == 0)
                    break;
        }

        /// <summary>
        /// IncludeAaTransition をクロップで実行しても全画像実行とビット同一になる矩形
        /// (d 格子整列・マージン込み)を導出する。mask に true が無ければ false(AA 不要)。
        ///
        /// マージンの根拠(=クロップ実行が読むデータが全画像実行と一致する条件):
        /// - 各パスの追加は境界外側の帯(距離 ≤ d)のみ → AaMaxPasses パスで成長 ≤ AaMaxPasses·d、
        ///   フェーズ 2 のリング吸収は 1px × AaMaxPasses パス。
        /// - 帯画素が読むセル統計は半径 MaxWindowRadius セル = (MaxWindowRadius+1)d 以内。
        ///   クロップ端の欠けセルまで読まないよう +1 セル。
        /// - 距離変換は mask がクロップ内に完全に収まっていれば全画素で全画像実行と一致
        ///   (最近接 mask/非 mask 画素がクロップ内にあるため)。
        /// → 要る余白は AaMaxPasses·(d+1) + (MaxWindowRadius+2)·d(今の定数で 13d+3)。切りのよい 15d と
        ///   大きい方を使う(定数を変えたときに余白が足りなくならないよう、式からも求める)。
        /// rx0/ry0 は d の倍数に切り下げ、セル境界(x/d)を全画像実行と一致させる。
        /// </summary>
        public static bool TryDeriveAaCropRect(bool[] mask, int w, int h,
                                               out int rx0, out int ry0, out int rw, out int rh,
                                               out int d)
        {
            rx0 = ry0 = rw = rh = 0;
            d = BandHalfWidth(w, h);
            if (mask == null || mask.Length != w * h) return false;

            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (!mask[row + x]) continue;
                    if (x < bx0) bx0 = x;
                    if (x > bx1) bx1 = x;
                    if (y < by0) by0 = y;
                    by1 = y;
                }
            }
            if (bx1 < 0) return false; // true 画素なし → AA 包含は no-op

            int pad = Mathf.Max(15 * d, AaMaxPasses * (d + 1) + (MaxWindowRadius + 2) * d);
            rx0 = Mathf.Max(0, (bx0 - pad) / d * d);
            ry0 = Mathf.Max(0, (by0 - pad) / d * d);
            int rx1 = Mathf.Min(w - 1, ((bx1 + pad) / d + 1) * d - 1);
            int ry1 = Mathf.Min(h - 1, ((by1 + pad) / d + 1) * d - 1);
            rw = rx1 - rx0 + 1;
            rh = ry1 - ry0 + 1;
            return true;
        }

        /// <summary>
        /// fullMask のうち TryDeriveAaCropRect で導出した矩形だけを切り出して
        /// IncludeAaTransition(d は全画像値で上書き)を実行し、追加画素を fullMask へ書き戻す。
        /// cropPixels は同矩形の下原点画素列(呼び出し側が全体画素から行コピーで切り出す。GetPixels(rect) は使わない)。
        /// 出力は全画像実行とビット同一(根拠は TryDeriveAaCropRect のコメント)。
        /// </summary>
        public static void IncludeAaTransitionCropped(bool[] fullMask, int w, int h,
                                                      Color32[] cropPixels,
                                                      int rx0, int ry0, int rw, int rh, int d,
                                                      System.Threading.CancellationToken token = default)
        {
            if (fullMask == null || cropPixels == null || fullMask.Length != w * h ||
                cropPixels.Length < rw * rh || rw <= 0 || rh <= 0) return;

            var cropMask = new bool[rw * rh];
            for (int cy = 0; cy < rh; cy++)
                System.Array.Copy(fullMask, (ry0 + cy) * w + rx0, cropMask, cy * rw, rw);

            IncludeAaTransition(cropMask, cropPixels, rw, rh, token, dOverride: d);

            // IncludeAaTransition は追加のみ(true→false は起きない)なので行コピーで書き戻せる。
            for (int cy = 0; cy < rh; cy++)
                System.Array.Copy(cropMask, cy * rw, fullMask, (ry0 + cy) * w + rx0, rw);
        }

        /// <summary>
        /// 境界に接する外側画素を、隣接マスク画素 m と反対側画素 q の局所線分で実混合判定して
        /// マスクへ吸収する 1 パス。追加した画素数を返す。読みはパス開始時のスナップショット、
        /// 書きは追加のみ(決定的・順序非依存)。別色の構造(輪郭線等)は残差ゲートで残る。
        /// </summary>
        static int AbsorbEdgeBlendPass(bool[] mask, Color32* pixelsBottomUp, int w, int h,
                                       System.Threading.CancellationToken token = default)
        {
            using var scratch = new WorkScratch();
            bool* mask0 = scratch.CopyOf(mask, w * h);
            int[] ex = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] ey = { 0, 0, 1, -1, 1, -1, 1, -1 };
            // 読みはスナップショット・書きは自画素のみなので行並列で決定的。
            // added は行ローカルに数えて最後に合算する(値も逐次実行と一致)。
            int added = 0;
            Parallel.For(0, h, MakeParallelOptions(token),
                () => 0,
                (y, _, local) =>
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
                            local++;
                            break;
                        }
                    }
                    return local;
                },
                local => Interlocked.Add(ref added, local));
            return added;
        }

        /// <summary>IncludeAaTransition の 1 パス。追加した画素数を返す。</summary>
        static int IncludeAaTransitionPass(bool[] mask, Color32* pixelsBottomUp, int w, int h, int d,
                                           System.Threading.CancellationToken token = default)
        {
            using var scratch = new WorkScratch();
            var distIn = DistanceToOpposite(scratch, mask, w, h, inside: true, token);
            var distOut = DistanceToOpposite(scratch, mask, w, h, inside: false, token);

            // 確信領域の局所色統計(SnapBoundary と同じ粗グリッド集計・現マスク基準)。
            // 外側統計は近傍(d 超)と遠方(2d 超)の 2 系統を持ち、判定はユニオン(どちらかの
            // 軸で実混合なら包含)にする。近傍軸だけだと、パーツ縁の soft skirt(ぼかし縁)が
            // 帯幅を超えて伸びる素材で外側平均が skirt 自身に汚染され、skirt 画素の混合率が
            // 0 に見えて取り残される(実測: 遠方軸のみへの置換は逆に隣接パーツ汚染で悪化。
            // 軸の原点=外側平均の近傍にある画素は t≈0 で弾かれる構造のため、ユニオンは
            // どちらかの軸が汚染されても誤包含になりにくい)。
            // 並列化はグリッド行単位(1 タスク = 画素行 d 本)でセル書き込みを共有しない=決定的。
            var po = MakeParallelOptions(token);
            int gw = (w + d - 1) / d, gh = (h + d - 1) / d;
            var sumIn = new long[gw * gh * 4];
            var sumOutNear = new long[gw * gh * 4];
            var sumOutFar = new long[gw * gh * 4];
            var cntIn = new int[gw * gh];
            var cntOutNear = new int[gw * gh];
            var cntOutFar = new int[gw * gh];
            int farDist = 2 * d;
            Parallel.For(0, gh, po, gy =>
            {
                int gRow = gy * gw;
                int yEnd = System.Math.Min(h, (gy + 1) * d);
                for (int y = gy * d; y < yEnd; y++)
                {
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
            });

            // 外側セルモード(MinCellSamples 以上のセル)。混合軸の原点は「画素色に最も近い
            // 外側モード」を使う: プール平均だと隣接する別パーツの色が混入した窓で原点が
            // 実在しない中間色(例: 暗色+白の灰)へずれ、素の背景画素が「原点→内側」軸上の
            // 実混合に見えて層状に吸収され続ける(実測: ギャップ突出の最大増幅段だった)。
            // 最近傍モードなら素の背景画素は原点そのもの(t≈0)で弾かれる。
            var meanNear = new double[gw * gh * 4];
            var meanFar = new double[gw * gh * 4];
            var validNear = new bool[gw * gh];
            var validFar = new bool[gw * gh];
            ComputeCellModes(sumOutNear, cntOutNear, 4, meanNear, validNear);
            ComputeCellModes(sumOutFar, cntOutFar, 4, meanFar, validFar);

            // 判定はパス開始時のマスク由来の distOut に対して行い、書き込みは追加のみ
            // (決定的・順序非依存。統計は確信領域=帯外なので追加書き込みの影響を受けない)。
            // 行並列でも決定的: 各画素の mask 読みは自インデックスのみで、そこへ書くのは
            // 自イテレーションだけ(近傍の mask は読まない)。added は行ローカル合算。
            int added = 0;
            Parallel.For(0, h, po,
                () => 0,
                (y, _, local) =>
                {
                int row = y * w;
                int gy = y / d;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] || distOut[i] > d) continue; // 境界外側の帯のみ

                    int gx = x / d;
                    var c = pixelsBottomUp[i];
                    long ir = 0, ig = 0, ib = 0, ia = 0;
                    long nr = 0, ng = 0, nb = 0, na = 0, fr = 0, fg = 0, fb = 0, fa = 0;
                    int ic = 0, nc = 0, fc = 0;
                    double bestNearD = double.MaxValue, bestFarD = double.MaxValue;
                    int bestNearO = -1, bestFarO = -1;
                    for (int radius = MinWindowRadius; radius <= MaxWindowRadius; radius += 2)
                    {
                        ir = ig = ib = ia = 0; ic = 0;
                        nr = ng = nb = na = 0; nc = 0;
                        fr = fg = fb = fa = 0; fc = 0;
                        bestNearD = bestFarD = double.MaxValue;
                        bestNearO = bestFarO = -1;
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
                                    if (validNear[g])
                                    {
                                        double dd = Dist2Mean(c, meanNear, o);
                                        if (dd < bestNearD) { bestNearD = dd; bestNearO = o; }
                                    }
                                }
                                if (cntOutFar[g] > 0)
                                {
                                    fr += sumOutFar[o]; fg += sumOutFar[o + 1];
                                    fb += sumOutFar[o + 2]; fa += sumOutFar[o + 3];
                                    fc += cntOutFar[g];
                                    if (validFar[g])
                                    {
                                        double dd = Dist2Mean(c, meanFar, o);
                                        if (dd < bestFarD) { bestFarD = dd; bestFarO = o; }
                                    }
                                }
                            }
                        }
                        if (ic >= MinSamples && nc >= MinSamples) break;
                    }
                    if (ic < MinSamples) continue; // 内側統計不足 → 触らない

                    double inR = ir / (double)ic, inG = ig / (double)ic,
                           inB = ib / (double)ic, inA = ia / (double)ic;
                    bool include = false;
                    if (nc >= MinSamples)
                    {
                        include = bestNearO >= 0
                            ? IsAaBlend(c, inR, inG, inB, inA, meanNear[bestNearO],
                                        meanNear[bestNearO + 1], meanNear[bestNearO + 2],
                                        meanNear[bestNearO + 3])
                            : IsAaBlend(c, inR, inG, inB, inA, nr / (double)nc,
                                        ng / (double)nc, nb / (double)nc, na / (double)nc);
                    }
                    if (!include && fc >= MinSamples)
                    {
                        include = bestFarO >= 0
                            ? IsAaBlend(c, inR, inG, inB, inA, meanFar[bestFarO],
                                        meanFar[bestFarO + 1], meanFar[bestFarO + 2],
                                        meanFar[bestFarO + 3])
                            : IsAaBlend(c, inR, inG, inB, inA, fr / (double)fc,
                                        fg / (double)fc, fb / (double)fc, fa / (double)fc);
                    }
                    if (include)
                    {
                        mask[i] = true;
                        local++;
                    }
                }
                return local;
                },
                local => Interlocked.Add(ref added, local));
            return added;
        }

        /// <summary>
        /// 画素 c が「外側原点色 → 内側平均色」の線分上の実混合(混合率 AaBlendMin 以上)かを
        /// 判定する。原点は呼び出し側が選ぶ(最近傍外側モード、無ければプール平均)。
        /// </summary>
        static bool IsAaBlend(Color32 c, double inR, double inG, double inB, double inA,
                              double outR, double outG, double outB, double outA)
        {
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
        static void MajoritySmoothBand(WorkScratch scratch, bool[] mask, bool* mask0, int* distIn, int* distOut,
                                       int d, int w, int h,
                                       System.Threading.CancellationToken token = default)
        {
            bool* snapped = scratch.CopyOf(mask, w * h);
            Parallel.For(1, h - 1, MakeParallelOptions(token), y =>
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
            });
        }

        /// <summary>
        /// 提案マスクを別解像度のキャンバスへ「被覆保存」で転写する(フットプリント内に
        /// 1 画素でも提案があれば true)。最近傍サンプリングだと縮小時(実ファイル解像度で
        /// 仕上げた提案 → インポート解像度のマスクキャンバス)に境界の被覆が 1 セル単位で
        /// 欠け、取り残し画素が再着色されて点ノイズになる。保護マスクは「少しでも掛かる
        /// セルは保護」が安全側。拡大方向(ブロック=1px)では最近傍と同値。
        /// 座標系は行方向が一貫していれば上/下原点いずれでも正しい。
        /// </summary>
        public static bool[] TransferCoverage(bool[] src, int sw, int sh, int dw, int dh)
        {
            if (src == null || sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0 ||
                src.Length != sw * sh) return null;
            var dst = new bool[dw * dh];
            // 各出力画素は自分のフットプリントだけを読む(行並列で決定的)。
            // コミット経路のメインスレッドで呼ばれるため、ここの短縮は UI の引っかかりに直接効く。
            Parallel.For(0, dh, MakeParallelOptions(), my =>
            {
                int sy0 = (int)((long)my * sh / dh);
                int sy1 = (int)((long)(my + 1) * sh / dh);
                if (sy1 <= sy0) sy1 = Mathf.Min(sh, sy0 + 1);
                int dRow = my * dw;
                for (int mx = 0; mx < dw; mx++)
                {
                    int sx0 = (int)((long)mx * sw / dw);
                    int sx1 = (int)((long)(mx + 1) * sw / dw);
                    if (sx1 <= sx0) sx1 = Mathf.Min(sw, sx0 + 1);
                    bool any = false;
                    for (int sy = sy0; sy < sy1 && !any; sy++)
                    {
                        int sRow = sy * sw;
                        for (int sx = sx0; sx < sx1; sx++)
                            if (src[sRow + sx]) { any = true; break; }
                    }
                    dst[dRow + mx] = any;
                }
            });
            return dst;
        }

        static double Dist2(Color32 c, long sr, long sg, long sb, long sa, int n)
        {
            double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n, ma = sa / (double)n;
            double dr = c.r - mr, dg = c.g - mg, db = c.b - mb, da = c.a - ma;
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>事前計算済みセル平均(RGBA, オフセット o)との距離 2 乗。</summary>
        static double Dist2Mean(Color32 c, double[] mean, int o)
        {
            double dr = c.r - mean[o], dg = c.g - mean[o + 1],
                   db = c.b - mean[o + 2], da = c.a - mean[o + 3];
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>
        /// inside=true: mask 内の各画素について最も近い mask 外画素までの L1 距離(外は 0)。
        /// inside=false: 逆。分離型 city-block 距離変換:
        ///   行パス(行内 1D 距離)→ 列パス(min over y' of rowDist + |y-y'| を前進/後退走査で合成)。
        /// 旧 2 パスチャンファー(左下前進/右上後退)と厳密に同値(どちらも正確な L1)だが、
        /// 行パスは行単位・列パスは列レンジ単位で並列化できる(いずれも決定的)。
        /// </summary>
        internal static int* DistanceToOpposite(WorkScratch scratch, bool[] mask, int w, int h, bool inside,
                                                System.Threading.CancellationToken token = default)
        {
            fixed (bool* m = mask) return DistanceToOpposite(scratch, m, w, h, inside, token);
        }

        /// <summary>上と同じ(mask が作業配列のとき)。結果は scratch から借りる(全画素に書く)。</summary>
        internal static int* DistanceToOpposite(WorkScratch scratch, bool* mask, int w, int h, bool inside,
                                                System.Threading.CancellationToken token = default)
        {
            const int Inf = 1 << 28;
            int* dist = scratch.Int(w * h, clear: false);
            var po = MakeParallelOptions(token);

            // 行パス: 各行の左右走査で「同じ行内の最近接反対側画素」までの距離(無ければ Inf)
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                int run = Inf;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (mask[i] == inside)
                    {
                        if (run < Inf) run++;
                        dist[i] = run;
                    }
                    else
                    {
                        run = 0;
                        dist[i] = 0;
                    }
                }
                run = Inf;
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = row + x;
                    if (dist[i] == 0) { run = 0; continue; }
                    if (run < Inf) run++;
                    if (run < dist[i]) dist[i] = run;
                }
            });

            // 列パス: x を並列度ぶんの連続レンジに分け、各レンジで y 前進/後退走査
            // (行メジャー配列で連続アクセスになり、列単位分割よりキャッシュ効率が良い)
            int dop = System.Math.Max(1, po.MaxDegreeOfParallelism);
            int chunk = (w + dop - 1) / dop;
            Parallel.For(0, dop, po, p =>
            {
                int x0 = p * chunk, x1 = System.Math.Min(w, x0 + chunk);
                if (x0 >= x1) return;
                for (int y = 1; y < h; y++)
                {
                    int row = y * w, prev = row - w;
                    for (int x = x0; x < x1; x++)
                    {
                        int v = dist[prev + x] + 1;
                        if (v < dist[row + x]) dist[row + x] = v;
                    }
                }
                for (int y = h - 2; y >= 0; y--)
                {
                    int row = y * w, next = row + w;
                    for (int x = x0; x < x1; x++)
                    {
                        int v = dist[next + x] + 1;
                        if (v < dist[row + x]) dist[row + x] = v;
                    }
                }
            });
            return dist;
        }
    }
}
