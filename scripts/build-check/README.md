# build-check

Unity を起動せずに Editor asmdef (`Code/**/*.cs`) のコンパイルチェックだけを
走らせるための最小 csproj。Unity の `Library/` 生成や VPM パッケージ作成には
**一切関与しない**。CI / エージェント / ローカルでの軽量な型チェック専用。

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

既定パスの解決（`UnityVersion` の既定・`UNITY_EDITOR_PATH`・Hub の既定パス）は `UnityEditorPath.props` の 1 箇所にあり、build-check の 6 本の csproj が Import する（`scripts/headless-run/Harness.csproj` と `scripts/unit-run/UnitRun.csproj` は別管理）。

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
