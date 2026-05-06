# AvatarColorChanger リファクタリング計画

**対象**: `refactoring-unity-editor-antipatterns.md` 記載の20項目アンチパターン  
**方針**: 6フェーズで段階的に解消。フェーズ完了ごとに Unity 上で手動検証してからコミット（`dev_safe/Tests/` は本リファクタ後に別途設計し直す前提）  
**ブランチ**: `main` から `feature/refactor-all` を切って全フェーズを進める

---

## 目標アーキテクチャ

```
Code/
├── VACCWindow.cs              (EditorWindow 本体 — 薄いホスト)
├── VACCConsts.cs              (MenuPath / Layout 定数)
├── VACCColors.cs              (Light/Dark Skin 対応色定数)
├── UI/
│   ├── MaskPaintView.cs       (DrawMaskSection + ペイント入力)
│   ├── PreviewView.cs         (DrawPreview — PreviewJob<T> 使用)
│   ├── DetailPreviewView.cs   (詳細プレビュー)
│   ├── ExportView.cs          (DrawExportSection / DrawBatchSection)
│   └── PresetsView.cs         (DrawPresetsSection)
├── Core/
│   ├── VACCSessionState.cs    ([Serializable] 編集状態コンテナ — 既存のハードコード初期値を集約)
│   ├── ColorZoneDrawer.cs     (CustomPropertyDrawer<ColorZone>)
│   ├── MaskState.cs           (RLE永続表現 + MaskZoneEntry)
│   ├── PixelProcessor.cs      (静的処理メソッド群 — Editor 非依存・テスト可能)
│   ├── PreviewJob.cs          (PreviewJob<T> 非同期ヘルパー)
│   └── TextureSlot.cs         (Texture2D ライフサイクル管理)
└── Infra/
    ├── MaskFileStore.cs       (<Project>/UserSettings/VACC/MaskCache/<GUID>.vacc-mask.json I/O)
    ├── PresetStore.cs         (プリセット JSON I/O)
    └── VACCAssetWatcher.cs    (AssetPostprocessor — delete 時クリーンアップのみ)
```

> 注記: 本書ではリポジトリ上の説明を簡潔にするため `Code/` 表記を使うが、実際の Unity プロジェクト配置は `Assets/VACC/Editor/` 配下を想定する。`Code/UI/...` は実配置では `Assets/VACC/Editor/UI/...` に対応する。
> 注記: この計画は **Assets/VACC/Editor へのインプロジェクト配置** を前提とする。VPM / Packages 配置との完全両立は今回のリファクタ対象外とし、必要なら別タスクで扱う。

### 設定ファイルとマスクの保存場所

| データ | 保存先 | 備考 |
|--------|--------|------|
| プリセット | 既存の場所 | ゾーン設定（各ゾーンのアドバンスフィールド含む）+ 処理パラメータ（`edgeFeather`, `antiAliasCleanup`, `useDecontamination`, `decontaminationRadius`, `advancedMode`, `holeFillPasses`, `holeFillMinNeighbors`, `relaxedSatMin`, `relaxedSatRamp`）+ マスク（任意・フルレス RLE）を一括保存・復元。プロジェクト単位・git 管理 |
| 初期値（ゾーン + 処理パラメータ） | コード内（現行 `VACCWindow` の `[SerializeField]` 初期化子を `VACCSessionState.CreateDefault()` に移管） | ユーザー編集不可。プリセット未読込時の基準値としてのみ使用 |
| マスクデータ（フルレス） | `<Project>/UserSettings/VACC/MaskCache/<テクスチャGUID>.vacc-mask.json` | git 非追跡フォルダ（個人作業データ）。GUID ベースのため rename/move 自動追従 |

**初期値の扱い**: 新しい ScriptableObject アセットは作成しない。現行 `VACCWindow.cs` にある `[SerializeField]` の初期化子（例: `edgeFeather = 0f`, `antiAliasCleanup = 3`, `useDecontamination = true`, `decontaminationRadius = 4`, `holeFillPasses = 5`, `holeFillMinNeighbors = 4`, `relaxedSatMin = 0.02f`, `relaxedSatRamp = 0.08f`）を `VACCSessionState.CreateDefault()` に 1:1 で移管し、プリセット未読込時の唯一の基準値とする。再現性はプリセット保存・読込で担保する。

**マスクのデータ構造を再設計**: `bool[] + Dictionary<string, bool[]>` → `MaskState`（RLE エンコード文字列 + `List<MaskZoneEntry>`）に変更。これにより Unity の SerializedObject でシリアライズ可能になり、`[SerializeField]` で持つことで Unity 標準 Undo に乗る。`bool[]` は実行時バッファ（`[NonSerialized]`）として保持し、ペイント時のみ更新→ストローク終了時に RLE 文字列へ encode。独自 Ctrl+Z / `_undoMaskHistory` は撤去。

**プリセット内マスクはフルレスで保存**: 解像度を落とすとペイント境界の精度が失われるため、プリセット JSON への同梱時もフルレスのまま RLE エンコードして保存する（`presetIncludeMasks` オプトイン）。RLE の特性上、典型的な使用（疎な除外領域）では 4096² テクスチャでも数KB〜数十KB に収まる。MaskCache にも同じフルレスマスクが保持される。現時点では**再現性優先のため `presetIncludeMasks` の既定値は ON のまま**とし、git 差分コストが問題化した時点で既定値変更を検討する。

### asmdef について

既存の `com.yukkuri-aoba.vrc-avatar-color-changer.Editor.asmdef` をそのまま使い続ける。  
今回の前提では、**asmdef は `Assets/VACC/Editor/` に置く** のを推奨する。`VACCWindow.cs` を含む全コードが Editor 専用であり、`PixelProcessor` も再利用予定がないため、最も単純で誤解が少ない配置になる。`UI/` / `Core/` / `Infra/` をその配下に作れば、すべて同じ Editor アセンブリに包含される。`Editor` フォルダ名と asmdef の `includePlatforms: ["Editor"]` は役割が重複するが、安全側の二重ガードとしては問題ない。

推奨配置:

```
Assets/
└── VACC/
    └── Editor/
        ├── com.yukkuri-aoba.vrc-avatar-color-changer.Editor.asmdef
        ├── VACCWindow.cs
        ├── VACCConsts.cs
        ├── VACCColors.cs
        ├── UI/
        ├── Core/
        └── Infra/
```

> 補足: `PixelProcessor` や `VACCSessionState` も `Assets/VACC/Editor/` 配下に置く限り Editor 専用コンパイルになる。この計画における「Editor 非依存・テスト可能」は、**UnityEditor API に依存しない純粋ロジックとして分離する** という意味に留まる。将来 Runtime/非Editor テストで再利用したくなった場合は、後から Editor 外アセンブリへ再分離する。

### ファイル責務の境界

着手前に責務境界を以下で固定する。これを超える処理は他ファイルへ移す。

