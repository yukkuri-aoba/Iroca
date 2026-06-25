# Camereo BOOTH 販促画像

BOOTH の商品ギャラリーに使う販促画像を HTML/CSS で組んだ「撮影台」です。
Unity Editor のスクショは自動取得できないため、**枠だけ先に作り、後から画像を差し込んで PNG として書き出す** 方式になっています。

デザインは **リソグラフ（蛍光ピンク × 青の 2 色刷り）風の ZINE スタイル**。クリーム紙・網点・版ズレ・トンボ・ゴム印などで「刷りもの」の質感を出しています。

```
docs/booth/
  index.html   … 8 スライドを並べたギャラリー兼撮影台
  style.css    … デザイン（インク色・ロゴ・レイアウト・紙の質感）
  images/      … ここにスクショを置く（下の表のファイル名で）
  capture.py   … （任意）スライドを一括 PNG 化する補助スクリプト
  README.md    … このファイル
```

ロゴ・アイコン・カメレオン・図解はすべて CSS/SVG で描いているので、画像が無くても全スライドはそのまま完成します。
差し込むのは **Unity のスクショだけ** で OK です。

> **フォントについて**：見出しに Google Fonts（Dela Gothic One / Anton / Zen Kaku Gothic New）を使っています。
> PNG 書き出しのときは **インターネット接続がある状態**で開いてください（フォントが読み込まれます）。

---

## 1. 画像を差し込む

`images/` フォルダに、下記のファイル名で PNG を置くだけです。
置くと点線のプレースホルダが自動で消え、画像が表示されます（ブラウザを再読み込み）。
**すべて任意**で、置かなければプレースホルダのまま書き出せます。

| スライド | ファイル名 | 推奨サイズ | 内容 |
|---|---|---|---|
| 3 Before/After | `images/ba-before.png` | 900×1200 (3:4) | 色改変 **前** のアバター |
| 3 Before/After | `images/ba-after.png` | 900×1200 (3:4) | 色改変 **後** のアバター |
| 4 カラーゾーン | `images/zone-eyedropper.png` | 1600×900 (16:9) | スポイトで色を選んでいる操作画面 |
| 5 除外マスク | `images/mask-brush.png` | 1600×900 (16:9) | ブラシでマスクを塗っている操作画面 |
| 6 プレビュー | `images/preview-diff.png` | 1600×900 (16:9) | 前後比較／差分表示の画面 |

> 画像は枠に対して `object-fit: cover`（中央トリミング）で表示されます。
> 推奨比率に近いと余白なくきれいに収まります。多少違っても問題ありません。

スライド 1（表紙）・2・7・8 は文字と図解だけで完結するため、画像は不要です。

### 任意：スクショをリソ 2 色トーン風にする

写真もインクっぽく馴染ませたいときは、`index.html` の該当の `<div class="frame" ...>` に `duo` クラスを足します
（例：`<div class="frame duo" data-ratio="16/9">`）。グレースケール化＋ピンク／青のトーンが乗ります。既定は OFF（スクショそのまま）です。

---

## 2. PNG として書き出す（BOOTH 用）

BOOTH には PNG をアップロードするので、各スライドを実寸 PNG にします。

### 方法A：ブラウザの開発者ツール（依存ゼロ・推奨）

1. `index.html` を **Chrome / Edge** で開く
2. 画面上部の **「⇆ 実寸表示の切り替え」** を押して実寸（1280px）にする
3. `F12` で開発者ツールを開く
4. `Ctrl + Shift + P` → `Capture node screenshot` と入力して選択
5. 撮影したいスライド（`<section class="slide ...">`）の要素をクリック
   - 要素を選びやすいよう、各スライドには `id="slide-1"`〜`id="slide-8"` を付けています
6. 実寸（横長 1280×720 / 正方形 1280×1280）の PNG が保存されます

> ヒント：要素を選ぶときは、開発者ツールの Elements タブで該当の
> `<section id="slide-3" ...>` を右クリック →「Capture node screenshot」でも撮れます。

### 方法B：capture.py で一括書き出し（任意）

Playwright を使って 8 枚を一括で `out/` に書き出します。依存を入れたくない場合は方法A だけで十分です。

```bash
pip install playwright
playwright install chromium
python capture.py
```

`docs/booth/out/slide-1.png` 〜 `slide-8.png` が実寸で生成されます。

---

## 3. BOOTH にアップロード

- 1 枚目（`slide-1` = 正方形の表紙）を **メイン画像** にすると、一覧サムネがきれいに揃います。
- 以降はスライド順（表紙 → 課題 → Before/After → 機能×3 → 技術 → 裏表紙）に並べると流れが自然です。

---

## カスタマイズ

- 文言：`index.html` 内のテキストを直接編集（コピー元は `../BOOTH_description.md`）。
- 色（インク）：`style.css` 冒頭の `:root` 変数 `--pink` / `--blue` / `--ink` / `--paper` を変更。
  2 色刷りなので、ピンクと青を別の組み合わせ（例：青→緑 `--blue:#00a15a`）にするだけで刷り色を総入れ替えできます。
- フォント：`index.html` の Google Fonts リンクと `style.css` の `--font-disp` / `--font-en` を差し替え。
- ロゴを画像にしたい場合：`index.html` の `<svg class="logo__mark">` を `<img>` に差し替え可能。
