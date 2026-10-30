# テスト構成（testing-architecture）

CLAUDE.md / `.claude/instructions/improvement-cycle.md` が「テスト構成の正」として参照する文書。
**clone から全テスト緑までの手順は `docs/development-setup.md` が正**（本ファイルは構成の説明）。
2026-08-03 に環境ごと再構築した際、旧版（ローカルのみ・git 非追跡）が失われていたため書き直した。
**再発防止のため本ファイルは git 追跡する**（テスト構成の説明であり秘匿情報は含まない）。

## 大原則

- **製品 C#（`Code/Core/Pipeline/PixelProcessor.cs` ほか）が唯一の正。** テスト・計測・視覚レビューは
  必ず実 C# の出力を測る。アルゴリズムの Python 再実装は作らない（過去、Python プロキシが
  製品から IoU 0.26 乖離し、改善サイクルがプロキシを測っていた事故がある）。
- **GT（正解データ）は PSD のレイヤー情報が正。** `dev_safe/scripts/` の `build_*_gt.py` /
  `analyze_*_psd.py` で PSD から生成する。

## 構成要素

### 評価条件の境界（2026-09-05）

同じ C# でも入力した補助情報と操作が違えば別の品質を測る。**「マスクなし」という
だけではワンショットを意味しない。** 機械定義は `tools/workflow_contract.py`。
条件を合算して「製品品質」と報告しない。
主な条件は `pytest -m oneshot` / `pytest -m assisted_include` /
`pytest -m oracle_masked` で個別実行できる（共通のインフラ検査は全体実行で行う）。

| 条件ID | 入力・操作 | 結果の意味 |
|---|---|---|
| `oneshot` | GT内p50クリック → SAM証拠付き自動調整。手動マスクなし | 初回出力。証拠は強制包含ではない |
| `assisted_include` | 同じクリックの自動調整 + SAM提案を含める。flood/ドメイン過少(証拠不採用)の提案は不採用 | 明記した追加操作ポリシー後の品質 |
| `oracle_masked` | PSDレイヤーから合成した部位領域を除外マスクにし、従来自動調整 | 理想部位情報を与えた上限性能。実ユーザー/AIのマスク精度ではない |
| `fixed` / `fixed_excluded` | fixtureの固定設定、必要ならGTから合成した部分除外 | 再着色・除外契約。自動調整や初回体験ではない |
| `fallback` | 証拠なしの従来自動調整 | フォールバック経路の回帰 |

**ホールドアウト**（2026-09-24 に枠、2026-10-10 から運用）: `HOLDOUT_SUBJECTS` は上の条件のどれにも使わない被写体で、
`SUBJECTS` と交わらない（`scripts/source_checks/test_workflow_contract.py` が検査）。採否判断のときだけ
`dev_safe/scripts/holdout_eval.py` がワンショット p50 の集計値を出し、`Tests/Baselines/holdout_log.jsonl` に
見た回数ごと記録する。次に GT を足すときからここへ入れる（ユーザー決定）。一覧は `SUBJECTS` と同じく
`dev_safe/Tests/subjects.json`（`holdout_subjects`）にあり、2026-10-10 に回帰の被写体と別の作者のアバター 5 体から
12 被写体を入れた。GT は `dev_safe/scripts/build_holdout_gt.py` が作る。部位レイヤーの無い PSD は、PSD の島
（作者の島マスク / UV 線の塗りつぶし）と同じ塗りの島から作る素材単位、部位レイヤーのある PSD は
部位レイヤー ∩ 島。テスト側の定義は `fixtures.HOLDOUT_REGISTRY` で、`SUBJECT_REGISTRY` には入れない。
見るたびにホールドアウトへ合わせてしまうので、被写体ごとの延べの回数が 20 回に届いたら holdout_eval.py が
入れ替えの時期を知らせる（2026-10-23。次に GT を足すとき、届いた被写体を `SUBJECTS` へ移し、新しい被写体を
ホールドアウトにする）。集計には ΔE 球（下記）に負けた被写体の数も出す。
2026-10-23 に最初の入れ替えをした（ユーザー了承）: 延べ 16〜17 回見た 5 体（14 被写体）を `SUBJECTS` へ移し、
回帰の被写体は 33 件（9 体）になった。移した被写体の定義は `fixtures.py` で、どちらの一覧に属すかは `subjects.json` が決める。

**汎化の物差し**（2026-10-23〜、`dev_safe/scripts/generalization_report.py`）: ホールドアウトを見る前に、
手元の被写体で過学習しない変更かを確かめる。回帰の被写体は件数より描いた人（アバター）の数が少ない（入れ替え前は
19 件で 4 組、後は 33 件で 9 組）ので、`subjects.json` の `subject_groups` でアバターごとにまとめる。`run` はワンショットを
開発に使ったクリック（p25/p50/p95）と使っていないクリック（p05/p75/p99）の 被写体 × 6 件（33 被写体で 198 件）で回し
（DLL は `_harness_cache/builds/` へ写して使う）、
`compare` はアバター別の平均・変更のきっかけのアバター（`--motivated-by`）を除いた平均・ケースごとの退行床・
素朴な基準線（クリック画素の色から CIELAB ΔE76 ≤ 16 の球、design IoU で採点）に 0.05 以上負けた数を並べて
採否の目安を出す。`sweep` は定数の掃引から台地とアバター単位の交差検証（LOAO）を出す。採否の決まりは
`.claude/instructions/improvement-cycle.md` の「過学習を防ぐ採否の物差し」。

GTは採点とテスト用クリック位置の選定に使う。`oneshot` / `assisted_include` の処理入力に
GTマスクを渡さない。証拠・含める・除外・採点GTは役割を別に記録する。
プリセット比較も「作者の調整済み出力への近さ」であり、GT精度の代わりにはしない。
**回帰なし・満足基準達成・未計測(skip)は別の結果。** ワンショットの満足基準は
precision_design ≥ 0.98 / recall ≥ 0.95 / 残存島 ≤ 0.5%。既知の未達はxfailで可視化する。

### GT の 3 値と二本立ての採点（2026-09-05）

製品の契約（ユーザー決定）: **スポイト 1 回は「色」を選ぶ。同じ色は部位が違っても一緒に染まり、
部位の切り分けはマスク（含める / 除外 / シード）の仕事。** GT は PSD レイヤー（部位）で引かれて
いるので、同色の別部位（アトラスの同色きょうだい、柄の同色ストライプ）を塗った画素は従来の
precision で過検出に数えられ、設計どおりの挙動と本当の欠陥（白がクリームを巻き込む）が
同じ赤になっていた。この曖昧性を採点側で吸収する。

- GT に第 3 値 **「許容」** を足す。許容 = 対象部位の外にあるが、対象と色で分離できない
  可視画素。導出は `dev_safe/Tests/regression/gt_tolerance.py` が機械的に行う: OkLab を
  0.01 幅で量子化し、bin の **尤度比 (n_対象/N_対象)/(n_他/N_他) ≥ 1**（その色は対象以外より
  対象に特徴的）なら許容 bin。製品の tolerance にもキャラ固有の定数にも依存しない。
  「対象に存在する色なら許容」は GT 縁の混入 91 px が背景 800 万 px を許容にしたので却下、
  「再現率 r に必要な最小色集合」は同一コピーのきょうだいに (1−r) の残差を必ず残すので却下。
- 採点は **strict と design の二本立て**。strict（`iou` / `precision`）は PSD 部位 GT どおり、
  design（`iou_design` / `precision_design`）は許容画素を過検出の分母から外す。
  `tolerated_frac` は変更画素のうち許容へ落ちた割合（床は置かず、緩みの監視用に凍結）。
  **満足判定と退行床は design 側**（`tools/workflow_contract.py` の `SELECTION_SLACK`）。
  strict は情報として凍結・併記する。
- **見逃し側に許容はない。** ハイライト復帰は製品機能なので、対象内の取りこぼしは欠陥のまま。
- 規則と設計判断が食い違うケースは `dev_safe/Tests/Baselines/gt_tolerance_overrides.json` で
  **理由つき**の上書きができる（`tolerate` で許容に足す / `strict` で外す。理由なしは拒否）。
  適用件数は審査ページと `workflow_review/review.json` に常時出す。上書きマスクの PNG は
  `Tests/Baselines/**/*.png` が gitignore なので `texture_sample/ground_truth/` 配下に置く。
- 審査: `python dev_safe/scripts/build_tolerance_gt.py` が被写体ごとの許容を画（緑=GT、
  濃灰=許容）にする。38 パネルと oneshot_review の GT 採点タイルは灰=許容を赤と分けて描く。
  規則や定数を変えたら `RULE_VERSION` を上げ、`measure_evidence_autotune.py` の `--baseline` /
  `--assisted-baseline` で再凍結する。

### 主経路と追加操作後の必須ゲート

- `test_autotune_evidence`: ワンショット。既定p50 19件、FULLで57件。
- `test_assisted_include`: 追加操作後p50 19件。独立した `assisted_include_baseline.json` に
  対するIoU/precision/recall/残存島の退行と、提案採用がno-opに変わる事故を検出する。
- `test_baselines_present`: 上記2種とプリセット比較の必須ファイル・全ケース・有限な指標値を
  検査。欠落はfail（個別実行時もエラー）。ONNX等の環境不備によるskipとは区別する。
- 追加操作後の基準は `python dev_safe/scripts/measure_evidence_autotune.py --assisted-baseline`
  で全19件を別ファイルに凍結する。ワンショットの基準は書き換えない。

