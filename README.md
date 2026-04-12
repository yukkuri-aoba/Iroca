# VRC AvatarColorChanger (VACC) Ver 0.1.0 (Beta)

*[日本語](#日本語) | [English](#english)*

> **注：** 日本語版が公式版です。英語版は参考情報としてご利用ください。
> また、英語版はAI翻訳を使用しています。

---

## 日本語

### 概要

VRC AvatarColorChanger (VACC) は、Unity Editor 上でテクスチャの色を直感的に変更できるエディタ拡張ツールです。
主に VRChat アバターのテクスチャ編集を想定していますが、一般的な Unity プロジェクトでも使用できます。

### 推しポイント

- **無料** — 基本的に無料で利用できます（投げ銭は歓迎しますが、購入は任意です）
- **PSDがないテクスチャ向け** — 1枚のPNGテクスチャを色改変したい場合に便利
- **結合されたテクスチャもOK** — ブラシで除外マスクを簡単に描けるため、複数パーツが1枚にまとまっていても利用可能
- **高精度なアルゴリズム** — 色改変の精度が高く、細かい部分も正確に変更可能


### 機能

- **カラーゾーン** — 複数のゾーンを定義してテクスチャの特定部分を一括色改変
  - **カラーピック モード** — サンプルカラーと許容範囲 (Tolerance) で対象ピクセルを自動検出
  - **UV矩形 モード** — UV座標で矩形範囲を指定して色改変
  - **模様保持スライダー** — 変更先の明度と元テクスチャの明度の混合比を調整（0 = ベタ塗り、1 = 模様完全保持）
  - **エッジ柔らかさ** — 選択境界の硬さを調整（0 = 硬いエッジ、1 = 滑らかなエッジ）
  - **レイヤーインデックス** — 複数ゾーンが重なる際の適用順を指定
- **エッジぼかし** — 選択境界をガウシアンブラーでぼかし、滑らかな遷移を実現
- **除外マスク** — プレビュー上でブラシをドラッグして、色改変したくない領域をマスクする（Ctrl+Z でUndo対応）
- **プレビュー** — ズーム対応（Ctrl+スクロールでズーム）。設定変更後、短い遅延で自動更新
  - **前後比較** — 変更前後を並べて確認
  - **差分表示** — 変更されたピクセルをハイライト表示
- **プリセット** — ゾーン設定をプリセットとして保存・読み込み可能
- **バッチ適用** — 複数テクスチャに同じカラーゾーン設定を一括適用
- **エクスポート** — 新規 PNG として保存、または元テクスチャへの上書き保存に対応
- **日本語・英語対応** — 自動検知、またはツールバーで手動切り替え可能

### 上手くいきやすいケース

- 陰影がはっきりしたテクスチャ
- 単純な色のベタ塗りのテクスチャ

### 上手くいかないケース

- 色の似た部分が多いテクスチャ
- 色のグラデーションが複雑なテクスチャ
- 反射や光沢のあるテクスチャ
- 色を黒色など、暗い色に変更しようとする場合(模様保持率を下げることである程度は可能)

これらのケースでは、カラーピック モードで狙った部分をうまく選択できないことがあります。その場合は[「上手くいかないときは」](#上手くいかないときは)のコツを試してみてください。

### 動作環境(検証済みの環境)

- Unity 2022.3.22f1
- VCCなどとの依存関係はありません（Unity Editor 単体で動作）

※ その他のバージョンでも動作する可能性がありますが、未検証です。

### 使い方

> **注意：** 対象テクスチャは **Read/Write Enabled** が有効である必要があります。無効の場合は、ウィンドウ内のボタンから自動的に有効化できます。

1. **Unity Editorに.unitypackageをインポート**
   - 完了すると、`VACC`フォルダがAssets内に作成されます。

2. **ウィンドウを開く**
   - 上部のメニューから `Tools > VRC AvatarColorChanger` を選択。
   - ウィンドウのサイズを適切に調整してください（プレビューの見やすさが変わります）

3. **テクスチャを選択**
   - Unityのテクスチャ選択UIを使用して、色改変したいテクスチャを選択
   - テクスチャはプレビューに表示されます。

4. **カラーゾーンを追加**
   - 色改変したい部分を定義
   - カラーピック モードでサンプルカラーと許容範囲を調整して、狙った部分を選択

   > カラーピック モードでうまく選択できない場合は、[「上手くいかないときは」](#上手くいかないときは)のコツを試してみてください。

5. **模様保持スライダーを調整**
   - 元の模様をどの程度残すか調整
   - 0に近づけるほどベタ塗り、1に近づけるほど模様が保持されます

6. **除外マスクを描画**（必要に応じて）
   - プレビュー上でブラシをドラッグ
   - 色改変したくない部分をマスクする

7. **エクスポート**
   - 「適用して保存」ボタンをクリック
   - 新規ファイルとして保存するか、元のテクスチャに上書き保存するか選択


### 上手くいかないときは

以下のコツを試してみてください：

1. テクスチャの変えたい部分の中から、色が最も鮮やかな部分を選ぶ
2. 許容範囲 (Tolerance) を広げて調整
3. 除外マスクを使用して色改変したくない部分をマスクする

> とはいえ、これでも上手くいかない場合が残念ながら存在すると思われます。
> (目的の色が他の部分と似ている、グラデーションが複雑すぎる、など)
> どうしても上手くいかない場合は、UV矩形モードで手動で範囲指定してみてください。

### ライセンス

MIT License with Commons Clause

### 免責事項

- 本ツールは現状有姿で提供されます。使用によって生じた損害・不具合・データ損失について、制作者(yukkuri__aoba)は一切の責任を負いません。
- バグや不具合についてはできる限り対応しますが、修正を保証するものではありません。
- 各利用規約・ガイドラインに従った使用は利用者の責任です。
- このツールはMIT Licenseに基づいて提供されますが、Commons Clauseが追加されているため、そのまま転売することは禁止されています。

---

## English

> **Note:** The Japanese version is the official version. Please use the English version as reference information.
> AI translation was used for the English version.

### Overview

VRC AvatarColorChanger (VACC) is a Unity Editor extension for intuitively recoloring textures.
Primarily designed for VRChat avatar texture editing, but works in any Unity project.

### Highlights

- **Free** — Free to use at its core (tips welcome, purchase optional)
- **For textures without a PSD** — Great when you just want to recolor a single PNG texture
- **Works with merged textures** — The exclusion mask brush makes it easy to isolate parts even when multiple elements share one texture

### Features

- **Color Zones** — Define multiple zones to recolor specific areas of a texture in bulk
  - **Color Pick mode** — Auto-detect target pixels by sample color and a Tolerance value
  - **UV Rect mode** — Specify a rectangular region by UV coordinates for recoloring
  - **Pattern Preserve slider** — Mix between target and original brightness (0 = flat recolor, 1 = full pattern preservation)
  - **Edge Softness** — Adjust selection boundary hardness (0 = hard edge, 1 = smooth edge)
  - **Layer Index** — Control the application order when multiple zones overlap
- **Edge Feather** — Gaussian blur on selection boundaries for smooth transitions
- **Exclusion Mask** — Paint areas on the preview with a brush to exclude them from recoloring (Ctrl+Z to undo)
- **Preview** — Zoom-capable (Ctrl+scroll to zoom); auto-updates after a short delay on every change
  - **Before/After comparison** — View original and recolored side by side
  - **Diff view** — Highlight pixels that have been changed
- **Presets** — Save and load zone configurations as named presets
- **Batch Apply** — Apply the same Color Zone settings to multiple textures at once
- **Export** — Save as a new PNG file or overwrite the source texture
- **Multilingual UI** — Auto-detects Japanese/English. Manual toggle available in the toolbar

### Best Use Cases

- Textures with clear, well-defined shading
- Textures with simple flat colors

### Limitations

- Textures with many similarly-colored areas
- Textures with complex color gradients
- Textures with reflections or specular highlights

In these cases, Color Pick mode may struggle to isolate the intended area. See [Tips when it doesn't work](#tips-when-it-doesnt-work) for workarounds.

### Requirements

- Unity 2022.3.22f1 (Editor script)
- No VCC / VRCSDK required (works standalone in Unity Editor)

※ Other Unity versions may work but have not been tested.

### Usage

> **Note:** The source texture must have **Read/Write Enabled**. If it is off, use the in-window button to enable it automatically.

1. **Import .unitypackage into Unity Editor**
   - A `VACC` folder will be created.

2. **Open the window**
   - Select `Tools > VRC AvatarColorChanger` from the top menu.
   - Resize the window as needed (affects preview visibility).

3. **Select a texture**
   - Use Unity's texture picker to select the texture you want to recolor.
   - The texture will appear in the preview.

4. **Add Color Zones**
   - Define the areas you want to recolor.
   - In Color Pick mode, adjust the sample color and Tolerance to target the desired area.

5. **Adjust the Pattern Preserve slider**
   - Control how much of the original pattern is retained.
   - Closer to 0 = flat solid recolor; closer to 1 = pattern preserved.

6. **Paint the Exclusion Mask** (optional)
   - Drag a brush on the preview to mask areas you don't want recolored.

7. **Export**
   - Click the **Apply & Save** button.
   - Choose to save as a new file or overwrite the source texture.

### Tips when it doesn't work

Try the following:

1. Pick the most vivid/saturated color within the area you want to recolor
2. Increase the Tolerance value to broaden the selection
3. Use the Exclusion Mask to protect areas you don't want affected

> That said, there will unfortunately be cases where even these tips are not enough
> (e.g., the target color is too similar to surrounding areas, or the gradient is too complex).

### License

MIT License with Commons Clause

### Disclaimer

- This tool is provided "as is" without warranty of any kind. The author (yukkuri__aoba) is not liable for any damages, data loss, or issues arising from its use.
- Bugs will be addressed on a best-effort basis, but fixes are not guaranteed.
- It is the user's responsibility to comply with VRChat's Terms of Service and Community Guidelines.
- This tool is licensed under the MIT License with Commons Clause. Reselling this tool as-is is prohibited.

---

## クレジット / Credits

| Role | Name |
|------|------|
| Developer / 開発 | **yukkuri__aoba** |
| AI Assistance / AI補助 | **Claude** (Anthropic) |

Copyright (c) 2026 yukkuri__aoba

## アルゴリズムの開発に使用したデータ / Data Used for Development
かなﾘぁさんち<br>
-ハオラン-HAOLAN【オリジナル3Dモデル】<br>
https://booth.pm/ja/items/3818504

Senna Studio<br>
オリジナル3Dモデル - フェイナ #Feina3D<br>
https://booth.pm/ja/items/7428637


> これらのモデルに含まれるPSD、テクスチャを用いて開発させていただきました。<br>
> フェイナちゃんは自分のお気に入りです！おすすめ。

## 連絡先 / Contact
- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
