namespace VRCAvatarColorChanger
{
    public enum LanguageMode { Auto, Japanese, English }

    public static class Localization
    {
        public static LanguageMode CurrentLanguage = LanguageMode.Auto;

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

        // ─── Window ───
        public static string WindowTitle => "VRC AvatarColorChanger";

        // ─── Header / Language ───
        public static string LangAuto     => IsJapanese ? "自動(Auto)" : "Auto";
        public static string LangJapanese => "日本語";
        public static string LangEnglish  => "English";
        public static string Credit       => IsJapanese ? "クレジット" : "Credit";

        // ─── Credit Dialog ───
        public static string CreditTitle => IsJapanese ? "クレジット" : "Credits";
        public static string CreditBody  => IsJapanese
            ? "VRC AvatarColorChanger (VACC)\n\n制作: yukkuri__aoba\nAI補助: Claude (Anthropic)\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Noncommercial License 1.0.0\nhttps://polyformproject.org/licenses/noncommercial/1.0.0"
            : "VRC AvatarColorChanger (VACC)\n\nDeveloper: yukkuri__aoba\nAI Assistance: Claude (Anthropic)\n\nCopyright (c) 2026 yukkuri__aoba\nPolyForm Noncommercial License 1.0.0\nhttps://polyformproject.org/licenses/noncommercial/1.0.0";

        // ─── Source Texture ───
        public static string SourceTexture => IsJapanese ? "元テクスチャ" : "Source Texture";
        public static string Texture => IsJapanese ? "テクスチャ" : "Texture";
        public static string ReadWriteError => IsJapanese
            ? "Read/Write Enabled がオフです。\nインポート設定で有効にしてください。"
            : "Read/Write Enabled is off.\nPlease enable it in the import settings.";
        public static string EnableReadWrite => IsJapanese
            ? "Read/Write を自動で有効にする"
            : "Enable Read/Write automatically";

        // ─── Color Zones ───
        public static string ColorZones => IsJapanese ? "カラーゾーン" : "Color Zones";
        public static string AddZone => IsJapanese ? "+ ゾーン追加" : "+ Add Zone";
        public static string SelectionMode => IsJapanese ? "選択モード" : "Selection Mode";
        public static string SampleColor => IsJapanese ? "サンプルカラー" : "Sample Color";
        public static string Tolerance => IsJapanese ? "許容範囲" : "Tolerance";
        public static string UVRect => IsJapanese ? "UV範囲 (0-1)" : "UV Rect (0-1)";
        public static string TargetColor => IsJapanese ? "変更先カラー" : "Target Color";
        public static string PatternPreserve => IsJapanese ? "模様保持" : "Pattern Preserve";
        public static string PatternPreserveTooltip => IsJapanese
            ? "0 = 変更先の明度に完全に合わせる（ベタ塗り風）\n1 = 元の明度を完全保持（模様が残りやすい）"
            : "0 = Match target brightness entirely (flat recolor)\n1 = Keep original brightness (preserves patterns)";
        public static string EdgeSoftness => IsJapanese ? "エッジ柔らかさ" : "Edge Softness";
        public static string EdgeSoftnessTooltip => IsJapanese
            ? "0 = 硬いエッジ（従来通り）\n1 = 柔らかいエッジ（アンチエイリアス境界を滑らかに）"
            : "0 = Hard edge (legacy)\n1 = Soft edge (smooth anti-aliased boundaries)";
        public static string SaturationStrictness => IsJapanese ? "彩度制限" : "Saturation Strictness";
        public static string SaturationStrictnessTooltip => IsJapanese
            ? "低彩度ピクセル（AO/影）をどの程度厳しく除外するかを調整します。\n高い値 = はみ出しが少ないが、境界にドットが残る場合がある\n低い値 = ドットが減るが、はみ出しが増える\nデフォルト: 0.50"
            : "Controls how aggressively low-saturation pixels (AO/shadow) are excluded.\nHigher = less bleed but may leave dot artifacts at edges\nLower = fewer dots but more bleed\nDefault: 0.50";

        // ─── Processing ───
        public static string Processing => IsJapanese ? "加工設定" : "Processing";
        public static string EdgeFeather => IsJapanese ? "エッジぼかし" : "Edge Feather";
        public static string EdgeFeatherTooltip => IsJapanese
            ? "選択境界をガウシアンブラーでぼかし、滑らかな遷移を実現します。\n0 = オフ"
            : "Gaussian blur on selection boundary for smooth transitions.\n0 = off";

        public static string AntiAliasCleanup => IsJapanese ? "AA境界クリーンアップ" : "AA Edge Cleanup";
        public static string AntiAliasCleanupTooltip => IsJapanese
            ? "アンチエイリアス境界の残りドットを除去するパス数。\n0 = オフ、3 = 標準（推奨）、5 = 最大\n値を大きくすると境界の回収範囲が広がります。"
            : "Number of passes to recover anti-alias boundary pixels.\n0 = off, 3 = normal (recommended), 5 = max\nHigher values recover more edge pixels.";

