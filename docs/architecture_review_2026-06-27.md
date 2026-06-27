# Iroca アーキテクチャレビュー（2026-06-27）

**対象**: `Code/` 配下の全 C#（`com.yukkuri-aoba.iroca` v0.2.0、計 ~12,400 行 / 38 ファイル）
**目的**: 採用アルゴリズムの構造、レイヤー分割、責務分離、依存関係を多角的に評価し、強み・弱み・改善優先度を整理する。
**位置づけ**: 本書は**現状（大規模リファクタ完了後）の構造を新規評価**する。旧 partial class 時代の問題を扱う [`refactoring-unity-editor-antipatterns.md`](refactoring-unity-editor-antipatterns.md) / [`refactoring-plan.md`](refactoring-plan.md) は概ね解消済みで、それらの「目標アーキテクチャ」が現コードの実体である。アルゴリズム内部の数学的妥当性は [`image_processing_math_review_2026-06-10.md`](image_processing_math_review_2026-06-10.md) / [`recolor_design_rationale.md`](recolor_design_rationale.md) を参照し、本書では深入りしない。

---

## 0. 評価サマリー

| 観点 | 評価 | 一言 |
|------|:----:|------|
| **計算層とUI層の分離** | ★★★★★ | 画像処理は Editor 非依存・headless テスト可能。本リポジトリ最大の長所 |
| **「選択 / 調整 / 再着色」の責務分離** | ★★★★★ | ColorZone（選択）・ZoneAutoTuner（調整）・RecolorPixel（再着色）が疎結合 |
| **オプション機能の隔離** | ★★★★★ | Debug / MCP を asmdef + 条件コンパイルで完全分離。本体は外部依存ゼロ |
| **永続化層の設計** | ★★★★☆ | GUID 追従・orphan 清理が堅牢。UI への逆依存 1 箇所が傷 |
| **パフォーマンス設計** | ★★★★★ | ArrayPool / ビットパック / bbox 限定 / 並列度制御 / 世代キャンセル |
| **ドキュメント** | ★★★★★ | 数学・設計根拠・性能・リファクタ計画を体系的に保有（異例の充実度） |
| **UI 層の構造** | ★★★☆☆ | `IrocaWindow` が神クラス化。View が Window 内部へ逆依存 |
| **巨大ファイル / 巨大関数** | ★★☆☆☆ | `PixelProcessor`(2588行) / `ProcessPixelsArray`(~600行) が単一肥大 |
| **パラメータ管理** | ★★☆☆☆ | `RecolorPixel`≈30 引数・`ColorZone` 18+ フィールドの引数爆発 |
| **C# ↔ Python 二重実装** | ★★☆☆☆ | アルゴリズムを 2 言語で手動同期。ドリフトが実際に発生している |

**総評**: **「画像処理エンジンとしての分離設計」は模範的、「Unity Editor UI とコード物量管理」が弱点**という、はっきりした非対称を持つコードベース。コア価値（再着色アルゴリズム）が UI から独立してテスト可能な点が最も評価できる。

---

## 1. システム全体構造

### 1.1 アセンブリ構成（3 + 衛星）

```
com.yukkuri-aoba.iroca.Editor        … 本体（Editor 専用、references: [] = 外部依存ゼロ）
├─ com.yukkuri-aoba.iroca.Editor.Debug   … デバッグ可視化（本体のみ参照、フォルダ削除で消滅）
└─ Iroca.McpIntegration                  … MCP 連携（#if IROCA_MCP_PRESENT で自動ゲート）
```

- 本体 asmdef の `references: []` は**「配布パッケージが MCPForUnity 等の外部パッケージに一切依存しない」**ことを構造的に保証している（`Code/com.yukkuri-aoba.iroca.Editor.asmdef`）。
- Debug / MCP は `versionDefines` + `defineConstraints` で、依存パッケージが無ければコンパイル対象から外れる。**オプション機能をプラグイン化する設計として教科書的**。

### 1.2 フォルダ（≒論理レイヤー）

