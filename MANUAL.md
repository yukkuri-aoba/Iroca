# VRC AvatarColorChanger (VACC) ユーザーマニュアル

*[日本語](#日本語) | [English](#english)*

---

## 日本語

### 目次

- [インストール](#インストール)
- [基本的な使い方](#基本的な使い方)
- [カラーゾーンの設定](#カラーゾーンの設定)
- [加工設定](#加工設定)
- [詳細設定モード](#詳細設定モード)
- [プレビュー機能](#プレビュー機能)
- [除外マスク](#除外マスク)
- [プリセット](#プリセット)
- [書き出し](#書き出し)
- [トラブルシューティング](#トラブルシューティング)
- [よくある質問](#よくある質問)

---

### インストール

#### 前提条件

- Unity 2022.3.22f1 以降
- 対象テクスチャは **Read/Write Enabled** が有効である必要があります

> ウィンドウ起動後、警告のボタンを押すと自動で有効にできます。

#### 手順

1. [GitHub Releases](https://github.com/yukkuri-aoba/VRC_AvatarColorChanger/releases) から最新のVACCが入ったzipファイルをダウンロードし、展開します。

2. Unity Editor に`.unitypackage`をドラッグ＆ドロップします。

   [スクリーンショット: エクスプローラとUnity Editor]

3. ダイアログで「Import」をクリックします

   [スクリーンショット: Import ダイアログ]

4. 読み込みが完了すると、`Assets/VACC` フォルダが作成されます

5. `Tools > VRC AvatarColorChanger` を選択してウィンドウを開けば使用可能です！

   [スクリーンショット: Tools メニュー]

#### Read/Write Enabled の有効化

Read/Write Enabled が無効なテクスチャを選択すると、ウィンドウに警告とボタンが表示されます。ボタンをクリックすると自動で有効になります。

[スクリーンショット: Read/Write 警告と有効化ボタン]

---

### 基本的な使い方

#### ステップ 1: テクスチャを選択する

VACC ウィンドウの「Texture」欄をクリックして、色改変したいテクスチャを選択します。選択すると、プレビュー欄にテクスチャが表示されます。

[スクリーンショット: テクスチャ選択後のウィンドウ全体]

#### ステップ 2: カラーゾーンを追加する

「+ ゾーン追加」ボタンをクリックします。新しいカラーゾーンが作成されます。ゾーン名は自由に変更できます（処理には影響しません）。

#### ステップ 3: 色改変対象を選択する

カラーピック モードと UV 矩形 モードの 2 つがあります。

**カラーピック モード（推奨）**

1. ゾーン設定の「選択モード」を「ColorPick」に設定します
2. 「サンプルカラー」のカラーフィールドをクリックします
3. カラーピッカーが開くので、スポイトアイコンでプレビュー上の色をクリックします
4. 「許容範囲」スライダーを調整して選択範囲を微調整します

> 改変したい部分の中で最も鮮やかな色を選ぶとうまくいきやすいです。

**UV 矩形 モード**

1. ゾーン設定の「選択モード」を「Rect」に設定します
2. UV 座標（0〜1）で X / Y（左下が原点）と W / H（幅・高さ）を指定します

色情報に頼らず、正確な範囲指定が必要な場合に使います。

#### ステップ 4: 改変後の色を設定する

ゾーン設定の「Target Color」をクリックして、新しい色を選びます。プレビューに即座に反映されます。

#### ステップ 5: 各設定を調整する（必要に応じて）

| 設定 | 内容 |
|---|---|
| **模様保持 (Pattern Preserve)** | 元の柄の残し具合（0 = 単色、1 = 柄をそのまま保持） |
| **エッジ柔らかさ (Edge Softness)** | エッジの硬さ（0 = 硬い、1 = 柔らかい） |
| **彩度制限 (Saturation Strictness)** | 薄い色の除外度（0 = 除外なし、1 = 鮮やかな色のみ） |
| **ハイライト補助 (Highlight Recovery)** | 鏡面反射・光沢部分の変換漏れを防ぎます |
| **L (Layer Index)** | 複数ゾーン重複時の優先度（大きい値が後に適用されるため優先されます） |

#### ステップ 6: テクスチャを保存する

保存手順は「[書き出し](#書き出し)」を参照してください。

---

### カラーゾーンの設定

#### カラーピック モード

サンプル色との一致度に基づいて、改変対象のピクセルを自動で検出します。

**サンプルカラー (Sample Color)**

カラーフィールドをクリックすると Unity のカラーピッカーが開きます。ピッカー内のスポイトアイコンを使うと、プレビューや画面上の任意のピクセルから色を取得できます。

**許容範囲 (Tolerance)**

色の一致許容範囲です（0.0〜1.0）。推奨値は 0.15〜0.40 です。

| 値 | 効果 |
|---|---|
| 低い | 厳密に判定する（選択範囲が狭い） |
| 高い | 広く判定する（選択範囲が広い、ノイズが増える） |

#### UV 矩形 モード

UV 座標で矩形範囲を直接指定します。複雑な色構成のテクスチャに向いています。

| 設定 | 説明 |
|---|---|
| **X / Y** | 矩形の左下コーナーの UV 座標（0〜1） |
| **W / H** | 矩形の幅と高さ（UV 座標、0〜1） |

#### 共通設定

**模様保持 (Pattern Preserve)**

改変後のピクセルに元の明度をどの程度残すかを制御します。

- 0 に近い値：指定した色で完全に上書きします（単色）
- 0.5：元の柄の明度を 50% 保持します
- 1 に近い値：元の柄をほぼそのまま保持します

**エッジ柔らかさ (Edge Softness)**

選択エッジ判定の柔軟性を制御します。ぼかし処理のあるテクスチャに対応します。

- 0（硬い）：エッジをシャープに判定します（通常のテクスチャ向け）
- 0.5：バランス型
- 1（柔らかい）：ぼやけたエッジでも判定します（ぼかしのあるテクスチャ向け）

**彩度制限 (Saturation Strictness)**

山テクスチャ周辺の薄い色（低彩度の色）を対象に含めるかを調整します。

| 値 | 効果 |
|---|---|
| 0 | 薄い色も対象に含めます |
| 0.5（デフォルト） | 中程度の絞り込み |
| 1 | 鮮やかな色のみを対象にします |

> テクスチャ周辺はぼかし処理で薄い色になりがちです。値を上げると周辺を無視でき、元の色のドットが残るのを抑えられます。

**ハイライト補助 (Highlight Recovery)**

高明度・低彩度のハイライト領域（鏡面反射や光沢部分）も色改変の対象に含めます。

- ON（デフォルト）：ハイライト領域の変換漏れを防ぎます
- OFF：ハイライト領域を厳密に除外したい場合に使います

**シャドウ・ハイライト詳細設定**

暗部やグレーの扱いを細かく制御します。

| 設定 | 説明 | デフォルト |
|---|---|---|
| **シャドウ彩度低下 (Shadow Desaturation)** | 暗いピクセルの彩度を落とす明度の閾値。低い値にすると暗い色も鮮やかに染まります | 0.35 |
| **シャドウ巻き込み最低彩度 (Shadow Forgiveness Sat Min)** | 暗いピクセルを影として巻き込むために必要な最低彩度。純粋なグレー・黒が色付けされるのを防ぎます | 0.05 |
| **自動無彩色判定 (Auto Grayscale Threshold)** | サンプル色の彩度がこの値以下の場合、色相を無視して純粋な無彩色（黒・グレー）として処理します | 0.05 |

**L（レイヤーインデックス）**

ゾーンヘッダーの `L` 欄で指定する整数値です。複数のゾーンが重なる場合の適用順を制御します。

- 値が小さいゾーンから順に処理されます
- 大きい値のゾーンが後から適用されるため、上書きされます（優先されます）
- 同じ値の場合は追加した順に適用されます

**Target Color（改変後の色）**

改変後の色を指定します。

---

### 加工設定

エッジやノイズの処理を調整する設定です。設定の変更はプレビューに自動で反映されます。最終的には「Apply & Save」で適用・保存します。

#### エッジぼかし (Edge Feather)

選択エッジにぼかし処理を適用して、滑らかな色の遷移を実現します。

| 値 | 効果 |
|---|---|
| 0 | オフ（エッジがシャープ） |
| 0.5〜1.5 | 標準的なぼかし |
| 2.0 以上 | 強いぼかし（滑らかなテクスチャ向け） |

#### AA 境界クリーンアップ (AA Edge Cleanup)

アンチエイリアス境界に残った細かいノイズを除去するパス数です。

| 値 | 効果 |
|---|---|
| 0 | オフ |
| 1〜2 | 弱いクリーンアップ |
| 3 | 標準（推奨） |
| 4〜5 | 強いクリーンアップ |

#### 境界クリーンアップ（α 分解）

アンチエイリアス境界で α 分解と再合成を行い、境界に発生する薄汚れた色（ハロー効果）を防ぎます。

- ON（デフォルト）：推奨。AA 境界の色汚染を防止します。
- OFF：従来のクリーンアップのみ使用します。

---

### アドバンスモード

加工設定セクションの「アドバンスモード」トグルを有効にすると、アルゴリズムの内部パラメータを調整できます。通常のテクスチャではデフォルト値のままで問題ありません。思い通りの結果が出ない場合にのみ使用してください。

アドバンスモードを有効にすると、ゾーン設定にも追加項目が表示されます。

#### ゾーンごとのアドバンスパラメータ

| 設定 | 説明 | デフォルト |
|---|---|---|
| **明度重み (Value Weight)** | 距離計算における明度の重み。高い値は明度差に敏感になります。低い値は同じ素材の影・ハイライトを吸収します | 1.0 |
| **彩度距離重み (Sat Distance Weight)** | 彩度距離の重み | 0.15 |
| **彩度ランプスケール (Sat Ramp Scale)** | 動的彩度ランプのスケール。大きい値は彩度閾値付近でなだらかにフェードします | 0.10 |

#### 加工設定のアドバンスパラメータ

| 設定 | 説明 | デフォルト |
|---|---|---|
| **穴埋めパス数 (Hole Fill Passes)** | AA 境界の孤立ドット除去のパス数 | 5 |
| **穴埋め最小隣接数 (Hole Fill Min Neighbors)** | 穴埋めに必要な一致隣接ピクセル数。低い値はより積極的に埋めます | 4 |
| **境界復元 彩度最小 (Boundary Sat Min)** | 境界復元時の彩度最小閾値 | 0.02 |
| **境界復元 彩度ランプ (Boundary Sat Ramp)** | 境界復元時の彩度ランプ幅 | 0.08 |
| **α 分解 近傍半径 (Decontamination Radius)** | 境界クリーンアップ（α 分解）で背景色を推定する近傍ピクセルの半径 | 4 |

> アドバンスモードの設定はプリセットに含めて保存・読み込みできます。うまく機能する組み合わせが見つかったら、プリセットとして保存しておくと便利です。

---

### プレビュー機能

#### ズーム操作

- **Ctrl + スクロール** — ズームイン・アウト
- **ドラッグ** — ビューを移動（ズーム 1 倍超のときのみ有効）

#### 表示モード

- **Normal** — 現在の処理結果を表示します
- **前後比較** — 変更前後を左右に並べて表示します
- **差分表示** — 変更されたピクセルのみを強調表示します

#### 自動更新

設定変更後、短い遅延（デフォルト：0.2 秒）で自動的に更新されます。

---

### 除外マスク

プレビュー上でブラシを使って、色改変したくない領域を指定します。共通マスク（全ゾーンに適用）とゾーン別マスク（特定ゾーンにのみ適用）を使い分けられます。

[スクリーンショット: 除外マスクを描いた状態のプレビュー]

#### 使い方

1. 「マスク対象」プルダウンで編集対象を選択します（共通または各ゾーン）
2. 「除外」ボタンをクリックして描画モードを開始します
3. プレビュー上でドラッグしてマスクを描きます（赤い重ね表示 = 共通、黄色系 = ゾーン別）
4. マスクした部分は色改変されません
5. 「除外」ボタンを再度クリックすると描画モードを解除します

#### マスク対象

- **共通マスク** — 全ゾーンに適用されるマスクです
- **ゾーン別マスク** — プルダウンでゾーン名を選択すると、そのゾーンにのみ適用されるマスクを編集できます

#### ブラシ設定

- **ブラシサイズ (Brush Size)** — ブラシの大きさを 1〜64 で指定します

#### 描画モード

- **除外** ボタン — クリックで描画モードを開始します（マスクを追加します）。再クリックで解除します
- **含める** ボタン — クリックで消去モードを開始します（マスクを削除します）。再クリックで解除します

#### 取り消しとリセット

- **Ctrl+Z** — 直前のストロークを取り消します（Unity 標準の Undo に対応しています）
- **マスクをクリア** ボタン — 現在選択中の対象のマスクをすべて削除します

---

### プリセット

カラーゾーン設定や加工設定をプリセットとして保存・読み込みできます。

#### 保存と読み込み

1. 「プリセット」セクションを開きます
2. 保存先を選びます（「プロジェクト内」または「ユーザー共通」）
3. プリセット名を入力して「保存」をクリックします
4. 一覧から「読込」で設定を読み込み、「×」で削除します

#### マスクの保存・読み込みオプション

- **マスクを含める** — ON にすると除外マスクもプリセットに含めて保存します
- **読込時にマスクも適用** — ON にするとプリセット読み込み時にマスクも同時に復元します

#### JSON の書き出し・読み込み

「JSON 書き出し」「JSON 読み込み」ボタンで設定を外部ファイルとして共有できます。

---

### エクスポート

「新規ファイルとして保存」トグルで保存方法を選択してから「Apply & Save」ボタンをクリックします。

#### 保存方法の選択

**「新規ファイルとして保存」ON**

元のテクスチャを保持したまま、別のファイルに保存します。ファイル名を指定できます。

**「新規ファイルとして保存」OFF**

元のテクスチャファイルを上書き保存します。バックアップを強く推奨します。

#### その他のオプション

**インポート設定を継承**

ON（デフォルト）にすると、新しく生成されたテクスチャが元のテクスチャのインポート設定を自動で引き継ぎます。

**「フォルダを開く」ボタン**

保存されたテクスチャが入っているフォルダをファイルエクスプローラで開きます。

---

### トラブルシューティング

#### 図形の周りに薄い色やドットが残る

**原因**

テクスチャの周辺部分（アンチエイリアスやぼかし処理がされた箇所）は薄い色になっています。この薄い色が改変されずに残ることがあります。

**対策**

1. **彩度制限を高く設定する（推奨）**
   - 0.7〜0.9 あたりから試します
   - 薄い色を除外するため、周辺部分を無視できます

2. **許容範囲を狭める**
   - 混ざった色を厳密に除外します

3. **除外マスクを使う**
   - 残ってしまう部分を直接マスクします

---

#### 色がはみ出す

**原因**

彩度制限が低すぎるか、許容範囲が広すぎます。

**対策**

1. **彩度制限を高く設定する（推奨：0.8〜0.95）**
2. **許容範囲を狭める**
3. **エッジ柔らかさを調整する**（0.0〜0.5 で試します）

---

#### 境界に細かいノイズが残る

**原因**

彩度制限が高すぎるか、境界処理が不足しています。

**対策**

1. **彩度制限を低めに設定する（0.1〜0.4）**
   - より多くの薄い色を対象にします（ただし色がはみ出しやすくなります）

2. **AA 境界クリーンアップを有効にする**
   - 0 → 3 に設定するか、3 → 5 に増やします

3. **エッジぼかしを有効にする**
   - 0.5〜1.5 程度から始めます

4. **エッジ柔らかさを上げる**
   - 0.3〜0.7 で試します

---

#### 境界がギザギザしている・硬い

**原因**

エッジ処理が不足しています。

**対策**

1. **エッジぼかしを有効にする（推奨）**
   - 0.5〜1.5 程度から始めます

2. **エッジ柔らかさを上げる**
   - 0.3〜0.7 で試します

3. **彩度制限を少し下げる**
   - 0.3〜0.45 で試します（境界部分のピクセルを拾いやすくなります）

---

#### テクスチャ全体が変わってしまう

**原因**

許容範囲が広すぎます。

**対策**

1. **許容範囲を大幅に狭める**（0.05〜0.15 程度から始めます）
2. **サンプルカラーを選び直す**（より限定的な色を選びます）
3. **UV 矩形 モードへの切り替えを検討する**（正確な範囲指定が可能です）

---

#### 黒い色に変更できない

**原因**

黒色は明度情報がほぼないため、模様保持の効果が適用しにくいです。

**対策**

1. **模様保持を低く設定する**（0〜0.3 あたりで試します）
2. **エッジ柔らかさを 0 に設定する**
3. **除外マスクを活用する**（保護したい部分を先にマスクします）

---

### よくある質問

**Q: 複数のカラーゾーンを組み合わせられますか？**

はい。「+ ゾーン追加」で複数ゾーンを追加できます。レイヤーインデックスで優先度を制御できます。

**Q: PSD のレイヤー構造をサポートしていますか？**

いいえ。本ツールは PNG などの統合済みテクスチャを対象としています。PSD がある場合は、そちらを直接編集することをお勧めします。

**Q: Undo は対応していますか？**

除外マスクの描画は Ctrl+Z で取り消せます。Unity 標準の Undo に統合されています。テクスチャへの色適用自体は元に戻せないため、事前にバックアップをお勧めします。

**Q: 複数のプロジェクトで使えますか？**

はい。`.unitypackage` を各プロジェクトに読み込むだけで使えます。「ユーザー共通」の保存先を選ぶと、プロジェクト間で設定を共有できます。

**Q: 対応しているファイル形式は？**

入力は **PNG / JPG** です。出力は常に **PNG** で保存されます。TGA、EXR、PSD などは現在対応していません。Unity 上で **Read/Write Enabled** を有効にする必要があります。

**Q: 大きなテクスチャでも使えますか？**

使えます。ただし、処理はメモり上で行うため、大きなテクスチャでは一時的にメモリ使用量が増えます。

---

## English

### Table of Contents

- [Installation](#installation)
- [Basic Usage](#basic-usage)
- [Color Zone Settings](#color-zone-settings)
- [Processing Settings](#processing-settings)
- [Advanced Mode](#advanced-mode-1)
- [Preview Features](#preview-features-1)
- [Exclusion Mask](#exclusion-mask-1)
- [Presets](#presets-1)
- [Export](#export)
- [Troubleshooting](#troubleshooting-1)
- [FAQ](#faq)

---

### Installation

#### Prerequisites

- Unity 2022.3.22f1 or later
- Target textures must have **Read/Write Enabled** activated

> Select a texture in the VACC window and click the button shown in the warning to enable it automatically.

#### Steps

1. Download the latest `.unitypackage` from [GitHub Releases](https://github.com/yukkuri-aoba/VRC_AvatarColorChanger/releases)
2. In Unity Editor, select `Assets > Import Package > Custom Package...`
3. Choose the downloaded file and click "Import" in the dialog
4. Open the window via `Tools > VRC AvatarColorChanger`

#### Enable Read/Write on Textures

If a texture does not have Read/Write Enabled, a warning and a button will appear in the window. Click the button to enable it automatically.

---

### Basic Usage

#### Step 1: Select a Texture

Click the "Texture" field in the VACC window. A texture picker will open. Select the texture you want to recolor. It will appear in the preview.

#### Step 2: Add a Color Zone

Click "+ Add Zone". A new color zone is created. You can rename it freely (the name does not affect processing).

#### Step 3: Select the Recolor Target

Two modes are available.

**Color Pick Mode (recommended)**

1. Set "Selection Mode" to "ColorPick" in zone settings
2. Click the "Sample Color" field
3. Use the eyedropper icon in the color picker to sample a color from the preview
4. Adjust "Tolerance" to fine-tune the selection range

> Pick the most vivid color in the area you want to recolor for best results.

**UV Rect Mode**

1. Set "Selection Mode" to "Rect" in zone settings
2. Specify X/Y (bottom-left origin) and W/H (width/height) using UV coordinates (0–1)

Use this mode for precise range control without relying on color detection.

#### Step 4: Set the Target Color

Click "Target Color" in zone settings and choose the new color. The preview updates instantly.

#### Step 5: Adjust settings (optional)

| Setting | Description |
|---|---|
| **Pattern Preserve** | How much of the original pattern to retain (0 = solid color, 1 = full pattern) |
| **Edge Softness** | Edge detection flexibility (0 = hard, 1 = soft) |
| **Saturation Strictness** | Exclusion of light colors (0 = include all, 1 = vivid only) |
| **Highlight Recovery** | Prevents missed recoloring on reflective/glossy areas |
| **L (Layer Index)** | Priority when zones overlap (higher = applied later = takes priority) |

#### Step 6: Save the texture

See the [Export](#export) section for details.

---

### Color Zone Settings

#### Color Pick Mode

Auto-detects target pixels based on the sample color and matching criteria.

**Sample Color**

Click the color field to open Unity's color picker. Use the built-in eyedropper icon to sample a color from the preview or any on-screen pixel.

**Tolerance**

The color matching range (0.0–1.0). Recommended: 0.15–0.40.

| Value | Effect |
|---|---|
| Low | Stricter matching (narrower selection) |
| High | Broader matching (wider selection, more noise) |

#### UV Rect Mode

Explicitly specify a rectangular region using UV coordinates.

| Setting | Description |
|---|---|
| **X / Y** | Bottom-left corner of the rectangle in UV coordinates (0–1) |
| **W / H** | Width and height of the rectangle in UV coordinates (0–1) |

#### Common Settings

**Pattern Preserve**

Controls how much of the original brightness is retained after recoloring.

- Near 0: Complete override (solid color)
- 0.5: Balanced
- Near 1: Original pattern nearly preserved

**Edge Softness**

Controls edge detection flexibility for blurred textures.

- 0 (hard): Sharp edge detection (normal textures)
- 0.5: Balanced
- 1 (soft): Handles blurred edges (soft/blurred textures)

**Saturation Strictness**

Controls whether light-colored (low-saturation) pixels at texture edges are included.

| Value | Effect |
|---|---|
| 0 | Include light colors |
| 0.5 (default) | Standard filtering |
| 1 | Vivid colors only |

**Highlight Recovery**

Matches high-brightness, low-saturation highlight regions to prevent missed recoloring on reflective/glossy surfaces.

- ON (default): Prevents recoloring gaps in highlight areas
- OFF: Use when you want to strictly exclude highlights

**Shadow/Highlight Details**

Fine-grained control over dark and desaturated pixel handling.

| Setting | Description | Default |
|---|---|---|
| **Shadow Desaturation** | Brightness threshold below which dark pixels lose saturation. Lower values allow darker colors to be recolored more vividly. | 0.35 |
| **Shadow Forgiveness Sat Min** | Minimum saturation required to include dark pixels as shadow. Prevents pure grey/black from being colorized. | 0.05 |
| **Auto Grayscale Threshold** | If sample saturation is below this value, hue is ignored and the zone treats pixels as pure grayscale (black/grey). | 0.05 |

**L (Layer Index)**

An integer set in the `L` field of the zone header. Controls application order when zones overlap.

- Zones are processed in ascending order
- Higher values apply later and overwrite lower ones (take priority)
- Zones with the same index are applied in the order they were added

**Target Color**

The color to apply after recoloring.

---

### Processing Settings

Settings for edge and noise processing. Changes are reflected in the preview automatically and saved when you click "Apply & Save".

#### Edge Feather

Applies blur to selection boundaries for smooth color transitions.

| Value | Effect |
|---|---|
| 0 | Off (sharp edges) |
| 0.5–1.5 | Standard blur |
| 2.0+ | Strong blur |

#### AA Edge Cleanup

Number of passes to remove fine noise at anti-aliased boundaries.

| Value | Effect |
|---|---|
| 0 | Off |
| 1–2 | Weak |
| 3 | Standard (recommended) |
| 4–5 | Strong |

#### Edge Decontamination

Rebuilds AA boundary pixels via alpha decomposition and recomposition to prevent halo-like muddy colors at edges.

- ON (default): Recommended. Prevents color contamination at AA boundaries.
- OFF: Use legacy cleanup only.

---

### Advanced Mode

Enable the "Advanced Mode" toggle in the Processing section to access internal processing values. Default values work well for most textures. Use this only when the standard settings cannot achieve the desired result.

Enabling this mode also reveals additional per-zone values.

#### Per-zone Values

| Setting | Description | Default |
|---|---|---|
| **Value Weight** | Brightness weight in color distance calculation. Higher = sensitive to brightness differences. Lower = tolerates shadow/highlight variation. | 1.0 |
| **Sat Distance Weight** | Saturation distance weight. | 0.15 |
| **Sat Ramp Scale** | Controls fade smoothness near the saturation threshold. Larger = more gradual. | 0.10 |

#### Processing Values

| Setting | Description | Default |
|---|---|---|
| **Hole Fill Passes** | Passes to fill isolated dots at anti-aliased edges. | 5 |
| **Hole Fill Min Neighbors** | Minimum matched neighbors to fill a hole. Lower = more aggressive. | 4 |
| **Boundary Sat Min** | Minimum saturation threshold for boundary recovery. | 0.02 |
| **Boundary Sat Ramp** | Saturation ramp width for boundary recovery. | 0.08 |
| **Decontamination Radius** | Neighborhood radius used to estimate background color for Edge Decontamination. | 4 |

> Advanced Mode values are saved and loaded with presets. Once you find a combination that works well, save it as a preset.

---

### Preview Features

#### Zoom

- **Ctrl + Scroll** — Zoom in/out
- **Drag** — Pan the view (available only when zoom > 1x)

#### View Modes

- **Normal** — Shows the current processing result
- **Compare** — Side-by-side before/after comparison
- **Diff** — Highlights changed pixels

#### Auto-update

Updates automatically after setting changes (default: 0.2s delay).

---

### Exclusion Mask

Paint areas on the preview to exclude them from recoloring. Supports both a common mask (applied to all zones) and per-zone masks.

#### How to Use

1. Select the mask target from the "Mask Target" dropdown (Common or a specific zone)
2. Click "Exclude" to enter paint mode
3. Drag on the preview to paint the exclusion mask (red overlay = common, colored overlay = zone-specific)
4. Painted areas will not be recolored
5. Click "Exclude" again to exit paint mode

#### Mask Target

- **Common Mask** — Applied to all zones
- **Zone-specific Mask** — Select a zone name from the dropdown to edit a mask that applies only to that zone

#### Brush Settings

- **Brush Size** — Brush size (1–64)

#### Brush Modes

- **Exclude** button — Click to start paint mode (adds mask). Click again to stop.
- **Include** button — Click to start erase mode (removes mask). Click again to stop.

#### Undo and Reset

- **Ctrl+Z** — Undo the last brush stroke (integrated with Unity's standard Undo)
- **Clear Mask** button — Removes all masks for the currently selected mask target

---

### Presets

Save and load color zone and processing settings as presets.

#### Save and Load

1. Open the "Presets" section
2. Select a storage location ("In-Project" or "User Common")
3. Enter a preset name and click "Save"
4. Click "Load" to restore a preset, or "×" to delete it

#### Mask Options

- **Include Mask** — When ON, the exclusion mask is saved with the preset
- **Apply Mask on Load** — When ON, the saved mask is restored when loading

#### JSON Export / Import

Use the "JSON Export" and "JSON Import" buttons to share settings as external files.

---

### Export

Choose a save method with the "Save as new file" toggle, then click "Apply & Save".

**"Save as new file" ON**

Saves to a new file while keeping the original texture intact. You can specify the filename.

**"Save as new file" OFF**

Overwrites the original texture file. Back up the original before proceeding.

#### Other Options

**Inherit Import Settings**

When ON (default), the new texture automatically inherits the original texture's import settings.

**"Open Folder" button**

Opens the folder containing the saved texture in the file explorer.

---

### Troubleshooting

#### Faint colors or stray dots remain around shapes

**Cause**

Texture edges affected by anti-aliasing or blur are rendered as light colors. These can be left unrecolored.

**Solutions**

1. **Increase Saturation Strictness (recommended)** — Try 0.7–0.9. Light colors are excluded from recoloring.
2. **Decrease Tolerance** — Stricter matching excludes mixed colors.
3. **Use the Exclusion Mask** — Manually mask the remaining areas.

---

#### Color bleeds outside the intended area

**Cause**

Saturation Strictness is too low or Tolerance is too high.

**Solutions**

1. **Increase Saturation Strictness (recommended: 0.8–0.95)**
2. **Decrease Tolerance**
3. **Adjust Edge Softness** (try 0.0–0.5)

---

#### Fine noise remains at boundaries

**Cause**

Saturation Strictness is too high or boundary processing is insufficient.

**Solutions**

1. **Lower Saturation Strictness (try 0.1–0.4)** — Includes more light-colored pixels (may increase bleed slightly)
2. **Increase AA Edge Cleanup** — Set from 0 → 3, or increase from 3 → 5
3. **Enable Edge Feather** — Start at 0.5–1.5
4. **Increase Edge Softness** — Try 0.3–0.7

---

#### Boundaries appear jagged or hard

**Cause**

Edge processing is insufficient.

**Solutions**

1. **Enable Edge Feather (recommended)** — Start at 0.5–1.5
2. **Increase Edge Softness** — Try 0.3–0.7
3. **Slightly lower Saturation Strictness** — Try 0.3–0.45 to include more boundary pixels

---

#### The entire texture is recolored

**Cause**

Tolerance is too high.

**Solutions**

1. **Lower Tolerance significantly** (start around 0.05–0.15)
2. **Re-select Sample Color** (choose a more specific color)
3. **Switch to UV Rect Mode** (allows precise range specification)

---

#### Cannot change to black

**Cause**

Black has almost no brightness information, making Pattern Preserve difficult to apply.

**Solutions**

1. **Lower Pattern Preserve** (try 0–0.3)
2. **Set Edge Softness to 0**
3. **Use the Exclusion Mask** to protect areas you do not want changed

---

### FAQ

**Q: Can I use multiple color zones together?**

Yes. Click "+ Add Zone" to add multiple zones. Use Layer Index to control priority.

**Q: Does this support PSD layer structures?**

No. This tool targets merged textures such as PNG files. If you have a PSD file, editing it directly is recommended.

**Q: Is Undo supported?**

Brush strokes in the Exclusion Mask support Ctrl+Z (integrated with Unity's standard Undo). Applying color changes to a texture cannot be undone. Keep a backup of the original.

**Q: Can I use this in multiple projects?**

Yes. Import the `.unitypackage` into each project. Use the "User Common" preset location to share settings across projects.

**Q: What file formats are supported?**

Input: **PNG / JPG**. Output: always **PNG**. TGA, EXR, and PSD are not supported. The texture must also have **Read/Write Enabled** set in Unity.

**Q: Does it work with large textures?**

Yes. Processing happens in memory, so large textures will temporarily increase RAM usage.
