# 未修正・一部改善の残存課題

> 作成日: 2026-05-06  
> 最終更新: 2026-05-06  
> 元ドキュメント: `docs/codebase_analysis.md` の検証結果に基づく

---

## 凡例

| 記号 | 意味 |
|:----:|------|
| ✅ 修正済み | コミット済み |
| ❌ 未修正 | 分析時点から手が付けられていない |
| ⚠️ 一部改善 | 部分的に対処されたが、本質的な問題は残る |

---

## ✅ 修正済み（4件）

| 項目 | 内容 | コミット |
|------|------|---------|
| 3.9 | package.json `url` インデント修正 | `chore: package.json の url フィールドのインデントを修正` |
| 3.6 | SessionState マスク RLE 圧縮（4096×4096: 約21MB→18バイト） | `perf(mask): マスク保存に RLE 圧縮を導入し SessionState のサイズを削減` |
| 3.4 | `finally` 内の volatile レースコンディション修正（世代番号チェック追加） | `fix(ui): バックグラウンドタスクの finally で旧世代のみフラグをリセットしレースを修正` |
| 3.5 | Ctrl+Z をウィンドウ全体で捕捉（プレビュー領域外でもマスク Undo 有効） | `fix(ui): Ctrl+Z をウィンドウ全体で捕捉しプレビュー領域外でもマスク Undo を有効化` |

---

## ❌ 未修正（1件）

### 3.10 partial class の過剰分割

**該当**: `VACCWindow.cs` + 6 つの partial class ファイル

7 ファイルに分割された partial class 構造は維持されている。状態のライフサイクル把握が難しく、密結合になっている点は変わらない。ただし修正難易度が高く、優先度は低い。

**改善案**:
- マスクロジック → `MaskManager` クラスに抽出
- プレビュー生成 → `PreviewRenderer` クラスに抽出
- エクスポート → `TextureExporter` クラスに抽出

---

## ⚠️ 一部改善（2件）

### 3.5 Ctrl+Z カバレッジ（参考）

独自 Undo スタック（`_undoMaskHistory`）は残存しているが、**Ctrl+Z はウィンドウ全体で捕捉**されるようになり、プレビュー領域外でも機能する。

**残存制限**:
- Unity の Edit > Undo 履歴リストには「Mask Paint」が表示されない（SessionState ベースのため）
- ScriptableObject プロキシを使った完全統合は実装コストが高い（大きなマスクの直列化問題）

### 3.3 バックグラウンドスレッドからの Unity API 呼び出し

**該当**: `VACCWindow.Preview.cs` L675, `VACCWindow.DetailPreview.cs` L160

`EditorApplication.delayCall += Repaint` は依然として `Task.Run` の `finally` ブロック内（非 UI スレッド）にある。ただし `EditorApplication.delayCall` は Unity 公式のスレッドセーフ API であり、登録されたデリゲートはメインスレッドで実行されるため、実害はない。

**改善された点**:
- `_asyncCancelled` ガードが追加され、ウィンドウ破棄時は `Repaint` を呼ばないようになった

**残存リスク**:
- `_previewGenerating = false` の書き込みが `volatile` のみで保護されている（3.4 と同根）

---

### 3.4 volatile 多用による擬似的スレッド安全性

**該当**: `VACCWindow.Preview.cs` L21-L23, `VACCWindow.DetailPreview.cs` L14-L15

```csharp
private volatile bool _previewGenerating;
private volatile bool _asyncCancelled;
private volatile int _asyncGeneration;
private volatile bool _detailGenerating;
private volatile int _detailAsyncGeneration;
```

複数の `volatile` 変数を組み合わせた条件判断はアトミックではない。

**改善された点**:
- `CancellationTokenSource`（`_previewCts`, `_detailCts`）が導入され、キャンセル伝播には `OperationCanceledException` が使われるようになった
- `_asyncCancelled` の用途が `OnDestroy()` での `Repaint` 抑制に限定された

**残存リスク**:
- `_previewGenerating` / `_detailGenerating` の読み取りと `_asyncGeneration` / `_detailAsyncGeneration` の比較がアトミックではない
- レースコンディションが発生する可能性は低いが、完全に排除できていない

**改善案**:
- `lock` ステートメントを使用する
- `Interlocked.CompareExchange` を使用する
- または `CancellationTokenSource` に一元化して `volatile` 変数を削除する

---

## 対応優先度マトリクス（残存課題）

| 優先度 | 問題 | 影響 | 修正難易度 | 状態 |
|--------|------|------|-----------|------|
| 🟢 低 | 3.10 partial class 過剰分割 | 保守性低下 | 高 | ❌ 未修正 |
| 🟢 低 | 3.3 バックグラウンドスレッド API | 低リスク（API はスレッドセーフ） | 低 | ⚠️ 低リスク |
| 🟢 低 | 3.4 volatile スレッド同期 | 低リスク（CTS 導入済） | 中 | ⚠️ 低リスク |
