# インタラクティブ・マニュアル

`MANUAL.md`（原稿の正）から生成する HTML 版。GitHub Pages の `docs/` 配下にあるので、
公開 URL は <https://yukkuri-aoba.github.io/Iroca/manual/>。`index.html` をそのまま
ダブルクリックしてもローカルで開ける（外部 CDN もフォントの取得もしない）。

## 中身

| ファイル | 役割 |
|---|---|
| `index.html` | 生成物。日英の本文・目次・検索インデックスを埋め込んだ 1 枚 |
| `assets/manual.css` `assets/manual.js` | 見た目と、目次・検索・言語切替・実演デモ |
| `img/ui/*.webp` | Unity Editor の実ウィンドウのスクリーンショット |
| `img/demo/*.webp` | 実 C# エンジンの出力（パラメータ実演のコマ） |
| `build_manual.py` | `MANUAL.md` → `index.html` |
| `build_assets.py` | スクリーンショットの切り出しと、実演コマの再着色 |

## 作り直す

```powershell
# 文章を直した（MANUAL.md を編集した）とき
.venv\Scripts\python.exe docs/manual/build_manual.py

# 画像も作り直すとき（dev_safe と .NET SDK が要る）
.venv\Scripts\python.exe docs/manual/build_assets.py
```

`build_manual.py` は原稿を書き換えない。スクリーンショットと実演を差し込む位置は
`build_manual.py` の `INSERTS` / `PLACEHOLDERS` / `DEMOS` に定義してある。
原稿の見出し文言を変えたら、そこのキーも合わせること（`### ` の数が日英で食い違うと
ビルドが止まるので、落ちたらメッセージを見る）。

## 実演コマの作り方

**製品 C# が唯一の正**なので、コマ画像は headless ハーネス経由の実出力を使う
（`docs/testing-architecture.md`）。被写体・サンプル色・パレットは販促素材と同じ
HAOLAN スニーカーの青 (32,0,144) → 伝統色で、色見本は実機の「自動調整」と同じ
`--autotune` 経路。許容範囲・模様保持・出力彩度の実演だけは、効果を単独で見せるため
tolerance を固定している。

## スクリーンショットの撮り直し

原板は `dev_safe/manual_shots/*.png`。撮影は Unity を起動したまま行う
（手順とスクリプトは `dev_safe/scripts/unity_ui_shot/README.md`）。

- 撮影中だけ `Code/Debug/IrocaEditor.Debug.asmdef` の `defineConstraints` にダミーを入れて
  Debug 衛星アセンブリを落とす。**配布物に Code/Debug は含まれない**ので、
  入れたまま撮ると出荷物に無い「パフォーマンス」セクションが写り込む。
  撮り終えたら必ず空配列へ戻す。
- ゾーンの詳細パラメータは製品既定に揃えてから撮る（マニュアル本文の既定値表と食い違わせない）。
- 切り出し範囲は `build_assets.py` の `SHOTS`。原板の座標は pixelsPerPoint 1.25 の実ピクセル。