`visual_review.py snapshot / compare / approve` は固定設定に加え、
`tools/workflow_review.py` のワンショット19件・追加操作後19件も必須にする。
追加パネルは `dev_safe/Tests/workflow_review/compare/` に条件ID別に保存する。
元画像・変更前・変更後、AI証拠、GT採点、等倍クロップを分けて表示する。
**両方のディレクトリのうち変更前から変化したパネルを1枚ずつ目視してから**
`python tools/visual_review.py approve --note "実際の確認範囲と既知の未達"` を実行する
（変更前とビット一致のパネルは読まなくてよく、変更前の無いパネルは変化ありとして扱う。下記「視覚レビュー」）。

既存の固定設定レビューが有効なら、ワークフローだけは
`python tools/workflow_review.py compare` → 38枚のうち変化したパネルの目視 →
`python tools/workflow_review.py approve --note "確認範囲と既知の未達"` で更新できる。
pre-commitは**両方の承認**を要求する。部分実行・回帰・古いコード/測定器/ベースライン/
入力資産・欠落/変更画像は承認不可。再compareすると旧承認は無効になる。
このゲートもnet8ハーネスの評価であり、Unity Editorの操作E2Eを代替しない。

実行キャッシュは共通除外配列だけでなく、ゾーン別 `includeMask` / `excludeMask` /
`evidenceMask` のrawヘッダと内容もキーへ含める。同じファイル名・mtime・サイズで
マスクを更新しても再計測し、欠落時に古い出力で代用しない。

```
Iroca 本体リポジトリ（公開）
├─ Code/                        製品 C#（唯一の正）
├─ scripts/headless-run/        実 C# を Unity なしで実行するハーネス
│   ├─ Harness.csproj           Code/Core 全部 + MaskSuggest/Ops を同一アセンブリに
│   │                           取り込み（internal アクセス可）、実 UnityEngine.CoreModule.dll を
│   │                           参照して net8.0 でビルド。取り込む一覧は HarnessSources.props
│   │                           （dev_safe の局所プローブも同じものを Import する）
│   └─ Harness.cs               raw 形式 I/O（[int32 w][int32 h][payload]）+ 各種検証モード
├─ scripts/build-check/         dotnet 単体での型チェック用 csproj 5 本
│   │                           （IrocaEditor / IrocaEditor.Debug / IrocaNdmfCheck / IrocaVrcsdkCheck / IrocaSentisCheck）
│   │                           ※ UnityEngine.dll + UnityEditor.dll が必要（下記「前提」参照）
│   │                           ※ IrocaNdmfCheck は NDMF 入りのホスト、IrocaVrcsdkCheck は VRChat SDK 入りのホスト
│   │                             （NDMF は参照しない）、IrocaSentisCheck は ML ホストが前提
├─ scripts/golden/              golden（出力ハッシュ固定）テスト。expected/ に期待出力 PNG
├─ scripts/source_checks/       C# ソースを読むだけの構造検査（ハーネス・資産不要）
├─ scripts/unit-run/            Unity 不要の部品（Infra のパス・書き込み、Scene クリックの当たり判定等）のユニットテスト
│                               （ハーネスと分けてあるので視覚レビューゲートの対象外）
├─ scripts/editor-tests/        Unity 実機の EditMode テスト（保存/復元・書き出し・Mono とハーネスの一致・メッシュ探索）。
│                               配布物(Code/)の外。scripts/Run-EditorTests.ps1 で batchmode 実行
├─ scripts/hooks/pre-commit     出荷ゲート（下記）
└─ tools/visual_review.py       視覚レビュー（snapshot / compare / approve）
    tools/check_visual_review.py  pre-commit から呼ばれる承認鮮度・較正チェック

dev_safe/（別リポジトリ・プライベート: yukkuri-aoba/Iroca_dev_safe。本体からは gitignore）
├─ Tests/regression/            pytest 本体（IoU 回帰・品質ゲート・synth・autotune・parity 等）
│   └─ fixtures.py              ハーネスのビルド・実行・ゾーン組み立ての一元化
├─ Tests/Baselines/             ベースライン JSON（再生成は明示的に行う）
├─ Tests/visual_review/         視覚レビューの成果物（approved.json / compare PNG）
├─ texture_sample/              PSD・テクスチャ素材（GT の源泉）
└─ scripts/                     GT 生成スクリプト（build_*_gt.py / analyze_*_psd.py）
```

## 実行の前提

| 要素 | 要件 | 備考 |
|------|------|------|
| Python | `.venv`（リポジトリ直下） | `dev_safe/Tests/requirements.txt` を pip install |
| .NET SDK | 8.0+（`dotnet --version`） | ハーネス・build-check のビルドに必要 |
| Unity CoreModule DLL | ハーネスのビルドに必要 | 既定: `C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\Managed`。無い場合は `UnityManaged` 環境変数で上書き（下記） |
| Unity Editor 一式 | build-check のみ必要 | `UnityEngine.dll`+`UnityEditor.dll` を参照。`UNITY_EDITOR_PATH` で上書き可 |
| ホスト Unity プロジェクト | Editor UI の実機確認・`#if UNITY_EDITOR` 内の検証に必要 | `yukkuri-aoba/Iroca_Unity`（private）。`scripts/Link-HostPackage.ps1` でリンクする（下記「ホスト Unity プロジェクト」） |
| ML ホスト（Sentis 入り） | `IrocaSentisCheck` と `Code/SentisIntegration/` の検証に必要 | `yukkuri-aoba/Iroca_MLDev`（下記「ML ホスト」） |

### Unity 導入済み環境（推奨。2026-08-04 に本機で復旧）

Unity 2022.3.22f1 が既定パスに入っていれば、`UnityManaged` / `UNITY_EDITOR_PATH` は
どちらも `...\Editor\Data\Managed` を指す **1 つの値で足りる**。両方をここに向けておくと、
ハーネス・build-check・Sentis チェックが同じ Unity を見る:

```powershell
$real = "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\Managed"
[Environment]::SetEnvironmentVariable("UnityManaged", $real, "User")
[Environment]::SetEnvironmentVariable("UNITY_EDITOR_PATH", $real, "User")
```

下の「Unity 未インストール環境」の 2 DLL シムは、Unity が入ったら**使わない**
（CoreModule 以外のモジュールを参照する変更が入った時に、原因の分かりにくい参照解決エラーになる）。

### Unity 未インストール環境でのハーネス実行（2026-08-03 確立）

ハーネスは `UnityEngine.CoreModule.dll`（+ 依存の `UnityEngine.SharedInternalsModule.dll`）だけ
あればビルド・実行できる。Unity 本体が無いマシンでは、この 2 DLL を以下のレイアウトで置き、
環境変数 `UnityManaged` にディレクトリを指定する:

```
%LOCALAPPDATA%\Iroca\UnityManaged\
└─ UnityEngine\
   ├─ UnityEngine.CoreModule.dll
   └─ UnityEngine.SharedInternalsModule.dll
```

```powershell
[Environment]::SetEnvironmentVariable("UnityManaged", "$env:LOCALAPPDATA\Iroca\UnityManaged", "User")
```

DLL の出所は Unity 2022.3.22f1 の `Editor\Data\Managed\UnityEngine\`（過去ビルドの
`scripts/headless-run/bin/Release/` にもコピーが残る）。**DLL は Unity のライセンス物なので
リポジトリにコミットしない。**

## ホスト Unity プロジェクト

`.meta` はリポジトリで追跡しない（`.gitignore`）。**ホスト Unity プロジェクトから本体を
ローカルパッケージ参照し、そこで `.meta` を生成させる**のが正の構成。headless ハーネスと
build-check は Unity を起動しないので、この構成が無くても pytest は緑になる。だが
Editor UI の実機確認と `#if UNITY_EDITOR` 内のコンパイル検証は Unity 経由でしかできない。

- 実体は **`yukkuri-aoba/Iroca_Unity`（private）** で git 管理する。撮影台シーン
  (`Assets/_Promo/PromoStage.unity`) と Editor スクリプトはここにしか無い自作物。
  有料アバター資産を含むため public にしない。
- 本機のホスト: `C:\Users\k6803\Documents\Avatar_Projects\Iroca_Dev`（2022.3.22f1 / VRC SDK 3.10.4 / lilToon）
- リンクは **`scripts/Link-HostPackage.ps1`** で行う:
  ```powershell
  .\scripts\Link-HostPackage.ps1 -HostProject "C:\...\Avatar_Projects\Iroca_Dev"
  .\scripts\Link-HostPackage.ps1 -HostProject "..." -Unlink   # 解除
  ```
  ホスト側に `Packages/com.yukkuri-aoba.iroca/` を作り、`Code/` だけをディレクトリ
  ジャンクションで繋ぎ、`package.json` などの配布物をコピーする。`Packages/<名前>/` は
  Unity が自動で埋め込みパッケージとして認識するので manifest.json への追記は不要。
  配布時と同じ形なので `PackageInfo.FindForAssembly` も期待どおり解決される。
  **package.json のバージョンを上げたらスクリプトを再実行してコピーを同期すること。**

#### manifest.json に `file:<リポジトリルート>` を書いてはいけない（2026-08-04 に実測で却下）

一見自然だが破綻する。リポジトリルートには dev_safe（約 10GB の PSD/テクスチャ）と
dotnet のビルド成果物が同居しており、Unity がそれらを全部アセットとして取り込む:

- `dev_safe/texture_sample` の PSD/PNG を延々インポートし続けて実用にならない
  （`.venv` や `.git` は先頭が `.` なので Unity が無視するが、`dev_safe` は無視されない）
- `scripts/build-check/bin/com.yukkuri-aoba.iroca.Editor.dll` が **同名アセンブリの
  プラグイン** として読み込まれ、ソースからのコンパイルと型が衝突する
  （`warning CS0436: The type ... conflicts with the imported type ...`）。
  `scripts/headless-run/obj/Release/IrocaHeadless.dll` も
  `Assembly ... will not be loaded due to errors` になる