| ファイル | 持つ責務 | 持たない責務 |
|---------|---------|-------------|
| `VACCWindow.cs` | `EditorWindow` のライフサイクル、メニュー登録、`OnGUI` のホスト、`SerializedObject(this)` 更新、各 View の生成と配線、Undo/Repaint の起点 | ピクセル処理本体、JSON I/O、マスクファイル I/O、各セクションの詳細 UI 実装 |
| `UI/PreviewView.cs` | メインプレビュー、比較/差分モード、ズーム、プレビュー生成ジョブの起動、表示用 Texture の所有 | プリセット保存、マスク永続化、エクスポート、ゾーン一覧編集 |
| `UI/DetailPreviewView.cs` | 詳細プレビュー専用の切り出し表示と詳細差分生成。**`PreviewView` の補助に限定** | 単独のトップレベル状態管理、プリセット/エクスポート/マスク保存 |
| `UI/MaskPaintView.cs` | マスク対象選択、ブラシ入力、オーバーレイ再構築、`bool[]` バッファと `_session.maskState` の同期、`MaskFileStore` との連携 | ゾーン本体の編集 UI、プレビュー生成本体、プリセット一覧、エクスポート |
| `UI/ExportView.cs` | 単体/一括出力 UI、PNG 書き出し、必要最小限の AssetDatabase 連携 | プレビュー生成、プリセット管理、マスク編集 |
| `UI/PresetsView.cs` | プリセット一覧 UI、保存/読込/JSON 入出力、`PresetStore` 呼び出し | マスク描画入力、プレビュー生成、エクスポート |
| `Core/VACCSessionState.cs` | ゾーン、処理パラメータ、`MaskState` をまとめた**編集状態の永続表現** | GUI、ファイル I/O、`AssetDatabase`、一時 Texture、非同期処理 |
| `Core/MaskState.cs` | マスク永続表現 (`commonMaskBase64`, `MaskZoneEntry`) のデータ定義 | ブラシ処理、ファイル保存、Texture 操作 |
| `Core/ColorZoneDrawer.cs` | `ColorZone` 1件分の描画 | ゾーン追加/削除、アクティブマスク切替、プリセット保存、リスト全体制御 |
| `Core/PixelProcessor.cs` | 再着色アルゴリズム、ダウンサンプル、フラッドフィル等の純粋処理 | `EditorWindow`, `AssetDatabase`, GUI, ファイル I/O |
| `Core/PreviewJob.cs` | 非同期ジョブの世代管理・キャンセル制御 | UI レイアウト、個別アルゴリズム、ファイル保存 |
| `Core/TextureSlot.cs` | 一時 Texture2D の確保/解放補助 | ピクセル処理本体、永続化 |
| `Infra/PresetStore.cs` | プリセット JSON の保存/読込、固定パス解決、既存 `VACCPresetData` スキーマ互換維持 | GUI、プレビュー状態、マスク描画 |
| `Infra/MaskFileStore.cs` | マスク JSON ファイルの保存/読込/削除 | ブラシ処理、オーバーレイ生成、プレビュー描画 |
| `Infra/VACCAssetWatcher.cs` | テクスチャ削除時のマスクファイル後始末 | 他のアプリケーションロジック |

依存ルール:

- `UI` は `Core` を参照してよいが、`UI` 同士の直接依存は原則禁止。例外は `PreviewView` が `DetailPreviewView` を補助として所有する場合のみ。
- `Infra` は `Core` のデータ型を使ってよいが、`UI` や `VACCWindow` を参照しない。
- `Core` は `UI` / `Infra` に依存しない。
- `VACCWindow` はホストであり、各 View/Store/Helper を束ねるが、処理本体を抱え込まない。

### ドキュメントだけで実装する際の注意点

この計画は **既存実装を動作互換のまま分割・整理するリファクタ** を前提とする。明示的な変更指示がない限り、アルゴリズム・JSON フィールド名・UI 挙動は変えず、**既存メソッド本体を移動または薄いラッパー化で保つ**。

仕様の正は **この計画書 + 現行コードの現在動作** とする。両者が衝突した場合は、まず「動作互換を保つ」という原則を優先し、必要ならこの文書へ追記してから実装する。

状態の持ち主は以下で固定する:

| 現在の主なフィールド | 移管先 | 備考 |
|-------------------|--------|------|
| `zones`, `edgeFeather`, `antiAliasCleanup`, `useDecontamination`, `decontaminationRadius`, `advancedMode`, `holeFillPasses`, `holeFillMinNeighbors`, `relaxedSatMin`, `relaxedSatRamp` | `Core/VACCSessionState.cs` | 再現性対象の編集状態 |
| `sourceTexture` | `VACCWindow.cs` | Preview / Mask / Export から共有参照されるホスト状態 |
| `zonesFoldout`, `processingFoldout`, `_windowSerializedObject`, `_sessionProperty`, `_zonesProperty` | `VACCWindow.cs` | ホストが直接描くセクションと `SerializedObject(this)` 管理 |
| `saveAsNewFile`, `newFileName`, `inheritImportSettings`, `exportFoldout`, `batchFoldout`, `batchTextures`, `batchScrollPos` | `UI/ExportView.cs` | エクスポート専用 UI 状態 |
| `previewZoom`, `comparisonMode`, `diffMode`, `previewDirty`, preview textures, async flags, source pixel cache | `UI/PreviewView.cs` | プレビュー無効化は他所から `MarkDirty()` 相当で通知 |
| `activeMaskTarget`, `brushSize`, `brushEraseMode`, `maskFoldout`, `maskPaintActive`, `isPainting`, `lastPaintUV`, `bool[]` バッファ, overlay textures, `maskDirty` | `UI/MaskPaintView.cs` | 永続表現は `_session.maskState`、実行時バッファは View 側 |
| `presetsFoldout`, `presetSaveName`, `presetStorageProject`, `presetIncludeMasks`, `presetApplyMasks`, `presetScrollPos` | `UI/PresetsView.cs` | プリセット専用 UI 状態 |

互換性ルール:

- `VACCPresetData` の JSON フィールド名は変更しない。
- `JsonUtility` の不足フィールドは **フィールド初期化子を保持する** という前提で後方互換を保つ。`LoadPreset()` 側で `> 0 ? value : default` のような manual defaulting を入れない。
- `MaskState` の RLE 文字列フォーマットは既存の `EncodeMask` / `DecodeMask` と互換に保つ。
- 旧 `SessionState` キーから `MaskFileStore` への移行は一度きりで、移行後は旧キーを削除する。

実装ルール:

- `PreviewView` は `DetailPreviewView` を補助オブジェクトとして所有してよいが、逆依存は禁止。
- 各 View/Store は可能な限り **必要最小限の依存だけ** をコンストラクタまたは初期化メソッドで受け取る。`VACCWindow` 全体を丸ごと渡して責務を曖昧にしない。
- `previewDirty` / `maskDirty` のような無効化通知は、他コンポーネントから直接フィールドを書き換えるのではなく、ホストまたは所有 View のメソッド経由で通知する。
- フェーズ途中でも、コンパイルが通る中間状態を保つ。特に Phase 4 は「新 View 作成 → 呼び出し切替 → 旧 partial 削除」の順で進める。
- 新しい serialized field や `ColorZoneDrawer` / `VACCSessionState` に移す既存フィールドは、**既存ツールチップを維持**する。`SerializedProperty` 化で GUIContent 手書きをやめる箇所は `[Tooltip]` 属性を追加する。

---

## フェーズ一覧

| Phase | 対象項目 | 難度 | 推定コミット数 |
|-------|---------|------|--------------|
| 0 | 回帰ベースライン固定 | 低 | ~2 |
| 1 | 3, 9, 10, 12, 13, 14, 15, 16, 17, 18, 19, 20 | 低 | ~12 |
| 2 | 8 | 中 | ~5 |
| 3 | 6(partial), 11 | 中 | ~5 |
| 4a | 状態構造再設計（`VACCSessionState` + `MaskState`） | 高 | ~6 |
| 4b | View 分離（partial 廃止） | 中 | ~6 |
| 4c | Undo 統合と独自 Ctrl+Z 撤去 | 中 | ~4 |
| 5 | 7 | 中 | ~4 |
| 6 | 4, 5 | 中 | ~4 |

