# テスト構成（testing-architecture）

CLAUDE.md / `.claude/instructions/improvement-cycle.md` が「テスト構成の正」として参照する文書。
2026-08-03 に環境ごと再構築した際、旧版（ローカルのみ・git 非追跡）が失われていたため書き直した。
**再発防止のため本ファイルは git 追跡する**（テスト構成の説明であり秘匿情報は含まない）。

## 大原則

- **製品 C#（`Code/Core/PixelProcessor.cs` ほか）が唯一の正。** テスト・計測・視覚レビューは
  必ず実 C# の出力を測る。アルゴリズムの Python 再実装は作らない（過去、Python プロキシが
  製品から IoU 0.26 乖離し、改善サイクルがプロキシを測っていた事故がある）。
- **GT（正解データ）は PSD のレイヤー情報が正。** `dev_safe/scripts/` の `build_*_gt.py` /
  `analyze_*_psd.py` で PSD から生成する。

## 構成要素

```
Iroca 本体リポジトリ（公開）
├─ Code/                        製品 C#（唯一の正）
├─ scripts/headless-run/        実 C# を Unity なしで実行するハーネス
│   ├─ Harness.csproj           Code/Core の対象ファイル + MaskSuggest/Ops を同一アセンブリに
│   │                           取り込み（internal アクセス可）、実 UnityEngine.CoreModule.dll を
│   │                           参照して net8.0 でビルド
│   └─ Harness.cs               raw 形式 I/O（[int32 w][int32 h][payload]）+ 各種検証モード
├─ scripts/build-check/         dotnet 単体での型チェック用 csproj 3 本
│   │                           （IrocaEditor / IrocaEditor.Debug / IrocaSentisCheck）
│   │                           ※ UnityEngine.dll + UnityEditor.dll が必要（下記「前提」参照）
├─ scripts/golden/              golden（出力ハッシュ固定）テスト
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
| ホスト Unity プロジェクト | Editor UI の実機確認・`#if UNITY_EDITOR` 内の検証に必要 | `scripts/Link-HostPackage.ps1` でリンクする（下記「ホスト Unity プロジェクト」） |
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

# 実 C# 品質ゲートを bandana 以外の被写体（hair/costume/sneakers）まで広げる
$env:VACC_CSHARP_GATE_FULL = "1"

# golden（C# 出力ハッシュの固定）— dev_safe を必要としない自己完結テスト
.\.venv\Scripts\python.exe -m pytest scripts/golden -q
```

- マーカー: `perf`（性能計測・品質検査なし）/ `slow`（メモリ・時間コスト大）。
  マーカー登録とルート rootdir はリポジトリルートの `pytest.ini` が持つ。
- **`scripts/golden` は `dev_safe/Tests/regression/` に含まれない**（別ツリー）。合成入力で
  自己完結しており実行は数十秒なので、`Code/Core` を触ったときは回帰テストと併せて回すこと。
  golden が落ちる＝C# の出力が変わった、の意味。意図した変更なら
  `python scripts/golden/golden_lib.py --force` で再生成する（`--force` 無しだと、
  どのケースがどう変わるかを列挙して止まる）。
- **改善サイクルの採否判断では `VACC_CSHARP_GATE_FULL=1` を必ず立てる**（既定は bandana のみで、
  残り 3 被写体の実 C# 品質を測らずに「全パス」と誤認するため）。

## skip と fail の区別（fixtures.require_harness）

- dotnet が無い・Unity DLL が無い＝**環境不備 → skip**。
- `Code/Core` のコンパイルエラー＝**製品退行 → fail**。
- skip が出た実行は「全パス」ではない。`-ra` で skip 理由を確認すること。

## 視覚レビュー（出荷ゲート）

```
python tools/visual_review.py snapshot            # 変更前の出力を保存
python tools/visual_review.py compare --engine csharp   # 3 列比較パネル生成
（パネル PNG を Read で 1 枚ずつ目視確認）
python tools/visual_review.py approve             # 承認マーカー書き込み
```

- 承認は `dev_safe/Tests/visual_review/approved.json` に記録される。
- pre-commit フック（`scripts/hooks/pre-commit`）が `Code/` 変更コミット時に
  承認の鮮度と品質ゲート較正（`tools/check_visual_review.py`）を検査する。
- **フックは clone ごとに 1 回の有効化が必要**（忘れるとゲートは一切動かない）:
  ```
  git config core.hooksPath scripts/hooks
  ```
  確認: `git config core.hooksPath` の出力が `scripts/hooks` であること。
- `SKIP_VISUAL_REVIEW=1` は「出力に影響しない変更（IO・プレビュー・コメントのみ）」に限る。

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
- baseline 完全一致方式のテスト（bandana/costume/sneakers の IoU 系）は「現状出力の凍結」で
  あり、正当な改善でも fail する。改善採用時はベースライン再生成の理由をコミットに残すこと。
- `Tests/visual_review/` の approve は compare パネルの存在と鮮度しか機械検証できない。
  「Read で 1 枚ずつ目視」は運用規律として守る。
