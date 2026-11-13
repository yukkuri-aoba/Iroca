// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// AI マスク提案の精密化(純計算)。SAM のマスクは 256² の低解像度ロジット由来のため、元寸では 1 セル =
    /// texLong/256 px(4096² で 16px)の粒度しかなく、境界が階段状に ±半セル程度はみ出す。それを実テクスチャの色で
    /// 仕上げる 4 つの段と、解像度の転写を持つ(呼ぶ順は SamMaskPostprocess.RefineInPlace)。段ごとにファイルを分けてある:
    ///   1. SnapBoundary(SamMaskRefine.Snap.cs): 境界の不確実帯(幅は解像度から構造的に導く)に限り、帯の内側・外側の
    ///      確信領域の局所平均色(RGBA)への近さで画素を再分類し、境界を色エッジへ吸着させる。
    ///   2. ExtendFringe(SamMaskRefine.Fringe.cs): 房(毛先など)の外郭まで広げる。
    ///   3. IncludeAaTransition(SamMaskRefine.AaTransition.cs): 境界の外側の AA 遷移帯(実混合の画素)を含める。切り出し版あり。
    ///   4. PeelThinSpill(SamMaskRefine.Peel.cs): 細い描き込みの提案から、巻き込んだ地を外す。
    ///   ほかに TransferCoverage(SamMaskRefine.Transfer.cs): 別解像度のキャンバスへの、被覆を保つ転写。
    /// このファイルは、段が共有する定数と下回り(格子の色の統計・切り出しの矩形の導出・距離変換)。
    /// 統計は対象テクスチャ自身から局所的に導出し、特定の色・座標・素材への
    /// 依存はない(デコンタミの局所ドナー統計と同じ思想)。
    /// 配列を受ける口(各段の「配列版」)は製品では呼ばない。ハーネスとテストが使う(製品はポインタの版)。
    ///
    /// 並列化について: 全画素ループは Parallel.For で行分割する(4K で数秒 → コア数分の一)。
    /// 出力の決定性は「各画素の書き込み先は自分のインデックスのみ・入力は読み取り専用・
    /// スレッド間の浮動小数集約なし」で保証する(集計はグリッド行単位に分割し、同一セルへの
    /// 書き込みを単一タスクに閉じる)。結果は逐次実行とビット同一。
    /// </summary>
    internal static unsafe partial class SamMaskRefine {
        /// <summary>確信領域の平均色に必要な最小画素数(これ未満の側があれば再分類しない)。</summary>
        const int MinSamples = 16;

        /// <summary>
        /// Ops 共通の並列設定。並列度は Core の規則(CoreParallelism.Max())に従う
        /// (直書きしない。既定のスレッド数と DebugCaptureHooks.ParallelismOverride は向こうで決める)。
        /// PixelProcessor の転送役を経由しないのは、Ops が PixelProcessor 本体に依存しないようにするため
        /// (テストは AI 提案の結果を、Ops と Ops が使う Core だけのダイジェストでキャッシュする)。
        /// token を渡すと Parallel.For がパーティション境界でキャンセルを観測する
        /// (破棄確定の後処理が計算用のスレッドを占有し続け、次クリックの計算と奪い合うのを防ぐ)。
        /// </summary>
        internal static ParallelOptions MakeParallelOptions(System.Threading.CancellationToken token = default)
            => CoreParallelism.Options(token);

        /// <summary>
        /// mask(w*h 個)の true の画素の外接矩形。1 つも無ければ false。行ごとに並列に左右の端を求めて、行の順にまとめる
        /// (整数の比較だけなので、逐次に全画素を見たのと同じ答え)。精密化の切り出しの矩形・Peel・AA の切り出し・ズームの
        /// 判定が、それぞれ同じ走査を持っていた(うち 2 つは 1 スレッドで全画素を見ていた)のを、ここへ寄せた。
        /// </summary>
        internal static bool TryMaskBounds(bool* mask, int w, int h, ParallelOptions po,
            out int x0, out int y0, out int x1, out int y1) {
            var rowMin = new int[h];
            var rowMax = new int[h];
            Parallel.For(0, h, po, y => {
                // 左から最初の true まで、右から最後の true までだけ読む(間は端に効かない)。
                int row = y * w, mn = 0;
                while (mn < w && !mask[row + mn]) {
                    mn++;
                }
                if (mn == w) {
                    rowMin[y] = -1;
                    rowMax[y] = -1;
                    return;
                }
                int mx = w - 1;
                while (!mask[row + mx]) {
                    mx--;
                }
                rowMin[y] = mn;
                rowMax[y] = mx;
            });
            x0 = w; y0 = h; x1 = -1; y1 = -1;
            for (int y = 0; y < h; y++) {
                if (rowMin[y] < 0) {
                    continue;
                }
                if (rowMin[y] < x0) {
                    x0 = rowMin[y];
                }
                if (rowMax[y] > x1) {
                    x1 = rowMax[y];
                }
                if (y < y0) {
                    y0 = y;
                }
                y1 = y;
            }
            return x1 >= 0;
        }

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
        static void ComputeCellModes(long* sum, int* cnt, int cells, int channels, double* mean, bool* valid) {
            for (int g = 0; g < cells; g++) {
                if (cnt[g] >= MinCellSamples) {
                    valid[g] = true;
                    int o = g * channels;
                    for (int k = 0; k < channels; k++) {
                        mean[o + k] = sum[o + k] / (double)cnt[g];
                    }
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
        /// 精密化 4 段(<see cref="SamMaskPostprocess.RefineInPlace(bool*, Color32*, int, int, System.Threading.CancellationToken)"/>)
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
            out bool empty, out int rx0, out int ry0, out int rw, out int rh) {
            rx0 = ry0 = rw = rh = 0;
            empty = false;
            if (mask == null || mask.Length != w * h) {
                return false;
            }
            fixed (bool* m = mask) {
                return TryDeriveRefineCropRect(m, w, h, token, out empty, out rx0, out ry0, out rw, out rh);
            }
        }

        /// <summary><see cref="TryDeriveRefineCropRect(bool[], int, int, System.Threading.CancellationToken, out bool, out int, out int, out int, out int)"/> のポインタ版(mask は w*h 個)。</summary>
        internal static bool TryDeriveRefineCropRect(bool* mask, int w, int h, System.Threading.CancellationToken token,
            out bool empty, out int rx0, out int ry0, out int rw, out int rh) {
            rx0 = ry0 = rw = rh = 0;
            empty = false;
            if (mask == null) {
                return false;
            }
            if (!TryMaskBounds(mask, w, h, MakeParallelOptions(token), out int bx0, out int by0, out int bx1, out int by1)) {
                empty = true;
                return false;
            }

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

        static double Dist2(Color32 c, long sr, long sg, long sb, long sa, int n) {
            double mr = sr / (double)n, mg = sg / (double)n, mb = sb / (double)n, ma = sa / (double)n;
            double dr = c.r - mr, dg = c.g - mg, db = c.b - mb, da = c.a - ma;
            return dr * dr + dg * dg + db * db + da * da;
        }

        /// <summary>事前計算済みセル平均(RGBA, オフセット o)との距離 2 乗。</summary>
        static double Dist2Mean(Color32 c, double* mean, int o) {
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
        /// <remarks>mask は w*h 個(提案のマスクか作業配列)。結果は scratch から借りる(全画素に書く)。
        /// 距離は ushort で持つ(int の半分。精密化は 1 回に最大 13 本作るので、作業配列の山に効く)。本当の距離は
        /// 画像の辺の和(16384² でも 32766)を超えないので、反対側が無いことを表す Inf = 65535 とは混ざらない。
        /// 呼び出し側の判定(&gt; d・&lt;= d、d は数百まで)は、以前の Inf = 2^28 と同じ答えになる。</remarks>
        internal static ushort* DistanceToOpposite(WorkScratch scratch, bool* mask, int w, int h, bool inside,
                                                System.Threading.CancellationToken token = default) {
            const int Inf = ushort.MaxValue;
            ushort* dist = (ushort*)scratch.Byte(w * h * sizeof(ushort), clear: false);
            var po = MakeParallelOptions(token);

            // 行パス: 各行の左右走査で「同じ行内の最近接反対側画素」までの距離(無ければ Inf)
            Parallel.For(0, h, po, y => {
                int row = y * w;
                int run = Inf;
                for (int x = 0; x < w; x++) {
                    int i = row + x;
                    if (mask[i] == inside) {
                        if (run < Inf) {
                            run++;
                        }
                        dist[i] = (ushort)run;
                    } else {
                        run = 0;
                        dist[i] = 0;
                    }
                }
                run = Inf;
                for (int x = w - 1; x >= 0; x--) {
                    int i = row + x;
                    if (dist[i] == 0) { run = 0; continue; }
                    if (run < Inf) {
                        run++;
                    }
                    if (run < dist[i]) {
                        dist[i] = (ushort)run;
                    }
                }
            });

            // 列パス: x を並列度ぶんの連続レンジに分け、各レンジで y 前進/後退走査
            // (行メジャー配列で連続アクセスになり、列単位分割よりキャッシュ効率が良い)
            int dop = System.Math.Max(1, po.MaxDegreeOfParallelism);
            int chunk = (w + dop - 1) / dop;
            Parallel.For(0, dop, po, p => {
                int x0 = p * chunk, x1 = System.Math.Min(w, x0 + chunk);
                if (x0 >= x1) {
                    return;
                }
                for (int y = 1; y < h; y++) {
                    int row = y * w, prev = row - w;
                    for (int x = x0; x < x1; x++) {
                        int v = dist[prev + x] + 1;
                        if (v < dist[row + x]) {
                            dist[row + x] = (ushort)v;
                        }
                    }
                }
                for (int y = h - 2; y >= 0; y--) {
                    int row = y * w, next = row + w;
                    for (int x = x0; x < x1; x++) {
                        int v = dist[next + x] + 1;
                        if (v < dist[row + x]) {
                            dist[row + x] = (ushort)v;
                        }
                    }
                }
            });
            return dist;
        }
    }
}