---

## Phase 0 — 回帰ベースライン固定

リファクタ開始前に、既存の `dev_safe/Tests/` と `AlgorithmCore.cs` ミラーを **安全網としてのみ** 利用し、現在のアルゴリズム出力を固定する。テスト再設計は後で行ってよいが、Phase 中の回帰検知は残す。

### 0-1. ベースラインの仕様

| 項目 | 内容 |
|------|------|
| 保存先 | `dev_safe/Tests/Baselines/refactor-pre/` |
| 内容 | (a) 既存 `GroundTruthIoUTests` 等の全テスト出力ログ（IoU 数値・ケース名）／(b) 代表 1〜2 ケースの再着色後 PNG（差分目視用） |
| 形式 | (a) は plain text のテストランナー出力（`dotnet test ... > baseline.log` でリダイレクト）／(b) は既存テスト内で書き出している PNG をコピー |
| 比較方法 | (a) は新ログとの diff で IoU 数値の完全一致または許容差（±0.001）以内を確認／(b) は目視 |

**ベースラインは Phase 0 のコミットに含めて固定する**。途中で再生成しない（再生成すると比較対象が動いてしまうため）。

### 0-2. 比較を実施するタイミング

| タイミング | 比較対象 | 必須/任意 |
|-----------|---------|----------|
| Phase 3 完了時 | (a) | 必須 |
| Phase 4a 完了時 | (a) | 必須 |
| Phase 4b 完了時 | (a) | 必須 |
| Phase 4c 完了時 | (a) | 必須 |
| Phase 5 完了時 | (a) | 必須 |
| **Phase 6 完了時** | **(a) + (b)** | **必須（最終比較）** |
| 上記以外（Phase 1, 2 等） | — | 任意（アルゴリズム非変更フェーズなので） |

ベースラインと乖離が出た場合は、構造変更ではなくロジック変更が入っていないかを優先確認する。

コミット: `test(refactor): 既存AlgorithmCoreベースラインを凍結`

---

## Phase 1 — 即効クリーンアップ（項目 3, 9, 10, 12, 13, 14, 15, 16, 17, 18, 19, 20）

低リスクの機械的変更のみ。各ステップは独立したコミット。

### 1-1. 項目3: `AssetDatabase.Refresh()` → `ImportAsset()` に置換

対象ファイル:
- `Code/VACCWindow.Export.cs:169` — 単体出力後: `AssetDatabase.ImportAsset(relativePath)` に変更
- `Code/VACCWindow.Export.cs:323` — バッチ出力: `StartAssetEditing()` / `StopAssetEditing()` でラップし、`Refresh` を削除
- `Code/VACCWindow.Presets.cs:126, 152` — Assets 配下なら `AssetDatabase.DeleteAsset()` / `ImportAsset()`、Assets 外なら `File.Delete()` のみ（`Refresh` 不要）

```csharp
// Export.cs 単体出力後の例
File.WriteAllBytes(outputPath, pngData);
string relativePath = ToAssetsRelative(outputPath);
if (relativePath != null)
    AssetDatabase.ImportAsset(relativePath);

// Export.cs バッチ出力の例
try
{
    AssetDatabase.StartAssetEditing();
    foreach (var tex in batchTextures)
    {
        /* PNGを書き出す */
        string relativePath = ToAssetsRelative(outputPath);
        if (relativePath != null)
            AssetDatabase.ImportAsset(relativePath);
    }
}
finally
{
    AssetDatabase.StopAssetEditing();
}
```

コミット: `perf(export): AssetDatabase.RefreshをImportAssetに置換して応答速度改善`

### 1-2. 項目9: `Localization.CurrentLanguage` を `EditorPrefs` に永続化

対象: `Code/Localization.cs:7`

```csharp
public static class Localization
{
    private const string PrefsKey = "VACC.Language";
    public static LanguageMode CurrentLanguage;

    static Localization()
    {
        CurrentLanguage = (LanguageMode)EditorPrefs.GetInt(PrefsKey, (int)LanguageMode.Auto);
    }

    public static void SaveLanguagePreference()
        => EditorPrefs.SetInt(PrefsKey, (int)CurrentLanguage);
}
```

`CurrentLanguage` は **public static field のまま維持**し、外部スクリプトからのソース互換・バイナリ互換を壊さない。ヘッダー UI 側で値変更後に `SaveLanguagePreference()` を呼ぶ。

**Save 呼び忘れチェック**: 代入だけでは永続化されないため、実装後に必ず以下を確認する:

```
grep -n "Localization.CurrentLanguage\s*=" Code/
```

ヒットしたすべての行の直後で `Localization.SaveLanguagePreference()` が呼ばれていることを目視確認する。代入箇所が 1 箇所に限られない場合は、`SetCurrentLanguage(LanguageMode)` ヘルパーに集約することも検討する。

コミット: `fix(ui): 言語設定をEditorPrefsに永続化し再起動後も維持`

### 1-3. 項目10: ハードコード色を `VACCColors` クラスに集約

`Code/VACCColors.cs` を新規作成し、`EditorGUIUtility.isProSkin` で分岐:

```csharp
internal static class VACCColors
{
    public static Color ActiveMaskTarget =>
        EditorGUIUtility.isProSkin
            ? new Color(0.45f, 0.65f, 0.95f)
            : new Color(0.30f, 0.55f, 0.85f);

    public static Color ExcludeButton =>
        EditorGUIUtility.isProSkin
            ? new Color(1f, 0.55f, 0.55f)
            : new Color(0.85f, 0.30f, 0.30f);

    public static Color IncludeButton =>
        EditorGUIUtility.isProSkin
            ? new Color(0.55f, 1f, 0.55f)
            : new Color(0.30f, 0.75f, 0.30f);

    // ブラシカーソルは Exclude/Include の意味区別を保つため 2 定数に分離する
    public static Color BrushCursorExclude => new Color(1f, 0f, 0f, 0.5f); // 赤
    public static Color BrushCursorInclude => new Color(0f, 1f, 0f, 0.5f); // 緑
}
```

置換箇所:
- `Code/VACCWindow.cs:284` — ゾーンマスク編集ボタンのアクティブ色（`ActiveMaskTarget`）
- `Code/VACCWindow.Mask.cs:103, 112` — Exclude/Include ボタン（`ExcludeButton` 等）
- `Code/VACCWindow.Preview.cs:419-421` — ブラシカーソル色（`BrushCursorExclude` / `BrushCursorInclude` を `brushEraseMode` で出し分け、コード上の赤=Exclude / 緑=Include の意味を維持）

コミット: `refactor(ui): ハードコード色をVACCColorsに集約しLight/DarkSkin対応`

### 1-4. 項目12: `EnableReadWrite` は Undo 対応とせず明示確認を追加

対象: `Code/VACCWindow.cs:494-504`

```csharp
if (EditorUtility.DisplayDialog(
        Localization.Confirm,
        Localization.EnableReadWriteConfirm,
        Localization.OK,
        Localization.Cancel))
{
    importer.isReadable = true;
    importer.SaveAndReimport();
}
```

`TextureImporter.SaveAndReimport()` はディスク上の import 設定を書き換えるため、`Undo.RecordObject(importer)` だけで「Undo 対応」と謳うのは不正確。ここでは **Undo 不可の明示確認アクション** として扱う。

