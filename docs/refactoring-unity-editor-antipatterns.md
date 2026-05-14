# Unity Editor 拡張アンチパターン棚卸しとリファクタリング計画

**対象**: `Code/` 配下（VACCWindow とその partial 群、ColorZone、Localization、BuildHelper 等）
**目的**: Unity Editor 拡張として「望ましくない実装」を整理し、次回リファクタリングの優先度・修正方針を共有する。
**読み方**: 各項目は **問題 → 該当箇所 → なぜ悪いか → 修正方針 → 修正例** の順。重大度（Critical / Major / Minor）と推定影響範囲を併記。

---

## 0. サマリー

| # | 項目 | 重大度 | カテゴリ |
|---|------|--------|----------|
| 1 | SerializedObject / SerializedProperty パターン未使用 | Critical | アーキテクチャ |
| 2 | Unity Undo システム不使用（独自 Ctrl+Z） | Critical | UX |
| 3 | `AssetDatabase.Refresh()` の濫用 | Critical | パフォーマンス |
| 4 | `SessionState` のみのマスク永続化 | Critical | データ保全 |
| 5 | アセット rename/move 非対応（AssetPostprocessor 無し） | Critical | データ保全 |
| 6 | partial class による 4,900 行 God Class | Major | アーキテクチャ |
| 7 | OnGUI で重い同期処理（オーバーレイ／Diff／GetPixels32） | Major | パフォーマンス |
| 8 | `Texture2D` 手動管理＋`DestroyImmediate` 散在 | Major | リソース管理 |
| 9 | `Localization.CurrentLanguage` がセッション間で消える | Major | UX |
| 10 | テーマ非対応のハードコード色（Light/Dark Skin） | Major | UX |
| 11 | バックグラウンドタスクの volatile + 手動レース管理 | Major | スレッド安全 |
| 12 | `EnableReadWrite` が `Undo.RecordObject` 未使用 | Minor | UX |
| 13 | `try { ... } catch { }` による例外握り潰し | Minor | デバッグ性 |
| 14 | `ProjectPresetFolder` の `AssetDatabase.FindAssets` 毎回呼び | Minor | パフォーマンス |
| 15 | `MenuItem` に priority / shortcut が無い | Minor | UX |
| 16 | `ColorZone.Clone()` が JsonUtility round-trip | Minor | パフォーマンス |
| 17 | レイアウトのマジックナンバー散在 | Minor | 保守性 |
| 18 | `EditorGUI.indentLevel` の手動 inc/dec（例外復元なし） | Minor | 保守性 |
| 19 | `EditorGUI.BeginChangeCheck` の運用が部分的 | Minor | 保守性 |
| 20 | `MenuItem` パス・ウィンドウタイトルの一元化不足 | Minor | 保守性 |

---

## Critical

### 1. SerializedObject / SerializedProperty パターン未使用

#### 問題
すべてのフィールドを直接 `EditorGUILayout.Slider(... value, min, max)` で読み書きしている。Unity Editor 拡張の標準である「`SerializedObject` 経由で `SerializedProperty` をバインドし、`EditorGUI.PropertyField` で描画する」流儀に乗っていない。

