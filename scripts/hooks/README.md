# Git フック（追跡版）

pre-commit フックの実体をここで git 管理する。`.git/hooks/` 直置きは clone や事故で
黙って消える（実際に 2026-07-02 のレビューで「フック不在のまま数日運用」が発覚した）ため、
追跡ディレクトリを直接フックパスに指定する方式へ移行した。

## 有効化（clone 直後に 1 回）

```
git config core.hooksPath scripts/hooks
```

## フック一覧

| フック | 役割 |
|--------|------|
| `pre-commit` | 視覚レビューの範囲のファイル（ハーネスがコンパイルする製品ソース・`scripts/headless-run`・tools の測定器。`tools/review_state.py`）を含むコミット時に、固定設定とワークフローの承認・**ステージした中身**が「HEAD と同じ」か「承認した比較で測った中身と同じ」か（ファイル単位）・dev_safe の測定器と基準・品質ゲート較正を検査（`tools/check_visual_review.py`）。承認の時刻やファイルの mtime は見ない（2026-11-10〜） |

## バイパス

- `SKIP_VISUAL_REVIEW=1 git commit ...` — 視覚レビューチェックを回避。**出力に影響しない変更（IO・プレビュー・コメントのみ）に限る**。
  ワークフロー承認と品質ゲート較正も含めて、フックの検査を全部飛ばす。
- `SKIP_QUALITY_GATE=1` — 品質ゲート較正検証のみ回避（dev_safe のフックが、しきい値・ラベル・CSV を変えたときに回す）。

## dev_safe のフック

dev_safe（別のリポジトリ）にも `dev_safe/hooks/pre-commit` がある（2026-11-10〜）。dev_safe のルートで
`git config core.hooksPath hooks` を 1 回実行して有効化する。測定器と基準（fixtures.py・測定スクリプト・ベースライン・
`subjects.json` など）をステージしたコミットに視覚レビューの承認を求め、品質ゲートのしきい値・ラベル・CSV を変えたら
`quality_report.py --validate` を回す（本体の `tools/check_visual_review.py --dev-safe`）。

## 注意

- `core.hooksPath` が未設定（または `.git/hooks` を指したまま）だとフックは**一切動かない**。
  `git config core.hooksPath` の出力が `scripts/hooks` であることを確認すること。