| フォルダ | 役割 | 主なファイル |
|---------|------|------------|
| `Code/`(直下) | ドメイン中核 + ウィンドウ | `ColorZone.cs`(597)、`IrocaWindow.cs`(1285)、`Localization.cs`(572)、`IrocaConsts/Colors/PresetData` |
| `Core/` | Editor 非依存の計算・状態 | `PixelProcessor.cs`(2588)、`ZoneAutoTuner.cs`(1129)、`PreviewJob`、`SessionState`、`TextureSlot`、`HighlightSampleCorrector` |
| `UI/` | IMGUI 描画ビュー | `PreviewView.cs`(1049)、`MaskPaintView.cs`(1031)、`DetailPreviewView`、`ExportView`、`PresetsView` |
| `Infra/` | 永続化・アセット監視 | `PresetStore`、`MaskFileStore`、`IrocaAssetWatcher` |
| `Automation/` | ヘッドレス実行口 | `IrocaAutomation.cs`(572) |
| `Debug/` / `McpIntegration/` | オプション衛星 | 別 asmdef |

---

## 2. レイヤーと依存関係

### 2.1 想定される依存方向（概ね健全）

```
        ┌──────────── Automation ─── McpIntegration（衛星）
        │                  │
   IrocaWindow(UI) ──→ UI/Views ──┐
        │                            ├──→ Core（PixelProcessor / ZoneAutoTuner / PreviewJob …）
        │                            │         │
        └──────────────→ Infra ──────┘         └──→ ドメイン（ColorZone / SessionState）
                                                       ▲
                                          Debug ── IDebugCapture（インターフェース注入）
```

- **計算（Core/ドメイン）→ 上位を参照しない**のが守られている。`PixelProcessor` / `ZoneAutoTuner` / `ColorZone` は `UnityEditor` も `EditorWindow` も知らず、純粋な `Color32[]` 入出力で完結する。これが headless 実行（`scripts/headless-run/Harness.cs`）を可能にしている。
- Debug 機能は本体が `IDebugCapture` インターフェースのみを参照し、実装は別 asmdef に存在する（`Code/Core/IDebugCapture.cs` / `DebugCaptureHooks.cs` の Factory + event 注入）。**依存性逆転が正しく効いている**。

### 2.2 検出した依存違反・におい

1. **【要修正】Infra → UI の逆流**
   `Code/Infra/PresetStore.cs:151` と `Code/Automation/IrocaAutomation.cs:382` が `IrocaWindow.ToAssetsRelative()` を呼ぶ。下位（永続化・自動化）が上位（UI ウィンドウ）の static メソッドに依存している。
   → `PathUtils.ToAssetsRelativeOrNull()` 等のユーティリティへ抽出すれば、Infra / Automation が完全に UI 非依存になる（低コスト・効果大）。

2. **【構造的弱点】名前空間がフラット**
   フォルダで `Core` / `UI` / `Infra` を分けているが、**名前空間は全て `Iroca`**（Debug/MCP のみ別）。`grep "^namespace"` で確認済み。レイヤー境界が**命名規約だけで、コンパイラに強制されていない**。`UI` が `Infra` の内部型を直接触っても誰も止められない。サブ名前空間（`Iroca.Core` 等）導入で境界を可視化・強制できる。

3. **循環依存** … **なし**（Automation→Infra、Core→ドメインはいずれも一方向）。

---

## 3. アルゴリズムとデータフロー（コア価値）

このツールの本質は「1 クリックで選んだ色（とその陰影）を別の色へ違和感なく置換する」こと。実装は **3 つの関心事に明確に分離**されており、これが設計上の最大の達成点である。

| 関心事 | 担当 | 性質 |
|--------|------|------|
| **何を選ぶか（選択）** | `ColorZone.MatchOneSample`（`ColorZone.cs:388`） | サンプル色との距離でピクセル強度を判定 |
| **どう設定を決めるか（自動調整）** | `ZoneAutoTuner.Analyze`（`Core/ZoneAutoTuner.cs:116`） | テクスチャ統計から tolerance 等を推定 |
| **どう塗り替えるか（再着色）** | `PixelProcessor.RecolorPixel`（`Core/PixelProcessor.cs:2387`） | OkLab で明度・陰影を保ったまま色相変換 |

