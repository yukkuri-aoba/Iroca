# Unity MCP 自動化（VACCAutomation）

AI エージェントから [MCP for Unity](https://github.com/CoplayDev/unity-mcp)（UPM パッケージ `MCPForUnity`）経由で
VACC をヘッドレス駆動するための仕組みと使い方。`docs/idea2.md` の「AIツールによる操作をサポートしたい」への対応。

> **これは開発時ツール**。MCP は VRChat アバター制作者（エンドユーザー）に導入を強制しない。
> 配布パッケージ（`com.yukkuri-aoba.vrc-avatar-color-changer`）は MCPForUnity に依存しない（依存ゼロのまま）。
> 将来エンドユーザーに提供する場合も、各自が MCPForUnity を導入すれば同じ自動化 API がそのまま使える。

## 前提：このリポジトリはパッケージであってプロジェクトではない

本リポジトリは VPM パッケージ本体（`Code/` がソース）であり、`Assets/` も `ProjectSettings/` も持たない。
MCPForUnity は「起動中の Unity Editor」に接続するため、**ホスト Unity プロジェクト**に本パッケージと
MCPForUnity を同居させて初めて MCP から操作できる。

## ホストプロジェクトの構築（開発用・リポジトリには含めない）

1. Unity 2022.3 系の空プロジェクトを用意する（リポジトリ外、または個人作業フォルダ。配布物に含めない）。
2. 本パッケージを導入する。`<host>/Packages/manifest.json` にローカル参照を追加するのが手軽:
   ```json
   { "dependencies": {
       "com.yukkuri-aoba.vrc-avatar-color-changer": "file:../../AvatarColorChanger"
   } }
   ```
   （git URL でも可）。
3. MCPForUnity を導入する。Package Manager → 「+」→ Add package from git URL：
   ```
   https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main
   ```
   要件: Python 3.10+ / Git。
4. MCP クライアントを設定する。`Window → MCP for Unity → Configure All Detected Clients`
   （Claude Code / Claude Desktop / VS Code 等）。

## 呼び出し経路（3 つ・すべて同一の中核を通る）

中核は `Code/Automation/VACCAutomation.cs`。`ExportView` の実出力経路（ディスクの PNG を直接読み、
`PixelProcessor.ProcessPixelsArray` に通す）を同期で再現するので、製品の出力と一致する。

### 1. 静的 API（生 C# 実行が可能なクライアント / EditMode テスト）

```csharp
VRCAvatarColorChanger.VACCAutomation.RecolorByPreset(
    "Assets/Textures/body.png", "MyPreset", "Assets/Textures/body_recolored.png");

VRCAvatarColorChanger.VACCAutomation.RecolorWithZones(
    "Assets/Textures/body.png",
    "{\"zones\":[{\"sample\":[1,1,1],\"target\":[0.1,0.3,0.8],\"tolerance\":0.25}],\"settings\":{}}",
    "Assets/Textures/body_recolored.png");

VRCAvatarColorChanger.VACCAutomation.DescribeSchema();  // 呼び出し方を自己発見
VRCAvatarColorChanger.VACCAutomation.ListPresets();
VRCAvatarColorChanger.VACCAutomation.GetVersion();
```

戻り値は JSON 文字列（`RecolorResult`）。例外は投げず `{"ok":false,"error":"..."}` で返す。

### 2. メニュー + ジョブファイル（「メニュー実行」と「ファイル読み書き」だけで完結）

生 C# 実行に非対応のクライアントでも駆動できる、保証された経路。

1. `<host>/UserSettings/VACC/mcp/job.json` を書く（git 非追跡フォルダ）：
   ```json
   { "mode": "preset",
     "source": "Assets/Textures/body.png",
     "output": "Assets/Textures/body_recolored.png",
     "preset": "MyPreset" }
   ```
   または zones モード：
   ```json
   { "mode": "zones",
     "source": "Assets/Textures/body.png",
     "output": "Assets/Textures/body_recolored.png",
     "zones": { "zones": [ { "sample": [1,1,1], "target": [0.1,0.3,0.8], "tolerance": 0.25 } ], "settings": {} } }
   ```
2. メニュー `Tools/VRC AvatarColorChanger/Automation/Run Job File` を実行する。
3. 結果を `<host>/UserSettings/VACC/mcp/result.json` から読む。

補助メニュー: `Describe Schema`（→ `schema.json`）、`List Presets`（→ `presets.json`）、
`Open MCP Folder`（フォルダを開く）。

### 3. batchmode CLI（MCP を介さない完全ヘッドレス・CI 用）

```
Unity.exe -batchmode -quit -projectPath <host> \
  -executeMethod VRCAvatarColorChanger.VACCAutomation.RunFromCommandLine \
  -vaccSource Assets/Textures/body.png \
  -vaccOutput Assets/Textures/body_recolored.png \
  -vaccPreset MyPreset
```
`-vaccZonesFile <zones.json>` / `-vaccJob <job.json>` でも指定可。結果は Console と `result.json` に出る。

## 入力スキーマ

- パスは Assets 相対（`Assets/...`）・プロジェクト相対・絶対のいずれも可。出力は `.png`。
- 色は `[r,g,b]`（0..1）。`enabled` なゾーンが 1 つも無いと error。
- ゾーン/設定フィールドの詳細・既定値は `DescribeSchema()`（または `schema.json`）が返す。
  フラットなゾーン設定は `scripts/headless-run/Harness.cs` の `--zones` スキーマと同一。

## 制限事項 / 今後

- **v1 ではプリセット同梱マスクをヘッドレス適用しない**（パーツ単位の粗いマスクは後続対応）。
  マスクを含むプリセットを渡すと、結果 JSON の `warnings` に明示したうえでマスク無しで処理する。
  マスクを反映したい場合は当面 UI（VACCWindow）から実行する。
- カスタム MCP ツール登録（MCPForUnity の Tool Group に `vacc_recolor` 等を第一級ツールとして出す）は
  本 API を素材に後付け可能だが、今回は対象外（汎用 MCP の「C# 実行」「メニュー実行」で駆動する設計）。
