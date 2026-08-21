// Copyright 2026 yukkuri__aoba https://github.com/yukkuri-aoba/Iroca
// Licensed under PolyForm Shield License 1.0.0 https://polyformproject.org/licenses/shield/1.0.0
namespace Iroca
{
    public enum LanguageMode { Auto, Japanese, English }

    public static class Localization
    {
        private const string PrefsKey = "Iroca.Language";
        public static LanguageMode CurrentLanguage;

        static Localization()
        {
            CurrentLanguage = (LanguageMode)UnityEditor.EditorPrefs.GetInt(PrefsKey, (int)LanguageMode.Auto);
        }

        public static void SaveLanguagePreference()
            => UnityEditor.EditorPrefs.SetInt(PrefsKey, (int)CurrentLanguage);

        private const string EnglishNoticeKey = "Iroca.EnglishTranslationNoticeShown";

        /// <summary>
        /// 英語表示が初めて使われたとき（Auto 判定・手動選択どちらでも）、
        /// UI の英訳が AI 補助による機械翻訳である旨を一度だけダイアログ表示する。
        /// </summary>
        public static void MaybeShowEnglishTranslationNotice()
        {
            if (IsJapanese) return;
            if (UnityEditor.EditorPrefs.GetBool(EnglishNoticeKey, false)) return;
            UnityEditor.EditorPrefs.SetBool(EnglishNoticeKey, true);
            UnityEditor.EditorUtility.DisplayDialog(
                "AI Translation Notice",
                "The English text in this tool is machine-translated with AI assistance.\n"
                + "It may contain unnatural or inaccurate wording. The Japanese text is authoritative.\n\n"
                + "この英語表示は AI による機械翻訳です。",
                "OK");
        }

        public static bool IsJapanese
        {
            get
            {
                switch (CurrentLanguage)
                {
                    case LanguageMode.Japanese: return true;
                    case LanguageMode.English:  return false;
                    default: // Auto
                        return System.Globalization.CultureInfo.CurrentUICulture
                            .TwoLetterISOLanguageName == "ja";
                }
            }
        }

        public static string WindowTitle => "いろか";

        public static string LangAuto     => IsJapanese ? "自動(Auto)" : "Auto";
        public static string LangJapanese => "日本語";
        public static string LangEnglish  => "English";
        public static string Credit       => IsJapanese ? "クレジット" : "Credit";

        public static string CreditTitle => IsJapanese ? "クレジット" : "Credits";
        public static string CreditBody  => IsJapanese
            ? "いろか\n\n制作: yukkuri__aoba\nAI補助: Claude (Anthropic)\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Shield License 1.0.0\nhttps://polyformproject.org/licenses/shield/1.0.0"
            : "いろか\n\nDeveloper: yukkuri__aoba\nAI Assistance: Claude (Anthropic)\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Shield License 1.0.0\nhttps://polyformproject.org/licenses/shield/1.0.0";

        // セクションが等価に並んで開始点が分かりにくいので、主要 4 ステップに番号を振り、
        // テクスチャ未設定時はこの導入ヒントで一連の流れを示す。
        public static string StepPrefixTexture => IsJapanese ? "① " : "1. ";
        public static string StepPrefixZones   => IsJapanese ? "② " : "2. ";
        public static string StepPrefixPreview => IsJapanese ? "③ " : "3. ";
        public static string StepPrefixExport  => IsJapanese ? "④ " : "4. ";
        public static string WorkflowHint => IsJapanese
            ? "手順: ① 元テクスチャを選ぶ → ② カラーゾーンで色を指定（スポイトで色を取り「自動調整」が簡単）→ ③ プレビューで確認 → ④ 適用して保存"
            : "Steps: 1. Pick a source texture  2. Set colors in Color Zones (sample a color and use Auto-tune)  3. Check the Preview  4. Apply & Save";

        public static string SourceTexture => IsJapanese ? "元テクスチャ" : "Source Texture";
        public static string Texture => IsJapanese ? "テクスチャ" : "Texture";
        public static string TextureTooltip => IsJapanese
            ? "色を変えたいテクスチャをここにドラッグ＆ドロップ、または右の◎から選びます（作業の開始点）。\nテクスチャごとにゾーン設定とマスクが保存され、切り替えると自動で読み戻されます。"
            : "Drag & drop the texture you want to recolor here, or pick it with the ◎ button (this is where the workflow starts).\nZones and masks are saved per texture and restored when you switch back.";
        public static string ReadWriteError => IsJapanese
            ? "Read/Write Enabled がオフです。\nインポート設定で有効にしてください。"
            : "Read/Write Enabled is off.\nPlease enable it in the import settings.";
        public static string EnableReadWrite => IsJapanese
            ? "Read/Write を自動で有効にする"
            : "Enable Read/Write automatically";
        public static string EnableReadWriteConfirm => IsJapanese
            ? "このテクスチャの Read/Write Enabled を有効化してインポート設定を変更します。\nこの操作は Undo できません。続行しますか？"
            : "This will enable Read/Write Enabled on the texture and modify its import settings.\nThis action cannot be undone. Continue?";

        public static string ColorZones => IsJapanese ? "カラーゾーン" : "Color Zones";
        // 名前未設定ゾーンの表示名（マスク対象プルダウン・ドラッグゴースト等で使用）。
        public static string UnnamedZone => IsJapanese ? "ゾーン" : "Zone";
        public static string AddZone => IsJapanese ? "+ ゾーン追加" : "+ Add Zone";
        public static string SelectionMode => IsJapanese ? "選択モード" : "Selection Mode";
        public static string SampleColor => IsJapanese ? "サンプルカラー" : "Sample Color";

        // サンプルカラー欄と同一行に置くため短縮ラベル（詳細説明は EyedropperTooltip が保持）。
        public static string EyedropperIdle => IsJapanese
            ? "スポイト"
            : "Eyedropper";
        public static string EyedropperActive => IsJapanese
            ? "■ クリック取得"
            : "■ Click to pick";
        public static string EyedropperTooltip => IsJapanese
            ? "押すとスポイトモードになり、プレビュー上をクリックするとそのピクセルの色を\nこのゾーンの「サンプルカラー」に取り込みます。\n実テクスチャの画素から直接取得するため、カラーピッカーのスポイトより正確です。\nもう一度押すと解除します。"
            : "Enters eyedropper mode; click the preview to load that pixel's color into this zone's Sample Color.\nIt reads the actual texture pixel directly, so it is more accurate than the color picker's eyedropper.\nClick again to cancel.";
        public static string Tolerance => IsJapanese ? "許容範囲" : "Tolerance";
        public static string UVRect => IsJapanese ? "UV範囲 (0-1)" : "UV Rect (0-1)";
        public static string TargetColor => IsJapanese ? "変更先カラー" : "Target Color";
        public static string PatternPreserve => IsJapanese ? "模様保持" : "Pattern Preserve";
        public static string PatternPreserveTooltip => IsJapanese
            ? "0 = 変更先の明度に完全に合わせる（ベタ塗り風）\n1 = 元の明度を完全保持（模様が残りやすい）"
            : "0 = Match target brightness entirely (flat recolor)\n1 = Keep original brightness (preserves patterns)";
        public static string OutputSaturation => IsJapanese ? "出力彩度" : "Output Saturation";
        public static string OutputSaturationTooltip => IsJapanese
            ? "再着色後の彩度。1.0 = 変更先の鮮やかさをそのまま使う。\n彩度100%の純色（例: 純赤）は明暗のグラデーションが潰れて「ベタ塗り」に見えやすい。\n少し下げると（0.7〜0.9）色相は保ったまま明度の立体感（陰影）が戻る。\nデフォルト: 1.0"
            : "Saturation after recoloring. 1.0 = use the target color's full vividness.\nFully saturated colors (e.g. pure red) tend to flatten brightness gradients (a solid-fill look).\nLowering it (0.7-0.9) keeps the hue but restores the brightness gradient / shading.\nDefault: 1.0";
        public static string EdgeSoftness => IsJapanese ? "エッジ柔らかさ" : "Edge Softness";
        public static string EdgeSoftnessTooltip => IsJapanese
            ? "0 = 硬いエッジ（従来通り）\n1 = 柔らかいエッジ（アンチエイリアス境界を滑らかに）"
            : "0 = Hard edge (legacy)\n1 = Soft edge (smooth anti-aliased boundaries)";
        public static string SaturationStrictness => IsJapanese ? "彩度制限（影の厳しさ）" : "Saturation Strictness";
        public static string SaturationStrictnessTooltip => IsJapanese
            ? "【何をする?】選んだ色の“薄い影・AO（暗い陰り）”をどこまで仲間として拾うかの厳しさ。\n上げる → はみ出しが減るが、境界に色のドットが残りやすい\n下げる → ドットが減るが、まわりへはみ出しやすい\n※「彩度ガード」との違い：こちらは“薄い影をどう扱うか”の調整。あちらは“白/黒/灰そのものを弾く”安全装置。\nデフォルト: 0.50"
            : "[What] How strictly the faint shadows / AO of the picked color are kept as part of the selection.\nHigher → less bleed, but color dots may remain at edges\nLower → fewer dots, but more bleed into surroundings\nNote vs 'Saturation Guard': this tunes how shadows are handled; Guard hard-rejects achromatic (white/black/gray) pixels.\nDefault: 0.50";