**重要な疎結合**: `ZoneAutoTuner` は `PixelProcessor` を呼ばない。`TuneResult` 構造体を返すだけで、選択も再着色もしない。「統計分析」と「画像変換」が交差しない。

### 3.1 選択アルゴリズム（ColorZone）

- **二系統のマッチング**（`ColorZone.cs:405`）:
  - 低彩度サンプル（グレー/黒）→ **純 RGB 距離モード**（色相を無視。`:408-411`）
  - 有彩サンプル → **HSV ハイブリッド距離**（色相 + 彩度 + 明度の重み付き。`CalculateHybridDistance` `:524`）
- **陰影/ハイライトの対称免除**（`:466` シャドウ / `:490` ハイライト）: 同色相なら、暗部の彩度低下・明部の白飛びを「同マテリアル」として許容する。明暗で対称な数式になっている点が綺麗。
- **彩度整合ゲート**（`:431`）: 高彩度サンプル時、純白背景など中性画素へ距離ペナルティを加え過検出を防ぐ。明部限定（`gateWeight=clamp(sV/0.3)`）で暗布の正常な中性を保護。
- **マルチサンプル和集合**（`GetColorMatchScores` `:378`）: `extraSamples`（自動トーン抽出で得た暗/中/明の代表色）を OR で合成し、スポイト位置に依存しない選択を実現。
- **キャッシュ設計**（`SampleCache` / `UpdateCacheIfNeeded`）: サンプル依存の派生値をホットループ前に確定し、`Parallel.For` 内の条件分岐を排除。**性能を意識した良いキャッシュ**。

### 3.2 再着色パイプライン（PixelProcessor.ProcessPixelsArray）

`Code/Core/PixelProcessor.cs:138` の単一メソッドが全段を統括する。ゾーンごとに以下を順に実行：

```
[全画素 HSV 一括計算（zone 数に依らず1回）:191]
  └ foreach zone:
     1.  マッチ強度マップ構築            GetMatchScoresPrecomputedHSV  :254
     1a. ハイライト空間伝播             PropagateHighlights           :264
     1a1.ハイライト帯成長               GrowHighlightBand             :274
     1a2.連結成分アンカリング(全画像時) ApplyConnectedComponentMask   :295
     ──  後段の処理範囲 bbox を算出      TryComputeStrengthBBox        :333  ← 性能
     1b. 小穴埋め(relaxed ゲート付き)    FillSmallHoles                :384
     1c. 境界エッジ回復                 RecoverBoundaryEdges          :398
     2.  端ガウシアンブラー             GaussianBlur + ConstrainBlur  :418
     3.  除外マスク再適用                                             :440
     3a. 中性リジェクト(有彩→無彩時)    RejectNeutralForAchromaTarget :473
     ──  無彩内部固め                   SolidifyAchromaInterior       :481
     3b. AA 境界デコンタミ(α分解再合成) DecontaminateAaBoundary       :493
     4.  OkLab 再着色 + レイヤー排他合成 RecolorPixel                  :647
     ──  無彩フチ消し                   CleanAchromaFringe            :687
```

特筆すべき設計:
- **レイヤー排他合成**（`claimed` バッファ `:175,630`）: 上位ゾーンが占有した分を下位が差し引く front-to-back over。単一ゾーン/非重複では `claimed` が 0 のままで**従来出力がバイト不変**になるよう注意深く実装。
- **連結成分アンカリングのキャッシュ転写**（`FloodFillKeepCache` `:298-320`）: フル画像で解いた keep を詳細プレビュー（クロップ）へ転写し、プレビューと最終結果を完全一致させる。
- **OkLab 再着色**（`RecolorPixel` `:2400-`）: L は 2 区間線形リマップ（不動点 sL→tL、リング無し・端点保存）、彩度は target 色相方向へ均一化。`recolor_design_rationale.md` で「中核は正当」と結論付け済み。

### 3.3 自動調整（ZoneAutoTuner）

