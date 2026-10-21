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
        // 閉じたかの控え(描画のたびに EditorPrefs を読まない)。
        private static bool? s_englishNoticeClosed;

        /// <summary>
        /// 英語表示のとき（Auto 判定・手動選択どちらでも）、UI の英訳が AI 補助による機械翻訳であることを
        /// いろかのウィンドウ上部の帯で知らせるか。「閉じる」を押すまで出し、押したらこの PC ではもう出さない。
        /// 以前はエディタ全体を止めるダイアログで、MCP から窓を開いた直後にも出て、OK を押すまで
        /// AI エージェントの次の操作が進まなかった。
        /// </summary>
        public static bool ShowEnglishTranslationNotice =>
            !IsJapanese && !(s_englishNoticeClosed ??= UnityEditor.EditorPrefs.GetBool(EnglishNoticeKey, false));

        public static void CloseEnglishTranslationNotice()
        {
            s_englishNoticeClosed = true;
            UnityEditor.EditorPrefs.SetBool(EnglishNoticeKey, true);
        }

        // 英語表示のときだけ出すので本文は英語。日本語の 1 行は以前のダイアログと同じく残す。
        public static string EnglishTranslationNotice =>
            "The English text in this tool is machine-translated with AI assistance. "
            + "It may contain unnatural or inaccurate wording. The Japanese text is authoritative.\n"
            + "この英語表示は AI による機械翻訳です。";
        public static string EnglishTranslationNoticeClose => IsJapanese ? "閉じる" : "Close";
        public static string EnglishTranslationNoticeCloseTooltip => IsJapanese
            ? "このお知らせを閉じます。次からは表示しません"
            : "Close this notice. It will not be shown again.";

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

        public static string LangAuto     => IsJapanese ? "自動（Auto）" : "Auto";
        public static string LangJapanese => "日本語";
        public static string LangEnglish  => "English";
        public static string Credit       => IsJapanese ? "クレジット" : "Credits";
        public static string Manual       => IsJapanese ? "マニュアル" : "Manual";

        public static string CreditTitle => IsJapanese ? "クレジット" : "Credits";
        public static string CreditOpenList => IsJapanese ? "一覧を開く" : "Open full list";
        public static string CreditClose => IsJapanese ? "閉じる" : "Close";
        // 開発に使わせていただいたモデル・テクスチャを含む全クレジットの公開ページ(リポジトリの CREDITS.md)。
        public const string CreditsUrl = "https://github.com/yukkuri-aoba/Iroca/blob/main/CREDITS.md";
        public static string CreditBody  => IsJapanese
            ? "いろか\n\n制作: yukkuri__aoba\nAI 補助: Claude（Anthropic）\n\nAI マスク提案: MobileSAM（Apache License 2.0）を ONNX に変換して使用\n\n開発に使わせていただいたモデル・テクスチャの一覧は「一覧を開く」から\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Shield License 1.0.0\nhttps://polyformproject.org/licenses/shield/1.0.0"
            : "Iroca\n\nDeveloper: yukkuri__aoba\nAI Assistance: Claude (Anthropic)\n\nAI Mask Suggestion: MobileSAM (Apache License 2.0), converted to ONNX\n\nModels and textures used in development: see \"Open full list\"\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Shield License 1.0.0\nhttps://polyformproject.org/licenses/shield/1.0.0";

        // セクションが等価に並んで開始点が分かりにくいので、主要 4 ステップに番号を振り、
        // テクスチャ未設定時はこの導入ヒントで一連の流れを示す。
        public static string StepPrefixTexture => IsJapanese ? "① " : "1. ";
        public static string StepPrefixZones   => IsJapanese ? "② " : "2. ";
        public static string StepPrefixPreview => IsJapanese ? "③ " : "3. ";
        public static string StepPrefixExport  => IsJapanese ? "④ " : "4. ";
        // ④ は NDMF があれば非破壊の登録、無ければテクスチャへの書き出し(エクスポート欄の主ボタンと揃える)。
#if IROCA_NDMF_PRESENT
        public static string WorkflowHint => IsJapanese
            ? "① 元テクスチャを選ぶ（Scene でモデルをクリックしても開けます）→ ② 変えたい色をスポイトで選び、変更先カラーを決める → ③ プレビューで確認 → ④ アバターに非破壊で登録"
            : "1. Pick a source texture (or click the model in the Scene view)  2. Pick the color to change with the eyedropper and choose the Target Color  3. Check the Preview  4. Register to avatar (non-destructive)";
#else
        public static string WorkflowHint => IsJapanese
            ? "① 元テクスチャを選ぶ（Scene でモデルをクリックしても開けます）→ ② 変えたい色をスポイトで選び、変更先カラーを決める → ③ プレビューで確認 → ④ 適用して保存"
            : "1. Pick a source texture (or click the model in the Scene view)  2. Pick the color to change with the eyedropper and choose the Target Color  3. Check the Preview  4. Apply & Save";