        public static string SaturationGuard => IsJapanese ? "彩度ガード（無彩色よけ）" : "Saturation Guard";
        public static string SaturationGuardTooltip => IsJapanese
            ? "【何をする?】鮮やかな色を選んだとき、白・黒・灰色など“色味のない”部分が結果に混ざるのを防ぐ安全装置。\n0 = 無効（従来動作）\n1 = 厳格（無彩色を強く弾く）\n選んだ色がもともと灰色寄りなら自動で無効になります。\n許容範囲を大きく上げて色の芯まで拾うとき、関係ない黒/白の巻き込みを抑えるのに使います。\n※「彩度制限」との違い：あちらは“薄い影の拾い方”、こちらは“無彩色そのものの除外”。\nデフォルト: 0"
            : "[What] A safety guard that keeps colorless areas (white/black/gray) out of the result when you pick a vivid color.\n0 = off (legacy behavior)\n1 = strict (aggressively reject achromatic pixels)\nAutomatically disabled when the picked color is itself grayish.\nUse it when raising tolerance to recover the core while keeping unrelated black/white out.\nNote vs 'Saturation Strictness': that one tunes shadow handling; this one excludes achromatic pixels outright.\nDefault: 0";

        public static string Processing => IsJapanese ? "加工設定" : "Processing";
        public static string EdgeFeather => IsJapanese ? "エッジぼかし" : "Edge Feather";
        public static string EdgeFeatherTooltip => IsJapanese
            ? "選択境界をガウシアンブラーでぼかし、滑らかな遷移を実現します。\n0 = オフ"
            : "Gaussian blur on selection boundary for smooth transitions.\n0 = off";

        public static string AntiAliasCleanup => IsJapanese ? "AA境界クリーンアップ" : "AA Edge Cleanup";
        public static string AntiAliasCleanupTooltip => IsJapanese
            ? "アンチエイリアス境界の残りドットを除去するパス数。\n0 = オフ、3 = 標準（推奨）、5 = 最大\n値を大きくすると境界の回収範囲が広がります。"
            : "Number of passes to recover anti-alias boundary pixels.\n0 = off, 3 = normal (recommended), 5 = max\nHigher values recover more edge pixels.";

        public static string UseDecontamination => IsJapanese ? "境界クリーンアップ（α分解）" : "Edge Decontamination";
        public static string UseDecontaminationTooltip => IsJapanese
            ? "AA境界で α 分解＋再合成を行い、薄汚れた中間色（halo）の発生を防ぎます。\n推奨: ON"
            : "Reconstruct AA boundary via alpha decomposition to prevent muddy halo at edges.\nRecommended: ON";

        public static string DecontaminationRadius => IsJapanese ? "α分解 近傍半径" : "Decontamination Radius";
        public static string DecontaminationRadiusTooltip => IsJapanese
            ? "α分解で背景色を推定する近傍ピクセルの半径。\n小さい = シャープな境界に対応、大きい = ノイズの多い背景に対応\n標準: 4"
            : "Radius of neighborhood used to estimate background color for decontamination.\nSmaller = sharper boundaries, larger = noisier backgrounds\nDefault: 4";

        public static string AdvancedMode => IsJapanese ? "アドバンスモード" : "Advanced Mode";
        public static string AdvancedModeTooltip => IsJapanese
            ? "有効にすると、アルゴリズムの内部パラメータをより細かく調整できます。\n通常はデフォルト値で十分ですが、特殊なテクスチャに対して微調整が必要な場合に使用してください。"
            : "Enables fine-grained control over internal algorithm parameters.\nDefault values work well for most textures, but can be tuned for special cases.";

        public static string EditMode      => IsJapanese ? "編集モード" : "Mode";
        public static string SimpleMode    => IsJapanese ? "かんたん" : "Simple";
        public static string NormalMode    => IsJapanese ? "通常" : "Normal";
        public static string AdvancedShort => IsJapanese ? "上級" : "Advanced";
        // 「かんたん」モードは UI から隠してある（IrocaWindow.ZoneList.cs の Simple 非表示を参照）。
        // 再表示するときはこのツールチップにも説明を戻すこと。
        public static string EditModeTooltip => IsJapanese
            ? "「通常」: 標準的な調整項目（エッジ・彩度・シャドウ/ハイライト）を手動で調整します。\n「上級」: 通常に加えて、内部マッチング重みなど最も細かいパラメータまで表示します。\nどちらのモードでも自動調整が勝手に走ることはありません（「自動調整」ボタンを押したときだけ実行）。"
            : "Normal: the standard manual controls (edge, saturation, shadow/highlight).\nAdvanced: Normal plus the finest internal parameters (matching weights, etc.).\nNeither mode runs Auto-tune on its own - it only runs when you press the Auto-tune button.";
        public static string AutoTuningInProgress => IsJapanese ? "自動調整中…" : "Auto-tuning…";

        // ゾーンカード内の詳細パラメータ折りたたみ見出し（通常モードで既定畳む）。
        public static string ZoneDetailFoldout => IsJapanese ? "詳細設定" : "Details";
        public static string ZoneDetailFoldoutTooltip => IsJapanese
            ? "許容範囲・彩度制限・エッジなどの詳細パラメータを開閉します。通常は自動調整に任せて閉じたままで構いません。"
            : "Show/hide advanced parameters (tolerance, saturation limits, edges). Usually you can leave this closed and rely on auto-tune.";

        public static string ResetZoneTuning => IsJapanese ? "詳細を既定値に戻す" : "Reset details to default";
        public static string ResetZoneTuningTooltip => IsJapanese
            ? "このゾーンの詳細パラメータ（エッジ・彩度・シャドウ/ハイライト等）だけを既定値に戻します。\nサンプル/変更先カラー・許容範囲・名前は変わりません。試行錯誤で値を崩したときの復旧用です。"
            : "Resets only this zone's detailed parameters (edge, saturation, shadow/highlight, etc.) to defaults.\nSample/target color, tolerance, and name are kept. Handy to recover after over-tweaking.";

        public static string HoleFillPasses => IsJapanese ? "穴埋めパス数" : "Hole Fill Passes";
        public static string HoleFillPassesTooltip => IsJapanese
            ? "アンチエイリアス端の孤立ドットを除去するパス数。\n多いほど大きなギャップを埋めますが、過剰に埋める可能性があります。\nデフォルト: 3"
            : "Passes to fill isolated dots at anti-aliased edges.\nMore passes fill larger gaps but may over-fill.\nDefault: 3";

        public static string HoleFillMinNeighbors => IsJapanese ? "穴埋め最小隣接数" : "Hole Fill Min Neighbors";
        public static string HoleFillMinNeighborsTooltip => IsJapanese
            ? "穴を埋めるために必要なマッチした隣接ピクセルの最小数。\n低い値 = より積極的に穴を埋める\n高い値 = より保守的\nデフォルト: 4"
            : "Minimum matched neighbors required to fill a hole.\nLower = more aggressive filling\nHigher = more conservative\nDefault: 4";

        public static string RelaxedSatMin => IsJapanese ? "境界復元 彩度最小" : "Boundary Sat Min";
        public static string RelaxedSatMinTooltip => IsJapanese
            ? "境界復元時の彩度最小閾値。\n低い値 = より多くの境界ピクセルを回収\n高い値 = より厳格な回収\nデフォルト: 0.02"
            : "Minimum saturation threshold for boundary recovery.\nLower = recover more boundary pixels\nHigher = stricter recovery\nDefault: 0.02";