コミット: `fix(ui): ReadWrite有効化前に確認ダイアログを追加`

### 1-5. 項目13: 例外握り潰しを修正

- `Code/VACCWindow.cs` の `catch { }` は、**想定済み例外だけを個別に扱い、それ以外はログに出す** 形へ変更する
- `Code/VACCWindow.Mask.cs` のデコード失敗 catch に `Debug.LogWarning($"[VACC] Mask decode failed: {ex.Message}")` を追加

例:

```csharp
catch (ObjectDisposedException)
{
}
catch (InvalidOperationException ex)
{
    Debug.LogWarning($"[VACC] Ignored UI teardown exception: {ex.Message}");
}
catch (Exception ex)
{
    Debug.LogException(ex);
}
```

コミット: `fix: 例外の握り潰しを修正しデバッグログを追加`

### 1-6. 項目14: `ProjectPresetFolder` の探索を廃止し固定パス化

対象: `Code/VACCWindow.Presets.cs:20-44`

```csharp
private const string ProjectPresetFolderRelative = "Assets/VACC/Presets";

private static string ProjectPresetFolder
    => Path.GetFullPath(Path.Combine(
        Application.dataPath, "..", ProjectPresetFolderRelative));
```

`Assets/VACC/Editor/` 配置を前提にするため、`AssetDatabase.FindAssets("t:Script VACCWindow")` による自己探索は不要。Phase 4 で `PresetStore` を導入したら、この固定パス解決も `Infra/PresetStore.cs` に移す。

コミット: `refactor(preset): ProjectPresetFolderの自己探索を廃止して固定パス化`

### 1-7. 項目15: `[MenuItem]` に priority 追加

対象: `Code/VACCWindow.cs:57`

```csharp
[MenuItem("Tools/yukkuri-aoba/VRC AvatarColorChanger", priority = 100)]
```

`priority = 100` は、`Tools` 配下の他ツールより極端に上にも下にも寄せず、同作者配下の将来項目を前に差し込める余地を残すための値とする。

あわせて `titleContent` にアイコンを設定:

```csharp
window.titleContent = new GUIContent(
    Localization.WindowTitle,
    EditorGUIUtility.IconContent("d_Image Icon").image);
```

コミット: `chore(ui): MenuItemにpriority追加とtitleContentにアイコン設定`

### 1-8. 項目16: `ColorZone.Clone()` → `MemberwiseClone()` に変更

対象: `Code/ColorZone.cs:99-100`

```csharp
// 変更前
public ColorZone Clone()
    => JsonUtility.FromJson<ColorZone>(JsonUtility.ToJson(this));

// 変更後
public ColorZone Clone() => (ColorZone)MemberwiseClone();
```

`[NonSerialized]` キャッシュフィールドはコピー後 `UpdateCacheIfNeeded` で再計算されるため安全。

コミット: `perf(color): ColorZone.CloneをMemberwiseCloneに変更しアロケーション削減`

### 1-9. 項目17+20: `VACCConsts.cs` にマジックナンバーと文字列定数を集約

`Code/VACCConsts.cs` を新規作成:

```csharp
internal static class VACCConsts
{
    public const string MenuPath    = "Tools/yukkuri-aoba/VRC AvatarColorChanger";

    public static class Layout
    {
        public const float SideBySideMinWidth = 600f;
        public const float LeftColumnRatio    = 0.4f;
        public const float LeftColumnMin      = 280f;
        public const float LeftColumnMax      = 450f;
        public const float RemoveButtonWidth  = 22f;
        public const float SmallButtonWidth   = 48f;
    }
}
```

`VACCWindow.cs` 内の散在するリテラルを置換する。**表示文字列の正は `Localization` に残し、`WindowTitle` は `VACCConsts` に重複定義しない。**

コミット: `refactor(ui): マジックナンバーとMenuPath/WindowTitleをVACCConstsに集約`

### 1-10. 項目18: `EditorGUI.indentLevel` → `EditorGUI.IndentLevelScope`

Unity 標準の `EditorGUI.IndentLevelScope` を使う:

```csharp
using (new EditorGUI.IndentLevelScope())
{
    holeFillPasses = EditorGUILayout.IntSlider(...);
}
```

コミット: `refactor(ui): indentLevelの手動管理をEditorGUI.IndentLevelScopeに置換`

### 1-11. 項目19: `BeginChangeCheck` の運用整理

`DrawZoneList` 内で `previewDirty = true` を明示しているケースを `BeginChangeCheck/EndChangeCheck` 対象内に移動して整理する（Phase 4 の `ApplyModifiedProperties()` 戻り値による一元化で完全解消予定）。

コミット: `refactor(ui): BeginChangeCheck範囲を整理`

**Phase 1 検証:**
- Unity でツールを開き各 UI 操作が正常に動作することを確認（手動）
- Phase 1 はアルゴリズムを変更しないため、テストは別途再設計後に実施

---

## Phase 2 — TextureSlot ヘルパー（項目 8）

### 2-1. `Code/Core/TextureSlot.cs` を新規作成

```csharp
internal static class TextureSlot
{
    public static void Resize(ref Texture2D tex, int w, int h,
                               FilterMode filter = FilterMode.Bilinear)
    {
        if (tex != null && tex.width == w && tex.height == h) return;
        DestroyNow(ref tex);
        tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter };
    }

    public static void Release(ref Texture2D tex)
    {
        DestroyNow(ref tex);
    }

    private static void DestroyNow(ref Texture2D t)
    {
        if (t == null) return;
        Object.DestroyImmediate(t);
        t = null;
    }
}
```

`TextureSlot` は **メインスレッドからのみ** 呼ぶ前提とし、遅延破棄は使わない。理由がない `delayCall` はリーク追跡と制御フローを複雑にする。

### 2-2〜2-5. 各ファイルのテクスチャ管理を `TextureSlot` に置換

| 対象ファイル | 対象箇所 | 変更内容 |
|------------|---------|---------|
| `VACCWindow.cs` | `OnDestroy` の 9 個の手動 `DestroyImmediate` | `TextureSlot.Release(ref xxx)` に置換 |
| `VACCWindow.Preview.cs:679-712` | テクスチャ再確保ロジック | `TextureSlot.Resize(ref _previewTex, w, h)` に置換 |
| `VACCWindow.Mask.cs:319-414` | オーバーレイテクスチャ再確保 | `TextureSlot.Resize` に置換 |
| `VACCWindow.DetailPreview.cs:185-234` | detail テクスチャ再確保 | `TextureSlot.Resize` に置換 |

各ファイルを独立コミット: `refactor(ui): TextureSlotヘルパーで{Preview|Mask|Detail}のテクスチャ管理を統一`

**Phase 2 検証:**
- プレビュー生成・マスクペイント・詳細プレビューでテクスチャが正常に表示・破棄されることを確認
- Unity Profiler で Texture2D の Leak がないことを確認

---

## Phase 3 — PixelProcessor 分離 + PreviewJob（項目 6 partial / 11）

### 3-1. `Code/Core/PixelProcessor.cs` を新規作成

`Code/VACCWindow.Processing.cs` の static メソッド群（`ProcessPixelsArray`, `BoxDownsample`, フラッドフィル等）を `PixelProcessor` クラスに移管。  
`VACCWindow.Processing.cs` は削除（または薄いプロキシとして残す）。