        // ─── Advanced Mode ───
        public static string AdvancedMode => IsJapanese ? "アドバンスモード" : "Advanced Mode";
        public static string AdvancedModeTooltip => IsJapanese
            ? "有効にすると、アルゴリズムの内部パラメータをより細かく調整できます。\n通常はデフォルト値で十分ですが、特殊なテクスチャに対して微調整が必要な場合に使用してください。"
            : "Enables fine-grained control over internal algorithm parameters.\nDefault values work well for most textures, but can be tuned for special cases.";

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

        public static string HighlightRecovery => IsJapanese ? "ハイライト補助" : "Highlight Recovery";
        public static string HighlightRecoveryTooltip => IsJapanese
            ? "高明度・低彩度のハイライト領域を補助的にマッチします。\n鏡面反射や光沢のある素材の色変換漏れを防ぎます。\nデフォルト: ON"
            : "Match high-brightness low-saturation highlight regions.\nPrevents missed recoloring on reflective/glossy materials.\nDefault: ON";

        // ─── Exclusion Mask ───
        public static string ExclusionMask => IsJapanese ? "除外マスク" : "Exclusion Mask";
        public static string BrushSize => IsJapanese ? "ブラシサイズ" : "Brush Size";
        public static string BrushMode => IsJapanese ? "ブラシモード" : "Brush Mode";
        public static string Exclude => IsJapanese ? "除外" : "Exclude";
        public static string Include => IsJapanese ? "含める" : "Include";
        public static string ClearMask => IsJapanese ? "マスクをクリア" : "Clear Mask";
        public static string MaskHint => IsJapanese
            ? "プレビュー上でドラッグして塗りつぶし除外"
            : "Drag on preview to paint exclusion";

        // ─── Preview ───
        public static string Preview => IsJapanese ? "プレビュー" : "Preview";
        public static string GeneratingPreview => IsJapanese ? "⟳ プレビュー生成中..." : "⟳ Generating preview...";
        public static string Zoom => IsJapanese ? "ズーム" : "Zoom";
        public static string SetTexture => IsJapanese
            ? "テクスチャを設定してください。"
            : "Please set a texture.";

        // ─── Export ───
        public static string Export => IsJapanese ? "エクスポート" : "Export";
        public static string SaveAsNewFile => IsJapanese ? "新規ファイルとして保存" : "Save as new file";
        public static string FileName => IsJapanese ? "ファイル名" : "File Name";
        public static string ApplyAndSave => IsJapanese ? "適用して保存" : "Apply & Save";
        public static string OpenFolder => IsJapanese ? "フォルダを開く" : "Open Folder";

        // ─── Dialogs ───
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
        public static string Saved(string path) => IsJapanese
            ? $"保存しました:\n{path}"
            : $"Saved:\n{path}";

        // ─── Layer ───
        public static string LayerIndex => IsJapanese ? "L" : "L";

        // ─── Zoom hint ───
        public static string ZoomHint => IsJapanese
            ? "Ctrl+スクロールでズーム"
            : "Ctrl+scroll to zoom";
        public static string ZoomLabel => IsJapanese
            ? "ズーム: {0}%  (Ctrl+スクロール)"
            : "Zoom: {0}%  (Ctrl+Scroll)";
        public static string PanHint => IsJapanese
            ? "ドラッグでパン"
            : "Drag to pan";

        // ─── Comparison / Diff ───
        public static string ComparisonMode => IsJapanese ? "前後比較" : "Compare";
        public static string DiffMode       => IsJapanese ? "差分表示" : "Diff";
        public static string Before         => IsJapanese ? "変更前" : "Before";
        public static string After          => IsJapanese ? "変更後" : "After";

        // ─── Undo mask ───
        public static string UndoMask => IsJapanese ? "マスクを元に戻す (Ctrl+Z)" : "Undo Mask (Ctrl+Z)";

        // ─── Mask paint mode ───
        public static string MaskHintPaintOff => IsJapanese
            ? "「除外」または「含める」を押すとペイントモードになります。同じボタンを押すと解除。"
            : "Click Exclude or Include to enter paint mode. Click the active button again to exit.";

        // ─── Detail preview ───
        public static string GeneratingDetailPreview => IsJapanese ? "⟳ 詳細プレビュー生成中..." : "⟳ Generating detail preview...";

        // ─── Presets ───
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

        // ─── Preset Tips ───
        public static string PresetTips => IsJapanese
            ? "【ヒント】\n保存: 現在のゾーン設定をプリセットとして保存\n読込: プリセットを読み込みゾーン設定を上書き\n×: プリセットを削除\n\n保存先\n・プロジェクト内 … Assets フォルダ内に保存 (Gitなどで共有可)\n・ユーザー共通 … 全プロジェクトで共有 (端末ローカルに保存)\n\nJSON エクスポート/インポートで設定を外部ファイルとして共有できます"
            : "[Tips]\nSave: Save current zone settings as a preset\nLoad: Overwrite zone settings with a preset\n×: Delete a preset\n\nStorage\n· In Project … Saved inside Assets folder (shareable via Git)\n· Shared (User) … Shared across all projects (machine-local)\n\nUse Export/Import JSON to share settings as a file";

