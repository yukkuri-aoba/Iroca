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

- **Unity 2022.3.22f1**（Windows で検証しています）
- VCC や VRChat SDK は不要です（Unity Editor 単体で動作します）
- 自動調整と AI マスク提案には、Unity Sentis 2.1.3（Unity 2022.3.11f1 以降）と AI モデル（約 44 MB）が必要です。ウィンドウ上部の案内から導入できます
- 4096×4096 までのテクスチャで検証しています

### クイックスタート

1. [Releases](https://github.com/yukkuri-aoba/Iroca/releases) から `Iroca_Installer.unitypackage` をダウンロードし、Unity Editor のプロジェクトウィンドウにドラッグ＆ドロップして「Import」をクリックします
2. 確認ダイアログで「Install」をクリックすると、最新版がダウンロードされて入ります（VCC / ALCOM からも導入できます。[インストール手順](#インストール手順)）
3. `Tools > いろか` からウィンドウを開きます
4. テクスチャを選択し、カラーゾーンを追加して色を設定します
5. 「適用して保存」ボタンで保存します

詳しい使い方は **[オンラインマニュアル](https://yukkuri-aoba.github.io/Iroca/manual/)** をご覧ください
（設定の効き方をスライダーで試せます）。テキスト版は [MANUAL.md](MANUAL.md) です。

### 主な機能

#### 色改変
- テクスチャの特定部分を指定して色を変更します（カラーゾーン）
- 色替え対象は、プレビュー上の変えたい色をスポイトでクリックして指定します（カラーピック）
- 自動調整：スポイトしたパーツを AI 提案で解析し、暗部からハイライトまで覆うように許容範囲などを自動で合わせます
- 複数ゾーンが重なる部分の優先度は、ゾーンリストの並び順（`☰` ハンドルをドラッグ）で制御します

#### 境界処理
- エッジぼかし・AA 境界クリーンアップ・境界クリーンアップ（α分解）に対応します
(元のテクスチャを壊さず、誤った部分を変換しないための仕組みです)

#### マスク（除外・含める）
- プレビュー上でブラシを使って、色改変しない領域（除外）と必ず色改変する領域（含める）を指定します
- 除外は全ゾーン共通と各ゾーン専用の 2 種類を使い分けられます
- Unity 標準の Undo（Ctrl+Z）に対応しています
- AI マスク提案（実験的）：パーツを右クリックすると AI が領域を推定し、その場でマスクへ追加します。1 回で取れるのはつながった 1 領域なので、分かれたパーツは島ごとにクリックします（左ドラッグはプレビューの移動のまま。Unity Sentis + MobileSAM）

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

次のどちらかで導入します。どちらも、いろかは Unity の `Packages` に入ります。

**インストーラ（unitypackage）から**

1. [Releases](https://github.com/yukkuri-aoba/Iroca/releases) から `Iroca_Installer.unitypackage` をダウンロードします
2. Unity Editor にドラッグ＆ドロップし、ダイアログで「Import」をクリックします
3. 確認ダイアログで「Install」をクリックすると、最新版がダウンロードされて入ります（インターネット接続が必要です。インストーラ自体は自動で消えます）

**VCC / ALCOM から**

1. VCC（または ALCOM）の設定で、リポジトリ `https://yukkuri-aoba.github.io/Iroca/index.json` を追加します
2. プロジェクトの管理画面で「いろか」を追加します

入ったら `Tools > いろか` からウィンドウを開きます。

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

- **Unity 2022.3.22f1** (tested on Windows)
- No VCC / VRCSDK required (works standalone in Unity Editor)
- Auto-tune and AI Mask Suggestion need Unity Sentis 2.1.3 (Unity 2022.3.11f1 or later) and the AI models (about 44 MB). You can install them from the notice at the top of the window
- Tested with textures up to 4096×4096

### Quick Start

1. Import `Iroca_Installer.unitypackage` into Unity Editor and click "Install" in the confirmation dialog (or add it via VCC / ALCOM; see [Installation](#installation))
2. Open the window: `Tools > いろか`
3. Select a texture, add a color zone, and set the target color
4. Click `Apply & Save`

See the **[online manual](https://yukkuri-aoba.github.io/Iroca/manual/)** for detailed instructions
(sliders let you try how each setting behaves). A plain-text version lives in [MANUAL.md](MANUAL.md).

### Main Features

#### Recoloring
- Target specific texture areas and change their color (Color Zones)
- Select the color to replace by picking it directly on the preview (Color Pick)
- Auto-tune: analyzes the picked part with the AI suggestion and sets the tolerance and related settings to cover it from shadows to highlights
- Control priority across overlapping zones by their order in the zone list (drag the `☰` handle to reorder)

#### Boundary Processing
- Edge Feather / AA Edge Cleanup / Edge Decontamination for smooth color transitions

#### Masks (Exclude / Include)
- Paint the areas to protect (Exclude) and the areas to always recolor (Include) directly on the preview
- Exclude supports both a common mask and per-zone masks
- Integrated with Unity's standard Undo (Ctrl+Z)
- AI Mask Suggestion (experimental): right-click a part and the AI estimates its region and adds it to the mask. One click picks one connected region, so click each island of a split part (a left drag still pans the preview; Unity Sentis + MobileSAM)

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

Use either method. Both install Iroca into your project's `Packages`.

**From the installer (unitypackage)**

1. Download `Iroca_Installer.unitypackage` from [Releases](https://github.com/yukkuri-aoba/Iroca/releases)
2. In Unity Editor, select `Assets > Import Package > Custom Package...`, choose the file, and click "Import"
3. Click "Install" in the confirmation dialog. The latest version is downloaded and installed (requires an internet connection; the installer removes itself)

**From VCC / ALCOM**

1. In VCC (or ALCOM) settings, add the repository `https://yukkuri-aoba.github.io/Iroca/index.json`
2. Add "いろか" from your project's management screen

Then open the window via `Tools > いろか`.

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

Arka_X  
[ゆめか / Yumeka](https://arkax.booth.pm/)

C#アルゴリズムの開発にはこれらのモデル・衣装のテクスチャを使用させていただきました。
配布パッケージ（zip・インストーラ）にはモデルやテクスチャのデータは含まれていません。
オンラインマニュアルの画面写真と実演画像には、かなﾘぁさんち「ハオラン-HAOLAN」のテクスチャ（一部は色替え後）を、作者の規約に基づき掲載しています。
コーディングにAIを使用していますが、
モデルの学習からオプトアウトされるよう設定して利用しています。
また、AIマスク提案機能のモデルの学習にも一切使用していません。
提供されているVN3ライセンスには抵触していない認識ですが、万一問題があればお問い合わせください。

### サードパーティ / Third-party

- **VPMPackageAutoInstaller**（`Iroca_Installer.unitypackage` に同梱）: Copyright (c) 2022 anatawa12 — MIT License（[全文](https://github.com/anatawa12/VPMPackageAutoInstaller/blob/master/LICENSE)）
- **MobileSAM**（AI マスク提案のモデル。いろか本体には含まれず、初回にダウンロード）: Apache License 2.0 — 変換済みモデルと NOTICE は [Iroca-Models](https://github.com/yukkuri-aoba/Iroca-Models)

### 連絡先 / Contact

- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
