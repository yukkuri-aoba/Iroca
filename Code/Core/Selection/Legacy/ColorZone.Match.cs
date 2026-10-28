// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using UnityEngine;

namespace Iroca
{
    // ColorZone: マッチングエンジン(サンプル派生キャッシュとピクセル一致スコア計算)。旧の選択の一部で、照合の定数と
    // キャッシュの欄もここに置く(0.4.0 の 3-7a。ColorZone.cs はゾーンのデータだけ)。
    public partial class ColorZone
    {
        // ハイライト補助マッチで対象ピクセルとみなす「鏡面反射/ハイライトらしさ」の閾値。
        // internal: ZoneAutoTuner のハイライト候補カウントが同じ条件をリテラルで再掲していたため共有。
        internal const float HighlightValueMin = 0.80f;
        internal const float HighlightSaturationMax = 0.20f;
        // 高彩度サンプル(sS≈1.0 の塗り)ほど、ハイライトの塗り自体が完全な白ではなく
        // 「本体色をやや脱彩した色」で表現されがち(実測: 高彩度の青い靴 sS=1.0 の
        // ハイライトは S 0.2〜0.5 に分布し、絶対閾値 0.20 の外側に外れて高確率で取りこぼしていた)。
        // 固定の絶対閾値だとサンプル自身が高彩度なときに閾値がハイライトの実分布より内側に
        // 寄ってしまうため、サンプル彩度に比例した床を追加する(サンプル彩度に依存する統計量で、
        // 特定キャラ・テクスチャへのハードコードではない)。低〜中彩度サンプルでは
        // sS*Frac が絶対閾値を下回るため従来どおり 0.20 のまま(回帰なし)。
        internal const float HighlightSaturationMaxSampleFrac = 0.5f;
        private const float HighlightRelaxedSatMin = 0.02f;
        private const float HighlightRelaxedSatRamp = 0.08f;

        /// <summary>
        /// ハイライト判定に使う彩度上限を、サンプル彩度に応じて動的に返す。
        /// 高彩度サンプルほど「地色をやや脱彩しただけ」のハイライト塗りが遠くまで分布するため、
        /// 絶対床 <see cref="HighlightSaturationMax"/> とサンプル比例床の大きい方を採る。
        /// </summary>
        internal static float HighlightSaturationCeiling(float sampleS)
            => Mathf.Max(HighlightSaturationMax, sampleS * HighlightSaturationMaxSampleFrac);

        // 彩度ガード: 源色が高彩度なときだけ作動し、対象ピクセルの彩度が
        // 「源色 S × FractionScale × saturationGuard」を下回ったら hard reject する。
        // 源色 S が ActiveSourceSat 未満（灰色寄り）のときは作動しない（自動無効）。
        private const float SaturationGuardActiveSourceSat = 0.40f;
        private const float SaturationGuardFractionScale = 0.30f;

        // 彩度整合ゲート(グレー抽出モード専用): サンプルが微小な tint(sS)を持つとき、
        // それより著しく中性(無彩)寄りの候補ピクセル(=純白 UV 背景など)を別マテリアルと
        // みなし、サンプル彩度に対する相対床を下回る分だけ距離を加算してソフトに排除する。
        // 純RGB距離だけでは、わずかに色の付いた地色(生成り・オフホワイトの布地など)と純白の距離が
        // 極小(~0.03)になり、その素材の暗い側を拾うために tolerance を上げると純白まで巻き込む。
        // 明度で見ると両者は隣接するが、tint の有無で見れば明確に分離できる。
        // サンプル自身が真の無彩(sS≈0)なら作動せず=従来の純RGB距離挙動を完全維持。
        // internal: 緩和マッチ経路(PixelProcessor.GetRelaxedMatchStrength)が同じ値を使うため共有する。
        // かつては PixelProcessor 側に同名 const を複製していたが、二重管理で乖離の温床になるため一本化。
        // 床には絶対上限 FloorCap を併置する。中性性は絶対量であり(圧縮ノイズ由来の偽 tint は
        // S≈0.01 台、視認可能な tint は S≈0.02 以上)、相対床 sS*frac 単独だとサンプル彩度が
        // 高めの無彩寄り素材(生成り・クリーム sS 0.05〜0.15)で床が 0.03〜0.08 まで上がり、
        // その素材自身の脱彩部(白プリーツ・ハイライト、S 0.02〜0.05)まで「中性すぎ」と棄却して
        // recall を壊す。S≥cap の画素は絶対的に tint を持つ=純白 UV 背景(S≲0.012)ではないので
        // ペナルティ対象にしない。cap は ActivateSat と同じ「絶対中性境界」のスケール。
        internal const float ChromaGateFloorFrac = 0.5f;    // サンプル彩度 sS*frac 未満は「中性すぎ」
        internal const float ChromaGateFloorCap = 0.02f;    // 床の絶対上限。これ以上の tint は中性扱いしない
        internal const float ChromaGatePenalty = 1.0f;      // 最大加算距離(tolerance 単位)