- Unity 側で生成された `.meta` が dev_safe（別リポジトリ）に大量に流れ込む

- Unity を起動せずコンパイルだけ確認する:
  ```powershell
  & "C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe" `
      -batchmode -nographics -quit -projectPath "<ホスト>" -logFile "<ログ>"
  ```
  Unity.exe は GUI サブシステムなので **PowerShell は待たずに戻る**。終了判定は
  プロセス消滅かログの `Exiting batchmode successfully` で見ること。

### ML ホスト（Sentis 用・IrocaSentisCheck の前提）

`Code/SentisIntegration/` は `IROCA_SENTIS_PRESENT` ゲートの別 asmdef なので、通常のホスト
（VRChat 側）でも `IrocaEditor.csproj` でもコンパイルされない。ここを検査するのが
`scripts/build-check/IrocaSentisCheck.csproj` で、com.unity.sentis 2.x を入れた **別の**
ホストプロジェクトの `Library\ScriptAssemblies`（Unity.Sentis / Burst / Collections /
Mathematics）を要求する。CI（`ci.yml`）は対象外＝ローカル専用のチェック。

そのプロジェクトは **`yukkuri-aoba/Iroca_MLDev`（Sentis 2.1.3 入り）** で、csproj の既定パス
`../../../Avatar_Projects/Iroca_MLDev` に置けば環境変数の上書きは不要:

```powershell
git clone https://github.com/yukkuri-aoba/Iroca_MLDev "$env:USERPROFILE\Documents\Avatar_Projects\Iroca_MLDev"
.\scripts\Link-HostPackage.ps1 -HostProject "$env:USERPROFILE\Documents\Avatar_Projects\Iroca_MLDev"
# Unity で一度開いて Library\ScriptAssemblies を生成させる（batchmode でよい）
dotnet build scripts/build-check/IrocaSentisCheck.csproj
```

別の場所に置くなら `-p:SentisAssembliesPath=` か `IROCA_SENTIS_ASSEMBLIES` で上書きする。
Unity で開くと `Iroca.SentisIntegration.dll` も `Library\ScriptAssemblies` に出る。
**dotnet 側は 1 アセンブリにまとめてコンパイルするので asmdef 境界は再現しない。**
境界（internal の見え方など）の最終確認はこの Unity 側コンパイルが正。

## 標準の実行コマンド

```powershell
# 回帰テスト一式（改善サイクルの基本形）
.\.venv\Scripts\python.exe -m pytest dev_safe/Tests/regression/ -q

# 大規模ケース（8K/16K、数 GB のディスク書き込みあり）を除外する場合
.\.venv\Scripts\python.exe -m pytest dev_safe/Tests/regression/ -q -m "not slow"

# 実 C# 品質ゲートを bandana 以外の被写体（hair/costume/sneakers）まで広げる（旧名 VACC_CSHARP_GATE_FULL も効く）
$env:IROCA_CSHARP_GATE_FULL = "1"

# 自動調整の品質ゲート(compute_all_metrics)を全被写体へ広げる（採否判断時に必須。この変数は旧名のみで IROCA_ の名前は無い）
$env:VACC_AUTOTUNE_GATE_FULL = "1"

