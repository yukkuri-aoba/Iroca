# build-check

Unity を起動せずに Editor asmdef (`Code/**/*.cs`) のコンパイルチェックだけを
走らせるための最小 csproj。Unity の `Library/` 生成や VPM パッケージ作成には
**一切関与しない**。CI / エージェント / ローカルでの軽量な型チェック専用。

## 分け方

Unity の asmdef と同じ境目で csproj を分けてある（`IrocaEditor.Runtime`・`IrocaEditor.Core`・`IrocaEditor.Services`・`IrocaEditor`）。
参照は asmdef と同じく推移させないので、下の層が上の層の型を使うと Unity と同じようにここで落ちる。共通の設定は `IrocaAssembly.props`。

## 前提

- .NET SDK 6.0+ (`dotnet --version`)
- Unity Editor 2022.3.22f1（VRChat 標準）がローカルにインストールされていること
  - 既定パス: `C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Data\Managed`

## 使い方

```powershell
# プロジェクトルートから
dotnet build scripts/build-check/IrocaEditor.csproj
```

- 成功すると `scripts/build-check/bin/` に DLL が出力される（gitignore 済）
- エラーがあれば `CS####` で表示される

### Unity を別パスにインストールしている場合

```powershell
# バージョンだけ変える
dotnet build scripts/build-check/IrocaEditor.csproj -p:UnityVersion=2022.3.22f1

# フルパスを上書き（Hub 経由でないインストール等）
dotnet build scripts/build-check/IrocaEditor.csproj -p:UnityEditorPath="D:\Unity\2022.3.22f1\Editor\Data\Managed"

# 環境変数で恒久指定
$env:UNITY_EDITOR_PATH = "D:\Unity\2022.3.22f1\Editor\Data\Managed"
dotnet build scripts/build-check/IrocaEditor.csproj
```

既定パスの解決（`UnityVersion` の既定・`UNITY_EDITOR_PATH`・Hub の既定パス）は `UnityEditorPath.props` の 1 箇所にあり、build-check の csproj（`unity-test-framework/` の 2 本を含む）がすべて Import する（`scripts/headless-run/Harness.csproj` と `scripts/unit-run/UnitRun.csproj` は別管理）。

## EditMode テストのコンパイル（IrocaEditorTests.csproj）

`scripts/editor-tests`（asmdef は `Iroca.EditorTests`）を、Unity を起動せずにコンパイルだけ確かめる。テストの実行は
Unity が要る（`scripts/Run-EditorTests.ps1`）。CI（`ci.yml`）とクラウドのセッションでも回る。

```powershell
dotnet build scripts/build-check/IrocaEditorTests.csproj
dotnet build scripts/build-check/IrocaEditorTests.csproj -p:EditorTestsHostProject="D:\path\to\Project"
dotnet build scripts/build-check/IrocaEditorTests.csproj -p:UnityPackagesPath="<Library/PackageCache と同じ形のフォルダ>"
```

- 参照はテストの asmdef と同じ: 本体（`IrocaEditor.csproj`）・`UnityEngine.TestRunner`・`UnityEditor.TestRunner`・
  Unity の NUnit。アセンブリの名前も `Iroca.EditorTests` にして、本体の `InternalsVisibleTo` が効くようにしてある。
- `UnityEngine.TestRunner` と `UnityEditor.TestRunner` は、Unity と同じく Unity Test Framework のパッケージのソースから
  作る（`unity-test-framework/` の 2 本）。代わりの型は作らない（Unity に無い API を使ったテストがここだけ通るのを防ぐ）。
- パッケージ（`com.unity.test-framework`・`com.unity.ext.nunit`）の置き場は `UnityTestFramework.props` で決まる:
  `-p:UnityPackagesPath=` → 環境変数 `IROCA_UNITY_PACKAGES` → テストを回すホストの `Library/PackageCache`
  （`-p:EditorTestsHostProject=` か `IROCA_TESTS_HOST`。既定はほかの check と同じ `..\Avatar_Projects\Iroca_Dev`）。
- ホストを持たない環境（クラウド・CI）は `bash scripts/build-check/fetch-unity-test-framework.sh <置き場>` で、Unity の
  パッケージの配布元から Unity 2022.3.22f1 の既定の版（test-framework 1.1.33・ext.nunit 1.0.6）を sha256 を確かめて取る。
  Unity Companion License の物なのでリポジトリには入れない。