        // ── 彩度天井(グレーモード専用、彩度整合ゲートの鏡像) ──
        // 無彩/微 tint サンプルの「素材彩度包絡」を大きく超える高彩度画素=染められた別素材
        // (クリーム色の布等)を排除するための天井。無彩素材の濃淡・AA は彩度がサンプル近辺に
        // 留まるのに対し、染められた別素材はサンプルよりはるかに高彩度になる、という非対称性
        // で分離する。彩度整合ゲートが「サンプルより中性すぎる画素=UV 背景」を弾くのに対し、
        // こちらは「サンプルより彩度が高すぎる画素=染めた別素材」を弾く。
        // 天井は床ゲートと同じくサンプル相対(sS*Frac)と絶対床(Abs)の大きい方。
        // 適用は 2 経路:
        //   ・主経路: PixelProcessor.ApplyChromaCeilingGate が post-match の空間ゲートとして
        //     適用(低彩度コア近傍の高彩度画素=素材自身の装飾は保護し、独立した別素材だけ落とす)。
        //   ・緩和マッチ(穴埋め/境界回復): GetRelaxedMatchStrength 内で距離加算として適用し、
        //     主経路で落とした別素材を復元経路から逆送しないようにする。
        internal const float ChromaCeilSampleFrac = 3.0f; // 天井 = max(サンプル彩度×この倍率, 絶対床)
        internal const float ChromaCeilAbs = 0.04f;       // 天井の絶対床(真の無彩サンプルでも作動)
        internal const float ChromaCeilPenalty = 1.0f;    // 緩和マッチ側の最大加算距離(tolerance 単位)

        // グレー抽出モードの AA 縁ソフトランプ床(tolerance 比)。edgeSoftness=0 だとグレーモードは
        // 二値マッチになり AA 境界(地色↔背景の混色)も full strength に固まる→デコンタミ
        // (0<s<thr で作動する α 再合成)が走らず、元の滑らかな AA が硬い段差に潰れる。地色コア
        // (rgbDist が hardRange 未満)は full のまま、外側の混色帯に soft ramp を与えて partial
        // strength にし、既存デコンタミが α·target+(1-α)·背景 を忠実復元できるようにする。
        private const float AchromaEdgeSoftness = 0.7f;

        // 連結成分アンカリング(flood fill)の「確信できる地色」の固定半径。matchConf はマッチ距離
        // dist をこの半径で正規化した値(matchConf>0 ⟺ dist<CoreMatchDistance)で、FF はこの確信コアを
        // 含む連結成分だけを残す。**tolerance ではなく固定半径**にするのが要点:
        //   ・tolerance ≤ この半径(タイト) → 全マッチ画素が自動的にコア → FF は何も落とさない
        //     (=取りこぼしゼロ。低 tolerance で正当画素が消える症状を防ぐ)
        //   ・tolerance > この半径(ルーズ) → 色が半径より遠い過選択(別素材の滲み)だけが、確信コアを
        //     持たない別成分として落ちる(=ノイズ除去)。確信コアに連結した陰影/AA は残る。
        // コア判定が tolerance 非依存=安定になり、「tolerance を動かすと挙動が反転する」感覚を解消する。
        // 値はテクスチャ非依存の色距離(ハイブリッド HSV/RGB, [0,1])で、複数被写体の sweep で確定。
        private const float CoreMatchDistance = 0.14f;

