# AvatarColorChanger リファクタリング計画

**対象**: `refactoring-unity-editor-antipatterns.md` 記載の20項目アンチパターン  
**方針**: 6フェーズで段階的に解消。フェーズ完了ごとに Unity 上で手動検証してからコミット（`dev_safe/Tests/` は本リファクタ後に別途設計し直す前提）  
**ブランチ**: `main` から `feature/refactor-all` を切って全フェーズを進める

---

## 目標アーキテクチャ

```
Code/
├── VACCWindow.cs              (EditorWindow 本体 — 薄いホスト)
├── VACCConsts.cs              (MenuPath / WindowTitle / Layout 定数 / IndentScope)
├── VACCColors.cs              (Light/Dark Skin 対応色定数)
├── UI/
│   ├── MaskPaintView.cs       (DrawMaskSection + ペイント入力)
│   ├── PreviewView.cs         (DrawPreview — PreviewJob<T> 使用)
│   ├── DetailPreviewView.cs   (詳細プレビュー)
│   ├── ExportView.cs          (DrawExportSection / DrawBatchSection)
│   └── PresetsView.cs         (DrawPresetsSection)
├── Core/
│   ├── VACCSettings.cs        (ScriptableObject — ゾーン定義のみ。デフォルトゾーンリストとして機能)
│   ├── VACCSettingsEditor.cs  (CustomEditor — DrawZoneList)
│   ├── ColorZoneDrawer.cs     (CustomPropertyDrawer<ColorZone>)
│   ├── PixelProcessor.cs      (静的処理メソッド群 — Editor 非依存・テスト可能)
│   ├── PreviewJob.cs          (PreviewJob<T> 非同期ヘルパー)
│   └── TextureSlot.cs         (Texture2D ライフサイクル管理)
└── Infra/
    ├── MaskFileStore.cs       (Assets/VACC/MaskCache/<GUID>.vacc-mask.json I/O)
    ├── PresetStore.cs         (プリセット JSON I/O)
    └── VACCAssetWatcher.cs    (AssetPostprocessor — delete 時クリーンアップのみ)
```

### 設定ファイルとマスクの保存場所

| データ | 保存先 | 備考 |
|--------|--------|------|
| プリセット | 既存の場所 | ゾーン設定（各ゾーンのアドバンスフィールド含む）+ 処理パラメータ（`edgeFeather`, `antiAliasCleanup`, `useDecontamination`, `decontaminationRadius`, `advancedMode`, `holeFillPasses`, `holeFillMinNeighbors`, `relaxedSatMin`, `relaxedSatRamp`）+ マスク（任意・フルレス RLE）を一括保存・復元。プロジェクト単位・git 管理 |
| デフォルト処理パラメータ | `Assets/VACC/VACCSettings.asset` | プリセット未指定時のデフォルト値として機能。ゾーン定義のみ保持（処理パラメータはプリセット側で管理） |
| マスクデータ（フルレス） | `<Project>/UserSettings/VACC/MaskCache/<テクスチャGUID>.vacc-mask.json` | git 非追跡フォルダ（個人作業データ）。GUID ベースのため rename/move 自動追従 |

**マスクのデータ構造を再設計**: `bool[] + Dictionary<string, bool[]>` → `MaskState`（RLE エンコード文字列 + `List<ZoneMaskEntry>`）に変更。これにより Unity の SerializedObject でシリアライズ可能になり、`[SerializeField]` で持つことで Unity 標準 Undo に乗る。`bool[]` は実行時バッファ（`[NonSerialized]`）として保持し、ペイント時のみ更新→ストローク終了時に RLE 文字列へ encode。独自 Ctrl+Z / `_undoMaskHistory` は撤去。

**プリセット内マスクはフルレスで保存**: 解像度を落とすとペイント境界の精度が失われるため、プリセット JSON への同梱時もフルレスのまま RLE エンコードして保存する（`presetIncludeMasks` オプトイン）。RLE の特性上、典型的な使用（疎な除外領域）では 4096² テクスチャでも数KB〜数十KB に収まる。MaskCache にも同じフルレスマスクが保持される。

### asmdef について