        public static string RelaxedSatRamp => IsJapanese ? "境界復元 彩度ランプ" : "Boundary Sat Ramp";
        public static string RelaxedSatRampTooltip => IsJapanese
            ? "境界復元時の彩度ランプ幅。\n大きい値 = より段階的な遷移\n小さい値 = よりシャープな境界\nデフォルト: 0.08"
            : "Saturation ramp width for boundary recovery.\nLarger = more gradual transition\nSmaller = sharper boundary\nDefault: 0.08";

        public static string ValueWeight => IsJapanese ? "明度重み" : "Value Weight";
        public static string ValueWeightTooltip => IsJapanese
            ? "距離式における明度（V）の重み。\n高い値 = 明度差に敏感（異なる素材を分離しやすい）\n低い値 = 明度差を許容（同じ素材の影/ハイライト変動を吸収）\nデフォルト: 1.0"
            : "Weight of value (brightness) in the distance formula.\nHigher = sensitive to brightness differences (separates different materials)\nLower = tolerates brightness variation (absorbs shadow/highlight of same material)\nDefault: 1.0";

        public static string SatDistWeight => IsJapanese ? "彩度距離重み" : "Sat Distance Weight";
        public static string SatDistWeightTooltip => IsJapanese
            ? "距離式における彩度距離の重み。\n高い値 = 彩度差に敏感\n低い値 = 彩度差を許容\nデフォルト: 0.15"
            : "Weight of saturation distance in the distance formula.\nHigher = sensitive to saturation differences\nLower = tolerates saturation differences\nDefault: 0.15";

        public static string SatRampScale => IsJapanese ? "彩度ランプスケール" : "Sat Ramp Scale";
        public static string SatRampScaleTooltip => IsJapanese
            ? "動的彩度ランプのスケール係数。\nsatRamp = Max(0.08, サンプル彩度 × この値)\n大きい値 = 彩度閾値付近で段階的なフェードイン\n小さい値 = より急激な閾値\nデフォルト: 0.10"
            : "Scale factor for the dynamic saturation ramp.\nsatRamp = Max(0.08, sampleSat × this)\nLarger = more gradual fade near threshold\nSmaller = sharper threshold\nDefault: 0.10";

        public static string ShadowDesaturation => IsJapanese ? "シャドウ彩度低下" : "Shadow Desaturation";
        public static string ShadowDesaturationTooltip => IsJapanese
            ? "暗いピクセルの彩度をどの程度の明度以下から強制的に落とすかの閾値です。低くすると暗い色でも鮮やかに染まります。\nデフォルト: 0.35"
            : "Threshold below which dark pixels have their saturation reduced to avoid unnatural colors.\nDefault: 0.35";

        public static string ShadowForgivenessSatMin => IsJapanese ? "シャドウ巻き込み最低彩度" : "Shadow Forgiveness Sat Min";
        public static string ShadowForgivenessSatMinTooltip => IsJapanese
            ? "グレーや黒のピクセルを同系色の影として巻き込むのを防ぐための最低彩度です。\nデフォルト: 0.05"
            : "Minimum saturation required to include a dark pixel as part of the shadow. Prevents pure greys from being colorized.\nDefault: 0.05";

        public static string ShadowHighlightSection => IsJapanese ? "シャドウ・ハイライト詳細設定" : "Shadow / Highlight Details";

        public static string ChromaThreshold => IsJapanese ? "自動しきい値(無彩色判定)" : "Auto Grayscale Threshold";
        public static string ChromaThresholdTooltip => IsJapanese
            ? "スポイトで取ったサンプルの彩度がこの値以下の場合は、自動的に【無彩色(黒/グレー)】として認識され、色相を無視して綺麗に抽出します。"
            : "If the sample saturation is below this value, it automatically ignores hue and extracts pure grayscale nicely.";

        public static string HighlightRecovery => IsJapanese ? "ハイライト補助" : "Highlight Recovery";
        public static string HighlightRecoveryTooltip => IsJapanese
            ? "高明度・低彩度のハイライト領域を補助的にマッチします。\n鏡面反射や光沢のある素材の色変換漏れを防ぎます。\n白背景に溶けたアンチエイリアス境界も同じ特徴（明るく低彩度・同色相）を持つため、\n境界の取りこぼし（ドット残り）の低減にも副次的に効きます。\nデフォルト: ON"
            : "Match high-brightness low-saturation highlight regions.\nPrevents missed recoloring on reflective/glossy materials.\nAnti-aliased edges blended into a light background share the same signature\n(bright, low-saturation, same hue), so this also helps reduce leftover edge dots.\nDefault: ON";

        public static string HighlightBandExpand => IsJapanese ? "ハイライト帯の拡張" : "Highlight Band Expansion";
        public static string HighlightBandExpandTooltip => IsJapanese
            ? "本体にマッチした領域から、描き込まれたハイライト（同色相で白方向に色が薄くなった明部）へ\n変換範囲を空間的に広げます。許容値を上げずに、薄いハイライトのベタ塗り化・取りこぼしを防ぎます。\n本体に連結した領域のみ広げるため、別パーツや白素材への巻き込みは起きません。\n白背景へのアンチエイリアス境界も同じ「源色→白」軸上に乗るため、\n本体に連結した境界の取りこぼし（ドット残り）の低減にも副次的に効きます。\n「ハイライト補助」が ON のときのみ有効。\nデフォルト: ON"
            : "Expands recoloring from the matched body into drawn-in highlights (same-hue bright pixels washed toward white).\nKeeps thin highlights from being flattened or missed without raising tolerance.\nOnly grows regions connected to the matched body, so it never bleeds into other parts or white material.\nAnti-aliased edges blended into a light background lie on the same sample-to-white axis,\nso this also helps reduce leftover edge dots on boundaries connected to the body.\nActive only when Highlight Recovery is ON.\nDefault: ON";

        public static string ApplyHighlightWash => IsJapanese ? "ハイライト白寄せ合成" : "Highlight White Blend";
        public static string ApplyHighlightWashTooltip => IsJapanese
            ? "明部（サンプルより明るい部分）を白方向へ寄せて、鏡面ハイライトの白い反射を表現します。\n光沢・プラスチックなど、ハイライトが白く飛ぶ素材で効果的です。\nOFF のときは色相を変えるだけで、明部の明度・彩度はそのまま保たれます。\nON でも、別色の有彩な模様は「軸残差フェード」で保護され、本来の鏡面（地色が白く飛んだ部分）だけが白寄せされます。\nデフォルト: OFF（必要なゾーンだけ ON）"
            : "Pushes bright areas (brighter than the sample) toward white to reproduce the white reflection of specular highlights.\nUseful for glossy/plastic materials where highlights blow out to white.\nWhen OFF, only the hue changes and the brightness/saturation structure of bright areas is preserved.\nEven when ON, off-hue colored patterns are protected by an axis-residual fade, so only genuine specular highlights (the base color washed to white) get the white blend.\nDefault: OFF (enable per zone as needed)";

        public static string AutoHighlightSample => IsJapanese ? "ハイライト自動補正" : "Auto Highlight Sample";
        public static string AutoHighlightSampleTooltip => IsJapanese
            ? "「ハイライト白寄せ合成」の配下オプション。ON のときのみ有効です。\n明るい光沢部をスポイトすると、中央ハイライトのグラデーションが潰れて「ベタ塗り」に見えることがあります。\nこの設定は、パーツの地色(同じ色相の中間トーン)をテクスチャから自動で見つけ、白寄せ合成がドーム全体に\n効くようにして鏡面の立体感を出します。再着色する範囲(マッチング)は変えず、見え方だけを補正します。\n※ 髪など細い房が多いテクスチャでは白寄せが広がりすぎることがあるため、その場合は OFF にしてください。\nデフォルト: OFF（必要なゾーンだけ ON）"
            : "A sub-option of \"Highlight White Blend\"; only active when that is ON.\nWhen you eyedrop a bright glossy spot, the central highlight gradient can collapse into a flat look.\nThis finds the part's base tone (same-hue mid tone) from the texture automatically so the white blend covers the\nwhole dome, making specular shading appear. It does not change which pixels are recolored (matching), only how it looks.\nNote: on hair-like textures with many thin strands the white-ward blend can spread too much - turn this OFF there.\nDefault: OFF (enable per zone as needed)";

