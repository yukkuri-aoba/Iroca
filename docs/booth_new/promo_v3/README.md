# BOOTH 販促画像キット v3（2026-09 版）— BOOTH ネイティブ × 実物主義

v2（`../promo/`）のコピーから作った改稿版。**v2 は上書きしていない**（比較用に残す）。
写真素材は v2 と同じ実物（HAOLAN の元テクスチャを実 C# エンジンで色替えし、Unity 内スタジオで撮影）。

## v2 との違い（「AI が作った感」への対策）

Web 調査（AI 製デザインの兆候・BOOTH の慣習）から導いた方針。根拠は末尾の参考 URL。

| v2 の癖（AI 製に見える要因） | v3 での置き換え |
|---|---|
| 全 6 枚が「中央揃え大見出し＋灰色サブ＋灰色フッター」の同型 | 左寄せ・1 枚 1 主張。枚ごとに構図を変える（キャラ主役／2 連比較／実 UI＋注釈／平置き PNG） |
| 文字だけの 3 カラム（使い方・仕様）＝典型的なフィーチャーグリッド | 使い方は **実際の いろか ウィンドウ**に赤丸・番号で注釈。仕様は **書き出した PNG の実物**の下にタグで最小限 |
| トラッキングを広げた英字 eyebrow、`→` 文字、文字グラデ | 手描き矢印・赤丸（seed 固定の SVG）、蛍光マーカー、「無料」丸タグ、手書き系書体の書き込み |
| 評価語（安っぽく・質感・陰影） | 見れば分かる部位を名指し（組紐・宝石）、実数だけ（許容範囲 0.25、4096×4096、プリセット名） |
| 「〜だけ。」「X も、Y も。」の反復 | 見出しごとに文型を変える：困りごと／数字／部位／手順／実物／失敗の名指し |
| 製作者・作品の気配がない | 各枚にワードマーク（明朝）・ページ番号（手書き）・モデルクレジット |

守っていること: **数字と名前は実物のものしか書かない**（捏造の具体性は逆効果）。
写真はレタッチなし。UI は起動中の実画面のキャプチャ。

## ファイル構成

| ファイル | 役割 |
|---|---|
| `build_html.py` | コンプ HTML を生成 → `dev_safe/promo/booth_new_v3/booth-promo-v3.html`（生成物は公開リポに置かない） |
| `export_slides.py` | 各ボードを 1280×1280 PNG に書き出し → `dev_safe/promo/booth_new_v3/out/slide-N.png`（headless Chrome / Edge） |
| `capture_ui.ps1` | 起動中の Unity のトップレベルウィンドウ（いろか ウィンドウ含む）を PrintWindow で PNG 保存。マウスや前面を奪わない |
| `img/hero_pair.jpg` ほか | v2 と同じ撮影写真（`../promo/img/` のコピー） |
| `img/compare-*.png` | SLIDE 6 用（v2 では dev_safe 側にあったものを同梱） |
| `img/ui_panel.png` | いろか ウィンドウ左列（①元テクスチャ〜加工設定）の切り出し。**プレビュー領域は含めない**（他アバターのテクスチャを写さない） |
| `img/atlas_before.jpg` / `atlas_after.jpg` | Iroca_Dev `Assets/_Promo/Textures/sneakers_00_original.png` / `verify_red.png` を白地 1200px に縮小 |

書体: 見出し・本文は Noto Sans JP（OS の VF）。注釈は Klee One / Yomogi（OFL）。
フォントは `dev_safe/promo/booth_new_v3/fonts/` に置き、無ければ `build_html.py` が Google Fonts の GitHub から取得する。

## ワークフロー

```
python docs/booth_new/promo_v3/build_html.py      # 1. HTML 生成
python docs/booth_new/promo_v3/export_slides.py   # 2. PNG 書き出し（番号を渡すと 1 枚だけ）
```

検収: 書き出した PNG を 150px に縮小して見出しが読めるか（BOOTH 一覧）、中央 1:1 と横長 1.91:1（X カード）の帯に主役が収まっているかを確認する。

## SLIDE 4 の UI を撮り直す（HAOLAN を読み込んだ全体ショットにする場合）

1. Unity（Iroca_Dev）で いろか ウィンドウを**フローティング**で開き、HAOLAN のスニーカーテクスチャを読み込んで
   `preset_samples/HAOLAN_Sneakers_Red.json` を適用する（プレビューに赤が出る状態）。
2. `powershell -File docs/booth_new/promo_v3/capture_ui.ps1 -Pid_ <UnityのPID> -Out <保存先.png>`
   （Unity の PID は `Get-Process Unity`）。タイトル「いろか」のウィンドウが 1 枚の PNG になる。
3. 必要な範囲を切り出して `img/ui_panel.png` を置き換え、`build_html.py` の `PX, PY, PS` と赤丸の座標を合わせる。

## 権利
- HAOLAN（かなﾘぁさんち）: 規約でクレジット必須（表記名: かなリぁ）。画像内フッターと BOOTH 説明文の両方に入れる。
- Klee One / Yomogi: SIL Open Font License（`fonts/OFL-*.txt`）。

## 参考（調査時に当たった主な URL）
- AI 製デザインの兆候: https://www.developersdigest.tech/blog/ai-design-slop-and-how-to-spot-it / https://sikora.software/blog/ai-website-design / https://growthguys.tech/blog/genuine-website-vs-ai-slop.html（Name Swap Test）
- スライド: https://perceptis.ai/blog/how-to-make-ai-slides-that-don-t-look-ai-generated-a-consultant-s-guide-2026
- 日本語コピー: https://zenn.dev/correlate_dev/articles/ai-writing-rhythm-taigendome-technique / https://blog.btrax.com/jp/apple-copies/ / https://www.kwm.co.jp/blog/ai-catchphrase/
- BOOTH の慣習: https://note.com/fortuneko/n/n909f5fcf1fcb / https://ameblo.jp/altkb43/entry-12598250577.html / 実例 lilToon https://booth.pm/ja/items/3087170、TexTransTool https://booth.pm/ja/items/4833984、AAO https://booth.pm/ja/items/4885109、AvatarMenuCreator https://booth.pm/ja/items/4419509