- NDMF を使うテスト（`#if IROCA_NDMF_PRESENT`）は、NDMF が無いので外れる。
- 手元の交互 A/B（`Run-EditorTests.ps1 -AllowForeignCode` で Code だけを基準の木へ向ける）は、テストのアセンブリ全体が
  基準の Code でコンパイルできないと回らない。`bash scripts/build-check/ab-compile.sh <基準のコミット> [テストのコミット]` で、
  Unity を開く前に確かめられる（作業ツリーと git の状態は変えない）。2026-10-27 の時点で、7d2bf6f（0.3.0 の途中）の Code には
  今のテストは MemoryBench 単体でも当たらない（`WorkingResolution.Size` の引数が違う）。古い基準と比べるときは、基準の木の
  テストを使う。

## NDMF 連携の型チェック（IrocaNdmfCheck.csproj）

`Code/NdmfIntegration/`（NDMF が入ったときだけコンパイルされる別 asmdef）は、本体の csproj では
除外している。NDMF を入れた Unity プロジェクト（既定は `..\Avatar_Projects\Iroca_Dev`）の
`Library/ScriptAssemblies` から NDMF の DLL を借りて型チェックする:

```powershell
dotnet build scripts/build-check/IrocaNdmfCheck.csproj
dotnet build scripts/build-check/IrocaNdmfCheck.csproj -p:NdmfHostProject="D:\path\to\Project"
```

ホストを一度 Unity で開いて ScriptAssemblies ができていることが前提（CI では走らせない）。
ホストに VRChat SDK があれば、Unity と同じく `IROCA_VRCSDK_BASE`・`IROCA_VRCSDK_AVATARS` も付けてコンパイルする。

## VRChat SDK はあるが NDMF が無いとき（IrocaVrcsdkCheck.csproj）

VRChat SDK があれば、`IrocaRecolor` は NDMF の有無に関係なく `IEditorOnly` になり、NDMF が使えないときは
`NdmfUploadGuard`（本体）がアップロードを止める。NDMF の入った検証ホストではこの分岐を通らないので、
SDK が入った Unity プロジェクト（既定は `..\Avatar_Projects\Iroca_Dev`）の `Packages` から VRChat SDK の DLL だけを借り、
NDMF を参照せずに型チェックする:

```powershell
dotnet build scripts/build-check/IrocaVrcsdkCheck.csproj
dotnet build scripts/build-check/IrocaVrcsdkCheck.csproj -p:VrcsdkHostProject="D:\path\to\Project"
# NDMF が新しすぎるとき（古いときは IROCA_NDMF_TOO_NEW を外す）
dotnet build scripts/build-check/IrocaVrcsdkCheck.csproj "-p:DefineConstants=IROCA_VRCSDK_BASE%3BIROCA_VRCSDK_AVATARS%3BIROCA_NDMF_INSTALLED%3BIROCA_NDMF_TOO_NEW"
```

## MCP 連携の型チェック（IrocaMcpCheck.csproj）

`Code/McpIntegration/`（MCP for Unity が入ったときだけコンパイルされる別 asmdef）は、本体の csproj では
除外している。MCP for Unity を入れた Unity プロジェクト（既定は `..\Avatar_Projects\Iroca_Dev`）の
`Library/ScriptAssemblies` から MCPForUnity の DLL を、`Library/PackageCache` から Newtonsoft.Json を借りて型チェックする。
本体は ProjectReference なので、Unity と同じく本体の internal は見えない:

```powershell
dotnet build scripts/build-check/IrocaMcpCheck.csproj
dotnet build scripts/build-check/IrocaMcpCheck.csproj -p:McpHostProject="D:\path\to\Project"
```

既定のホストの場所はリポジトリの直下から数えるので、worktree から回すときは `-p:McpHostProject=` か
環境変数 `IROCA_MCP_HOST` で渡す（NDMF・VRChat SDK の check も同じ）。

## 既知の限界

- **Unity の Define シンボル**（`UNITY_EDITOR`, `UNITY_2022_3_OR_NEWER` 等）は
  解決されない。`#if UNITY_EDITOR` で囲まれた箇所は **常に除外** されるので、
  そこにエラーがあっても検知できない。
- **VRChat SDK / VPM パッケージへの参照** は、`IrocaEditor.csproj` では見ない（SDK も NDMF も無いプロジェクトの形）。
  VRChat SDK に触れるのは `#if IROCA_VRCSDK_*` の中だけで、そちらは `IrocaVrcsdkCheck.csproj` と
  `IrocaNdmfCheck.csproj` が SDK の DLL を借りて見る。
- 出力 DLL は Unity が生成するものとは別物。**実機テスト・ビルドには絶対に
  使わない**こと。Unity Editor を実際に開いた時の出力が正となる。

## 想定運用

- ローカル: コミット前の素振り (`dotnet build scripts/build-check/`)
- CI: GitHub Actions の job として「Unity Editor を起動せずに通る最低限の型チェック」
  を回す（Unity Hub を CI に入れるより安価）
- 変更直後: Code/ を書き換えたらすぐ呼んで構文エラーを即検知