既存の `com.yukkuri-aoba.vrc-avatar-color-changer.Editor.asmdef` をそのまま使い続ける。  
`Code/UI/` / `Code/Core/` / `Code/Infra/` に新規ファイルを置いても `Code/` 配下に含まれるため自動的に同じアセンブリに包含される（追加作業不要）。

---

## フェーズ一覧

| Phase | 対象項目 | 難度 | 推定コミット数 |
|-------|---------|------|--------------|
| 1 | 3, 9, 10, 12, 13, 14, 15, 16, 17, 18, 19, 20 | 低 | ~12 |
| 2 | 8 | 中 | ~5 |
| 3 | 6(partial), 11 | 中 | ~5 |
| 4 | 1, 2, 6(全体) + マスクのデータ構造再設計 | 高 | ~14 |
| 5 | 7 | 中 | ~4 |
| 6 | 4, 5 | 中 | ~4 |

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
    foreach (var tex in batchTextures) { /* ... */ }
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
    private static LanguageMode? _cached;

    public static LanguageMode CurrentLanguage
    {
        get => _cached ??= (LanguageMode)EditorPrefs.GetInt(PrefsKey, (int)LanguageMode.Auto);
        set
        {
            _cached = value;
            EditorPrefs.SetInt(PrefsKey, (int)value);
        }
    }
}
```

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

### 1-4. 項目12: `EnableReadWrite` に `Undo.RecordObject` を追加

対象: `Code/VACCWindow.cs:494-504`

```csharp
Undo.RecordObject(importer, "Enable Read/Write");
importer.isReadable = true;
importer.SaveAndReimport();
```

コミット: `fix(ui): ReadWrite有効化をUndoスタックに登録`

### 1-5. 項目13: 例外握り潰しを修正

- `Code/VACCWindow.cs` の `catch { }` → `catch (ObjectDisposedException) { }`
- `Code/VACCWindow.Mask.cs` のデコード失敗 catch に `Debug.LogWarning($"[VACC] Mask decode failed: {ex.Message}")` を追加

コミット: `fix: 例外の握り潰しを修正しデバッグログを追加`

### 1-6. 項目14: `ProjectPresetFolder` を static キャッシュ化

対象: `Code/VACCWindow.Presets.cs:20-44`

```csharp
private static string _cachedProjectFolder;
private static string ProjectPresetFolder
{
    get
    {
        if (_cachedProjectFolder != null) return _cachedProjectFolder;
        // ... 既存ロジック ...
        return _cachedProjectFolder = result;
    }
}
```

コミット: `perf(preset): ProjectPresetFolderのAssetDatabase検索結果をキャッシュ`

### 1-7. 項目15: `[MenuItem]` に priority 追加

対象: `Code/VACCWindow.cs:57`

```csharp
[MenuItem("Tools/yukkuri-aoba/VRC AvatarColorChanger", priority = 100)]
```

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
    public const string WindowTitle = "VRC AvatarColorChanger";

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

`VACCWindow.cs` 内の散在するリテラルと `Localization.WindowTitle` の重複定義を置換。

コミット: `refactor(ui): マジックナンバーとMenuPath/WindowTitleをVACCConstsに集約`

### 1-10. 項目18: `EditorGUI.indentLevel` → `IndentScope` struct

`Code/VACCConsts.cs`（または同ファイル）に追加:

```csharp
internal struct IndentScope : System.IDisposable
{
    private readonly int _delta;
    public IndentScope(int delta = 1) { _delta = delta; EditorGUI.indentLevel += delta; }
    public void Dispose() { EditorGUI.indentLevel -= _delta; }
}

// 使い方
using (new IndentScope())
{
    holeFillPasses = EditorGUILayout.IntSlider(...);
}
```

コミット: `refactor(ui): indentLevelの手動管理をIndentScopeに置換`

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
        ScheduleDestroy(tex);
        tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = filter };
    }

    public static void Release(ref Texture2D tex)
    {
        ScheduleDestroy(tex);
        tex = null;
    }

    private static void ScheduleDestroy(Texture2D t)
    {
        if (t == null) return;
        EditorApplication.delayCall += () => { if (t != null) Object.DestroyImmediate(t); };
    }
}
```

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
internal sealed class PreviewJob<T>
{
    private CancellationTokenSource _cts;
    private int _generation;

