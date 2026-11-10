// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Iroca {
    /// <summary>
    /// テクスチャと ColorZone の (sampleColor, targetColor) から、
    /// 許容範囲・彩度制限などのパラメータを自動的に算出する純粋ロジック層。
    /// UI からは IrocaWindow.RunAutoTune() 経由で呼ばれ、結果は TuneResult として返す。
    /// 純粋計算のため、ピクセル配列さえあればバックグラウンドスレッドからも呼べる。
    /// </summary>
    internal static unsafe partial class ZoneAutoTuner {
        // ── デフォルト値（単一ソース: ColorZone / IrocaSessionState のフィールド初期値） ──
        // 旧実装は const をここへ複製し「同期は手作業」だったが、片側だけ変えると
        // CollectOverwrittenSettings の判定が静かに壊れる(2026-07/08 監査で指摘)。既定値インスタンスから
        // 読むことで構造的に同期する。
        // 注意: 読み取り専用。変異させないこと(BG スレッドからも参照される)。
        private static readonly ColorZone s_zoneDefaults = new ColorZone();
        private static float DefaultTolerance => s_zoneDefaults.tolerance;
        private static float DefaultSaturationStrictness => s_zoneDefaults.saturationStrictness;
        private static float DefaultSaturationGuard => s_zoneDefaults.saturationGuard;
        private static float DefaultChromaThreshold => s_zoneDefaults.chromaThreshold;
        private static bool DefaultHighlightRecovery => s_zoneDefaults.highlightRecovery;
        private static float DefaultValueBlend => s_zoneDefaults.valueBlend;
        private static float DefaultEdgeSoftness => s_zoneDefaults.edgeSoftness;
        private static float DefaultShadowDesaturation => s_zoneDefaults.shadowDesaturation;
        private static float DefaultShadowForgivenessSatMin => s_zoneDefaults.shadowForgivenessSatMin;
        private static float DefaultShadowValueFloor => s_zoneDefaults.shadowValueFloor;
        private static float DefaultPartSatCeiling => s_zoneDefaults.partSatCeiling;
        private static float DefaultPartHueBand => s_zoneDefaults.partHueBand;
        private static float DefaultChromaCeiling => s_zoneDefaults.chromaCeiling;
        private static float DefaultHoleFillMargin => s_zoneDefaults.holeFillMargin;

        // 彩度ガード自動導出パラメータ
        //
        // 感度解析の結論:
        //   - 源色がほぼ全域で高彩度の素材(sS≈1.0): guard ON で過検出が大幅に減り、選択精度が改善。
        //   - 影で彩度が落ちる素材(暗部=低彩度が正常): guard ON で悪化。低彩度画素を弾くと
        //     正常なシェーディングまで削ってしまうため。
        //
        // ⇒ 自動提案は「源色 S が極端に高い (>=0.95)」のときに限定する。
        //    境界の sS=0.95〜1.0 では guard を 0.5〜0.8 で線形補間。
        //    UI スライダーは独立に常時露出されているので、中彩度サンプルでも
        //    ユーザーが手動で guard を有効化することは可能。
        private const float SaturationGuardActivateSS = 0.95f;
        private const float SaturationGuardMinAtActivate = 0.5f;
        private const float SaturationGuardMaxAtHighSS = 0.8f;

        // ── 解析パラメータ ──
        private const int HistogramBins = 32;
        private const float NearHueDist = 0.10f;
        private const float NearSatDist = 0.20f;
        private const float NearValDist = 0.30f;
        private const int MinTextureDim = 32;
        private const int MinNearSampleCount = 64;

        /// <summary>
        /// 自動調整が上書きする項目(既定値から変わっていると、上書きの確認に並べる)。文言は UI が付ける
        /// (Core から UI の Localization を見ない。0.4.0 の計画の 3-1)。並びは確認に並べる順。
        /// </summary>
        public enum TunedSetting {
            Tolerance,
            SaturationStrictness,
            SaturationGuard,
            ChromaThreshold,
            HighlightRecovery,
            PatternPreserve,          // valueBlend(UI の「模様保持」)
            EdgeSoftness,
            ShadowDesaturation,
            ShadowForgivenessSatMin,
            ShadowValueFloor,
            PartSatCeiling,
            PartHueBand,
            ChromaCeiling,
            HoleFillMargin,
        }

        /// <summary>
        /// 自動調整がセッションから読むもの(0.4.0 の計画の 3-1。呼び出し側のスレッドで <see cref="From"/> が 1 回だけ取る)。
        /// 今は何も読まない: 以前は全体設定の 2 つと「どのゾーンも基本の項目が既定のままか」を読み、条件を満たすと
        /// 境界の回収(antiAliasCleanup)を解像度で 4・5 に上げて全体設定へ書いていた(applyGlobals)。製品(UI・MCP)では
        /// 新しいゾーンの許容範囲が既定と違うため一度も当たらず、ハーネスでだけ毎回当たって、測る自動調整と利用者の
        /// 自動調整が食い違っていたので消した(2026-11-10 点検 自-2 案 A・B-8)。型と引数は、呼び出し側(AutoTuneService・
        /// EditMode のテスト)を変えずに済むよう残してある。
        /// </summary>
        public readonly struct SessionInput {
            public static SessionInput From(IrocaSessionState session) {
                return default;
            }
        }

        public struct TuneResult {
            // Per-zone（常に有効）
            public float tolerance;
            public float saturationStrictness;
            public float saturationGuard;
            public float chromaThreshold;
            public bool highlightRecovery;
            public float valueBlend;
            public float edgeSoftness;
            public float shadowDesaturation;
            public float shadowForgivenessSatMin;
            // 陰影の明度下限(0=無効)。証拠つき導出だけが値を入れる(従来導出は既定 0 のまま)。
            public float shadowValueFloor;
            // 別パーツの彩度上限(0=無効)。証拠つき導出だけが値を入れる(従来導出は既定 0 のまま)。
            public float partSatCeiling;
            // 別パーツの色相幅(0=無効)。証拠つき導出(有彩地色)だけが値を入れる(従来導出は既定 0 のまま)。
            public float partHueBand;
            // 彩度天井(グレーモード専用、0=自動)。証拠つき導出だけが値を入れる(従来導出は既定 0 のまま)。
            public float chromaCeiling;
            // 穴埋めの余白(0=なし)。証拠つき導出だけが値を入れる(従来導出は既定 0 のまま)。
            public float holeFillMargin;

            // 全体設定(RecolorSettings)は扱わない(ゾーンの項目だけを導く。2026-11-10 点検 自-2 案 A で
            // 境界の回収の提案を消した。edgeFeather は「ごまかし」なので元から扱わない)。

            // 現在値がデフォルトと異なる(自動調整で上書きされる)項目。文言は UI が付ける。
            public List<TunedSetting> overwrittenSettings;

            // 自動トーン抽出で生成した内部サンプル（暗部/中間/明部の代表色）。
            // ユーザーのスポイト1点から算出され、選択（マッチング）の和集合に使う。
            // 空のとき＝単一サンプル挙動。zone.extraSamples へ適用される。
            public List<Color> autoSamples;

            // スポイト位置の正規化で得たパーツの代表地色。hasNormalizedSample が true のとき
            // zone.sampleColor へ適用する（ツヤや深い影をクリックしても、同じパーツなら同じ
            // 基準色で選択・再着色されるようにするため）。詳細は ZoneAutoTuner.Normalize.cs。
            public Color normalizedSample;
            public bool hasNormalizedSample;

            // 証拠つき導出(AnalyzeWithEvidence)の診断文字列。null=従来経路。
            // ハーネス/テストが導出の内訳(ツヤ画素数・tolerance の合成内訳等)を観測するためだけの
            // フィールドで、製品 UI は使わない。
            public string evidenceDiag;

            /// <summary>
            /// 導出結果をゾーンへ適用する(session は今は書かない。呼び出し側を変えずに済むよう引数を残してある)。
            /// 製品 UI（IrocaWindow.AutoTune の apply）と headless ハーネス（--autotune）は必ずここを通す。
            /// 以前は両者が同じ項目を手書きで並べる二重実装で、片方に項目を足し忘れても UI は
            /// ハーネスのコンパイル対象外なのでどのゲートにも掛からず、「テストが測る自動調整」と
            /// 「ユーザーが得る自動調整」が黙って別物になり得た（2026-09-02 レビュー）。
            /// ここが単一の正で、ハーネスの対象（出荷ゲート対象）に入る。フィールドを増やしたら
            /// ここにも書く（scripts/source_checks/test_autotune_apply_single_source が機械検査する）。
            /// </summary>
            public void ApplyTo(ColorZone zone, IrocaSessionState session) {
                if (zone == null) {
                    return;
                }
                // スポイト位置の正規化: クリックした 1 texel がツヤや深い影でも、パーツの代表地色を
                // 基準色に据え直す。以降の選択・再着色がクリック位置に依存しなくなる
                // （スウォッチの色も代表地色へ変わるので、何が基準かが UI から見て分かる）。
                if (hasNormalizedSample) {
                    zone.sampleColor = normalizedSample;
                }
                zone.tolerance = tolerance;
                zone.saturationStrictness = saturationStrictness;
                zone.saturationGuard = saturationGuard;
                zone.chromaThreshold = chromaThreshold;
                zone.highlightRecovery = highlightRecovery;
                zone.valueBlend = valueBlend;
                zone.edgeSoftness = edgeSoftness;
                zone.shadowDesaturation = shadowDesaturation;
                zone.shadowForgivenessSatMin = shadowForgivenessSatMin;
                zone.shadowValueFloor = shadowValueFloor;
                zone.partSatCeiling = partSatCeiling;
                zone.partHueBand = partHueBand;
                zone.chromaCeiling = chromaCeiling;
                zone.holeFillMargin = holeFillMargin;
                // 自動トーン抽出で得た内部サンプル（暗部/中間/明部の代表色）。ユーザーが複数スポイトする
                // 代わりにアルゴリズムがパーツの濃淡を自動取得した結果で、選択（マッチング）の和集合に
                // 使われる。出力色は主サンプル基準のまま変わらない。
                zone.extraSamples = autoSamples ?? new List<Color>();
                zone.UpdateCacheIfNeeded();
            }
        }

        /// <summary>
        /// 解析の全走査が共有する HSV 前計算格子。
        ///
        /// 自動調整は最大 15 回前後の全画面走査を行うが、走査対象の格子(ストライド)と
        /// 変換式(<see cref="Color.RGBToHSV"/> に r/255f,g/255f,b/255f を渡す)は全パスで同一
        /// だった。従来はそれぞれのパスが同じ画素の HSV を毎回計算し直しており、4K では
        /// RGBToHSV だけで延べ 1 億回規模になっていた(しかも全て単スレッド)。
        /// ここで 1 回だけ並列に作って全パスで読む。同じ関数に同じ入力を与えた値を配列経由で
        /// 読むだけなので、各パスの判定・集計は従来と完全に同一 = 出力ビット不変。
        /// </summary>
        internal sealed class HsvGrid : IDisposable {
            public int stride, gw, gh;
            /// <summary>解析の取り消し。格子をなめる並列ループ(照合の検証など)に渡し、ループの途中でも止める。</summary>
            public CancellationToken ct;
            public float* h, s, v;
            /// <summary>解析の間だけ使う作業配列(ヒープの外)。格子と一緒に返す。並列ループの中から借りないこと。</summary>
            public readonly WorkScratch scratch = new WorkScratch();

            public void Dispose() => scratch.Dispose();
        }

        private static HsvGrid BuildHsvGrid(Color32* pixels, int w, int h, CancellationToken ct) {
            int stride = (w <= 2048) ? 1 : 2;
            int gw = (w + stride - 1) / stride, gh = (h + stride - 1) / stride;
            var grid = new HsvGrid { stride = stride, gw = gw, gh = gh, ct = ct };
            grid.h = grid.scratch.Float(gw * gh, clear: false);
            grid.s = grid.scratch.Float(gw * gh, clear: false);
            grid.v = grid.scratch.Float(gw * gh, clear: false);
            var po = new ParallelOptions {
                // PixelProcessor と同じ規則を使う。直書きするとデバッグ時のスレッド数指定
                // (DebugCaptureHooks.ParallelismOverride) がここだけ効かず計測が食い違う。
                MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism(),
                CancellationToken = ct,
            };
            try {
                Parallel.For(0, gh, po, gy => {
                    int row = (gy * stride) * w;
                    int grow = gy * gw;
                    for (int gx = 0; gx < gw; gx++) {
                        Color32 c = pixels[row + gx * stride];
                        Color.RGBToHSV(new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f),
                            out grid.h[grow + gx], out grid.s[grow + gx], out grid.v[grow + gx]);
                    }
                });
            } catch {
                grid.Dispose();   // 取り消し。呼び出し側はまだ受け取っていない
                throw;
            }
            return grid;
        }

        /// <summary>
        /// pixels, zone, session から推奨値と上書き対象ラベルを返す。
        /// 副作用なし。失敗時もデフォルト相当の TuneResult を返す。
        /// pixels が null / 寸法が極端に小さい場合はヒューリスティック既定のみで返す。
        /// </summary>
        /// <param name="excluded">
        /// 除外マスク(共通∪ゾーン別の OR 結合, true=除外)。サイズ maskW*maskH。
        /// null または全 false の場合はマスク無しパス。マスクがある場合は
        /// 「含有(非除外)領域全体をパーツとみなし、その距離分布から tolerance を導出」する。
        /// </param>
        public static TuneResult Analyze(Color32[] pixels, int width, int height,
            ColorZone zone, SessionInput session,
            bool[] excluded = null, int maskW = 0, int maskH = 0, CancellationToken ct = default,
            Action<float> report = null) {
            // 長さが足りない配列は渡さない(ヒューリスティック既定だけを返す。ポインタ版は長さを確かめられない)。
            if (pixels != null && pixels.Length < (long)width * height) {
                pixels = null;
            }
            fixed (Color32* p = pixels) {
                return Analyze(p, width, height, zone, session, excluded, maskW, maskH, ct, report);
            }
        }

        /// <summary>
        /// <see cref="Analyze(Color32[],int,int,ColorZone,SessionInput,bool[],int,int,CancellationToken,System.Action{float})"/> の
        /// ポインタ版(Mono のヒープの外に置いた画素を写さずに読む)。pixels は width*height 画素以上を、解析が返るまで保つこと。
        /// </summary>
        public static TuneResult Analyze(Color32* pixels, int width, int height,
            ColorZone zone, SessionInput session,
            bool[] excluded = null, int maskW = 0, int maskH = 0, CancellationToken ct = default,
            Action<float> report = null) {
            // 長さが足りないマスクは無いものとして扱う(大きさの分かるのはここまで)。
            if (excluded != null && excluded.Length < (long)maskW * maskH) {
                excluded = null;
            }
            fixed (bool* e = excluded) {
                return AnalyzeCore(pixels, width, height, zone, session, e, maskW, maskH, ct, report);
            }
        }

        /// <summary>
        /// 解析の本体。excluded は maskW*maskH 要素以上を、解析が返るまで保つこと(null = マスク無し)。
        /// </summary>
        internal static TuneResult AnalyzeCore(Color32* pixels, int width, int height,
            ColorZone zone, SessionInput session,
            bool* excluded, int maskW, int maskH, CancellationToken ct,
            Action<float> report) {
            // 進捗は「解析ステップの通過点」を単調増加で報告する。各ステップの実時間は
            // テクスチャ次第で読めないため、割合はステップ数ベースの目安。null 可(テスト等)。
            void Progress(float v) { report?.Invoke(v); }

            var result = BuildHeuristicDefault(zone);

            bool canAnalyze = pixels != null
                && width >= MinTextureDim && height >= MinTextureDim;
            if (canAnalyze) {
                // 解析は最大 15 回前後の全画面走査で数百 ms かかる。各ステップ間でキャンセルを確認し、
                // 新しい自動調整が来たら旧解析をゾンビ実行させない(CPU 2 倍/進捗バー飛びを防ぐ)。
                // 各ステップは O(len) なので最悪でも 1 ステップ分で停止する。
                ct.ThrowIfCancellationRequested();
                // マスクがある場合は含有(非除外)領域にクラスタを限定する(色が同じ別パーツを除外)。
                // ヒストグラム解析(TryAnalyzePixels)も含め全経路で同じ含有領域だけを見る。マスクで
                // 除外したパーツの画素が shadowForgivenessSatMin / saturationStrictness /
                // highlightRecovery 初期値へ混入するのを防ぐ(tolerance 導出側は元から尊重していた)。
                bool useMask = HasUsableMask(excluded, maskW, maskH);
                bool* clusterMask = useMask ? excluded : null;

                // 全走査が共有する HSV 格子を 1 回だけ並列で作る(以降のパスは表引きするだけ)。
                using var hsv = BuildHsvGrid(pixels, width, height, ct);
                Progress(0.30f);

                // ── スポイト位置の正規化 ──
                // 以降の導出はすべて「サンプル色」起点なので、クリック画素がツヤ/深い影だと
                // パーツの同じ場所を指していても別の結論になる。ここでクリック色をパーツの
                // 代表地色へ寄せ、以降の全パスへ同じ入力を渡す(ZoneAutoTuner.Normalize.cs)。
                // 正規化できない場合(無彩クリック等)は aZone == zone のまま従来挙動。
                ct.ThrowIfCancellationRequested();
                ColorZone aZone = zone;
                bool hasNorm = TryNormalizeSample(pixels, width, height, zone,
                    clusterMask, maskW, maskH, hsv, out Color canonicalSample, out _);
                if (hasNorm) {
                    aZone = zone.Clone();
                    aZone.sampleColor = canonicalSample;
                }

                Progress(0.40f);

                if (TryAnalyzePixels(pixels, width, height, aZone, clusterMask, maskW, maskH, hsv, out var analyzed)) {
                    result = MergeAnalyzed(result, analyzed);
                }
                Progress(0.50f);

                // tolerance とトーン代表色(導出の中身と理由は DeriveToleranceAndTones)。
                Color.RGBToHSV(aZone.sampleColor, out _, out float sampleS, out _);
                DeriveToleranceAndTones(pixels, width, height, aZone, clusterMask, maskW, maskH, hsv,
                    sampleS < AchromaSampleSatMax, ct, report, 0.62f, ref result,
                    out bool foreignCapped, out int vConnHiBin);

                Progress(0.75f);

                // ── 閉ループ検証: 導出パラメータを実マッチャーに通し、有害な設定を安全側へ倒す ──
                RunClosedLoopVerification(pixels, width, height, aZone, excluded, maskW, maskH, hsv,
                    sampleS >= AchromaSampleSatMax, foreignCapped, vConnHiBin, ct, report,
                    0.85f, 0.92f, null, 0, 0, ref result);

                Progress(0.98f);

                // MergeAnalyzed が TuneResult を作り直すので、正規化結果は全解析の後で載せる。
                result.normalizedSample = canonicalSample;
                result.hasNormalizedSample = hasNorm;
            }

            CollectOverwrittenSettings(zone, ref result);
            return result;
        }

        // ─────────────────── tolerance 導出と閉ループ検証(Analyze / AnalyzeWithEvidence 共通) ───────────────────
        // 2 つの入口が同じ導出・同じ検証を通ることを、コードを 1 か所に置くことで保つ。
        // 入口ごとに違うのは地色(aZone)と進捗の値だけ。

        // 進捗の番兵: この値を渡した通過点では進捗を報告しない。
        private const float NoProgress = -1f;

        /// <summary>
        /// tolerance とトーン代表色(result.autoSamples)を導出する。母集団はクリック近傍窓(clusterMask)。
        ///
        /// tolerance は常に「サンプル近傍クラスタの実マッチ距離分布」から導出する。
        /// 無彩(グレーモード=純 RGB 距離)と有彩(HSV マッチ)で距離式が違うため経路を分けるが、
        /// いずれも MergeAnalyzed の hSpread/vSpread 由来ヒューリスティック(実距離と切り離され
        /// 過大選択を招く)を実距離分布へ置き換える。
        ///
        /// 旧「マスク認識経路(含有領域全画素の P99.9, 上限0.40)」は、ゆるい/残存マスクや
        /// 明暗の広いパーツ(例: 明るいサンプルの髪)で上限 0.40 に張り付いていた(ユーザー報告)
        /// ため廃止。パーツ内の暗部・薄い装飾は本番のシャドウ免除/ハイライト復元が tolerance
        /// とは独立に拾うので、tolerance を膨らませない。
        /// </summary>
        /// <param name="achromatic">
        /// true=無彩サンプル。判定式(サンプル S と AchromaSampleSatMax の比較)は呼び出し側に置き、
        /// 各入口の比較の向きをそのまま渡す(向きを変えると S が NaN のときの分岐が変わる)。
        /// </param>
        /// <param name="progressAfterTones">トーン抽出の直後に報告する進捗(有彩のみ)。<see cref="NoProgress"/> なら報告しない。</param>
        /// <param name="foreignCapped">
        /// foreign 打ち切り(隣接同色相パーツの検出)が効いたか。効いた場合は明部ツヤ救済
        /// (VerifyBrightSheenRecall)の拡張を封印する(打ち切りと拡張が相殺し隣接パーツを再び巻き込むのを防ぐ)。
        /// </param>
        /// <param name="vConnHiBin">トーン連結域の V 上端 bin(明部ツヤ救済の上限に使う)。-1=未確定。</param>
        private static void DeriveToleranceAndTones(Color32* pixels, int width, int height,
            ColorZone aZone, bool* clusterMask, int maskW, int maskH, HsvGrid hsv,
            bool achromatic, CancellationToken ct, Action<float> report, float progressAfterTones,
            ref TuneResult result, out bool foreignCapped, out int vConnHiBin) {
            ct.ThrowIfCancellationRequested();
            foreignCapped = false;
            vConnHiBin = -1;
            if (achromatic) {
                // 無彩色サンプル: グレーモードの純 RGB 距離分布から(V 広がりの過大評価を回避)。
                // 自動トーン抽出は無彩では背景の白/黒と色で分離できず危険なので行わない（単一経路）。
                if (TryDeriveAchromaticTolerance(pixels, width, height, aZone,
                        clusterMask, maskW, maskH, hsv, out float achTol)) {
                    result.tolerance = achTol;
                    // 無彩色サンプルではハイライト復元を切る。グレーモードのハイライト経路は
                    // 「明度だけ」で判定し色相/彩度を見ないため、明るい有彩画素(別素材)まで
                    // 巻き込んでしまう。グレー本体のハイライトは RGB 距離 tolerance で拾える。
                    result.highlightRecovery = false;
                }
            } else {
                // ── 有彩サンプル: 自動トーン抽出（内部マルチサンプル）─────────────────
                // スポイト1点から、同色相のパーツ全体のトーン分布を内部で走査し、暗部・中間・
                // 明部の代表色を自動生成する（ユーザーの追加スポイト操作は不要）。これらを和集合の
                // 内部サンプルとして、各画素の最近サンプルまでの距離 P95 から tolerance を導出する。
                // スポイト位置が明部でも暗部でも、トーン全域を覆うので取りこぼし/はみ出しを抑えられる。
                var autoSamples = DeriveAutoTonalSamples(pixels, width, height, aZone,
                    clusterMask, maskW, maskH, hsv, out vConnHiBin);
                if (progressAfterTones >= 0f) {
                    report?.Invoke(progressAfterTones);
                }
                bool derivedMulti = false;
                if (autoSamples.Count > 0) {
                    var samples = BuildSampleHSVs(aZone.sampleColor, autoSamples);
                    if (TryDeriveChromaticToleranceMulti(pixels, width, height, aZone, samples,
                            clusterMask, maskW, maskH, hsv, out float chromTolM, out bool fCapM)) {
                        result.autoSamples = autoSamples;
                        result.tolerance = chromTolM;
                        foreignCapped = fCapM;
                        derivedMulti = true;
                    }
                }
                if (!derivedMulti && TryDeriveChromaticTolerance(pixels, width, height, aZone,
                        clusterMask, maskW, maskH, hsv, out float chromTol, out bool fCap)) {
                    // 単一サンプルへフォールバック(トーン抽出が不発/クラスタ過少)。
                    result.tolerance = chromTol;
                    foreignCapped = fCap;
                }
            }
        }

        /// <summary>
        /// 閉ループ検証: 導出パラメータを実マッチャーに通し、有害な設定を安全側へ倒す。
        /// 全画面視点で、母集団は除外マスク excluded(導出側の clusterMask ではない)。
        /// </summary>
        /// <param name="chromatic">true=有彩サンプル(免除過剰の判定と明部ツヤ救済を行う)。</param>
        /// <param name="foreignCapped">導出で foreign 打ち切りが効いたか(<see cref="DeriveToleranceAndTones"/>)。</param>
        /// <param name="vConnHiBin">トーン連結域の V 上端 bin。-1=未確定。</param>
        /// <param name="progressAfterGrowth">ハイライト成長テストの後に報告する進捗。<see cref="NoProgress"/> なら報告しない。</param>
        /// <param name="progressAfterOvershoot">免除過剰の判定の後に報告する進捗(有彩のみ)。<see cref="NoProgress"/> なら報告しない。</param>
        /// <param name="evidence">証拠マスク(AI 提案のセグメント = クリックしたパーツ)。null = 証拠なし。
        /// ハイライト成長テストだけが使う(<see cref="VerifyHighlightRecoveryGrowth"/>)。</param>
        private static void RunClosedLoopVerification(Color32* pixels, int width, int height,
            ColorZone aZone, bool* excluded, int maskW, int maskH, HsvGrid hsv,
            bool chromatic, bool foreignCapped, int vConnHiBin, CancellationToken ct,
            Action<float> report, float progressAfterGrowth, float progressAfterOvershoot,
            bool* evidence, int evW, int evH,
            ref TuneResult result) {
            ct.ThrowIfCancellationRequested();
            var baseSel = new BaseSelection();   // 導出 tolerance の基礎選択(最初に数えた検証が埋める)
            bool hlRecBeforeVerify = result.highlightRecovery;
            if (result.highlightRecovery) {
                VerifyHighlightRecoveryGrowth(pixels, width, height, aZone,
                    excluded, maskW, maskH, hsv, evidence, evW, evH, baseSel, ref result);
            }
            // 成長テストが highlightRecovery を落とした=「明るい同色相の別素材」が既に検出された
            // 状況なので、同じ方向へ広げる明部ツヤ救済も封印する。
            bool hlRecVetoed = hlRecBeforeVerify && !result.highlightRecovery;
            if (progressAfterGrowth >= 0f) {
                report?.Invoke(progressAfterGrowth);
            }
            ct.ThrowIfCancellationRequested();
            if (chromatic) {
                float tolBeforeOvershoot = result.tolerance;
                VerifyBrightForgivenessOvershoot(pixels, width, height, aZone,
                    excluded, maskW, maskH, hsv, baseSel, ref result);
                // 免除過剰で tolerance を縮めた直後に拡張するのは矛盾するのでスキップする。
                bool overshootShrunk = result.tolerance < tolBeforeOvershoot;
                if (progressAfterOvershoot >= 0f) {
                    report?.Invoke(progressAfterOvershoot);
                }

                ct.ThrowIfCancellationRequested();
                // vConnHiBin < 0(トーン構造を確定できなかった)ときは拡張しない(構造未知のまま
                // 広げるのは危険。素直なパーツならヒストグラムは常に作れる)。
                if (!foreignCapped && !overshootShrunk && !hlRecVetoed && vConnHiBin >= 0) {
                    VerifyBrightSheenRecall(pixels, width, height, aZone,
                        excluded, maskW, maskH, hsv, vConnHiBin, baseSel, ref result);
                }
            }
        }

        // ─────────────────── 既定値ベース ───────────────────

        private static TuneResult BuildHeuristicDefault(ColorZone zone) {
            return new TuneResult {
                tolerance = 0.25f,
                saturationStrictness = DefaultSaturationStrictness,
                saturationGuard = DefaultSaturationGuard,
                chromaThreshold = DefaultChromaThreshold,
                highlightRecovery = DefaultHighlightRecovery,
                valueBlend = DefaultValueBlend,
                edgeSoftness = 0.15f,
                shadowDesaturation = DefaultShadowDesaturation,
                shadowForgivenessSatMin = DefaultShadowForgivenessSatMin,
                shadowValueFloor = DefaultShadowValueFloor,
                partSatCeiling = DefaultPartSatCeiling,
                partHueBand = DefaultPartHueBand,
                chromaCeiling = DefaultChromaCeiling,
                holeFillMargin = DefaultHoleFillMargin,
                overwrittenSettings = new List<TunedSetting>(),
                autoSamples = new List<Color>(),
            };
        }

        // ─────────────────── ピクセル解析 ───────────────────

        private struct AnalysisStats {
            public int nearSampleCount;
            public int[] hBins;
            public int[] sBins;
            public int[] vBins;
            public int highlightCandidates;
            public float darkestNearSampleS;
            public bool darkSeen;
            public float sH, sS, sV;
            public float tV;  // target color V（明度差で valueBlend 判定に使う）
        }

        private static bool TryAnalyzePixels(Color32* pixels, int w, int h, ColorZone zone,
            bool* excluded, int maskW, int maskH, HsvGrid hsv, out AnalysisStats stats) {
            stats = new AnalysisStats {
                hBins = new int[HistogramBins],
                sBins = new int[HistogramBins],
                vBins = new int[HistogramBins],
                darkestNearSampleS = 1f,
            };

            Color.RGBToHSV(zone.sampleColor, out stats.sH, out stats.sS, out stats.sV);
            Color.RGBToHSV(zone.targetColor, out _, out _, out stats.tV);

            int stride = hsv.stride;
            float* gH = hsv.h, gS = hsv.s, gV = hsv.v;
            int gw = hsv.gw;
            float sHs = stats.sH, sSs = stats.sS, sVs = stats.sV;
            float hlSatCeil = ColorZone.HighlightSaturationCeiling(sSs);
            var ml = GridMaskLookup.Build(excluded, maskW, maskH, w, h, stride, gw, hsv.gh);

            // 格子の行を並列に回し、スレッドごとに数えてから足し合わせる。数は整数の和、最も暗い所の彩度は最小値で、
            // どちらも足す・比べる順に依らない(逐次の「より小さければ置き換える」と同じ値)。
            var gate = new object();
            var po = new ParallelOptions { MaxDegreeOfParallelism = PixelProcessor.GetMaxParallelism(), CancellationToken = hsv.ct };
            var st = stats;
            Parallel.For(0, hsv.gh, po,
                () => new AnalysisStats {
                    hBins = new int[HistogramBins],
                    sBins = new int[HistogramBins],
                    vBins = new int[HistogramBins],
                    darkestNearSampleS = 1f,
                },
                (gy, _, loc) => {
                    int y = gy * stride;
                    int rowStart = y * w;
                    int grow = gy * gw;
                    for (int x = 0, gx = 0; x < w; x += stride, gx++) {
                        Color32 c32 = pixels[rowStart + x];
                        if (c32.a < PixelProcessor.OpaqueAlphaMin) {
                            continue;
                        }
                        if (ml != null && excluded[ml.Row[gy] + ml.Col[gx]]) {
                            continue; // マスク除外領域は対象外
                        }

                        int gi = grow + gx;
                        float pH = gH[gi], pS = gS[gi], pV = gV[gi];

                        float hDist = HueDistance(pH, sHs);

                        // ハイライト復元候補（サンプル色と同系のハイライト領域）。
                        // 条件は ColorZone のハイライト判定(HighlightSaturationCeiling で動的化)と
                        // Verify.cs:GrowHighlightBand ミラーと同じ定数を共有する。
                        if (pV > ColorZone.HighlightValueMin && pS < hlSatCeil
                            && hDist < ColorZone.ForgivenessHueGate) {
                            loc.highlightCandidates++;
                        }

                        if (hDist < NearHueDist &&
                            Mathf.Abs(pS - sSs) < NearSatDist &&
                            Mathf.Abs(pV - sVs) < NearValDist) {
                            loc.nearSampleCount++;
                            loc.hBins[Mathf.Clamp((int)(pH * HistogramBins), 0, HistogramBins - 1)]++;
                            loc.sBins[Mathf.Clamp((int)(pS * HistogramBins), 0, HistogramBins - 1)]++;
                            loc.vBins[Mathf.Clamp((int)(pV * HistogramBins), 0, HistogramBins - 1)]++;

                            if (pV < 0.4f && pS < loc.darkestNearSampleS) {
                                loc.darkestNearSampleS = pS;
                                loc.darkSeen = true;
                            }
                        }
                    }
                    return loc;
                },
                loc => {
                    lock (gate) {
                        st.highlightCandidates += loc.highlightCandidates;
                        st.nearSampleCount += loc.nearSampleCount;
                        for (int b = 0; b < HistogramBins; b++) {
                            st.hBins[b] += loc.hBins[b]; st.sBins[b] += loc.sBins[b]; st.vBins[b] += loc.vBins[b];
                        }
                        if (loc.darkSeen) {
                            st.darkSeen = true;
                            if (loc.darkestNearSampleS < st.darkestNearSampleS) {
                                st.darkestNearSampleS = loc.darkestNearSampleS;
                            }
                        }
                    }
                });
            stats = st;

            return stats.nearSampleCount >= MinNearSampleCount;
        }

        private static TuneResult MergeAnalyzed(TuneResult heuristic, AnalysisStats s) {
            float hSpread = HueSpreadFromHistogram(s.hBins, s.sH, s.nearSampleCount);
            float sP10 = PercentileBin(s.sBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP10 = PercentileBin(s.vBins, s.nearSampleCount, 0.10f) / (float)HistogramBins;
            float vP90 = PercentileBin(s.vBins, s.nearSampleCount, 0.90f) / (float)HistogramBins;
            float vSpread = Mathf.Max(0f, vP90 - vP10);

            // tolerance（フォールバック）: 色相の広がり + マージン。低彩度時は V の広がりで近似。
            // 通常は Analyze で TryDeriveChromaticTolerance/TryDeriveAchromaticTolerance(実距離分布)に
            // 上書きされる。ここはクラスタが過少(<MinNearSampleCount)で実距離導出が失敗したときの保険。
            float tolerance;
            if (s.sS < 0.10f) {
                tolerance = Mathf.Clamp(vSpread + 0.10f, 0.12f, 0.50f);
            } else {
                tolerance = Mathf.Clamp(hSpread + 0.10f, 0.12f, 0.50f);
            }

            // saturationStrictness: 低彩度サンプル → 緩める。低 S テールが目立つ → 厳しく。
            float saturationStrictness;
            if (s.sS < 0.10f) {
                saturationStrictness = 0.30f;
            } else if (sP10 < s.sS * 0.3f) {
                saturationStrictness = 0.65f;
            } else {
                saturationStrictness = 0.50f;
            }

            // chromaThreshold: サンプル彩度がしきい値以下のときグレースケールモードに入る仕様
            // （ColorZone.BuildSampleCache の isGrayMode = GrayModeEffectiveChromaThreshold 参照）。
            // 低彩度サンプル(sS < 0.15)はしきい値を sS + 0.05 に引き上げて確実にグレーモード化、
            // 有彩色サンプルは既定値 0.05 を維持して通常の HSV マッチに任せる。
            float chromaThreshold = (s.sS < 0.15f)
                ? Mathf.Min(0.20f, s.sS + 0.05f)
                : DefaultChromaThreshold;

            // highlightRecovery: 同系ハイライトが十分にあれば有効化
            int highlightThreshold = Mathf.Max(50, Mathf.RoundToInt(s.nearSampleCount * 0.02f));
            bool highlightRecovery = s.highlightCandidates >= highlightThreshold;

            // valueBlend: 原則 1.0（模様完全保持）を維持。
            // サンプルとターゲットの明度差が極端（例: 明るい色 → 黒）な場合のみ下げる。
            // それ以外では模様を残すことを最優先する方針。
            float vDelta = Mathf.Abs(s.sV - s.tV);
            float valueBlend;
            if (vDelta > 0.55f) {
                valueBlend = 0.7f;        // 例: 白系 → 黒系
            } else if (vDelta > 0.35f) {
                valueBlend = 0.9f;   // 中程度の明度差
            } else {
                valueBlend = 1.0f;                       // 通常は模様完全保持
            }

            // edgeSoftness: 色相広がりが大きいほど柔らかく
            float edgeSoftness;
            if (hSpread > 0.15f) {
                edgeSoftness = 0.4f;
            } else if (hSpread < 0.05f) {
                edgeSoftness = 0.0f;
            } else {
                edgeSoftness = 0.15f;
            }

            // shadowForgivenessSatMin: シャドウ免除(暗部の同系色画素を救済する仕組み)の下限彩度。
            // 免除は「自パーツの暗部彩度まで」に限定する = darkestNearSampleS(自パーツの暗部の
            // 最小彩度)のすぐ下に下限を置く。darkestNearSampleS は定義上「自パーツ暗部の最小彩度」
            // なので、その 0.95 倍を下限にすれば自パーツの暗部は全て免除され(recall 不変)、それより
            // 彩度の低い別パーツの暗部だけが免除から外れる(暗部の巻き込み=はみ出しを抑える)。
            // 旧式(darkestNearSampleS*0.5, 上限0.20)は、暗部でも彩度がほとんど落ちないパーツ(陰影が
            // ほぼ V だけで表現され、暗部でも彩度が 0.85 前後に留まる濃色の布地など)でも下限が 0.20 に
            // 張り付き、彩度 0.2〜0.85 の別パーツ暗部を巻き込んでいた(実測例: この種のパーツで下限を
            // 0.20→0.80 にすると precision 0.980→0.994、recall は不変)。
            // 暗部画素が無いパーツ(darkSeen=false)は既定 0.05 のまま。
            // 事実(点検 自-4、2026-11-10): darkestNearSampleS を集めるのは近傍の窓(TryAnalyzePixels。色相 ±NearHueDist・
            // 彩度 ±NearSatDist・明度 ±NearValDist)の中の V < 0.4 の画素だけなので、「暗部が脱彩する素材なら下限が下がる」
            // は窓の幅までしか効かない。
            //   - 下限は常に 0.95 × (sS − NearSatDist) 以上(窓の外の、もっと脱彩した暗部は数えない)。
            //   - サンプルの明度が 0.4 + NearValDist(= 0.7)以上なら窓に V < 0.4 の画素が入らず、darkSeen=false で既定の
            //     0.05 になる。明度 0.69 と 0.71 のサンプルで下限が大きく違う(崖)。
            // 証拠つきの自動調整も同じ関数を通る(ZoneAutoTuner.Evidence.cs の TryAnalyzePixels)。直すのは測ってから
            // (修正計画の自-4 段 2: 崖の両側のケースを数え、暗部を色相帯で集める案を改善サイクルで試す)。
            float shadowForgivenessSatMin = DefaultShadowForgivenessSatMin;
            if (s.darkSeen && s.darkestNearSampleS > 0.10f) {
                shadowForgivenessSatMin = Mathf.Clamp(s.darkestNearSampleS * 0.95f, 0.05f, 0.90f);
            }

            // saturationGuard: 源色 S が極端に高い (>=0.95) ときだけ自動提案。
            // sS=0.95 → Min(0.5)、sS=1.0 → Max(0.8) で線形補間。
            // sS<0.95 では 0 のまま（感度解析で、影で彩度が落ちる素材は
            // 控えめ guard でも悪化することを確認 — シェーディングが S を落とす素材を
            // 巻き込まないよう厳しめのゲート）。
            //
            // 数値はキャラ・テクスチャ依存ではなく「源色の彩度」という距離式入力から
            // 導出しているため、特定テクスチャへのハードコードにはならない。
            float saturationGuard;
            if (s.sS < SaturationGuardActivateSS) {
                saturationGuard = 0f;
            } else {
                float t = Mathf.Clamp01((s.sS - SaturationGuardActivateSS)
                                        / Mathf.Max(0.001f, 1f - SaturationGuardActivateSS));
                saturationGuard = Mathf.Lerp(SaturationGuardMinAtActivate,
                                             SaturationGuardMaxAtHighSS, t);
            }

            return new TuneResult {
                tolerance = tolerance,
                saturationStrictness = saturationStrictness,
                saturationGuard = saturationGuard,
                chromaThreshold = chromaThreshold,
                highlightRecovery = highlightRecovery,
                valueBlend = valueBlend,
                edgeSoftness = edgeSoftness,
                shadowDesaturation = heuristic.shadowDesaturation,
                shadowForgivenessSatMin = shadowForgivenessSatMin,
                shadowValueFloor = DefaultShadowValueFloor,
                partSatCeiling = DefaultPartSatCeiling,
                partHueBand = DefaultPartHueBand,
                chromaCeiling = DefaultChromaCeiling,
                holeFillMargin = DefaultHoleFillMargin,
                overwrittenSettings = new List<TunedSetting>(),
                autoSamples = new List<Color>(),
            };
        }

        // ─────────────────── 上書きされる項目の収集 ───────────────────

        /// <summary>
        /// Analyze を呼ぶ前に、現在の zone 値が default と異なるか（＝自動調整で上書きされ
        /// うるか）を判定して項目を返す（CollectOverwrittenSettings と同じ項目・同じ順）。
        ///
        /// 非同期化のために事前確認をジョブ開始前へ移動する用途で使う。
        /// </summary>
        public static List<TunedSetting> PreviewOverwrittenSettings(ColorZone zone) {
            var settings = new List<TunedSetting>();
            if (zone == null) {
                return settings;
            }

            AddZoneOverwrittenSettings(zone, settings);
            return settings;
        }

        private static void CollectOverwrittenSettings(ColorZone zone, ref TuneResult result) {
            AddZoneOverwrittenSettings(zone, result.overwrittenSettings);
        }

        // ゾーン単位の項目のうち既定値と異なるものを、決まった順で settings へ足す。
        // PreviewOverwrittenSettings(Analyze 前の事前確認)と CollectOverwrittenSettings(TuneResult)で
        // 項目と順序をそろえるための共通部分。
        private static void AddZoneOverwrittenSettings(ColorZone zone, List<TunedSetting> settings) {
            if (!Mathf.Approximately(zone.tolerance, DefaultTolerance)) {
                settings.Add(TunedSetting.Tolerance);
            }
            if (!Mathf.Approximately(zone.saturationStrictness, DefaultSaturationStrictness)) {
                settings.Add(TunedSetting.SaturationStrictness);
            }
            if (!Mathf.Approximately(zone.saturationGuard, DefaultSaturationGuard)) {
                settings.Add(TunedSetting.SaturationGuard);
            }
            if (!Mathf.Approximately(zone.chromaThreshold, DefaultChromaThreshold)) {
                settings.Add(TunedSetting.ChromaThreshold);
            }
            if (zone.highlightRecovery != DefaultHighlightRecovery) {
                settings.Add(TunedSetting.HighlightRecovery);
            }
            if (!Mathf.Approximately(zone.valueBlend, DefaultValueBlend)) {
                settings.Add(TunedSetting.PatternPreserve);
            }
            if (!Mathf.Approximately(zone.edgeSoftness, DefaultEdgeSoftness)) {
                settings.Add(TunedSetting.EdgeSoftness);
            }
            if (!Mathf.Approximately(zone.shadowDesaturation, DefaultShadowDesaturation)) {
                settings.Add(TunedSetting.ShadowDesaturation);
            }
            if (!Mathf.Approximately(zone.shadowForgivenessSatMin, DefaultShadowForgivenessSatMin)) {
                settings.Add(TunedSetting.ShadowForgivenessSatMin);
            }
            if (!Mathf.Approximately(zone.shadowValueFloor, DefaultShadowValueFloor)) {
                settings.Add(TunedSetting.ShadowValueFloor);
            }
            if (!Mathf.Approximately(zone.partSatCeiling, DefaultPartSatCeiling)) {
                settings.Add(TunedSetting.PartSatCeiling);
            }
            if (!Mathf.Approximately(zone.partHueBand, DefaultPartHueBand)) {
                settings.Add(TunedSetting.PartHueBand);
            }
            if (!Mathf.Approximately(zone.chromaCeiling, DefaultChromaCeiling)) {
                settings.Add(TunedSetting.ChromaCeiling);
            }
            if (!Mathf.Approximately(zone.holeFillMargin, DefaultHoleFillMargin)) {
                settings.Add(TunedSetting.HoleFillMargin);
            }
        }

        // ─────────────────── ユーティリティ ───────────────────

        private static float HueDistance(float a, float b) {
            float d = Mathf.Abs(a - b);
            return d > 0.5f ? 1f - d : d;
        }

        /// <summary>
        /// ヒストグラムから P10/P90 を求め、色相は環状なのでサンプル色相 sH を中心として
        /// 「中心からの最大距離」相当を返す（P90 of |hue - sH|_wrap）。
        /// </summary>
        private static float HueSpreadFromHistogram(int[] hBins, float sH, int total) {
            if (total <= 0) {
                return 0f;
            }
            // 各 bin の中心色相 → サンプル色相からの環状距離をキーに、上位 10% を切る。
            // bin 数 = 32 と小さいので、距離ソートをしてから累積で 90 パーセンタイル位置を取る。
            int n = hBins.Length;
            float[] dists = new float[n];
            int[] counts = new int[n];
            for (int i = 0; i < n; i++) {
                float center = (i + 0.5f) / n;
                dists[i] = HueDistance(center, sH);
                counts[i] = hBins[i];
            }
            // 簡易ソート（n=32 なので選択ソートでも十分高速）
            for (int i = 0; i < n - 1; i++) {
                int min = i;
                for (int j = i + 1; j < n; j++) {
                    if (dists[j] < dists[min]) {
                        min = j;
                    }
                }
                if (min != i) {
                    float tmpD = dists[i]; dists[i] = dists[min]; dists[min] = tmpD;
                    int tmpC = counts[i]; counts[i] = counts[min]; counts[min] = tmpC;
                }
            }

            int target = Mathf.CeilToInt(total * 0.90f);
            int cum = 0;
            for (int i = 0; i < n; i++) {
                cum += counts[i];
                if (cum >= target) {
                    return dists[i];
                }
            }
            return 0.5f;
        }

        /// <summary>
        /// ヒストグラムから指定パーセンタイルに対応する bin index を返す（小数）。
        /// </summary>
        private static float PercentileBin(int[] bins, int total, float p) {
            if (total <= 0) {
                return 0f;
            }
            int target = Mathf.CeilToInt(total * p);
            int cum = 0;
            for (int i = 0; i < bins.Length; i++) {
                cum += bins[i];
                if (cum >= target) {
                    return i;
                }
            }
            return bins.Length - 1;
        }
    }
}
