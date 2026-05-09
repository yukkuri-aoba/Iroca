# VRC AvatarColorChanger (VACC) ユーザーマニュアル

*[日本語](#日本語) | [English](#english)*

---

## 日本語

### 目次

- [インストール](#インストール)
- [基本的な使い方](#基本的な使い方)
- [カラーゾーンの詳しい説明](#カラーゾーンの詳しい説明)
- [加工設定](#加工設定)
- [アドバンスモード](#アドバンスモード)
- [プレビュー機能](#プレビュー機能)
- [除外マスク](#除外マスク)
- [プリセット](#プリセット)
- [一括適用](#一括適用)
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

1. ウィンドウの「+ ゾーン追加」ボタンをクリック
2. 新しいカラーゾーンが作成されます
3. 各ゾーンには名前を自由に付けられます（処理には影響しません）

#### ステップ 3: 色改変対象を選択

カラーピック モードとUV矩形 モードの2つがあります。

**カラーピック モード（推奨）：**
1. ゾーン設定の「選択モード」を「ColorPick」に設定
2. 「サンプルカラー」のカラーフィールドをクリック
3. カラーピッカーが開くので、スポイト（Eyedropper）アイコンでプレビューから改変したい色をクリック
4. 「許容範囲」スライダーを調整して、選択範囲を微調整

**UV矩形 モード：**
1. ゾーン設定の「選択モード」を「Rect」に設定
2. UV座標（0〜1）で X / Y（左下原点）と W / H（幅・高さ）を指定
3. 色情報に頼らず正確な範囲指定が可能です

#### ステップ 4: 色を設定

1. ゾーン設定の「Target Color」をクリック
2. カラーピッカーで新しい色を選択
3. プレビューに反映されます

#### ステップ 5: 詳細を調整（必要に応じて）

以下の設定のいずれかまたはすべてを調整します：

- **模様保持 (Pattern Preserve)** — 元の柄の残し具合（0 = 単色、1 = 柄がそのまま）
- **エッジ柔らかさ (Edge Softness)** — エッジの硬さ（0 = 硬い、1 = 柔らかい）
- **彩度制限 (Saturation Strictness)** — 薄い色の除外度（0 = 除外なし、1 = 鮮やかな色のみ）
- **ハイライト補助 (Highlight Recovery)** — 鏡面反射/光沢部分の色変換漏れを防ぐ
- **シャドウ・ハイライト詳細設定** — 暗部の巻き込み制御・無彩色自動判定（アドバンス設定参照）
- **L (Layer Index)** — 複数ゾーン重複時の優先度（大きい値が後に適用・優先）

#### ステップ 6: エクスポート

1. ウィンドウの「新規ファイルとして保存」トグルで保存方法を選択
   - ON: 元のファイルを保持して新規ファイルとして保存（ファイル名を入力）
   - OFF: 元のテクスチャを上書き保存
2. 「Apply & Save」ボタンをクリック
3. ファイルが保存されます

---

### カラーゾーンの詳しい説明

#### カラーピック モード

サンプル色と合致度に基づいて自動的に改変対象を検出します。

**設定項目：**

- **サンプルカラー (Sample Color)**
  - カラーフィールドをクリックすると Unity のカラーピッカーが開きます
  - ピッカー内のスポイト (Eyedropper) アイコンを使うと、プレビューや画面上の任意のピクセルから色をサンプリングできます
  - 改変したい部分の中で最も鮮やかな色を選ぶとうまくいきやすい

- **許容範囲 (Tolerance)**
  - 色の合致許容範囲（0.0 ～ 1.0）
  - 低い値：より厳密に色を判定（選択範囲が狭い）
  - 高い値：より広く色を判定（選択範囲が広い、ノイズが増える傾向）
  - 推奨値：0.15 ～ 0.40（テクスチャに応じて調整）
- **連続領域モード (Flood Fill)**
  - カラーピック時に表示されるトグル
  - 有効にすると、プレビュー上をクリックして「シード点」を指定できます
  - シード点からエッジで境界されるまでを自動追跡し、まとまった領域のみを改変対象にできます
  - エッジストッパー強度 (Edge Stop Threshold): 領域の境界とみなす色差の閖値（アドバンスモード）
#### UV矩形 モード

UV座標で矩形範囲を明示的に指定します。複雑な色構成のテクスチャに向いています。

**設定項目：**

- **X / Y** — 矩形の左下コーナーのUV座標（0〜1）
- **W / H** — 矩形の幅と高さ（UV座標、0〜1）

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

**彩度制限 (Saturation Strictness)**

テクスチャの周辺に存在する「薄い色（低彩度色）」をどう扱うかを制御します。

- **0**（含める） — 薄い色も対象に含める
- **0.5**（標準・デフォルト） — 中程度のフィルタリング
- **1**（除外） — 鮮やかな色のみを対象

> **注：** テクスチャの周辺部分はぼかし処理などがされていることが多く、薄い色になっています。この設定を0に近くすると、元の色のドットが残るのを抑制できます。

**詳しくは「トラブルシューティング」を参照してください。**

**Highlight Recovery（ハイライト補助）**

高明度・低彩度のハイライト領域（鏡面反射や光沢部分）を補助的にマッチします。

- **ON**（デフォルト） — ハイライト領域の色変換漏れを防ぐ
- **OFF** — 厳密にハイライト領域を除外したい場合に使用

**シャドウ・ハイライト詳細設定**

暗部やグレーの取り扱いを細かく制御するセクションです。

- **シャドウ彩度低下 (Shadow Desaturation)** — 暗いピクセルの彩度を落とす明度閖値。低い値にすると暗い色も鮮やかに染まります。デフォルト: 0.35
- **シャドウ巻き込み最低彩度 (Shadow Forgiveness Sat Min)** — 暗いピクセルを影各として巻き込むために必要な最低彩度。純粋なグレー/黒が色付けされるのを防ぐ。デフォルト: 0.05
- **自動無彩色判定 (Auto Grayscale Threshold)** — サンプル色の彩度がこの値以下の場合、色相を無視して純粋な無彩色（黒/グレー）として高精度に投圖します。デフォルト: 0.05

**L（Layer Index：レイヤーインデックス）**

ゾーンヘッダーの `L` 欄で指定する整数値。複数のカラーゾーンが重なる場合の適用順を制御します。

- 値が小さいゾーンから順に処理されます
- 大きい値のゾーンが後から適用されるため、上書き優先となります
- 同じ値の場合は追加された順に適用

#### Target Color（対象色）

改変後の色を指定します。

---

### 加工設定

エッジやノイズの処理を調整する設定です（「加工設定」セクションにあります）。
設定変更はプレビューに自動反映されます。最終的には「Apply & Save」で適用・保存されます。

#### Edge Feather（エッジぼかし）

選択エッジに Gaussian Blur を適用して滑らかな色の遷移を実現します。

- **0** — オフ（エッジがシャープ）
- **0.5 ～ 1.5** — 標準的なぼかし
- **2.0 以上** — 強いぼかし（滑らかなテクスチャ向け）

#### AA境界クリーンアップ（AA Edge Cleanup）

アンチエイリアス境界に残った細かいノイズを除去するパス数です。

- **0** — オフ
- **1 ～ 2** — 弱いクリーンアップ
- **3**（標準） — 標準クリーンアップ（推奨）
- **4 ～ 5** — 強いクリーンアップ（より多くのノイズを除去）

#### 境界クリーンアップ（α分解）

AA境界で α 分解＋再合成を行い、薄汚れた中間色（ハロー効果）の発生を構造的に防げます。

- **ON**（デフォルト） — 推奨。AA境界の色汚染を防止する。
- **OFF** — 従来のクリーンアップのみ使用

---

### アドバンスモード

加工設定セクションの「アドバンスモード」トグルを有効にすると、アルゴリズムの内部パラメータを微調整できます。通常のテクスチャではデフォルト値のままで十分ですが、特殊なテクスチャで思い通りの結果が出ない場合に使用してください。

アドバンスモードを有効にするとゾーン設定にも以下の追加項目が表示されます。

#### ゾーンごとのアドバンスパラメータ

- **明度重み (Value Weight)** — 距離計算における明度（V）の重み。デフォルト: 1.0
  - 高い値：明度差に敏感（異なる素材をより分離しやすい）
  - 低い値：明度差を許容（同じ素材の影/ハイライト変動を吸収）
- **彩度距離重み (Sat Distance Weight)** — 彩度距離の重み。デフォルト: 0.15
- **彩度ランプスケール (Sat Ramp Scale)** — 動的彩度ランプのスケール。デフォルト: 0.10
  - 大きい値：彩度閾値付近で段階的なフェードイン
  - 小さい値：より急激な閾値

#### 加工設定のアドバンスパラメータ

- **穴埋めパス数 (Hole Fill Passes)** — AA境界の孤立ドット除去のパス数。デフォルト: 5
- **穴埋め最小隣接数 (Hole Fill Min Neighbors)** — 穴埋めに必要なマッチ隣接ピクセル数。デフォルト: 4
  - 低い値：より積極的に穴を埋める（過剰に埋める可能性）
  - 高い値：より保守的
- **境界復元 彩度最小 (Boundary Sat Min)** — 境界復元時の彩度最小鎖値。デフォルト: 0.02
- **境界復元 彩度ランプ (Boundary Sat Ramp)** — 境界復元時の彩度ランプ幅。デフォルト: 0.08
- **α分解 近傍半径 (Decontamination Radius)** — 境界クリーンアップ（α分解）で背景色を推定する近傍ピクセルの半径。デフォルト: 4

> **ヒント：** アドバンスモードの設定はプリセットとして保存・読込できます。うまく機能する組み合わせを見つけたら、プリセットとして保存しておくと便利です。

---

### プレビュー機能

#### ズーム

- **Ctrl + スクロール** — ズームイン/アウト
- **ドラッグ** — ビューをパン（移動）（ズーム1倍超の時のみ有効）

#### ビューモード

- **Normal**（前後比較/差分表示OFFの状態） — 現在の処理結果をプレビュー
- **前後比較** — 変更前後を左右に並べて表示
- **差分表示** — 変更されたピクセルのみをハイライト表示

#### プレビューの自動更新

設定変更後、短い遅延（デフォルト: 0.2秒）で自動的に更新されます。

---

### 除外マスク

プレビュー上でブラシを使用して、色改変したくない領域をマスクします。共通マスク（全ゾーンに適用）とゾーン別マスク（特定ゾーンのみ適用）の両方を使い分けられます。

#### 使い方

1. 「マスク対象」プルダウンで編集対象を選択（共通または各ゾーン）
2. 「除外」ボタンをクリックしてペイントモードを開始
3. プレビュー上でドラッグしてマスクを描画（赤い叠り = 共通、黄色系 = ゾーン別）
4. マスクされた部分は色改変されません
5. 「除外」ボタンを再度クリックするとペイントモードを解除

#### マスク対象

- **共通マスク** — 全ゾーンに適用されるマスク。デフォルトの選択肢。
- **ゾーン別マスク** — プルダウンでゾーン名を選択するとそのゾーンにのみ適用されるマスクを編集できます。

#### ブラシ設定

- **Brush Size（ブラシサイズ）** — ブラシサイズ（1 ～ 64）

#### ブラシモード

- **除外** ボタン — クリックでペイントモード開始（赤いマスクを描画）、再クリックで解除
- **含める** ボタン — クリックで消去モード開始（マスクを消去）、再クリックで解除

#### アンドゥ

- **Ctrl + Z** または「マスクを元に戻す」ボタン — 最後の描画をアンドゥ

#### リセット

「マスクをクリア」ボタンで全てのマスクをクリアします。

---

### プリセット

カラーゾーン設定や加工設定をプリセットとして保存・読み込みできます。

#### 手順

1. 「プリセット」セクションを開く
2. 保存先を選択：「プロジェクト内」または「ユーザー共通」
3. プリセット名を入力して「保存」をクリック
4. 保存済みプリセット一覧から「読込」で設定を読み込み、「×」で削除

**マスクの保存/読込オプション:**

- **マスクを含める** — ONにすると現在の除外マスクもプリセットに保存されます。
- **読込時にマスクも適用** — ONにするとプリセット読込時に保存されたマスクも同時に復元します。

#### JSONエクスポート／インポート

「JSONエクスポート」「JSONインポート」ボタンで設定を外部ファイルとして共有できます。

---

### 一括適用

同じカラーゾーン設定を複数のテクスチャに一括適用します。

#### 手順

1. マスターテクスチャでカラーゾーンを完成させる
2. 「一括適用」セクションを開く
3. 適用対象のテクスチャを追加
4. 「一括適用して保存」ボタンをクリック

全対象テクスチャに同じ設定が適用されます。

---

### エクスポート

#### 保存方法

「新規ファイルとして保存」トグルで保存方法を事前に選択してから「Apply & Save」ボタンをクリックします。

**「新規ファイルとして保存」 ON**
- 元のテクスチャを保持して新規ファイルに保存
- ファイル名を指定可能
- 元のテクスチャは保護される

**「新規ファイルとして保存」 OFF**
- 元のテクスチャファイルを上書き保存
- バックアップを強く推奨

**インポート設定を継承**
- ON（デフォルト）にすると、新しく生成されたテクスチャが元のテクスチャのインポート設定を自動継承します。

**「フォルダを開く」ボタン**
保存されたテクスチャの存在するフォルダをファイルエクスプローラで開きます。

---

### トラブルシューティング

#### 問題: 図形の周りに色が薄い部分やドットが残る

**原因：**
テクスチャの周辺部分（アンチエイリアス処理やぼかしがされている部分）は「薄い色」になっています。

**対策：**

1. **彩度制限を高めに設定**（推奨）
   - 0.7 ～ 0.9 あたりから試す
   - 「薄い色を除外」するため、周辺部分を無視できる

2. **許容範囲 を狭める**
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

2. **AA境界クリーンアップ（AA Edge Cleanup）を有効にする**
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
   - 0.05～0.15程度から始めて調整

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

A: 入力は **PNG / JPG** ファイルに対応しています（Unity の `Texture2D.LoadImage` を使用して元ファイルから直接読み込むため）。出力は常に **PNG** 形式で保存されます。TGA / EXR / PSD などその他の形式は現在サポートしていません。また、Unity 上で **Read/Write Enabled** を有効にしておく必要があります。

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
- [Processing Settings](#processing-settings)
- [Advanced Mode](#advanced-mode)
- [Preview Features](#preview-features)
- [Exclusion Mask](#exclusion-mask)
- [Presets](#presets)
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

1. Click "+ Add Zone"
2. A new zone is created; you can rename it freely (name is for display only)

#### Step 3: Select Color Target

Two modes are available:

**Color Pick Mode (recommended):**
1. Set "Selection Mode" to "ColorPick" in zone settings
2. Click the "Sample Color" color field to open Unity's color picker
3. Use the eyedropper icon inside the picker to sample a color from the preview or anywhere on screen
4. Adjust "Tolerance" to fine-tune the selection

**UV Rect Mode:**
1. Set "Selection Mode" to "Rect" in zone settings
2. Specify X/Y (bottom-left origin) and W/H (width/height) using UV coordinates (0–1)
3. More precise range control available without relying on color information

#### Step 4: Set Target Color

1. Click "Target Color" in zone settings
2. Choose a new color
3. Preview updates instantly

#### Step 5: Fine-tune (optional)

Adjust any or all of:

- **Pattern Preserve** — How much pattern to retain (0 = solid, 1 = full pattern)
- **Edge Softness** — Edge hardness (0 = hard, 1 = soft)
- **Saturation Strictness** — Light color exclusion (0 = include all, 1 = vivid only)
- **Highlight Recovery** — Prevents recoloring gaps on reflective/glossy areas
- **Shadow/Highlight Details** — Controls dark-area bleed and auto grayscale detection (see Advanced Mode)
- **L (Layer Index)** — Priority when zones overlap (higher = later = takes priority)

#### Step 6: Export

1. Use the "Save as new file" toggle to choose whether to save as a new file or overwrite
2. Click "Apply & Save"
3. File is saved

---

### Color Zone Settings

#### Color Pick Mode

Auto-detects target pixels based on sample color and matching criteria.

**Settings:**

- **Sample Color** — Click the color field to open Unity's color picker, then use the built-in eyedropper icon to sample a color from the preview or any on-screen pixel. Pick the most vivid color in the target area for best results.

- **Tolerance** — Matching range (0.0 ～ 1.0)
  - Low: stricter matching (narrow selection)
  - High: broader matching (wider selection, more noise)
  - Recommended: 0.15 ～ 0.40

- **Connected Region (Flood Fill)** — Toggle available in Color Pick mode.
  When enabled, click on the preview to set a "seed point". The tool automatically
  traces neighboring pixels up to a color-difference boundary, limiting the recolored
  area to a connected region only.
  - Edge Stop Threshold: color-difference value that acts as a region boundary (Advanced Mode)

#### UV Rect Mode

Explicitly specify a rectangular region using UV coordinates.

**Settings:**

- **X / Y** — Bottom-left corner of the rectangle in UV coordinates (0–1)
- **W / H** — Width and height in UV coordinates (0–1)

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

**Saturation Strictness**

Controls handling of "light colors" at texture edges.

- 0: Include light colors
- 0.5: Standard filtering (default)
- 1: Vivid colors only

**Highlight Recovery**

Matches high-brightness, low-saturation highlight regions (reflective/glossy areas) to prevent missed recoloring.

- **ON** (default): Prevents recoloring gaps in highlight areas
- **OFF**: Use when you want to strictly exclude highlight regions

**Shadow/Highlight Details**

Fine-grained control over how dark and desaturated pixels are handled.

- **Shadow Desaturation** — Brightness threshold below which dark pixels have their saturation reduced. Lower values allow darker colors to be recolored more vividly. Default: 0.35
- **Shadow Forgiveness Sat Min** — Minimum saturation required to include a dark pixel as shadow. Prevents pure greys from being colorized. Default: 0.05
- **Auto Grayscale Threshold** — If the sample saturation is below this value, hue is ignored and the zone treats pixels as pure grayscale (black/grey), giving very clean results for dark neutrals. Default: 0.05

**L (Layer Index)**

An integer set via the `L` field in the zone header. Controls application order when zones overlap.

- Zones are processed in ascending order of Layer Index
- Higher values apply later and therefore overwrite lower ones (take priority)
- Zones with the same index are applied in the order they were added

---

### Processing Settings

Profiles for edge and noise processing (found in the "Processing" section).
Changes are reflected in the preview automatically and applied on "Apply & Save".

#### Edge Feather

Gaussian blur on selection boundaries for smooth transitions.

- 0: Off
- 0.5 ～ 1.5: Standard blur
- 2.0+: Strong blur

#### AA Edge Cleanup

Number of passes to recover anti-alias boundary pixels.

- 0: Off
- 1 ～ 2: Weak
- 3: Standard (default)
- 4 ～ 5: Strong

#### Edge Decontamination

Rebuilds AA boundary pixels via alpha decomposition and recomposition to prevent muddy halo colors at edges.

- **ON** (default): Recommended. Prevents color contamination at AA boundaries.
- **OFF**: Use legacy cleanup only.

---

### Advanced Mode

Enable the "Advanced Mode" toggle in the Processing section to fine-tune internal algorithm parameters. Default values work well for most textures; use Advanced Mode only when you need tighter control for tricky textures.

Turning on Advanced Mode also reveals additional per-zone parameters.

#### Per-zone advanced parameters

- **Value Weight** — Weight of value (brightness) in the distance formula. Default: 1.0
  - Higher: sensitive to brightness differences (better separation between materials)
  - Lower: tolerates brightness variation (absorbs shadow/highlight of the same material)
- **Sat Distance Weight** — Weight of saturation distance in the distance formula. Default: 0.15
- **Sat Ramp Scale** — Scale factor for the dynamic saturation ramp. Default: 0.10
  - Larger: more gradual fade near the saturation threshold
  - Smaller: sharper threshold

#### Processing advanced parameters

- **Hole Fill Passes** — Passes to fill isolated dots at anti-aliased edges. Default: 5
- **Hole Fill Min Neighbors** — Minimum matched neighbors required to fill a hole. Default: 4
  - Lower: more aggressive filling (may over-fill)
  - Higher: more conservative
- **Boundary Sat Min** — Minimum saturation threshold for boundary recovery. Default: 0.02
- **Boundary Sat Ramp** — Saturation ramp width for boundary recovery. Default: 0.08
- **Decontamination Radius** — Neighborhood radius used to estimate background color for Edge Decontamination. Default: 4

> **Tip:** Advanced Mode settings are included when saving/loading presets. Once you find a combination that works well for a specific texture style, save it as a preset.

---

### Preview Features

#### Zoom

- **Ctrl + Scroll** — Zoom in/out
- **Drag** — Pan (available only when zoom > 1x)

#### View Modes

- **Normal** (comparison/diff off) — Current result
- **Compare** — Side-by-side before/after comparison
- **Diff** — Highlight changed pixels

#### Auto-update

Updates automatically after setting changes (default: 0.2s delay).

---

### Exclusion Mask

Paint areas on the preview to exclude them from recoloring. Supports both a common mask (applied to all zones) and per-zone masks.

#### How to Use

1. Select the mask target from the "Mask Target" dropdown (Common or a specific zone)
2. Click "Exclude" to enter paint mode
3. Drag on the preview to paint the exclusion mask (red overlay = common, colored overlay = zone-specific)
4. Masked areas won't be recolored
5. Click "Exclude" again to exit paint mode

#### Mask Target

- **Common Mask** — Applied to all zones. Default selection.
- **Zone-specific Mask** — Select a zone name from the dropdown to edit a mask that applies only to that zone.

#### Brush Settings

- **Brush Size** — Brush size (1–64)

#### Brush Modes

- **Exclude** button — Click to enter paint mode (draw red mask), click again to exit
- **Include** button — Click to enter erase mode (erase mask), click again to exit

#### Undo

- **Ctrl + Z** or "Undo Mask" button — Undo last stroke

#### Clear

Click "Clear Mask" to remove all masks.

---

### Batch Apply

Apply the same zone settings to multiple textures at once.

#### Steps

1. Complete zones on a master texture
2. Open the "Batch Apply" section
3. Add target textures
4. Click "Batch Apply & Save"

---

### Presets

Save and load zone and processing settings as presets.

#### Steps

1. Open the "Presets" section
2. Select storage location: "In Project" or "Shared (User)"
3. Enter a preset name and click "Save"
4. Load a preset from the list with "Load", or delete with "×"

**Mask save/load options:**

- **Include Masks** — When ON, the current exclusion masks are saved together with the preset.
- **Apply Masks on Load** — When ON, masks stored in the preset are restored when loading.

#### JSON Export / Import

Use "Export JSON" / "Import JSON" buttons to share settings as external files.

---

### Export

#### Save Methods

Use the "Save as new file" toggle to select the save method, then click "Apply & Save".

**"Save as new file" ON**
- Save as a new file while keeping the original
- Choose a filename
- Original texture protected

**"Save as new file" OFF**
- Save over the original texture
- Backup strongly recommended

**Inherit Import Settings**
- ON (default): The newly generated texture automatically inherits the import settings of the source texture.

**Open Folder button**
Reveals the saved texture in the system file explorer.

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

2. **Enable AA Edge Cleanup** (set to 3 or 5)

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

1. **Greatly narrow Tolerance** (start at 0.05～0.15)

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

A: Input files must be **PNG** or **JPG** (VACC reads raw bytes and decodes them via `Texture2D.LoadImage`). Output is always saved as **PNG**. Other formats such as TGA / EXR / PSD are not supported. Textures also need **Read/Write Enabled** in their import settings.

**Q: Does Batch Apply include all Color Zones?**

A: Yes. All zones are applied to each target texture.

**Q: Does it increase RAM usage?**

A: Yes temporarily, as textures are processed in memory. Large textures may use significant RAM.