- 32bin の H/S/V ヒストグラム、600bin の距離分布、64bin の彩度/明度分布から、P95+マージン等で tolerance を導出（`Core/ZoneAutoTuner.cs`）。
- **foreign-hue 打ち切り**: 自パーツ core の色相広がり（P90）を基準に隣接別パーツの距離分布を分離し、tolerance 上限を抑える。隣接同色相パーツへの滲みを抑制する賢い仕組み。
- 定数は名前付き `const`（`SaturationGuardActivateSS=0.95f` 等）に集約され、**コミットコメントに感度解析の根拠が残っている**点が良い。

---

## 4. 責務分離の評価

### 4.1 良く分離できている箇所（強み）

- **計算層の Editor 非依存**: §2.1 の通り。`scripts/headless-run/` で実 DLL を Unity 無しで実行・GT 比較できるのは、この分離の直接的な果実。
- **Core サポート群の粒度が適切**: `TextureSlot`(40行=Texture2D ライフサイクル)、`PreviewJob<T>`(126行=世代管理+キャンセル)、`MaskState`(23行=純データ)、`UndoHelper`(91行=Undo ラッパー) 等、いずれも単一責任で小さい。
- **PreviewJob<T> の非同期抽象化**: 世代インクリメントで古い結果を破棄、`ConcurrentQueue` でメインスレッド復帰、ドメインリロード防御まで含む。プレビュー/Diff/オーバーレイ/エクスポート/自動調整が同じ仕組みに乗る。
- **永続化の分離**: `PresetStore`（JSON、Assets/ユーザー/任意パスを明確に分岐）、`MaskFileStore`（GUID キーで rename/move 追従 + 二段構えの orphan 清理 `:119`）。マスクが空なら既存ファイルを削除しディスクを節約（`:47`）。
- **ヘッドレス API の自己記述**: `IrocaAutomation.DescribeSchema()` が AI エージェント向けに JSON スキーマ・既定値・説明を返す。MCP ツールはこれを薄くラップするだけ。

### 4.2 分離が弱い箇所（弱み）

#### (A) `IrocaWindow` の神クラス化（Major）
- 約 **38 フィールド・5+ の責務**（レイアウト / ゾーン CRUD / ドラッグ&ドロップ / 各 View 統制 / 自動調整ジョブ / Undo 統合 / パス正規化）。
- View 群が `_host`（=IrocaWindow）への逆ポインタを持ち、`_host.Session.zones` / `_host._maskView` / `_host.MarkPreviewDirty()` と**内部構造へ直接アクセス**する（PreviewView ↔ MaskPaintView の相互参照を Window 経由で行う）。
- dirty / pending / drag 状態が Window と各 View に**分散**し、状態遷移ルールが暗黙。
- ※ ただし IMGUI（即時モード）では「描画と状態変更が同一フレームで混ざる」「`ExitGUI` 回避の pending パターン」は構造的に避けにくい。UI のテスト困難性も IMGUI 由来で、設計者だけの責任ではない。

#### (B) `PixelProcessor` の単一肥大（Major）
- **2588 行 1 ファイル / `ProcessPixelsArray` ≈600 行 1 メソッド**。§3.2 の全 14 段がインライン展開され、循環的複雑度が非常に高い。
- 各段は `if (hasPostBox)` 等で丁寧にゲートされコメントも厚いが、「パイプライン段を `IStage` 的に分離」すれば可読性・単体テスト性が上がる余地。
- ArrayPool の借用/返却が `try/finally` で人手管理されており（`:230-739`）、段追加のたびにリーク注意が要る。

#### (C) パラメータ引数爆発（Major）
- `RecolorPixel` の引数は **約 30 個**（`:2387-2398`）、`ProcessPixelsArray` は約 20 個、`ColorZone` の公開チューニングフィールドは **18+**。
- 関連パラメータ（サンプリング系 / 再着色系 / マッチング系）を構造体にグループ化すれば、シグネチャ・UI パネル・JSON の三方が整理される。

#### (D) C# ↔ Python の二重実装コスト（Major / ドメイン固有）
- C# 内に **「`algorithm.py … と同期」というコメントが 28 箇所**（`PixelProcessor` 25 / `HighlightSampleCorrector` 2 / `ColorZone` 1）。改善サイクルが Python（`dev_safe/`）で試作 → C# へ手動移植する運用のため、**同一アルゴリズムが 2 言語に存在**する。
- これは「Python で速く試す」運用の代償だが、**ドリフトが実際に起きている**（メモ `project_dark_target_brightness_cap`: 「現 C# に topL/HighlightLMult 不在＝要再移植」、`project_logo_strip_noise_holefill`: 「reset で C# から消えていたのを再移植」）。
- 緩和策: ①移植チェックリスト/差分テストの常設、②長期的には C# を単一の正とし Python を検証専用に降格、のいずれか。