    public void Schedule(Func<CancellationToken, T> work, Action<T> apply)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int myGen = ++_generation;
        Task.Run(() =>
        {
            try
            {
                var result = work(token);
                token.ThrowIfCancellationRequested();
                EditorApplication.delayCall += () =>
                {
                    if (myGen == _generation) apply(result);
                };
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Cancel() => _cts?.Cancel();
}
```

コミット: `refactor(ui): PreviewJobヘルパーを追加`

### 3-3〜3-4. Preview / DetailPreview の非同期部分を `PreviewJob<T>` で書き直し

- `VACCWindow.Preview.cs` の volatile フラグ群（`_previewGenerating`, `_asyncGeneration` 等）を `PreviewJob<T>` 内部に隠蔽
- `VACCWindow.DetailPreview.cs` の重複 Async パターンも同様に統一

コミット: `refactor(ui): PreviewJobヘルパーでAsyncパターンを統一`

**Phase 3 検証:**
- PixelProcessor の static メソッド群が Unity 上で従来通り動作することを確認（手動でゾーン色変更→プレビュー結果の差分目視）
- プレビュー生成・詳細プレビューが正常に動作することを確認

---

## Phase 4 — ScriptableObject 化 + Undo 統合（項目 1 / 2 / 6 全体）

最大規模フェーズ。**partial class を完全廃止**し UI / Core / Infra 構造に移行する。  
あわせて **マスクのデータ構造を再設計** し、Unity 標準 Undo に統合する（ゾーン設定 + 処理パラメータ + マスクすべてが Ctrl+Z / Ctrl+Y で戻る/進むようになる）。

### マスクのデータ構造再設計

現状の `bool[] exclusionMask` / `Dictionary<string, bool[]> zoneMasks` は Unity の SerializedObject でそのままシリアライズできない（`Dictionary` はシリアライズ非対応）。また `bool[]` を直接 `[SerializeField]` にすると 4096² で 16MB のスナップショットになり Undo コスト過大。

→ 既存の **RLE エンコード文字列**（`EncodeMask` / `DecodeMask`）を Unity の永続表現として採用し、`bool[]` は実行時専用バッファに格下げする:

```csharp
namespace VRCAvatarColorChanger
{
    [System.Serializable]
    public class ZoneMaskEntry
    {
        public string zoneId;
        public string encodedMask; // EncodeMask 結果（RLE + Base64）
    }

    [System.Serializable]
    public class MaskState
    {
        public int width;
        public int height;
        public string commonEncoded;                // 共通マスク（空文字 = 未設定）
        public List<ZoneMaskEntry> zones = new();   // ゾーン別マスク
    }
}
```

`VACCWindow` 側のフィールドを以下のように再構成する:

```csharp
// 永続表現（Unity Undo 対象）
[SerializeField] private MaskState _maskState = new();

// 実行時バッファ（Undo 非対象）
[System.NonSerialized] private bool[] exclusionMask;
[System.NonSerialized] private Dictionary<string, bool[]> zoneMasks = new();
[System.NonSerialized] private bool _maskBuffersDirty; // _maskState からの再構築待ち
```

**同期ルール**:
- 編集（ペイント等）: `bool[]` バッファを直接更新 → ストローク終了時に `EncodeMask` で `_maskState` に書き戻す
- Undo / Redo / 起動 / sourceTexture 切替: `_maskState` の RLE 文字列を `DecodeMask` で `bool[]` バッファに展開
- `OnUndoRedoPerformed` が呼ばれたら `_maskBuffersDirty = true` にして次フレームでバッファを再構築

**スナップショットコスト（Undo 用）**: RLE + Base64 文字列なので、典型的な使用（疎な除外領域）で数 KB〜数十 KB／ストローク。ワーストケース（高エントロピー）でも数 MB 程度に収まり、`bool[]` 直書きの 16MB に比べ十分許容範囲。

**プリセットへのマスク同梱コスト**: 解像度を落とすとペイント境界の精度が失われるため、プリセット JSON にもフルレスのまま RLE エンコードして同梱する。RLE の特性上、典型的な使用（疎な除外領域）では 4096² テクスチャでも数KB〜数十KB に収まり問題ない。

**独自 `_undoMaskHistory` / `HandleGlobalKeyboardShortcuts` の Ctrl+Z は撤去** する（Unity 標準 Undo に置き換わるため）。

### 4-1. `Code/Core/VACCSettings.cs` を新規作成（ScriptableObject）

ゾーン定義のみを保持。処理パラメータはプリセット側で管理：

```csharp
[CreateAssetMenu(fileName = "VACCSettings", menuName = "VACC/Settings")]
public class VACCSettings : ScriptableObject
{
    public List<ColorZone> zones = new();
}
```

VACCWindow の `OnEnable` で `Assets/VACC/VACCSettings.asset` を自動作成/検索し、アクティブプリセットが未指定の場合のデフォルトゾーンリストとして機能します。

> **処理パラメータについて**: `edgeFeather`, `antiAliasCleanup`, `useDecontamination`, `decontaminationRadius`, `advancedMode`, `holeFillPasses`, `holeFillMinNeighbors`, `relaxedSatMin`, `relaxedSatRamp` はプリセットに含まれるため、VACCSettings には保持しません。プリセット未指定時は、`VACCWindow` 内で定義したデフォルト値を使用します。

VACCWindow の `OnEnable` で `Assets/VACC/VACCSettings.asset` を自動作成/検索:

```csharp
private void OnEnable()
{
    _settings = AssetDatabase.LoadAssetAtPath<VACCSettings>("Assets/VACC/VACCSettings.asset");
    if (_settings == null)
    {
        _settings = CreateInstance<VACCSettings>();
        AssetDatabase.CreateAsset(_settings, "Assets/VACC/VACCSettings.asset");
    }
    _settingsEditor = Editor.CreateEditor(_settings);
    // ...
}
```

コミット: `refactor(ui): VACCSettingsをScriptableObjectに切り出し`

### 4-2. `Code/Core/ColorZoneDrawer.cs` を新規作成

```csharp
[CustomPropertyDrawer(typeof(ColorZone))]
public class ColorZoneDrawer : PropertyDrawer
{
    // ColorZone 内のフィールド描画ロジックをここに移管
    // [Range] / [Tooltip] 属性が自動的に反映される
}
```

`DrawZoneList` の 170 行のうち、ColorZone 単体のフィールド描画部分（約 100〜120 行）が PropertyDrawer に吸収される。  
ただし **以下は外側のコレクションロジックとして残る**:
- ゾーン削除（×）ボタン、ゾーン追加ボタン
- ゾーンマスク編集ボタン（`activeMaskTarget` と連動するためゾーン単独の描画では完結しない）
- `advancedMode` 連動の表示分岐の一部

最終的に `DrawZoneList` は **40〜50 行程度** に縮退する見込み。

コミット: `refactor(ui): ColorZoneDrawerを実装しDrawZoneListを簡略化`

### 4-3. `Code/Core/VACCSettingsEditor.cs` を新規作成

```csharp
[CustomEditor(typeof(VACCSettings))]
public class VACCSettingsEditor : Editor
{
    // DrawZoneList の描画ロジックをここに移管
}
```

コミット: `refactor(ui): VACCSettingsEditorを実装しDrawZoneListを移管`

### 4-4. VACCWindow を薄いホストに変更

`OnGUI()` は以下のみを担当:
- `HandleGlobalKeyboardShortcuts()` の Undo 関連を除いた部分
- `settingsEditor.OnInspectorGUI()` 呼び出し（ゾーン定義のみ）
- アクティブプリセット選択 UI
- 処理パラメータ表示・入力（プリセット未指定時のデフォルト値用）
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

### 4-6. Undo 統合（項目 2 — VACCSettings + マスク）

`VACCSettings`（ゾーン設定 + 処理パラメータ）と マスク（`MaskState`）の両方を Unity 標準 Undo に統合する。

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
    // VACCSettings 側はインスペクタが自動再描画してくれる。
    // マスク側は _maskState の RLE 文字列が Undo で書き戻されたので、
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
    // EditorWindow を Undo 対象として登録（_maskState の RLE 文字列ごと差分が記録される）
    Undo.RegisterCompleteObjectUndo(this, "Paint Mask Stroke");
    _maskStrokeStarted = true;
}

private void EndMaskStroke()
{
    if (!_maskStrokeStarted) return;
    // ストローク終了時に bool[] バッファを RLE 文字列に encode して _maskState に書き戻す
    SyncMaskBuffersToState();
    EditorUtility.SetDirty(this);
    _maskStrokeStarted = false;
}
```

**ゾーン削除時のマスク連動**: ゾーンが削除されたら `_maskState.zones` の対応エントリも削除する必要がある。これは VACCSettings 側の Undo とは別レコードになるため、ゾーン削除を `Undo.IncrementCurrentGroup()` / `Undo.CollapseUndoOperations()` で囲み、両方の変更を 1 つの Undo ステップにまとめる。

**まとめ**:
- `SerializedObject.ApplyModifiedProperties()` 戻り値で `previewDirty` を 1 箇所判定（項目 19 完全解消）
- `_undoMaskHistory` / `HandleGlobalKeyboardShortcuts` の独自 Ctrl+Z を **撤去**
- マスクは `_maskState`（RLE 文字列）を `[SerializeField]` で持つことで Unity Undo に乗る

コミット: `feat(ui): UnityUndoシステムに統合し独自Ctrl+Z実装を撤去`

**Phase 4 検証:**
- Ctrl+Z / Ctrl+Y でゾーン設定（追加・削除・色・閾値等）が正しく戻る / 進むことを確認
- Ctrl+Z / Ctrl+Y でマスクペイント1ストロークが正しく戻る / 進むことを確認（プレビュー領域の内外を問わず動作）
- ゾーン追加→マスクペイント→ゾーン削除のような混在操作で Undo の順序・対応関係が破綻しないことを確認
- プリセット保存・読み込みが正常に動作することを確認（マスクが `MaskState` 構造に変わってもプリセット側のフォーマット互換が保たれること。必要なら Phase 6-2 のマイグレーションと統合）
- `dev_safe/Tests/` のテストは本リファクタ後に再設計予定のため、Phase 4 は UI 動作の手動検証で確認

---

## Phase 5 — マスクオーバーレイ最適化（項目 7）

### 5-1. オーバーレイ生成解像度をプレビュー表示サイズに変更

対象: `Code/UI/MaskPaintView.cs`（Phase 4 後）の `RebuildMaskOverlay` / `RebuildZoneMaskOverlay`

現状は `maskWidth × maskHeight`（最大4096²）→ プレビュー表示サイズ（≤512²）に変更。  
4096テクスチャで **64倍** 軽量化。

コミット: `perf(mask): マスクオーバーレイ解像度をプレビュー表示サイズに最適化`

### 5-2. マスクオーバーレイ生成をバックグラウンドへ

`PreviewJob<T>` を使い、ペイント後の `RebuildMaskOverlay` をバックグラウンドで実行。  
`SetPixels32 + Apply` のみメインスレッドで呼ぶ。

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
        return Path.Combine(CacheDir, $"{guid}.vacc-mask.json");
    }

    public static void SaveMask(string texturePath, MaskState state)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(MaskFilePath(texturePath), JsonUtility.ToJson(state));
    }