        public static string AutoRecolorAnchor => IsJapanese ? "サンプル自動補正（再着色）" : "Auto Sample Anchor";
        public static string AutoRecolorAnchorTooltip => IsJapanese
            ? "スポイトした位置の明るさ・鮮やかさに関わらず、パーツの明るい面の色が「変更先の色」に一致するよう、\n再着色の基準をマッチした領域の統計から自動で補正します。\nこれにより、影の部分をスポイトしても出力が指定より過度に明るく・ベタ塗りになるのを防ぎます。\nOFF にするとスポイトした画素そのものが変更先の色になるため、クリックした画素を厳密に変更先の色へ\n当てたいとき、または意図的に明るく塗りたいときに使います。\n再着色する範囲(マッチング)は変わらず、色の写り方だけが補正されます。\nデフォルト: ON（明るくしたいゾーンだけ OFF）"
            : "Automatically corrects the recoloring reference from the matched region's statistics so the lit side of the part\nmatches the target color, regardless of how bright or saturated the eyedropped spot was.\nThis prevents the output from looking excessively brighter or flatter than specified when you sample in a shadow.\nWhen OFF, the eyedropped pixel itself maps to the target color — use OFF when you want the clicked pixel mapped\nexactly to the target, or when you intentionally want a brighter result.\nIt does not change which pixels are recolored (matching), only how colors are mapped.\nDefault: ON (turn OFF per zone when you want it brighter)";

        // 含めるマスク追加(2026-08)以降、このセクションは除外/含めるの両マスクを扱う。
        public static string ExclusionMask => IsJapanese ? "マスク" : "Masks";
        public static string BrushSize => IsJapanese ? "ブラシサイズ" : "Brush Size";
        public static string BrushMode => IsJapanese ? "ブラシモード" : "Brush Mode";
        public static string Exclude => IsJapanese ? "除外" : "Exclude";
        public static string Include => IsJapanese ? "含める" : "Include";
        public static string MaskLayerKind => IsJapanese ? "マスクの種類" : "Mask Type";
        public static string MaskLayerKindTooltip => IsJapanese
            ? "編集するマスクの種類を切り替えます\n・除外: 塗った領域を色替えから外す（色が合っていても変更しない）\n・含める: 塗った領域を必ず色替えに含める（色が合わなくても変更する）\n両方に塗られた画素は「除外」が優先されます"
            : "Switch which kind of mask you edit\n- Exclude: painted areas are never recolored (even if the color matches)\n- Include: painted areas are always recolored (even if the color does not match)\nWhere both overlap, Exclude wins";
        public static string BrushPaint => IsJapanese ? "塗る" : "Paint";
        public static string BrushPaintTooltip => IsJapanese
            ? "ペイントモード: プレビューをドラッグして、いま選んでいる種類のマスクを塗ります\n同じボタンを再度押すとモードを解除"
            : "Paint mode: drag on the preview to paint the currently selected mask type\nClick again to exit paint mode";
        public static string BrushErase => IsJapanese ? "消す" : "Erase";
        public static string BrushEraseTooltip => IsJapanese
            ? "消しゴムモード: プレビューをドラッグして、いま選んでいる種類のマスクを消します\n同じボタンを再度押すとモードを解除"
            : "Eraser mode: drag on the preview to erase the currently selected mask type\nClick again to exit paint mode";
        public static string ClearMask => IsJapanese ? "マスクをクリア" : "Clear Mask";
        public static string MaskHint => IsJapanese
            ? "プレビュー上でドラッグしてマスクを塗り/消しできます"
            : "Drag on the preview to paint or erase the mask";

        public static string Preview => IsJapanese ? "プレビュー" : "Preview";
        public static string GeneratingPreview => IsJapanese ? "⟳ プレビュー生成中..." : "⟳ Generating preview...";
        public static string Zoom => IsJapanese ? "ズーム" : "Zoom";
        public static string SetTexture => IsJapanese
            ? "テクスチャを設定してください。"
            : "Please set a texture.";

        public static string PreviewWindowTitle => IsJapanese ? "いろか プレビュー" : "Iroca Preview";
        public static string OpenPreviewWindow => IsJapanese
            ? "プレビューを別ウィンドウで開く"
            : "Open preview in a separate window";
        public static string FocusPreviewWindow => IsJapanese
            ? "プレビューウィンドウを前面に表示"
            : "Bring the preview window to front";
        public static string OpenPreviewWindowTooltip => IsJapanese
            ? "プレビューを独立したウィンドウで開きます\nズーム・比較/差分・スポイト・マスクのブラシ/AI 操作はそのウィンドウ上で行えます\n開いている間は同じプレビューの二重表示を避けるため、本体ウィンドウ側には表示しません"
            : "Opens the preview in its own window.\nZoom, comparison/diff, the eyedropper, and brush/AI mask editing all work there.\nWhile it is open the main window does not draw the preview, to avoid showing the same preview twice.";
        public static string PreviewDetachedNarrowHint => IsJapanese
            ? "ウィンドウ幅が狭いため、本体にはプレビューを表示していません。別ウィンドウで開くか、ウィンドウ幅を広げてください。"
            : "The window is too narrow, so the preview is not shown here. Open it in a separate window, or widen this window.";
        public static string PreviewDetachedActive => IsJapanese
            ? "プレビューは別ウィンドウで表示中です。"
            : "The preview is currently shown in a separate window.";
        public static string ReattachPreview => IsJapanese
            ? "プレビューを本体に戻す"
            : "Show the preview in the main window";
        public static string ReattachPreviewTooltip => IsJapanese
            ? "プレビューウィンドウを閉じて、本体ウィンドウ内の表示に戻します"
            : "Closes the preview window and shows the preview inside the main window again";

        public static string Export => IsJapanese ? "エクスポート" : "Export";
        public static string NoEnabledZones => IsJapanese
            ? "変更するゾーンがありません。カラーゾーンを追加・有効化してください"
            : "No zones to apply. Add or enable a color zone first.";
        public static string SaveAsNewFile => IsJapanese ? "新規ファイルとして保存" : "Save as new file";
        public static string FileName => IsJapanese ? "ファイル名" : "File Name";
        public static string ApplyAndSave => IsJapanese ? "適用して保存" : "Apply & Save";
        public static string OpenFolder => IsJapanese ? "フォルダを開く" : "Open Folder";

        public static string Error => IsJapanese ? "エラー" : "Error";
        public static string Confirm => IsJapanese ? "確認" : "Confirm";
        public static string Complete => IsJapanese ? "完了" : "Complete";
        public static string OK => "OK";
        public static string Overwrite => IsJapanese ? "上書き" : "Overwrite";
        public static string Cancel => IsJapanese ? "キャンセル" : "Cancel";
        public static string TextureReadError => IsJapanese
            ? "テクスチャが読み込めません。Read/Write Enabled を確認してください。"
            : "Cannot read texture. Please check Read/Write Enabled.";
        public static string TextureLoadError => IsJapanese
            ? "テクスチャファイルの読み込みに失敗しました。PNG または JPG 形式のファイルを使用してください。"
            : "Failed to load texture file. Please use a PNG or JPG file.";
        public static string PathNotFound => IsJapanese
            ? "テクスチャのパスが見つかりません。"
            : "Texture path not found.";
        public static string FileExistsConfirm(string path) => IsJapanese
            ? $"{path} は既に存在します。上書きしますか？"
            : $"{path} already exists. Overwrite?";
        public static string OverwriteConfirm => IsJapanese
            ? "元のテクスチャファイルを上書きします。よろしいですか？"
            : "This will overwrite the original texture file. Are you sure?";
        // ソースが PNG でない（.jpg/.tga 等）ときの上書きは、元ファイルを上書きせず隣に .png を
        // 新規作成する挙動になる。マテリアルは自動で切り替わらない旨を明示して誤解を防ぐ。
        public static string OverwriteNonPngConfirm(string newFileName) => IsJapanese
            ? $"元のテクスチャは PNG ではないため上書きできません。\n代わりに同じ場所へ「{newFileName}」を新規作成します。\nマテリアルの参照は自動で切り替わりません（手動で差し替えてください）。続けますか？"
            : $"The source texture is not a PNG, so it cannot be overwritten.\nInstead, a new file \"{newFileName}\" will be created in the same folder.\nThe material reference will NOT switch automatically (re-assign it manually). Proceed?";
        public static string Saved(string path) => IsJapanese
            ? $"保存しました:\n{path}"
            : $"Saved:\n{path}";

        public static string LayerIndex => IsJapanese ? "L" : "L";