        // ── マッチング(MatchOneSample / CalculateHybridDistance)のしきい値定数 ──
        // すべてテクスチャ非依存の比率/正規化係数。
        // RGB 3 次元ユークリッド距離を [0,1] へ正規化する係数 1/√3(=単位立方体対角 √3 の逆数)。
        // グレー抽出モードとハイブリッド距離の RGB 項で共用する。
        // internal: PixelProcessor.RecoverEnclosedNeutral が「ゲートが無ければグレーモードで
        // マッチしていたか」を同じ距離式で再現するため共有する(複製すると定数ドリフトの温床)。
        internal const float InvSqrt3 = 0.57735027f;
        // 同色相とみなすシャドウ/ハイライト免除の色相距離ゲート上限。これを超える色相差は別色として
        // 免除しない(無関係色の巻き込み防止)。免除のオン/オフ判定とフェード分母の双方で使う。
        // internal: ZoneAutoTuner の閉ループ検証(明部ツヤ判定)が同じ「同色相」定義を共有する
        // (手動同期による定数ドリフトを避ける)。
        internal const float ForgivenessHueGate = 0.15f;
        // シャドウ免除を始める明度しきい(源色 V 比)。pV が sV×この値 未満なら「影」とみなす(25%
        // デッドマージン)。1-この値(=0.25)が明部側ヘッドルーム HighlightValueHeadroomFrac の鏡像。
        private const float ShadowValueThresholdFrac = 0.75f;
        // ハイライト免除を始める明度ヘッドルーム(上方 1-sV に対する比)。ShadowValueThresholdFrac の
        // 鏡像(0.25 = 1 - 0.75)で、明暗対称に免除を始める。
        // internal: PixelProcessor.RecoverEnclosedHighlight が「本体の典型明度より明るい周囲(グロー)」の
        // 判定に同じヘッドルーム比を使う(手動同期による定数ドリフトを避ける)。
        internal const float HighlightValueHeadroomFrac = 0.25f;
        // ハイライト経路の同色相帯 = max(Floor, tolerance×TolFrac)。復元マッチの hlHueCap・帯成長
        // (PixelProcessor.GrowHighlightBand)・自動調整の閉ループ模擬(ZoneAutoTuner.VerifyHighlightRecoveryGrowth)
        // が同じ帯を使う前提なので、式の係数を 1 か所で持つ(手動同期による定数ドリフトを避ける)。
        internal const float HighlightHueCapFloor = 0.05f;
        internal const float HighlightHueCapTolFrac = 0.3f;
        // サンプルの chromaConfidence が 0→1 に立ち上がる彩度の幅(chromaThreshold から)。
        // internal: PixelProcessor.RecoverEnclosedHighlight が「色相が完全に信用できる彩度」
        // (chromaThreshold+この値)を彩度床に使う。
        internal const float ChromaConfidenceSpan = 0.10f;
        // 暗すぎる色(黒)は色相がノイズで暴れるので信用しない。サンプルの明度がこの値から +HueValueConfSpan までの
        // 間で chromaConfidence を 0→1 に立ち上げる。internal: 緩和マッチ(PixelProcessor の relaxedChromaConf)が
        // 主経路と同じ式を使うため。
        internal const float HueValueConfLo = 0.05f;
        internal const float HueValueConfSpan = 0.15f;
        // シャドウ/ハイライト免除ランプの幅(利用可能レンジ比)。免除が 0→1 へ立ち上がる区間長。
        // シャドウは sV×この値、ハイライトは (1-sV)×この値。
        private const float ForgivenessRangeFrac = 0.6f;
        // サンプル彩度に対してこの割合未満の画素は無彩寄りで、色相が信用できない(8bit の量子化で大きく振れる)。
        // 自動調整のトーン抽出・証拠ドメインの彩度床(ZoneAutoTuner.AutoToneSatFrac)と、別パーツの色相幅
        // (partHueBand)の「反証にしない画素」の判定で共有する。
        internal const float HueReliableSatFrac = 0.35f;
        // ハイブリッド距離のハイライト距離免除の下限係数(Lerp(1,この値))。同色相・明部で最大
        // 1-0.3=70% まで距離を短縮する。シャドウ側距離短縮は廃止済み(暗部巻き込み防止)のため明部のみ
        // 非対称に温存している。
        private const float BrightDistanceForgivenessMin = 0.3f;
        // グレー抽出モードの有効 chromaThreshold は源色 V で動的に決まる: sV=0 で BaseChromaThreshold、
        // sV>=ChromaConfidenceRamp で zone.chromaThreshold に収束。暗い源色ほど色相/彩度が不安定なため
        // グレーモード適用範囲を広げる。
        // internal: 緩和マッチ経路(PixelProcessor.GetRelaxedMatchStrength)が主経路と同じ動的しきい値を
        // 使うため共有する(以前は 0.30/0.20 を複製焼き込みしていた)。
        internal const float GrayModeBaseChromaThreshold = 0.30f;
        internal const float GrayModeChromaConfidenceRamp = 0.20f;
        // グレー抽出モードで「暗い源色」とみなす V 上限。これ未満では無彩色の彩度(中立逸脱度)を距離
        // 指標に混ぜ(輝度差があっても無彩なら同素材)、彩度整合ゲートの明部限定 gateWeight のフェード
        // 区間 [0,この値] にも使う。
        internal const float GrayModeDarkSampleValue = 0.3f; // internal: 緩和マッチ経路と共有(暗サンプル判定/gateWeight)
        // 輝度盲対策: 暗サンプルのグレーモードは距離を彩度 pS へ寄せて輝度差を捨てるため、
        // 純白(pS=0)まで距離0でマッチしていた。サンプルより「ヘッドルームを超えて明るい」画素へ
        // 輝度超過ペナルティ (pV-sV-headroom)*weight を距離に加える。黒布の中間グレーハイライト
        // (V ≲ sV+headroom)は無罰で拾い、純白/白装飾/UV 背景(超過大)だけを距離で弾く。
        // weight は最暗サンプルでも AchromaTolMax(0.40)を超えて白を弾ける大きさ((1-headroom)*weight>0.40)。
        internal const float GrayHighlightHeadroom = 0.55f;
        internal const float GrayLumExcessWeight   = 1.2f;

        // 【シミュレーション専用】ハイライト(明部)の距離免除・彩度ゲート免除を無効化する。
        // ZoneAutoTuner の閉ループ検証が「免除経路だけで選択される画素」を切り分けて数える
        // ために使う。製品 UI からは変更されず常に false(=免除有効)。シリアライズ対象外。
        [System.NonSerialized] internal bool simDisableBrightForgiveness = false;

        [NonSerialized] private bool _cacheInitiated = false;
        [NonSerialized] private Color _cSampleColor;
        [NonSerialized] private float _cTolerance, _cSatStrictness, _cSatRampScale, _cEdgeSoftness;
        [NonSerialized] private float _cSaturationGuard;
        // chromaThreshold も派生値(SampleCache.chromaConfidence)の入力なので無効化判定に要る。
        // ここに載せ忘れると「値は変えたのに派生値だけ古い」不整合になる（UpdateCacheIfNeeded 参照）。
        [NonSerialized] private float _cChromaThreshold;
        // 遅延キャッシュ構築(ColorZone.Match の安全網)を直列化するゲート。Unity の逆シリアライズで
        // フィールド初期化子が走らない可能性があるため、初期化子ではなく遅延生成する。
        [NonSerialized] private object _cacheGate;
        // extraSamples の内容が変わった場合にキャッシュを再構築するためのスナップショット。
        [NonSerialized] private Color[] _cExtraSamples;

        // サンプルごとに変わる派生値（HSV・彩度ゲート床など）。マルチサンプルでは
        // サンプル数ぶんの配列を持ち、マッチングは全サンプルの最大強度を採る（和集合）。
        // ゾーン全体で共通の値（tolerance・各 range・重み・chromaThreshold 等）は
        // インスタンスフィールドのまま保持する。
        private struct SampleCache
        {
            public Color color;
            public float sH, sS, sV;       // サンプル色の HSV
            public float satMin, satRamp;
            public float chromaConfidence;
            public float saturationGuardFloor; // 0=無効。pS がこの値未満なら hard reject。

