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
| `pre-commit` | `Code/` 変更コミット時に視覚レビュー承認の鮮度と品質ゲート較正を検査（`tools/check_visual_review.py`） |

## バイパス

- `SKIP_VISUAL_REVIEW=1 git commit ...` — 視覚レビューチェックを回避。**出力に影響しない変更（IO・プレビュー・コメントのみ）に限る**。
- `SKIP_QUALITY_GATE=1` — 品質ゲート較正検証のみ回避。

## 注意

- `core.hooksPath` が未設定（または `.git/hooks` を指したまま）だとフックは**一切動かない**。
  `git config core.hooksPath` の出力が `scripts/hooks` であることを確認すること。