        public static string ZoneDragHandleTooltip => IsJapanese
            ? "ドラッグして並べ替え＝優先度の変更。上にあるゾーンほど優先され、重なった部分は上のゾーンだけが適用されます（下のゾーンのマスクとして機能します）。"
            : "Drag to reorder = change priority. Upper zones take precedence; in overlapping areas only the upper zone is applied (it acts as a mask for lower zones).";
        public static string ZoomHint => IsJapanese
            ? "Ctrl+スクロールでズーム。高解像度プレビューはピクセル単位まで拡大できます（上限はテクスチャ解像度に応じて自動調整）"
            : "Ctrl+scroll to zoom. The high-res preview can be magnified down to pixel level (max zoom auto-scales with texture resolution).";
        public static string ZoomLabel => IsJapanese
            ? "ズーム: {0}%  (Ctrl+スクロール)"
            : "Zoom: {0}%  (Ctrl+Scroll)";
        public static string PanHint => IsJapanese
            ? "ドラッグでパン"
            : "Drag to pan";

        public static string ComparisonMode => IsJapanese ? "前後比較" : "Compare";
        public static string DiffMode       => IsJapanese ? "差分表示" : "Diff";
        public static string Before         => IsJapanese ? "変更前" : "Before";
        public static string After          => IsJapanese ? "変更後" : "After";

        // 実装は Unity 標準 Undo（Undo.PerformUndo）の薄いショートカットで、対象は「直前の操作」。
        // 直前がマスク編集とは限らない（スライダー変更やシーン編集かもしれない）ため、
        // 「マスクを元に戻す」とは名乗らない。名乗ると、マスク以外が巻き戻ったとき破壊的な
        // サプライズになる。
        public static string UndoMask => IsJapanese ? "直前の操作を元に戻す (Ctrl+Z)" : "Undo Last Action (Ctrl+Z)";

        public static string MaskHintPaintOff => IsJapanese
            ? "「塗る」または「消す」を押すとペイントモードになります。同じボタンを押すと解除。"
            : "Click Paint or Erase to enter paint mode. Click the active button again to exit.";

        public static string GeneratingDetailPreview => IsJapanese ? "⟳ 詳細プレビュー生成中..." : "⟳ Generating detail preview...";

        public static string Presets              => IsJapanese ? "プリセット" : "Presets";
        public static string PresetName           => IsJapanese ? "プリセット名" : "Preset Name";
        public static string SavePreset           => IsJapanese ? "保存" : "Save";
        public static string LoadPreset           => IsJapanese ? "読込" : "Load";
        public static string NoPresets            => IsJapanese ? "プリセットがありません" : "No presets saved yet";
        public static string ExportJson           => IsJapanese ? "JSONエクスポート" : "Export JSON";
        public static string ImportJson           => IsJapanese ? "JSONインポート" : "Import JSON";
        public static string PresetStorageProject => IsJapanese ? "プロジェクト内" : "In Project";
        public static string PresetStorageUser    => IsJapanese ? "ユーザー共通" : "Shared (User)";
        public static string DeletePresetConfirm(string name) => IsJapanese
            ? $"プリセット「{name}」を削除しますか？"
            : $"Delete preset '{name}'?";
        public static string PresetOverwriteConfirm(string name) => IsJapanese
            ? $"プリセット「{name}」は既に存在します。上書きしますか？"
            : $"Preset '{name}' already exists. Overwrite?";

        public static string PresetSaved        => IsJapanese ? "プリセットを保存しました" : "Preset saved";
        public static string PresetSaveFailed   => IsJapanese ? "プリセットの保存に失敗しました" : "Failed to save preset";
        public static string PresetLoaded       => IsJapanese ? "プリセットを読み込みました" : "Preset loaded";
        public static string PresetLoadFailed   => IsJapanese ? "プリセットの読み込みに失敗しました" : "Failed to load preset";
        public static string PresetDeleted      => IsJapanese ? "プリセットを削除しました" : "Preset deleted";
        public static string PresetDeleteFailed => IsJapanese ? "プリセットの削除に失敗しました" : "Failed to delete preset";
        public static string MaskSaveFailed     => IsJapanese ? "マスクの保存に失敗しました" : "Failed to save mask";

        public static string ResetSession => IsJapanese ? "リセット" : "Reset";
        public static string ResetSessionTooltip => IsJapanese
            ? "現在のテクスチャのゾーン・色・処理設定・マスクをすべて消して初期状態に戻します（Ctrl+Z の「元に戻す」で復元できます）。"
            : "Clear all zones, colors, processing settings and masks for the current texture and return to the initial state (undoable with Ctrl+Z).";
        public static string ResetSessionConfirm => IsJapanese
            ? "現在のテクスチャのゾーン・色・処理設定・マスクをすべて消して初期状態に戻します。よろしいですか？\n（「元に戻す」で復元できます）"
            : "This clears all zones, colors, processing settings and masks for the current texture. Continue?\n(You can undo this.)";

        public static string BatchApply        => IsJapanese ? "一括適用" : "Batch Apply";
        public static string BatchHint         => IsJapanese
            ? "現在のゾーン設定を複数のテクスチャに一括適用します。出力は各ファイル名に _recolored を付与します。"
            : "Apply current zone settings to multiple textures. Output files are named with _recolored suffix.";
        public static string AddBatchTexture   => IsJapanese ? "+ テクスチャ追加" : "+ Add Texture";
        public static string RemoveBatchTextureTooltip => IsJapanese
            ? "このテクスチャを一括リストから外します"
            : "Remove this texture from the batch list";
        public static string BatchApplyAndSave => IsJapanese ? "一括適用して保存" : "Batch Apply & Save";
        public static string BatchProgress     => IsJapanese ? "一括適用中..." : "Batch processing...";
        public static string BatchComplete(int count) => IsJapanese
            ? $"{count} 件のテクスチャを処理しました"
            : $"Processed {count} texture(s)";

        public static string ZoneEnabledTooltip => IsJapanese
            ? "このゾーンを有効/無効にします"
            : "Enable or disable this zone";
        public static string ZoneNameTooltip => IsJapanese
            ? "ゾーンの識別名（処理には影響しません）"
            : "Zone name for identification (does not affect processing)";
        public static string LayerIndexTooltip => IsJapanese
            ? "処理の適用順序。数値が小さいゾーンほど先に処理されます（デフォルト: 0）"
            : "Processing order. Zones with smaller values are applied first (default: 0)";
        public static string RemoveZoneTooltip => IsJapanese
            ? "このゾーンを削除します"
            : "Remove this zone";
        public static string SelectionModeTooltip => IsJapanese
            ? "ColorPick: サンプルカラーに近い色のピクセルを選択\nUVRect: UV座標の矩形範囲内のピクセルを選択"
            : "ColorPick: Select pixels matching the sampled color\nUVRect: Select pixels within a UV coordinate rectangle";
        public static string SampleColorTooltip => IsJapanese
            ? "選択する基準色。下の「スポイト」ボタンを押してプレビューを直接クリックすると、実テクスチャの色を正確に取得できます"
            : "Reference color for selection. Press the Eyedropper button below and click the preview to sample the exact texture color";
        public static string ToleranceTooltip => IsJapanese
            ? "色の許容範囲。値が大きいほど基準色から離れた色も選択されます（0〜1）"
            : "Color matching tolerance. Higher values select colors further from the sample (0-1)";
        public static string UVRectTooltip => IsJapanese
            ? "選択するテクスチャ上の矩形範囲をUV座標で指定します（0〜1）\nX/Y: 左下の原点、W/H: 幅と高さ"
            : "UV coordinate rectangle for the selected region (0-1)\nX/Y: bottom-left origin, W/H: width and height";
        public static string TargetColorTooltip => IsJapanese
            ? "変更後の色。対象の部分がこの色に変更されます"
            : "Target color. Pixels in this zone will be recolored to this color";