#endif

        public static string SourceTexture => IsJapanese ? "元テクスチャ" : "Source Texture";
        public static string Texture => IsJapanese ? "テクスチャ" : "Texture";
        public static string TextureTooltip => IsJapanese
            ? "色を変えたいテクスチャをここにドラッグ＆ドロップするか、右の◎から選びます。\nゾーンとマスクはテクスチャごとに保存され、切り替えると読み戻されます。"
            : "Drag & drop the texture you want to recolor here, or pick it with the ◎ button.\nZones and masks are saved per texture and restored when you switch back.";
        public static string ReadWriteError => IsJapanese
            ? "Read/Write Enabled がオフです。\nインポート設定で有効にしてください。"
            : "Read/Write Enabled is off.\nPlease enable it in the import settings.";
        public static string EnableReadWrite => IsJapanese
            ? "Read/Write を有効にする"
            : "Enable Read/Write";
        public static string EnableReadWriteConfirm => IsJapanese
            ? "このテクスチャのインポート設定で Read/Write を有効にします。\nこの変更は Undo できません。続けますか？"
            : "This turns on Read/Write in the texture's import settings.\nThis cannot be undone. Continue?";
        // {0}×{1} = 元画像の寸法、{2} = 使うメモリの目安(GB)、{3} = この PC の搭載メモリ(GB)。
        // 作業解像度も元画像のまま(取り込み設定の Max Size が元画像以上)のとき。
        public static string LargeSourceNoticeFormat => IsJapanese
            ? "大きい画像です（{0}×{1}）。インポート設定の Max Size がこの大きさ以上なので元の解像度で処理し、メモリを {2:0} GB ほど使います（この PC は {3:0} GB）。足りないと Unity が重くなったり落ちたりします。そのときはほかのアプリを閉じるか、Max Size を下げてください（編集はその大きさで行い、書き出し（「適用して保存」）だけ元の解像度で行います）。"
            : "Large image ({0}×{1}). The import Max Size is at least this size, so Iroca works at the original resolution and uses about {2:0} GB of memory (this PC has {3:0} GB). If memory runs short, Unity may slow down or crash. Close other apps or lower the Max Size (editing then happens at that size, and only exporting (\"Apply & Save\") uses the original resolution).";
        // 「元の解像度で編集」がオンで、元画像のまま処理しているとき。引数は LargeSourceNoticeFormat と同じ。
        public static string LargeSourceAtSourceNoticeFormat => IsJapanese
            ? "大きい画像です（{0}×{1}）。「元の解像度で編集」がオンなので元の解像度で処理し、メモリを {2:0} GB ほど使います（この PC は {3:0} GB）。足りないと Unity が重くなったり落ちたりします。そのときはほかのアプリを閉じるか、「加工設定」の「元の解像度で編集」をオフにしてインポート設定の Max Size を下げてください。"
            : "Large image ({0}×{1}). \"Edit at original resolution\" is on, so Iroca works at the original resolution and uses about {2:0} GB of memory (this PC has {3:0} GB). If memory runs short, Unity may slow down or crash. Close other apps, or turn off \"Edit at original resolution\" under \"Processing\" and lower the import Max Size.";
        // 編集は取り込み後の寸法で、書き出しだけ元画像の解像度のとき。{0}×{1} = 元画像、{2}×{3} = 編集の寸法、
        // {4} = 書き出しで使うメモリの目安(GB)、{5} = この PC の搭載メモリ(GB)。
        public static string LargeSourceExportNoticeFormat => IsJapanese
            ? "大きい画像です（{0}×{1}）。編集はインポート設定の大きさ（{2}×{3}）で行いますが、書き出し（「適用して保存」）は元の解像度で行うため、書き出すときにメモリを {4:0} GB ほど使います（この PC は {5:0} GB）。足りないと Unity が重くなったり落ちたりするので、書き出す前にほかのアプリを閉じてください。"
            : "Large image ({0}×{1}). Editing happens at the import size ({2}×{3}), but exporting (\"Apply & Save\") uses the original resolution, so it uses about {4:0} GB of memory (this PC has {5:0} GB). If memory runs short, Unity may slow down or crash, so close other apps before exporting.";
        public static string LargeSourceDismiss => IsJapanese ? "閉じる" : "Dismiss";
        public static string LargeSourceDismissTooltip => IsJapanese
            ? "このテクスチャではこの案内を表示しません。次に Unity を開いたときにまた出ます。"
            : "Hide this notice for this texture. It comes back the next time you open Unity.";

        public static string ColorZones => IsJapanese ? "カラーゾーン" : "Color Zones";
        // 名前未設定ゾーンの表示名（マスク対象プルダウン・ドラッグゴースト等で使用）。
        public static string UnnamedZone => IsJapanese ? "ゾーン" : "Zone";
        // 色を足す入口は「スポイトで…」のボタン（StartWithEyedropper / AddColorWithEyedropper）。
        // こちらは色を決めていない空のゾーンを作る脇の入口なので、その旨が分かる名前にしている。
        public static string AddZone => IsJapanese ? "+ 空のゾーン" : "+ Empty zone";
        public static string SelectionMode => IsJapanese ? "選択モード" : "Selection Mode";
        public static string SampleColor => IsJapanese ? "サンプルカラー" : "Sample Color";

        // サンプルカラー欄と同一行に置くため短縮ラベル（詳細説明は EyedropperTooltip が保持）。
        public static string EyedropperIdle => IsJapanese
            ? "スポイト"
            : "Eyedropper";
        public static string EyedropperActive => IsJapanese
            ? "■ クリックで取得"
            : "■ Click to pick";
        public static string EyedropperTooltip => IsJapanese
            ? "押すとスポイトモードになり、プレビュー上をクリックするとそのピクセルの色を\nこのゾーンの「サンプルカラー」に取り込みます。\n実テクスチャの画素から直接取得するため、カラーピッカーのスポイトより正確です。\nもう一度押すと解除します。"
            : "Enters eyedropper mode; click the preview to load that pixel's color into this zone's Sample Color.\nIt reads the actual texture pixel directly, so it is more accurate than the color picker's eyedropper.\nClick again to cancel.";
        public static string Tolerance => IsJapanese ? "許容範囲" : "Tolerance";
        public static string UVRect => IsJapanese ? "UV 範囲（0-1）" : "UV Rect (0-1)";
        public static string TargetColor => IsJapanese ? "変更先カラー" : "Target Color";
        public static string PatternPreserve => IsJapanese ? "模様保持" : "Pattern Preserve";
        public static string PatternPreserveTooltip => IsJapanese
            ? "0 = 変更先の明度に完全に合わせる（ベタ塗り風）\n1 = 元の明度を完全保持（模様が残りやすい）"
            : "0 = Match target brightness entirely (flat recolor)\n1 = Keep original brightness (preserves patterns)";
        public static string OutputSaturation => IsJapanese ? "出力彩度" : "Output Saturation";
        public static string OutputSaturationTooltip => IsJapanese
            ? "色替え後の鮮やかさ。1.0 = 変更先カラーの鮮やかさのまま。\n真っ赤のように鮮やかさが最大の色は陰影がつぶれてベタ塗りに見えやすいので、少し下げる（0.7〜0.9）と立体感が戻ります。\n既定: 1.0"
            : "Saturation after recoloring. 1.0 = use the target color's full vividness.\nFully saturated colors (e.g. pure red) tend to flatten brightness gradients (a solid-fill look).\nLowering it (0.7-0.9) keeps the hue but restores the brightness gradient / shading.\nDefault: 1.0";
        public static string EdgeSoftness => IsJapanese ? "エッジ柔らかさ" : "Edge Softness";
        public static string EdgeSoftnessTooltip => IsJapanese
            ? "0 = くっきりした縁\n1 = 柔らかい縁（アンチエイリアスの境界をなめらかに）"
            : "0 = Hard edge\n1 = Soft edge (smooth anti-aliased boundaries)";
        public static string SaturationStrictness => IsJapanese ? "彩度制限（影の厳しさ）" : "Saturation Strictness";
        public static string SaturationStrictnessTooltip => IsJapanese
            ? "選んだ色の薄い影や AO（暗い陰り）を、どこまで同じ色として拾うかの厳しさです。\n上げる → はみ出しが減るが、境界に色の点が残りやすい\n下げる → 点は減るが、周りへはみ出しやすい\n「彩度ガード」は白・黒・灰色そのものを外す設定です。\n既定: 0.50"
            : "How strictly faint shadows / AO of the picked color count as part of the selection.\nHigher → less bleed, but color dots may remain at edges\nLower → fewer dots, but more bleed into surroundings\n'Saturation Guard' is the one that excludes white/black/gray pixels.\nDefault: 0.50";

        public static string SaturationGuard => IsJapanese ? "彩度ガード（無彩色よけ）" : "Saturation Guard";
        public static string SaturationGuardTooltip => IsJapanese
            ? "鮮やかな色を選んだとき、白・黒・灰色のような色味のない部分が混ざるのを防ぎます。\n0 = オフ、1 = 強く外す\n選んだ色が灰色寄りなら自動でオフになります。\n許容範囲を大きく上げたときに、関係ない白や黒の巻き込みを抑えるのに使います。\n既定: 0"
            : "Keeps colorless areas (white/black/gray) out of the result when you pick a vivid color.\n0 = off, 1 = strict\nTurns itself off when the picked color is grayish.\nUse it after raising Tolerance a lot, to keep unrelated white or black out.\nDefault: 0";

        public static string Processing => IsJapanese ? "加工設定" : "Processing";
        public static string EdgeFeather => IsJapanese ? "エッジぼかし" : "Edge Feather";
        public static string EdgeFeatherTooltip => IsJapanese
            ? "選択範囲の縁をぼかして、なめらかにつなぎます。\n0 = オフ"
            : "Blurs the edge of the selection for a smoother transition.\n0 = off";

        public static string AntiAliasCleanup => IsJapanese ? "縁のドット回収" : "AA Edge Cleanup";
        public static string AntiAliasCleanupTooltip => IsJapanese
            ? "アンチエイリアス境界の残りドットを除去するパス数。\n0 = オフ、3 = 既定（推奨）、5 = 最大\n値を大きくすると境界の回収範囲が広がります。\n初期設定（境界クリーンアップ ON・エッジぼかし 0）では、色のあるパーツには効きません。縁は境界クリーンアップが塗ります。"
            : "Number of passes to recover anti-alias boundary pixels.\n0 = off, 3 = default (recommended), 5 = max\nHigher values recover more edge pixels.\nWith the defaults (Edge Decontamination ON, Edge Feather 0) this has no effect on colored parts; Edge Decontamination paints their edges.";

        public static string UseDecontamination => IsJapanese ? "境界クリーンアップ" : "Edge Decontamination";
        public static string UseDecontaminationTooltip => IsJapanese
            ? "境界の混ざった色（アンチエイリアス・にじみ）を、パーツの色が混ざっている割合だけ色替えします。\n縁に元の色が残る、パーツの周りに輪が出る、境界が薄汚れた中間色になる、を防ぎます。\n推奨: ON"
            : "Recolors the mixed colors at the boundary (anti-aliasing, bleeding) only by the share of the part's color in them.\nPrevents leftover original color along edges, rings around parts, and a muddy halo.\nRecommended: ON";

        public static string DecontaminationRadius => IsJapanese ? "境界クリーンアップの半径" : "Decontamination Radius";
        public static string DecontaminationRadiusTooltip => IsJapanese
            ? "境界の画素について、パーツの色と背景の色を探す近傍の半径。\n小さい = シャープな境界に対応、大きい = ノイズの多い背景に対応\n既定: 4"
            : "Radius searched around a boundary pixel for the part's color and the background color.\nSmaller = sharper boundaries, larger = noisier backgrounds\nDefault: 4";

        // ゾーンカード内の詳細パラメータ折りたたみ見出し（既定で畳む）。
        public static string ZoneDetailFoldout => IsJapanese ? "詳細設定" : "Details";
        public static string ZoneDetailFoldoutTooltip => IsJapanese
            ? "連続領域モード・彩度制限・ハイライト・暗部などの詳細パラメータを開閉します。通常は自動調整に任せて閉じたままで構いません。"
            : "Show/hide advanced parameters (connected regions, saturation limits, highlights, shadows). Usually you can leave this closed and rely on auto-tune.";

        public static string ResetZoneTuning => IsJapanese ? "詳細を既定値に戻す" : "Reset details to default";
        public static string ResetZoneTuningTooltip => IsJapanese
            ? "このゾーンの詳細パラメータ（エッジ・彩度・シャドウ/ハイライト等）と、模様保持・出力彩度を既定値に戻します。\nサンプル/変更先カラー・許容範囲・名前・連続領域モード・シードは変わりません。試行錯誤で値を崩したときの復旧用です。"
            : "Resets this zone's detailed parameters (edge, saturation, shadow/highlight, etc.) and Pattern Preserve / Output Saturation to defaults.\nSample/target color, tolerance, name, Connected Region, and the seed are kept. Handy to recover after over-tweaking.";

        public static string HoleFillPasses => IsJapanese ? "穴埋めパス数" : "Hole Fill Passes";
        public static string HoleFillPassesTooltip => IsJapanese
            ? "アンチエイリアス端の孤立ドットを除去するパス数。\n多いほど大きなギャップを埋めますが、過剰に埋める可能性があります。\n既定: 5\n初期設定（境界クリーンアップ ON・エッジぼかし 0）では、色のあるパーツには効きません。縁は境界クリーンアップが塗ります。"
            : "Passes to fill isolated dots at anti-aliased edges.\nMore passes fill larger gaps but may over-fill.\nDefault: 5\nWith the defaults (Edge Decontamination ON, Edge Feather 0) this has no effect on colored parts; Edge Decontamination paints their edges.";

        public static string HoleFillMinNeighbors => IsJapanese ? "穴埋め最小隣接数" : "Hole Fill Min Neighbors";
        public static string HoleFillMinNeighborsTooltip => IsJapanese
            ? "穴を埋めるために必要なマッチした隣接ピクセルの最小数。\n低い値 = より積極的に穴を埋める\n高い値 = より保守的\n既定: 4\n初期設定（境界クリーンアップ ON・エッジぼかし 0）では、色のあるパーツには効きません。縁は境界クリーンアップが塗ります。"
            : "Minimum matched neighbors required to fill a hole.\nLower = more aggressive filling\nHigher = more conservative\nDefault: 4\nWith the defaults (Edge Decontamination ON, Edge Feather 0) this has no effect on colored parts; Edge Decontamination paints their edges.";

        public static string RelaxedSatMin => IsJapanese ? "境界復元 彩度最小" : "Boundary Sat Min";
        public static string RelaxedSatMinTooltip => IsJapanese
            ? "境界復元時の彩度最小閾値。\n低い値 = より多くの境界ピクセルを回収\n高い値 = より厳格な回収\n既定: 0.02\n初期設定（境界クリーンアップ ON・エッジぼかし 0）では、色のあるパーツには効きません。縁は境界クリーンアップが塗ります。"
            : "Minimum saturation threshold for boundary recovery.\nLower = recover more boundary pixels\nHigher = stricter recovery\nDefault: 0.02\nWith the defaults (Edge Decontamination ON, Edge Feather 0) this has no effect on colored parts; Edge Decontamination paints their edges.";

        public static string ValueWeight => IsJapanese ? "明度重み" : "Value Weight";
        public static string ValueWeightTooltip => IsJapanese
            ? "距離式における明度（V）の重み。\n高い値 = 明度差に敏感（異なる素材を分離しやすい）\n低い値 = 明度差を許容（同じ素材の影/ハイライト変動を吸収）\n既定: 1.0"
            : "Weight of value (brightness) in the distance formula.\nHigher = sensitive to brightness differences (separates different materials)\nLower = tolerates brightness variation (absorbs shadow/highlight of same material)\nDefault: 1.0";

        public static string SatDistWeight => IsJapanese ? "彩度距離重み" : "Sat Distance Weight";
        public static string SatDistWeightTooltip => IsJapanese
            ? "距離式における彩度距離の重み。\n高い値 = 彩度差に敏感\n低い値 = 彩度差を許容\n既定: 0.15"
            : "Weight of saturation distance in the distance formula.\nHigher = sensitive to saturation differences\nLower = tolerates saturation differences\nDefault: 0.15";

        public static string SatRampScale => IsJapanese ? "彩度ランプスケール" : "Sat Ramp Scale";
        public static string SatRampScaleTooltip => IsJapanese
            ? "動的彩度ランプのスケール係数。\nsatRamp = Max(0.08, サンプル彩度 × この値)\n大きい値 = 彩度閾値付近で段階的なフェードイン\n小さい値 = より急激な閾値\n既定: 0.10"
            : "Scale factor for the dynamic saturation ramp.\nsatRamp = Max(0.08, sampleSat × this)\nLarger = more gradual fade near threshold\nSmaller = sharper threshold\nDefault: 0.10";

        public static string ShadowDesaturation => IsJapanese ? "暗部の彩度低下" : "Shadow Desaturation";
        public static string ShadowDesaturationTooltip => IsJapanese
            ? "この明るさより暗い画素は、色替え後の鮮やかさを少し落とします（暗い所が不自然に鮮やかになるのを防ぐため）。下げると暗い所も鮮やかに染まります。0 = オフ\n既定: 0.35"
            : "Pixels darker than this get slightly less saturation after recoloring, so dark areas don't look unnaturally vivid. Lower it to keep dark areas vivid. 0 = off\nDefault: 0.35";

        public static string ShadowForgivenessSatMin => IsJapanese ? "暗部に含める最低彩度" : "Shadow Min Saturation";
        public static string ShadowForgivenessSatMinTooltip => IsJapanese
            ? "グレーや黒のピクセルを同系色の影として巻き込むのを防ぐための最低彩度です。\n既定: 0.05"
            : "Minimum saturation required to include a dark pixel as part of the shadow. Prevents pure grays from being colorized.\nDefault: 0.05";

        public static string ShadowValueFloor => IsJapanese ? "暗部の明度下限" : "Shadow Value Floor";
        public static string ShadowValueFloorTooltip => IsJapanese
            ? "この明度以上のピクセルが半分に満たない離れた領域は、同系色でも同じ素材の陰影とみなさず選択から外します。\n同じ色相で暗いだけの別パーツ（上衣に対する暗いジャケット、肌に対する口の中など）の巻き込みを防ぎます。本体に地続きの影は残ります。\n自動調整（AI 提案あり）がクリックした部分に実在する暗さから導きます。連続領域モードでのみ効きます。0 = 無効\n既定: 0"
            : "Detached regions where fewer than half of the pixels are at or above this value are not treated as shading of the same material, even when the hue matches.\nStops darker parts of the same hue (a dark jacket next to a top, the inside of a mouth next to skin) from being included; shading connected to the body is kept.\nAuto-tune with an AI proposal derives it from the darkness actually present in the clicked part. Only in connected-region mode. 0 = off\nDefault: 0";

        public static string PartSatCeiling => IsJapanese ? "別パーツの彩度上限" : "Part Saturation Ceiling";
        public static string PartSatCeilingTooltip => IsJapanese
            ? "彩度がこの値以下のピクセルが半分に満たない離れた領域は、同系色でも別のパーツとみなして選択から外します。\n色の近い、より鮮やかな別パーツ（濃いグレーのパンツに対する焦げ茶のブーツなど）の巻き込みを防ぎます。本体に地続きの鮮やかな陰影や装飾は残ります。\n自動調整（AI 提案あり）がクリックした部分に実在する彩度の広がりから導きます。連続領域モードでのみ効きます。0 = 無効\n既定: 0"
            : "Detached regions where fewer than half of the pixels are at or below this saturation are treated as a different part, even when the color is close.\nStops more vivid parts of a similar color (dark brown boots next to dark gray trousers) from being included; vivid shading and trim connected to the body are kept.\nAuto-tune with an AI proposal derives it from the saturation range actually present in the clicked part. Only in connected-region mode. 0 = off\nDefault: 0";

        public static string PartHueBand => IsJapanese ? "別パーツの色相幅" : "Part Hue Band";
        public static string PartHueBandTooltip => IsJapanese
            ? "色相がスポイト色からこの幅に入るピクセルが 4 分の 1 に満たない離れた領域は、色が近くても別のパーツとみなして選択から外します。\n色相が少しずれた別パーツ（焦げ茶のブーツに対するグレーがかったパンツ、紺の瞳に対する紫の影など）の巻き込みを防ぎます。彩度の低いピクセルは色相が不安定なので判定に使いません。\n自動調整（AI 提案あり）がクリックした部分に実在する色相の広がりから導きます。連続領域モード・色のあるスポイトでのみ効きます。0 = 無効\n既定: 0"
            : "Detached regions where fewer than a quarter of the pixels fall within this hue distance from the sampled color are treated as a different part, even when the color is close.\nStops parts whose hue is slightly off (grayish trousers next to dark brown boots, a purple shadow next to a navy iris) from being included. Low-saturation pixels are not used because their hue is unstable.\nAuto-tune with an AI proposal derives it from the hue range actually present in the clicked part. Only in connected-region mode with a chromatic sample. 0 = off\nDefault: 0";

        // 「ハイライト」は独立した小見出しへ分けたので、ここは暗部と無彩色の扱いだけを指す
        // （2026-09-11 の詳細パラメータのグループ化。旧名は「シャドウ・ハイライト詳細設定」）。
        public static string ShadowHighlightSection => IsJapanese ? "暗部・無彩色" : "Shadows and neutrals";

        public static string ChromaCeiling => IsJapanese ? "無彩色の彩度上限" : "Neutral Chroma Ceiling";
        public static string ChromaCeilingTooltip => IsJapanese
            ? "サンプルが無彩色（白/グレー/黒）のとき、この彩度を超える離れた領域は「染められた別素材」とみなして選択から外します。\n0 = 自動（サンプル彩度の 3 倍、最低 0.04）。白い布の青みがかった陰のように、素材自身の陰影が地色より強く色づく場合は上げます。\n自動調整（AI 提案あり）がクリックした部分に実在する彩度から導きます。\n既定: 0"
            : "When the sample is neutral (white/gray/black), detached regions more saturated than this are treated as a dyed, different material and excluded.\n0 = automatic (3x the sample saturation, at least 0.04). Raise it when the material's own shading is more tinted than its base, such as bluish shadows on white cloth.\nAuto-tune with an AI proposal derives it from the saturation actually present in the clicked part.\nDefault: 0";

        public static string HoleFillMargin => IsJapanese ? "穴埋めの余白" : "Hole Fill Margin";
        public static string HoleFillMarginTooltip => IsJapanese
            ? "選択に囲まれた小さな穴を埋めるとき、許容範囲をこの割合だけ広げて判定します。\n布の明るい面のように色が許容範囲の境目をまたぐ所で、元の色が点々と残るのを防ぎます。囲まれていない画素は広がりません。\n自動調整（AI 提案あり）が設定します。許容範囲を手で広く取ったときに上げると、別の部位が塊で塗られることがあります。0 = 余白なし\n既定: 0\n初期設定（境界クリーンアップ ON・エッジぼかし 0）では、色のあるパーツには効きません。"
            : "When filling small holes surrounded by the selection, the tolerance is widened by this fraction.\nStops dots of the original color from being left where the color straddles the tolerance edge, such as the lit side of cloth. Pixels that are not surrounded are not added.\nAuto-tune with an AI proposal sets it. Raising it with a wide manual tolerance can paint other parts in blocks. 0 = no margin\nDefault: 0\nWith the defaults (Edge Decontamination ON, Edge Feather 0) this has no effect on colored parts.";

        public static string ChromaThreshold => IsJapanese ? "無彩色とみなす彩度" : "Neutral Threshold";
        public static string ChromaThresholdTooltip => IsJapanese
            ? "スポイトで取った色の彩度がこの値以下なら、無彩色（白・灰色・黒）として扱い、色相を見ずに選びます。"
            : "If the sampled color's saturation is at or below this value, it is treated as neutral (white/gray/black) and hue is ignored.";

        public static string HighlightRecovery => IsJapanese ? "ハイライト補助" : "Highlight Recovery";
        public static string HighlightRecoveryTooltip => IsJapanese
            ? "明るく色の薄いハイライトも同じ色として拾い、光沢のある素材の塗り残しを防ぎます。\n既定: ON"
            : "Also picks up bright, pale highlights as the same color, so glossy materials are not left unrecolored.\nDefault: ON";

        public static string HighlightBandExpand => IsJapanese ? "ハイライト帯の拡張" : "Highlight Band Expansion";
        public static string HighlightBandExpandTooltip => IsJapanese
            ? "本体から続くハイライト（同じ色が白っぽく明るくなった所）まで、色替えの範囲を広げます。許容範囲を上げずに、ハイライトの塗り残しを防ぎます。\n本体とつながった所だけ広げるので、別のパーツや白い素材には広がりません。\n「ハイライト補助」が ON のときだけ効きます。\n既定: ON"
            : "Extends the recolor from the body into connected highlights (the same color washed toward white), without raising Tolerance.\nOnly areas connected to the body are added, so it does not spread into other parts or white material.\nWorks only when Highlight Recovery is ON.\nDefault: ON";

        public static string ApplyHighlightWash => IsJapanese ? "ハイライト白寄せ合成" : "Highlight White Blend";
        public static string ApplyHighlightWashTooltip => IsJapanese
            ? "明部（サンプルより明るい部分）を白方向へ寄せて、鏡面ハイライトの白い反射を表現します。\n光沢・プラスチックなど、ハイライトが白く飛ぶ素材で効果的です。\nOFF のときは色相を変えるだけで、明部の明度・彩度はそのまま保たれます。\nON でも、別色の有彩な模様は「軸残差フェード」で保護され、本来の鏡面（地色が白く飛んだ部分）だけが白寄せされます。\n既定: OFF（必要なゾーンだけ ON）"
            : "Pushes bright areas (brighter than the sample) toward white to reproduce the white reflection of specular highlights.\nUseful for glossy/plastic materials where highlights blow out to white.\nWhen OFF, only the hue changes and the brightness/saturation structure of bright areas is preserved.\nEven when ON, off-hue colored patterns are protected by an axis-residual fade, so only genuine specular highlights (the base color washed to white) get the white blend.\nDefault: OFF (enable per zone as needed)";

        public static string AutoHighlightSample => IsJapanese ? "ハイライト自動補正" : "Auto Highlight Sample";
        public static string AutoHighlightSampleTooltip => IsJapanese
            ? "「ハイライト白寄せ合成」の配下オプション。ON のときのみ有効です。\n明るい光沢部をスポイトすると、中央ハイライトのグラデーションが潰れて「ベタ塗り」に見えることがあります。\nこの設定は、パーツの地色（同じ色相の中間トーン）をテクスチャから自動で見つけ、白寄せ合成がドーム全体に効くようにして鏡面の立体感を出します。色替えする範囲は変えず、見え方だけを補正します。\n髪など細い房が多いテクスチャでは白寄せが広がりすぎることがあるため、その場合は OFF にしてください。\n既定: OFF（必要なゾーンだけ ON）"
            : "A sub-option of \"Highlight White Blend\"; only active when that is ON.\nWhen you eyedrop a bright glossy spot, the central highlight gradient can collapse into a flat look.\nThis finds the part's base tone (same-hue mid tone) from the texture automatically so the white blend covers the whole dome, making specular shading appear. It does not change which pixels are recolored (matching), only how it looks.\nNote: on hair-like textures with many thin strands the white-ward blend can spread too much - turn this OFF there.\nDefault: OFF (enable per zone as needed)";

        public static string AutoRecolorAnchor => IsJapanese ? "基準色の自動補正" : "Auto Sample Anchor";
        public static string AutoRecolorAnchorTooltip => IsJapanese
            ? "パーツの明るい面が「変更先カラー」と同じ色になるように、色の合わせ方を自動で補正します。影の所をスポイトしても、仕上がりが明るくなりすぎません。\nOFF にすると、スポイトした画素そのものが変更先カラーになります（わざと明るく塗りたいときなど）。\n色替えする範囲は変わりません。\n既定: ON（明るくしたいゾーンだけ OFF）"
            : "Adjusts how colors are mapped so the lit side of the part matches the Target Color, even if you sampled a shadow.\nWhen OFF, the sampled pixel itself becomes the Target Color (e.g. when you want a brighter result).\nIt does not change which pixels are recolored.\nDefault: ON (turn OFF per zone when you want it brighter)";

        // 含めるマスク追加(2026-08)以降、このセクションは除外/含めるの両マスクを扱う。
        public static string ExclusionMask => IsJapanese ? "マスク" : "Masks";
        public static string BrushSize => IsJapanese ? "ブラシサイズ" : "Brush Size";
        public static string Exclude => IsJapanese ? "除外" : "Exclude";
        public static string Include => IsJapanese ? "含める" : "Include";
        public static string MaskLayerKind => IsJapanese ? "マスクの種類" : "Mask Type";
        public static string MaskLayerKindTooltip => IsJapanese
            ? "編集するマスクの種類を切り替えます\n・除外: 塗った領域を色替えから外す（色が合っていても変更しない）\n・含める: 塗った領域を必ず色替えに含める（色が合わなくても変更する）\n同じゾーンでは後から塗ったほうが勝ちます（含めるを塗るとその場所の除外が消え、除外を塗ると含めるが消えます）\n共通マスクの除外は、ゾーンの含めるより優先されます"
            : "Switch which kind of mask you edit\n- Exclude: painted areas are never recolored (even if the color matches)\n- Include: painted areas are always recolored (even if the color does not match)\nWithin a zone, the later paint wins (painting Include removes Exclude there, and painting Exclude removes Include)\nThe common mask's Exclude wins over a zone's Include";
        public static string BrushPaint => IsJapanese ? "塗る" : "Paint";
        public static string BrushPaintTooltip => IsJapanese
            ? "ペイントモード: プレビューをドラッグして、いま選んでいる種類のマスクを塗ります\n同じボタンを再度押すとモードを解除"
            : "Paint mode: drag on the preview to paint the currently selected mask type\nClick again to exit paint mode";
        public static string BrushErase => IsJapanese ? "消す" : "Erase";
        public static string BrushEraseTooltip => IsJapanese
            ? "消しゴムモード: プレビューをドラッグして、いま選んでいる種類のマスクを消します\n同じボタンを再度押すとモードを解除"
            : "Eraser mode: drag on the preview to erase the currently selected mask type\nClick again to exit paint mode";
        public static string AiSuggestSectionHeader => IsJapanese ? "AI 提案（プレビューを右クリック）" : "AI Suggest (right-click the preview)";
        public static string AiSuggestSectionHeaderTooltip => IsJapanese
            ? "AI 提案（実験的）: プレビューを右クリックして「ここも塗る / ここは塗らない / この部分だけ塗る」を選ぶと、\nクリックした場所の部分を AI が見分けて、メニューの見出しのゾーンに当てます\nここでは見分ける粒度を選べます。推論の進み具合も確認できます"
            : "AI Suggest (experimental): right-click the preview and choose \"Paint here too / Don't paint here / Paint only this part\";\nthe AI finds the part you clicked and applies it to the zone shown at the top of the menu\nHere you can set the granularity and see the inference status";
        public static string AiSuggestNoModelInEditor => IsJapanese
            ? "AI モデルが未取得です。いろか本体ウィンドウ上部の案内から取得してください。"
            : "The AI model has not been downloaded yet. Get it from the notice at the top of the main Iroca window.";
        public static string MaskHintContextMenu => IsJapanese
            ? "プレビューを右クリックすると「ここも塗る / ここは塗らない / この部分だけ塗る」を、AI が見分けた部分やメッシュの形で当てられます（Ctrl+Z で 1 手ずつ戻せます）"
            : "Right-click the preview to apply \"Paint here too / Don't paint here / Paint only this part\" to the part the AI finds or to a mesh shape (Ctrl+Z undoes one step at a time)";

        // ─── プレビューの右クリックメニュー(項目にツールチップを付けられないので、文言だけで分かるようにする) ───
        // 「追加先」「除外」「含める」はマスクの仕組みを知らないと選べないので、メニューでは使わない(2026-10-06)。
        public static string CtxHeaderZoneFormat => IsJapanese ? "「{0}」を直す" : "Fix \"{0}\"";
        // 右クリックした場所を、いま直しているゾーンとは別のゾーンが塗っているとき(そのゾーンに当てる)。
        public static string CtxHeaderPaintingZoneFormat => IsJapanese
            ? "「{0}」を直す（ここを塗っているゾーン）" : "Fix \"{0}\" (the zone painting here)";
        public static string CtxHeaderCommon => IsJapanese ? "全部のゾーンに共通で直す" : "Fix for all zones";
        public static string CtxHeaderNoZone => IsJapanese
            ? "先に「スポイトで変えたい色を選ぶ」で色を選んでください"
            : "Pick a color first with \"Pick the color to change\"";
        public static string CtxPaintHere => IsJapanese ? "ここも塗る" : "Paint here too";
        public static string CtxDontPaintHere => IsJapanese ? "ここは塗らない" : "Don't paint here";
        public static string CtxOnlyThisPart => IsJapanese ? "この部分だけ塗る" : "Paint only this part";
        public static string CtxDontPaintHereCommon => IsJapanese ? "ここはどのゾーンでも塗らない" : "Don't paint here in any zone";
        public static string CtxChangeZone => IsJapanese ? "直すゾーンを変える" : "Change the zone to fix";
        public static string CtxCommonTargetItem => IsJapanese ? "全部のゾーンに共通（塗らないだけ）" : "All zones (don't-paint only)";
        // AI の部分の大きさを、その 1 回だけ変えるサブメニュー(中身は「ここも塗る」などと同じ)。
        public static string CtxAiFineRoot => IsJapanese ? "小さめの部分で" : "As a smaller part";
        public static string CtxAiCoarseRoot => IsJapanese ? "大きめの部分で" : "As a larger part";
        public static string CtxAiNoSentis => IsJapanese
            ? "AI が使えません（本体ウィンドウ上部の案内から導入。メッシュの形では使えます）"
            : "AI unavailable (set up from the notice at the top of the main window; mesh shapes still work)";
        public static string CtxAiNoModel => IsJapanese
            ? "AI モデルが未取得です（本体ウィンドウ上部の案内から取得。メッシュの形では使えます）"
            : "AI model not downloaded (get it from the notice at the top of the main window; mesh shapes still work)";
        // メッシュの形(UV の島)で当てるサブメニュー。{0} = ここも塗る 等、{1} = メッシュ名。
        public static string CtxMeshRoot => IsJapanese ? "メッシュの形で" : "By mesh shape";
        public static string CtxMeshIslandFormat => IsJapanese ? "{0}（{1} のこの島）" : "{0} (this island of {1})";
        public static string CtxMeshOthersFormat => IsJapanese ? "{0} 以外のメッシュは塗らない" : "Don't paint meshes other than {0}";
        public static string CtxPartNoIsland => IsJapanese
            ? "メッシュの形で: この場所はどのメッシュの島にも乗っていません" : "By mesh shape: no mesh island at this spot";
        public static string CtxPartNoMesh => IsJapanese
            ? "メッシュの形で: このテクスチャを使うメッシュが見つかりません" : "By mesh shape: no mesh using this texture was found";
        public static string CtxPartUnreadableFormat => IsJapanese
            ? "メッシュの形で: メッシュを読めません（{0} 個。FBX の Read/Write を有効にして探し直してください）"
            : "By mesh shape: cannot read the meshes ({0}; enable Read/Write on the FBX and rescan)";
        public static string CtxPartUseSelectionFormat => IsJapanese
            ? "選択中の「{0}」のメッシュを使う" : "Use the meshes of the selected \"{0}\"";
        public static string CtxPartUseSelectionNone => IsJapanese
            ? "メッシュはマスク欄の「メッシュ」で指定できます（FBX・Prefab をドラッグ）"
            : "Choose meshes in the \"Mesh\" field of the mask section (drag an FBX or prefab)";
        public static string CtxPartResearch => IsJapanese ? "メッシュを探し直す" : "Rescan for meshes";
        // 右クリックの操作の結果(MaskPaintView.DescribeRegionEdit)。{0} = ゾーン名。
        public static string NotifyPaintHereFormat => IsJapanese
            ? "「{0}」で、ここも塗るようにしました（Ctrl+Z で戻せます）" : "\"{0}\" now paints here too (Ctrl+Z to undo)";
        public static string NotifyDontPaintHereFormat => IsJapanese
            ? "「{0}」で、ここは塗らないようにしました（Ctrl+Z で戻せます）" : "\"{0}\" no longer paints here (Ctrl+Z to undo)";
        public static string NotifyDontPaintHereCommon => IsJapanese
            ? "どのゾーンでも、ここは塗らないようにしました（Ctrl+Z で戻せます）" : "No zone paints here now (Ctrl+Z to undo)";
        public static string NotifyOnlyThisPartFormat => IsJapanese
            ? "「{0}」は、この部分だけを塗るようにしました（Ctrl+Z で戻せます）" : "\"{0}\" now paints only this part (Ctrl+Z to undo)";
        public static string NotifyCommonOverlap => IsJapanese
            ? "全部のゾーンに共通の「塗らない」が重なる所は塗られません（マスク編集で消せます）"
            : "Where an all-zones \"don't paint\" overlaps, it stays unpainted (clear it in the mask editor)";
        public static string NotifyOnlyThisZeroTolerance => IsJapanese
            ? "許容範囲が 0 なので、この部分の中も色では選ばれません。許容範囲を上げるか「自動調整」を押してください"
            : "Tolerance is 0, so nothing inside this part is selected by color. Raise Tolerance or press Auto-tune";
        public static string NotifyOnlyThisAutoTuneHint => IsJapanese
            ? "「自動調整」を押すと、この部分の中だけを見て範囲を合わせます"
            : "Press Auto-tune to fit the range by looking only inside this part";
        public static string NotifyMaskNoChange =>IsJapanese ? "変わりませんでした（すでにそうなっています）" : "Nothing changed (it was already that way)";
        public static string NotifyMaskNotAdded => IsJapanese
            ? "当てられませんでした（直すゾーンが無いか、この場所に島がありません）"
            : "Could not apply (the zone is gone, or there is no island here)";
        public static string NotifyMeshFoundFormat => IsJapanese ? "メッシュを {0} 個見つけました" : "Found {0} mesh(es)";
        public static string NotifyMeshNotFound => IsJapanese
            ? "このテクスチャを使うメッシュは見つかりませんでした（色での選択はそのまま使えます）"
            : "No mesh using this texture was found (color selection still works)";
        public static string NotifyMeshNotUsableFormat => IsJapanese
            ? "「{0}」に使えるメッシュがありませんでした" : "\"{0}\" has no usable mesh";

        // ─── マスク欄の「メッシュ」行(パーツ操作と UV の島の表示に使うメッシュ) ───
        public static string MeshSource => IsJapanese ? "メッシュ" : "Mesh";
        public static string MeshSourceTooltip => IsJapanese
            ? "このテクスチャを使っているメッシュ（FBX・Prefab）。右クリックメニューの「メッシュの形で」と、UV の島の表示に使います。\n"
              + "開いているシーン → テクスチャと同じ素材フォルダの Prefab の順に自動で探します。\n"
              + "見つからないときや違うものを拾ったときは、Project か Hierarchy から FBX・Prefab をここへドラッグするか、◎ で選びます。\n"
              + "欄を空にする（選んで Delete）と自動に戻ります"
            : "The meshes (FBX or prefab) that use this texture. Used by the \"By mesh shape\" items of the right-click menu and to show the UV islands.\n"
              + "Searched automatically in the open scene, then in prefabs in the texture's asset folder.\n"
              + "If nothing is found or the wrong one is picked, drag an FBX or prefab here from the Project or Hierarchy, or pick one with ◎.\n"
              + "Clearing the field (select it and press Delete) goes back to automatic";
        public static string MeshResearch => IsJapanese ? "探し直す" : "Rescan";
        public static string MeshResearchTooltip => IsJapanese
            ? "メッシュを自動で探し直します（アバターをシーンに置いた・FBX の Read/Write を変えたあと。シーンを開き直したときは自動で探し直します）。手動の指定は外れます"
            : "Search for meshes again (after placing the avatar in the scene or changing Read/Write on the FBX; reopening a scene searches again automatically). Clears a manual choice";
        public static string MeshStatusSceneFormat => IsJapanese
            ? "自動（シーンから）: メッシュ {0} 個" : "Automatic (from the scene): {0} mesh(es)";
        public static string MeshStatusProjectFormat => IsJapanese
            ? "自動（素材フォルダの Prefab から）: メッシュ {0} 個" : "Automatic (from a prefab in the asset folder): {0} mesh(es)";
        public static string MeshStatusManualFormat => IsJapanese
            ? "指定: メッシュ {0} 個" : "Chosen: {0} mesh(es)";
        public static string MeshStatusNone => IsJapanese
            ? "見つかりません（色での選択はそのまま使えます）。FBX・Prefab をドラッグして指定できます"
            : "Not found (color selection still works). Drag an FBX or prefab here to choose one";
        public static string MeshStatusUnreadableFormat => IsJapanese
            ? "メッシュを読めません（{0} 個）。FBX の Read/Write を有効にして「探し直す」"
            : "Cannot read the meshes ({0}). Enable Read/Write on the FBX, then \"Rescan\"";
        public static string MeshListTooltipHeader => IsJapanese ? "使っているメッシュ:" : "Meshes in use:";
        public static string MeshShowIslands => IsJapanese ? "UV の島をプレビューに表示" : "Show UV islands on the preview";
        public static string MeshShowIslandsTooltip => IsJapanese
            ? "メッシュの UV の島の輪郭をプレビューに重ねます。右クリックの「メッシュの形で」で当てる範囲が、この線で囲まれた島（と線のすぐ外のにじみ）です。表示だけで、色替えの結果は変わりません"
            : "Overlay the outlines of the mesh's UV islands on the preview. The right-click \"By mesh shape\" items apply to the island enclosed by these lines (plus the bleed just outside them). Display only; the recolor result does not change";
        public static string NotifyAiNotStarted => IsJapanese
            ? "AI 提案を開始できませんでした（マスク編集ウィンドウの AI 提案欄を確認してください）"
            : "Could not start AI suggest (see the AI section of the mask edit window)";
        public static string MaskHint => IsJapanese
            ? "プレビュー上でドラッグしてマスクを塗り/消しできます"
            : "Drag on the preview to paint or erase the mask";

        public static string Preview => IsJapanese ? "プレビュー" : "Preview";
        public static string GeneratingPreview => IsJapanese ? "⟳ プレビュー生成中…" : "⟳ Generating preview...";
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
        // 原本ファイル（PSD/TGA/EXR 等）を直接デコードできず、取り込み済みテクスチャの画素で
        // 書き出したときの注意。取り込み側は maxTextureSize の縮小・圧縮を受けているので、
        // 出力が原本と同じ解像度・画質とは限らない。黙って書き出さないための表示。
        public static string ExportFromImportedTexture(int w, int h) => IsJapanese
            ? $"元ファイルを直接読めないため、取り込み済みテクスチャ（{w}×{h}）から書き出しました。インポート設定の縮小・圧縮が反映されます。"
            : $"The source file could not be read directly, so the output came from the imported texture ({w}×{h}). Import downscaling and compression apply.";
        public static string ExportSourceUnavailable => IsJapanese
            ? "元ファイルを読めず、テクスチャからも画素を取れません。インポート設定で Read/Write を有効にするか、PNG / JPG のテクスチャを使ってください。"
            : "The source file could not be read, and pixels are not available from the texture either. Turn on Read/Write in the import settings, or use a PNG/JPG texture.";
        public static string SaveAsNewFile => IsJapanese ? "新規ファイルとして保存" : "Save as new file";
        public static string FileName => IsJapanese ? "ファイル名" : "File Name";
        public static string ApplyAndSave => IsJapanese ? "適用して保存" : "Apply & Save";
        public static string ExportPngEncodeEmpty => IsJapanese
            ? "PNG への変換結果が空でした" : "PNG encoding returned no data";
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

        public static string ZoneDragHandleTooltip => IsJapanese
            ? "ドラッグで並べ替えます。重なった所は、上にあるゾーンだけが色替えします。"
            : "Drag to reorder. Where zones overlap, only the upper one recolors.";
        public static string ZoomHint => IsJapanese
            ? "Ctrl+スクロールで拡大・縮小します。100% はテクスチャ全体が枠に収まる大きさで、1 画素が見えるところまで拡大できます。"
            : "Ctrl+scroll to zoom. 100% fits the whole texture in the frame; you can zoom in until single pixels are visible.";
        public static string ZoomLabel => IsJapanese
            ? "ズーム: {0}%（Ctrl+スクロール）"
            : "Zoom: {0}%  (Ctrl+Scroll)";
        // 見出し行に収まらない狭いプレビュー列で使う短い表記（操作方法はツールチップ ZoomHint にある）。
        public static string ZoomLabelShort => IsJapanese
            ? "ズーム: {0}%"
            : "Zoom: {0}%";

        public static string ComparisonMode => IsJapanese ? "前後比較" : "Compare";
        public static string DiffMode       => IsJapanese ? "差分表示" : "Diff";
        public static string Before         => IsJapanese ? "変更前" : "Before";
        public static string After          => IsJapanese ? "変更後" : "After";

        // 実装は Unity 標準 Undo（Undo.PerformUndo）の薄いショートカットで、対象は「直前の操作」。
        // 直前がマスク編集とは限らない（スライダー変更やシーン編集かもしれない）ため、
        // 「マスクを元に戻す」とは名乗らない。名乗ると、マスク以外が巻き戻ったとき破壊的な
        // サプライズになる。
        public static string UndoMask => IsJapanese ? "直前の操作を元に戻す（Ctrl+Z）" : "Undo Last Action (Ctrl+Z)";

        public static string MaskHintPaintOff => IsJapanese
            ? "「塗る」または「消す」を押すとペイントモードになります。同じボタンを押すと解除。"
            : "Click Paint or Erase to enter paint mode. Click the active button again to exit.";

        public static string GeneratingDetailPreview => IsJapanese ? "⟳ 詳細プレビュー生成中…" : "⟳ Generating detail preview...";

        public static string Presets              => IsJapanese ? "プリセット" : "Presets";
        public static string PresetName           => IsJapanese ? "プリセット名" : "Preset Name";
        public static string SavePreset           => IsJapanese ? "保存" : "Save";
        public static string LoadPreset           => IsJapanese ? "読込" : "Load";
        public static string NoPresets            => IsJapanese ? "プリセットがありません" : "No presets saved yet";
        public static string ExportJson           => IsJapanese ? "JSON エクスポート" : "Export JSON";
        public static string ImportJson           => IsJapanese ? "JSON インポート" : "Import JSON";
        public static string PresetStorageProject => IsJapanese ? "プロジェクト内" : "In Project";
        public static string PresetStorageUser    => IsJapanese ? "全プロジェクト共通" : "All Projects";
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
            ? "今のテクスチャのゾーン・色・加工設定・マスクをすべて消して、最初の状態に戻します（Ctrl+Z で戻せます）。"
            : "Clear all zones, colors, processing settings and masks for the current texture and return to the initial state (undoable with Ctrl+Z).";
        public static string ResetSessionConfirm => IsJapanese
            ? "今のテクスチャのゾーン・色・加工設定・マスクをすべて消して、最初の状態に戻します。よろしいですか？\n（Ctrl+Z で戻せます）"
            : "This clears all zones, colors, processing settings and masks for the current texture. Continue?\n(You can undo this.)";

        public static string BatchApply        => IsJapanese ? "一括適用" : "Batch Apply";
        public static string BatchHint         => IsJapanese
            ? "現在のゾーン設定を複数のテクスチャに一括適用します。出力は各ファイル名に _recolored を付与します。"
            : "Apply the current zone settings to several textures at once. Output files get a _recolored suffix.";
        public static string AddBatchTexture   => IsJapanese ? "+ テクスチャ追加" : "+ Add Texture";
        public static string RemoveBatchTextureTooltip => IsJapanese
            ? "このテクスチャを一括リストから外します"
            : "Remove this texture from the batch list";
        public static string BatchApplyAndSave => IsJapanese ? "一括適用して保存" : "Batch Apply & Save";
        public static string BatchProgress     => IsJapanese ? "一括適用中…" : "Batch processing...";
        public static string BatchComplete(int count) => IsJapanese
            ? $"{count} 件のテクスチャを処理しました"
            : $"Processed {count} texture(s)";

        public static string ZoneEnabledTooltip => IsJapanese
            ? "このゾーンを有効/無効にします"
            : "Enable or disable this zone";
        public static string ZoneNameTooltip => IsJapanese
            ? "ゾーンの識別名（処理には影響しません）"
            : "Zone name for identification (does not affect processing)";
        public static string RemoveZoneTooltip => IsJapanese
            ? "このゾーンを削除します"
            : "Remove this zone";
        public static string SelectionModeTooltip => IsJapanese
            ? "ColorPick: サンプルカラーに近い色のピクセルを選択\nUVRect: UV 座標の矩形範囲内のピクセルを選択"
            : "ColorPick: Select pixels matching the sampled color\nUVRect: Select pixels within a UV coordinate rectangle";
        public static string SampleColorTooltip => IsJapanese
            ? "選択する基準色。右の「スポイト」を押してプレビューをクリックすると、テクスチャの色を正確に取れます"
            : "Reference color for selection. Press Eyedropper next to it and click the preview to sample the exact texture color";
        public static string ToleranceTooltip => IsJapanese
            ? "色の許容範囲。値が大きいほど基準色から離れた色も選択されます（0〜1）"
            : "Color matching tolerance. Higher values select colors further from the sample (0-1)";
        public static string UVRectTooltip => IsJapanese
            ? "選択するテクスチャ上の矩形範囲を UV 座標で指定します（0〜1）\nX/Y: 左下の原点、W/H: 幅と高さ"
            : "UV coordinate rectangle for the selected region (0-1)\nX/Y: bottom-left origin, W/H: width and height";
        public static string TargetColorTooltip => IsJapanese
            ? "色替え後の色。選んだ部分がこの色になります"
            : "The new color. The selected area is recolored to this color";

        public static string BrushSizeTooltip => IsJapanese
            ? "ブラシの半径。画面上の大きさは拡大しても変わらないので、細かく塗るときはプレビューを拡大します（拡大して小さいブラシにすると 1 画素ずつ塗れます）。"
            : "Brush radius. It keeps its size on screen when you zoom, so zoom in to paint finer detail (zoom in with a small brush to paint single pixels).";
        public static string ExcludeTooltip => IsJapanese
            ? "除外マスクを編集対象にします（赤系の表示）\n塗った領域は色が合っていても色替えされません"
            : "Edit the exclusion mask (shown in red)\nPainted areas are never recolored, even if their color matches";
        public static string IncludeLayerTooltip => IsJapanese
            ? "「含める」マスクを編集します（緑で表示）\n塗った所は、色が合わなくても必ず色替えされます（色で拾えなかった所を足すとき）\n共通マスクには「含める」はありません（どのゾーンの色にするか決まらないため）"
            : "Edit the Include mask (shown in green)\nPainted areas are always recolored, even if the color does not match (use it to add areas the color match missed)\nThe common mask has no Include (there would be no way to know which zone's color to use)";
        public static string ClearMaskTooltip => IsJapanese
            ? "いま「編集対象」と「マスクの種類」に選ばれているマスク 1 枚だけを消去します。\nほかのマスクはそのまま残ります。"
            : "Erases only the mask currently selected by \"Edit Target\" and \"Mask Type\".\nOther masks are left untouched.";
        public static string UndoMaskTooltip => IsJapanese
            ? "直前の操作を 1 ステップ前に戻します（Unity 標準の Ctrl+Z と同じ）。"
              + "マスク専用ではないため、直前がマスク以外の操作ならそちらが戻ります"
            : "Undo the last action, same as Unity's Ctrl+Z. This is not mask-specific: "
              + "if your last action was not a mask edit, that action is undone instead";

        public static string MaskEditOpen => IsJapanese ? "マスクを編集…" : "Edit Masks...";
        public static string MaskEditOpenTooltip => IsJapanese
            ? "マスク編集ウィンドウを開いて、塗れる状態にします\n編集するゾーン・マスクの種類（除外/含める）・塗る/消すは、そのウィンドウで切り替えます\nウィンドウを閉じると編集は終わります"
            : "Opens the mask editing window, ready to paint\nSwitch the zone, the mask type (Exclude/Include) and Paint/Erase in that window\nClosing the window ends mask editing";
        public static string MaskEditWindowTitle => IsJapanese ? "いろか マスク編集" : "Iroca Masks";
        public static string MaskCurrentTargetFormat => IsJapanese
            ? "編集対象: {0} / {1}" : "Edit target: {0} / {1}";
        public static string MaskCurrentTargetTooltip => IsJapanese
            ? "「マスクを編集…」で開くウィンドウが、いまどのマスクを対象にしているかの表示です\n切り替えはそのウィンドウの「編集対象」「マスクの種類」で行います"
            : "Shows which mask the editing window currently targets\nSwitch it with \"Edit Target\" and \"Mask Type\" in that window";
        public static string ClearMaskTargetFormat => IsJapanese
            ? "このマスクをクリア（{0} / {1}）" : "Clear this mask ({0} / {1})";
        public static string BrushPaletteNoHost => IsJapanese
            ? "いろかウィンドウが開いていません。"
            : "The Iroca window is not open.";
        public static string OpenIrocaWindow => IsJapanese ? "いろかを開く" : "Open Iroca";
        public static string OpenIrocaWindowTooltip => IsJapanese
            ? "いろか本体ウィンドウを開きます"
            : "Opens the main Iroca window";

        public static string AiSuggest => IsJapanese ? "AI マスク提案（実験的）" : "AI Mask Suggestion (Experimental)";
        public static string AiSuggestInstallSentis => IsJapanese ? "AI 機能を有効化（Sentis を導入）" : "Enable AI feature (install Sentis)";
        // AI の準備を求めるダイアログ(MaskSuggestSetupPrompt。自動調整を押したのに AI が無いときに出る)。
        public static string AiSetupTitle => IsJapanese ? "いろか — AI の準備" : "Iroca — AI setup";
        public static string AiSetupSentisBody => IsJapanese
            ? "自動調整と AI マスク提案には、Unity Sentis {0} と AI モデル（約 44 MB）が必要です。\n\n今すぐ Sentis を Package Manager から導入しますか？\n導入後に Unity が再コンパイルし、続けて AI モデルのダウンロードを案内します。\nSentis と一緒に、VRChat SDK も使う Burst と Collections が新しい版に上がります。Unity から再起動の案内が出たら、再起動してください。\n\n「あとで」を選ぶと自動調整は使えません（ウィンドウ上部の案内からいつでも導入できます）。"
            : "Auto-tune and AI Mask Suggestion require Unity Sentis {0} and the AI models (about 44 MB).\n\nInstall Sentis via the Package Manager now?\nUnity will recompile, then you will be asked to download the models.\nBurst and Collections, which the VRChat SDK also uses, are updated to newer versions along with Sentis. If Unity asks you to restart, please do.\n\nIf you choose \u201cLater\u201d, Auto-tune stays unavailable (you can install any time from the notice at the top of the window).";
        public static string AiSetupModelBody => IsJapanese
            ? "自動調整と AI マスク提案に必要な AI モデル（MobileSAM、約 44 MB）をダウンロードします。\n\n保存先: {0}\n取得元: {1}\n\n「あとで」を選ぶと自動調整は使えません（ウィンドウ上部の案内からいつでもダウンロードできます）。"
            : "Download the AI models (MobileSAM, about 44 MB) required by Auto-tune and AI Mask Suggestion.\n\nSave to: {0}\nSource: {1}\n\nIf you choose \u201cLater\u201d, Auto-tune stays unavailable (you can download any time from the notice at the top of the window).";
        public static string AiSetupInstall => IsJapanese ? "導入する" : "Install";
        public static string AiSetupDownload => IsJapanese ? "ダウンロード" : "Download";
        public static string AiSetupLater => IsJapanese ? "あとで" : "Later";
        public static string AiSuggestInstallSentisTooltip => IsJapanese
            ? "Unity Package Manager 経由で {0}（バージョン {1}）を導入します\n導入後 Unity が自動で再コンパイルし、続けて AI モデルのダウンロードを案内します\n一緒に、VRChat SDK も使う Burst と Collections が新しい版に上がります（Unity から再起動の案内が出ることがあります）\n（Package Manager から手動で「Add package by name」しても同じです）"
            : "Installs {0} (version {1}) via the Unity Package Manager.\nUnity recompiles automatically afterward, then you will be asked to download the AI models.\nBurst and Collections, which the VRChat SDK also uses, are updated to newer versions along with it (Unity may ask you to restart).\n(Equivalent to adding it manually via 'Add package by name'.)";
        public static string AiSuggestInstalling => IsJapanese
            ? "Sentis を導入中… 完了すると Unity が自動で再コンパイルします。"
            : "Installing Sentis... Unity will recompile automatically when it finishes.";
        public static string AiSuggestSentisVersionReplace => IsJapanese
            ? "このプロジェクトには Sentis {0} が導入済みですが、AI マスク提案は {1} でのみ動作確認しています。\n下のボタンを押すと {1} に差し替わります。他のツールが Sentis {0} を使っている場合は、そちらへの影響をご確認ください。"
            : "Sentis {0} is already installed in this project, but AI mask suggestion is verified only with {1}.\nThe button below will replace it with {1}. If another tool depends on Sentis {0}, please check the impact on it first.";
        public static string AiSuggestReplaceSentis => IsJapanese ? "AI 機能を有効化（Sentis を {0} に差し替え）" : "Enable AI feature (replace Sentis with {0})";
        public static string AiSuggestInstallFailed => IsJapanese
            ? "Sentis の導入に失敗しました: {0}\nPackage Manager から手動で「com.unity.sentis」を追加することもできます（手順はマニュアル参照）。"
            : "Failed to install Sentis: {0}\nYou can also add 'com.unity.sentis' manually via the Package Manager (see the manual).";
        public static string AiSuggestRestartRecommended => IsJapanese
            ? "AI 機能を導入しました。Unity を再起動すると確実に使えます（再起動しないと、まれに最初だけ AI が動かないことがあります）。"
            : "The AI feature is installed. Restart Unity to make sure it works (without a restart, the AI occasionally does not run the first time).";
        public static string AiSuggestRestartNow => IsJapanese ? "Unity を再起動" : "Restart Unity";
        public static string AiSuggestRestartNowTooltip => IsJapanese
            ? "現在のプロジェクトを開き直して Unity を再起動します（未保存の変更は先に保存してください）"
            : "Reopens the current project to restart Unity (save any unsaved changes first).";
        public static string AiSuggestRestartLater => IsJapanese ? "あとで" : "Later";
        public static string AiSuggestRestartLaterTooltip => IsJapanese
            ? "この案内を閉じます（このまま使えることが多いですが、AI が動かないときは Unity を再起動してください）"
            : "Dismiss this notice (it usually works as-is; if the AI doesn't run, restart Unity).";
        public static string AiSuggestLoadingModel => IsJapanese ? "モデルを読み込み中…" : "Loading model...";
        public static string AiSuggestEncoding => IsJapanese ? "画像を解析中…" : "Analyzing image...";
        public static string AiSuggestDecoding => IsJapanese ? "提案を生成中…" : "Generating proposal...";
        public static string AiSuggestPendingClicksFormat => IsJapanese
            ? "処理待ちクリック {0} 件（順に反映されます）"
            : "{0} click(s) queued (applied in order)";
        public static string AiSuggestOpenModelFolder => IsJapanese ? "モデルフォルダを開く" : "Open model folder";
        public static string AiSuggestOpenModelFolderTooltip => IsJapanese
            ? "モデルファイル（.onnx）を配置するフォルダをエクスプローラーで開きます"
            : "Open the folder where the model files (.onnx) should be placed.";
        public static string AiSuggestDownload => IsJapanese ? "モデルをダウンロード" : "Download models";
        public static string AiSuggestDownloadTooltip => IsJapanese
            ? "MobileSAM の AI モデル 2 ファイル（合計約 44 MB、Apache-2.0 ライセンス）を\n{0}\nからダウンロードし、モデルフォルダへ自動配置します（sha256 検証つき）"
            : "Download the two MobileSAM model files (about 44 MB total, Apache-2.0 license) from\n{0}\nand place them into the model folder automatically (with sha256 verification).";
        public static string AiSuggestDownloading => IsJapanese ? "モデルをダウンロード中…" : "Downloading models...";
        public static string AiSuggestDownloadCancel => IsJapanese ? "中止" : "Cancel";
        public static string AiSuggestDownloadCancelTooltip => IsJapanese
            ? "ダウンロードを中止します（ダウンロード済みの部分ファイルは削除されます）"
            : "Cancel the download (partially downloaded files are deleted).";
        public static string AiSuggestDownloadFailed => IsJapanese
            ? "ダウンロードに失敗しました: {0}\n手動でダウンロードしてモデルフォルダへ配置することもできます（手順はマニュアル参照）"
            : "Download failed: {0}\nYou can also download manually and place the files into the model folder (see the manual).";
        public static string AiSuggestAreaWarning => IsJapanese
            ? "直前のクリックが背景まで広がった可能性があります。Ctrl+Z で戻して、右クリックの「小さめの部分で」から選び直すか、パーツのより内側を右クリックし直してください。"
            : "The last click may have spread into the background. Undo with Ctrl+Z, then choose from \"As a smaller part\" in the right-click menu, or right-click again further inside the part.";
        public static string AiSuggestEmptyCommit => IsJapanese
            ? "直前のクリックではマスクが変わりませんでした（その部分はすでにそうなっています）。"
            : "The last click did not change the mask (that part was already that way).";
        public static string AiSuggestEmptyProposal => IsJapanese
            ? "AI が領域を返しませんでした。別の場所を右クリックするか、右クリックの「小さめの部分で」「大きめの部分で」も試してください。\nどこを右クリックしても同じ場合は、内部コンパイル（Burst）の初期化に失敗している可能性があります。Unity を再起動すると直ります。"
            : "The AI returned no region. Try right-clicking elsewhere, or try \"As a smaller part\" / \"As a larger part\" in the right-click menu.\nIf every click behaves this way, the internal compiler (Burst) may have failed to initialize — restarting Unity fixes it.";
        public static string AiSuggestBurstFailed => IsJapanese
            ? "この Unity では内部コンパイル（Burst）が失敗しているため、AI が部分を見分けられず、右クリックしてもマスクが変わりません。Unity を再起動すると直ります。"
            : "The internal compiler (Burst) failed in this Unity session, so the AI cannot find regions and right-clicks won't change the mask. Restarting Unity fixes it.";
        public static string AiSuggestErrorRestartHint => IsJapanese
            ? "推論エンジンの初期化に失敗している可能性があります（Unity 起動時の Burst コンパイル失敗など）。Unity を再起動すると直ることが多いです。プレビューの右クリックからもう一度選ぶと、準備をやり直します。"
            : "The inference engine may have failed to initialize (e.g. a Burst compile failure at Unity startup). Restarting Unity usually fixes it. Choosing an item from the preview's right-click menu again retries the setup.";
        // モデルのファイルを読めなかったとき(ONNX の取り込みの失敗・ファイルの欠け)。原因が分かっているので、
        // 再起動の案内(AiSuggestErrorRestartHint)は出さず、取り直しだけを案内する。
        public static string AiSuggestModelFileErrorHint => IsJapanese
            ? "AI モデルのファイルを読めませんでした。「モデルを確認して取り直す」を押すと、ファイルを確かめて、壊れていれば取り直します。"
            : "The AI model files could not be read. Click \"Check and re-download models\" to check the files and download them again if they are broken.";
        // 理由が返ってこなかった失敗(Package Manager・推論)。
        public static string AiUnknownError => IsJapanese ? "不明なエラー" : "unknown error";
        public static string AiSuggestRecheckModels => IsJapanese ? "モデルを確認して取り直す" : "Check and re-download models";
        public static string AiSuggestRecheckModelsTooltip => IsJapanese
            ? "置いてあるモデル 2 ファイルを配布物と照合し（sha256）、壊れていたり別物だったりしたら取り直します\n一致していれば取り直さず、その旨を表示します"
            : "Compare the two model files with the released ones (sha256) and re-download them if they are broken or different\nIf they match, nothing is downloaded and a note is shown";
        public static string AiSuggestModelsVerifiedOk => IsJapanese
            ? "モデルファイルは配布物と一致しています。原因はモデル以外です（多くは Unity の再起動で直ります）。"
            : "The model files match the released ones, so the cause is elsewhere (restarting Unity usually fixes it).";
        public static string AiSuggestGranularity => IsJapanese ? "提案の粒度" : "Granularity";
        public static string AiSuggestGranularityTooltip => IsJapanese
            ? "AI はクリック 1 点に対して粒度の違う候補（模様・パーツ・全体）を同時に推定します\n右クリックの「ここも塗る」などがどの候補を使うかを、ここで選びます（次のクリックから適用）\n1 回だけ変えるときは、右クリックの「小さめの部分で」「大きめの部分で」から選べます"
            : "The AI estimates candidates of different granularity (pattern / part / whole) for one click.\nChoose which candidate \"Paint here too\" and the other right-click items use (applies from the next click).\nTo change it for one click only, use \"As a smaller part\" / \"As a larger part\" in the right-click menu.";
        public static string AiSuggestGranularityAuto => IsJapanese ? "自動" : "Auto";
        public static string AiSuggestGranularityAutoTooltip => IsJapanese
            ? "輪郭がはっきりした候補のうち、最も大きいものを採用します（既定）"
            : "Use the largest candidate among those with a crisp outline (default).";
        public static string AiSuggestGranularityFine => IsJapanese ? "小さめ" : "Fine";
        public static string AiSuggestGranularityFineTooltip => IsJapanese
            ? "輪郭がはっきりした候補のうち、最も小さいものを採用します。模様や小さなパーツ（例: バンダナの三角模様）だけを選びたいときに"
            : "Use the smallest candidate among those with a crisp outline. For selecting patterns or small pieces only.";
        public static string AiSuggestGranularityCoarse => IsJapanese ? "大きめ" : "Coarse";
        public static string AiSuggestGranularityCoarseTooltip => IsJapanese
            ? "輪郭のはっきりさを問わず、最も大きい候補を採用します。「自動」で足りないときに（周囲を巻き込むこともあります）"
            : "Use the largest candidate regardless of outline crispness. When Auto is not enough (may spill into surroundings).";
        public static string AiSuggestNoZoneWarning => IsJapanese
            ? "有効なゾーンがまだありません。マスクはゾーンの色替え範囲を直すものなので、先に「スポイトで変えたい色を選ぶ」で色を選んでください。"
            : "There is no enabled zone yet. Masks adjust where a zone recolors, so first pick a color with \"Pick the color to change\".";

        // ─────────────────────────────────────────────────────────────
        // AI まわりのエラー文。帯・マスク編集ウィンドウの HelpBox にそのまま出るので、
        // 以前の日本語直書きだと英語 UI でも日本語が表示されていた。
        // ─────────────────────────────────────────────────────────────
        public static string AiErrModelDirFormat => IsJapanese
            ? "モデル置き場を作成できません: {0}"
            : "Cannot create the model folder: {0}";
        public static string AiErrDownloadOversizedFormat => IsJapanese
            ? "{0}: 配布サイズ（{1:N0} バイト）を超えたため中止しました（配布元が想定と異なります。時間をおいて再試行してください）"
            : "{0}: stopped because it exceeded the expected size ({1:N0} bytes). The download source is not as expected; try again later.";
        public static string AiErrDownloadStalledFormat => IsJapanese
            ? "{0}: {1:F0} 秒間応答がありません（接続を確認して再試行してください）"
            : "{0}: no response for {1:F0} seconds. Check your connection and try again.";
        public static string AiErrDownloadHashFormat => IsJapanese
            ? "{0}: sha256 が一致しません（{1}）"
            : "{0}: sha256 does not match ({1})";
        public static string AiErrWorkerCreateFormat => IsJapanese
            ? "推論ワーカーの作成に失敗しました（{0}）: {1}"
            : "Failed to create the inference worker ({0}): {1}";
        public static string AiErrPreprocessFormat => IsJapanese
            ? "前処理に失敗しました: {0}"
            : "Preprocessing failed: {0}";
        public static string AiErrEmbeddingSizeFormat => IsJapanese
            ? "埋め込みサイズが不正です: {0}"
            : "Unexpected embedding size: {0}";
        public static string AiErrCropEmbeddingSizeFormat => IsJapanese
            ? "クロップ埋め込みサイズが不正です: {0}"
            : "Unexpected crop embedding size: {0}";
        public static string AiErrEmptyResult => IsJapanese
            ? "推論結果が空です（バックエンドがカーネルを実行できていません）"
            : "The inference result is empty (the backend did not run its kernels).";
        public static string AiErrEncodeFormat => IsJapanese
            ? "画像の解析に失敗しました: {0}"
            : "Image analysis failed: {0}";
        public static string AiErrDecodeFormat => IsJapanese
            ? "提案の推論に失敗しました: {0}"
            : "Suggestion inference failed: {0}";
        public static string AiErrProposalFormat => IsJapanese
            ? "提案の生成に失敗しました: {0}"
            : "Failed to generate the suggestion: {0}";
        public static string AiErrModelMissingFormat => IsJapanese
            ? "モデルファイルがありません: {0}"
            : "Model file not found: {0}";
        public static string AiErrModelLoadFormat => IsJapanese
            ? "モデルのロードに失敗しました: {0}"
            : "Failed to load the model: {0}";
        public static string AiErrTempDirCollisionFormat => IsJapanese
            ? "一時フォルダ名が衝突しました: {0}"
            : "Temporary folder name collision: {0}";
        public static string AiErrOnnxImportFormat => IsJapanese
            ? "ONNX のインポートに失敗しました（Console のログを確認してください）: {0}"
            : "Failed to import the ONNX model (see the Console log): {0}";
        public static string AiErrReadbackTimeout => IsJapanese
            ? "GPU からの読み出しがタイムアウトしました"
            : "Reading back from the GPU timed out";

        public static string MaskTarget => IsJapanese ? "編集対象" : "Edit Target";
        public static string MaskTargetCommon => IsJapanese ? "共通マスク（全ゾーン）" : "Common Mask (all zones)";
        public static string MaskTargetTooltip => IsJapanese
            ? "編集するマスクを選びます\n・共通マスク: どのゾーンでも塗らない所\n・各ゾーン: そのゾーンだけの「除外」と「含める」\n「含める」はゾーンを選んだときだけ編集できます\n「塗る／消す」とプレビューの右クリック操作は、ここで選んだマスクに働きます"
            : "Choose which mask to edit\n- Common: areas no zone paints\n- Per-zone: that zone's Exclude and Include areas\nInclude can only be edited when a zone is selected\nPaint / Erase and the preview's right-click actions apply to the mask selected here";
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
            ? "詳細（マスク・JSON 入出力）"
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
            ? "色替えをテクスチャに書き込んで保存します（新しいファイルにするか上書きするかは上の設定で決まります）"
            : "Writes the recolor into the texture and saves it (as a new file or over the original, per the setting above)";
        public static string OpenFolderTooltip => IsJapanese
            ? "元テクスチャのあるフォルダをエクスプローラーで開きます"
            : "Open the folder containing the source texture in Explorer";
        public static string InheritImportSettings => IsJapanese
            ? "インポート設定を引き継ぐ"
            : "Inherit Import Settings";
        public static string InheritImportSettingsTooltip => IsJapanese
            ? "ON: 元テクスチャのインポート設定（テクスチャタイプ・圧縮・ミップマップなど）を出力ファイルに引き継ぎます\nOFF: Unity の既定のインポート設定を使います"
            : "ON: Copy import settings (texture type, compression, mipmaps, etc.) from the source texture to the output file\nOFF: Use Unity's default import settings";
        public static string AddBatchTextureTooltip => IsJapanese
            ? "一括適用リストにテクスチャを追加します"
            : "Add a texture to the batch apply list";
        public static string BatchApplyAndSaveTooltip => IsJapanese
            ? "リスト内のすべてのテクスチャに現在のゾーン設定を一括適用して保存します\n出力ファイル名は元のファイル名に _recolored を付与"
            : "Apply current zone settings to all textures in the list and save\nOutput files get a _recolored suffix";

        public static string ComparisonModeTooltip => IsJapanese
            ? "変更前と変更後を横並びで比較表示します。拡大しているときはプレビュー解像度で表示します"
            : "Show before and after side by side. At high zoom both are shown at preview resolution";
        public static string DiffModeTooltip => IsJapanese
            ? "変更前後の差分（変化したピクセル）を強調表示します"
            : "Highlight pixels that changed between before and after";

        public static string PresetStorageProjectTooltip => IsJapanese
            ? "プリセットを Project の Assets フォルダ内に保存します。Git などでチームと共有できます"
            : "Save presets inside the project's Assets folder. Can be shared via Git.";
        public static string PresetStorageUserTooltip => IsJapanese
            ? "プリセットを OS のユーザーフォルダに保存します。この端末の全プロジェクトで共有されます"
            : "Save presets in the OS user folder. Shared across all projects on this machine.";
        public static string PresetNameTooltip => IsJapanese
            ? "保存するプリセットの名前を入力してください"
            : "Enter a name for the preset to save";
        public static string SavePresetTooltip => IsJapanese
            ? "現在のゾーン設定と加工設定をプリセットとして保存します"
            : "Save current zone and processing settings as a preset";
        public static string ExportJsonTooltip => IsJapanese
            ? "現在のゾーン設定と加工設定を JSON ファイルに書き出します"
            : "Export the current zone and processing settings to a JSON file";
        public static string ImportJsonTooltip => IsJapanese
            ? "JSON ファイルを読み込み、現在のゾーン設定と加工設定を上書きします"
            : "Load a JSON file and overwrite the current zone and processing settings";
        public static string LoadPresetTooltip => IsJapanese
            ? "このプリセットを読み込み、現在のゾーン設定と加工設定を上書きします"
            : "Load this preset and overwrite the current zone and processing settings";
        public static string DeletePresetTooltip => IsJapanese
            ? "このプリセットを削除します"
            : "Delete this preset";

        public static string EnableReadWriteTooltip => IsJapanese
            ? "元テクスチャのインポート設定で Read/Write を有効化します。プレビューと色替えに必要です"
            : "Enable Read/Write in the source texture's import settings. Required for preview and recoloring";
        public static string AddZoneTooltip => IsJapanese
            ? "色を決めていない空のカラーゾーンを追加します（色はあとからゾーンの「スポイト」か色の欄で入れます）。\nふつうは左の「スポイトで…」ボタンで、色を選びながらゾーンを作るほうが手早くできます。"
            : "Add an empty color zone with no colors yet (set them later with the zone's \"Eyedropper\" or color fields).\nUsually the button on the left is quicker: it creates the zone as you pick the color.";
        // ゾーンが無いときの開始ボタン（押す → プレビューをクリック でゾーンができる）。
        public static string StartWithEyedropper => IsJapanese
            ? "スポイトで変えたい色を選ぶ"
            : "Pick the color to change";
        // ゾーンがあるときの同じボタン（2 色目以降も 1 色目と同じ「押す → クリック」で足す）。
        public static string AddColorWithEyedropper => IsJapanese
            ? "スポイトで別の色を追加"
            : "Add a color (eyedropper)";
        public static string StartWithEyedropperActive => IsJapanese
            ? "■ プレビューで変えたい色をクリック"
            : "■ Click a color on the preview";
        public static string StartWithEyedropperTooltip => IsJapanese
            ? "押してからプレビュー上の変えたい色をクリックすると、カラーゾーンを作ってその色をサンプルカラーに入れます。\nもう一度押すか Esc で解除します。"
            : "Press, then click the color you want to change on the preview: a color zone is created with that color as its Sample Color.\nPress again or hit Esc to cancel.";
        public static string CancelActionTooltip => IsJapanese
            ? "実行中の処理を中止します"
            : "Cancel the running operation";
        public static string ManualTooltip => IsJapanese
            ? "使い方のマニュアル（オンライン版）をブラウザで開きます"
            : "Open the online user manual in your browser";
        public static string CreditTooltip => IsJapanese
            ? "作者とライセンスを表示します。開発に使わせていただいたモデルの一覧も、そこから開けます"
            : "Shows the author and license. The list of models used in development opens from there too";
        public static string LanguageToolbarTooltip => IsJapanese
            ? "UI の表示言語を切り替えます（自動 / 日本語 / English）"
            : "Switch the UI display language (Auto / Japanese / English)";

        public static string UseFloodFill => IsJapanese ? "連続領域モード" : "Connected Region";
        public static string UseFloodFillTooltip => IsJapanese
            ? "色が合った所のうち、確実に合っている芯を含む、つながった塊だけを色替えします。\n離れた所にある同じ色のパーツや、背景へのにじみを自動で外します。\nふだんはシードは要りません。塊がいくつかあって 1 つだけ残したいときは、Shift+クリックでシードを置けます。"
            : "Restrict recoloring to connected regions that contain a high-confidence core.\nAutomatically removes bleed into physically separate same-color parts or the background.\nNo seed needed by default. To keep only one specific region, Shift+click the preview to set a seed.";
        public static string FloodFillSeedPoint => IsJapanese ? "シード（任意）" : "Seed (optional)";
        public static string FloodFillSeedNotSet => IsJapanese ? "自動（シードなし）" : "Auto (no seed)";
        public static string FloodFillSeedHint => IsJapanese
            ? "Shift+クリックでシードを指定すると、その塊だけを残します（任意）"
            : "Shift+click to set a seed and keep only that region (optional)";
        public static string FloodFillClear => IsJapanese ? "自動へ" : "Clear";
        public static string FloodFillClearTooltip => IsJapanese
            ? "シードを外して自動に戻します"
            : "Clear the seed and go back to automatic";

        public static string AutoTune => IsJapanese ? "自動調整" : "Auto-tune";
        // 完了通知に使う。ボタン名と同じ「自動調整」だけを出すと、開始・完了・失敗の区別がつかない。
        public static string AutoTuneDone => IsJapanese ? "自動調整が完了しました" : "Auto-tune complete";
        // 証拠つき自動調整（スポイト位置の AI 提案セグメントを教師にした導出）が使われたとき。
        // 何が根拠になったかが分かるよう、通常の完了通知と文言を分ける。
        public static string AutoTuneDoneWithEvidence => IsJapanese
            ? "自動調整が完了しました（AI 提案を参照）"
            : "Auto-tune complete (used AI suggestion)";
        // 証拠（AI 提案セグメント）を待っている間の進捗表示。
        public static string AutoTuneEvidenceFetching => IsJapanese
            ? "AI 提案を取得中…"
            : "Fetching AI suggestion…";
        // AI の準備(モデルロード・埋め込み計算・別の提案処理)が終わるのを待っている間。
        public static string AutoTuneEvidencePreparing => IsJapanese
            ? "AI の準備を待っています…"
            : "Waiting for the AI to get ready…";
        // 自動調整は AI 提案を証拠にする。AI が無いときは従来導出へ落とさず中止して案内する。
        public static string AutoTuneNeedsAi => IsJapanese
            ? "自動調整には AI モデルが必要です。ウィンドウ上部の案内から導入・ダウンロードしてください"
            : "Auto-tune requires the AI models. Install/download them from the notice at the top of the window";
        public static string AutoTuneAiError => IsJapanese
            ? "AI を利用できないため自動調整を中止しました: {0}"
            : "Auto-tune aborted because the AI is unavailable: {0}";
        public static string AutoTuneSourceNotShared => IsJapanese
            ? "元画像を AI に渡せなかったため、自動調整できません。テクスチャを開き直してください"
            : "Auto-tune could not pass the source image to the AI. Reopen the texture";
        public static string AutoTuneNoSamplePosition => IsJapanese
            ? "スポイトで取った位置がないため、AI 提案を参照せずに解析します（スポイトで色を取ると AI 提案を参照します）"
            : "No eyedropper position; analyzing without the AI suggestion (pick the color with the eyedropper to use it)";
        public static string AutoTuneEvidenceTimeout => IsJapanese
            ? "AI の準備が終わらなかったため自動調整を中止しました。しばらくしてからもう一度お試しください"
            : "Auto-tune aborted: the AI did not get ready in time. Please try again later";
        public static string AutoTuneEvidenceCancelled => IsJapanese
            ? "AI 提案を取得できなかったため自動調整を中止しました"
            : "Auto-tune aborted: the AI suggestion could not be obtained";
        public static string AutoTuneEvidenceUnusable => IsJapanese
            ? "AI 提案が背景まで広がっていたため、AI 提案を使わずに調整しました"
            : "The AI suggestion spread into the background, so Auto-tune ran without it";
        public static string AnalyzingTexture => IsJapanese ? "テクスチャを解析中…" : "Analyzing texture…";
        public static string AutoTuneTooltip => IsJapanese
            ? "サンプルカラーと変更先カラーから、テクスチャを解析して許容範囲・彩度制限などのパラメータを自動的に決定します。\n上の 2 色（サンプルカラー・変更先カラー）を決めてから押してください。下の項目が結果で上書きされます。"
            : "Analyzes the texture using the sample and target colors and automatically sets tolerance, saturation strictness, and related parameters.\nSet both colors above (sample and target) before pressing; the settings below are overwritten with the result.";
        public static string AutoTuneDisabledTooltip => IsJapanese
            ? "次のときは使えません:\n・元テクスチャを選んでいない\n・テクスチャを読めない（Read/Write の警告が出ているとき）\n・サンプルカラーをまだ選んでいない"
            : "Disabled when:\n- No source texture is selected\n- The texture cannot be read (the Read/Write warning is shown)\n- No sample color has been picked yet";
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
                    ? "\n「加工設定」も変わります。"
                    : "\nThe Processing settings will also change.");
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────
        // ワークフロー案内（手順の完了マークと「次の一手」）
        // 見出しの番号だけでは「今どこまで進んだか / 次に何をするか」が伝わらず、
        // テクスチャを入れた瞬間に導入ヒントが消えて案内が途切れていた。
        // ─────────────────────────────────────────────────────────────
        public static string StepDoneMark => " ✓";
        public static string NextStepAddZone => IsJapanese
            ? "次の手順: 「スポイトで変えたい色を選ぶ」を押して、プレビュー上の変えたい色をクリックします。"
            : "Next: press \"Pick the color to change\", then click that color on the preview.";
        // 以下 3 つの {0} はゾーン名。2 色目を足したときに、どのゾーンの話かが分かるようにする。
        public static string NextStepPickColorFormat => IsJapanese
            ? "次の手順: 「{0}」の「スポイト」を押してから、プレビュー上の変えたい色をクリックします。"
            : "Next: press \"Eyedropper\" on \"{0}\", then click the color you want to change on the preview.";
        // 色を選んだ直後は変更先が既定の白なので、選んだ所が白っぽくなるだけで色替えは終わっていない。
        public static string NextStepPickTargetFormat => IsJapanese
            ? "次の手順: 「{0}」の「変更先カラー」をクリックして、新しい色を選びます。"
            : "Next: click \"Target Color\" on \"{0}\" and choose the new color.";
        // 自動調整は変更先の明るさも使う（模様保持の導出）ので、変更先を決めた後に案内する。
        public static string NextStepAutoTuneFormat => IsJapanese
            ? "次の手順（任意）: 「{0}」の「自動調整」を押すと、そのパーツの暗い所から明るい所までに範囲を合わせます。"
            : "Next (optional): press \"Auto-tune\" on \"{0}\" to fit the range to the part from its shadows to its highlights.";

        // ─────────────────────────────────────────────────────────────
        // AI 準備バナー（メインウィンドウ上部）
        // 以前はウィンドウを開いた瞬間にモーダルで導入・ダウンロードを求めていた。
        // Editor 全体をブロックするうえ「あとで」を選ぶと案内ごと消え、自動調整だけが
        // 黙って使えない状態になっていたため、常に見える非モーダルの帯へ移した。
        // ─────────────────────────────────────────────────────────────
        public static string AiBannerSentis => IsJapanese
            ? "自動調整と AI マスク提案（実験的）を使うには Unity Sentis の導入が必要です（未導入でも色替えとマスクの手描きは使えます）。"
            : "Auto-tune and AI Mask Suggestion (experimental) need the Unity Sentis package (recoloring and hand-painted masks work without it).";
        public static string AiBannerModel => IsJapanese
            ? "自動調整と AI マスク提案を使うには AI モデル（約 44 MB・一度だけ）のダウンロードが必要です。"
            : "Auto-tune and AI Mask Suggestion need the AI models (about 44 MB, downloaded once).";
        public static string AiBannerDismiss => IsJapanese ? "閉じる" : "Dismiss";
        public static string AiBannerDismissTooltip => IsJapanese
            ? "この案内をこの Unity セッション中は表示しません。次に Unity を開いたときにまた出ます。"
            : "Hide this notice for this Unity session. It comes back the next time you open Unity.";

        // ─────────────────────────────────────────────────────────────
        // プレビューの操作モード表示（1 行）
        // クリックの意味がモードで変わるのに、状態はゾーンカードやマスク編集ウィンドウの
        // ボタン色にしか出ていなかった。プレビューを別ウィンドウへ切り出しているときや
        // 設定列をスクロールしているときは、その手掛かりが画面の外へ出る。
        // ─────────────────────────────────────────────────────────────
        public static string PreviewModeEyedropper => IsJapanese
            ? "スポイト: クリックで色を取得"
            : "Eyedropper: click to pick a color";
        public static string PreviewModeSeed => IsJapanese
            ? "シード指定: クリックで残す塊を指定"
            : "Seed: click the region to keep";
        public static string PreviewModePaintFormat => IsJapanese
            ? "マスクを塗る: {0} / {1}"
            : "Painting mask: {0} / {1}";
        public static string PreviewModeEraseFormat => IsJapanese
            ? "マスクを消す: {0} / {1}"
            : "Erasing mask: {0} / {1}";
        public static string PreviewModeAi => IsJapanese
            ? "AI 提案を計算中（結果はマスクへ自動で入ります）"
            : "AI suggest is working (the result goes into the mask automatically)";
        public static string PreviewModeEscHint => IsJapanese ? "（Esc で解除）" : "  (Esc to cancel)";
        public static string PreviewModeTooltip => IsJapanese
            ? "プレビュー上のクリックがいま何をするかを示します。Esc で解除できます。\nどのモードでも、中ボタンドラッグまたは Alt+ドラッグで表示を移動できます。"
            : "Shows what a click on the preview does right now. Press Esc to leave the mode.\nIn any mode you can pan the view with a middle-button drag or Alt+drag.";
        // ─── Scene でモデルをクリックした場所の表示(操作モード行が空いているときに出す) ───
        public static string SceneClickHint => IsJapanese
            ? "ヒント: Scene でモデルをクリックすると、その場所をここに表示します"
            : "Tip: click the model in the Scene view to show that spot here";
        public static string SceneClickHintTooltip => IsJapanese
            ? "Scene ビューでモデルをクリックすると、その部分がテクスチャのどこにあるかをプレビューに表示します（その UV の島を塗り、クリックした点に輪を付けます）。\n"
              + "Unity のふつうの選択はそのまま行われ、いろかの設定は何も変わりません。\n"
              + "いろかで何も開いていないときは、クリックした部分のテクスチャを開きます。"
            : "Click the model in the Scene view to see where that part is on the texture (its UV island is filled and the clicked point gets a ring).\n"
              + "Unity selects the object as usual, and none of the Iroca settings change.\n"
              + "If no texture is open in Iroca, the texture of the clicked part is opened.";
        public static string SceneClickOtherTextureFormat => IsJapanese
            ? "ここは「{0}」のテクスチャです"
            : "This part uses \"{0}\"";
        public static string SceneClickOtherTextureTooltip => IsJapanese
            ? "Scene でクリックした部分は、いま開いているのとは別のテクスチャを使っています。"
            : "The part you clicked in the Scene view uses a different texture from the one open now.";
        public static string SceneClickOpen => IsJapanese ? "開く" : "Open";
        public static string SceneClickOpenTooltip => IsJapanese
            ? "このテクスチャをいろかで開き、クリックした場所を表示します（今のテクスチャの編集内容は保存されます）"
            : "Open this texture in Iroca and show the clicked spot (your edits to the current texture are saved)";
        public static string NotifySceneClickOpenedFormat => IsJapanese ? "「{0}」を開きました" : "Opened \"{0}\"";
        // ─── 右クリックの使い方(色替えが済んでから、右クリックメニューを一度開くまで操作モード行に出す) ───
        public static string ContextMenuHint => IsJapanese
            ? "ヒント: はみ出し・塗り残し・一部だけ変えたい所は、そこを右クリック"
            : "Tip: right-click a spot to fix spills or gaps, or recolor just that part";
        public static string ContextMenuHintTooltip => IsJapanese
            ? "プレビューの直したい場所を右クリックすると、「ここも塗る」「ここは塗らない」「この部分だけ塗る」から選べます（Ctrl+Z で戻せます）。\n"
              + "右クリックのメニューを一度開くと、このヒントは出なくなります。"
            : "Right-click the spot you want to fix in the preview and choose \"Paint here too\", \"Don't paint here\" or \"Paint only this part\" (Ctrl+Z undoes it).\n"
              + "This tip goes away once you have opened the right-click menu.";

        public static string PreviewSoloFormat => IsJapanese
            ? "ソロ表示中: {0}"
            : "Solo: {0}";
        public static string PreviewSoloTooltip => IsJapanese
            ? "1 つのゾーンだけをプレビューしています。保存される内容は変わりません（有効なゾーンがすべて適用されます）。"
            : "Only one zone is being previewed. This does not change what gets saved (every enabled zone is applied).";

        // ─────────────────────────────────────────────────────────────
        // ズームのリセット（ズーム自体は Ctrl+スクロール。拡大後に初期表示へ戻す手段が無かった）
        // ─────────────────────────────────────────────────────────────
        public static string ZoomReset => IsJapanese ? "リセット" : "Reset";
        public static string ZoomResetTooltip => IsJapanese
            ? "ズームを 100%（テクスチャ全体が収まる大きさ）に戻し、表示位置も先頭に戻します（ズームは Ctrl+スクロールで変えられます）"
            : "Reset the zoom to 100% (the whole texture fits) and the scroll position to the top-left (zoom with Ctrl+Scroll)";

        public static string PeekOriginal => IsJapanese ? "元を表示" : "Original";
        public static string PeekOriginalTooltip => IsJapanese
            ? "押している間だけ変更前のテクスチャを表示します（離すと変更後に戻ります）。\n拡大しているときは変更前をプレビュー解像度で表示します。"
            : "Shows the original texture while the button is held (release it to return to the result).\nAt high zoom the original is shown at preview resolution.";

        // ─────────────────────────────────────────────────────────────
        // ゾーンのソロ表示
        // ─────────────────────────────────────────────────────────────
        public static string ZoneSolo => IsJapanese ? "ソロ" : "Solo";
        public static string ZoneSoloTooltip => IsJapanese
            ? "このゾーンだけをプレビューに表示して、どこを拾えているかを確かめます。\n表示だけの機能で、保存される内容は変わりません。もう一度押すと解除します。"
            : "Preview only this zone to check what it selects.\nThis affects the preview only; what gets saved does not change. Press again to release.";
        public static string ExportSoloWarning => IsJapanese
            ? "ソロ表示中です。保存には有効なすべてのゾーンが適用されます。"
            : "A zone is soloed for preview. Saving still applies every enabled zone.";

        // ─────────────────────────────────────────────────────────────
        // 連続領域モードのシード指定（Shift+クリックだけでは対象ゾーンが分からなかった）
        // ─────────────────────────────────────────────────────────────
        public static string FloodFillSeedPick => IsJapanese ? "指定" : "Set";
        public static string FloodFillSeedPickActive => IsJapanese ? "■ クリック" : "■ Click";
        public static string FloodFillSeedPickTooltip => IsJapanese
            ? "押してからプレビューをクリックすると、このゾーンのシードを置きます（1 回で解除）。\nShift+クリックでも指定できます。"
            : "Press, then click the preview to place this zone's seed (disarms after one click).\nShift+click also works.";

        // ─────────────────────────────────────────────────────────────
        // スポイト位置が無いことの警告（自動調整が AI 提案を参照できるかが事前に分からなかった）。
        // 位置があるのは普通の状態なので、無いときだけ出す。
        // ─────────────────────────────────────────────────────────────
        public static string SampleUvMissing => IsJapanese
            ? "スポイト位置がありません。自動調整に AI 提案を使うには「スポイト」で色を取り直してください。"
            : "No sampled position. To let Auto-tune use the AI suggestion, sample the color again with \"Eyedropper\".";
        public static string SampleUvTooltip => IsJapanese
            ? "「スポイト」でプレビューをクリックすると位置が記録され、自動調整はその位置の AI マスク提案を手がかりにします。\nカラーフィールドで色を変えると位置は消えます。"
            : "Clicking the preview with the Eyedropper records the position, and Auto-tune uses the AI mask suggestion there as a hint.\nChanging the color from the color field clears the position.";

        // ─────────────────────────────────────────────────────────────
        // 詳細パラメータの小見出し（同じ「彩度」でも役割の違うものが平坦に並んでいた）
        // ─────────────────────────────────────────────────────────────
        public static string ZoneGroupSelection => IsJapanese ? "選択の範囲" : "Selection range";
        public static string ZoneGroupHighlight => IsJapanese ? "ハイライト（光沢・明部）" : "Highlights";
        public static string ZoneGroupRecolor => IsJapanese ? "色の写り方" : "Color mapping";
        public static string ZoneGroupMatching => IsJapanese ? "マッチング距離の重み" : "Matching distance weights";
        // ウィンドウのタブの「⋮」メニュー。白寄せ合成とマッチング距離の重みを詳細設定に出す。
        public static string ShowDeveloperSettings => IsJapanese ? "開発者向けの設定を表示" : "Show developer settings";

        public static string EditAtSourceResolution => IsJapanese ? "元の解像度で編集" : "Edit at original resolution";
        public static string EditAtSourceResolutionTooltip => IsJapanese
            ? "オフ（既定）のときは、プレビュー・AI 提案・自動調整と「アバターに非破壊で登録」のテクスチャを、インポート設定の Max Size の大きさで処理します。ゲームに出るのもこの大きさです。オンにすると元の画像の解像度で処理します。拡大表示で細部まで確かめられますが、大きい画像ではメモリを多く使います（8192×8192 で 6 GB ほど）。書き出し（「適用して保存」）は、どちらでも元の画像の解像度で行います。この PC のいろか全体に効きます。"
            : "When off (default), the preview, AI suggestions, auto-tune, and the textures for \"Register to avatar (non-destructive)\" are processed at the import Max Size, which is also the size that appears in game. When on, Iroca processes at the original image's resolution: zooming in shows every detail, but large images use a lot of memory (about 6 GB at 8192×8192). Exporting (\"Apply & Save\") always uses the original resolution. This applies to Iroca on this PC.";

        public static string ProcessingDetailFoldout => IsJapanese ? "詳細設定" : "Details";
        public static string ProcessingDetailFoldoutTooltip => IsJapanese
            ? "穴埋め・境界復元・境界クリーンアップの半径など、内部の細かいパラメータを開閉します。通常は既定のままで構いません。"
            : "Show or hide internal parameters such as hole filling, boundary recovery, and the decontamination radius. The defaults are usually fine.";

        // ─────────────────────────────────────────────────────────────
        // エクスポート
        // ─────────────────────────────────────────────────────────────
        public static string RevealInProject => IsJapanese ? "Project で表示" : "Show in Project";
        public static string RevealInProjectTooltip => IsJapanese
            ? "直前に保存したテクスチャを Unity の Project ウィンドウで選択して表示します"
            : "Select and reveal the texture you just saved in Unity's Project window";
        public static string DismissError => IsJapanese ? "閉じる" : "Dismiss";
        public static string DismissErrorTooltip => IsJapanese
            ? "このエラー表示を消します（内容は Console にも残ります）"
            : "Hide this error message (it is also logged to the Console)";

        // 新規ゾーンの既定名。同名が並ぶとマスク編集ウィンドウの「編集対象」で区別できない。
        public static string NewZoneNameFormat => IsJapanese ? "ゾーン {0}" : "Zone {0}";

        // ─────────────────────────────────────────────────────────────
        // 非破壊(NDMF)
        // ─────────────────────────────────────────────────────────────
        public static string RegisterNonDestructive => IsJapanese ? "アバターに非破壊で登録" : "Register to avatar (non-destructive)";
        public static string ApplyToAvatar => IsJapanese ? "アバターに反映" : "Apply to Avatar";
        public static string ExportAsTexture => IsJapanese ? "テクスチャとして書き出す" : "Export as a texture";
        public static string ExportAsTextureTooltip => IsJapanese
            ? "色替えを書き込んだ画像ファイル（PNG）を作ります。配布・販売する衣装のように、受け取る側にいろかや NDMF が無い場合に使います。自分のアバターには「アバターに非破壊で登録」がおすすめです（元のテクスチャを残したまま、何度でも直せます）。"
            : "Creates an image file (PNG) with the recolor written into it. Use it when the people who receive the result may not have Iroca or NDMF, such as outfits you distribute or sell. For your own avatar, \"Register to avatar (non-destructive)\" is recommended (the original texture stays as is and you can change it any time).";
        public static string RegisterNonDestructiveTooltip => IsJapanese
            ? "元のテクスチャとマテリアルを書き換えずに色替えします。Hierarchy で選んだ衣装やアバター（未選択なら、このテクスチャを使っている所の共通の親）に「Iroca Recolor」を付け、再生・アップロードのときだけ色替えしたテクスチャに差し替えます。コンポーネントを外せば元に戻ります。"
            : "Recolors without changing the original texture or materials. Adds \"Iroca Recolor\" to the outfit or avatar selected in the Hierarchy (or, if nothing is selected, the common parent of the objects using this texture) and swaps in the recolored texture only when entering Play mode or uploading. Remove the component to go back.";
        public static string RegisterNoTarget => IsJapanese
            ? "登録先が決まりません。Hierarchy で衣装かアバターを選んでから押してください（このテクスチャを使っているオブジェクトがシーンに無いか、複数のアバターにまたがっています）。"
            : "Could not decide where to register. Select the outfit or avatar in the Hierarchy and try again (no object in the scene uses this texture, or several avatars do).";
        public static string RegisterNoZones => IsJapanese
            ? "有効なゾーンがありません。色替えするゾーンを作ってから登録してください。"
            : "There is no enabled zone. Create a zone to recolor before registering.";
        public static string RegisterNotInScene => IsJapanese
            ? "選んでいるのはシーン上のオブジェクトではありません。Hierarchy で選び直してください。"
            : "The selection is not an object in the scene. Select one in the Hierarchy.";
        public static string RegisterConfirmFormat => IsJapanese
            ? "「{0}」とその子のマテリアルのうち「{1}」を使っている所を、再生・アップロード時に色替えします。元のテクスチャとマテリアルは変わりません。\n\nレシピ: {2}"
            : "Materials under \"{0}\" that use \"{1}\" will be recolored when entering Play mode or uploading. The original texture and materials are not changed.\n\nRecipe: {2}";
        public static string RegisterAddToExisting => IsJapanese
            ? "このオブジェクトにある「Iroca Recolor」にレシピを足します（コンポーネントは増えません）。"
            : "The recipe is added to the \"Iroca Recolor\" already on this object (no new component).";
        public static string RegisterMergeFormat => IsJapanese
            ? "このオブジェクトに付いている「Iroca Recolor」{0} 個を 1 つにまとめます。"
            : "The {0} \"Iroca Recolor\" components on this object are merged into one.";
        public static string RegisterReplaceFormat => IsJapanese
            ? "このオブジェクトにある「{0}」の別のレシピ（{1}）は、このレシピに置き換えます。"
            : "The other recipe for \"{0}\" on this object ({1}) is replaced with this one.";
        public static string RegisterSwitchExportsFormat => IsJapanese
            ? "「適用して保存」で書き出した画像を使っているマテリアル {0} 個を、元のテクスチャ「{1}」に戻します（非破壊の色替えは元のテクスチャを探すため。Ctrl+Z で戻せます）。"
            : "{0} material(s) that use an image exported with \"Apply & Save\" are switched back to the original texture \"{1}\" (non-destructive recoloring looks for the original texture. Ctrl+Z restores them).";
        public static string RegisterLockedExportsFormat => IsJapanese
            ? "書き出した画像を使っているマテリアルのうち {0} 個は、書き換えられない場所（FBX の中・パッケージ）にあるのでそのままです。"
            : "{0} material(s) that use an exported image are in places that cannot be changed (inside an FBX or a package), so they are left as they are.";
        public static string ExportUsersHintFormat => IsJapanese
            ? "シーンに、このテクスチャを書き出した画像を使っているマテリアルが {0} 個あります。非破壊で色替えするには、「アバターに非破壊で登録」で元のテクスチャに戻してください（登録のときにまとめて戻せます）。"
            : "{0} material(s) in the scene use an image exported from this texture. To recolor non-destructively, switch them back to the original texture with \"Register to avatar (non-destructive)\" (it does this for you when registering).";
        public static string RegisterRecipeNew => IsJapanese ? "{0}（新しく作ります）" : "{0} (new)";
        public static string RegisterDoneFormat => IsJapanese ? "「{0}」に登録しました" : "Registered to \"{0}\"";
        public static string RegisterOk => IsJapanese ? "登録" : "Register";
        // いろかウィンドウとインスペクタの警告。入っていない・古い・新しすぎるで直し方が違う。
        internal static string NdmfUnavailable(NdmfSupportState state)
        {
            string cause = NdmfCause(state);
            return IsJapanese
                ? cause + "ので、非破壊の色替えは使えません。画像の書き出しは使えます。" + NdmfFix(state)
                : char.ToUpperInvariant(cause[0]) + cause.Substring(1)
                  + ", so non-destructive recoloring is not available. Exporting images still works. " + NdmfFix(state);
        }
        // NDMF が使えない理由(「〜ので」「〜ためです」に続く形)と直し方。
        private static string NdmfCause(NdmfSupportState state)
        {
            switch (state)
            {
                case NdmfSupportState.TooOld: return IsJapanese
                    ? "入っている NDMF（Non-Destructive Modular Framework）が古い（1.8.0 より前の版）"
                    : "the installed NDMF (Non-Destructive Modular Framework) is too old (before 1.8.0)";
                case NdmfSupportState.TooNew: return IsJapanese
                    ? "入っている NDMF（Non-Destructive Modular Framework）が、このいろかの対応している版より新しい"
                    : "the installed NDMF (Non-Destructive Modular Framework) is newer than this version of Iroca supports";
                default: return IsJapanese
                    ? "NDMF（Non-Destructive Modular Framework）が入っていない"
                    : "NDMF (Non-Destructive Modular Framework) is not installed";
            }
        }
        private static string NdmfFix(NdmfSupportState state)
        {
            switch (state)
            {
                case NdmfSupportState.TooOld: return IsJapanese
                    ? "VCC / ALCOM で、NDMF を新しい版にしてください。"
                    : "Update NDMF in VCC / ALCOM.";
                case NdmfSupportState.TooNew: return IsJapanese
                    ? "VCC / ALCOM で、いろかを最新の版にしてください。"
                    : "Update Iroca to the latest version in VCC / ALCOM.";
                default: return IsJapanese
                    ? "VCC / ALCOM で、いろかと同じリポジトリから「Non-Destructive Modular Framework」を追加してください。"
                    : "Add \"Non-Destructive Modular Framework\" in VCC / ALCOM from the same repository as Iroca.";
            }
        }
        public static string UploadBlockedTitle => IsJapanese ? "いろか: アップロードを止めました" : "Iroca: Upload stopped";
        internal static string UploadBlocked(NdmfSupportState state) => IsJapanese
            ? "「Iroca Recolor」で色替えできないので、アップロードを止めました（色替えされないまま上がらないように）。"
              + NdmfCause(state) + "ためです。" + NdmfFix(state)
              + "色替えせずにアップロードするときは、アバターから「Iroca Recolor」を外してください。"
            : "The upload was stopped because \"Iroca Recolor\" cannot recolor this avatar (so that it is not uploaded without the recolor): "
              + NdmfCause(state) + ". " + NdmfFix(state)
              + " To upload without the recolor, remove \"Iroca Recolor\" from the avatar.";
        public static string BoundRecipe => IsJapanese ? "保存先のレシピ" : "Recipe";
        public static string BoundRecipeTooltip => IsJapanese
            ? "このテクスチャの編集内容は、このレシピにも保存されます（アバターの「Iroca Recolor」がビルド時に使う内容）。"
            : "Edits to this texture are also saved to this recipe (what \"Iroca Recolor\" on the avatar uses at build time).";
        public static string OpenInIroca => IsJapanese ? "いろかで開く" : "Open in Iroca";
        public static string OpenInIrocaTooltip => IsJapanese
            ? "このレシピをいろかウィンドウで開いて編集します。編集内容はこのレシピに保存されます。"
            : "Open this recipe in the Iroca window to edit it. Edits are saved to this recipe.";
        public static string RecolorInspectorHelp => IsJapanese
            ? "このオブジェクトとその子のマテリアルのうち、各レシピの元テクスチャを使っている所を、再生・アップロード時だけ色替えします。レシピはテクスチャごとに 1 つです。元のアセットは変わりません。"
            : "Materials on this object and its children that use each recipe's source texture are recolored only when entering Play mode or uploading. There is one recipe per texture. The original assets are not changed.";
        public static string RecolorNoRecipe => IsJapanese
            ? "レシピが設定されていません。いろかウィンドウの「アバターに非破壊で登録」で作れます。"
            : "No recipe is set. Create one with \"Register to avatar (non-destructive)\" in the Iroca window.";
        public static string RecolorNoSource => IsJapanese
            ? "レシピに元テクスチャが設定されていません。"
            : "The recipe has no source texture.";
        public static string RecolorUnreadable => IsJapanese
            ? "レシピの中身を読めません（空か、壊れているか、新しい版のいろかで作られています）。"
            : "Cannot read the recipe (empty, broken, or made with a newer version of Iroca).";
        public static string RecolorNotUsedFormat => IsJapanese
            ? "この範囲の Renderer に「{0}」を使っているマテリアルがありません。衣装のトグルなどアニメーションで切り替えた先でだけ使うならこのままで構いません。そうでなければ、コンポーネントを衣装やアバターの親に置いてください。"
            : "No renderer in this scope uses \"{0}\". That is fine if it is only used by a material that an animation (such as an outfit toggle) switches to. Otherwise, place the component on the outfit or avatar root.";
        public static string RecolorScopeFormat => IsJapanese
            ? "対象のマテリアル: {0} 個（Renderer {1} 個）"
            : "Target materials: {0} ({1} renderer(s))";
        public static string RecipeInspectorZonesFormat => IsJapanese ? "ゾーン: {0} 個（有効 {1} 個）" : "Zones: {0} ({1} enabled)";
        public static string RecipeField => IsJapanese ? "レシピ" : "Recipes";
        public static string RecipeFieldTooltip => IsJapanese
            ? "色替えの内容（元テクスチャ・色・マスク）。テクスチャごとに 1 つ。いろかウィンドウの「アバターに非破壊で登録」で足されます。同じテクスチャのレシピが重なったら上のものが使われます。"
            : "What to recolor (source texture, colors, masks), one per texture. Added by \"Register to avatar (non-destructive)\" in the Iroca window. If two recipes use the same texture, the upper one is used.";
        public static string RecolorEmptyEntry => IsJapanese
            ? "空の欄があります。レシピを設定するか、欄を消してください。"
            : "There is an empty entry. Set a recipe or remove the entry.";
        public static string RecolorDuplicateSourceFormat => IsJapanese
            ? "「{0}」のレシピが複数あります。使われるのは上のものだけです。"
            : "There are several recipes for \"{0}\". Only the upper one is used.";
        public static string RecolorMergeFormat => IsJapanese
            ? "このオブジェクトに「Iroca Recolor」が {0} 個付いています。1 つにまとめられます。"
            : "This object has {0} \"Iroca Recolor\" components. They can be merged into one.";
        public static string RecolorMergeButton => IsJapanese ? "1 つにまとめる" : "Merge into one";
        public static string RecolorMergeButtonTooltip => IsJapanese
            ? "このオブジェクトの「Iroca Recolor」を先頭の 1 つにまとめます。レシピは付いていた順に並べ、同じテクスチャのレシピは先のもの（ビルドで使われていた方）だけ残すので、色替えの結果は変わりません。Undo で戻せます。"
            : "Merges the \"Iroca Recolor\" components on this object into the first one. Recipes keep their order, and for the same texture only the earlier one (the one the build used) is kept, so the result does not change. Undo restores it.";
        public static string RecipeSourceField => IsJapanese ? "元テクスチャ" : "Source Texture";
        public static string RecipeSourceFieldTooltip => IsJapanese
            ? "色替えの元になるテクスチャ。マテリアルがこのテクスチャを参照している所だけが差し替わります。"
            : "The texture to recolor. Only materials that reference this texture are swapped.";

        // 新しい版のお知らせ（UpdateNotice）。{0} は新しい版、{1} は入っている版。
        public static string UpdateNoticeFormat => IsJapanese
            ? "いろか {0} が出ています（入っているのは {1}）。VCC / ALCOM で、このプロジェクトの管理画面から更新できます。"
            : "Iroca {0} is available (installed: {1}). You can update it from this project's management page in VCC / ALCOM.";
        public static string UpdateNoticeChanges => IsJapanese ? "変更点" : "What's new";
        public static string UpdateNoticeChangesTooltip => IsJapanese
            ? "いろかの変更履歴をブラウザで開きます。"
            : "Opens the Iroca changelog in your browser.";
        public static string UpdateNoticeHowTo => IsJapanese ? "更新のしかた" : "How to update";
        public static string UpdateNoticeHowToTooltip => IsJapanese
            ? "マニュアルの更新の手順をブラウザで開きます。"
            : "Opens the update steps in the manual in your browser.";
        public static string UpdateNoticeDismiss => IsJapanese ? "閉じる" : "Dismiss";
        public static string UpdateNoticeDismissTooltip => IsJapanese
            ? "この版のお知らせを閉じます。もっと新しい版が出たら、また知らせます。お知らせそのものは Preferences の「いろか」で止められます。"
            : "Hides the notice for this version. You will be notified again when a newer version comes out. To turn off these notices, use Preferences > Iroca.";
        public static string PreferencesLabel => IsJapanese ? "いろか" : "Iroca";
        public static string UpdateCheckToggle => IsJapanese ? "新しい版を知らせる" : "Notify me of new versions";
        public static string UpdateCheckToggleTooltip => IsJapanese
            ? "いろかのウィンドウを開いたときに（1 日 1 回まで）新しい版があるかを調べ、あればウィンドウの上部で知らせます。"
            : "When you open the Iroca window (at most once a day), checks for a new version and shows a notice at the top of the window if there is one.";
        public static string UpdateCheckPrivacy => IsJapanese
            ? "調べるときは、VCC / ALCOM が読むのと同じ公開リストを読むだけで、こちらから送る情報はありません。更新は VCC / ALCOM で行います。\n読むリスト: " + UpdateCheck.ListingUrl
            : "The check only reads the same public listing that VCC / ALCOM reads. Nothing about you or your project is sent. Updating is done in VCC / ALCOM.\nListing: " + UpdateCheck.ListingUrl;
    }
}