#### (E) `ColorZone` のインライン・マジックナンバー（Minor）
- `ZoneAutoTuner` は定数を named const + 根拠コメント付きで集約しているが、`ColorZone` のマッチングは `0.75`（シャドウ閾値 `:466`）、`0.15`（色相ゲート）、`0.3`、`0.57735027f`（1/√3）等が**インライン**。改善サイクル指針（特定値で条件分岐しない・統計量から導く）に照らし、命名 + 根拠付けが望ましい。

---

## 5. 横断的関心事

- **パフォーマンス**: `ArrayPool<float/bool>`（GC 圧迫回避）、マスクの `ulong[]` ビットパック（`MaskSnapshot`）、後段パスの **bbox 限定**（小マッチで全画素走査回避、出力ビット不変を担保）、`MaxDegreeOfParallelism = コア数-2` で Editor スレッドプール保護。詳細は [`csharp_performance_review_2026-06-12.md`](csharp_performance_review_2026-06-12.md)。**コア計算の性能設計は一級**。
- **国際化**: `Localization.cs` に集約。キャッシュでアロケーション削減、言語設定を EditorPrefs に永続化。表示文字列の正を一元化し `IrocaConsts` は非多言語値のみ持つ、という分担も明確。
- **定数集約**: `IrocaConsts`（レイアウト/プレビュー/実験フラグ）。実験機能は `ExperimentalFeatures.EnableFloodFill` のようにコンパイル時 const で集約。
- **ドキュメント**: 数学レビュー・設計根拠・性能レビュー・リファクタ計画/アンチパターン台帳を体系保有。**コードと並走するドキュメント文化は希少な強み**。ただし `refactoring-unity-editor-antipatterns.md` は旧 partial class 時代（VACCWindow 4900行）の記述で**陳腐化**しており、「解消済み」追記が望ましい。

---

## 6. 改善推奨（優先度順）

| 優先 | 項目 | 概要 | コスト/効果 |
|:----:|------|------|:----------:|
| **A** | Infra→UI 逆流解消 | `ToAssetsRelative` を `PathUtils` へ抽出（PresetStore:151 / Automation:382） | 低 / 中 |
| **A** | アンチパターン台帳の更新 | 旧 partial class 前提の記述に「解消済み」を明記し誤読防止 | 低 / 中 |
| **B** | レイヤー名前空間の導入 | `Iroca.Core/UI/Infra` で境界をコンパイラ強制 | 中 / 中 |
| **B** | C#↔Python ドリフト対策 | 移植チェックリスト + headless 差分テストの常設化 | 中 / 大 |
| **B** | パラメータの構造体化 | `RecolorPixel`/`ColorZone` の関連引数をグループ化 | 中 / 中 |
| **C** | `ProcessPixelsArray` の段分割 | パイプライン段を抽出しテスト境界を作る（出力不変を担保しつつ） | 大 / 中 |
| **C** | `IrocaWindow` の状態集約 | dirty/pending を状態クラスへ、View の Window 内部参照を縮小 | 大 / 中 |
| **C** | `ColorZone` マジックナンバー命名 | インライン定数を named const + 根拠コメント化 | 低 / 小 |

> いずれも**設計の根本的欠陥ではなく、物量・境界強制・運用ドリフトの管理**に関する改善。コア（計算層の分離と再着色アルゴリズム）には手を入れる必要がない。

---

## 6.1 対応状況（2026-06-27 実施）

§6 のうち、費用対効果が高く・低〜中リスク・**コア計算の出力を変えない**項目を実施した。コア（計算層の分離・再着色アルゴリズム）は不変。出力バイト不変が要る項目は、新設した **C# 自己ゴールデン回帰テスト**（後述）で全件バイト一致を機械確認している。