        public static string BrushSizeTooltip => IsJapanese
            ? "ペイントブラシの半径（プレビューの格子セル単位。塗られる円の直径は 2×サイズ+1 セル）。\n1 セルが何テクセルに当たるかは表示倍率で変わります（4K を全体表示すると 1 セル = 10 テクセル超）。"
            : "Brush radius in preview grid cells (the painted circle is 2 x size + 1 cells across).\nHow many texels one cell covers depends on the zoom (over 10 texels per cell when a 4K texture is fully zoomed out).";
        public static string ExcludeTooltip => IsJapanese
            ? "除外マスクを編集対象にします（赤系の表示）\n塗った領域は色が合っていても色替えされません"
            : "Edit the exclusion mask (shown in red)\nPainted areas are never recolored, even if their color matches";
        public static string IncludeLayerTooltip => IsJapanese
            ? "含めるマスクを編集対象にします（緑の表示）\n塗った領域は色が合わなくても必ず色替えに含まれます（色で拾えなかった部分の追加用）\nゾーン別のみ編集できます（共通マスクには「含める」はありません — どのゾーンの色にするかが決まらないため）\n除外マスクと重なった画素は除外が優先されます"
            : "Edit the include mask (shown in green)\nPainted areas are always recolored, even if their color does not match (use it to add areas the color match missed)\nPer-zone only (the common mask has no include layer — there would be no way to know which zone's color to apply)\nWhere it overlaps the exclusion mask, exclusion wins";
        public static string ClearMaskTooltip => IsJapanese
            ? "いま「編集対象」と「マスクの種類」に選ばれているマスク 1 枚だけを消去します。\nほかのマスクはそのまま残ります。"
            : "Erases only the mask currently selected by \"Edit Target\" and \"Mask Type\".\nOther masks are left untouched.";
        public static string UndoMaskTooltip => IsJapanese
            ? "直前の操作を1ステップ前に戻します（Unity 標準の Ctrl+Z と同じ）。"
              + "マスク専用ではないため、直前がマスク以外の操作ならそちらが戻ります"
            : "Undo the last action, same as Unity's Ctrl+Z. This is not mask-specific: "
              + "if your last action was not a mask edit, that action is undone instead";

        public static string BrushEdit => IsJapanese ? "ブラシで編集..." : "Edit with Brush...";
        public static string BrushEditTooltip => IsJapanese
            ? "ブラシ操作パレットを開き、ペイントモードを ON にします\nプレビュー上をドラッグして塗れます（マスクの種類はパレットで切り替え）\nパレットを閉じるとペイントモードは解除されます"
            : "Opens the brush palette and arms the paint brush\nDrag on the preview to paint (switch the mask type in the palette)\nClosing the palette exits paint mode";
        public static string BrushPaletteTitle => IsJapanese ? "Iroca ブラシ" : "Iroca Brush";
        public static string BrushPaletteTargetFormat => IsJapanese ? "編集対象: {0}" : "Editing: {0}";
        public static string BrushPaletteNoHost => IsJapanese
            ? "Iroca ウィンドウが開いていません。"
            : "The Iroca window is not open.";
        public static string OpenIrocaWindow => IsJapanese ? "Iroca を開く" : "Open Iroca";
        public static string OpenIrocaWindowTooltip => IsJapanese
            ? "Iroca 本体ウィンドウを開きます"
            : "Opens the main Iroca window";

        public static string AiSuggest => IsJapanese ? "AI マスク提案（実験的）" : "AI Mask Suggestion (Experimental)";
        public static string AiSuggestStart => IsJapanese ? "AI 提案を開始" : "Start AI Suggestion";
        public static string AiSuggestActive => IsJapanese ? "■ AI 提案中（クリックで終了）" : "■ AI Suggesting (click to stop)";
        public static string AiSuggestToggleTooltip => IsJapanese
            ? "プレビュー上のパーツを右クリック（mac は Control+クリック）すると、AI がそのパーツの領域を推定して、その場で編集対象のマスクへ追加します\n追加先は「マスクの種類」に従います — 除外（＝色替えしない範囲）または含める（＝必ず色替えする範囲）\n左ドラッグはこれまでどおりプレビューの移動（パン）なので、AI モード中でも見たい場所へ寄せられます\n間違えたら Ctrl+Z で 1 つずつ戻せます（確定ボタンはありません）\nブラシペイントとは排他で、開始するとペイントモードは解除されます"
            : "Right-click a part on the preview (Control+click on macOS) and the AI estimates that part's region and adds it to the active mask right away.\nWhere it goes follows \"Mask Type\" — Exclude ('do-not-recolor' area) or Include ('always-recolor' area).\nLeft-drag still pans the preview, so you can move around while this mode is on.\nUndo with Ctrl+Z one step at a time (there is no commit button).\nMutually exclusive with brush painting; starting this exits paint mode.";
        public static string AiSuggestSentisRequired => IsJapanese
            ? "この AI 機能には Unity Sentis パッケージが必要です。下のボタンで導入すると有効になります（不要なら導入しなければ従来どおりの動作で、ストレージも消費しません）。"
            : "This AI feature needs the Unity Sentis package. Install it with the button below to enable it (skip it to keep the classic behavior with no extra storage).";
        public static string AiSuggestInstallSentis => IsJapanese ? "AI 機能を有効化（Sentis を導入）" : "Enable AI feature (install Sentis)";
        public static string AiSuggestInstallSentisTooltip => IsJapanese
            ? "Unity Package Manager 経由で {0}（バージョン {1}）を導入します\n導入後 Unity が自動で再コンパイルし、AI マスク提案が使えるようになります\n（Package Manager から手動で「Add package by name」しても同じです）"
            : "Installs {0} (version {1}) via the Unity Package Manager.\nUnity recompiles automatically afterward and AI mask suggestion becomes available.\n(Equivalent to adding it manually via 'Add package by name'.)";
        public static string AiSuggestInstalling => IsJapanese
            ? "Sentis を導入中... 完了すると Unity が自動で再コンパイルします。"
            : "Installing Sentis... Unity will recompile automatically when it finishes.";
        public static string AiSuggestSentisVersionReplace => IsJapanese
            ? "このプロジェクトには Sentis {0} が導入済みですが、AI マスク提案は {1} でのみ動作確認しています。\n下のボタンを押すと {1} に差し替わります。他のツールが Sentis {0} を使っている場合は、そちらへの影響をご確認ください。"
            : "Sentis {0} is already installed in this project, but AI mask suggestion is verified only with {1}.\nThe button below will replace it with {1}. If another tool depends on Sentis {0}, please check the impact on it first.";
        public static string AiSuggestReplaceSentis => IsJapanese ? "AI 機能を有効化（Sentis を {0} に差し替え）" : "Enable AI feature (replace Sentis with {0})";
        public static string AiSuggestInstallFailed => IsJapanese
            ? "Sentis の導入に失敗しました: {0}\nPackage Manager から手動で「com.unity.sentis」を追加することもできます（手順はマニュアル参照）。"
            : "Failed to install Sentis: {0}\nYou can also add 'com.unity.sentis' manually via the Package Manager (see the manual).";
        public static string AiSuggestRestartRecommended => IsJapanese
            ? "AI 機能を導入しました。確実に有効化するため Unity の再起動を推奨します。\n（再起動しないと、まれに初回だけ内部コンパイル（Burst）の準備が間に合わず AI が動かないことがあります。その場合も再起動で直ります。）"
            : "The AI feature has been installed. Restarting Unity is recommended to enable it reliably.\n(Without a restart, the internal compiler (Burst) can occasionally fail to warm up on the first load and the AI won't run. A restart fixes it.)";
        public static string AiSuggestRestartNow => IsJapanese ? "Unity を再起動" : "Restart Unity";
        public static string AiSuggestRestartNowTooltip => IsJapanese
            ? "現在のプロジェクトを開き直して Unity を再起動します（未保存の変更は先に保存してください）"
            : "Reopens the current project to restart Unity (save any unsaved changes first).";
        public static string AiSuggestRestartLater => IsJapanese ? "後で" : "Later";
        public static string AiSuggestRestartLaterTooltip => IsJapanese
            ? "この案内を閉じます（このまま使えることが多いですが、AI が動かないときは Unity を再起動してください）"
            : "Dismiss this notice (it usually works as-is; if the AI doesn't run, restart Unity).";
        public static string AiSuggestLoadingModel => IsJapanese ? "モデルを読み込み中..." : "Loading model...";
        public static string AiSuggestEncoding => IsJapanese ? "画像を解析中..." : "Analyzing image...";
        public static string AiSuggestDecoding => IsJapanese ? "提案を生成中..." : "Generating proposal...";
        public static string AiSuggestPendingClicksFormat => IsJapanese
            ? "処理待ちクリック {0} 件(順に反映されます)"
            : "{0} click(s) queued (applied in order)";
        public static string AiSuggestNoModel => IsJapanese
            ? "AI モデルが未配置です。モデルをダウンロードして下のフォルダへ配置すると使えるようになります。"
            : "AI model files are not installed. Download the models into the folder below to enable this feature.";
        public static string AiSuggestOpenModelFolder => IsJapanese ? "モデルフォルダを開く" : "Open model folder";
        public static string AiSuggestOpenModelFolderTooltip => IsJapanese
            ? "モデルファイル（.onnx）を配置するフォルダをエクスプローラーで開きます"
            : "Open the folder where the model files (.onnx) should be placed.";
        public static string AiSuggestDownload => IsJapanese ? "モデルをダウンロード" : "Download models";
        public static string AiSuggestDownloadTooltip => IsJapanese
            ? "MobileSAM の AI モデル 2 ファイル（合計約 45MB、Apache-2.0 ライセンス）を\n{0}\nからダウンロードし、モデルフォルダへ自動配置します（sha256 検証つき）"
            : "Download the two MobileSAM model files (about 45MB total, Apache-2.0 license) from\n{0}\nand place them into the model folder automatically (with sha256 verification).";
        public static string AiSuggestDownloading => IsJapanese ? "モデルをダウンロード中..." : "Downloading models...";
        public static string AiSuggestDownloadCancel => IsJapanese ? "中止" : "Cancel";
        public static string AiSuggestDownloadCancelTooltip => IsJapanese
            ? "ダウンロードを中止します（ダウンロード済みの部分ファイルは削除されます）"
            : "Cancel the download (partially downloaded files are deleted).";
        public static string AiSuggestDownloadFailed => IsJapanese
            ? "ダウンロードに失敗しました: {0}\n手動でダウンロードしてモデルフォルダへ配置することもできます（手順はマニュアル参照）"
            : "Download failed: {0}\nYou can also download manually and place the files into the model folder (see the manual).";
        public static string AiSuggestAreaWarning => IsJapanese
            ? "直前のクリックが背景まで広がった可能性があります。Ctrl+Z で戻して、粒度を「細かい」にするか、パーツのより内側を右クリックし直してください。"
            : "The last click may have spread into the background. Undo with Ctrl+Z, then set granularity to 'Fine' or right-click again further inside the part.";
        public static string AiSuggestEmptyCommit => IsJapanese
            ? "直前のクリックではマスクが変わりませんでした（その領域はすでに追加済みです）。"
            : "The last click did not change the mask (that region was already added).";
        public static string AiSuggestEmptyProposal => IsJapanese
            ? "AI が領域を返しませんでした。別の場所を右クリックするか、粒度を変えてみてください。\nどこを右クリックしても同じ場合は、内部コンパイル（Burst）の初期化に失敗している可能性があります。Unity を再起動すると直ります。"
            : "The AI returned no region. Try right-clicking elsewhere or changing the granularity.\nIf every click behaves this way, the internal compiler (Burst) may have failed to initialize — restarting Unity fixes it.";
        public static string AiSuggestBurstFailed => IsJapanese
            ? "この Unity セッションでは内部コンパイル（Burst）が失敗しています。この状態では AI が領域を返せず、右クリックしてもマスクが変わりません。Unity を再起動してください（再起動すれば直ります）。"
            : "The internal compiler (Burst) failed in this Unity session. The AI cannot produce regions in this state, so clicks won't change the mask. Please restart Unity (a restart fixes it).";
        public static string AiSuggestErrorRestartHint => IsJapanese
            ? "推論エンジンの初期化に失敗している可能性があります（Unity 起動時の Burst コンパイル失敗など）。Unity を再起動すると直ることが多いです。AI 提案を一度終了してから開始し直すと再試行します。"
            : "The inference engine may have failed to initialize (e.g. a Burst compile failure at Unity startup). Restarting Unity usually fixes it. Stopping and restarting AI suggestion retries the setup.";
        public static string AiSuggestGranularity => IsJapanese ? "提案の粒度" : "Granularity";
        public static string AiSuggestGranularityTooltip => IsJapanese
            ? "AI はクリック 1 点に対して粒度の違う候補(模様・パーツ・全体)を同時に推定します\nどの候補を提案として表示するかを選びます(次のクリックから適用)"
            : "The AI estimates candidates of different granularity (pattern / part / whole) for one click.\nChoose which candidate to show as the proposal (applies from the next click).";
        public static string AiSuggestGranularityAuto => IsJapanese ? "自動" : "Auto";
        public static string AiSuggestGranularityAutoTooltip => IsJapanese
            ? "モデルの確信度が最も高い候補を採用します(標準)"
            : "Use the candidate the model is most confident about (default).";
        public static string AiSuggestGranularityFine => IsJapanese ? "細かい" : "Fine";
        public static string AiSuggestGranularityFineTooltip => IsJapanese
            ? "最も小さい候補を採用します。模様や小さなパーツ(例: バンダナの三角模様)だけを選びたいときに"
            : "Use the smallest candidate. For selecting patterns or small pieces only.";
        public static string AiSuggestGranularityCoarse => IsJapanese ? "大きい" : "Coarse";
        public static string AiSuggestGranularityCoarseTooltip => IsJapanese
            ? "最も大きい候補を採用します。パーツ全体をまとめて選びたいときに"
            : "Use the largest candidate. For selecting the whole part at once.";
        public static string AiSuggestHintIdle => IsJapanese
            ? "プレビュー上でパーツを右クリック（mac は Control+クリック）すると、その領域をその場で除外マスクへ追加します（マスクの色で表示）。左ドラッグはプレビューの移動です。間違えたら Ctrl+Z で戻せます。"
            : "Right-click a part on the preview (Control+click on macOS) to add its region to the exclusion mask right away (shown in the mask color). Left-drag still pans. Undo with Ctrl+Z.";
        public static string AiSuggestCommitTargetFormat => IsJapanese
            ? "追加先: {0}" : "Add to: {0}";
        public static string AiSuggestNoZoneWarning => IsJapanese
            ? "色ゾーンがありません。マスクは色ゾーンの色替え範囲を制限する機能なので、足してもプレビュー／エクスポートの見た目は変わりません。まず色替えする色ゾーンを追加してください。"
            : "No color zone exists. A mask only limits where color zones recolor, so adding to it will not change the preview/export. Add a color zone to recolor first.";