> **テストとの関係について**: 現在 `dev_safe/Tests/` は Unity 非依存の `AlgorithmCore.cs`（手動同期されたミラー実装）を経由してテストしており、`VACCWindow` のメソッドを直接呼んではいない。本リファクタの後、テスト戦略は別途設計し直す前提のため、この計画書では扱わない。

コミット: `refactor(export): PixelProcessorを独立クラスに切り出し`

### 3-2. `Code/Core/PreviewJob.cs` を新規作成

```csharp
internal sealed class PreviewJob<T> : IDisposable
{
    private static readonly ConcurrentQueue<Action> MainThreadQueue = new();
    private CancellationTokenSource _cts;
    private int _generation;

    [InitializeOnLoadMethod]
    private static void InstallMainThreadPump()
    {
        EditorApplication.update -= DrainMainThreadQueue;
        EditorApplication.update += DrainMainThreadQueue;
    }

    private static void DrainMainThreadQueue()
    {
        while (MainThreadQueue.TryDequeue(out var action))
            action();
    }

    public void Schedule(Func<CancellationToken, T> work, Action<T> apply, Action<Exception> onError = null)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int myGen = ++_generation;
        Task.Run(() =>
        {
            try
            {
                var result = work(token);
                token.ThrowIfCancellationRequested();
                MainThreadQueue.Enqueue(() =>
                {
                    if (myGen == _generation) apply(result);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MainThreadQueue.Enqueue(() => onError?.Invoke(ex));
            }
        }, token);
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
```

`EditorApplication.delayCall` をバックグラウンドスレッドから直接触らない。メインスレッド復帰は `EditorApplication.update` で捌くキューを介し、キャンセル以外の例外はログへ流す。

コミット: `refactor(ui): PreviewJobヘルパーを追加`

### 3-3〜3-4. Preview / DetailPreview の非同期部分を `PreviewJob<T>` で書き直し

- `VACCWindow.Preview.cs` の volatile フラグ群（`_previewGenerating`, `_asyncGeneration` 等）を `PreviewJob<T>` 内部に隠蔽
- `VACCWindow.DetailPreview.cs` の重複 Async パターンも同様に統一

コミット: `refactor(ui): PreviewJobヘルパーでAsyncパターンを統一`

**Phase 3 検証:**
- PixelProcessor の static メソッド群が Unity 上で従来通り動作することを確認（手動でゾーン色変更→プレビュー結果の差分目視）
- プレビュー生成・詳細プレビューが正常に動作することを確認

---

## Phase 4 — `VACCSessionState` 化と View 分離、Undo 統合（項目 1 / 2 / 6 全体）

最大規模フェーズ。**partial class を完全廃止**し UI / Core / Infra 構造に移行する。  
あわせて **マスクのデータ構造を再設計** し、Unity 標準 Undo に統合する（ゾーン設定 + 処理パラメータ + マスクすべてが Ctrl+Z / Ctrl+Y で戻る/進むようになる）。初期値は新規アセットではなく、現行 `VACCWindow` にハードコードされている既定値から移管する。

**実装順序は 4a / 4b / 4c に分割する。**

- **4a**: `VACCSessionState` と `MaskState` の導入、既存 partial を維持したまま状態の持ち主だけ移す
- **4b**: View クラス分離。状態構造を固定したまま `VACCWindow` を薄いホストへ縮退させる
- **4c**: Unity Undo 統合と独自 Ctrl+Z 撤去

1 回のコミットでこの 3 つを同時に進めない。各サブフェーズごとにコンパイル・手動確認・回帰チェックを行う。

### マスクのデータ構造再設計

現状の `bool[] exclusionMask` / `Dictionary<string, bool[]> zoneMasks` は Unity の SerializedObject でそのままシリアライズできない（`Dictionary` はシリアライズ非対応）。また `bool[]` を直接 `[SerializeField]` にすると 4096² で 16MB のスナップショットになり Undo コスト過大。

→ 既存の **RLE エンコード文字列**（`EncodeMask` / `DecodeMask`）を Unity の永続表現として採用し、`bool[]` は実行時専用バッファに格下げする:

```csharp
namespace VRCAvatarColorChanger
{
    [System.Serializable]
    public class MaskZoneEntry
    {
        public string zoneId;
        public string maskBase64; // EncodeMask 結果（RLE + Base64）
    }

    [System.Serializable]
    public class MaskState
    {
        public int width;
        public int height;
        public string commonMaskBase64;                // 共通マスク（空文字 = 未設定）
        public List<MaskZoneEntry> zones = new();     // ゾーン別マスク
    }
}
```

`VACCPresetData` 側の既存 JSON 互換を壊さないため、**フィールド名は `maskBase64` / `commonMaskBase64` に揃える**。新規の内部型名は `MaskZoneEntry` とし、既存 `VACCPresetData.ZoneMaskEntry` との型名衝突を避ける。

`VACCWindow` 側のフィールドを以下のように再構成する:

```csharp
// 編集状態（Unity Undo 対象）
[SerializeField] private VACCSessionState _session = VACCSessionState.CreateDefault();

// 実行時バッファ（Undo 非対象）
[System.NonSerialized] private bool[] exclusionMask;
[System.NonSerialized] private Dictionary<string, bool[]> zoneMasks = new();
[System.NonSerialized] private bool _maskBuffersDirty; // _session.maskState からの再構築待ち
```

**同期ルール**:
- 編集（ペイント等）: `bool[]` バッファを直接更新 → ストローク終了時に `EncodeMask` で `_session.maskState` に書き戻す
- Undo / Redo / 起動 / sourceTexture 切替: `_session.maskState` の RLE 文字列を `DecodeMask` で `bool[]` バッファに展開
- `OnUndoRedoPerformed` が呼ばれたら `_maskBuffersDirty = true` にして次フレームでバッファを再構築

**スナップショットコスト（Undo 用）**: RLE + Base64 文字列なので、典型的な使用（疎な除外領域）で数 KB〜数十 KB／ストローク。ワーストケース（高エントロピー）でも数 MB 程度に収まり、`bool[]` 直書きの 16MB に比べ十分許容範囲。

**プリセットへのマスク同梱コスト**: 解像度を落とすとペイント境界の精度が失われるため、プリセット JSON にもフルレスのまま RLE エンコードして同梱する。RLE の特性上、典型的な使用（疎な除外領域）では 4096² テクスチャでも数KB〜数十KB に収まり問題ない。

**独自 `_undoMaskHistory` / `HandleGlobalKeyboardShortcuts` の Ctrl+Z は撤去** する（Unity 標準 Undo に置き換わるため）。

### 4-1. `Code/Core/VACCSessionState.cs` を新規作成

現行 `VACCWindow` の `[SerializeField]` フィールド群を、編集状態コンテナ `VACCSessionState` に集約する。初期値は既存コードのハードコード値をそのまま移管する：

```csharp
[System.Serializable]
internal class VACCSessionState
{
    public List<ColorZone> zones = new();
    public float edgeFeather = 0f;
    public int antiAliasCleanup = 3;
    public bool useDecontamination = true;
    public int decontaminationRadius = 4;
    public bool advancedMode;
    public int holeFillPasses = 5;
    public int holeFillMinNeighbors = 4;
    public float relaxedSatMin = 0.02f;
    public float relaxedSatRamp = 0.08f;
    public MaskState maskState = new();

    public static VACCSessionState CreateDefault() => new VACCSessionState();
}
```

