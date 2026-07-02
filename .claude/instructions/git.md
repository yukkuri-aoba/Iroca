# Git 操作ルール

## コミットのタイミング
- テストがすべてパスした状態でのみコミットする。失敗が残っている状態でコミットしない。
- `improvement-cycle` サイクルでは、**採用が確定した変更のみ**をコミット対象とする。試行中・比較中の変更は stash やワーキングツリーに留め、コミットしない。
- 1 コミット = 1 論理変更。複数の独立した変更を 1 コミットにまとめない。

## コミットメッセージ形式（Conventional Commits）
```
<type>(<scope>): <概要（日本語可）>
```

### type 一覧
| type | 用途 |
|------|------|
| `feat` | 新機能追加 |
| `fix` | バグ修正 |
| `perf` | パフォーマンス改善（アルゴリズム改善など） |
| `test` | テストの追加・修正 |
| `refactor` | 動作を変えないリファクタリング |
| `chore` | ビルド設定・CI・ドキュメント等の雑務 |
| `docs` | ドキュメントのみの変更 |

### scope の例
- `mask` — マスク生成ロジック
- `color` — 色抽出・色マッチングロジック
- `ui` — Unity Editor ウィンドウ (CamereoWindow)
- `export` — エクスポート処理
- `preset` — プリセットデータ
- `test` — テストコード
- `ci` — GitHub Actions ワークフロー

### 例
```
perf(mask): フラッドフィル後の連結成分フィルタ閾値を動的計算に変更
fix(color): HSV 変換時の Hue 折り返し処理が負値で誤判定する問題を修正
test(mask): Feina 衣装テクスチャの IoU テストケースを追加
chore(ci): release.yml に SHA256 検証ステップを追加
```

## ブランチ運用

### 基本方針：`develop` 直コミットを原則とする
このプロジェクトは次々と変更を実装する高速開発サイクルのため、**ブランチを切らずに `develop` へ直接コミット**するのが原則。

- `main` — 安定版。直接プッシュは原則禁止（リリース bot コミットを除く）。
- `develop` — **メインの開発ブランチ。通常の修正・機能追加・アルゴリズム改善はここに直接コミット。**
- `feature/<短い説明>` — **例外的な場合のみ使用**。壊れる可能性が高い実験的変更で、失敗したら丸ごと捨てたいときだけブランチを切る。数コミット以内で完結させ、完了後すぐ `develop` へマージして削除する（長生きさせない）。
- リリース時は `develop` を `main` へマージし、タグを打つ。

### ブランチを切ってよいケース（限定）
- 実装が完全に失敗した場合に丸ごと破棄したい実験
- 上記以外は `develop` へ直接コミット

### ブランチを切ってはいけないケース
- 通常のバグ修正
- アルゴリズムの改善
- 機能追加（改善サイクル内の変更はすべて含む）
- ドキュメント・テスト・chore 系の変更

## リリース手順
詳細は `docs/RELEASING.md` が正。概要は以下の通り。

1. `CHANGELOG.md` に `## [<VERSION>]` 節を記載する。
2. Unity で `Iroca_Ver<VERSION>.unitypackage` をエクスポートする（`BuildHelper`）。
3. `scripts/Build-VpmPackage.ps1 -Version <VERSION> -UnityPackagePath <unitypackageのパス>` を実行する。
   package.json / docs/index.json / zip + SHA256 が更新される。
   **`-UnityPackagePath` を省略しない**（省略した zip は SHA256 が最終版と一致しない）。
4. `package.json` / `docs/index.json` / `CHANGELOG.md` をコミットする。
5. `develop` を `main` へマージし、タグを push する（いずれもユーザー確認必須の操作）：
   ```
   git tag v<VERSION>
   git push origin v<VERSION>
   ```
   CI（release.yml）がバージョン整合を検証し、**draft** リリースを作成する。
6. draft へ zip と unitypackage を手動アップロードし、本文のチェックリスト節を消して publish する。
7. publish 後、release-verify ワークフローが zip の SHA256 を listing と照合する。green を確認して完了。

> タグ形式は必ず `v` プレフィックスを付けること（例: `v0.2.0`）。これが `release.yml` の起動条件。
> CI が作るのは draft まで。資産のアップロードと publish は手動ステップ。

## エージェントが自動実行してよい操作
- `git add` / `git commit`（テストパス済みの変更に限る）
- `git status` / `git diff` / `git log`（読み取り専用操作）
- `git stash` / `git stash pop`（試行中変更の一時退避）

## エージェントが確認なしに実行してはいけない操作
- `git push`（リモートへの反映は必ずユーザー確認を取る）
- `git tag` の作成・削除
- `git reset --hard` / `git clean -fd`（作業ツリーの破壊的操作）
- `git rebase` / `git merge`（履歴改変・統合操作）
- `git push --force` / `git push --force-with-lease`