            // ── 以下は MatchOneSample が毎画素で再計算していたサンプル定数 ──
            // マッチは「全画素 × 全ゾーン × 全サンプル」で回る最ホットループ(4K・4 サンプルで
            // 6700 万回)なので、サンプル色にしか依存しない値をここへ畳む。式と演算順は元の
            // インライン計算と一字一句同じで、除算は除算のまま残す(逆数化は丸めが変わる)ため
            // 出力はビット不変。
            public bool  isGrayMode;       // sS <= GrayModeEffectiveChromaThreshold(sV, chromaThreshold)
            public bool  isDarkGray;       // sV < GrayModeDarkSampleValue(暗サンプルの明側許容)
            public float darknessFactor;   // Clamp01((GrayModeDarkSampleValue - sV) / GrayModeDarkSampleValue)
            public bool  grayGateActive;   // sS > ChromaGateActivateSat(彩度整合ゲートが作動するか)
            public float grayGateWeight;   // Clamp01(sV / GrayModeDarkSampleValue)
            public float graySatFloor;     // Min(sS * ChromaGateFloorFrac, ChromaGateFloorCap)
            public float graySatFloorDen;  // Max(graySatFloor, 1e-4f)
            public float shadowVThreshold; // sV * ShadowValueThresholdFrac
            public float shadowRangeDen;   // sV * ForgivenessRangeFrac
            public float brightThreshold;  // sV + (1 - sV) * HighlightValueHeadroomFrac
            public float brightRangeDen;   // Max(0.01f, (1 - sV) * ForgivenessRangeFrac)
            public bool  hueTrusted;       // 彩度の上で chromaConfidence が 1(sS >= chromaThreshold + ChromaConfidenceSpan)
        }
        [NonSerialized] private SampleCache[] _sampleCaches;

        [NonSerialized] private float softRange, hardRange;
        [NonSerialized] private float hlHueCap;
        // グレーモードの AA 縁レンジと hueRelevance の分母。どちらも tolerance/edgeSoftness/
        // chromaThreshold だけに依存する(いずれも UpdateCacheIfNeeded の無効化条件に入っている)。
        [NonSerialized] private float aaSoftRange, aaHardRange;
        [NonSerialized] private float chromaThresholdFloor;

        static partial void ResetMatchCache(ColorZone clone)
        {
            clone._cExtraSamples = null;
            clone._sampleCaches = null;
            clone._cacheInitiated = false;
        }

        /// <summary>
        /// セッション開始時などに明示的にキャッシュを更新する場合に呼び出します。
        /// GetMatchScoresPrecomputedHSV を呼ぶ前に必ず呼ぶこと。キャッシュ未構築のときだけ
        /// GetColorMatchScores の安全網(ロック付き)が構築するが、構築後のパラメータ変更は検知しない。
        /// </summary>
        public void UpdateCacheIfNeeded()
        {
            if (_cacheInitiated &&
                _cSampleColor == sampleColor &&
                _cTolerance == tolerance &&
                _cSatStrictness == saturationStrictness &&
                _cSatRampScale == satRampScale &&
                _cEdgeSoftness == edgeSoftness &&
                _cSaturationGuard == saturationGuard &&
                _cChromaThreshold == chromaThreshold &&
                !ExtraSamplesChanged())
            {
                return;
            }

            _cSampleColor = sampleColor;
            _cTolerance = tolerance;
            _cSatStrictness = saturationStrictness;
            _cSatRampScale = satRampScale;
            _cEdgeSoftness = edgeSoftness;
            _cSaturationGuard = saturationGuard;
            _cChromaThreshold = chromaThreshold;
            _cacheInitiated = true;

            // サンプルごとの派生値を構築（主サンプル + 内部サンプル(extraSamples)）。
            int extraN = extraSamples?.Count ?? 0;
            _sampleCaches = new SampleCache[1 + extraN];
            _sampleCaches[0] = BuildSampleCache(sampleColor);
            for (int i = 0; i < extraN; i++)
                _sampleCaches[i + 1] = BuildSampleCache(extraSamples[i]);
            // 変更検知用にスナップショットを取る。
            if (_cExtraSamples == null || _cExtraSamples.Length != extraN)
                _cExtraSamples = new Color[extraN];
            for (int i = 0; i < extraN; i++) _cExtraSamples[i] = extraSamples[i];

            // ハイライト復元(グレーモードの highlightPotential と CalculateHighlightRecovery)も同じレンジを使う。
            softRange = tolerance * edgeSoftness;
            hardRange = tolerance - softRange;

            hlHueCap = Mathf.Max(HighlightHueCapFloor, tolerance * HighlightHueCapTolFrac);

            // ホットループから追い出したゾーン定数(式は MatchOneSample のインライン計算と同一)。
            // softRange 確定後に計算する必要があるのでここに置く。
            aaSoftRange = Mathf.Max(softRange, _cTolerance * AchromaEdgeSoftness);
            aaHardRange = _cTolerance - aaSoftRange;
            chromaThresholdFloor = Mathf.Max(0.01f, chromaThreshold);
        }

        // 遅延キャッシュ構築の直列化用ゲート。フィールド初期化子に頼らず遅延生成する
        // （Unity の逆シリアライズで初期化子が走らない経路があっても null にならないように）。
        private object CacheGate
        {
            get
            {
                if (_cacheGate != null) return _cacheGate;
                System.Threading.Interlocked.CompareExchange(ref _cacheGate, new object(), null);
                return _cacheGate;
            }
        }

        // extraSamples の内容が前回キャッシュ時と変わったか（個数・各色）。
        private bool ExtraSamplesChanged()
        {
            int n = extraSamples?.Count ?? 0;
            int cn = _cExtraSamples?.Length ?? 0;
            if (n != cn) return true;
            for (int i = 0; i < n; i++)
                if (_cExtraSamples[i] != extraSamples[i]) return true;
            return false;
        }

