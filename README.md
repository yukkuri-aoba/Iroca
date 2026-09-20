# Iroca/いろか Ver 0.2.0 (Beta)

Iroca/いろか は、Unity Editor 上でテクスチャの色を直感的に変更できる拡張ツールです。VRChat アバターのテクスチャ編集を主な対象としていますが、一般的な Unity プロジェクトでも使用できます。

[日本語](#日本語) | [English](#english)

> **注：** 日本語版が公式版です。英語版は参考情報としてご利用ください。
> **Note:** The Japanese version is the official version. The English version is for reference only.


---

## 日本語

### 主な特徴

- **無料**: 基本的に無料で利用できます（投げ銭は歓迎しますが、任意です）
- **PSD がないテクスチャ向け**: 1 枚の PNG テクスチャを色改変したい場合に便利です
- **結合されたテクスチャも対応**: ブラシで保護エリアを描けるため、複数パーツが 1 枚にまとまっていても使用できます
- **高精度な処理**: 陰影や細部も正確に変更できます
- **3Dモデルに依存しない**: UV情報などに依存せず、テクスチャ単体で使用できます

### 動作環境（検証済み）

- **Unity 2022.3.22f1**
- VCC などとの依存関係はありません（Unity Editor 単体で動作します）

### クイックスタート

1. [Releases](https://github.com/yukkuri-aoba/Iroca/releases) から最新の `.unitypackage` をダウンロードします
2. Unity Editor のプロジェクトウィンドウ（Assets フォルダ）にドラッグ＆ドロップします
3. インポートダイアログで「Import」をクリックします
4. `Tools > いろか` からウィンドウを開きます
5. テクスチャを選択し、カラーゾーンを追加して色を設定します
6. 「適用して保存」ボタンで保存します

詳しい使い方は **[オンラインマニュアル](https://yukkuri-aoba.github.io/Iroca/manual/)** をご覧ください
（設定の効き方をスライダーで試せます）。テキスト版は [MANUAL.md](MANUAL.md) です。

### 主な機能

#### 色改変
- テクスチャの特定部分を指定して色を変更します（カラーゾーン）
- 色替え対象は、プレビュー上の変えたい色をスポイトでクリックして指定します（カラーピック）
- 複数ゾーンが重なる部分の優先度は、ゾーンリストの並び順（`☰` ハンドルをドラッグ）で制御します

#### 境界処理
- エッジぼかし・AA 境界クリーンアップ・境界クリーンアップ（α分解）に対応します
(元のテクスチャを壊さず、誤った部分を変換しないための仕組みです)

#### マスク（除外・含める）
- プレビュー上でブラシを使って、色改変しない領域（除外）と必ず色改変する領域（含める）を指定します
- 除外は全ゾーン共通と各ゾーン専用の 2 種類を使い分けられます
- Unity 標準の Undo（Ctrl+Z）に対応しています
- AI マスク提案（実験的・任意インストール）：パーツを右クリックすると AI が領域を推定し、その場でマスクへ追加します（左ドラッグはプレビューの移動のまま。Unity Sentis + MobileSAM。導入手順は MANUAL 参照）

#### その他
- プレビュー：ズーム（Ctrl+スクロール・リセット）・前後比較・差分表示・押している間だけ元画像を表示
- ゾーンのソロ表示：1 つのゾーンだけをプレビューして、どこを拾っているか確かめられます
- 操作モードの表示：プレビュー上のクリックが何をするかを 1 行で表示し、Esc で解除できます
- プリセット：設定とマスクの保存・読み込み、JSON での書き出し・読み込み対応
- 日本語・英語の自動切り替え

### 向いているケース

- 陰影がはっきりしたテクスチャ
- 単純なベタ塗りのテクスチャ

### あまり向かないケース

- 色の似た部分が多いテクスチャ
- 色のグラデーションが複雑なテクスチャ
- 反射や光沢の強いテクスチャ

これらのケースでも、[MANUAL.md](MANUAL.md) の「トラブルシューティング」で対策を紹介しています。

### インストール手順

1. [Releases](https://github.com/yukkuri-aoba/Iroca/releases) から最新の `.unitypackage` をダウンロードします
2. Unity Editor にドラッグ＆ドロップして読み込みます
3. ダイアログで「Import」をクリックします
4. `Tools > いろか` からウィンドウを開きます

### ライセンス

[PolyForm Shield License 1.0.0](LICENSE)

- 個人・商用を問わず自由に使用できます
- 本ツールと競合する製品・サービスの開発・提供に使用することは禁止されています
- 改変および再配布は自由に許可されています（競合製品への使用を除く）
- 各規約・ガイドラインに従った使用は利用者の責任です

---

## English

### Key Features

- **Free**: Free to use at its core (tips welcome, purchase optional)
- **For textures without a PSD**: Great when you just want to recolor a single PNG texture
- **Works with merged textures**: The exclusion mask brush makes it easy to isolate parts even when multiple elements share one texture
- **High-precision algorithm**: Accurate recoloring with fine details preserved
- **Independent of 3D models**: Works on textures alone, without relying on UV information or other model data

### Requirements

- **Unity 2022.3.22f1**
- No VCC / VRCSDK required (works standalone in Unity Editor)

### Quick Start

1. Import `.unitypackage` into Unity Editor
2. Open the window: `Tools > いろか`
3. Select a texture, add a color zone, and set the target color
4. Click `Apply & Save`

See the **[online manual](https://yukkuri-aoba.github.io/Iroca/manual/)** for detailed instructions
(sliders let you try how each setting behaves). A plain-text version lives in [MANUAL.md](MANUAL.md).

### Main Features

#### Recoloring
- Target specific texture areas and change their color (Color Zones)
- Select the color to replace by picking it directly on the preview (Color Pick)
- Control priority across overlapping zones by their order in the zone list (drag the `☰` handle to reorder)

#### Boundary Processing
- Edge Feather / AA Edge Cleanup / Edge Decontamination for smooth color transitions

#### Masks (Exclude / Include)
- Paint the areas to protect (Exclude) and the areas to always recolor (Include) directly on the preview
- Exclude supports both a common mask and per-zone masks
- Integrated with Unity's standard Undo (Ctrl+Z)
- AI Mask Suggestion (experimental, optional install): right-click a part and the AI estimates its region and adds it to the mask (a left drag still pans the preview; Unity Sentis + MobileSAM, see MANUAL for setup)

#### Other
- Preview: zoom (Ctrl+Scroll and Reset), before/after comparison, diff view, hold a button to see the original
- Solo a zone: preview a single zone to check what it selects
- Current mode row: one line showing what a click on the preview does, with Esc to leave the mode
- Presets: save and load settings with masks, JSON export/import
- Auto language detection (Japanese / English)

### Best Use Cases

- Textures with clear, well-defined shading
- Textures with simple flat colors

### Limitations

- Textures with many similarly-colored areas
- Textures with complex color gradients
- Textures with reflections or specular highlights

See [MANUAL.md](MANUAL.md) for workarounds and tips.

### Installation

1. Download the latest `.unitypackage` from [Releases](https://github.com/yukkuri-aoba/Iroca/releases)
2. In Unity Editor, select `Assets > Import Package > Custom Package...`
3. Choose the downloaded file, then click "Import" in the dialog
4. Open the window via `Tools > いろか`

### License

[PolyForm Shield License 1.0.0](LICENSE)

- Free to use for personal and commercial purposes.
- Cannot be used to provide a product that competes with this software.
- Modification and redistribution are freely permitted (except for competing products).
- Use in accordance with each license and guideline is the responsibility of the user.

---

## クレジット / Credits

| 役割 / Role | 名前 / Name |
|---|---|
| 開発 / Developer | **yukkuri__aoba** |
| AI 補助 / AI Assistance | **Claude** · **GPT** ・ **Gemini**|
Copyright (c) 2026 yukkuri__aoba  
Licensed under [PolyForm Shield License 1.0.0](LICENSE)

### アルゴリズムの開発に使用したデータ(敬称略) / Data Used for Algorithm Development

かなﾘぁさんち  
[ハオラン-HAOLAN【オリジナル3Dモデル】](https://booth.pm/ja/items/3818504)

Senna Studio  
[オリジナル3Dモデル - フェイナ #Feina3D](https://booth.pm/ja/items/7428637)

もやしちゃん  
[【無料】Quanstella - クアンステーラ VRChat用アバター](https://booth.pm/ja/items/5922294)

NOIRVAIL_BOOTH  
[【14アバター対応】ROUGHCUT【VRChat向け衣装モデル】](https://noirvail029.booth.pm/items/8111161)

C#アルゴリズムの開発にはこれらのモデル・衣装のテクスチャを使用させていただきました。
モデルやテクスチャのデータ自体はこのコードベースに含まれていません。
コーディングにAIを使用していますが、
モデルの学習からオプトアウトされるよう設定して利用しています。
また、AIマスク提案機能のモデルの学習にも一切使用していません。
提供されているVN3ライセンスには抵触していない認識ですが、万一問題があればお問い合わせください。

### 連絡先 / Contact

- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
