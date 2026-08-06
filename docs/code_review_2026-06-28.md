# コードレビュー（2026-06-28）— アーカイブ

> **この文書は履歴として残しているアーカイブです。現況は `docs/code_review_2026-08-06.md` が正。**
>
> 2026-08-06 時点の消化状況:
>
> - **High「`package.json` が不正な JSON」** — 解消済み（parse 成功、`displayName` は「いろか」）。
> - **High「日本語ドキュメント・コメントの文字化け」** — 解消済み。ただし mojibake 検出の
>   CI 化（対応案の 3 番目）は未実施（→ 2026-08-06 レビュー §6-7）。
> - **Medium「エクスポートが Unity の CWD に依存」** — 未対応。関連して `PathUtils` の
>   判定がカルチャ依存である点が新たに判明（→ 2026-08-06 レビュー §5 中）。
> - **Medium「automation API が任意の絶対出力パスを書ける」** — 未対応。さらに
>   `output == source` のガードが無く元テクスチャを非可逆破壊できることが判明
>   （→ 2026-08-06 レビュー §5 中）。
> - **Medium「golden テストが通常の確認経路に入っていない」** — 未対応
>   （→ 2026-08-06 レビュー §6-19）。
>
> 2026-06-28 当時、この文書はリポジトリのルートに `CODE_REVIEW.md` として置かれていたが、
> 解消済みの High 指摘（「package.json が壊れている」）を公開リポのルートで宣言し続ける
> 状態になっていたため、2026-08-06 に `docs/` へ移した。

レビュー日: 2026-06-28

## 概要

Iroca は、VRChat アバター向けテクスチャの色替えを行う Unity Editor 拡張です。コードベースはおおむね次の責務に分かれています。

- `Code/UI/*`: プレビュー、マスク、プリセット、エクスポートなどの Editor UI。
- `Code/Core/*`: 画像処理本体、プレビュー Job、自動調整、マスク、性能計測。
- `Code/Infra/*`: プリセット、マスク、パス、アセット監視などの永続化処理。
- `Code/Automation/*` / `Code/McpIntegration/*`: headless 実行や MCP 連携の入口。
- `scripts/build-check/*`: Unity を起動せずに行う軽量 C# コンパイルチェック。
- `scripts/golden/*`: Python から C# ハーネスを動かす golden テスト。

UI と画像処理コアの分離は良好です。重いプレビュー/エクスポート処理をバックグラウンド化し、メインスレッドへ戻す設計も入っています。ピクセル処理について golden テストの土台がある点も強いです。

## 実施した確認

- `dotnet build scripts\build-check\IrocaEditor.csproj`
  - 結果: 成功。警告 0、エラー 0。
- `Get-Content -Raw package.json | ConvertFrom-Json | Out-Null`
  - 結果: 失敗。`package.json` が JSON として不正。
- `python -m py_compile scripts\golden\test_golden_csharp.py scripts\golden\golden_lib.py scripts\golden\synth_textures.py tools\visual_review.py tools\check_visual_review.py`
  - 結果: 成功。

作業開始時点で `README.md` には既存の未コミット変更があったため、このレビューでは変更していません。

## 指摘事項

### High: `package.json` が不正な JSON になっている

対象: `package.json:4`

`displayName` の文字列が閉じられておらず、値自体も文字化けしています。この状態だと Unity Package Manager、VPM パッケージ生成、リリース自動化、CI のメタデータ読み取りが失敗する可能性が高いです。

対応案:

- 意図した表示名を UTF-8 の正しい文字列で復元する。
- `package.json` を JSON として parse するチェックを CI または build-check に追加する。

### High: 日本語ドキュメントと一部コメントが文字化けしている

対象例: `README.md`, `MANUAL.md`, `scripts/build-check/README.md`, `scripts/golden/README.md`, `Code/*` 内の XML コメント。

英語セクションや C# のコンパイル自体は成立していますが、日本語の説明文が広範囲で文字化けしています。コメントだけなら保守性の問題ですが、パッケージ名や UI 表示文字列に混ざっている場合はユーザー体験にも直接影響します。

対応案:

- 文字化け前の UTF-8 原本から復元する。
- リポジトリ内のテキストエンコーディングを UTF-8 に統一する。
- 代表的な mojibake パターンを検出する軽量チェックを追加する。

### Medium: エクスポート処理が Unity のカレントディレクトリに依存している

対象: `Code/UI/ExportView.cs:148-271`, `Code/UI/ExportView.cs:480-483`

単体エクスポート/一括エクスポートで `Assets/.../file.png` のような asset-relative path を組み立て、そのまま `File.Exists`, `File.ReadAllBytes`, `File.WriteAllBytes` に渡しています。Unity では通常プロジェクトルートがカレントディレクトリなので動きますが、環境前提が暗黙です。

対応案:

- `System.IO` に渡すパスは絶対パスへ正規化する。
- `AssetDatabase` に渡すパスは asset-relative のまま保つ。
- 変換処理を `PathUtils` などの共通ヘルパーに寄せる。

### Medium: automation API が任意の絶対出力パスを書ける

対象: `Code/Automation/IrocaAutomation.cs:526`

`ResolveOutputPath` は rooted path を受け取ると、その絶対パスに PNG を書き出します。信頼済みのローカル batchmode API としては許容できる可能性がありますが、MCP/headless 経由で間接的に呼ばれる入口でもあるため、許可範囲を設計として明示した方が安全です。

対応案:

- 出力をプロジェクトルート配下に制限するか、任意パスを許可するかを決める。
- 任意パスを許可するなら、schema/documentation に明記する。
- 制限するなら、プロジェクト外へ解決されるパスを拒否する。

### Medium: golden テストが通常の確認経路に入っていない

対象: `scripts/golden/*`

ピクセル処理の回帰検知に使える golden テストは用意されています。ただし今回実行できたのは Python 構文チェックと C# 軽量ビルドまでで、golden テスト本体は Unity/CoreModule とハーネス環境に依存します。

対応案:

- 正式なローカル実行コマンドを README または開発者向け文書に明記する。
- リリース前チェックリストまたは CI で canonical な Unity 2022.3.22f1 環境の golden テストを走らせる。
- 現在の build-check は高速な compile gate として維持する。

## 良い点

- 画像処理コアが Unity UI 依存からかなり切り離されています。
- プレビュー/エクスポートの長時間処理に cancellation とメインスレッド復帰が用意されています。
- プリセット/マスクの永続化が小さな infra クラスに分離されています。
- pixel output の意図しない変化を検知する golden テスト方針があります。
- packed mask、pooling、parallelism 制御、preview cache など、性能面への配慮が見えます。

## 次にやるとよいこと

1. `package.json` を修正する。現時点で最も明確なリリースブロッカーです。
2. 日本語ドキュメントとメタデータの文字化けを復元する。
3. `package.json` parse と文字化け検出を build-check/CI に追加する。
4. エクスポートと automation のパス処理を正規化する。
5. Unity 2022.3.22f1 の想定環境で golden テストを実行する。