        // 1 サンプル分の派生キャッシュを計算する。ゾーン共通の倍率
        // （saturationStrictness/satRampScale/chromaThreshold/saturationGuard）を使うので、
        // 主サンプルに対しては従来の単一サンプル計算と完全に一致する（＝後方互換）。
        private SampleCache BuildSampleCache(Color c)
        {
            SampleCache sc;
            sc.color = c;
            Color.RGBToHSV(c, out sc.sH, out sc.sS, out sc.sV);

            // 彩度ガード床: 源色が高彩度なときだけ正値になる。
            // 既定 saturationGuard=0 では常に 0（=機能無効）で従来動作と完全互換。
            sc.saturationGuardFloor = (saturationGuard > 0f && sc.sS >= SaturationGuardActiveSourceSat)
                ? sc.sS * SaturationGuardFractionScale * saturationGuard
                : 0f;

            sc.satMin = Mathf.Max(0.02f, sc.sS * saturationStrictness);
            sc.satRamp = Mathf.Max(0.08f, sc.sS * satRampScale);

            float currentChromaHi = chromaThreshold + ChromaConfidenceSpan;
            float baseChromaConf = Mathf.Clamp01((sc.sS - chromaThreshold) / ((currentChromaHi) - chromaThreshold));
            // 暗すぎる色（黒）は彩度データが高くても色相（Hue）の計算がノイズで暴れるため信用しない
            float valueConf = Mathf.Clamp01((sc.sV - HueValueConfLo) / HueValueConfSpan); // Vが0.05(非常に暗い)〜0.20の範囲で減衰
            sc.chromaConfidence = Mathf.Min(baseChromaConf, valueConf);

            // ── ホットループから追い出したサンプル定数(式・演算順は MatchOneSample と同一) ──
            sc.isGrayMode = sc.sS <= GrayModeEffectiveChromaThreshold(sc.sV, chromaThreshold);
            sc.isDarkGray = sc.sV < GrayModeDarkSampleValue;
            sc.darknessFactor = Mathf.Clamp01((GrayModeDarkSampleValue - sc.sV) / GrayModeDarkSampleValue);
            sc.grayGateActive = sc.sS > ChromaGateActivateSat;
            sc.grayGateWeight = Mathf.Clamp01(sc.sV / GrayModeDarkSampleValue);
            sc.graySatFloor = Mathf.Min(sc.sS * ChromaGateFloorFrac, ChromaGateFloorCap);
            sc.graySatFloorDen = Mathf.Max(sc.graySatFloor, 1e-4f);
            sc.shadowVThreshold = sc.sV * ShadowValueThresholdFrac;
            sc.shadowRangeDen = sc.sV * ForgivenessRangeFrac;
            sc.brightThreshold = sc.sV + (1f - sc.sV) * HighlightValueHeadroomFrac;
            sc.brightRangeDen = Mathf.Max(0.01f, (1f - sc.sV) * ForgivenessRangeFrac);
            sc.hueTrusted = baseChromaConf >= 1f;
            return sc;
        }

