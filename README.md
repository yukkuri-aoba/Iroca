# Iroca/いろか Ver 0.2.0 (Beta)

Iroca/いろか は、Unity Editor 上でテクスチャの色を直感的に変更できる拡張ツールです。VRChat アバターのテクスチャ編集を主な対象としていますが、一般的な Unity プロジェクトでも使用できます。

[日本語](#日本語) | [English](#english)

> **注：** 日本語版が公式版です。英語版は参考情報としてご利用ください。

---

## 日本語

### 主な特徴

- **無料**: 基本的に無料で利用できます（投げ銭は歓迎しますが、任意です）
- **PSD がないテクスチャ向け**: 1 枚の PNG テクスチャを色改変したい場合に便利です
- **結合されたテクスチャも対応**: ブラシで保護エリアを描けるため、複数パーツが 1 枚にまとまっていても使用できます
- **高精度な処理**: 陰影や細部も正確に変更できます

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

詳しい使い方は [MANUAL.md](MANUAL.md) をご覧ください。

### 主な機能

#### 色改変
- テクスチャの特定部分を指定して色を変更します（カラーゾーン）
- 色の選択方法は「カラーピック」と「UV 矩形」の 2 種類です
- 複数ゾーンの重なりはレイヤー番号で優先度を制御します

#### 境界処理
- エッジぼかし・AA 境界クリーンアップ・境界クリーンアップ（α分解）に対応します

#### 保護マスク
- プレビュー上でブラシを使って色改変しない領域を指定します
- 全ゾーン共通と各ゾーン専用の 2 種類を使い分けられます
- Unity 標準の Undo（Ctrl+Z）に対応しています

#### その他
- プレビュー：ズーム・前後比較・差分表示
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

### Requirements

- **Unity 2022.3.22f1**
- No VCC / VRCSDK required (works standalone in Unity Editor)

### Quick Start

1. Import `.unitypackage` into Unity Editor
2. Open the window: `Tools > いろか`
3. Select a texture, add a color zone, and set the target color
4. Click `Apply & Save`

See [MANUAL.md](MANUAL.md) for detailed instructions.

### Main Features

#### Recoloring
- Target specific texture areas and change their color (Color Zones)
- Two selection methods: Color Pick and UV Rect
- Control priority across overlapping zones with the Layer Index

#### Boundary Processing
- Edge Feather / AA Edge Cleanup / Edge Decontamination for smooth color transitions

#### Exclusion Mask
- Paint protected areas directly on the preview
- Supports both a common mask and per-zone masks
- Integrated with Unity's standard Undo (Ctrl+Z)

#### Other
- Preview: zoom, before/after comparison, diff view
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
| AI 補助 / AI Assistance | **Claude** · **Gemini** |

Copyright (c) 2026 yukkuri__aoba  
Licensed under [PolyForm Shield License 1.0.0](LICENSE)

### アルゴリズムの開発に使用したデータ / Data Used for Algorithm Development

かなﾘぁさんち  
[ハオラン-HAOLAN【オリジナル3Dモデル】](https://booth.pm/ja/items/3818504)

Senna Studio  
[オリジナル3Dモデル - フェイナ #Feina3D](https://booth.pm/ja/items/7428637)

アルゴリズムの開発にはこれらのモデルのテクスチャを使用しました。モデルやテクスチャのデータ自体は含まれていません。

### 連絡先 / Contact

- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
