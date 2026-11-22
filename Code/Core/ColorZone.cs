// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Iroca {
    public enum SelectionMode {
        ColorPick,
        Rect
    }

    [Serializable]
    public partial class ColorZone {
        // 選択済み判定の strength 床。PixelProcessor 側の後段ゲート
        // (ApplyChromaCeilingGate / RecoverEnclosedNeutral / SolidifyAchromaInterior)が
        // それぞれローカル const 0.05f を持っていたため一本化(値の意味: これ以下は「未選択」扱い)。
        internal const float MatchStrengthFloor = 0.05f;

        // 無彩とみなすサンプルの彩度の境目(これ未満の tint は無彩)。照合の彩度整合ゲート(Selection の
        // ColorZone.Match.cs の ChromaGate*)と、再着色の有彩・無彩の分岐が同じ境目を使う。
        internal const float ChromaGateActivateSat = 0.02f;

        public string name = "Zone";
        public bool enabled = true;
        public SelectionMode mode = SelectionMode.ColorPick;

        public Color sampleColor = Color.white;
        // ユーザーがサンプルカラーを実際に指定したか。既定の白を「未指定センチネル」として
        // 扱うと、白い服・白髪など「色替え対象が白」という正当なケースまで自動調整不可になる。
        // スポイトやカラーフィールドで色を取った瞬間に true にし、明示的な白選択を未指定と区別する。
        public bool sampleColorSet = false;
        public float tolerance = 0f;

        // マルチサンプル選択用の内部サンプル（暗部／中間／明部の代表色）。空＝単一サンプルと等価。
        // ★ユーザーが手で追加するものではない★ — 自動調整（ZoneAutoTuner.DeriveAutoTonalSamples）が
        // スポイト1点から「同色相パーツのトーン分布」を走査して自動生成し、TuneResult.ApplyTo が設定する。
        // 単一サンプル＋大きな許容範囲は濃淡を覆おうとして隣接パーツまで巻き込みやすい（誤爆の主因）。
        // 代わりにパーツの濃い所・薄い所の代表色を中心とした小さな許容範囲の「和集合」でパーツ全体を
        // 捉えることで、precision（はみ出し低減）と recall（取りこぼし低減）を同時に高める。
        //   sampleColor   … 主サンプル（スポイト点）。再着色アンカー（＝出力色マッピング）に使う。
        //   extraSamples  … 自動生成の内部サンプル。選択（マッチング）だけを広げ、出力色には影響しない。
        // 出力を主サンプル基準に固定するのは autoRecolorAnchor と同じ「選択と出力の分離」方針に沿う。
        public List<Color> extraSamples = new List<Color>();

        public Rect uvRect = new Rect(0, 0, 1, 1);

        // Flood Fill（連続領域モード／連結成分アンカリング）: ColorPick モードで有効。
        // 既定 ON＝標準挙動。確信度の高い芯を含む連結領域だけに変換を絞り込み、物理的に離れた
        // 同色パーツや背景へのにじみ（誤爆）を自動除去する。シード不要の自動アンカリングが既定で、
        // クリーンな tolerance では OFF と出力ビット等価（効くのは tolerance を盛りすぎた時）。
        // 通常モードのトグルで OFF に戻せる。
        public bool useFloodFill = true;
        // シード点のUV座標（0-1）。負値 = 未設定
        public Vector2 seedUV = new Vector2(-1f, -1f);

        // スポイトでサンプル色を取ったプレビュー上の UV（下原点、0-1）。負値 = 未設定。
        // 自動調整が AI マスク提案（SAM）のセグメントを「その場の証拠」として取る位置
        // （ZoneAutoTuner.AnalyzeWithEvidence）。選択では、連続領域モードの包絡ゲート
        // （shadowValueFloor / partSatCeiling / partHueBand）が「この位置を含む連結成分は落とさない」
        // ためにだけ使う（PixelProcessor.ApplyConnectedComponentMask）。ゲートの値はこの位置の島から
        // 導いたものなので、クリックした場所そのものが外れるのを防ぐ。選択キーに含める。
        // シリアライズして Undo とセッション（テクスチャ単位）に載せる: sampleColor と必ず対で
        // 書き戻るので、Undo/Redo・ゾーン削除の Undo で位置だけが消えたり古い色と食い違ったり
        // しない（以前は NonSerialized で Undo 後に色の食い違いから無効化していたが、Redo で
        // 色だけ戻って位置が失われた）。テクスチャ固有の位置なのでプリセットには載せない
        // （IrocaPresetData.WithoutSamplePositions / DropSamplePositions）。
        // カラーフィールドで色を変えたら UI 側が無効化する。
        public Vector2 sampleUV = new Vector2(-1f, -1f);
        public bool HasSampleUV => sampleUV.x >= 0f;
        public void ClearSampleUV() => sampleUV = new Vector2(-1f, -1f);
        // スポイト位置が選択結果に効きうる状態か（スポイトの連続領域モード）。
        // 位置だけを変えたときにプレビューを作り直すかどうかの判定に使う。包絡ゲートが無くても、位置は
        // 離れた半透明の島を落とす段(PixelProcessor.DropDetachedTranslucent)の「残す起点」に使われるので、
        // ゲートの有無では絞らない(以前は絞っていて、位置だけ変えたプレビューが書き出しと食い違った。点検 選-2)。
        // 選択キャッシュの鍵(BuildSelectionKey)は元から位置を含む。
        internal bool SelectionUsesSampleUV =>
            mode == SelectionMode.ColorPick && useFloodFill;

        public Color targetColor = Color.white;
        // ユーザーが変更先カラーを実際に指定したか（sampleColorSet と同じく、既定の白と「白を選んだ」を
        // 区別する）。UI の手順案内（変更先を選ぶ → 自動調整）にだけ使い、選択・再着色は参照しない。
        public bool targetColorSet = false;

        [Range(0f, 1f)]
        public float valueBlend = 1f;

        // 自動調整が導いた模様保持(valueBlend)の値(負 = 導いていない: 新しいゾーン・zones JSON・この項目の無い古いデータ)。
        // valueBlend がこの値のままの間は「模様保持を利用者が手で触っていない」とみなし、塗り先の色を変えたら今の式で
        // 導き直す(ZoneEdits.SetTarget)。模様保持は自動調整の項目のうち塗り先を見る唯一のもので、スポイトのクリックで
        // 自動調整するときの塗り先はまだ既定の白なので、色を選んだ後に導き直さないと「塗り先 → 自動調整」と違う値が残る。
        // 書くのは自動調整の適用(ZoneAutoTuner.TuneResult.ApplyTo)と導き直しだけ。スライダー・プリセットなどで valueBlend を
        // 書いても触らない(値が食い違うので、手で触ったことになる)。選択・再着色は読まない。
        public float derivedValueBlend = -1f;

        /// <summary>模様保持が自動調整の導いた値のまま(利用者が手で触っていない)か(<see cref="derivedValueBlend"/>)。</summary>
        public bool ValueBlendIsDerived => derivedValueBlend >= 0f && Mathf.Abs(valueBlend - derivedValueBlend) <= 1e-5f;

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

        // 陰影の明度下限(0 = 無効)。有彩サンプルのマッチは「画素の彩度がサンプル以上なら明度差を
        // 見ない」(CalculateHybridDistance の vDist×(1−sRatio))ため、同色相で暗いだけの別部位
        // (桃ベージュの上衣に対する暗紫のジャケット、肌に対する舌・まつ毛)が陰影として無制限に
        // 巻き込まれる。**画素単位ではなく連結成分単位**で効く: 選択の連結成分のうち、この明度に
        // 達する画素が半分に満たない(=大半がこの素材の陰影レンジより暗い)成分を落とす
        // (PixelProcessor.ApplyConnectedComponentMask)。画素単位で切ると、クリックした島より深い
        // 陰影を持つ同素材(衣装の深い影)まで落として recall が下がった(実測で衣装の再現率 −5.4pt)。
        // 本体に地続きの深い影は残り、離れた暗い別パーツだけが落ちる。連続領域モード(useFloodFill)
        // でのみ効く。証拠つき自動調整がクリックした島(AI 提案セグメント)に実在する明度レンジから
        // 導く(ZoneAutoTuner.Evidence.cs)。手動でも使える。
        [Range(0f, 1f)]
        public float shadowValueFloor = 0f;

        // 別パーツの彩度上限(0 = 無効)。陰影の明度下限の対になる連結成分単位のゲート: 選択の連結成分の
        // うち、彩度がこの値以下の画素が半分に満たない(=大半がこの素材の彩度の広がりより鮮やかな)成分を
        // 落とす(PixelProcessor.ApplyConnectedComponentMask)。色の近い、より鮮やかな別パーツ(濃い灰の
        // パンツに対するこげ茶のブーツ、こげ茶のブーツに対する赤いバンダナ)は、明部免除や広めの tolerance を
        // 通って成分ごと入る。画素単位で切ると素材自身の鮮やかな陰影・装飾まで落とす(彩度天井を証拠の包絡へ
        // 下げる案は淡色の上衣で再現率 −0.04)ので、成分の大半が上限を超えるときだけ落とす。
        // 連続領域モード(useFloodFill)でのみ効く。証拠つき自動調整がクリックした島(AI 提案セグメント)に
        // 実在する彩度包絡から導く(ZoneAutoTuner.Evidence.cs)。手動でも使える。
        [Range(0f, 1f)]
        public float partSatCeiling = 0f;

        // 別パーツの色相幅(0 = 無効、有彩サンプルのみ)。明度下限・彩度上限と同じ連結成分単位のゲート:
        // 選択の連結成分のうち、色相がサンプル色相から ±この幅に入る画素が 1/4 に満たない成分を落とす。
        // マッチ距離は色相・彩度・明度の合算なので、tolerance の範囲で色相だけが少しずれた別パーツ
        // (こげ茶のブーツに対する灰色がかったパンツ、紺の瞳に対する紫の影、格子柄のスカートに対する
        // 別の布)が成分ごと入る。彩度が低く色相が不安定な画素(サンプル彩度 × HueReliableSatFrac 未満)は
        // 反証にしない(帯の内側として数える)ので、素材自身の脱彩したツヤや深い影の成分は落ちない。
        // 連続領域モード(useFloodFill)・有彩サンプルでのみ効く(グレーモードの色相は不定)。
        // 証拠つき自動調整がクリックした島に実在する色相の広がりから導く(ZoneAutoTuner.Evidence.cs)。
        [Range(0f, 0.5f)]
        public float partHueBand = 0f;

        // 彩度天井(グレーモード専用、0 = 自動)。無彩/微 tint サンプルの選択から、サンプル彩度の
        // ChromaCeilSampleFrac 倍(下限 ChromaCeilAbs)を超える高彩度画素を「染められた別素材」として
        // 落とす後段ゲート(PixelProcessor.ApplyChromaCeilingGate)の天井を上書きする。白い布の青みが
        // かった陰のように、素材自身の陰影が地色の何倍もの tint を持つと自動の天井では陰が丸ごと
        // 落ちる(実測: 白い布の明部クリックで再現率 0.60〜0.66、陰の S 0.07〜0.14 に対し
        // 天井 0.05)。証拠つき自動調整がクリックした島(AI 提案セグメント)に実在する彩度包絡から導き、
        // 自動の天井より高いときだけ入れる(ZoneAutoTuner.Evidence.cs)。手動でも使える。
        [Range(0f, 1f)]
        public float chromaCeiling = 0f;

        // 穴埋めの余白(tolerance に対する比、0 = 余白なし)。選択に囲まれた小さな穴を埋めてよいかの
        // 判定(穴埋めの緩和マッチ)だけを tolerance × (1 + この値) で行う。布の明るい所や柄の光の周りの
        // ように、素材の色が許容範囲の境目をまたぐ所では、画素ごとのノイズで選ぶ・選ばないが交互になり、
        // 元の色の点が点々と残る(穴埋めも主マッチと同じ境目で弾いていた)。境界回復と主マッチは変えない
        // ので、囲まれていない画素は広がらない。証拠つき自動調整が入れる(ZoneAutoTuner.Evidence.cs)。
        // 手動の許容範囲では 0 のまま: 許容範囲を広く取ると別の部位が穴だらけに選ばれ、デコンタミが
        // 縁として元の色へ戻すので目立たない。穴を埋めるとそれが塊として表に出る(実測: 生成りの布)。
        [Range(0f, 1f)]
        public float holeFillMargin = 0f;

        // 照合の上限距離(0 = 無効)。グレーモード(無彩のサンプル)の照合で、距離がこの値以上の画素を選ばない
        // (強度もハイライトの候補も 0。ColorZone.Match.cs の MatchOneSample)。穴埋めの許可と境界回復の緩和マッチも、
        // この値を許容範囲にした緩和マッチで外れる画素は選ばない(PixelProcessor.GetRelaxedMatchStrengthWithinCutoff)。
        // 自動調整の許容範囲の切り詰め(ZoneAutoTuner.Cut.cs)は、有彩の照合では許容範囲そのものを下げるが、
        // グレーモードでは許容範囲が縁のぼかしの幅も兼ねていて(全強度は許容範囲の 1 − AchromaEdgeSoftness = 3 割まで)、
        // 下げると素材の内側まで薄く塗られる。そこで許容範囲は変えず、切り落とす距離だけをここに持つ
        // (黒い布に対する灰色の部品のように、近い無彩の別の部品を選択から外す)。
        // 入れるのは自動調整の適用(ZoneAutoTuner.TuneResult.ApplyTo)だけで、ウィンドウには出さない。利用者が許容範囲を
        // 手で動かしたら外す(SetToleranceManually)。残すと、許容範囲を上げてもこの距離より先が選ばれず、理由が画面に見えない。
        // サンプル色を取り直したら、島から導くほかの項目と一緒に 0 へ戻す(ZoneAutoTuner.ResetIslandDerived)。
        [Range(0f, 1f)]
        public float matchCutoff = 0f;

        [Range(0f, 1f)]
        public float chromaThreshold = 0.05f;

        [Range(0f, 1f)]
        public float saturationGuard = 0f;

        public bool highlightRecovery = true;
        public bool highlightBandExpand = true;
        // ハイライト白寄せ合成: 明部(明度>サンプル)を「wash→白 軸」へ射影し、鏡面ハイライトを
        // 表現する。既定 OFF（オプトイン）。OFF のときは OkLab 再着色のみで、明部の
        // 明度・彩度構造はそのまま温存される。ON でも有彩の模様は軸残差フェードで保護され、
        // 軸上の真の鏡面のみが白寄せされる。
        public bool applyHighlightWash = false;
        // 俯瞰スポイト補正: ハイライト白寄せ合成(applyHighlightWash)用サンプルの明度を、テクスチャの
        // 地色まで自動で下げる配下オプション。明るい光沢部をスポイトしても鏡面グラデが潰れない。
        // match/base は不変＝再着色範囲は変えない。applyHighlightWash が ON のときだけ作用する。
        // 既定 OFF。房の多いテクスチャ（髪など）では OFF が望ましいことがある。
        public bool autoHighlightSample = false;
        // サンプル自動補正(再着色アンカー正規化): OkLab 再着色のアンカー (sL, sC) を、スポイト
        // 画素ではなくマッチ領域の統計(地色。明部より暗い所に地色の山があればその山、無ければ明部)から自動推定する。スポイトを陰影のどの明るさで
        // 取ってもパーツの地色が target 色に一致する(サンプル位置非依存)。マッチング・wash は
        // スポイト色のまま＝再着色範囲は不変。既定 ON(オプトアウト): 影をスポイトしても出力が過度に
        // 明るく/ベタ塗りにならないよう、位置非依存で代表地色を target 明度へ合わせる。クリック
        // 画素を厳密に target 色へ当てたい/意図的に明るく塗りたいゾーンだけ OFF にする。旧プリセット
        // JSON で明示保存された値は尊重(マイグレーションなし)。フィールド欠落の旧 JSON は新既定 true。
        public bool autoRecolorAnchor = true;
        // [非推奨] 旧: 処理順を表す数値。現在は「リストの並び順＝優先度(先頭が最優先)」に
        // 変更したため未使用。古いプリセット JSON の後方互換のためフィールドのみ残す
        // (PresetApplier.MigrateLegacyLayerPriority が読み込み時に一度だけ降順移行に使用)。
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

        /// <summary>
        /// ユーザーが変更先カラーを指定したか。旧データ（フラグなし）は非白なら指定済みとみなす。
        /// </summary>
        public bool HasTargetColor => targetColorSet || targetColor != Color.white;

        public void EnsureId() {
            if (string.IsNullOrEmpty(id)) {
                id = Guid.NewGuid().ToString();
            }
        }

        /// <summary>
        /// 利用者が許容範囲を手で変える(自動調整の適用以外で許容範囲を書く所はここを通す)。値が変わったら、
        /// 自動調整が入れた照合の上限距離(<see cref="matchCutoff"/>)を外す。残すと、許容範囲を上げても上限より先が
        /// 選ばれず、理由が画面に見えない。同じ値なら何もしない(スライダーを触っていないフレームで外さない)。
        /// Undo の記録は呼び出し側(記録してから呼べば、許容範囲と上限が同じ記録で戻る)。
        /// </summary>
        public void SetToleranceManually(float value) {
            if (value == tolerance) {
                return;
            }
            tolerance = value;
            matchCutoff = 0f;
        }

        /// <summary>
        /// このゾーンのコピーを返します。ゾーンは設定だけを持つ(照合が導く値は <see cref="Matcher"/> が持ち、ゾーンには残らない)。
        /// extraSamples は可変リストなので、クローン間で共有しないよう深いコピーにします。
        /// </summary>
        public ColorZone Clone() {
            var c = (ColorZone)MemberwiseClone();
            c.extraSamples = (extraSamples != null) ? new List<Color>(extraSamples) : new List<Color>();
            return c;
        }

        /// <summary>
        /// 再着色とマッチングの詳細チューニングだけを既定値へ戻します。
        /// ユーザーの明示的な選択（名前・id・有効状態・モード・サンプル/変更先カラー・
        /// 許容範囲）は保持し、上級モードで触る細かいパラメータのみリセットします。
        /// 試行錯誤で詳細値を壊したときに、ゾーンを作り直さずに復旧できるようにするためのもの。
        /// </summary>
        public void ResetTuningToDefault() {
            var d = new ColorZone();
            valueBlend = d.valueBlend;
            derivedValueBlend = d.derivedValueBlend;   // 既定へ戻した値は自動調整が導いたものではない
            outputSaturation = d.outputSaturation;
            edgeSoftness = d.edgeSoftness;
            saturationStrictness = d.saturationStrictness;
            saturationGuard = d.saturationGuard;
            highlightRecovery = d.highlightRecovery;
            highlightBandExpand = d.highlightBandExpand;
            applyHighlightWash = d.applyHighlightWash;
            autoHighlightSample = d.autoHighlightSample;
            autoRecolorAnchor = d.autoRecolorAnchor;
            shadowDesaturation = d.shadowDesaturation;
            shadowForgivenessSatMin = d.shadowForgivenessSatMin;
            shadowValueFloor = d.shadowValueFloor;
            partSatCeiling = d.partSatCeiling;
            partHueBand = d.partHueBand;
            chromaCeiling = d.chromaCeiling;
            holeFillMargin = d.holeFillMargin;
            matchCutoff = d.matchCutoff;
            chromaThreshold = d.chromaThreshold;
            valueWeight = d.valueWeight;
            satDistWeight = d.satDistWeight;
            satRampScale = d.satRampScale;
        }
    }
}