#### 該当箇所
- [Code/VACCWindow.cs:246-417](Code/VACCWindow.cs#L246-L417) `DrawZoneList()` 全体
- [Code/VACCWindow.cs:421-477](Code/VACCWindow.cs#L421-L477) `DrawProcessingSection()`
- [Code/ColorZone.cs:38-69](Code/ColorZone.cs#L38-L69) `[Range]` 属性が定義されているが利用されていない（直値で `EditorGUILayout.Slider` を呼んでいるため）

#### なぜ悪いか
1. **Unity Undo (Ctrl+Z) が一切効かない**。スライダーを動かしても Undo スタックに乗らない。
2. **Prefab override / Multi-object editing** に乗れない（将来 ScriptableObject 化したときに無償で得られる機能を捨てている）。
3. `[Range]`, `[Tooltip]`, `[Min]`, `[Header]` などの属性が活きない。Tooltip は別途 `new GUIContent(label, tooltip)` で手書きしているのが二重コスト。
4. 値が変わったかどうかは `EditorGUI.BeginChangeCheck/EndChangeCheck` で全体判定するしかなく、「どのフィールドが」変わったかが分からない。
5. 設定の dirty mark （`EditorUtility.SetDirty`）がされず、ScriptableObject 化したときにアセットが保存されない問題が顕在化する。

#### 修正方針
**段階 A（小）**: 既存構造を維持したまま、`EditorWindow.CreateSerializedObject()` でルートを作り、各フィールドを `serializedObject.FindProperty("zones")` 等でバインドして `EditorGUI.PropertyField` に置き換える。

**段階 B（推奨）**: 設定状態を `ScriptableObject`（例: `VACCSettings`）に切り出し、`UnityEditor.Editor` を `[CustomEditor(typeof(VACCSettings))]` で書く。`EditorWindow` 側はその Editor を `Editor.CreateEditor` でホストするだけにする。さらに `ColorZone` には `[CustomPropertyDrawer]` を割り当てれば、`DrawZoneList` の 170 行の手書きが消える。

#### 修正例（段階 A）

```csharp
// VACCWindow.cs
private SerializedObject _so;

private void OnEnable()
{
    EnsureAllZoneIds();
    RestoreMaskFromSession();
    _so = new SerializedObject(this);
}

private void OnGUI()
{
    _so.Update();

    EditorGUILayout.PropertyField(_so.FindProperty(nameof(zones)), true);
    EditorGUILayout.PropertyField(_so.FindProperty(nameof(edgeFeather)));
    // ...

    if (_so.ApplyModifiedProperties())
        previewDirty = true;
}
```

`ColorZone` の `[Range]` 属性が初めて意味を持ち、Tooltip 文言は `[Tooltip("...")]` をフィールドに付けるだけで反映される。

---

### 2. Unity Undo システム不使用（独自 Ctrl+Z 実装）

#### 問題
マスク編集に対して独自の `_undoMaskHistory` を持ち、`HandleGlobalKeyboardShortcuts` で `Ctrl+Z` を直接ハンドリングしている。Unity の標準 Undo (`Undo.RecordObject`, `Undo.RegisterCompleteObjectUndo`) に乗っていない。

#### 該当箇所
- [Code/VACCWindow.Mask.cs:42-78](Code/VACCWindow.Mask.cs#L42-L78) `_undoMaskHistory`, `HandleGlobalKeyboardShortcuts`, `UndoMaskStep`
- [Code/VACCWindow.Mask.cs:436-494](Code/VACCWindow.Mask.cs#L436-L494) `PushMaskUndo` / `UndoMaskStep`

#### なぜ悪いか
1. **Unity 内の他の Undo 操作と分断される**。ユーザーがゾーンの色を変えてからマスクをペイントすると、Ctrl+Z で「マスクペイントだけ」が戻り、色変更は戻らない（あるいはその逆）という不整合が起きる。
2. ゾーンの追加・削除・カラー変更に対しては Undo が一切ない（マスクだけ独自実装）。ユーザーが誤操作しても戻せない。
3. メニューバーの Edit > Undo, Edit > Redo に Ctrl+Z が表示されない。Mac の Cmd+Z, Cmd+Shift+Z にも対応していない。
4. `editingTextField` チェックを自前で行っているが、Unity 標準 Undo はテキストフィールド内の操作（IME 含む）も適切にハンドルしてくれる。

#### 修正方針
- `EditorWindow` を `Undo.undoRedoPerformed` に購読し、Undo が発火したらマスクを再構築・`Repaint()`。
- マスク編集の開始時に `Undo.RegisterCompleteObjectUndo(this, "Paint Mask")`（EditorWindow を Undo 対象に登録）。
- ゾーン追加/削除・スライダー変更も `Undo.RecordObject(this, "...")` を呼ぶ（`SerializedObject` 経由なら自動）。
- 独自 `Ctrl+Z` ハンドラを撤去。

#### 修正例

```csharp
private void OnEnable()
{
    Undo.undoRedoPerformed += OnUndoRedoPerformed;
}
private void OnDisable()
{
    Undo.undoRedoPerformed -= OnUndoRedoPerformed;
}
private void OnUndoRedoPerformed()
{
    maskDirty = true;
    previewDirty = true;
    Repaint();
}

private void PaintAtScreenPos(Vector2 screenPos, Rect previewRect)
{
    if (!_maskStrokeStarted)
    {
        Undo.RegisterCompleteObjectUndo(this, "Paint Mask Stroke");
        _maskStrokeStarted = true;
    }
    // ...
}
```

ただし `bool[]` フィールドは `[SerializeField]` であっても巨大なのでスナップショット保存が重い → 既存の RLE エンコード結果を `string` フィールドに格納し、それを Undo 対象にする等の最適化が必要。完全移行が重い場合は、最低限「Unity 標準 Undo の発火を Repaint に繋げる」だけでも UX が改善する。

---

### 3. `AssetDatabase.Refresh()` の濫用

#### 問題
プリセットの保存・削除・PNG 出力直後に毎回 `AssetDatabase.Refresh()` を呼んでいる。Refresh はプロジェクト全体のアセットを走査・必要なら再インポートする重い処理で、大規模プロジェクトでは数秒〜十数秒 Editor が固まる。

#### 該当箇所
- [Code/VACCWindow.Export.cs:169](Code/VACCWindow.Export.cs#L169) `ApplyRecolor` 後
- [Code/VACCWindow.Export.cs:323](Code/VACCWindow.Export.cs#L323) `RunBatchApply` 後
- [Code/VACCWindow.Presets.cs:126](Code/VACCWindow.Presets.cs#L126) プリセット削除後
- [Code/VACCWindow.Presets.cs:152](Code/VACCWindow.Presets.cs#L152) プリセット保存後

#### なぜ悪いか
- 大規模プロジェクトで体感的にツールが「重い」と感じる主因になりやすい。
- 影響範囲が「保存した 1 ファイル」しかないのにプロジェクト全体を走査するのは過剰。

#### 修正方針
**特定ファイルだけインポートしたい場合**は `AssetDatabase.ImportAsset(relativePath)` を使う。

```csharp
// 例: ApplyRecolor 内
File.WriteAllBytes(outputPath, pngData);
string relativePath = ToAssetsRelative(outputPath);
if (relativePath != null)
    AssetDatabase.ImportAsset(relativePath);  // Refresh ではなくこれ

// バッチでは StartAssetEditing/StopAssetEditing でラップする
try
{
    AssetDatabase.StartAssetEditing();
    foreach (var (src, dst) in savedPairs) { /* ... */ }
}
finally
{
    AssetDatabase.StopAssetEditing();  // 内部で必要分だけ Refresh
}
```

プリセット削除時は、削除対象が Assets 配下なら `AssetDatabase.DeleteAsset(relativePath)`（これは `Refresh` を呼ばない）、そうでなければ単に `File.Delete` で十分（プロジェクト外のファイルなら Refresh 不要）。

---

### 4. `SessionState` のみのマスク永続化

#### 問題
編集中のマスクは `SessionState.SetString` で保存されている。`SessionState` は **Unity Editor のプロセスが生きている間だけ**有効で、Unity を閉じると消える。

#### 該当箇所
- [Code/VACCWindow.Mask.cs:496-625](Code/VACCWindow.Mask.cs#L496-L625) `SaveMaskToSession` / `RestoreMaskFromSession`

#### なぜ悪いか
- ユーザーが何時間もペイントしたマスクが Unity 再起動で消える。プリセットに含めれば残せるが、明示的に「Save Preset」を押さないと消えるという仕様はトラップ。
- 「再コンパイル時の保持」目的なら正しい使い方だが、それだけが目的とコメントで明示されていない。

#### 修正方針
2 つの選択肢:

**A. 仕様として割り切る + UI 改善**
- 「マスクは Unity 再起動で消えます。残したい場合は Preset に含めて保存してください」と UI 上のヘルプボックスで明示。
- ウィンドウを閉じる/Unity を閉じる際、未保存マスクがあれば確認ダイアログ。

**B. テクスチャ単位で永続化**
- 元テクスチャの `.meta` ファイル隣に `<TextureName>.vacc-mask.json`（または `.bytes`）を置き、マスクを永続化。
- 利点: ユーザーが「同じテクスチャに対するマスク」を何度も使い回せる。
- 欠点: アセット move/rename への追従が必要（→ 項目 5 と一括対応）。

推奨は **B**。実装難度は中程度（既存の `EncodeMask` がそのまま使える）。

---

### 5. アセット rename/move 非対応（`AssetPostprocessor` 無し）

#### 問題
マスクの SessionState キーは「ソーステクスチャの AssetPath」で生成される。テクスチャをプロジェクト内で rename/move するとキーが切り替わり、マスクが行方不明になる（実際は古いパスで残るため `SessionState` ストレージがゴミとして肥大する）。

#### 該当箇所
- [Code/VACCWindow.Mask.cs:498-507](Code/VACCWindow.Mask.cs#L498-L507) `MaskSessionPathKey` / `MaskArraySessionKey`

#### なぜ悪いか
- ユーザーが「ファイル名を整理しよう」と思っただけでマスクが消える。
- 古いキーが `SessionState` に残り続ける（`SessionState` は容量制限が厳しいわけではないが、肥大すると遅くなる）。

#### 修正方針
`AssetPostprocessor.OnPostprocessAllAssets` で move/rename を検出し、SessionState（または項目 4 の永続ストレージ）のキーを差し替える。

```csharp
internal class VACCAssetWatcher : AssetPostprocessor
{
    static void OnPostprocessAllAssets(
        string[] imported, string[] deleted,
        string[] movedTo, string[] movedFromPath)
    {
        for (int i = 0; i < movedTo.Length; i++)
            VACCWindow.MigrateMaskKey(movedFromPath[i], movedTo[i]);
        foreach (var d in deleted)
            VACCWindow.PurgeMaskKey(d);
    }
}
```

---

## Major

### 6. partial class による 4,900 行 God Class

#### 問題
`VACCWindow` は 7 ファイルに分割された partial class だが、論理的には 1 つの巨大クラスのまま。フィールドは各ファイルに散在し、依存関係（どのフィールドがどのファイルから書かれるか）が追えない。

#### 該当箇所
| ファイル | 行数 |
|----------|------|
| `VACCWindow.cs` | 525 |
| `VACCWindow.Mask.cs` | 772 |
| `VACCWindow.Preview.cs` | 797 |
| `VACCWindow.DetailPreview.cs` | 303 |
| `VACCWindow.Processing.cs` | 949 |
| `VACCWindow.Export.cs` | 335 |
| `VACCWindow.Presets.cs` | 306 |
| **合計** | **3,987**（VACCWindow 内のみ） |

#### なぜ悪いか
- 「Mask」と「Preview」と「DetailPreview」がお互いの private フィールドを直接触っており、責務境界がない。
- リファクタリングのスコープが分割しづらい（どこを変えても全体に波及しうる）。
- 単体テストが書けない（Editor 限定 + EditorWindow 継承 + private フィールド依存）。

#### 修正方針
段階的に「素の C# クラス」へ引き剥がす。Editor 依存は薄い層に閉じ込める。

```
VACCWindow                         (EditorWindow 本体・OnGUI ディスパッチ)
├── ui/
│   ├── ZoneListView               (DrawZoneList)
│   ├── ProcessingView             (DrawProcessingSection)
│   ├── MaskPaintView              (DrawMaskSection + ペイント入力)
│   ├── PreviewView                (DrawPreview)
│   └── ExportView / PresetsView
├── core/                          (Editor 非依存・ユニットテスト可能)
│   ├── MaskStore                  (exclusionMask, zoneMasks, Encode/Decode)
│   ├── MaskPainter                (PaintMask, ストローク管理)
│   ├── PixelProcessor             (現 Processing.cs の static メソッド群)
│   └── PreviewScheduler           (デバウンス・キャンセル制御)
└── infra/
    ├── MaskSessionStore           (SessionState/EditorPrefs アクセス)
    └── PresetStore                (JSON I/O)
```

`PixelProcessor` 以下はすでに static メソッドになっており Editor 非依存なので、まずこれを別ファイル（`Code/Core/PixelProcessor.cs`）に動かし、`dev_safe/Tests/` から直接呼べるようにすると単体テスト可能性が向上する。

---

### 7. OnGUI 内で重い同期処理

#### 問題
プレビュー処理は背景スレッドに逃がしているが、**マスクオーバーレイ生成**と**Diff テクスチャ生成**は依然 OnGUI で同期実行している。テクスチャが 4096×4096 だとマスクオーバーレイは 1,600 万ピクセル分のループになり、ペイント中の体感が悪化する。

#### 該当箇所
- [Code/VACCWindow.Mask.cs:319-414](Code/VACCWindow.Mask.cs#L319-L414) `RebuildMaskOverlay` / `RebuildZoneMaskOverlay`
- [Code/VACCWindow.Preview.cs:728-754](Code/VACCWindow.Preview.cs#L728-L754) `BuildDiffTexture`
- [Code/VACCWindow.DetailPreview.cs:209-234](Code/VACCWindow.DetailPreview.cs#L209-L234) `BuildDetailDiffTexture`
- [Code/VACCWindow.Preview.cs:594, 738](Code/VACCWindow.Preview.cs#L594) `sourceTexture.GetPixels32()`（メインスレッド前提だが OnGUI から呼ぶと重い）

#### なぜ悪いか
- ペイントブラシでマスクを書き換えると `maskDirty = true` → 次の OnGUI で `RebuildMaskOverlay` が走り、毎フレーム 16ms 超の停止が起きる。
- Diff モードを ON にしている間、プレビュー更新ごとに同期で diff が再計算される。

#### 修正方針
- マスクオーバーレイは「ペイントしたピクセル領域だけ更新」する差分構築（dirty rect）に変える。あるいは `Texture2D` への書き込みを `RenderTexture + GPU Compute` に置き換える（マスクは bool 配列 → R8 テクスチャ）。
- Diff は背景スレッドで並列計算し、`SetPixels32 + Apply` だけメインスレッドで呼ぶ（プレビュー本体と同じパターン）。
- マスクオーバーレイサイズを「プレビュー表示サイズ」に揃える（現状 `width × height` で渡されているが、全画面プレビューは 512px なので 4096px のマスクから 512px へ毎回ダウンサンプリングしている。逆順に「表示サイズの解像度で持つ」と圧倒的に軽い）。

---

### 8. `Texture2D` 手動管理 + `DestroyImmediate` 散在

#### 問題
プレビュー用テクスチャを `new Texture2D(...)` で確保し、サイズが変わるたびに `DestroyImmediate` する。同じパターンがファイルを跨いで複数箇所に書かれている。

#### 該当箇所
- [Code/VACCWindow.cs:506-523](Code/VACCWindow.cs#L506-L523) OnDestroy で 9 個の Texture を `DestroyImmediate`
- [Code/VACCWindow.Preview.cs:679-712](Code/VACCWindow.Preview.cs#L679-L712) `ScheduleDestroy`、preview/raw テクスチャの再確保
- [Code/VACCWindow.Mask.cs:319-414](Code/VACCWindow.Mask.cs#L319-L414) overlay テクスチャの再確保
- [Code/VACCWindow.DetailPreview.cs:185-234](Code/VACCWindow.DetailPreview.cs#L185-L234) detail テクスチャの再確保

#### なぜ悪いか
- 「サイズ違いなら破棄して作り直す」ロジックが 5 箇所以上にコピペされており、**一箇所の修正もれでテクスチャがリーク**する（実際 `BuildDiffTexture` の旧版が `ScheduleDestroy` 抜けで疑わしい）。
- `DestroyImmediate` は OnGUI から呼ぶと IMGUI のコントロール ID を破壊する場合があり、`ScheduleDestroy` の `delayCall` 経由パターンが導入されているが、すべての破棄経路に適用されているか保証がない。

#### 修正方針
ヘルパーで一本化。

```csharp
internal static class TextureSlot
{
    public static void Resize(ref Texture2D tex, int w, int h, FilterMode filter = FilterMode.Bilinear)
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
    static void ScheduleDestroy(Texture2D t)
    {
        if (t == null) return;
        EditorApplication.delayCall += () => { if (t != null) Object.DestroyImmediate(t); };
    }
}
```

OnDestroy は `TextureSlot.Release(ref previewTexture);` の羅列にできる。

---

### 9. `Localization.CurrentLanguage` がセッション間で消える

#### 問題
言語選択は静的フィールドで保持されるだけで `EditorPrefs` に保存されない。Unity を再起動すると `Auto` に戻る。

#### 該当箇所
- [Code/Localization.cs:7](Code/Localization.cs#L7)
- [Code/VACCWindow.cs:182-190](Code/VACCWindow.cs#L182-L190) UI 切り替え部

#### 修正方針

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

---

### 10. ハードコード色（Light/Dark Skin 非対応）

#### 問題
`new Color(0.55f, 0.75f, 1f)`, `new Color(1f, 0.55f, 0.55f)` など、トグルアクティブ表示色がハードコードされている。Unity の Light Skin（Personal Edition の旧テーマ）では文字色との相性が悪い場合がある。

#### 該当箇所
- [Code/VACCWindow.cs:284](Code/VACCWindow.cs#L284) ゾーンマスク編集ボタンのアクティブ色
- [Code/VACCWindow.Mask.cs:103, 112](Code/VACCWindow.Mask.cs#L103-L112) Exclude/Include ボタン
- [Code/VACCWindow.Preview.cs:419-421](Code/VACCWindow.Preview.cs#L419-L421) ブラシカーソル色

#### 修正方針
`EditorGUIUtility.isProSkin` で分岐するか、`EditorStyles.miniButtonLeft` の selected/active state を活用する。少なくとも色定数は 1 箇所にまとめる。

```csharp
internal static class VACCColors
{
    public static Color ActiveMaskTarget =>
        EditorGUIUtility.isProSkin
            ? new Color(0.45f, 0.65f, 0.95f)
            : new Color(0.30f, 0.55f, 0.85f);
    // ...
}
```

---

### 11. バックグラウンドタスクの `volatile` + 手動レース管理

#### 問題
プレビュー生成は `Task.Run` + `volatile bool _previewGenerating` + `_asyncGeneration` カウンタの自前レース管理。コメントが詳細だが、Editor のライフサイクル（domain reload, assembly recompile）で何が起きるかは保証がない。

#### 該当箇所
- [Code/VACCWindow.Preview.cs:21-29](Code/VACCWindow.Preview.cs#L21-L29) volatile フラグ群
- [Code/VACCWindow.Preview.cs:556-673](Code/VACCWindow.Preview.cs#L556-L673) `GeneratePreviewAsync`
- [Code/VACCWindow.DetailPreview.cs:14-27, 59-164](Code/VACCWindow.DetailPreview.cs#L14-L27)

#### なぜ悪いか
- Domain reload が走ると `Task` は中断するが、`finally` の `delayCall += Repaint` が消失したウィンドウへの参照を持ち続ける可能性。
- `_pendingProcessedDisplay = ...` はメインスレッドで読むまで非同期、Texture API はメインスレッド限定 — このルールはコメントで担保されているが、コードの離れた箇所での暗黙ルールに依存しがち。
- 同様の Async パターンが Preview と DetailPreview で重複実装されている。

#### 修正方針
- `EditorCoroutines`（`com.unity.editorcoroutines`）または `EditorApplication.update` ベースの軽量スケジューラに統一。
- 「重い計算 + キャンセル」の組はヘルパーに抽出:

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
}
```

これで Preview / DetailPreview / Diff の Async パターン重複を 1/3 に圧縮できる。

---

## Minor

### 12. `EnableReadWrite` が `Undo.RecordObject` 未使用

[Code/VACCWindow.cs:494-504](Code/VACCWindow.cs#L494-L504) で `TextureImporter.isReadable = true; importer.SaveAndReimport();` を呼ぶが、Undo に登録していない。ユーザーが意図せず Read/Write を有効にしてしまった場合、戻すには手動でインポート設定を変える必要がある。

```csharp
Undo.RecordObject(importer, "Enable Read/Write");
importer.isReadable = true;
importer.SaveAndReimport();
```

---

### 13. `try { ... } catch { /* ignore */ }` で例外握り潰し

[Code/VACCWindow.cs:511-512](Code/VACCWindow.cs#L511-L512), [Code/VACCWindow.Mask.cs:578, 711-714, 731-735](Code/VACCWindow.Mask.cs#L578) などで例外が無条件に黙殺されている。

- `_previewCts?.Cancel()` の例外は `ObjectDisposedException` 想定なので `catch (ObjectDisposedException)` に絞る。
- `JsonUtility.FromJson` / `Convert.FromBase64String` の失敗は最低限 `Debug.LogWarning($"[VACC] Mask decode failed: {ex.Message}")` でログを残す（CLAUDE.md 改善サイクルでも「テストを通すために例外を黙殺するな」のスタンスと揃う）。

---

### 14. `ProjectPresetFolder` の `AssetDatabase.FindAssets` 毎回呼び

[Code/VACCWindow.Presets.cs:20-44](Code/VACCWindow.Presets.cs#L20-L44) は property getter 内で毎回 `AssetDatabase.FindAssets("t:Script VACCWindow")` を呼ぶ。プリセット UI を描画する OnGUI 内で 1 フレームに複数回呼び出されている可能性がある。

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

スクリプトの場所はランタイムでは変わらないので、static キャッシュで安全。

---

### 15. `MenuItem` に priority / shortcut が無い

[Code/VACCWindow.cs:57](Code/VACCWindow.cs#L57) `[MenuItem("Tools/VRC AvatarColorChanger")]` は priority 指定がないため Tools メニュー内の並びが alphabetical になる。複数のツールが入った環境で見つけづらい。

```csharp
[MenuItem("Tools/yukkuri-aoba/VRC AvatarColorChanger", priority = 100)]
```

セパレータが欲しければ priority を 11 以上飛ばす（Unity の慣習）。

また、`GetWindow<VACCWindow>(Localization.WindowTitle)` の第 2 引数は `string title` だが、ドック時は `titleContent`（GUIContent）を別途設定したほうがアイコンも付けられる。

```csharp
window.titleContent = new GUIContent(Localization.WindowTitle, EditorGUIUtility.IconContent("d_Image Icon").image);
```

---

### 16. `ColorZone.Clone()` が JsonUtility round-trip

[Code/ColorZone.cs:99-100](Code/ColorZone.cs#L99-L100) で `JsonUtility.FromJson<ColorZone>(JsonUtility.ToJson(this))` を呼ぶ。フィールド追加時の手動更新が不要というメリットはあるが、プレビュー生成のたびにゾーン数 ×（JSON シリアライズ + デシリアライズ）が走る。

通常モードでゾーンが 5 個もあれば、1 プレビューあたり 10 回の JSON 変換。アロケーションも増える。

#### 修正方針
- 完全コピーが必要なフィールドは限られているので、`MemberwiseClone` ベースの shallow copy で十分。値型と string と Vector2/Color/Rect しか持たないため shallow で安全。

```csharp
public ColorZone Clone() => (ColorZone)MemberwiseClone();
```

`[NonSerialized]` キャッシュは MemberwiseClone でコピーされるが、コピー後すぐ `UpdateCacheIfNeeded` で再計算されるので問題なし（むしろ JsonUtility 版より整合性が取りやすい）。

---

### 17. レイアウトのマジックナンバー散在

`GUILayout.Width(280f)`, `GUILayout.Width(450f)`, `GUILayout.Width(22)`, `GUILayout.Width(48)` などが各所に直書き。「× ボタンの幅」「IntField の幅」など意図ごとに定数化したい。

[Code/VACCWindow.cs:55](Code/VACCWindow.cs#L55) で `SideBySideMinWidth` だけは定数化されているので、同じ場所に他も並べる:

```csharp
private static class Layout
{
    public const float SideBySideMinWidth = 600f;
    public const float LeftColumnRatio = 0.4f;
    public const float LeftColumnMin = 280f;
    public const float LeftColumnMax = 450f;
    public const float RemoveButtonWidth = 22f;
    public const float SmallButtonWidth = 48f;
    // ...
}
```

---

### 18. `EditorGUI.indentLevel` の手動 inc/dec

`indentLevel++` の後、例外が起きると `indentLevel--` が呼ばれず、次フレーム以降のすべての描画がインデントされたままになる。`using` パターンで保証する。

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
    // ...
}
```

`EditorGUI.DisabledScope` と同じ流儀で揃う。

---

### 19. `EditorGUI.BeginChangeCheck` の運用が部分的

[Code/VACCWindow.cs:115-133, 155-165](Code/VACCWindow.cs#L115-L133) で `BeginChangeCheck/EndChangeCheck` を使い「変化があれば `previewDirty = true`」としているが、`DrawZoneList` 内のゾーン削除や追加では `previewDirty = true` を別途明示している。マスクペイントも別ルートで `previewDirty` を立てる。

→ 全体的に「設定変更 = previewDirty」を `SerializedObject.ApplyModifiedProperties()` の戻り値で 1 箇所判定すれば、明示の `previewDirty = true` は要らなくなる（項目 1 と連動）。

---

### 20. `MenuItem` パス・ウィンドウタイトルの一元化不足

`"Tools/VRC AvatarColorChanger"` と `Localization.WindowTitle` がそれぞれ別箇所で定義され、内容も微妙に違う。プラグインメタデータ（`package.json` の displayName）とも独立して管理されている。

→ `VACCConsts.MenuPath`, `VACCConsts.WindowTitle` を 1 箇所に集約し、`package.json` の表示名と齟齬がないかは目視チェック（あるいは CI でチェック）する仕組みを入れる。

---

## リファクタリング推奨順序

依存順・効果順を考慮した提案:

1. **[Critical] 項目 6 を先に着手**（God Class 分解）
   - `Code/Core/PixelProcessor.cs` を切り出すだけで、`dev_safe/Tests/` から直接呼べるようになり、改善サイクル（CLAUDE.md `improvement-cycle`）が高速化する。
2. **[Critical] 項目 1 → 2**（SerializedObject 化と Undo 統合）
   - SerializedObject に乗せることで Undo は自動で付いてくる。`DrawZoneList` の 170 行が `EditorGUILayout.PropertyField(zonesProp, true)` 1 行になる規模感。
3. **[Critical] 項目 3**（AssetDatabase.Refresh 撲滅）
   - 機械的な置換で済むので低リスク高効果。
4. **[Major] 項目 7, 8, 11**（パフォーマンスとリソース管理）
   - 項目 6 が終わっていれば各 View に閉じた変更で済む。
5. **[Critical] 項目 4 → 5**（マスク永続化と asset rename 追従）
   - 項目 4 で永続化方式を決め、項目 5 でその key を rename に追従させる。
6. **[Major] 項目 9, 10**（UX 微調整）
7. **[Minor] 項目 12-20**（クリーンアップ）

各段階で `dev_safe/Tests/` の IoU テストが落ちないことを確認しながら進める（CLAUDE.md `improvement-cycle.instructions.md` の「採用が確定した変更のみコミット」ルールに従う）。

---

## 参考資料

- Unity Editor scripting best practices: https://docs.unity3d.com/Manual/editor-CustomEditors.html
- `SerializedObject` / `SerializedProperty`: https://docs.unity3d.com/ScriptReference/SerializedObject.html
- `Undo` API: https://docs.unity3d.com/ScriptReference/Undo.html
- `AssetPostprocessor`: https://docs.unity3d.com/ScriptReference/AssetPostprocessor.html
- `EditorCoroutines` パッケージ: `com.unity.editorcoroutines`