# golden（C# 出力ハッシュの固定）・ソース構造検査・ユニットテスト — dev_safe を必要としない自己完結テスト
.\.venv\Scripts\python.exe -m pytest scripts/golden scripts/source_checks scripts/unit-run -q
```

**CI（`ci.yml` の build-check）が回すのはこの 3 つだけ**（2026-09-24〜）。テクスチャ資産は
プライベートの dev_safe にあるので、IoU などの回帰は CI では走らない。CI は Linux なので、
golden は `IROCA_GOLDEN_TOLERANT=1` でハッシュ不一致を `scripts/golden/expected/*.png` との
画素比較（最大差 2・差のある画素 1% まで）で判定し、`IROCA_REQUIRE_HARNESS=1` で
「ハーネスが無いので skip」を fail にしている。ソース構造検査（`test_autotune_apply_single_source` /
`test_event_subscription_symmetry` / `test_zone_cache_invalidation` など）は資産を使わないので
dev_safe から `scripts/source_checks/` へ移した。資産を要らない検査はこちらに置くこと。

`test_algorithm_constants`（2026-10-24〜）は、出力に効くファイル（`ColorZone*`・`PixelProcessor*`・`ZoneAutoTuner*`・
`HighlightSampleCorrector`・`UvChartMap`・`Code/MaskSuggest/Ops`）の名前付き数値定数を、定数の台帳
`docs/algorithm-constants.md`（種類・段・根拠）と突き合わせる。定数を足した・値を変えた・消したとき、名前の無い小数の
リテラルの数がファイルごとの記録と違うときに落ちる。手元の被写体で選んだ値（実測）と由来の記録が無い値（不明）を
区別しておき、減らす候補と、新しい被写体で外れたときに疑う先にするため。

- マーカー: `perf`（性能計測・品質検査なし）/ `slow`（メモリ・時間コスト大）/ `mesh_assisted`（メッシュの UV チャートから
  作った除外マスクを足した選択。メッシュが見つかったときの任意機能で、`test_uv_chart_map` の一部）。
  マーカー登録とルート rootdir はリポジトリルートの `pytest.ini` が持つ。
- **`pytest.ini` の `addopts` が既定を決める**（コマンドラインで後から書けば上書きできる）:
  `-m "not perf"`（性能計測テストは既定で走らない。測るときは `-m perf`）と
  `-n 4 --dist loadfile`（4 並列。`loadfile` はモジュール単位の割り当てで、これを外すと
  module スコープ fixture が各ワーカーで再実行されて逆に遅くなる）。直列に戻すなら `-n 0`。

### ネイティブメモリのはみ出しを捕まえる（検証用の環境変数）

0.3.0 から作業配列などは Mono のヒープの外（`IrocaNativeMemory`）に置いてポインタで扱うので、寸法の計算違いは
例外ではなくメモリ破壊として出る（後で別の所で落ちたり、エディタごと落ちたりする）。`Code/Core` の寸法まわりを
変えたら、次の 2 つを立てて golden・abcheck・回帰を回す。どちらも出力は変えない（変わったら書く前に読んでいる所がある）。

- `IROCA_NATIVE_GUARD=1`（Windows だけ）: `IrocaNativeMemory.Alloc` が要求長ちょうどで末尾をページ境界にそろえ、
  直後を読み書きできないガードページにする。作業配列のプール（`NativeWorkPool`）の切り上げと使い回しもやめる。
  塊の後ろへのはみ出しと解放後の読み書きが、その場でアクセス違反（ハーネスは異常終了）になる。前へのはみ出しは
  捕まえない。確保のたびに OS から取るので遅く、中身は 0 から始まる。
- `IROCA_POOL_POISON=1`: プールが貸す領域を 0xFF で埋める（float は NaN、bool は真）。ガードの領域は 0 から
  始まるので、書く前に読む所を探すにはこちらを併用する。返された塊は 0xDD で埋め、8 個のあいだ貸さずに寝かせる
  （2026-10-28〜）。返したあとに読む所は出力の違いで、返したものをもう一度返す所は「このプールが貸した塊では
  ありません」の例外で見つかる（寝かせないと、同じ番地が別の借り手へ貸された後の 2 回目の返却を見分けられない）。

ハーネスの出力はキャッシュから返ることがあるので、回帰では `IROCA_HARNESS_CACHE=0`・`IROCA_RESULT_CACHE=0`・
`IROCA_ROW_CACHE=0` も立てて実際に走らせる（golden はキャッシュを使わない）。Unity の中で試すときは、環境変数を立ててから Unity を起動する。

```powershell
$env:IROCA_NATIVE_GUARD = "1"; $env:IROCA_POOL_POISON = "1"
$env:IROCA_HARNESS_CACHE = "0"; $env:IROCA_RESULT_CACHE = "0"; $env:IROCA_ROW_CACHE = "0"
.\.venv\Scripts\python.exe -m pytest scripts/golden -q
```

### 実行時間の作り（2026-08-19 に整理）

同じ計算を何度もしないための主な仕掛けは次のとおり。いずれも **測る対象は実 C# のまま**で、
出力・指標の値は一切変えない（変わったら退行として検出されるべきものなので、
バッチ実行は単発実行との byte 一致を `dev_safe/scripts/verify_harness_batch.py` で検証している）。

| 仕掛け | 何をしているか | 効かせない方法 |
|--------|----------------|----------------|
| 実行キャッシュ | `(DLL の中身, 入力, 除外マスク, zones JSON, 外部マスクの内容, 追加引数)` の SHA-256 をキーに出力 raw を `dev_safe/_harness_cache/` へ保存。同じ入力の再実行を丸ごと省く。zones JSON 内の外部マスク（`includeMask` / `excludeMask` / `evidenceMask`）は**内容**で同定し、パス（ワーカー ID を含む）はキーに入れない | `IROCA_HARNESS_CACHE=0` |
| 指標の結果キャッシュ | 品質指標 `recolor_quality.compute_all_metrics`（4096² で 1 件約 5 秒）の結果を、`(入力, 出力, 評価マスク, 目標色)` の内容と `recolor_quality.py` / `colorspace.py`・numpy / scipy の版の SHA-256 をキーに `_harness_cache/metrics/` へ保存（2026-10-16）。出力が変われば計算し直す。値は型ごと戻すので計算直後と bit 一致（品質ゲート全 21 件で確認） | `IROCA_METRICS_CACHE=0` |
| PNG のデコードキャッシュ | `fixtures._load_rgba`（テクスチャ・GT・評価領域の読み込み口）のデコード結果を、ファイルの中身と Pillow の版の SHA-256 をキーに `_harness_cache/decoded/` へ `.npy` で保存（2026-10-17）。4096² で 1 枚 0.1〜0.4 秒 → 0.02 秒。1 枚 64MB なので被写体の画像 1 組で数 GB 使う | `IROCA_DECODED_CACHE=0` |
| SAM 提案の結果キャッシュ | `sam_proposal.proposal_for_click_full`（提案マスク + 診断）と `measure_sam_e2e.one_case`（e2e の 1 ケース）の結果を、DLL・ONNX・埋め込み / 凍結 logits・テクスチャ・GT・クリック・粒度と関与する Python ソースの SHA-256 をキーに `_harness_cache/results/` へ保存（2026-10-17）。どちらも 1 回に dotnet を 6〜10 回起動していた。共通部品は `fixtures.result_key / result_load / result_store`。ズーム再推論のクロップの埋め込みは、実 C# が作ったエンコーダ入力の中身・エンコーダ・onnxruntime の版をキーに `_harness_cache/cropemb/` へ置く（2026-10-20。以前は矩形だけをキーに作業ディレクトリへ置き、samops の入力画像 raw も「あれば使う」だったので、テクスチャ・C# の前処理・エンコーダを変えても古い物を使いえた。入力画像 raw はいま内容ハッシュ名） | `IROCA_RESULT_CACHE=0` |
| バッチ実行 | 同じテクスチャの複数ケース（通常 5 色）を Harness `--batch` で 1 プロセスにまとめ、dotnet 起動と 64MB の入力書き出しを 1 回にする | （常時。単発呼び出しは従来どおり） |
| ワーカー分離 | `dev_safe/_iou_csharp_work/<worker>/` に分けて並列実行の書き込み衝突を防ぐ。ハーネスのビルドはプロセス跨ぎのロックで直列化 | — |

**キャッシュキーには DLL のダイジェストが入っている**ので、`Code/` を変更して再ビルドした
時点で全エントリが自動的に無効化される（古い出力で緑になることはない）。容量は既定 24GB 上限で
古い順に捨てる（`IROCA_HARNESS_CACHE_GB` で変更、`IROCA_HARNESS_CACHE_DIR` で移動）。
**上限は「スイート 1 周分が全部入る」大きさでないと意味が無い。** 2026-09-14 の計測では 1 周の
作業集合が旧上限 8GB を超えており（4096² 出力 64MB × 300 件弱）、毎回古い順に捨てて次の周で
作り直すスラッシングになっていた（全体 8 分 30 秒のうち相当分が再実行）。さらに外部マスクの
パスにワーカー ID が入るせいで、同じ計算が別ワーカーに割り当たるだけでミスし、同一出力の複製が
上限を食い潰していた。いま捨てが起きると pytest の warning 要約に
「ハーネス実行キャッシュが上限 …GB を超え」と出るので、出たら `IROCA_HARNESS_CACHE_GB` を上げる。
プロセス内メモは `IROCA_HARNESS_MEM_ENTRIES`（既定 12 件）で件数を絞る（無制限だとワーカー 1 つが
3GB 近く抱える。ディスクからの読み戻しは 0.04 秒なので少数で足りる）。

**使われなくなったキャッシュと作業ファイルの掃除**（2026-10-20、`dev_safe/Tests/regression/cache_gc.py`）:
キーがハッシュなので、DLL やソースを変えた後の古いエントリは二度と当たらない。容量上限があったのは
実行キャッシュ（`*.raw`）だけで、ほかのキャッシュ（`decoded/` `results/` `rows/` `metrics/`
`gt_tolerance/`）と作業ディレクトリ（`_sam_freeze_work/` `_sam_e2e_work/` `_spike/evidence/`
`_iou_csharp_work/` など）は書きっぱなしだった。この時点で本体の dev_safe は 120GB を超え、
そのうち 77GB は 3 日以上使われていなかった。いまは pytest の開始時（xdist なら親プロセスで 1 回、
6 時間に 1 回まで）に、**キャッシュは 7 日、作業ファイルは 3 日、名前に `.tmp` を含む書きかけは
1 日**使われなかったものを消す。「使った」は mtime で見る。キャッシュは当たったときに
`fixtures.touch` で mtime を進め、作業ファイルは使うたびに書き直されるか touch される
（atime はウイルス対策の読み取りでも進むので使えない）。消したものがまた要れば計算し直すだけで、
結果は変わらない。走査はふだん 0.2 秒程度。消すと pytest の要約に 1 行出て、
`_harness_cache/cache_gc.log` に記録が残る。手で回すなら
`python dev_safe/Tests/regression/cache_gc.py [--dry-run]`。止めるなら `IROCA_CACHE_GC=0`、
日数は `IROCA_CACHE_GC_DAYS` / `IROCA_WORK_GC_DAYS`。対象の作業ディレクトリは gitignore 済みで
追跡ファイルを含まないことを `test_cache_gc.py` が git で確かめている。
2026-10-22 から、掃除する範囲を**本体リポジトリの各 worktree に置いた dev_safe の複製**
（`git worktree list` で探す）と abcheck の `%TEMP%/iroca-abcheck`（止められた実行が残した `<pid>/` と
入力キャッシュ）に広げた。以前は自分の dev_safe しか見なかったので、テストを回さなくなった複製は
一度も掃除されず、複製 5 つで 229GB 溜まっていた。さらに、ドライブの空きが
`IROCA_CACHE_GC_MIN_FREE_GB`（既定 50GB。冷えた状態から回帰を 1 回回すと複製 1 つのキャッシュが
20〜26GB まで育つので、その 2 回分）を切っていたら、6 時間の間隔を待たずに走り、日数に届かないものも
最後に使ったのが古い順に空きが戻るまで消す。6 時間以内に使ったもの（走っている別のテストの分）は消さない。
それでも足りなければ pytest の要約に、キャッシュ以外（取り込み済みの worktree・scratchpad・
Unity の確認用ホスト）を見直すよう 1 行出す。これらは掃除の対象にしない。
`tools/visual_review.py compare --engine csharp` の固定 104 ケースも同じ `run_harness_many`
（キャッシュ + `--batch`）を通るので、直前に回帰スイートを回していれば dotnet を起動せずに
同じ DLL の出力が返る（以前は 1 件ずつ単発起動で約 15 分かかっていた）。

**DLL のバイト列は「ソース + コンパイラ」だけで決まる**（2026-09-14、`Harness.csproj` の
`EnableSourceLink=false`）。それまでは SourceLink が `obj/Harness.sourcelink.json` に git の HEAD
コミット SHA を埋め込み、それが CoreCompile の入力だったので、**ドキュメントだけのコミットでも次の
ビルドで DLL が書き換わり、実行キャッシュが丸ごと無効化されていた**（コミットごとにコールドラン）。
空コミットを挟んで再ビルドしても SHA-256 が変わらないことを確認済み。

**`measure_evidence_autotune.run_case` の結果キャッシュ**（2026-09-14、`_harness_cache/rows/`、
`IROCA_ROW_CACHE=0` で無効化）: 追加操作後 14 件・証拠つき自動調整 14+7 件・プリセット比較・
ワークフロー審査が同じ被写体 × クリックを別ワーカーで繰り返し計算しており、ハーネス出力が
キャッシュ済みでも 1 件約 6 秒（SAM 提案の dotnet 5 回起動、採点の HSV/収縮、読み込み）かかっていた。
row と証拠セグメントを、**関与する Python ソース全部**（measure_evidence_autotune / sam_proposal /
measure_sam_e2e / freeze_sam_fixtures / measure_residual / measure_click_position / gt_tolerance /
colorspace / fixtures / headless_io / workflow_contract）と DLL・ONNX・凍結埋め込み・テクスチャ・GT・
評価領域・fixtures.json・GT 許容上書き（JSON + マスク PNG）の内容ダイジェスト、click / include /
conventional をキーにして保存する。しきい値や規則を書き換えれば自動で無効化される（DLL ダイジェストと
同じ考え）。`return_outputs=True` の出力配列は保存せず、ヒット時も同じ zones で
`fx.run_harness_autotune` を呼び直す（ハーネスの実行キャッシュが返す）。ヒット 0.3〜0.5 秒。
`measure_residual.island_mask` は erode 済 GT 内部の画素だけを集めて HSV を計算する（全画面版と
bit 一致、1.2 秒 → 0.3 秒）。

ハーネスのスレッド数は `IROCA_HARNESS_THREADS` で上書きできる。未設定のとき、xdist 並列中は
fixtures が **論理コア数の半分**（16 コア機で 8）を子プロセスへ渡し、単独実行（`-n 0`）では製品と
同じ `ProcessorCount-2` のままにする（2026-09-14）。製品既定のまま 4 ワーカーが同時に走ると最大
56 スレッドが 16 コアを取り合っていた。コールドラン（キャッシュ空・全回帰）の A/B では所要時間が
7 分 39 秒 → 7 分 35 秒で同等、平均 CPU 使用率が 70% → 62% に下がり、321 件の出力は全て byte
一致した（並列化しているのは整数ヒストグラム・min/max など順序非依存の集約だけなので、
スレッド数は出力に影響しない。単体でも 2/4/8/14 スレッドで一致を確認）。

**並列数は起動時の空きメモリで自動的に減る**（`dev_safe/Tests/conftest.py`）。
`(空き − IROCA_MIN_FREE_GB) ÷ 1 ワーカーの見積もり` に収まる数まで `-n` を下げる（見積もりは
`IROCA_WORKER_GB`、既定 3GB。固定は `IROCA_TEST_WORKERS`）。減らしたときは結果の最後に
`[iroca] … ワーカーを 4 → N に減らしました` と出る（2026-10-16 までは `-q` だと何も出ず、
普段使いの空き 10〜13GB で 1 並列に落ちていても気づけなかった）。同日の実測（ウォーム・全回帰）は
1 並列 11 分 47 秒、4 並列 3 分 25 秒。4 並列の同時合計の最大は 10.9GB（品質指標を計算する周）。

ここでいう「空き」と、ハーネス起動前の空き待ち（`IROCA_MIN_FREE_GB`、既定 4GB。待ちの上限は
`IROCA_MEM_WAIT_S`）は、どちらも `dev_safe/Tests/regression/memory_guard.py` が出す
「空き物理メモリ」と「コミット余裕」の小さい方。**コミット余裕には自動管理のページファイルの伸びしろを足す**
（2026-10-20〜。上限は RAM の 3 倍程度、ディスクの空きから `IROCA_PAGEFILE_DISK_RESERVE_GB`（既定 10GB）を残した分まで）。
Windows がページファイルを広げるのは誰かが今のコミット上限を超えて確保したときだけなので、
「今の上限 − 使用量」だけを見て手前で待つと自分では拡張を起こせない。実際に、物理が 6GB 空いているのに
コミット余裕 1GB でワーカーが 30 分ずつ止まっていた。拡張の最中に確保が断られた分は、OOM の再試行で吸収する。
memory_guard.py は出力に関与しないので、実行キャッシュ・行キャッシュ・審査の内容ハッシュのどれにも入れない
（以前は `headless_io.py` の中にあり、待ちの調整だけでキャッシュと承認が無効になっていた）。

**テストモジュールの大域変数に大きな配列を溜めない。** xdist のワーカーはファイルをまたいで
生き続けるので、モジュール変数の dict に出力画像やテクスチャを入れると、そのファイルが終わっても
解放されない（2026-10-16 に 2 か所・計 3.3GB を確認して修正。1 ワーカーが 6.6GB まで膨らんでいた）。
ファイルの中で使い回すなら module スコープの fixture にするか、直近の 1 件だけ持つ。
- **`scripts/golden` は `dev_safe/Tests/regression/` に含まれない**（別ツリー）。合成入力で
  自己完結しており実行は数十秒なので、`Code/Core` を触ったときは回帰テストと併せて回すこと。
  golden が落ちる＝C# の出力が変わった、の意味。意図した変更なら
  `python scripts/golden/golden_lib.py --force` で再生成する（`--force` 無しだと、
  どのケースがどう変わるかを列挙して止まる）。
- **色の表あり・なしのパリティ**（2026-10-24〜）: `PixelProcessor` は元画素の色の種類の表があると、色だけで決まる
  計算（照合・帯成長の候補判定・無彩の領域 L・再着色アンカー・再着色）を色ごとに 1 回だけ行い、表が作れない画像
  （色の種類が画素数の 1/4 を超える写真調など）では画素ごとに計算する。同じ式の 2 通りの実装なので、片方だけ
  直すと色の多いテクスチャでだけ出力が変わる。`scripts/golden/test_palette_parity.py` が golden の全ケースと
  帯成長の合成 2 ケースを、通常の実行とハーネスの `IROCA_NO_PALETTE=1`（表を作らない）で回してビット単位で比べる。
  色ごとの経路を足したら、そこを踏むケースがあるか（片側だけ変えて落ちるか）を確かめること。
- **改善サイクルの採否判断では `IROCA_CSHARP_GATE_FULL=1`（旧名 `VACC_CSHARP_GATE_FULL` も効く）と
  `VACC_AUTOTUNE_GATE_FULL=1` を必ず立てる**（品質ゲートの既定は bandana のみで、残り被写体の実 C# 品質を測らずに
  「全パス」と誤認するため）。
- **自動調整（スポイト→自動調整＝ユーザーの主経路）の選択ゲートは既定 ON**（2026-08-21〜）:
  `test_autotune_accuracy` は 4 被写体の選択床を常時、アトラス被写体の自動調整テスト（6 被写体）と
  `test_synth_gen2` の 3 クリック位置も既定で回る。ハーネス実行は fixtures の
  `run_harness_autotune`（実行キャッシュ + AUTOTUNE パラメータの aux キャッシュ）に一本化して
  あり、DLL 不変なら 2 回目以降の追加コストはほぼゼロ。初回も避けたい場合のみ
  アトラス側と gen2（`VACC_GEN2_FULL=0`）の環境変数で絞れる（名前は dev_safe のテスト冒頭）。
- **no-mask のアトラス系の低い precision（パンツ 0.21 等）は劣化検知の番兵であって
  品質目標ではない**。この条件は「実操作で起きない」わけではない — 通常クリックは
  シードを設定しない（シードは Shift+クリックのみ）ため、アトラスへの「スポイト→自動調整」
  の**初回プレビューはこの状態を見せる**。ただしユーザーは次の 1 手（シード/マスク/AI 提案）
  で回復するのが実際の流れで、正のゲートは:
  - `test_atlas_journey`（初回過検出→シード 1 手の遷移を Harness `--session` で通し検証。
    apply 写像の byte 一致・seedUV 変更のキャッシュ無効化・1 手後 precision ≥0.98 を契約。
    既定はパンツのみ、`VACC_JOURNEY_FULL=1` で boots/goggles も。`VACC_JOURNEY=0` で opt-out）
  - `test_autotune_masked`（partsim 部位マップ: pants/boots IoU 0.997〜0.998）
  - `test_autotune_seeded`（Shift+クリック相当: 全被写体 precision ≥0.98 のハード契約）
  いずれも既定 ON（`VACC_AUTOTUNE_MASKED=0` / `VACC_AUTOTUNE_SEEDED=0` で opt-out）。
  過検出の議論はこれらの数字で行う（2026-08-21 の 3 条件比較計測）。
  初回体験そのものの品質目標はアトラス自動調整テストの xfail target
  （precision の目標値）が持ち、改善が到達すると XPASS で浮かぶ
  → ベースラインをラチェットし目標を進める。
- **プロキシ段（操作中の表示）とフル確定段の表示一致ゲートは既定 ON**（2026-08-23〜）:
  ユーザーが操作中に見るのは長辺 ProxyMaxSize(512) の縮小処理表示で、確定表示とは設計上
  一致しない。`test_proxy_parity` が Harness `--stage proxy` / `--stage full-display`
  （PreviewPipeline の段階的リファイン両段を同じ丸め `PixelProcessor.ComputeFitSize` で
  再現し、表示解像度で出力）の乖離（選択 IoU・色差）を degradation-only で凍結する。
  opt-out は `VACC_PROXY_PARITY=0`、再凍結は `python dev_safe/measure_proxy_parity.py
  --baseline`（乖離を意図して受け入れる変更のときのみ。理由をコミットに残す）。
- **縁の混色の契約**（2026-10-03〜）: 選択の境目の画素（素材と隣の混色）は、混色帯モード（境界クリーンアップ
  ON・エッジぼかし 0・有彩サンプルのゾーン。`PixelProcessor.AnalyzeMixtureBand`）が p' = p + α·(F'−F) で塗り、
  このモードでは穴埋め・境界回復（緩和マッチ）を行わない。`test_edge_mixture` が正解つきの合成シーン
  （`dev_safe/Tests/regression/edge_scenes.py`: 層をスーパーサンプル合成して縮小し、正解 = 入力 + α·(M'−F)。
  M' は素材だけの参照帯をパイプライン自身が塗った色 = 再着色の設計には依らず、縁の扱いだけを測る）を、ワンショット
  （証拠つき自動調整）の設定・tolerance を上げた設定・0.81 で実 C# に通し、縁の平均誤差の上限・素材の外を変えない
  （輪が出ない）・内側の取りこぼし無しを契約にする。別の DLL と並べるときは
  `python dev_safe/scripts/edge_bench.py --dll <dll> --ref-dll <基準 dll> --hi-tol --tol 0.81`。
- **連続適用セッションの状態持ち越し契約**（2026-08-23〜）: `test_session_state` が
  Harness `--session`（SelectionCache をステップ間で持ち越し、ゾーンは name で同定）で
  マスク塗り→設定変更→ゾーン追加の往復を再現し、各ステップ出力が新規単発実行と
  byte 一致することを検証する（誤ヒット＝「前回の選択が残る」の機械検出）。
- **AI 提案の E2E 品質ゲート**（2026-08-23〜）: `test_sam_e2e` が凍結ロジット + ONNX 推論
  (onnxruntime) + 実 C# 純計算層で「クリック→提案マスク」の IoU / precision を
  degradation-only で凍結する（`measure_sam_e2e.one_case` = 製品と同順・ズームイン再推論込み）。
  既定は granularity auto × クリック p50 の 20 被写体、採否判断時は `VACC_SAM_E2E_FULL=1`
  で p25/p95 も回す。ONNX モデル / 凍結ロジット不在は skip。再凍結は
  `python dev_safe/scripts/measure_sam_e2e.py --grans auto`。
- **証拠つき自動調整（セグメント教師）の契約ゲート**（2026-08-25〜）:
  `ZoneAutoTuner.AnalyzeWithEvidence` は AI 提案のセグメントを「その場の正解データ」にして、
  従来導出の当て推量だった 3 点（地色の正規化母集団・未被覆トーンの補完・ハイライト補助の
  発火）だけを補う経路。ハーネスは zones JSON の `evidenceMask`（raw、ハーネス専用）で駆動する。
  `test_autotune_evidence` が `measure_evidence_autotune.run_case`（従来 `--autotune` と
  同一クリックの SAM 提案を証拠にした `--autotune` の A/B）を driver に、採用時の勝ち筋
  （gold の残存島 0 / tops 明部クリック / skirt precision / eye 反射 / 汚染セグメントでも
  ドメインから導く / 白の色付き陰の彩度天井 / tolerance の上限 / 別パーツの彩度上限・色相幅）と
  「従来を下回らない」を契約にする。成分単位のゲート（別パーツの彩度上限・色相幅）そのものは
  合成テクスチャの `test_part_sat_ceiling` / `test_part_hue_band` が固定設定で検査する。
  「スポイト位置を含む連結成分はゲートで落とさない」は `test_envelope_gate_anchor`（zones JSON の
  `sampleUV`。ワンショットの driver はクリック位置を必ず `sampleUV` として渡す）。既定は 9 ケース、
  `VACC_AUTOTUNE_EVIDENCE_FULL=1` で 19 被写体 × 3 位置の非退行。凍結埋め込み不在は skip。
  全 A/B 表は `python dev_safe/scripts/measure_evidence_autotune.py --clicks p25 p50 p95`。
  - **証拠は製品と同じ全経路で作る**（2026-09-02〜）: `sam_proposal.proposal_for_click_full`
    が製品 `RequestEvidence → RequestProposal(Auto)` と同順（クリック成分が小さいときの
    ズームイン再推論込み）で提案を作る。それまでの `proposal_for_click` は第 1 段のみで、
    ズームが発火するクリックでは製品と別の証拠で導出を検査していた。floodWarning の提案は
    製品どおり証拠にせず従来導出（B = A）へ落とす写像も再現している。
- **ワンショット（スポイト→自動調整）の現在地**（2026-09-02〜。**製品の主経路**）:
  2026-08-30 の AI 必須化以降、ユーザーの初回体験はほぼ常に証拠つき導出だが、従来 Analyze を
  測る既定ゲート（autotune_accuracy / アトラス被写体 / 布の被写体 / synth_gen2 / click_position /
  seeded / masked / atlas_journey とその xfail 目標）は今やフォールバック経路の番兵であり、
  主経路の絶対品質はスイートに数字として無かった。
  - `test_autotune_evidence::test_oneshot_floor`: `measure_evidence_autotune.py --baseline` が
    凍結した `evidence_autotune_baseline.json`（19 被写体 × p25/p50/p95、B のみ）を床に
    degradation-only。既定は p50 の 19 件、FULL で 57 件。
  - `test_oneshot_satisfaction_target`（xfail strict=False）: 「満足」
    （prec≥0.98 ∧ rec≥0.95 ∧ 残存島≤0.5%）に未達のケースが到達すると XPASS で浮かぶ →
    `--baseline` でラチェット（satisfied=true になり目標から外れる）。
  - `test_oneshot_vs_preset`: **仕上がりそのものの理想への近さ**。理想 =
    `dev_safe/preset_samples/` のプリセット（作者が手で追い込んだパラメータ）を実 C# に通した
    出力、ワンショット = GT 内 p50 クリック → 証拠つき自動調整 → 同じ変更先色。変化領域の
    OkLab 色差（p95 / 平均）と変化画素集合の IoU を `measure_oneshot_vs_preset.py --baseline`
    の凍結値を床に守り、併せて自動調整出力に構造品質ゲート（compute_all_metrics）を課す。
    目標（dE p95 ≤ 0.05 ∧ IoU ≥ 0.95）は xfail。PSD 由来の再着色 GT（gt_*.png）はベタ塗りに
    近く（元との明度相関 0.2）理想には使わない。4 被写体（bandana / costume / hair / sneakers）。
  - 導出結果 → ゾーンの適用写像は `ZoneAutoTuner.TuneResult.ApplyTo` が単一の正
    （UI とハーネスの二重実装を解消）。製品の側でそれを呼ぶのは自動調整の流れ `AutoTuneService`（ウィンドウと
    自動化の 2 本を 0.4.0 の 4-3 で 1 本にした）だけ。`scripts/source_checks/test_autotune_apply_single_source` が
    C# ソースから「流れとハーネスが ApplyTo だけを使う（ウィンドウと自動化は直接代入しない）」「TuneResult の全フィールドを
    写す」を検査する。
  - 同じ考えで、再着色の全体設定（edgeFeather〜decontaminationRadius）は `RecolorSettings` が単一の正
    （2026-09-24〜）。`PixelProcessor.ProcessPixelsArray` の入口はこれを受け取る 1 つだけで、プレビュー 2 段・
    詳細・単体/一括エクスポート・Automation・ハーネスの全呼び出し元が同じ型を渡す（以前は各所で 8 個の値を
    位置引数に並べ直していた）。セッション・プリセット・zones JSON とのフィールド対応と既定値の参照は
    `scripts/source_checks/test_recolor_settings.py` が検査する。同じテストが、全体設定のそれぞれが選択キャッシュの
    鍵（`BuildSelectionKey`）に入っているか「選択に効かない」と分類されているかも見る（ゾーン側は
    `scripts/golden/test_selection_key_audit.py`）。
  - **画で見る**（2026-09-03〜）: 上の 3 本は数字しか残さない。`dev_safe/scripts/build_oneshot_review.py`
    が同じ driver（`run_case` / `run_subject`）の出力を「クリック位置+証拠セグメント / 出力 /
    GT 判定オーバーレイ（+注目範囲クロップ）」の画と `review_data.json` に書き出し、
    `build_oneshot_review_html.py --junit <junit.xml>` が被写体 × 3 クリック位置のケース（2026-09-05 の生成時点で 42 件）+
    プリセット比較 4 件 + テスト結果表を 1 枚の HTML（`dev_safe/Tests/oneshot_review/oneshot_review.html`）に組む。
    未達ケースの「はみ出しか取りこぼしか、証拠はどこだったか」はここで目視する。
- **プレビュー座標変換（スポイト/シード/ペイント/AI クリックの v 反転）**（2026-08-23〜）:
  変換は `Code/Core/PreviewCoords.cs` が単一の正（UI でインライン再実装しない）。
  `test_preview_coords` が Harness `--previewcoords` で実 C# を駆動し、独立リファレンスと
  v 反転の向き（上端クリック→画素行 h-1）を固定する。UI 実行テストを持たない方針の中で、
  「クリックと違う画素を拾う」だけは機械検出できるようにするための切り出し。
- **現場の失敗→回帰ケースの取り込み口**（2026-08-23〜）: Unity メニュー
  `Tools/いろか/再現データを書き出す...`（`Code/Debug/ReproDump.cs`。Debug 衛星 asmdef =
  配布物には含まれない開発者専用機能。書き出しにテクスチャ原本が入るため）が現在のセッション
  （テクスチャ・有効ゾーン・マスク・設定・extraSamples）を 1 フォルダに書き出す。
  それを `dev_safe/Tests/repro_cases/<名前>/` に置くと `test_repro_cases` が実 C# で
  再現実行する（expected 未凍結の間は「調査中」skip、凍結後は byte 一致の恒久回帰）。
  ワークフローの正は `dev_safe/Tests/repro_cases/README.md`。
  - **ワンショットの再現**（2026-09-02〜）: zones.json は自動調整「後」の値なので、それだけでは
    導出そのものを再現できなかった。ReproDump が自動調整の入力（正規化前のサンプル色・変更先色・
    証拠マスク `zone{i}_evidence.png`・除外マスク使用の有無・手で触っていないか）も meta.json に
    書き、`test_repro_case_autotune` が `--autotune` + `evidenceMask` で導出し直して
    「再導出値 = 書き出し値」（製品 UI ⇔ ハーネスのワンショット同一性）と `expected_autotune.png`
    の byte 一致を検査する。見本は `make_sample_repro_case.py --oneshot`。
- **UV チャート地図（メッシュのパーツ操作）**（2026-09-30〜）: `Code/Core/UvChartMap.cs` は
  テクスチャを使うメッシュの UV からチャート（UV の辺を共有する三角形のまとまり）を求め、
  島の外のにじみ代を L1 最近傍のチャートへ割り当てる。再着色の経路からは呼ばれず、右クリックの
  「パーツ」操作が除外・含めるマスクを作るのにだけ使う。`test_uv_chart_map` が Harness `--uvcharts`
  （UV ダンプ + テクスチャのキー + 寸法。入力画像は不要）で、チャートの分け方・行 0 = 下端の向き・
  にじみ代の割り当てが総当たりの L1 最近傍と一致すること・アトラス被写体の実メッシュで GT がほぼ全部持ち主を
  得ること、クリックしたメッシュの外を除外すると pants / boots が分かれることを検査する。
  UV ダンプは `dev_safe/texture_sample/uv_dumps/`（作り方は `dev_safe/scripts/unity_uv_dump/`）。
  メッシュを探す側（`Code/Infra/MeshUvLocator.cs`）と、プレビューに重ねる島の輪郭は
  Unity 依存なので EditMode テスト（下記）で見る。
- **Scene でクリックした場所の表示**（2026-10-04〜）: 光線と三角形の当たり判定・UV の補間・
  Tiling の畳み込み・カリング（`Code/Infra/MeshRaycast.cs`）は Unity 不要なので `scripts/unit-run` が見る。
  シーンの Renderer を集めて当てる側（`Code/Infra/SceneMeshPicker.cs`。スキンメッシュはポーズ込みの形、
  NDMF のプレビュー用の複製は除く）とプレビューに重ねる島の組み立ては EditMode の `SceneMeshPickerTests`。
  再着色の経路には入らないので、ハーネス・視覚レビューの対象外。
- **保存形式を経由した再着色（非破壊ビルドの入口）**（2026-10-01〜）: 非破壊ビルド（NDMF）は
  編集画面を通さず、保存済みの編集状態（`IrocaSessionState`。マスクは RLE の `MaskState`）から
  `Code/Core/SessionRecolor.Apply` で再着色する。マスクは `MaskStateCodec.ToSnapshot` で作り直す
  （編集画面の `MaskSnapshot.FromBuffers` と同じ組み立て規則）。`test_session_recolor_parity` が
  Harness `--viastate`（入力を保存形式へ詰め直して `SessionRecolor.Apply` に流す）と通常実行の
  byte 一致を、合成 4 種・低解像度マスク・実テクスチャで検査する。全 false マスクの省略や RLE の往復で
  出力が変わると「エディタで見た色とアップロードした色が違う」になるため。
- Harness は未知の引数を拒否する（exit 2）。過去 `--matchDistance` がパーサ無しのまま
  渡され続けた「死にノブ」事故の再発防止。フラグを増やすときは Harness.Main の検証
  switch にも足すこと。

## Unity 実機の EditMode テスト（2026-09-24〜）

ハーネスも golden も Unity を起動しないので、AssetDatabase を使う保存・復元や PNG の読み書き、
Mono 上の出力は検証できない。`scripts/editor-tests/`（`Iroca.EditorTests` アセンブリ）がそこを受け持つ。

| テスト | 検査すること |
|---|---|
| `PersistenceTests` | セッション・マスク・プリセットの往復、読めないファイルを空保存で消さない（.bak 退避）、GUID 未解決ファイルの .orphan 退避・復元・期限切れ削除、プリセット半径の丸め |
| `ExportPipelineTests` | 原本を import 設定（maxTextureSize）に左右されず読む、TGA は取り込み済みで代替・Read/Write 無しは不可、PNG 往復、import 設定の引き継ぎ、上書きで GUID を保つ |
| `WorkingResolutionTests` | 編集の作業解像度（`WorkingResolution`）: 辺が 8192 以下なら元ファイルのまま、超えるときだけ取り込み後の寸法へ縮める（取り込みで大きくなるときは引き伸ばさない）。拡大表示に映す寸法は取り込み後の寸法。書き出しは常に元の解像度。テクスチャの生データから帯ごとに縮める経路が Color32[] の経路と同じ画素になる（RGBA32・ARGB32・BGRA32・RGB24） |
| `SampleUvUndoTests` | スポイト位置が Undo/Redo・ゾーン削除の Undo で色と対で戻る、プリセットには載らずセッションには残る |
| `WindowRestoreTests` | ウィンドウが状態を失わない・古い状態に戻らない: プレビュー・オーバーレイの一時テクスチャがシーンを開いても破棄されない、Unity が落ちた後の起動ではレイアウトに残った古い編集内容でなく自動保存を読む（ドメインリロードではメモリ上の内容を使う） |
| `UndoRestoreSignalTests` | 「関係ない Undo では作り直さない」判定の前提: ウィンドウ自身の Undo/Redo では `undoRedoPerformed` より前に `OnAfterDeserialize` が来て、別のオブジェクトの Undo/Redo では来ない |
| `PreviewDownsampleTests` | 拡大表示のクロップをフル解像度の配列から直接縮小する `BoxDownsampleRegion` が、切り出してから `BoxDownsample` したのとビット単位で同じ。範囲外の指定は拒否する |
| `UvChartMapPaddingTests` | メッシュ右クリックの UV チャート地図で、半径の外を落とした持ち主だけを持つ `AssignPaddingBaked` が、`AssignPadding` してから同じ半径で引いたのとどの画素・マスクも同じ（ランダムな三角形） |
| `MeshUvLocatorTests` | テクスチャを使うメッシュの探索（シーン → 同じ素材フォルダの Prefab、書き出し名 `_recolored` の追跡、手動指定、Read/Write 無効のメッシュで落ちない） |
| `MeshPartCacheTests` | メッシュの探索結果を捨てて探し直す条件（`MeshPartController.IsStale`）: 使っていた Renderer が消えた・自動の結果でシーンが替わった。手動の指定は残っている限りそのまま |
| `MeshIslandOutlineTests` | プレビューに重ねる UV の島の輪郭（内側の対角線を描かない、UV を重ねた左右対称のパーツでも輪郭が残る、Tiling / Offset） |
| `SceneMeshPickerTests` | Scene でクリックした場所: 手前の Renderer・三角形・UV、マテリアルのカリングどおりの面、非表示・無効・保存されない複製に当たらない、スキンメッシュはボーンを動かした直後でも描かれている形に当たる、プレビューに重ねる島はクリックした島だけ・Tiling を繰り返しで畳む |
| `RuntimeParityTests` | 上記「既知の限界」の Mono とハーネスの一致 |
| `NonDestructiveTests` | 非破壊の色替え: レシピ（JSON）を往復しても編集状態の直接適用と画素が一致、取り込み設定（最大サイズ・圧縮形式・ミップ・sRGB）にそろう、キャッシュの再利用、マテリアル差し替えの規則（範囲内で元テクスチャを使うものだけ・複製の共有（同じレシピを別々のオブジェクトに登録しても 1 つ）・範囲外と元アセットは無傷・入れ子は深い方が勝つ・失敗してもコンポーネントを消す・1 つのコンポーネントの複数レシピ・同じテクスチャは先が勝つ・アニメーションで切り替わるマテリアルも範囲内なら同じ複製で差し替え、アニメーションでだけ使うテクスチャは「使っていない」と報告しない・2 段（Transforming と最適化段）に分けたとき間に入った元のマテリアルも同じ複製とテクスチャ 1 枚で差し替え、設定の問題は 1 回だけ・「使っていない」は最後の段で判断）。原本のファイルを開けないときは例外で抜けず「元テクスチャを読めない」と報告する。レシピは作った時点でファイルに書かれている（プロジェクト全体の保存に頼らない） |
| `RecipeRegistrationTests` | 登録でコンポーネントが増えない: 同じオブジェクトへの登録は 1 つの `IrocaRecolor` にレシピを足す、同じテクスチャは置き換え（確認画面用の `Describe` も）、旧版の重複コンポーネントは付いていた順・同じテクスチャは先のものだけ残して 1 つにまとめ 1 回の Undo で戻る、旧保存形式（単一の `recipe`）は一覧の先頭へ移る |
| `NdmfProcessAvatarTests` | NDMF を通した結合（`AvatarProcessor.ProcessAvatar` でプラグインが走り、元のマテリアル・テクスチャのファイルを変えずに差し替わる。Animator のトグルで切り替わるマテリアルも範囲内だけ差し替わり、元のアニメーションは変わらない）。NDMF があるホストでだけコンパイルされる。アニメーターの拡張（AnimatorServicesContext）は要るときだけ開き、自分で開いたものだけを閉じる（前のパスから開いているものは閉じない） |
| `NdmfSupportTests` | NDMF が使えないときの扱い。asmdef の版の範囲（`[1.8.0,2.0.0-0)`。2.0 のベータ版も外す）を Unity がそのとおりに読み、入っている NDMF・VRChat SDK の版から付くはずの define が各 asmdef に付いている。NDMF が入っていない・古い・新しすぎるときは、元テクスチャのあるレシピを持つ「Iroca Recolor」があれば VRChat へのアップロードを止め、3 通りで違う直し方を案内する（空のレシピだけ・再生・NDMF が使えるときは止めない）。VRChat SDK があれば「Iroca Recolor」は NDMF と関係なく `IEditorOnly` で、アップロードの確認（`NdmfUploadGuard`）は NDMF などの前処理より先に走り、NDMF が使えるときは素通しする |
| `LivePreviewTests` | シーンでのプレビューのうち NDMF 非依存の部分: どこに何を映すかの規則（登録前はそのテクスチャを使う全 Renderer にライブ、登録後は範囲内だけ・結び付いたレシピならライブ・それ以外はレシピ、入れ子は深い方）、受け渡し口（編集対象の変化を 1 回だけ知らせる、取り込み済みの寸法の RenderTexture を共有して貸す、編集していないテクスチャの結果は書かない、縮小版も同じ RenderTexture に書く）。画素の読み戻しは `-nographics` では効かないので、GPU があるときだけ見る |
| `RecipePreviewTexturesTests` | 登録済みレシピの出来上がり（ビルドと同じテクスチャ）を同じ中身なら共有し（作りかけも共有）、編集で別物になり、最後の借り手が返したら捨てる。初回は作りかけで返し（色替えの計算でエディタを止めない）、キャッシュがあればその場で貸す。作っている間に返されたら出来上がりは捨てる。壊れたレシピは貸さない。原本のファイルを開けなくても作りかけを終わらせ（貸すものは無し）、開けるようになったら作り直す |
| `NdmfPreviewFilterTests` | `IrocaPreviewFilter` を NDMF の API どおりに呼ぶ: 対象の選び方、プロキシだけの差し替え（元の Renderer・マテリアルは無傷）、毎フレームの差し替え直し（NDMF は描画のたびに描画用プロキシを元のマテリアルへ戻す）、後片付け、編集対象の変化で無効化、初めて映すレシピは出来上がるまでノードを完了させずプロキシにも触らない（ライブは待たせない）。NDMF があるホストでだけコンパイルされる。待っている間に借りたマテリアルが消えてもノードを完了させる（NDMF は全部のノードがそろうまで表示を切り替えないので、終わらないとシーンのプレビュー全体が止まる） |
| `MaskSuggestInferenceTests` | AI マスク提案の実推論（パッケージの読み込み・モデルの変換とロード・推論）が Unity 2022.3（Sentis）と Unity 6（Inference Engine）で通る。灰地の赤い円の中心をクリックし、円と提案マスクの IoU を測る（推論のワーカーを手放した後も作り直して同じように取れる）。モデルの変換で Assets に残った一時フォルダは、いろかのものだけを片付ける。パッケージ未導入・モデル未配置なら Ignore |
| `PerfBenchTests` | Unity の Mono での再着色の速さ（`Perf` カテゴリ。品質は見ない）。`Run-EditorTests.ps1 -PerfCases <json>` のときだけ回り、ふだんは Ignore |
| `MemoryBenchTests` | プレビュー相当の処理を繰り返したときの Mono ヒープとプロセスのメモリと、最後にプール・キャッシュを手放して OS へ返るか（`Memory` カテゴリ）。`-MemCases <json>` のときだけ回る（1 回の起動に 1 ケース） |
| `HiResSmokeTests` | 8K・16K のテクスチャを製品の経路（取り込み〜色替え〜PNG の書き出しと読み戻し・NDMF の差し替えテクスチャなど）に 1 回ずつ通し、各段の時間とメモリを記録する（`HiRes` カテゴリ。品質は見ない）。`-HiResCases <json>` のときだけ回る（GPU の段は `-Graphics`。16K は 20GB 前後使う） |

```powershell
.\scripts\Link-HostPackage.ps1 -HostProject <ホスト> -WithEditorTests   # 1 回だけ
.\scripts\Run-EditorTests.ps1                                          # ホストを Editor で開いていないこと
```

テストは配布物（`Code/`）の外にあり、`Link-HostPackage.ps1 -WithEditorTests` がホストの
`Assets/IrocaEditorTests` へジャンクションで繋ぐ（ホスト側は .gitignore 済み）。CI では走らない
（Unity のライセンスが要る）。所要は取り込み済みのホストで 1 分弱。`Run-EditorTests.ps1` は Unity を
BelowNormal 優先度で起動する（ゲーム中でも邪魔しないように）。
UI の操作そのもの（OnGUI・ダイアログ）はここでも検証しない。

## skip と fail の区別（fixtures.require_harness）

- dotnet が無い・Unity DLL が無い＝**環境不備 → skip**。
- `Code/Core` のコンパイルエラー＝**製品退行 → fail**。
- skip が出た実行は「全パス」ではない。`-ra` で skip 理由を確認すること。

## 視覚レビュー（出荷ゲート）

```
python tools/visual_review.py snapshot            # 変更前の出力を保存
python tools/visual_review.py compare --engine csharp   # 3 列比較パネル生成
（変化したケースのパネル PNG を Read で 1 枚ずつ目視確認）
python tools/visual_review.py approve --note "確認した範囲と根拠"   # 承認マーカー
```

- 承認は `dev_safe/Tests/visual_review/approved.json` に記録される。`--note` には
  **実際に見た範囲と、それで十分と判断した根拠**を書く（必須。無いか空だと ValueError で止まり、承認されない）。
- `compare` は変更前（snapshot）との差を数え、`dev_safe/Tests/visual_review/change_summary.json` に書く。
  固定設定のケースごとに `changed`（変更前と RGB が違う画素数）・`toward_original`（元テクスチャとの差が縮んだ画素＝
  誤爆の除去）・`away_from_original`（差が広がった画素＝新しい・強まった着色）・`max_delta`（変更前との最大差）。
  snapshot の無いケースは載らない。同じ表を変化の大きい順にコンソールにも出し、`changed` が 0 でないケースだけ
  パネルに等倍クロップの 2 段目が付く。`changed` が 0 のケースは変更前とビット一致。ワークフロー側は
  `dev_safe/Tests/workflow_review/review.json` の各ケースの `changed_from_snapshot`（変更前と違う画素数。
  snapshot が無ければ null）が同じ役をする。目視するのは変化のあるケースと変更前の無いケースだけで
  （ビット一致のケースは読まなくてよい）、compare はそのパネルだけを一覧に出す。
- pre-commit フック（`scripts/hooks/pre-commit`）が承認の鮮度と品質ゲート較正
  （`tools/check_visual_review.py`）を検査する。鮮度は二方式で見る:
  - 固定設定: `approved.json` の承認時刻が、ステージしたゲート対象ファイルの **mtime** より新しいこと
    （`approve` 自体も、比較パネルがゲート対象ファイルの mtime より新しいことを要求する）。内容が同じでも
    mtime が進めば無効になるので、`git stash` の往復やチェックアウトは compare → approve の前に済ませる。
  - ワークフロー: `review.json` に compare 時点の **内容の SHA-256**（ゲート対象 C#・`Harness.cs` / `Harness.csproj`・
    `tools/visual_review.py` / `workflow_review.py` / `workflow_contract.py`・dev_safe の測定スクリプト・2 つのベースライン・
    `subjects.json`・GT 許容の上書き）とパネル・入力資産のハッシュを残し、今の内容と一致すること、
    `workflow_review/approved.json` がその `review.json` を指していること。時刻を見ないので stash では切れないが、
    ツールや測定器を直すと切れる。`python tools/workflow_review.py check` でこちらだけを単独で確かめられる
    （有効なら何も出さず終了コード 0、無効なら理由つきの例外で止まり終了コード 1）。
- **ゲートの対象は「headless ハーネスがコンパイルする製品ソース」**（`Harness.csproj` と、それが Import する
  `HarnessSources.props` から導出＝`tools/harness_scope.py`。0.4.0 の 3-13 から `Code/Core` 全部（サブフォルダを含む）と
  `Code/MaskSuggest/Ops`）。
  UI / Infra / Automation は対象外で、`Code/` 全体を対象にしていた頃のように
  「文言 1 行の修正で 30 分の compare か SKIP 変数か」の二択にはならない。
  - 理由: ハーネスがコンパイルしないファイルの変更は、compare を何回回しても検出できない。
    要求しても検証にならず、バイパスを日常動作として学習させるだけだった（レビュー §6-14）。
  - 対象外のファイルは ci.yml の `build-check`（dotnet 型チェック）が受け持つ。
  - Core にファイルを足せば（どのサブフォルダでも）ゲート対象も自動で増える（手書きミラーを置かない）。
- **フックは clone ごとに 1 回の有効化が必要**（忘れるとゲートは一切動かない）:
  ```
  git config core.hooksPath scripts/hooks
  ```
  確認: `git config core.hooksPath` の出力が `scripts/hooks` であること。
- `SKIP_VISUAL_REVIEW=1` は「出力に影響しない変更（IO・プレビュー・コメントのみ）」に限る。
- **出力を変えないはずのリファクタ**（Core の死んだコードの削除・重複の整理・定数化など）は、
  「影響しない」を思い込みで済ませず `dev_safe/scripts/refactor_abcheck/abcheck.py` で機械的に示す。
  変更前のコミット（`--base-rev`、既定 HEAD）と作業ツリーのハーネスに同じ入力を流し、既定でない設定・
  マスク・自動調整・samops・反射 probe まで含む全モード（約 1,400 単位・約 8 分）の出力をバイト比較する。
  不一致 0 を確かめたうえで SKIP し、コミット本文にその結果を書く（2026-10-07 の保守性リファクタ
  85 コミットはこの手順で通した）。使い方と限界は同フォルダの README。

## 環境の全損からの復旧手順（2026-08-03 実績）

1. Python をインストール（winget: `Python.Python.3.14`）し、`.venv` を作り直す:
   ```powershell
   Remove-Item -Recurse -Force .venv
   & "$env:LOCALAPPDATA\Programs\Python\Python314\python.exe" -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r dev_safe\Tests\requirements.txt
   ```
2. .NET SDK 8 をインストール（winget: `Microsoft.DotNet.SDK.8`）。
3. Unity 2022.3.22f1 を Unity Hub で入れて `UnityManaged` / `UNITY_EDITOR_PATH` を設定
   （上記「Unity 導入済み環境」）。Unity を入れられない場合のみ 2 DLL シムで代替。
4. `git config core.hooksPath scripts/hooks` でフックを有効化。
5. `dotnet build scripts/headless-run/Harness.csproj -c Release` → pytest 一式で緑を確認。
6. dev_safe が無いマシンでは `git clone https://github.com/yukkuri-aoba/Iroca_dev_safe dev_safe`。
7. ホスト Unity プロジェクトに本体をリンクする（上記「ホスト Unity プロジェクト」）。
   ここまでやって初めて Editor UI の実機確認まで再開できる。
8. Sentis 統合を触るなら ML ホスト（`Iroca_MLDev`）も用意する（上記「ML ホスト」）。

## 既知の限界・注意

- build-check は `#if UNITY_EDITOR` 内を検査できない（Define 未解決のため常に除外）。
- ハーネスは net8.0 + JIT で、製品の Unity 2022.3（Mono）と実行環境が異なる。
  golden ハッシュは toolchain 固定であり環境間比較には使えない。
  その差は `scripts/editor-tests/RuntimeParityTests` が測る: golden の入力（`scripts/golden/cases/`）を
  Unity 実機の製品経路（`IrocaAutomation.RecolorWithZones`）に通してハーネスの出力と比べる。
  2026-09-24 時点で 25 件すべてバイト一致。`Code/Core` を大きく変えたら `Run-EditorTests.ps1` で再確認する。
- baseline 完全一致方式のテスト（bandana/costume/sneakers の IoU 系）は「現状出力の凍結」で
  あり、正当な改善でも fail する。改善採用時はベースライン再生成の理由をコミットに残すこと。
- `Tests/visual_review/` の approve は compare パネルの存在と鮮度しか機械検証できない。
  「Read で 1 枚ずつ目視」は運用規律として守る。
