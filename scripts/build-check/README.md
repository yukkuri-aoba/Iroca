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

## 既知の限界

- **Unity の Define シンボル**（`UNITY_EDITOR`, `UNITY_2022_3_OR_NEWER` 等）は
  解決されない。`#if UNITY_EDITOR` で囲まれた箇所は **常に除外** されるので、
  そこにエラーがあっても検知できない。
- **VRChat SDK / VPM パッケージへの参照** はチェック対象外。本プロジェクトは
  Editor 専用で UnityEngine/UnityEditor のみに依存するため現状問題ないが、
  将来 VRCSDK に依存させる場合は別途 `<Reference>` を追加すること。
- 出力 DLL は Unity が生成するものとは別物。**実機テスト・ビルドには絶対に
  使わない**こと。Unity Editor を実際に開いた時の出力が正となる。

## 想定運用

- ローカル: コミット前の素振り (`dotnet build scripts/build-check/`)
- CI: GitHub Actions の job として「Unity Editor を起動せずに通る最低限の型チェック」
  を回す（Unity Hub を CI に入れるより安価）
- 変更直後: Code/ を書き換えたらすぐ呼んで構文エラーを即検知
