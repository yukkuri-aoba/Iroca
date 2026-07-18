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
    public partial class ColorZone
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
        // 純RGB距離だけでは、わずかに色の付いた地色(生成り・オフホワイトの布地など)と純白の距離が
        // 極小(~0.03)になり、その素材の暗い側を拾うために tolerance を上げると純白まで巻き込む。
        // 明度で見ると両者は隣接するが、tint の有無で見れば明確に分離できる。
        // サンプル自身が真の無彩(sS≈0)なら作動せず=従来の純RGB距離挙動を完全維持。
        // internal: 緩和マッチ経路(PixelProcessor.GetRelaxedMatchStrength)が同じ値を使うため共有する。
        // かつては PixelProcessor 側に同名 const を複製していたが、二重管理で乖離の温床になるため一本化。
        internal const float ChromaGateActivateSat = 0.02f; // この tint 未満のサンプルでは無効
        internal const float ChromaGateFloorFrac = 0.5f;    // サンプル彩度 sS*frac 未満は「中性すぎ」
        internal const float ChromaGatePenalty = 1.0f;      // 最大加算距離(tolerance 単位)

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
        // internal: ZoneAutoTuner の閉ループ検証(明部ツヤ判定)が同じ「同色相」定義を共有する
        // (手動同期による定数ドリフトを避ける)。
        internal const float ForgivenessHueGate = 0.15f;
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


        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        // カラーピックモード
        public Color sampleColor = Color.white;
        // ユーザーがサンプルカラーを実際に指定したか。既定の白を「未指定センチネル」として
        // 扱うと、白い服・白髪など「色替え対象が白」という正当なケースまで自動調整不可になる。
        // スポイトやカラーフィールドで色を取った瞬間に true にし、明示的な白選択を未指定と区別する。
        public bool sampleColorSet = false;
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
        // 【シミュレーション専用】ハイライト(明部)の距離免除・彩度ゲート免除を無効化する。
        // ZoneAutoTuner の閉ループ検証が「免除経路だけで選択される画素」を切り分けて数える
        // ために使う。製品 UI からは変更されず常に false(=免除有効)。シリアライズ対象外。
        [System.NonSerialized] internal bool simDisableBrightForgiveness = false;
        // ハイライト白寄せ合成: 明部(明度>サンプル)を「wash→白 軸」へ射影し、鏡面ハイライトを
        // 表現する。既定 OFF（オプトイン）。OFF のときは色相転送(HSV transfer)のみで、明部の
        // 明度・彩度構造はそのまま温存される。ON でも有彩の模様は軸残差フェードで保護され、
        // 軸上の真の鏡面のみが白寄せされる。
        public bool applyHighlightWash = false;
        // 俯瞰スポイト補正: ハイライト白寄せ合成(applyHighlightWash)用サンプルの明度を、テクスチャの
        // 地色まで自動で下げる配下オプション。明るい光沢部をスポイトしても鏡面グラデが潰れない。
        // match/base は不変＝再着色範囲は変えない。applyHighlightWash が ON のときだけ作用する。
        // 既定 OFF。房の多いテクスチャ（髪など）では OFF が望ましいことがある。
        public bool autoHighlightSample = false;
        // サンプル自動補正(再着色アンカー正規化): OkLab 再着色のアンカー (sL, sC) を、スポイト
        // 画素ではなくマッチ領域の統計(明部の地色)から自動推定する。スポイトを陰影のどの明るさで
        // 取ってもパーツの明部が target 色に一致する(サンプル位置非依存)。マッチング・wash は
        // スポイト色のまま＝再着色範囲は不変。既定 ON(オプトアウト): 影をスポイトしても出力が過度に
        // 明るく/ベタ塗りにならないよう、位置非依存で代表地色を target 明度へ合わせる。クリック
        // 画素を厳密に target 色へ当てたい/意図的に明るく塗りたいゾーンだけ OFF にする。旧プリセット
        // JSON で明示保存された値は尊重(マイグレーションなし)。フィールド欠落の旧 JSON は新既定 true。
        public bool autoRecolorAnchor = true;
        // [非推奨] 旧: 処理順を表す数値。現在は「リストの並び順＝優先度(先頭が最優先)」に
        // 変更したため未使用。古いプリセット JSON の後方互換のためフィールドのみ残す
        // (PresetsView.MigrateLegacyLayerPriority が読み込み時に一度だけ降順移行に使用)。
        public int layerIndex = 0;
        public string id = "";

        // ゾーンカードの「詳細設定」折りたたみ状態（UI 専用・非永続）。既定 false＝畳む。
        // 通常モードで詳細パラメータをゾーンごとに畳んで初見の圧を下げるために使う。
        // データ・プリセット JSON には残さないため [NonSerialized]（ドメインリロードで畳みへ戻る）。
        [NonSerialized] public bool detailFoldout = false;

        /// <summary>
        /// ユーザーがサンプルカラーを明示的に指定したか。既定の白（＝未指定センチネル）と、
        /// 「白を意図的に選んだ」正当なケースを区別して自動調整の可否などに使う。
        /// フラグを持たない旧データとの後方互換のため、非白なら指定済みとみなす。
        /// </summary>
        public bool HasSampleColor => sampleColorSet || sampleColor != Color.white;

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
    }
}
