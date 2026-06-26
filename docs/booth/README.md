# Camereo BOOTH 販促画像

BOOTH の商品ギャラリーに使う販促画像を HTML/CSS で組んだ「撮影台」です。
Unity Editor のスクショは自動取得できないため、**枠だけ先に作り、後から画像を差し込んで PNG として書き出す** 方式になっています。

デザインは **暗いウォームチャコールの "スタジオ" スタイル**（Unity の黒基調エディタに馴染むが、漆黒ではない）。
アクセントは **カメレオン・グリーン 1 色**、書体は **Zen Kaku Gothic New**（見出し・本文）＋ **Geist Mono**（英字・数値）。
[Hallmark](https://github.com/) のアンチ AI-slop 原則（OKLCH トークン・アクセント 1 色・eyebrow 番号やグラデ見出しの排除・各スライド別構成）で組んでいます。

```
docs/booth/
  index.html   … 8 スライドを並べたギャラリー兼撮影台
  style.css    … レイアウト・コンポーネント（tokens.css を @import）
  tokens.css   … 色・フォント・余白の設計トークン（OKLCH）
  images/      … ここにスクショを置く（下の表のファイル名で）
  capture.py   … （任意）スライドを一括 PNG 化する補助スクリプト
  README.md    … このファイル
```

ロゴ・カメレオン・図解はすべて CSS/SVG で描いているので、画像が無くても全スライドはそのまま完成します。
差し込むのは **Unity のスクショだけ** で OK です。

> **フォントについて**：Google Fonts（Zen Kaku Gothic New / Geist Mono）を使っています。
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

> 暗い地に暗い Unity スクショが乗るので、枠は明度を一段上げた面（エレベーション）＋細い罫にして、スクショが自然に浮いて見えるようにしています。

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
- 色：`tokens.css` の `--color-paper`（地）/ `--color-accent`（アクセント）/ `--color-ink`（文字）を変更。
  すべて OKLCH なので、`--color-accent` の H（色相）だけ変えれば、アクセントを別の色（例：H=28 で珊瑚、H=255 で青）に総入れ替えできます。
- フォント：`index.html` の Google Fonts リンクと `tokens.css` の `--font-display` / `--font-mono` を差し替え。
- ロゴを画像にしたい場合：`index.html` の `<svg class="wordmark__mark">` を `<img>` に差し替え可能。