移管元は、現行 `VACCWindow` の `zones = new List<ColorZone>()`, `edgeFeather = 0f`, `antiAliasCleanup = 3`, `useDecontamination = true`, `decontaminationRadius = 4`, `holeFillPasses = 5`, `holeFillMinNeighbors = 4`, `relaxedSatMin = 0.02f`, `relaxedSatRamp = 0.08f` などの初期化子。**新しい ScriptableObject アセットは作らない**。

コミット: `refactor(ui): VACCSessionStateに編集状態と既定値を集約`

### 4-2. `Code/Core/ColorZoneDrawer.cs` を新規作成

```csharp
[CustomPropertyDrawer(typeof(ColorZone))]
public class ColorZoneDrawer : PropertyDrawer
{
    // ColorZone 内のフィールド描画ロジックをここに移管
    // [Range] / [Tooltip] 属性が自動的に反映される
}
```

`DrawZoneList` の 170 行のうち、`_session.zones` 内の ColorZone 単体のフィールド描画部分（約 100〜120 行）が PropertyDrawer に吸収される。  
ただし **以下は外側のコレクションロジックとして残る**:
- ゾーン削除（×）ボタン、ゾーン追加ボタン
- ゾーンマスク編集ボタン（`activeMaskTarget` と連動するためゾーン単独の描画では完結しない）
- `advancedMode` 連動の表示分岐の一部

`ColorZone` の `[NonSerialized]` キャッシュは、既存通り `UpdateCacheIfNeeded()` / `GetMatchScores()` から再計算される前提を維持する。`ColorZoneDrawer` はキャッシュフィールドを直接触らず、プレビュー・処理側が **必ず既存 API を通って評価** することを前提とする。

最終的に `DrawZoneList` は **40〜50 行程度** に縮退する見込み。

> **レイアウト記法の注意**: `PropertyDrawer.OnGUI` は `Rect` ベースが基本で、`GetPropertyHeight` の上書きと併せて書く。既存の `EditorGUILayout.*` ベタ書きから移行する際、ホスト側（`DrawZoneList`）の行数は減るが、**`ColorZoneDrawer` 自体の総コードはむしろ増える**ことが多い。各フィールドの Rect 計算と `[Range]` 属性に対応した `EditorGUI.PropertyField` への置き換えで、見積もりより重い作業になりうる点に注意。`OnGUI` 内で `EditorGUILayout` を使う書き方も可能だが、Unity 公式は `Rect` ベース推奨。

コミット: `refactor(ui): ColorZoneDrawerを実装しDrawZoneListを簡略化`

### 4-3. `VACCWindow` に `SerializedObject(this)` ベースの描画基盤を追加

```csharp
[SerializeField] private VACCSessionState _session = VACCSessionState.CreateDefault();

private SerializedObject _windowSerializedObject;
private SerializedProperty _sessionProperty;
private SerializedProperty _zonesProperty;

private void OnEnable()
{
    _session ??= VACCSessionState.CreateDefault();
    _windowSerializedObject = new SerializedObject(this);
    _sessionProperty = _windowSerializedObject.FindProperty("_session");
    _zonesProperty = _sessionProperty.FindPropertyRelative("zones");
}
```

これにより、初期値のソースはコード内に留めたまま、`_session` を `SerializedObject` 経由で安全に編集できる。専用の CustomEditor やプロジェクト共有アセットは導入しない。

コミット: `refactor(ui): VACCWindowにVACCSessionStateのSerializedObject描画基盤を追加`

### 4-4. VACCWindow を薄いホストに変更

`OnGUI()` は以下のみを担当:
- `HandleGlobalKeyboardShortcuts()` の Undo 関連を除いた部分
- `_windowSerializedObject.Update()` / `ApplyModifiedProperties()`
- アクティブプリセット選択 UI
- `_session` のゾーン定義・処理パラメータ表示 / 入力
- `maskPaintView.Draw()` / `previewView.Draw()` / `exportView.Draw()` / `presetsView.Draw()`

コミット: `refactor(ui): VACCWindowを薄いホストに変更`

### 4-5. UI/ サブクラスを作成し partial class ファイルを移行・削除

| 新ファイル | 移管元 |
|----------|--------|
| `Code/UI/MaskPaintView.cs` | `VACCWindow.Mask.cs` の DrawMaskSection + ペイント入力 |
| `Code/UI/PreviewView.cs` | `VACCWindow.Preview.cs` の DrawPreview（PreviewJob<T> 使用） |
| `Code/UI/DetailPreviewView.cs` | `VACCWindow.DetailPreview.cs` |
| `Code/UI/ExportView.cs` | `VACCWindow.Export.cs` |
| `Code/UI/PresetsView.cs` | `VACCWindow.Presets.cs` |

旧 partial ファイルを削除:
- `Code/VACCWindow.Mask.cs`
- `Code/VACCWindow.Preview.cs`
- `Code/VACCWindow.DetailPreview.cs`
- `Code/VACCWindow.Processing.cs`（Phase 3 で移管済みなら削除済み）
- `Code/VACCWindow.Export.cs`
- `Code/VACCWindow.Presets.cs`

コミット（ファイルごと）: `refactor(ui): {Mask|Preview|Export|Presets}ViewをUI/サブクラスに移管`

### 4-6. Undo 統合（項目 2 — `VACCSessionState` + マスク）

EditorWindow が保持する `_session`（ゾーン設定 + 処理パラメータ + `MaskState`）全体を Unity 標準 Undo に統合する。初期実装では `Undo.RegisterCompleteObjectUndo(this, ...)` を使うが、**100 ストローク程度の Undo メモリ消費を検証し、過大な場合はマスク専用 Undo ターゲットの分離を検討する**。

```csharp
private void OnEnable()
{
    Undo.undoRedoPerformed += OnUndoRedoPerformed;
    // ...
}

private void OnDisable()
{
    Undo.undoRedoPerformed -= OnUndoRedoPerformed;
}

private void OnUndoRedoPerformed()
{
    // _session.maskState の RLE 文字列が Undo で書き戻されたので、
    // bool[] バッファを次フレームで再構築する。
    _maskBuffersDirty = true;
    maskDirty = true;
    previewDirty = true;
    Repaint();
}
```

**マスクペイント時の Undo 登録** (Phase 4 のマスク再設計と連動):

```csharp
private void BeginMaskStroke()
{
    if (_maskStrokeStarted) return;
    // EditorWindow を Undo 対象として登録（_session 全体の差分が記録される）
    Undo.RegisterCompleteObjectUndo(this, "Paint Mask Stroke");
    _maskStrokeStarted = true;
}

private void EndMaskStroke()
{
    if (!_maskStrokeStarted) return;
    // ストローク終了時に bool[] バッファを RLE 文字列に encode して _session.maskState に書き戻す
    // (Undo は BeginMaskStroke の RegisterCompleteObjectUndo で既に登録済み)
    SyncMaskBuffersToState();
    _maskStrokeStarted = false;
}
```

**ゾーン削除時のマスク連動**: ゾーンが削除されたら `_session.maskState.zones` の対応エントリも削除する。`_session` は EditorWindow の単一所有状態なので、ゾーンリストとマスク状態は同じ Undo ステップで記録できる。

> **`EditorUtility.SetDirty(this)` を呼ばない**: `EditorWindow` はディスクアセットを持たないため `SetDirty` の保存先がなく、Unity Undo に乗せる目的としては `Undo.RegisterCompleteObjectUndo` だけで十分。誤解を招くので呼び出さない。

