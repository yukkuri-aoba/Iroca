# VRC AvatarColorChanger (VACC) ユーザーマニュアル

*[日本語](#日本語) | [English](#english)*

---

## 日本語

### 目次

- [インストール](#インストール)
- [基本的な使い方](#基本的な使い方)
- [カラーゾーンの詳しい説明](#カラーゾーンの詳しい説明)
- [加工プリセット](#加工プリセット)
- [プレビュー機能](#プレビュー機能)
- [除外マスク](#除外マスク)
- [バッチ適用](#バッチ適用)
- [エクスポート](#エクスポート)
- [トラブルシューティング](#トラブルシューティング)
- [よくある質問](#よくある質問)

---

### インストール

#### 前提条件

- Unity 2022.3.22f1 以降
- 対象テクスチャは **Read/Write Enabled** が有効である必要があります。
> ツールを起動した後ボタンを押すと、自動で有効にすることも可能です。

#### インストール手順

1. **Unity Editor を開く**
   - プロジェクトの Assets フォルダを開きます。

2. **.unitypackage をインポート**
   - Unity メニューから `Assets > Import Package > Custom Package...` を選択
   - ダウンロードした `.unitypackage` ファイルを選択
   - インポートダイアログで「Import」をクリック
   - 完了すると、`Assets/VACC` フォルダが作成されます。

3. **ウィンドウを開く**
   - Unity メニューから `Tools > VRC AvatarColorChanger` を選択
   - VACC ウィンドウが起動します

#### Read/Write Enabled の有効化

テクスチャが Read/Write Enabled できていない場合：

1. VACC ウィンドウで対象テクスチャを選択
2. ウィンドウに警告メッセージが表示されます
3. 表示されたボタンをクリックして自動的に有効化します

---

### 基本的な使い方

#### ステップ 1: テクスチャを選択

1. VACC ウィンドウの「Texture」フィールドをクリック
2. Unity のテクスチャ選択ダイアログが開きます
3. 色改変したいテクスチャを選択
4. プレビュー画面にテクスチャが表示されます

#### ステップ 2: カラーゾーンを追加

1. ウィンドウの「Add Color Zone」ボタンをクリック
2. 新しいカラーゾーンが作成されます
3. 各ゾーンには一意の名前が自動付与されます

#### ステップ 3: 色改変対象を選択

カラーピック モードとUV矩形 モードの2つがあります。

**カラーピック モード（推奨）：**
1. ゾーン設定で「Color Pick」を選択
2. 「Sample Color」をクリック
3. プレビューから、改変したい色をクリック
4. 「Tolerance」スライダーを調整して、選択範囲を微調整

**UV矩形 モード：**
1. ゾーン設定で「UV Rect」を選択
2. UV座標で矩形範囲を指定（Min/Max）
3. より細かい範囲指定が可能です

#### ステップ 4: 色を設定

1. ゾーン設定の「Target Color」をクリック
2. カラーピッカーで新しい色を選択
3. プレビューに反映されます

#### ステップ 5: 詳細を調整（必要に応じて）

以下の設定のいずれかまたはすべてを調整します：

- **Pattern Preserve** — 元の柄の残し具合（0 = 単色、1 = 柄がそのまま）
- **Edge Softness** — エッジの硬さ（0 = 硬い、1 = 柔らかい）
- **Saturation Filter** — 薄い色の除外度（0 = 除外なし、1 = 鮮やかな色のみ）
- **Layer Index** — 複数ゾーン重複時の優先度（大きい値が優先）

#### ステップ 6: エクスポート

1. ウィンドウの「Apply & Save」ボタンをクリック
2. 保存ダイアログが表示されます
3. 「Overwrite」または「Save As」を選択
4. ファイルが保存されます

---

### カラーゾーンの詳しい説明

#### カラーピック モード

サンプル色と合致度に基づいて自動的に改変対象を検出します。

**設定項目：**

- **Sample Color**
  - クリックするとプレビュー上で色をピック可能
  - 改変したい部分の中で最も鮮やかな色を選ぶとうまくいきやすい

- **Tolerance**
  - 色の合致許容範囲（0.0 ～ 255.0）
  - 低い値：より厳密に色を判定（選択範囲が狭い）
  - 高い値：より広く色を判定（選択範囲が広い、ノイズが増える傾向）
  - 推奨値：15～40（テクスチャに応じて調整）

#### UV矩形 モード

UV座標で矩形範囲を明示的に指定します。複雑な色構成のテクスチャに向いています。

**設定項目：**

- **Min UV** — 矩形の左上コーナーのUV座標
- **Max UV** — 矩形の右下コーナーのUV座標

#### 共通設定

**Pattern Preserve（模様保持スライダー）**

改変されたピクセルの明度をどの程度残すかを制御します。

- **0 に近い** — 指定した色で完全に上書き（単色）
- **0.5** — 元の柄の明度を50%保持（バランス重視）
- **1 に近い** — 元の柄をほぼそのまま保持（明度パターンのみ変更）

**Edge Softness（エッジ柔らかさ）**

選択エッジの判定の柔軟性を制御します。ぼやけたテクスチャに対応します。

- **0**（硬い） — エッジをシャープに判定（通常のテクスチャ向け）
- **0.5** — バランス型
- **1**（柔らかい） — エッジをぼやけた状態で判定（ぼかしがあるテクスチャ向け）

**Saturation Filter（彩度制限）**

テクスチャの周辺に存在する「薄い色（低彩度色）」をどう扱うかを制御します。

- **0**（含める） — 薄い色も対象に含める
- **0.5**（標準） — 中程度のフィルタリング
- **1**（除外） — 鮮やかな色のみを対象

> **注：** テクスチャの周辺部分はぼかし処理などがされていることが多く、薄い色になっています。この設定を0に近くすると、元の色のドットが残るのを抑制できます。

**詳しくは「トラブルシューティング」を参照してください。**

**Layer Index（レイヤーインデックス）**

複数のカラーゾーンが重なる場合の適用順を指定します。

- 大きい値が優先（最後に適用される）
- 同じ値の場合は追加された順に適用

#### Target Color（対象色）

改変後の色を指定します。

---

### 加工プリセット

エッジやノイズの処理を調整するプロファイルです。

#### Edge Feather（エッジぼかし）

選択エッジに Gaussian Blur を適用して滑らかな色の遷移を実現します。

- **0** — オフ（エッジがシャープ）
- **0.5 ～ 1.5** — 標準的なぼかし
- **2.0 以上** — 強いぼかし（滑らかなテクスチャ向け）

#### Boundary Cleanup（境界クリーンアップ）

ぼやけた部分に残った細かいノイズを除去します。

- **0** — オフ
- **1 ～ 2** — 弱いクリーンアップ
- **3**（標準） — 標準クリーンアップ（推奨）
- **4 ～ 5** — 強いクリーンアップ（より多くのノイズを除去）

#### Apply Processing

「Presets」セクションで「Apply」をクリックすると、上記設定がテクスチャに適用されます。

---

### プレビュー機能

#### ズーム

- **Ctrl + スクロール** — ズームイン/アウト
- **右ドラッグ** — ビューをパン（移動）

#### ビューモード

- **Normal** — 現在の処理結果をプレビュー
- **Before/After** — 変更前後を左右に並べて表示
- **Diff** — 変更されたピクセルのみをハイライト表示

#### プレビューの自動更新

設定変更後、短い遅延（デフォルト: 0.5秒）で自動的に更新されます。

---

### 除外マスク

プレビュー上でブラシを使用して、色改変したくない領域をマスクします。

#### 使い方

1. ウィンドウの「Enable Exclusion Mask」をチェック
2. プレビュー上でドラッグしてマスクを描画
3. マスクされた部分は色改変されません

#### ブラシ設定

- **Brush Size** — ブラシサイズ（小 ～ 大）
- **Opacity** — ブラシの透明度（0 ～ 1）

#### Undo

- **Ctrl + Z** — 最後の描画をアンドゥ
- **Ctrl + Y** — 最後のアンドゥを取り消し

#### リセット

「Clear Mask」ボタンで全てのマスクをクリアします。

---

### バッチ適用

同じカラーゾーン設定を複数のテクスチャに一括適用します。

#### 手順

1. マスターテクスチャでカラーゾーンを完成させる
2. 「Batch Apply」セクションを開く
3. 適用対象のテクスチャを追加
4. 「Apply to All」をクリック

全対象テクスチャに同じ設定が適用されます。

---

### エクスポート

#### 保存方法

「Apply & Save」ボタンをクリックすると以下の選択肢が表示されます：

**Overwrite**
- 元のテクスチャを上書き保存
- ファイルパス変更なし
- バックアップを強く推奨

**Save As**
- 新しいファイルとして保存
- ファイル名を指定可能
- 元のテクスチャは保護される

---

### トラブルシューティング

#### 問題: 図形の周りに色が薄い部分やドットが残る

**原因：**
テクスチャの周辺部分（アンチエイリアス処理やぼかしがされている部分）は「薄い色」になっています。

**対策：**

1. **彩度制限を高めに設定**（推奨）
   - 0.7 ～ 0.9 あたりから試す
   - 「薄い色を除外」するため、周辺部分を無視できる

2. **Tolerance を狭める**
   - より厳密に色を判定し、混ざった色を除外

3. **除外マスクを使用**
   - 混ざっている部分を明示的にマスクする

#### 問題: 色がはみ出してしまう

**原因：**
彩度制限が低すぎるか、Toleranceが広すぎます。

**対策：**

1. **彩度制限を高める**（推奨：0.8 ～ 0.95）
   - より薄い色を除外

2. **Tolerance を狭める**
   - より限定的に色を判定

3. **Edge Softness を調整**
   - テクスチャに応じて 0.0 ～ 0.5 で試す

#### 問題: 境界に細かいノイズが残る

**原因：**
彩度制限が高すぎるか、境界処理が不足しています。

**対策：**

1. **彩度制限を低めに設定**（0.1 ～ 0.4）
   - より多くの薄い色を対象にする
   - ただし色がはみ出しやすくなる

2. **Boundary Cleanup を有効にする**
   - 0 → 3 に設定、または 3 → 5 に増やす

3. **Edge Feather を有効にする**
   - 0.5 ～ 1.5 程度から開始

4. **Edge Softness を上げる**
   - 0.3 ～ 0.7 で試す

#### 問題: 境界がギザギザしている / 硬い

**原因：**
エッジ処理が不足しています。

**対策：**

1. **Edge Feather を有効にする**（推奨）
   - 0.5 ～ 1.5 程度から開始

2. **Edge Softness を上げる**
   - 0.3 ～ 0.7 で試す

3. **彩度制限を少し下げる**
   - 0.3 ～ 0.45 で試す
   - 境界部分のピクセルをより多く拾える

#### 問題: テクスチャ全体が変わってしまう

**原因：**
Tolerance が高すぎます。

**対策：**

1. **Tolerance を大幅に狭める**
   - 5～15程度から始めて調整

2. **Sample Color を再選択**
   - より限定的な色を選ぶ

3. **UV矩形 モードへの切り替えを検討**
   - 正確な範囲指定が可能

#### 問題: 黒い色に変更できない

**原因：**
黒色は明度情報を失うため、Pattern Preserve の効果が適用しにくいです。

**対策：**

1. **Pattern Preserve を低めに設定**
   - 0 ～ 0.3 あたりから試す
   - 「単色へのべた塗り」になり、黒も指定可能

2. **Edge Softness を 0 に設定**
   - より硬いエッジで判定

3. **除外マスクを活用**
   - 保護したい部分を先に指定

---

### よくある質問

**Q: 複数のカラーゾーンを組み合わせられますか？**

A: はい。「Add Color Zone」で複数ゾーンを追加できます。Layer Index で優先度を制御できます。

**Q: PSD のレイヤー構造をサポートしていますか？**

A: いいえ。本ツールは PNG などの統合テクスチャを対象としています。PSD がある場合は、そちらを編集することをお勧めします。

**Q: Undo は対応していますか？**

A: 除外マスク作成時には Ctrl+Z でアンドゥできます。ただし、テクスチャの色改変自体は一度適用すると戻せませんので、バックアップを推奨します。

**Q: 複数の Project で使用できますか？**

A: はい。.unitypackage をインポートするだけで使用できます。異なるプロジェクト間での共有も可能です。

**Q: フォーマットに制限はありますか？**

A: Read/Write Enabled が有効な任意のテクスチャに対応しています。PNG、TGA、EXR などに対応しています。

**Q: バッチ適用時にカラーゾーンも複数適用されますか？**

A: はい。バッチ適用時は、全てのカラーゾーン設定が対象テクスチャに適用されます。

**Q: RAM 使用量が増えるのでは？**

A: テクスチャをメモリ上で処理するため、大きなテクスチャではメモリ使用量が一時的に増えます。

---

## English

### Table of Contents

- [Installation](#installation)
- [Basic Usage](#basic-usage)
- [Color Zone Settings](#color-zone-settings)
- [Processing Presets](#processing-presets)
- [Preview Features](#preview-features)
- [Exclusion Mask](#exclusion-mask)
- [Batch Apply](#batch-apply)
- [Export](#export)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)

---

### Installation

#### Prerequisites

- Unity 2022.3.22f1 or later
- Target textures must have **Read/Write Enabled** activated

#### Installation Steps

1. **Open Unity Editor**
   - Prepare your project's Assets folder

2. **Import .unitypackage**
   - Select `Assets > Import Package > Custom Package...` from the menu
   - Choose the downloaded `.unitypackage` file
   - Click "Import" in the dialog
   - An `Assets/VACC` folder will be created

3. **Open the Window**
   - Select `Tools > VRC AvatarColorChanger` from the menu
   - The VACC window will open
   - Dock it in a convenient location

#### Enable Read/Write on Textures

If your texture doesn't support Read/Write Enabled:

1. Select the texture in VACC
2. A warning message will appear
3. Click the button to auto-enable it

---

### Basic Usage

#### Step 1: Select Texture

1. Click the "Texture" field in VACC
2. Unity's texture picker will open
3. Select the texture you want to recolor
4. It will appear in the preview

#### Step 2: Add Color Zone

1. Click "Add Color Zone"
2. A new zone is created with an auto-generated name

#### Step 3: Select Color Target

Two modes are available:

**Color Pick Mode (recommended):**
1. Select "Color Pick" in zone settings
2. Click "Sample Color"
3. Click the color in the preview
4. Adjust "Tolerance" to fine-tune the selection

**UV Rect Mode:**
1. Select "UV Rect" in zone settings
2. Specify Min/Max UV coordinates
3. More precise range control available

#### Step 4: Set Target Color

1. Click "Target Color" in zone settings
2. Choose a new color
3. Preview updates instantly

#### Step 5: Fine-tune (optional)

Adjust any or all of:

- **Pattern Preserve** — How much pattern to retain (0 = solid, 1 = full pattern)
- **Edge Softness** — Edge hardness (0 = hard, 1 = soft)
- **Saturation Filter** — Light color exclusion (0 = include all, 1 = vivid only)
- **Layer Index** — Priority when zones overlap (higher = earlier)

#### Step 6: Export

1. Click "Apply & Save"
2. Choose "Overwrite" or "Save As"
3. File is saved

---

### Color Zone Settings

#### Color Pick Mode

Auto-detects target pixels based on sample color and matching criteria.

**Settings:**

- **Sample Color** — Click to pick from preview; choose the most vivid color in the area

- **Tolerance** — Matching range (0.0 ～ 255.0)
  - Low: stricter matching (narrow selection)
  - High: broader matching (wider selection, more noise)
  - Recommended: 15～40

#### UV Rect Mode

Explicitly specify a rectangular region using UV coordinates.

**Settings:**

- **Min UV** — Top-left corner
- **Max UV** — Bottom-right corner

#### Common Settings

**Pattern Preserve**

Controls how much of the original brightness is retained.

- Near 0: Complete override (solid color)
- 0.5: Balanced
- Near 1: Original pattern nearly preserved

**Edge Softness**

Controls edge detection flexibility for blurred textures.

- 0 (hard): Sharp edge (normal textures)
- 0.5: Balanced
- 1 (soft): Blurred edge detection (soft textures)

**Saturation Filter**

Controls handling of "light colors" at texture edges.

- 0: Include light colors
- 0.5: Standard filtering
- 1: Vivid colors only

**Layer Index**

Controls application order when zones overlap.

- Higher values apply later (take priority)

---

### Processing Presets

Profiles for edge and noise processing.

#### Edge Feather

Gaussian blur on selection boundaries for smooth transitions.

- 0: Off
- 0.5 ～ 1.5: Standard blur
- 2.0+: Strong blur

#### Boundary Cleanup

Removes fine noise on blurred edges.

- 0: Off
- 1 ～ 2: Weak
- 3: Standard (default)
- 4 ～ 5: Strong

---

### Preview Features

#### Zoom

- **Ctrl + Scroll** — Zoom in/out
- **Right-drag** — Pan

#### View Modes

- **Normal** — Current result
- **Before/After** — Side-by-side comparison
- **Diff** — Highlight changed pixels

#### Auto-update

Updates automatically after setting changes (default: 0.5s delay).

---

### Exclusion Mask

Paint areas on the preview to exclude them from recoloring.

#### How to Use

1. Enable "Exclusion Mask"
2. Drag on preview to paint mask
3. Masked areas won't be recolored

#### Brush Settings

- **Brush Size** — Brush size
- **Opacity** — Brush opacity (0 ～ 1)

#### Undo

- **Ctrl + Z** — Undo last stroke
- **Ctrl + Y** — Redo

#### Clear

Click "Clear Mask" to remove all masks.

---

### Batch Apply

Apply the same zone settings to multiple textures at once.

#### Steps

1. Complete zones on a master texture
2. Open "Batch Apply" section
3. Add target textures
4. Click "Apply to All"

---

### Export

#### Save Methods

"Apply & Save" offers:

**Overwrite**
- Save over the original
- Backup strongly recommended

**Save As**
- Save as new file
- Choose filename
- Original protected

---

### Troubleshooting

#### Issue: Light colors or dots remain around edges

**Cause:** Antialiased/blurred edges create light colors.

**Solutions:**

1. **Increase Saturation Filter** (0.7 ～ 0.9)
   - Excludes light colors

2. **Narrow Tolerance**
   - Stricter color judgment

3. **Use Exclusion Mask**
   - Explicitly mask affected areas

#### Issue: Color bleeds outside boundaries

**Cause:** Saturation Filter too low or Tolerance too high.

**Solutions:**

1. **Increase Saturation Filter** (0.8 ～ 0.95)

2. **Narrow Tolerance**

3. **Adjust Edge Softness** (0.0 ～ 0.5)

#### Issue: Fine noise remains at boundaries

**Cause:** Saturation Filter too high or insufficient boundary processing.

**Solutions:**

1. **Lower Saturation Filter** (0.1 ～ 0.4)
   - Includes more colors (may bleed)

2. **Enable Boundary Cleanup** (3 or 5)

3. **Enable Edge Feather** (0.5 ～ 1.5)

4. **Increase Edge Softness** (0.3 ～ 0.7)

#### Issue: Jagged / hard boundaries

**Cause:** Insufficient edge processing.

**Solutions:**

1. **Enable Edge Feather** (0.5 ～ 1.5)

2. **Increase Edge Softness** (0.3 ～ 0.7)

3. **Lower Saturation Filter** (0.3 ～ 0.45)

#### Issue: Entire texture changes

**Cause:** Tolerance too high.

**Solutions:**

1. **Greatly narrow Tolerance** (start at 5～15)

2. **Resample Color** — Choose more specific color

3. **Switch to UV Rect Mode** — More precise control

#### Issue: Can't recolor to black

**Cause:** Black loses brightness info; Pattern Preserve less effective.

**Solutions:**

1. **Lower Pattern Preserve** (0 ～ 0.3)
   - Achieves flat black

2. **Set Edge Softness to 0**
   - Harder edge detection

3. **Use Exclusion Mask**
   - Protect important areas first

---

### FAQ

**Q: Can I combine multiple Color Zones?**

A: Yes. Add multiple zones and control priority with Layer Index.

**Q: Does it support PSD layer structures?**

A: No. This tool targets flattened textures (PNG, etc.). Use Photoshop for PSD files.

**Q: Is Undo supported?**

A: Yes for mask painting (Ctrl+Z). For texture changes, backup beforehand.

**Q: Can I use it across multiple projects?**

A: Yes. Import .unitypackage into each project.

**Q: Supported formats?**

A: Any texture with Read/Write Enabled (PNG, TGA, EXR, etc.)

**Q: Does Batch Apply include all Color Zones?**

A: Yes. All zones are applied to each target texture.

**Q: Does it increase RAM usage?**

A: Yes temporarily, as textures are processed in memory. Large textures may use significant RAM.