        // ─── Batch apply ───
        public static string BatchApply        => IsJapanese ? "一括適用" : "Batch Apply";
        public static string BatchHint         => IsJapanese
            ? "現在のゾーン設定を複数のテクスチャに一括適用します。出力は各ファイル名に _recolored を付与します。"
            : "Apply current zone settings to multiple textures. Output files are named with _recolored suffix.";
        public static string AddBatchTexture   => IsJapanese ? "+ テクスチャ追加" : "+ Add Texture";
        public static string BatchApplyAndSave => IsJapanese ? "一括適用して保存" : "Batch Apply & Save";
        public static string BatchProgress     => IsJapanese ? "一括適用中..." : "Batch processing...";
        public static string BatchComplete(int count) => IsJapanese
            ? $"{count} 件のテクスチャを処理しました"
            : $"Processed {count} texture(s)";

        // ─── Zone tooltips ───
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
            ? "選択する基準色。スポイトアイコンでテクスチャからサンプリングできます"
            : "Reference color for selection. Use the eyedropper to sample from the texture";
        public static string ToleranceTooltip => IsJapanese
            ? "色の許容範囲。値が大きいほど基準色から離れた色も選択されます（0〜1）"
            : "Color matching tolerance. Higher values select colors further from the sample (0-1)";
        public static string UVRectTooltip => IsJapanese
            ? "選択するテクスチャ上の矩形範囲をUV座標で指定します（0〜1）\nX/Y: 左下の原点、W/H: 幅と高さ"
            : "UV coordinate rectangle for the selected region (0-1)\nX/Y: bottom-left origin, W/H: width and height";
        public static string TargetColorTooltip => IsJapanese
            ? "変更後の色。対象の部分がこの色に変更されます"
            : "Target color. Pixels in this zone will be recolored to this color";

        // ─── Mask tooltips ───
        public static string BrushSizeTooltip => IsJapanese
            ? "ペイントブラシのサイズ（ピクセル単位）"
            : "Paint brush size in pixels";
        public static string ExcludeTooltip => IsJapanese
            ? "除外ブラシモード: プレビューをドラッグして赤いマスクを描き、その領域を変更から除外します\n同じボタンを再度押すとモードを解除"
            : "Exclude brush mode: Drag on the preview to paint a red mask and exclude that area from recoloring\nClick again to exit paint mode";
        public static string IncludeTooltip => IsJapanese
            ? "消去ブラシモード: プレビューをドラッグして除外マスクを消去し、変更を再有効化します\n同じボタンを再度押すとモードを解除"
            : "Include brush mode: Drag on the preview to erase the exclusion mask and re-enable recoloring\nClick again to exit paint mode";
        public static string ClearMaskTooltip => IsJapanese
            ? "すべての除外マスクを消去します"
            : "Erase all exclusion mask paint";
        public static string UndoMaskTooltip => IsJapanese
            ? "マスクの変更を1ステップ前に戻します（Ctrl+Z でも操作可）"
            : "Undo the last mask change (also available via Ctrl+Z)";

        // ─── Per-Zone Mask strings ───
        public static string MaskTarget => IsJapanese ? "編集対象" : "Edit Target";
        public static string MaskTargetCommon => IsJapanese ? "共通マスク（全ゾーン）" : "Common Mask (all zones)";
        public static string MaskTargetTooltip => IsJapanese
            ? "マスクの編集対象を切り替えます\n・共通マスク: 全ゾーンで共通して除外される領域\n・各ゾーン: そのゾーンだけで除外される領域\n処理時は両者が OR 結合されて適用されます"
            : "Switch which mask is being edited\n- Common: area excluded from every zone\n- Per-zone: area excluded only from that zone\nBoth are OR-combined when processing";
        public static string EditMaskInactiveLabel => IsJapanese
            ? "このゾーンのマスクを編集"
            : "Edit this zone's mask";
        public static string EditMaskActiveLabel => IsJapanese
            ? "■ 編集中（クリックで解除）"
            : "■ Editing (click to release)";
        public static string EditMaskTooltip => IsJapanese
            ? "このゾーン専用の除外マスクを編集対象にします\nもう一度押すと共通マスク編集に戻ります"
            : "Make this zone's exclusion mask the edit target\nClick again to return to the common mask";
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

        // ─── Export tooltips ───
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

        // ─── Preview tooltips ───
        public static string ComparisonModeTooltip => IsJapanese
            ? "変更前と変更後を横並びで比較表示します（高ズーム時は使用不可）"
            : "Show before and after side by side (unavailable at high zoom)";
        public static string DiffModeTooltip => IsJapanese
            ? "変更前後の差分（変化したピクセル）を強調表示します"
            : "Highlight pixels that changed between before and after";

        // ─── Preset tooltips ───
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
    }
}