        public static string MaskTarget => IsJapanese ? "編集対象" : "Edit Target";
        public static string MaskTargetCommon => IsJapanese ? "共通マスク（全ゾーン）" : "Common Mask (all zones)";
        public static string MaskTargetTooltip => IsJapanese
            ? "マスクの編集対象を切り替えます\n・共通マスク: 全ゾーンで共通して除外される領域\n・各ゾーン: そのゾーンだけの除外／含める領域\n除外は共通とゾーンが OR 結合されて適用されます\n「含める」マスクはゾーン選択時のみ編集できます\n※ここは選択のみです。塗るには「ブラシで編集」を押してください"
            : "Switch which mask is being edited\n- Common: area excluded from every zone\n- Per-zone: that zone's exclude / include areas\nExclusions are OR-combined (common + zone) when processing\nThe Include mask can only be edited when a zone is selected\nNote: this only selects the target. To paint, press \"Edit with Brush\"";
        public static string PresetIncludeMasks => IsJapanese
            ? "マスクも保存する"
            : "Include masks when saving";
        public static string PresetIncludeMasksTooltip => IsJapanese
            ? "ON にすると、現在描かれている共通マスク・ゾーン別マスクもプリセットに同梱して保存します"
            : "When ON, currently painted common and per-zone masks are also saved into the preset";
        public static string PresetApplyMasks => IsJapanese
            ? "マスクも読み込む"
            : "Apply masks when loading";
        public static string PresetApplyMasksTooltip => IsJapanese
            ? "ON にすると、プリセットに同梱されたマスクを読み込み時に適用します\nOFF の場合はマスクを無視して他のパラメータのみ読み込みます"
            : "When ON, masks embedded in the preset are applied on load\nWhen OFF, masks are ignored and only other parameters are loaded";
        public static string PresetAdvanced => IsJapanese
            ? "詳細（マスク・JSON入出力）"
            : "Advanced (masks / JSON)";
        public static string PresetAdvancedTooltip => IsJapanese
            ? "マスクの保存/適用オプションと、設定を JSON ファイルとして書き出し/読み込みする機能です"
            : "Mask save/apply options and JSON export/import of settings";