**まとめ**:
- `SerializedObject.ApplyModifiedProperties()` 戻り値で `previewDirty` を 1 箇所判定（項目 19 完全解消）
- `_undoMaskHistory` / `HandleGlobalKeyboardShortcuts` の独自 Ctrl+Z を **撤去**
- マスクは `_session.maskState`（RLE 文字列）として保持し、`_session` 全体を通じて Unity Undo に乗せる

コミット: `feat(ui): VACCSessionStateをUnityUndoに統合し独自Ctrl+Z実装を撤去`

**Phase 4 検証:**
- Ctrl+Z / Ctrl+Y でゾーン設定（追加・削除・色・閾値等）が正しく戻る / 進むことを確認
- Ctrl+Z / Ctrl+Y でマスクペイント1ストロークが正しく戻る / 進むことを確認（プレビュー領域の内外を問わず動作）
- ゾーン追加→マスクペイント→ゾーン削除のような混在操作で Undo の順序・対応関係が破綻しないことを確認
- **Ctrl+Z 連打テスト**: 「ゾーン色変更 A → ペイント 1 ストローク B → ゾーン色変更 C」を順に行ったあと、Ctrl+Z を 3 回連打して `C → B → A` の順で 1 ステップずつ巻き戻ることを確認する（`RegisterCompleteObjectUndo` と `ApplyModifiedProperties()` の二経路が同じ Undo スタックに 1 ステップずつ正しく積まれていることの検証）
- プリセット保存・読み込みが正常に動作することを確認（マスクが `MaskState` 構造に変わってもプリセット側のフォーマット互換が保たれること。必要なら Phase 6-2 のマイグレーションと統合）
- `dev_safe/Tests/` のテストは本リファクタ後に再設計予定のため、Phase 4 は UI 動作の手動検証で確認

---

## Phase 5 — マスクオーバーレイ最適化（項目 7）

### 5-1. オーバーレイ生成解像度をプレビュー表示サイズに変更

対象: `Code/UI/MaskPaintView.cs`（Phase 4 後）の `RebuildMaskOverlay` / `RebuildZoneMaskOverlay`

現状は `maskWidth × maskHeight`（最大4096²）→ プレビュー表示サイズ（≤512²）に変更。  
4096テクスチャで **64倍** 軽量化。

ただし **詳細プレビュー用オーバーレイは別扱い** とし、`DetailPreviewView` では低解像度オーバーレイを使い回さない。拡大表示時のジャギーや滲みを防ぐため、詳細表示領域に対応する別オーバーレイを持つ。

コミット: `perf(mask): マスクオーバーレイ解像度をプレビュー表示サイズに最適化`

### 5-2. マスクオーバーレイ生成をバックグラウンドへ

`PreviewJob<T>` を使い、ペイント後の `RebuildMaskOverlay` をバックグラウンドで実行。  
`SetPixels32 + Apply` のみメインスレッドで呼ぶ。

バックグラウンドへ渡すのは **`bool[]` バッファのスナップショット** とし、メインスレッドが編集中の配列をそのまま読ませない。データレースを避けるため、ジョブ開始時点で `Array.Copy` した内容だけをワーカーに渡す。

コミット: `perf(mask): RebuildMaskOverlayをバックグラウンドスレッドに移行`

### 5-3. `BuildDiffTexture` / `BuildDetailDiffTexture` をバックグラウンドへ

対象:
- `Code/UI/PreviewView.cs` の `BuildDiffTexture`（旧 `VACCWindow.Preview.cs:728-754`）
- `Code/UI/DetailPreviewView.cs` の `BuildDetailDiffTexture`（旧 `VACCWindow.DetailPreview.cs:209-234`）

コミット: `perf(ui): BuildDiffTextureをPreviewJob経由でバックグラウンドに移行`

### 5-4. パフォーマンス計測

4096×4096 テクスチャでペイント中のフレームレートを Before/After で計測し、改善を記録。

**Phase 5 検証:**
- ペイント中のフレームレート改善を確認
- Diff モード ON 時のプレビュー更新がスムーズになることを確認

---

## Phase 6 — マスク永続化（項目 4 / 5）

### 6-1. `Code/Infra/MaskFileStore.cs` を新規作成

**保存先**: `<プロジェクトルート>/UserSettings/VACC/MaskCache/<テクスチャGUID>.vacc-mask.json`

`UserSettings/` は Unity 2020.1 以降で導入されたユーザー個人設定の標準的な置き場で、`.gitignore` のテンプレートでも除外されている慣例的なフォルダ。マスクは個人作業データのため git に含めず、かつ Unity の `Library/` のように Editor 操作で消えないこの場所が適切。

GUID ベースのため、テクスチャを rename/move しても**自動追従**される（AssetPostprocessor 不要）。

```csharp
internal static class MaskFileStore
{
    // <Project>/UserSettings/VACC/MaskCache
    private static string CacheDir =>
        Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "UserSettings", "VACC", "MaskCache"));

    private static string MaskFilePath(string texturePath)
    {
        var guid = AssetDatabase.AssetPathToGUID(texturePath);
        if (string.IsNullOrEmpty(guid)) return null;
        return Path.Combine(CacheDir, $"{guid}.vacc-mask.json");
    }

    public static void SaveMask(string texturePath, MaskState state)
    {
        var path = MaskFilePath(texturePath);
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(path, JsonUtility.ToJson(state));
    }

    public static MaskState LoadMask(string texturePath)
    {
        var path = MaskFilePath(texturePath);
        if (string.IsNullOrEmpty(path)) return null;
        if (!File.Exists(path)) return null;
        try { return JsonUtility.FromJson<MaskState>(File.ReadAllText(path)); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[VACC] Mask load failed: {ex.Message}");
            return null;
        }
    }

    public static void DeleteMask(string texturePath)
    {
        var path = MaskFilePath(texturePath);
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) File.Delete(path);
    }

    public static void DeleteMaskByGuid(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        var path = Path.Combine(CacheDir, $"{guid}.vacc-mask.json");
        if (File.Exists(path)) File.Delete(path);
    }
}
```

`AssetPathToGUID` が空文字を返す対象については保存をスキップし、`<empty>.vacc-mask.json` の衝突を作らない。

`MaskState` は Phase 4 で導入した RLE エンコード文字列を保持する `[Serializable]` クラス（`commonMaskBase64` + `List<MaskZoneEntry>`）。`JsonUtility` でそのままシリアライズできる。

> **Phase 4 と Phase 6 の関係**: Phase 4 で **データ構造**（`bool[] + Dictionary` → `MaskState` + RLE 文字列）を再設計し、`MaskState` を `[SerializeField]` にすることで Unity Undo に乗せる。Phase 6 ではその `MaskState` の **永続化先**（SessionState → UserSettings 配下のファイル）を切り替える。Phase 4 完了時点ではマスクは Unity Undo に乗っているが、Editor 再起動するとまだ消える状態（SessionState のため）。Phase 6 完了で再起動後も保持されるようになる。

コミット: `feat(mask): MaskFileStoreを実装しUserSettings/VACC/MaskCache配下に永続化`

### 6-2. SessionState 保存/読込を `MaskFileStore` に置換

対象: `Code/UI/MaskPaintView.cs`（Phase 4 後）の `SaveMaskToSession` / `RestoreMaskFromSession`

- `SaveMaskToSession` → `MaskFileStore.SaveMask(texturePath, _session.maskState)`
- `RestoreMaskFromSession` → `MaskFileStore.LoadMask(texturePath)` で `_session.maskState` を取得し、`bool[]` バッファを再構築