        /// <summary>
        /// HSV が事前計算済みの場合に使うバリアント。ColorPick モード専用。
        /// キャッシュは呼び出し前に UpdateCacheIfNeeded() で更新しておくこと。
        /// </summary>
        public void GetMatchScoresPrecomputedHSV(
            float pH, float pS, float pV, Color pixelColor,
            int x, int y, int texWidth, int texHeight,
            out float strength, out float highlightPot, out float matchConf)
        {
            strength = 0f;
            highlightPot = 0f;
            matchConf = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot, out matchConf);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                    {
                        strength = 1f;
                        matchConf = 1f; // 矩形選択は確定領域=完全確信
                    }
                    break;
            }
        }

        // マルチサンプルの和集合マッチング。全サンプル（主＋内部サンプル(extraSamples)）に対して
        // 1 サンプル分のマッチを計算し、最大強度を採る。サンプルが 1 個なら従来の
        // 単一サンプル計算と完全に一致する（内部サンプル(extraSamples)が無い限り出力はビット不変）。
        private void GetColorMatchScores(Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            matchConf = 0f;

            var caches = _sampleCaches;
            if (caches == null || caches.Length == 0)
            {
                // UpdateCacheIfNeeded 未実行時の安全網（通常は到達しない）。
                // この関数は Parallel.For の中から呼ばれるため、無同期で共有 _sampleCaches を
                // 作り直すと他スレッドが構築途中の配列を読み得た（安全網自体が危険。レビュー §4 中）。
                // ゲートで直列化する。主経路はループ前に初期化済みなので、ここは通らない。
                lock (CacheGate)
                {
                    UpdateCacheIfNeeded();
                    caches = _sampleCaches;
                }
            }

            for (int si = 0; si < caches.Length; si++)
            {
                MatchOneSample(in caches[si], pixelColor, pH, pS, pV, out float s, out float hPot, out float mc);
                if (s > strength) strength = s;
                if (hPot > highlightPotential) highlightPotential = hPot;
                if (mc > matchConf) matchConf = mc;
            }
        }

        /// <summary>
        /// グレーモード発動しきい値。暗いサンプルほどグレースケールモードの適用範囲を動的に広げる
        /// (sV=0 で GrayModeBaseChromaThreshold、sV>=Ramp で chromaThreshold に収束)。
        /// 主経路(MatchOneSample)・緩和経路(GetRelaxedMatchStrength)・後段ゲート
        /// (ApplyChromaCeilingGate / RecoverEnclosedNeutral)の 4 箇所が同式をインライン複製して
        /// いたため一本化(式・演算順は従来と同一=出力ビット不変)。
        /// </summary>
        internal static float GrayModeEffectiveChromaThreshold(float sV, float chromaThreshold)
            => Mathf.Lerp(GrayModeBaseChromaThreshold, chromaThreshold, Mathf.Clamp01(sV / GrayModeChromaConfidenceRamp));

        /// <summary>
        /// 彩度天井(グレーモード専用)。ゾーンの chromaCeiling が正ならそれを、0(自動)なら
        /// max(サンプル彩度 × ChromaCeilSampleFrac, ChromaCeilAbs) を返す。主経路の後段ゲート
        /// (PixelProcessor.ApplyChromaCeilingGate)と緩和マッチ(GetRelaxedMatchStrength)が同じ式を
        /// 使う(片方だけ上書きが効くと、主経路で落とした別素材を穴埋めが復元してしまう)。
        /// </summary>
        internal static float EffectiveChromaCeiling(float sS, float chromaCeiling)
            => chromaCeiling > 0f ? chromaCeiling : Mathf.Max(sS * ChromaCeilSampleFrac, ChromaCeilAbs);

        /// <summary>
        /// グレーモードの彩度整合ゲート(微 tint サンプルより著しく中性寄りの画素への距離加算)。
        /// 非作動(sS が ActivateSat 以下)なら 0。toleranceScale には主経路が _cTolerance、
        /// 緩和経路が tolerance を渡す。主経路と緩和経路が同ブロックを複製していたため一本化
        /// (式・演算順は従来と同一=出力ビット不変)。
        /// </summary>
        internal static float GrayChromaGatePenalty(float sS, float sV, float pS, float toleranceScale)
        {
            if (sS <= ChromaGateActivateSat) return 0f;
            float gateWeight = Mathf.Clamp01(sV / GrayModeDarkSampleValue);
            float satFloor = Mathf.Min(sS * ChromaGateFloorFrac, ChromaGateFloorCap);
            float shortfall = Mathf.Clamp01((satFloor - pS) / Mathf.Max(satFloor, 1e-4f));
            return shortfall * ChromaGatePenalty * toleranceScale * gateWeight;
        }

        // 1 サンプル分のマッチ強度／ハイライト候補を計算する。サンプル依存の値は sc から、
        // ゾーン共通の値（tolerance・各 range・重み・閾値）はインスタンスフィールドから読む。
        private void MatchOneSample(in SampleCache sc, Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            // matchConf: 連結成分アンカリング(flood fill)のコア判定専用の「色一致の確信度」。
            // strength は edgeSoftness=0 だと tolerance 内で二値(=色距離を反映しない)・有彩では
            // 彩度ゲートのみを反映するため、コア判定に使うと「色は遠いが彩度が高い別素材」を
            // コア扱いしてしまう。matchConf は固定半径 CoreMatchDistance で正規化した連続距離
            // (1=サンプル色に一致, 0=半径以遠)で、tolerance に依存せず「色がどれだけ近いか」を表す。
            // 選択/出力には一切使わない(FF OFF ではビット不変)。
            matchConf = 0f;

            // 彩度ガード: 源色が高彩度なときだけ作動し、白/黒/灰色など無彩色寄りの
            // 画素を hard reject する。源色 S が低い（グレー/黒）の場合は
            // saturationGuardFloor が 0 になり、このゲートは作動しない（自動無効）。
            if (sc.saturationGuardFloor > 0f && pS < sc.saturationGuardFloor)
            {
                return;
            }

            // サンプル色の彩度がしきい値以下の場合は、自動的に無彩色(グレー/黒)抽出モードとして扱う
            // (判定は BuildSampleCache で確定済み。同じ Lerp+Clamp01 を毎画素回していた)
            if (sc.isGrayMode)
            {
                // グレー抽出モード：HueやSatを完全に無視し、純粋なRGBの近さのみで判定する
                float dr = pixelColor.r - sc.color.r;
                float dg = pixelColor.g - sc.color.g;
                float db = pixelColor.b - sc.color.b;
                float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * InvSqrt3;

                // 暗いサンプル（黒〜暗グレー）の明るい側許容:
                // 黒いファブリックは表面の凹凸・照明により中間グレーのハイライトを持つが同じマテリアル。
                // サンプルが暗いほど、無彩色ピクセルの彩度（≒中立からの逸脱度）を距離指標として使い、
                // 輝度差があっても無彩色なら「同素材」とみなせるようにする。
                float effectiveDist = rgbDist;
                if (sc.isDarkGray)
                {
                    effectiveDist = Mathf.Lerp(rgbDist, pS, sc.darknessFactor);
                    // 輝度盲対策: pS へ寄せると輝度差を捨て純白(pS=0)まで距離0でマッチする。
                    // ヘッドルームを超えて明るい画素に輝度超過ペナルティを加え、白装飾/UV 背景を弾く。
                    // 中間グレーハイライト(V ≲ sV+headroom)は超過0で無罰=recall 維持。
                    float lumExcess = Mathf.Max(0f, (pV - sc.sV) - GrayHighlightHeadroom);
                    effectiveDist += lumExcess * GrayLumExcessWeight;
                }

                // 彩度整合ゲート: サンプルが微小な tint を持つ(sS>ActivateSat)ときのみ作動。
                // サンプル彩度の相対床 sS*FloorFrac を下回る中性画素(純白背景等)に距離を加算し、
                // pS=0 では確実に tolerance 超え→strength 0 に落とす。AA縁(tint一部残存)は連続的な
                // 部分ペナルティで崖を作らない。sS≈0(真の無彩サンプル)では作動しない。
                // 明部限定(gateWeight=clamp(sV/0.3)): 暗いサンプルは上の分岐で pS を距離指標に使い
                // 「中性=同素材」とみなす(暗布は中性が正常)ため、中性を罰するこのゲートと矛盾する。
                // 暗いサンプルではフェードさせ、明るい tint 素材(生成り・オフホワイトの布地等)でのみ
                // 全効果にする。
                // GrayChromaGatePenalty と同一(ゲート判定・gateWeight・satFloor はサンプル定数なので
                // キャッシュから読む)。乗算の結合順は元のまま保つ。非作動時は元コードが 0f を
                // 加算していただけなので、分岐で飛ばしても値は同じ。
                if (sc.grayGateActive)
                {
                    float shortfall = Mathf.Clamp01((sc.graySatFloor - pS) / sc.graySatFloorDen);
                    effectiveDist += shortfall * ChromaGatePenalty * _cTolerance * sc.grayGateWeight;
                }

                // 彩度天井(高彩度の別素材排除)は主経路では距離加算しない。
                // 部分強度への格下げは陰影相関(form_fidelity)を壊すことが判明したため、
                // 天井の適用は PixelProcessor.ApplyChromaCeilingGate が post-match の
                // 空間ゲート(低彩度コア近接保護付きの除去)として行う。
                // 緩和マッチ(穴埋め/境界回復)側には逆送防止として距離加算版を残す。

                // AA 縁の忠実復元: 外側の混色帯に soft ramp を与え partial strength にして、
                // 後段デコンタミ(α 再合成)が元の滑らかな AA を復元できるようにする(脚色でなく
                // 元の AA カバレッジの復元)。地色コアは hardRange 未満で full のまま=陰影は不変。
                // ユーザーが edgeSoftness を上げている場合はそちらを尊重(floor として作用)。
                strength = CalculateEdgeStrength(effectiveDist, aaHardRange, aaSoftRange);
                // FF コア判定用: グレーモードの色一致確信度(中性ペナルティ込み effectiveDist を使う)。
                if (strength > 0f) matchConf = Mathf.Clamp01(1f - effectiveDist / CoreMatchDistance);
                // ハイライト復元は輝度で判定するが、彩度ゲートを付ける。グレー/黒素材の
                // ハイライトはグレー/白(低彩度)なので、明るい高彩度画素(隣接する別の有彩素材)を
                // highlightPotential から除外する。これが無いと明るいグレーサンプルの隣にある有彩
                // トリムが PropagateHighlights でコアから滲む。有彩版 CalculateHighlightRecovery の
                // pS>=HighlightSaturationMax 棄却と同じ趣旨(無彩サンプルには意味ある sample hue が
                // 無いので彩度ゲートのみ)。
                if (highlightRecovery && pV > HighlightValueMin && pS < HighlightSaturationMax)
                {
                    float vDist = Mathf.Abs(pV - sc.sV);
                    highlightPotential = CalculateEdgeStrength(vDist, hardRange, softRange);
                }
                return;
            }

            float satConfidence = Mathf.Clamp01((pS - sc.satMin) / sc.satRamp);
            float hDist = CalculateHueDistance(pH, sc.sH);
            float sRatio = (sc.sS > 0.01f) ? Mathf.Clamp01(pS / sc.sS) : 1f;

            // 無彩色領域でのHueのバタつきを緩和する
            float maxSat = Mathf.Max(pS, sc.sS);
            float hueRelevance = Mathf.Clamp01(maxSat / chromaThresholdFloor);
            float effectiveHDist = hDist * hueRelevance;

            // 同系色・暗部のシャドウ許容（暗い影の部分は彩度や明度が落ちるが、同じ色として拾う）
            if (pV < sc.shadowVThreshold && effectiveHDist < ForgivenessHueGate)
            {
                float darkForgiveness = Mathf.Clamp01((sc.shadowVThreshold - pV) / sc.shadowRangeDen);

                // 1. 色相(Hue)が離れているほど免除を弱くする（ノイズによる無関係な色の巻き込み防止）
                float hueFactor = 1f - (effectiveHDist / ForgivenessHueGate);
                darkForgiveness *= hueFactor;

                // 2. サンプルが有彩色の場合、対象の彩度が低すぎる(グレー/黒に近い)と免除を減衰
                if (sc.sS > chromaThreshold)
                {
                    float satFactor = Mathf.Clamp01(pS / Mathf.Max(0.01f, shadowForgivenessSatMin));
                    darkForgiveness *= satFactor;
                }

                // 暗いほど、本来の彩度ゲート（satMin）を無視して拾いやすくする
                satConfidence = Mathf.Max(satConfidence, darkForgiveness);
            }
            // 同系色・明部のハイライト許容（上のシャドウ許容の対称形）。
            // 光が強く当たった部分は同じマテリアルでも明度が上がり彩度が抜けて
            // 白っぽくなる（手描きハイライトの芯）。サンプルより明るく同色相なら
            // 彩度ゲートを免除して同素材として拾う。FP は色相ゲートで抑える。
            // シャドウ側の satFactor 減衰は付けない（ハイライトは低彩度化が正常で
            // 暗部のグレー/黒混入とは性質が逆のため）。
            // 免除量は距離免除(CalculateHybridDistance)と発動条件・式・入力が完全に同一なので、
            // ここで 1 回だけ求めて渡す(従来は同じ Clamp01+除算+色相減衰を 2 回評価していた)。
            // 色のはっきりしたサンプル(sc.hueTrusted)では、色味の無い画素(彩度 ChromaGateActivateSat 未満
            // = 純白・純灰)を免除しない。色相が定まらず(RGBToHSV は R=G=B で 0 = 赤を返す)、色相ゲートが
            // 「サンプルの色相が赤に近いか」だけで通ってしまい、肌・茶髪・赤い布のような暖色のサンプルで
            // だけ白い背景まで同素材として拾っていた。サンプル自体が白に近い(色味がわずかな)ときは、純白は
            // その素材の地・ツヤなので従来どおり免除する(外すと白い布の模様の縁が縁の混色の扱いに回り、
            // サンプル色に置き換えられて濃い筋になった)。
            float brightForgiveness = 0f;
            bool brightActive = !simDisableBrightForgiveness && pV > sc.sV
                                && (pS >= ChromaGateActivateSat || !sc.hueTrusted)
                                && effectiveHDist < ForgivenessHueGate;
            if (brightActive)
            {
                // 明度の伸び量を上方ヘッドルーム (1 - sV) で正規化。
                // 閾値 sV + (1-sV)*0.25 は暗側 sV*0.75（25% デッドマージン）の鏡像。
                brightForgiveness = Mathf.Clamp01((pV - sc.brightThreshold) / sc.brightRangeDen);

                // 色相が離れているほど免除を弱くする（暗側と同形・無関係色の巻き込み防止）
                float hueFactor = 1f - (effectiveHDist / ForgivenessHueGate);
                brightForgiveness *= hueFactor;

                satConfidence = Mathf.Max(satConfidence, brightForgiveness);
            }
            float dist = CalculateHybridDistance(in sc, pixelColor, pS, pV, effectiveHDist, sRatio,
                                                 brightActive, brightForgiveness);
            float gate = Mathf.Lerp(1f, satConfidence, sc.chromaConfidence);

            strength = CalculateEdgeStrength(dist, hardRange, softRange) * gate;
            // FF コア判定用: 有彩モードの色一致確信度。strength は彩度ゲートを掛けるため色の近さを
            // 表さない。dist(実マッチ距離)を「確信できる地色」の固定半径 CoreMatchDistance で正規化。
            if (strength > 0f) matchConf = Mathf.Clamp01(1f - dist / CoreMatchDistance);

            if (highlightRecovery)
            {
                highlightPotential = CalculateHighlightRecovery(in sc, pH, pS, pV, effectiveHDist, sRatio);
            }
        }

        private float CalculateHueDistance(float pixelH, float sampleH)
        {
            float hDist = Mathf.Abs(pixelH - sampleH);
            return hDist > 0.5f ? 1f - hDist : hDist;
        }

        // brightActive / brightForgiveness は呼び出し側(MatchOneSample)が算出済みの値を渡す。
        // 旧版はここで同じ条件式と同じ免除量をもう一度計算していた(完全に同一入力の重複評価)。
        private float CalculateHybridDistance(in SampleCache sc, Color pixelColor, float pS, float pV, float hDist, float sRatio,
                                              bool brightActive, float brightForgiveness)
        {
            float sDist = Mathf.Abs(pS - sc.sS);
            float vDist = Mathf.Abs(pV - sc.sV);
            float hsvDist = hDist + sDist * satDistWeight + vDist * valueWeight * (1f - sRatio);

            float dr = pixelColor.r - sc.color.r;
            float dg = pixelColor.g - sc.color.g;
            float db = pixelColor.b - sc.color.b;

            // 距離の近似として平方根を残すが、共通して使うことで計算量を抑制できる
            float rgbDist = Mathf.Sqrt(dr * dr + dg * dg + db * db) * InvSqrt3;

            float finalDist = Mathf.Lerp(rgbDist, hsvDist, sc.chromaConfidence);

            // シャドウ（暗い色）の距離許容は廃止。距離短縮(dist*=Lerp(1,0.3,df))は、同色相だが彩度の
            // 低い near-black の別マテリアル(S≈0.40/V≈0.09 程度の暗い布地など)を tolerance 内へ逆送し
            // 巨大な巻き込みを生む主因だった。検証した全被写体で取りこぼしの救済には寄与せず(除去しても
            // recall は不変)、はみ出しだけが大幅に減った。暗部の取りこぼし救済は彩度ゲート緩和
            // (GetColorMatchScores の satConfidence 底上げ)で代替する(in-tolerance 画素にしか効かず安全)。
            // 明部(ハイライト)免除はベタ塗り対策で性質が逆のため温存=非対称は意図的。

            // ハイライト（明部）の距離許容: 上のシャドウ許容の対称形。
            // サンプルより明るく同色相なら、低彩度化したハイライト芯でも同素材として
            // 距離を免除する。免除の上限は BrightDistanceForgivenessMin（最大 70% 短縮。廃止したシャドウ側と同じ値）。
            if (brightActive)
                finalDist *= Mathf.Lerp(1f, BrightDistanceForgivenessMin, brightForgiveness);

            return finalDist;
        }

        private float CalculateHighlightRecovery(in SampleCache sc, float pH, float pS, float pV, float hDist, float sRatio)
        {
            if (pV <= HighlightValueMin || pS >= HighlightSaturationCeiling(sc.sS) || hDist > hlHueCap)
                return 0f;

            float relaxedSatConf = Mathf.Clamp01((pS - HighlightRelaxedSatMin) / HighlightRelaxedSatRamp);
            if (relaxedSatConf <= 0f)
                return 0f;

            float vDist = Mathf.Abs(pV - sc.sV);
            float highlightDist = hDist + vDist * valueWeight * (1f - sRatio);

            if (highlightDist >= _cTolerance)
                return 0f;

            float hlStrength = CalculateEdgeStrength(highlightDist, hardRange, softRange);
            return hlStrength * relaxedSatConf;
        }

        private float CalculateEdgeStrength(float distance, float hRange, float sRange)
        {
            if (distance >= _cTolerance) return 0f;
            if (sRange < 0.0001f || distance <= hRange) return 1f;
            return 1f - (distance - hRange) / sRange;
        }

        private bool IsInRect(int x, int y, int texWidth, int texHeight)
        {
            float u = (float)x / texWidth;
            float v = (float)y / texHeight;
            return uvRect.Contains(new Vector2(u, v));
        }
    }
}
