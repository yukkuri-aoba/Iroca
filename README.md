# VRC AvatarColorChanger (VACC) Ver 0.1.0 (Beta)

VRC AvatarColorChanger (VACC) は、Unity Editor 上でテクスチャの色を直感的に変更できるエディタ拡張ツールです。
主に VRChat アバターのテクスチャ編集を想定していますが、一般的な Unity プロジェクトでも使用できます。

[日本語](#日本語) | [English](#english)

> **注：** 日本語版が公式版です。英語版は参考情報としてご利用ください。

## 日本語

### 主な特徴

- **無料** — 基本的に無料で利用できます（投げ銭は歓迎しますが、購入は任意です）
- **PSDがないテクスチャ向け** — 1枚のPNGテクスチャを色改変したい場合に便利
- **結合されたテクスチャもOK** — ブラシで除外マスクを簡単に描けるため、複数パーツが1枚にまとまっていても利用可能
- **高精度なアルゴリズム** — 色改変の精度が高く、細かい部分も正確に変更可能

### 動作環境(検証済み)

- **Unity 2022.3.22f1**
- VCCなどとの依存関係はありません（Unity Editor 単体で動作）

### クイックスタート

1. Unity Editor に `.unitypackage` をインポート
2. `Tools > VRC AvatarColorChanger` からウィンドウを開く
3. テクスチャを選択して色改変

詳しい使い方は [MANUAL.md](MANUAL.md) をご覧ください。

### 主な機能

- **カラーゾーン** — 複数ゾーンを定義してテクスチャの特定部分を一括色改変
  - カラーピック / UV矩形 モード切り替え
  - 模様保持スライダー
  - エッジ柔らかさ調整
  - 彩度制限
- **エッジぼかし** — 滑らかな色の遷移を実現
- **除外マスク** — ブラシで保護エリアを描画
- **プレビュー** — ズーム対応、前後比較、差分表示
- **プリセット** — ゾーン設定の保存・読み込み
- **バッチ適用** — 複数テクスチャに同時適用
- **日本語・英語対応** — 自動検知

### 向いているケース

- 陰影がはっきりしたテクスチャ
- 単純な色のベタ塗りのテクスチャ

### あまり向かないケース

- 色の似た部分が多いテクスチャ
- 色のグラデーションが複雑なテクスチャ
- 反射や光沢のあるテクスチャ

これらのケースでも、[MANUAL.md](MANUAL.md) の「上手くいかないときは」セクションで対策を紹介しています。

### ライセンス

[PolyForm Noncommercial License 1.0.0](LICENSE)

- 個人利用・非商用利用は無償で自由に使用できます
- 第三者による商用利用（転売・有償再配布を含む）は禁止されています
- 改変・再配布は非商用の範囲で許可されます
- 作者(yukkuri__aoba)による商用利用は別途許可されています

### 免責事項

- 本ツールは現状有姿で提供されます。使用によって生じた損害・不具合・データ損失について、制作者(yukkuri__aoba)は一切の責任を負いません。
- バグや不具合についてはできる限り対応しますが、修正を保証するものではありません。
- 各利用規約・ガイドラインに従った使用は利用者の責任です。

---

## English

### Key Features

- **Free** — Free to use at its core (tips welcome, purchase optional)
- **For textures without a PSD** — Great when you just want to recolor a single PNG texture
- **Works with merged textures** — The exclusion mask brush makes it easy to isolate parts even when multiple elements share one texture
- **High-precision algorithm** — Accurate recoloring with fine details preserved

### Requirements

- **Unity 2022.3.22f1**
- No VCC / VRCSDK required (works standalone in Unity Editor)

### Quick Start

1. Import `.unitypackage` into Unity Editor
2. Open the window: `Tools > VRC AvatarColorChanger`
3. Select a texture and recolor

See [MANUAL.md](MANUAL.md) for detailed instructions.

### Main Features

- **Color Zones** — Define zones to recolor specific texture areas
  - Color Pick / UV Rect modes
  - Pattern Preserve slider
  - Edge Softness controls
  - Saturation filtering
- **Edge Feather** — Smooth color transitions
- **Exclusion Mask** — Paint areas to protect from recoloring
- **Preview** — Zoom-capable, before/after comparison, diff view
- **Presets** — Save and load configurations
- **Batch Apply** — Apply to multiple textures at once
- **Multilingual UI** — Auto-detects language

### Best Use Cases

- Textures with clear, well-defined shading
- Textures with simple flat colors

### Limitations

- Textures with many similarly-colored areas
- Textures with complex color gradients
- Textures with reflections or specular highlights

See [MANUAL.md](MANUAL.md) for workarounds and tips.

### License

[PolyForm Noncommercial License 1.0.0](LICENSE)

- Free for personal and noncommercial use
- Commercial use by third parties (including resale and paid redistribution) is prohibited
- Modification and redistribution are permitted for noncommercial purposes
- Commercial use by the author (yukkuri__aoba) is separately permitted

---

## クレジット / Credits

| Role | Name |
|------|------|
| Developer / 開発 | **yukkuri__aoba** |
| AI Assistance / AI補助 | **Claude** (Anthropic) |

Copyright (c) 2026 yukkuri__aoba  
Licensed under [PolyForm Noncommercial License 1.0.0](LICENSE)

### アルゴリズムの開発に使用したデータ / Data Used for Development
かなﾘぁさんち
[ハオラン-HAOLAN【オリジナル3Dモデル】](https://booth.pm/ja/items/3818504)

Senna Studio
[オリジナル3Dモデル - フェイナ #Feina3D](https://booth.pm/ja/items/7428637)

### 連絡先 / Contact

- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