| 項目 | 状態 | コミット | 備考 |
|------|:----:|---------|------|
| A1 Infra→UI 逆流解消 | ✅ 解消 | `7b59e01` | `ToAssetsRelative` を [`Code/Infra/PathUtils.cs`](../Code/Infra/PathUtils.cs) へ抽出。Infra/Automation が UI 非依存に。出力不変 |
| A2 アンチパターン台帳更新 | ✅ 解消 | `284649f` | 旧 partial class 台帳 2 件の冒頭に「解消済み」状態ヘッダ注記を追加 |
| B2 C#↔Python ドリフト対策 | ✅ 解消（方針変更） | `78068da` | **C# が製品の唯一の正・Python は使い捨て試作**という実態に合わせ、「C#≡Python 一致テスト」ではなく **C# 自己ゴールデン回帰テスト**（[`scripts/golden/`](../scripts/golden/)、Python 非依存）を新設。C# が黙って退行（段の欠落・定数ずれ等）したら検出。ネガティブ検証済み |
| B3 パラメータ構造体化 | ✅ 解消（RecolorPixel） | `3713c0e` | `RecolorPixel` の約27引数→`readonly struct RecolorParams` の `in` 渡しで 7 引数に。出力バイト不変。`ColorZone` 全体の構造体化は Preset JSON 互換破壊のため見送り |
| C3 ColorZone マジックナンバー命名 | ✅ 解消 | `9386ddf` | マッチ部のインライン定数を named const + 根拠/同期コメント化。出力バイト不変 |
| B1 レイヤー名前空間導入 | ⏸ 見送り | — | 26 ファイル一括変更・回帰面が広く効果中。asmdef + headless 制約で境界は概ね既達のため限界効用が小さい |
| C1 ProcessPixelsArray 段分割 | ⏸ 見送り | — | 既に 8 段抽出済み・共有可変バッファ密結合でコスト大。テスト境界は B2 ゴールデンで代替 |
| C2 IrocaWindow 状態集約 | ⏸ 見送り | — | IMGUI 構造的制約・View 逆参照 66 箇所で大規模・実機手動検証必須 |

補足:
- **B2 の方針変更**: 当初案（C#↔Python 双方向一致テスト）は、実測で両者が複数経路（グレーモード・ハイライト等）で意図的に乖離していることが判明し、かつ「Python は使い捨て」という運用方針と相反するため不採用。代わりに製品である C# 自身の出力スナップショットを固定する回帰テストとした。
- 既存の `dev_safe/Tests/regression/test_csharp_quality_gate.py` が旧 DLL 名 `VACCHeadless` を参照したまま skip に落ちていたリネーム取り残しをローカル修正（`IrocaHeadless`）。`dev_safe` は git 管理外のためコミットには含まれない。

---

## 7. 結論

Iroca は **「画像処理エンジン」としては模範的に分離されたアーキテクチャ**を持つ。選択 / 自動調整 / 再着色の 3 関心事が疎結合で、計算層が Unity Editor から完全独立し headless テスト可能であること、オプション機能（Debug / MCP）が asmdef + 条件コンパイルで本体から隔離され外部依存ゼロを保っていることは、特に評価できる。

一方、弱点は **Unity Editor UI 層とコード物量の管理**に集中する。`IrocaWindow` の神クラス化と View の逆依存、`PixelProcessor`(2588行)/`ProcessPixelsArray`(600行) の単一肥大、約 30 引数のパラメータ爆発、そして C# と Python の二重実装によるドリフトリスクが主な負債である。ただしこれらは段階的リファクタで解消可能で、コア計算の正しさ・性能には影響しない。

総じて、**「価値の中心（再着色アルゴリズム）を最も丁寧に設計し、周辺（UI・物量・運用）に技術的負債を寄せている」**、優先順位の付け方として合理的なコードベースである。

---

*本レビューは静的解析・コード読解に基づく。各指摘は `file:line` で追跡可能。アルゴリズムの数値的正しさは [`image_processing_math_review_2026-06-10.md`](image_processing_math_review_2026-06-10.md) を、再着色の設計判断は [`recolor_design_rationale.md`](recolor_design_rationale.md) を参照のこと。*
