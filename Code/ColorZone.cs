// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca
{
    public enum SelectionMode
    {
        ColorPick,
        Rect
    }

    [Serializable]
    public class ColorZone
    {
        // ハイライト補助マッチで対象ピクセルとみなす「鏡面反射/ハイライトらしさ」の閾値。
        private const float HighlightValueMin = 0.80f;
        private const float HighlightSaturationMax = 0.20f;
        private const float HighlightRelaxedSatMin = 0.02f;
        private const float HighlightRelaxedSatRamp = 0.08f;

        // 彩度ガード: 源色が高彩度なときだけ作動し、対象ピクセルの彩度が
        // 「源色 S × FractionScale × saturationGuard」を下回ったら hard reject する。
        // 源色 S が ActiveSourceSat 未満（灰色寄り）のときは作動しない（自動無効）。
        private const float SaturationGuardActiveSourceSat = 0.40f;
        private const float SaturationGuardFractionScale = 0.30f;

        // 彩度整合ゲート(グレー抽出モード専用): サンプルが微小な tint(sS)を持つとき、
        // それより著しく中性(無彩)寄りの候補ピクセル(=純白 UV 背景など)を別マテリアルと
        // みなし、サンプル彩度に対する相対床を下回る分だけ距離を加算してソフトに排除する。
        // 純RGB距離だけでは、サンプル(クリーム)と純白が近接(距離~0.03)し、暗い領域を拾う
        // ための大きな tolerance が純白も巻き込む。tint の有無で両者は明確に分離できる。
        // サンプル自身が真の無彩(sS≈0)なら作動せず=従来の純RGB距離挙動を完全維持。
        private const float ChromaGateActivateSat = 0.02f; // この tint 未満のサンプルでは無効
        private const float ChromaGateFloorFrac = 0.5f;    // サンプル彩度 sS*frac 未満は「中性すぎ」
        private const float ChromaGatePenalty = 1.0f;      // 最大加算距離(tolerance 単位)

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
        private const float InvSqrt3 = 0.57735027f;
        // 同色相とみなすシャドウ/ハイライト免除の色相距離ゲート上限。これを超える色相差は別色として
        // 免除しない(無関係色の巻き込み防止)。免除のオン/オフ判定とフェード分母の双方で使う。
        private const float ForgivenessHueGate = 0.15f;
        // シャドウ免除を始める明度しきい(源色 V 比)。pV が sV×この値 未満なら「影」とみなす(25%
        // デッドマージン)。1-この値(=0.25)が明部側ヘッドルーム HighlightValueHeadroomFrac の鏡像。
        private const float ShadowValueThresholdFrac = 0.75f;
        // ハイライト免除を始める明度ヘッドルーム(上方 1-sV に対する比)。ShadowValueThresholdFrac の
        // 鏡像(0.25 = 1 - 0.75)で、明暗対称に免除を始める。
        private const float HighlightValueHeadroomFrac = 0.25f;
        // シャドウ/ハイライト免除ランプの幅(利用可能レンジ比)。免除が 0→1 へ立ち上がる区間長。
        // シャドウは sV×この値、ハイライトは (1-sV)×この値。
        private const float ForgivenessRangeFrac = 0.6f;
        // ハイブリッド距離のハイライト距離免除の下限係数(Lerp(1,この値))。同色相・明部で最大
        // 1-0.3=70% まで距離を短縮する。シャドウ側距離短縮は廃止済み(暗部巻き込み防止)のため明部のみ
        // 非対称に温存している。
        private const float BrightDistanceForgivenessMin = 0.3f;
        // グレー抽出モードの有効 chromaThreshold は源色 V で動的に決まる: sV=0 で BaseChromaThreshold、
        // sV>=ChromaConfidenceRamp で zone.chromaThreshold に収束。暗い源色ほど色相/彩度が不安定なため
        // グレーモード適用範囲を広げる。
        private const float GrayModeBaseChromaThreshold = 0.30f;
        private const float GrayModeChromaConfidenceRamp = 0.20f;
        // グレー抽出モードで「暗い源色」とみなす V 上限。これ未満では無彩色の彩度(中立逸脱度)を距離
        // 指標に混ぜ(輝度差があっても無彩なら同素材)、彩度整合ゲートの明部限定 gateWeight のフェード
        // 区間 [0,この値] にも使う。
        private const float GrayModeDarkSampleValue = 0.3f;


        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        // カラーピックモード
        public Color sampleColor = Color.white;
        public float tolerance = 0f;

        // マルチサンプル選択用の内部サンプル（暗部／中間／明部の代表色）。空＝単一サンプルと等価。
        // ★ユーザーが手で追加するものではない★ — 自動調整（ZoneAutoTuner.DeriveAutoTonalSamples）が
        // スポイト1点から「同色相パーツのトーン分布」を走査して自動生成し、RunAutoTune が設定する。
        // 単一サンプル＋大きな許容範囲は濃淡を覆おうとして隣接パーツまで巻き込みやすい（誤爆の主因）。
        // 代わりにパーツの濃い所・薄い所の代表色を中心とした小さな許容範囲の「和集合」でパーツ全体を
        // 捉えることで、precision（はみ出し低減）と recall（取りこぼし低減）を同時に高める。
        //   sampleColor   … 主サンプル（スポイト点）。再着色アンカー（＝出力色マッピング）に使う。
        //   extraSamples  … 自動生成の内部サンプル。選択（マッチング）だけを広げ、出力色には影響しない。
        // 出力を主サンプル基準に固定するのは autoRecolorAnchor と同じ「選択と出力の分離」方針に沿う。
        public List<Color> extraSamples = new List<Color>();

        // 矩形モード（UV座標0-1）
        public Rect uvRect = new Rect(0, 0, 1, 1);

        // Flood Fill（連続領域モード／連結成分アンカリング）: ColorPick モードで有効。
        // 既定 ON＝標準挙動。確信度の高い芯を含む連結領域だけに変換を絞り込み、物理的に離れた
        // 同色パーツや背景へのにじみ（誤爆）を自動除去する。シード不要の自動アンカリングが既定で、
        // クリーンな tolerance では OFF と出力ビット等価（効くのは tolerance を盛りすぎた時）。
        // 通常モードのトグルで OFF に戻せる。
        public bool useFloodFill = true;
        // シード点のUV座標（0-1）。負値 = 未設定
        public Vector2 seedUV = new Vector2(-1f, -1f);

        [Range(0f, 0.5f)]
        public float edgeStopThreshold = 0.15f;

        // 変更先
        public Color targetColor = Color.white;

        [Range(0f, 1f)]
        public float valueBlend = 1f;

        // 出力彩度スケール（1.0=従来の鮮やかさ維持）。1.0未満で再着色後の彩度を比例的に下げ、
        // 純色 target でも明度グラデーション（立体感）が出るようにする。
        [Range(0f, 1f)]
        public float outputSaturation = 1f;

        [Range(0f, 1f)]
        public float edgeSoftness = 0f;

        [Range(0f, 1f)]
        public float saturationStrictness = 0.50f;

        [Range(0f, 1f)]
        public float valueWeight = 1.0f;

        [Range(0f, 1f)]
        public float satDistWeight = 0.15f;

        [Range(0.01f, 0.5f)]
        public float satRampScale = 0.10f;

        [Range(0f, 1f)]
        public float shadowDesaturation = 0.35f;

        [Range(0f, 1f)]
        public float shadowForgivenessSatMin = 0.05f;

        [Range(0f, 1f)]
        public float chromaThreshold = 0.05f;

        [Range(0f, 1f)]
        public float saturationGuard = 0f;

        public bool highlightRecovery = true;
        public bool highlightBandExpand = true;
        // ハイライト白寄せ合成: 明部(明度>サンプル)を「wash→白 軸」へ射影し、鏡面ハイライトを
        // 表現する。既定 OFF（オプトイン）。OFF のときは色相転送(HSV transfer)のみで、明部の
        // 明度・彩度構造はそのまま温存される。ON でも有彩の模様は軸残差フェードで保護され、
        // 軸上の真の鏡面のみが白寄せされる。Python 参照の apply_highlight_wash と同期。
        public bool applyHighlightWash = false;
        // 俯瞰スポイト補正: ハイライト白寄せ合成(applyHighlightWash)用サンプルの明度を、テクスチャの
        // 地色まで自動で下げる配下オプション。明るい光沢部をスポイトしても鏡面グラデが潰れない。
        // match/base は不変＝再着色範囲は変えない。applyHighlightWash が ON のときだけ作用する。
        // 既定 OFF。房の多いテクスチャ（髪など）では OFF が望ましいことがある。
        public bool autoHighlightSample = false;
        // サンプル自動補正(再着色アンカー正規化): OkLab 再着色のアンカー (sL, sC) を、スポイト
        // 画素ではなくマッチ領域の統計(明部の地色)から自動推定する。スポイトを陰影のどの明るさで
        // 取ってもパーツの明部が target 色に一致する(サンプル位置非依存)。マッチング・wash は
        // スポイト色のまま＝再着色範囲は不変。Python 参照の auto_recolor_anchor /
        // estimate_anchor_oklab と同期。既定 ON(オプトアウト): 影をスポイトしても出力が過度に
        // 明るく/ベタ塗りにならないよう、位置非依存で代表地色を target 明度へ合わせる。クリック
        // 画素を厳密に target 色へ当てたい/意図的に明るく塗りたいゾーンだけ OFF にする。旧プリセット
        // JSON で明示保存された値は尊重(マイグレーションなし)。フィールド欠落の旧 JSON は新既定 true。
        public bool autoRecolorAnchor = true;
        // [非推奨] 旧: 処理順を表す数値。現在は「リストの並び順＝優先度(先頭が最優先)」に
        // 変更したため未使用。古いプリセット JSON の後方互換のためフィールドのみ残す
        // (PresetsView.MigrateLegacyLayerPriority が読み込み時に一度だけ降順移行に使用)。
        public int layerIndex = 0;
        public string id = "";

        // === 事前計算キャッシュ ===
        [NonSerialized] private bool _cacheInitiated = false;
        [NonSerialized] private Color _cSampleColor;
        [NonSerialized] private float _cTolerance, _cSatStrictness, _cSatRampScale, _cEdgeSoftness;
        [NonSerialized] private float _cSaturationGuard;
        // extraSamples の変更検知用スナップショット（内容が変わったらキャッシュを作り直す）。
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
        }
        [NonSerialized] private SampleCache[] _sampleCaches;

        // ゾーン共通のキャッシュ値（サンプルに依存しない）
        [NonSerialized] private float softRange, hardRange;
        [NonSerialized] private float hlHueCap;
        [NonSerialized] private float hlSoftRange, hlHardRange;

        public void EnsureId()
        {
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// このゾーンのコピーを返します。
        /// [NonSerialized] のキャッシュフィールドもコピーされますが、
        /// 値の変更は UpdateCacheIfNeeded で再計算されるため安全です。
        /// extraSamples は可変リストなので、クローン間で共有しないよう深いコピーにし、
        /// キャッシュのスナップショット参照も切り離して各クローンが独立に再計算するようにします。
        /// </summary>
        public ColorZone Clone()
        {
            var c = (ColorZone)MemberwiseClone();
            c.extraSamples = (extraSamples != null) ? new List<Color>(extraSamples) : new List<Color>();
            c._cExtraSamples = null;
            c._sampleCaches = null;
            c._cacheInitiated = false;
            return c;
        }

        /// <summary>
        /// 再着色とマッチングの詳細チューニングだけを既定値へ戻します。
        /// ユーザーの明示的な選択（名前・id・有効状態・モード・サンプル/変更先カラー・
        /// 許容範囲）は保持し、上級モードで触る細かいパラメータのみリセットします。
        /// 試行錯誤で詳細値を壊したときに、ゾーンを作り直さずに復旧できるようにするためのもの。
        /// </summary>
        public void ResetTuningToDefault()
        {
            var d = new ColorZone();
            valueBlend              = d.valueBlend;
            outputSaturation        = d.outputSaturation;
            edgeSoftness            = d.edgeSoftness;
            saturationStrictness    = d.saturationStrictness;
            saturationGuard         = d.saturationGuard;
            highlightRecovery       = d.highlightRecovery;
            highlightBandExpand     = d.highlightBandExpand;
            applyHighlightWash      = d.applyHighlightWash;
            autoHighlightSample     = d.autoHighlightSample;
            autoRecolorAnchor       = d.autoRecolorAnchor;
            shadowDesaturation      = d.shadowDesaturation;
            shadowForgivenessSatMin = d.shadowForgivenessSatMin;
            chromaThreshold         = d.chromaThreshold;
            valueWeight             = d.valueWeight;
            satDistWeight           = d.satDistWeight;
            satRampScale            = d.satRampScale;
        }

        /// <summary>
        /// セッション開始時などに明示的にキャッシュを更新する場合に呼び出します。
        /// 呼ばれない場合は各ピクセルの評価時に暗黙的に更新されます。
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
            _cacheInitiated = true;

            // サンプルごとの派生値を構築（主サンプル + 追加スポイト）。
            int extraN = extraSamples?.Count ?? 0;
            _sampleCaches = new SampleCache[1 + extraN];
            _sampleCaches[0] = BuildSampleCache(sampleColor);
            for (int i = 0; i < extraN; i++)
                _sampleCaches[i + 1] = BuildSampleCache(extraSamples[i]);
            // 変更検知用にスナップショットを取る。
            if (_cExtraSamples == null || _cExtraSamples.Length != extraN)
                _cExtraSamples = new Color[extraN];
            for (int i = 0; i < extraN; i++) _cExtraSamples[i] = extraSamples[i];

            softRange = tolerance * edgeSoftness;
            hardRange = tolerance - softRange;

            hlHueCap = Mathf.Max(0.05f, tolerance * 0.3f);
            hlSoftRange = tolerance * edgeSoftness;
            hlHardRange = tolerance - hlSoftRange;
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

            float currentChromaHi = chromaThreshold + 0.10f;
            float baseChromaConf = Mathf.Clamp01((sc.sS - chromaThreshold) / ((currentChromaHi) - chromaThreshold));
            // 暗すぎる色（黒）は彩度データが高くても色相（Hue）の計算がノイズで暴れるため信用しない
            float valueConf = Mathf.Clamp01((sc.sV - 0.05f) / 0.15f); // Vが0.05(非常に暗い)〜0.20の範囲で減衰
            sc.chromaConfidence = Mathf.Min(baseChromaConf, valueConf);
            return sc;
        }

        public float GetMatchStrength(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            GetMatchScores(pixelColor, x, y, texWidth, texHeight, out float strength, out float highlightPot);
            return Mathf.Max(strength, highlightPot);
        }

        public void GetMatchScores(Color pixelColor, int x, int y, int texWidth, int texHeight, out float strength, out float highlightPot)
        {
            strength = 0f;
            highlightPot = 0f;
            if (!enabled) return;

            switch (mode)
            {
                case SelectionMode.ColorPick:
                    UpdateCacheIfNeeded();
                    Color.RGBToHSV(pixelColor, out float pH, out float pS, out float pV);
                    GetColorMatchScores(pixelColor, pH, pS, pV, out strength, out highlightPot, out _);
                    break;
                case SelectionMode.Rect:
                    if (IsInRect(x, y, texWidth, texHeight))
                        strength = 1f;
                    break;
            }
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

        public bool ContainsPixel(Color pixelColor, int x, int y, int texWidth, int texHeight)
        {
            return GetMatchStrength(pixelColor, x, y, texWidth, texHeight) > 0f;
        }

        // マルチサンプルの和集合マッチング。全サンプル（主＋追加スポイト）に対して
        // 1 サンプル分のマッチを計算し、最大強度を採る。サンプルが 1 個なら従来の
        // 単一サンプル計算と完全に一致する（追加スポイトが無い限り出力はビット不変）。
        private void GetColorMatchScores(Color pixelColor, float pH, float pS, float pV, out float strength, out float highlightPotential, out float matchConf)
        {
            strength = 0f;
            highlightPotential = 0f;
            matchConf = 0f;

            var caches = _sampleCaches;
            if (caches == null || caches.Length == 0)
            {
                // UpdateCacheIfNeeded 未実行時の安全網（通常は到達しない）。
                UpdateCacheIfNeeded();
                caches = _sampleCaches;
            }

            for (int si = 0; si < caches.Length; si++)
            {
                MatchOneSample(in caches[si], pixelColor, pH, pS, pV, out float s, out float hPot, out float mc);
                if (s > strength) strength = s;
                if (hPot > highlightPotential) highlightPotential = hPot;
                if (mc > matchConf) matchConf = mc;
            }
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
            // 暗いサンプルはHSV色相・彩度が不安定なため、黒るいほどグレースケールモードの適用範囲を動的に広げる。
            // sV = 0 で 0.30、sV >= 0.20 で chromaThreshold に収束する。
            float effectiveChromaThreshold = Mathf.Lerp(GrayModeBaseChromaThreshold, chromaThreshold, Mathf.Clamp01(sc.sV / GrayModeChromaConfidenceRamp));
            if (sc.sS <= effectiveChromaThreshold)
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
                if (sc.sV < GrayModeDarkSampleValue)
                {
                    float darknessFactor = Mathf.Clamp01((GrayModeDarkSampleValue - sc.sV) / GrayModeDarkSampleValue);
                    effectiveDist = Mathf.Lerp(rgbDist, pS, darknessFactor);
                }

                // 彩度整合ゲート: サンプルが微小な tint を持つ(sS>ActivateSat)ときのみ作動。
                // サンプル彩度の相対床 sS*FloorFrac を下回る中性画素(純白背景等)に距離を加算し、
                // pS=0 では確実に tolerance 超え→strength 0 に落とす。AA縁(tint一部残存)は連続的な
                // 部分ペナルティで崖を作らない。sS≈0(真の無彩サンプル)では作動しない。
                // 明部限定(gateWeight=clamp(sV/0.3)): 暗いサンプルは上の分岐で pS を距離指標に使い
                // 「中性=同素材」とみなす(暗布は中性が正常)ため、中性を罰するこのゲートと矛盾する。
                // 暗いサンプルではフェードさせ、明るい tint 素材(クリーム等)でのみ全効果にする。
                if (sc.sS > ChromaGateActivateSat)
                {
                    float gateWeight = Mathf.Clamp01(sc.sV / GrayModeDarkSampleValue);
                    float satFloor = sc.sS * ChromaGateFloorFrac;
                    float shortfall = Mathf.Clamp01((satFloor - pS) / Mathf.Max(satFloor, 1e-4f));
                    effectiveDist += shortfall * ChromaGatePenalty * _cTolerance * gateWeight;
                }

                // AA 縁の忠実復元: 外側の混色帯に soft ramp を与え partial strength にして、
                // 後段デコンタミ(α 再合成)が元の滑らかな AA を復元できるようにする(脚色でなく
                // 元の AA カバレッジの復元)。地色コアは hardRange 未満で full のまま=陰影は不変。
                // ユーザーが edgeSoftness を上げている場合はそちらを尊重(floor として作用)。
                float aaSoftRange = Mathf.Max(softRange, _cTolerance * AchromaEdgeSoftness);
                float aaHardRange = _cTolerance - aaSoftRange;
                strength = CalculateEdgeStrength(effectiveDist, aaHardRange, aaSoftRange);
                // FF コア判定用: グレーモードの色一致確信度(中性ペナルティ込み effectiveDist を使う)。
                if (strength > 0f) matchConf = Mathf.Clamp01(1f - effectiveDist / CoreMatchDistance);
                // ハイライト復元は輝度のみでざっくり判定
                if (highlightRecovery && pV > HighlightValueMin)
                {
                    float vDist = Mathf.Abs(pV - sc.sV);
                    highlightPotential = CalculateEdgeStrength(vDist, hlHardRange, hlSoftRange);
                }
                return;
            }

            // 基礎パラメータの計算
            float satConfidence = Mathf.Clamp01((pS - sc.satMin) / sc.satRamp);
            float hDist = CalculateHueDistance(pH, sc.sH);
            float sRatio = (sc.sS > 0.01f) ? Mathf.Clamp01(pS / sc.sS) : 1f;

            // 無彩色領域でのHueのバタつきを緩和する
            float maxSat = Mathf.Max(pS, sc.sS);
            float hueRelevance = Mathf.Clamp01(maxSat / Mathf.Max(0.01f, chromaThreshold));
            float effectiveHDist = hDist * hueRelevance;

            // 同系色・暗部のシャドウ許容（暗い影の部分は彩度や明度が落ちるが、同じ色として拾う）
            if (pV < sc.sV * ShadowValueThresholdFrac && effectiveHDist < ForgivenessHueGate)
            {
                float darkForgiveness = Mathf.Clamp01((sc.sV * ShadowValueThresholdFrac - pV) / (sc.sV * ForgivenessRangeFrac));

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
            if (pV > sc.sV && effectiveHDist < ForgivenessHueGate)
            {
                // 明度の伸び量を上方ヘッドルーム (1 - sV) で正規化。
                // 閾値 sV + (1-sV)*0.25 は暗側 sV*0.75（25% デッドマージン）の鏡像。
                float brightThreshold = sc.sV + (1f - sc.sV) * HighlightValueHeadroomFrac;
                float brightForgiveness = Mathf.Clamp01(
                    (pV - brightThreshold) / Mathf.Max(0.01f, (1f - sc.sV) * ForgivenessRangeFrac));

                // 色相が離れているほど免除を弱くする（暗側と同形・無関係色の巻き込み防止）
                float hueFactor = 1f - (effectiveHDist / ForgivenessHueGate);
                brightForgiveness *= hueFactor;

                satConfidence = Mathf.Max(satConfidence, brightForgiveness);
            }
            // 各距離の計算
            float dist = CalculateHybridDistance(in sc, pixelColor, pS, pV, effectiveHDist, sRatio);
            float gate = Mathf.Lerp(1f, satConfidence, sc.chromaConfidence);

            // 通常マッチ強度
            strength = CalculateEdgeStrength(dist, hardRange, softRange) * gate;
            // FF コア判定用: 有彩モードの色一致確信度。strength は彩度ゲートを掛けるため色の近さを
            // 表さない。dist(実マッチ距離)を「確信できる地色」の固定半径 CoreMatchDistance で正規化。
            if (strength > 0f) matchConf = Mathf.Clamp01(1f - dist / CoreMatchDistance);

            // ハイライト復元マッチ
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

        private float CalculateHybridDistance(in SampleCache sc, Color pixelColor, float pS, float pV, float hDist, float sRatio)
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

            // シャドウ（暗い色）の距離許容は廃止（algorithm.py DARK_FORGIVENESS_DISTANCE_REDUCE=False と同期）。
            // 距離短縮(dist*=Lerp(1,0.3,df))は、同色相だが彩度の低い near-black の別マテリアル(例:
            // HAOLAN_Sneakers の暗い紺ベロ S≈0.40/V≈0.09)を tolerance 内へ逆送し巨大な巻き込みを生む主因。
            // 全 subject で recall 非寄与・sneakers precision 0.72→0.96(GT recall 不変)と実測。暗部の取り
            // こぼし救済は彩度ゲート緩和(GetColorMatchScores の satConfidence 底上げ)で代替する(in-tolerance
            // 画素にしか効かず安全)。明部(ハイライト)免除はベタ塗り対策で性質が逆のため温存=非対称は意図的。

            // ハイライト（明部）の距離許容: 上のシャドウ許容の対称形。
            // サンプルより明るく同色相なら、低彩度化したハイライト芯でも同素材として
            // 距離を免除する。免除上限はシャドウ側と同じ 0.3f（最大70%）で対称。
            if (pV > sc.sV && hDist < ForgivenessHueGate)
            {
                float brightThreshold = sc.sV + (1f - sc.sV) * HighlightValueHeadroomFrac;
                float brightForgiveness = Mathf.Clamp01(
                    (pV - brightThreshold) / Mathf.Max(0.01f, (1f - sc.sV) * ForgivenessRangeFrac));

                float hueFactor = 1f - (hDist / ForgivenessHueGate);
                brightForgiveness *= hueFactor;

                finalDist *= Mathf.Lerp(1f, BrightDistanceForgivenessMin, brightForgiveness);
            }

            return finalDist;
        }

        private float CalculateHighlightRecovery(in SampleCache sc, float pH, float pS, float pV, float hDist, float sRatio)
        {
            if (pV <= HighlightValueMin || pS >= HighlightSaturationMax || hDist > hlHueCap)
                return 0f;

            float relaxedSatConf = Mathf.Clamp01((pS - HighlightRelaxedSatMin) / HighlightRelaxedSatRamp);
            if (relaxedSatConf <= 0f)
                return 0f;

            float vDist = Mathf.Abs(pV - sc.sV);
            float highlightDist = hDist + vDist * valueWeight * (1f - sRatio);

            if (highlightDist >= _cTolerance)
                return 0f;

            float hlStrength = CalculateEdgeStrength(highlightDist, hlHardRange, hlSoftRange);
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
