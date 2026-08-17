# Iroca（旧 VACC）— エージェント運用ガイド

このリポジトリでは、Claude と Codex が同じ運用ルールに従う。Claude 向けの既存ルールは `CLAUDE.md` と `.claude/instructions/` に分割されているため、作業開始前にこのファイルと併せて以下のファイルを必ず読むこと。

- `CLAUDE.md`
- `.claude/instructions/general.md`
- `.claude/instructions/improvement-cycle.md`
- `.claude/instructions/git.md`
- `.claude/instructions/notify.md`

上記モジュールの内容がこのファイルや他の指示と矛盾する場合は、モジュール側を優先する。

## 要約

- ユーザーへの応答は日本語で行う。
- 製品 C#（`Code/Core/PixelProcessor.cs` ほか）が唯一の正。アルゴリズムを Python で再実装して測定しない。テスト・計測・視覚レビューは実 C# の出力を対象にする。
- Unity Editor UI に新しい操作要素を追加するときは、必ずツールチップを付ける。
- アルゴリズム改善は、実 C# の計測、GT との比較、視覚レビューを含む自律改善サイクルで進める。1 ステップごとの承認は求めない。
- 採用が確定し、テストがすべてパスした変更だけを、Git ルールに従ってコミットする。
- 作業完了時は通知ルールに従って ntfy 通知を送る。

詳細な手順・停止条件・Git 操作の制約は、上記のモジュールを正とする。