        public static string SaveAsNewFileTooltip => IsJapanese
            ? "ON: 元のファイルを保持して新規ファイルに保存\nOFF: 元のテクスチャを上書き保存"
            : "ON: Save as a new file while keeping the original\nOFF: Overwrite the original texture";
        public static string FileNameTooltip => IsJapanese
            ? "新規保存するファイルの名前（拡張子なし）"
            : "File name for the new file (without extension)";
        public static string ApplyAndSaveTooltip => IsJapanese
            ? "現在のゾーン設定をリカラーとして適用し、テクスチャを保存します"
            : "Apply current zone settings as recolor and save the texture";
        public static string OpenFolderTooltip => IsJapanese
            ? "元テクスチャのあるフォルダをファイルマネージャーで開きます"
            : "Open the folder containing the source texture in the file manager";
        public static string InheritImportSettings => IsJapanese
            ? "インポート設定を引き継ぐ"
            : "Inherit Import Settings";
        public static string InheritImportSettingsTooltip => IsJapanese
            ? "ON: 元テクスチャのインポート設定（テクスチャタイプ・圧縮・ミップマップなど）を出力ファイルに引き継ぎます\nOFF: Unity のデフォルトのインポート設定を使用します"
            : "ON: Copy import settings (texture type, compression, mipmaps, etc.) from the source texture to the output file\nOFF: Use Unity's default import settings";
        public static string AddBatchTextureTooltip => IsJapanese
            ? "一括適用リストにテクスチャを追加します"
            : "Add a texture to the batch apply list";
        public static string BatchApplyAndSaveTooltip => IsJapanese
            ? "リスト内のすべてのテクスチャに現在のゾーン設定を一括適用して保存します\n出力ファイル名は元のファイル名に _recolored を付与"
            : "Apply current zone settings to all textures in the list and save\nOutput files are named with _recolored suffix";

        public static string ComparisonModeTooltip => IsJapanese
            ? "変更前と変更後を横並びで比較表示します（高ズーム時は使用不可）"
            : "Show before and after side by side (unavailable at high zoom)";
        public static string DiffModeTooltip => IsJapanese
            ? "変更前後の差分（変化したピクセル）を強調表示します"
            : "Highlight pixels that changed between before and after";

        public static string PresetStorageProjectTooltip => IsJapanese
            ? "プリセットをProjectのAssetsフォルダ内に保存します。Gitなどでチームと共有できます"
            : "Save presets inside the project's Assets folder. Can be shared via Git.";
        public static string PresetStorageUserTooltip => IsJapanese
            ? "プリセットをOSのユーザーフォルダに保存します。この端末の全プロジェクトで共有されます"
            : "Save presets in the OS user folder. Shared across all projects on this machine.";
        public static string PresetNameTooltip => IsJapanese
            ? "保存するプリセットの名前を入力してください"
            : "Enter a name for the preset to save";
        public static string SavePresetTooltip => IsJapanese
            ? "現在のゾーン設定と加工設定をプリセットとして保存します"
            : "Save current zone and processing settings as a preset";
        public static string ExportJsonTooltip => IsJapanese
            ? "現在の保存先のプリセットをJSONファイルとしてエクスポートします"
            : "Export presets from the current storage location as a JSON file";
        public static string ImportJsonTooltip => IsJapanese
            ? "JSONファイルからプリセット設定をインポートします"
            : "Import preset settings from a JSON file";
        public static string LoadPresetTooltip => IsJapanese
            ? "このプリセットを読み込み、現在のゾーン設定を上書きします"
            : "Load this preset and overwrite current zone settings";
        public static string DeletePresetTooltip => IsJapanese
            ? "このプリセットを削除します"
            : "Delete this preset";

        public static string EnableReadWriteTooltip => IsJapanese
            ? "元テクスチャのインポート設定で Read/Write を有効化します。プレビューと色替えに必要です"
            : "Enable Read/Write in the source texture's import settings. Required for preview and recoloring";
        public static string AddZoneTooltip => IsJapanese
            ? "色替え対象を指定する新しいゾーンを追加します"
            : "Add a new zone to define a recolor target";
        public static string CancelActionTooltip => IsJapanese
            ? "実行中の処理を中止します"
            : "Cancel the running operation";
        public static string CreditTooltip => IsJapanese
            ? "このツールのクレジット（作者・ライセンス）を表示します"
            : "Show credits (author and license) for this tool";
        public static string LanguageToolbarTooltip => IsJapanese
            ? "UI の表示言語を切り替えます（自動 / 日本語 / English）"
            : "Switch the UI display language (Auto / Japanese / English)";
        public static string EditModeToolbarTooltip => IsJapanese
            ? "通常: よく使う設定のみ表示 / 上級: 詳細パラメータも表示します"
            : "Normal: show common settings only / Advanced: also reveal detailed parameters";

        public static string UseFloodFill => IsJapanese ? "連続領域モード (Flood Fill)" : "Connected Region (Flood Fill)";
        public static string UseFloodFillTooltip => IsJapanese
            ? "色が一致した領域のうち、確信度の高い芯を含む『つながった塊』だけに変換を絞り込みます。\n物理的に離れた同色パーツや背景へのにじみ(誤爆)を自動で除去します。\n通常はシード不要(自動)。塊が複数あって特定の1つだけ残したいときは Shift+クリックでシードを指定できます。"
            : "Restrict recoloring to connected regions that contain a high-confidence core.\nAutomatically removes bleed into physically separate same-color parts or the background.\nNo seed needed by default. To keep only one specific region, Shift+click the preview to set a seed.";
        public static string FloodFillSeedPoint => IsJapanese ? "シード (任意)" : "Seed (optional)";
        public static string FloodFillSeedNotSet => IsJapanese ? "自動 (シードなし)" : "Auto (no seed)";
        public static string FloodFillSeedHint => IsJapanese
            ? "Shift+クリックでシードを指定すると、その塊だけを残します(任意)"
            : "Shift+click to set a seed and keep only that region (optional)";
        public static string FloodFillClear => IsJapanese ? "自動へ" : "Auto";
        public static string FloodFillClearTooltip => IsJapanese
            ? "シードを解除して自動アンカリングに戻します"
            : "Clear the seed and return to automatic anchoring";
        public static string EdgeStopThreshold => IsJapanese ? "エッジストッパー強度" : "Edge Stop Threshold";
        public static string EdgeStopThresholdTooltip => IsJapanese
            ? "輝度・彩度の急激な変化をパーツの境界とみなして Flood Fill を止める強度。\n0 = エッジストッパー無効（色の一致のみで拡張）\n大きいほど敏感に止まります（デフォルト: 0.15）"
            : "Sensitivity for stopping Flood Fill at edge (sudden brightness/saturation change).\n0 = disabled (expand by color match only)\nHigher = more sensitive stop (default: 0.15)";

        public static string AutoTune => IsJapanese ? "自動調整" : "Auto-tune";
        // 完了通知に使う。ボタン名と同じ「自動調整」だけを出すと、開始・完了・失敗の区別がつかない。
        public static string AutoTuneDone => IsJapanese ? "自動調整が完了しました" : "Auto-tune complete";
        public static string AnalyzingTexture => IsJapanese ? "テクスチャを解析中…" : "Analyzing texture…";
        public static string AutoTuneTooltip => IsJapanese
            ? "サンプルカラーと変更先カラーから、テクスチャを解析して許容範囲・彩度制限などのパラメータを自動的に決定します。\n上の 2 色（サンプルカラー・変更先カラー）を決めてから押してください。下の項目が結果で上書きされます。"
            : "Analyzes the texture using the sample and target colors and automatically sets tolerance, saturation strictness, and related parameters.\nSet both colors above (sample and target) before pressing; the settings below are overwritten with the result.";
        public static string AutoTuneDisabledTooltip => IsJapanese
            ? "次のいずれかの条件で使用できません:\n・元テクスチャが未設定\n・テクスチャの Read/Write が無効\n・サンプルカラーが未指定（スポイト等でまだ色を取っていない）\n・選択モードが Rect"
            : "Disabled when:\n- Source texture is not set\n- Texture's Read/Write is off\n- Sample color has not been picked yet\n- Selection mode is Rect";
        public static string AutoTuneConfirmTitle => IsJapanese ? "自動調整の確認" : "Confirm Auto-tune";
        public static string AutoTuneOverwriteBody(System.Collections.Generic.IList<string> labels, bool includesGlobals)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(IsJapanese
                ? "以下のパラメータは既定値から変更されています。自動調整で上書きしますか？\n\n"
                : "The following parameters have been modified from defaults. Overwrite via Auto-tune?\n\n");
            for (int i = 0; i < labels.Count; i++)
            {
                sb.Append("・");
                sb.Append(labels[i]);
                sb.Append('\n');
            }
            if (includesGlobals)
            {
                sb.Append(IsJapanese
                    ? "\nグローバル加工設定も変更されます。"
                    : "\nGlobal processing settings will also be modified.");
            }
            return sb.ToString();
        }
    }
}
