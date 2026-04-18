# VRC AvatarColorChanger (VACC) Ver 0.1.0 (Beta)

VRC AvatarColorChanger (VACC) は、Unity Editor 上でテクスチャの色を直感的に変更できるエディタ拡張ツールです。
主に VRChat アバターのテクスチャ編集を想定していますが、一般的な Unity プロジェクトでも使用できます。

[日本語](#日本語) | [English](#english)

> **注：** 日本語版が公式版です。英語版は参考情報としてご利用ください。

## 日本語

### 主な特徴

- **無料** — 基本的に無料で利用できます（投げ銭は歓迎しますが、任意です）
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
  - ハイライト補助（鏡面反射・光沢部分の変換漏れ防止）
  - レイヤーインデックスによる優先度制御
- **エッジと境界の処理** — エッジぼかし (Edge Feather) / AA境界クリーンアップで滞らかな遷移を実現
- **除外マスク** — プレビュー上でブラシを使って保護エリアを描画（最大 30 ステップの Undo 対応）
- **プレビュー** — ズーム対応、前後比較、差分表示、高ズーム時の詳細プレビュー
- **プリセット** — ゾーン設定の保存・読み込み（プロジェクト内 / ユーザー共通の 2 つの保存先、JSON インポート/エクスポート対応）
- **バッチ適用** — 複数テクスチャに同時適用
- **アドバンスモード** — 距離計算の重みや穴埋めパス数など内部パラメータを微調整可能
- **日本語・英語対応** — 自動検知

### 向いているケース

- 陰影がはっきりしたテクスチャ
- 単純な色のベタ塗りのテクスチャ

### あまり向かないケース

- 色の似た部分が多いテクスチャ
- 色のグラデーションが複雑なテクスチャ
- 反射や光沢のあるテクスチャ

これらのケースでも、[MANUAL.md](MANUAL.md) の「トラブルシューティング」セクションで対策を紹介しています。

### ライセンス

[PolyForm Noncommercial License 1.0.0](LICENSE)

- 個人利用・非商用利用は無償で自由に使用できます。
- 第三者による商用利用（転売・有償再配布を含む）は禁止されています。
- 改変・再配布は非商用の範囲で許可されます。
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
  - Highlight Recovery (prevents missed recoloring on reflective/glossy areas)
  - Priority control via Layer Index
- **Edge & boundary processing** — Edge Feather / AA Edge Cleanup for smooth color transitions
- **Exclusion Mask** — Paint areas to protect from recoloring (up to 30 undo steps)
- **Preview** — Zoom-capable, before/after comparison, diff view, detail preview at high zoom
- **Presets** — Save and load configurations (in-project or shared user storage, JSON import/export)
- **Batch Apply** — Apply to multiple textures at once
- **Advanced Mode** — Fine-tune internal parameters such as distance weights and hole-fill passes
- **Multilingual UI** — Auto-detects language (Japanese / English)

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

- Free for personal and noncommercial use.
- Commercial use by third parties (including resale and paid redistribution) is prohibited.
- Modification and redistribution are permitted for noncommercial purposes.
- Use in accordance with each license and guideline is the responsibility of the user.

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

アルゴリズムの開発にはこれらのモデルのテクスチャを使用しましたが、
モデルやテクスチャのデータは含まれていません。
> フェイナちゃんは開発者のお気に入りらしいです


### 連絡先 / Contact

- Misskey.io: [@yukkuri__aoba@misskey.io](https://misskey.io/@yukkuri__aoba)
