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

## 標準の実行コマンド

```powershell
# 回帰テスト一式（改善サイクルの基本形）
.\.venv\Scripts\python.exe -m pytest dev_safe/Tests/regression/ -q

# 大規模ケース（8K/16K、数 GB のディスク書き込みあり）を除外する場合
.\.venv\Scripts\python.exe -m pytest dev_safe/Tests/regression/ -q -m "not slow"

# 実 C# 品質ゲートを bandana 以外の被写体（hair/costume/sneakers）まで広げる
$env:VACC_CSHARP_GATE_FULL = "1"
```

- マーカー: `perf`（性能計測・品質検査なし）/ `slow`（メモリ・時間コスト大）。
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
3. Unity DLL を用意（上記「Unity 未インストール環境」参照）。
4. `git config core.hooksPath scripts/hooks` でフックを有効化。
5. `dotnet build scripts/headless-run/Harness.csproj -c Release` → pytest 一式で緑を確認。
6. dev_safe が無いマシンでは `git clone https://github.com/yukkuri-aoba/Iroca_dev_safe dev_safe`。

## 既知の限界・注意

- build-check は `#if UNITY_EDITOR` 内を検査できない（Define 未解決のため常に除外）。
- ハーネスは net8.0 + JIT で、製品の Unity 2022.3（Mono）と実行環境が異なる。
  golden ハッシュは toolchain 固定であり環境間比較には使えない。
- baseline 完全一致方式のテスト（bandana/costume/sneakers の IoU 系）は「現状出力の凍結」で
  あり、正当な改善でも fail する。改善採用時はベースライン再生成の理由をコミットに残すこと。
- `Tests/visual_review/` の approve は compare パネルの存在と鮮度しか機械検証できない。
  「Read で 1 枚ずつ目視」は運用規律として守る。