    public static MaskState LoadMask(string texturePath)
    {
        var path = MaskFilePath(texturePath);
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
        if (File.Exists(path)) File.Delete(path);
    }
}
```

`MaskState` は Phase 4 で導入した RLE エンコード文字列を保持する `[Serializable]` クラス（`commonEncoded` + `List<ZoneMaskEntry>`）。`JsonUtility` でそのままシリアライズできる。

> **Phase 4 と Phase 6 の関係**: Phase 4 で **データ構造**（`bool[] + Dictionary` → `MaskState` + RLE 文字列）を再設計し、`MaskState` を `[SerializeField]` にすることで Unity Undo に乗せる。Phase 6 ではその `MaskState` の **永続化先**（SessionState → UserSettings 配下のファイル）を切り替える。Phase 4 完了時点ではマスクは Unity Undo に乗っているが、Editor 再起動するとまだ消える状態（SessionState のため）。Phase 6 完了で再起動後も保持されるようになる。

コミット: `feat(mask): MaskFileStoreを実装しUserSettings/VACC/MaskCache配下に永続化`

### 6-2. SessionState 保存/読込を `MaskFileStore` に置換

対象: `Code/UI/MaskPaintView.cs`（Phase 4 後）の `SaveMaskToSession` / `RestoreMaskFromSession`

- `SaveMaskToSession` → `MaskFileStore.SaveMask(texturePath, _maskState)`
- `RestoreMaskFromSession` → `MaskFileStore.LoadMask(texturePath)` で `_maskState` を取得し、`bool[]` バッファを再構築

**マイグレーション**: 旧 SessionState キー（`VACC_MaskIndex_<path>` / `VACC_Mask_<path>:<targetKey>` / `VACC_Mask_<path>` レガシー）を初回 `LoadMask` 時に検出したら、`MaskState` に変換 → `MaskFileStore.SaveMask` で書き出し → 旧 SessionState キーを `EraseString` で削除する。一度きりの自動マイグレーションパスを `Code/UI/MaskPaintView.cs` 内に実装する。

- 再コンパイル間の保持: ファイル + Unity Undo（`_maskState` が `[SerializeField]` のため domain reload を超えて保持される）の二重で安全
- Editor 再起動間の保持: ファイルから読む

コミット: `refactor(mask): SessionStateによるマスク保存をMaskFileStoreに置換`

### 6-3. `Code/Infra/VACCAssetWatcher.cs` を新規作成

テクスチャ削除時のゴミファイルクリーンアップのみ担当（rename/move は GUID ベースのため不要）:

```csharp
internal class VACCAssetWatcher : AssetPostprocessor
{
    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted,
        string[] movedTo, string[] movedFromPath)
    {
        foreach (var d in deleted)
            MaskFileStore.DeleteMask(d);
    }
}
```

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
| `Code/Core/VACCSettings.cs` | 4 |
| `Code/Core/VACCSettingsEditor.cs` | 4 |
| `Code/Core/ColorZoneDrawer.cs` | 4 |
| `Code/Core/MaskState.cs` | 4（`MaskState` + `ZoneMaskEntry` の `[Serializable]` 定義） |
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

- **ブランチ**: Phase 1〜6 を通じて`develop`で行う。(developにコミットし続ける)
- **コミット形式**: Conventional Commits（`refactor(ui):`, `perf(mask):` 等）
- **粒度**: 1 コミット = 1 論理変更（1-1〜1-11 は各々独立コミット）
- **テスト**: `dev_safe/Tests/` は本リファクタ後に別途再設計する前提のため、各フェーズの検証は Unity 上での手動動作確認で代替する
- **push**: `git push` はユーザー確認後のみ実行（エージェントが勝手には実行しない）

---

## 決定事項まとめ

| 項目 | 決定内容 |
|------|---------|
| ScriptableObject の場所 | `Assets/VACC/VACCSettings.asset`（プロジェクト単位・git 管理） |
| ScriptableObject の内容 | ゾーン定義のみ。デフォルトゾーンリストとして機能。処理パラメータはプリセット側で管理 |
| マスクの保存先 | `<Project>/UserSettings/VACC/MaskCache/<GUID>.vacc-mask.json`（git 非追跡） |
| マスクのデータ構造 | Phase 4 で再設計: `bool[] + Dictionary` → `MaskState`（RLE 文字列 + `List<ZoneMaskEntry>`）。`bool[]` は実行時バッファとして残す |
| Unity Undo 統合範囲 | `VACCSettings`（ゾーン + 処理パラメータ）と `MaskState`（マスク全種）の両方。独自 `_undoMaskHistory` / 独自 Ctrl+Z は撤去 |
| rename/move 追従 | GUID ベースのため自動追従（AssetPostprocessor は delete 時のみ） |
| asmdef | 既存の `com.yukkuri-aoba.vrc-avatar-color-changer.Editor.asmdef` を継続使用 |
| God Class 分解 | partial class を完全廃止し UI / Core / Infra に全面移行 |
| SerializedObject | 段階 B（ScriptableObject + CustomEditor） |
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