**マイグレーション**: 旧 SessionState キー（`VACC_MaskIndex_<path>` / `VACC_Mask_<path>:<targetKey>` / `VACC_Mask_<path>` レガシー）を初回 `LoadMask` 時に検出したら、`MaskState` に変換 → `MaskFileStore.SaveMask` で書き出し → 旧 SessionState キーを `EraseString` で削除する。一度きりの自動マイグレーションパスを `Code/UI/MaskPaintView.cs` 内に実装する。

- 再コンパイル間の保持: ファイル + Unity Undo（`_session` が `[SerializeField]` であり、その中に `maskState` を保持するため domain reload を超えて保持される）の二重で安全
- Editor 再起動間の保持: ファイルから読む

コミット: `refactor(mask): SessionStateによるマスク保存をMaskFileStoreに置換`

### 6-3. `Code/Infra/VACCAssetWatcher.cs` を新規作成

テクスチャ削除時のゴミファイルクリーンアップのみ担当（rename/move は GUID ベースのため不要）。**削除前フックで GUID を確定し、加えて起動時の orphan 掃除を行う**:

```csharp
internal class VACCAssetWatcher : AssetModificationProcessor
{
    static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options)
    {
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        MaskFileStore.DeleteMaskByGuid(guid);
        return AssetDeleteResult.DidNotDelete;
    }
}
```

加えて `MaskFileStore.CleanupOrphans()` を用意し、Editor 起動時または `VACCWindow.OnEnable()` の初回で `MaskCache` を走査して、`GUIDToAssetPath(guid)` が空のファイルを削除する。Unity バージョンやセッション状態による GUID 解決挙動の差に依存しない二段構えにする。

コミット: `feat(infra): VACCAssetWatcherでテクスチャ削除時のマスクファイル自動削除を実装`

### 6-4. `PresetStore.cs` を `Code/Infra/` に移動

Phase 4 での `UI/PresetsView.cs` 移管時に `Infra/PresetStore.cs` に分離済みであれば整理のみ。

**Phase 6 検証:**
- Unity を再起動してもマスクが保持されることを確認（SessionState 時代は消えていた）
- テクスチャを rename/move した後もマスクが正常に読み込まれることを確認（GUID ベースのため自動）
- テクスチャ削除後、`UserSettings/VACC/MaskCache/` に残骸が残らないことを確認
- `UserSettings/VACC/MaskCache/` が git に含まれないことを確認（`.gitignore` で除外されている / または明示的に除外する）

---

## 修正対象ファイル（完全一覧）

### 削除（Phase 3〜4）

| ファイル | 削除タイミング |
|---------|--------------|
| `Code/VACCWindow.Processing.cs` | Phase 3（PixelProcessor に移管後） |
| `Code/VACCWindow.Mask.cs` | Phase 4（MaskPaintView に移管後） |
| `Code/VACCWindow.Preview.cs` | Phase 4（PreviewView に移管後） |
| `Code/VACCWindow.DetailPreview.cs` | Phase 4（DetailPreviewView に移管後） |
| `Code/VACCWindow.Export.cs` | Phase 4（ExportView に移管後） |
| `Code/VACCWindow.Presets.cs` | Phase 4（PresetsView に移管後） |

### 新規作成

| ファイル | Phase |
|---------|-------|
| `Code/VACCConsts.cs` | 1 |
| `Code/VACCColors.cs` | 1 |
| `Code/Core/TextureSlot.cs` | 2 |
| `Code/Core/PixelProcessor.cs` | 3 |
| `Code/Core/PreviewJob.cs` | 3 |
| `Code/Core/VACCSessionState.cs` | 4 |
| `Code/Core/ColorZoneDrawer.cs` | 4 |
| `Code/Core/MaskState.cs` | 4（`MaskState` + `MaskZoneEntry` の `[Serializable]` 定義） |
| `Code/UI/MaskPaintView.cs` | 4 |
| `Code/UI/PreviewView.cs` | 4 |
| `Code/UI/DetailPreviewView.cs` | 4 |
| `Code/UI/ExportView.cs` | 4 |
| `Code/UI/PresetsView.cs` | 4 |
| `Code/Infra/MaskFileStore.cs` | 6 |
| `Code/Infra/PresetStore.cs` | 4→6 |
| `Code/Infra/VACCAssetWatcher.cs` | 6 |

### 大幅修正

| ファイル | 変更内容 |
|---------|---------|
| `Code/VACCWindow.cs` | Phase 1（定数整理）→ Phase 4（薄いホストへ全面変更） |
| `Code/ColorZone.cs` | Phase 1: `Clone()` を `MemberwiseClone()` に変更 |
| `Code/Localization.cs` | Phase 1: `CurrentLanguage` を `EditorPrefs` に永続化 |

---

## ブランチ・コミット戦略

- **ブランチ**: Phase 0〜6 を通じて `feature/refactor-all` で行う
- **コミット形式**: Conventional Commits（`refactor(ui):`, `perf(mask):` 等）
- **粒度**: 1 コミット = 1 論理変更（1-1〜1-11 は各々独立コミット）
- **テスト**: `dev_safe/Tests/` は本リファクタ後に別途再設計する前提のため、各フェーズの検証は Unity 上での手動動作確認で代替する
- **push**: `git push` はユーザー確認後のみ実行（エージェントが勝手には実行しない）

---

## 決定事項まとめ

| 項目 | 決定内容 |
|------|---------|
| 初期値の定義場所 | コード内。現行 `VACCWindow` の `[SerializeField]` 初期化子を `Code/Core/VACCSessionState.cs` に移管 |
| 初期値の編集方針 | ユーザー編集不可。プリセット未読込時の基準値としてのみ使い、再現性はプリセットで確保 |
| マスクの保存先 | `<Project>/UserSettings/VACC/MaskCache/<GUID>.vacc-mask.json`（git 非追跡） |
| マスクのデータ構造 | Phase 4 で再設計: `bool[] + Dictionary` → `MaskState`（RLE 文字列 + `List<MaskZoneEntry>`）。`bool[]` は実行時バッファとして残す |
| Unity Undo 統合範囲 | `VACCSessionState`（ゾーン + 処理パラメータ + `MaskState`）全体。独自 `_undoMaskHistory` / 独自 Ctrl+Z は撤去 |
| rename/move 追従 | GUID ベースのため自動追従（AssetPostprocessor は delete 時のみ） |
| asmdef | 既存の `com.yukkuri-aoba.vrc-avatar-color-changer.Editor.asmdef` を継続使用 |
| God Class 分解 | partial class を完全廃止し UI / Core / Infra に全面移行 |
| SerializedObject | `EditorWindow` が保持する `VACCSessionState` を `SerializedObject(this)` 経由で編集 |
| ブランチ戦略 | `feature/refactor-all` の単一ブランチ |
| `dev_safe/Tests/` | 本リファクタの後で別途設計し直す前提。フェーズ完了時の自動検証対象とはしない |

**スコープ外:**
- アルゴリズム改善（IoU スコア向上）
- 新機能追加
- `dev_safe/` 配下のテスト再設計（別タスクとして実施）

> あくまでリファクタであり、アルゴリズムやUIの変更は行わない。

---

## 参考

- 問題の詳細: [`docs/refactoring-unity-editor-antipatterns.md`](refactoring-unity-editor-antipatterns.md)
- 既存テスト実行（参考・本リファクタ後に再設計予定）: `cd dev_safe/Tests && dotnet test VACCTests.csproj -c Release`
