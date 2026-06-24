# Unity MCP 自動化（CamereoAutomation）

AI エージェントから [MCP for Unity](https://github.com/CoplayDev/unity-mcp)（UPM パッケージ `MCPForUnity`）経由で
Camereo をヘッドレス駆動するための仕組みと使い方。`docs/idea2.md` の「AIツールによる操作をサポートしたい」への対応。

> **これは開発時ツール**。MCP は VRChat アバター制作者（エンドユーザー）に導入を強制しない。
> 配布パッケージ（`com.yukkuri-aoba.camereo`）は MCPForUnity に依存しない（依存ゼロのまま）。
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
       "com.yukkuri-aoba.camereo": "file:../../AvatarColorChanger"
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
5. MCPForUnity が同居すると `Code/McpIntegration/` の asmdef が自動でコンパイルされ（`com.coplaydev.unity-mcp`
   検出時のみ・`CAMEREO_MCP_PRESENT` ゲート）、Camereo のカスタムツールが登録される。
   `mcpforunity://custom-tools` リソースに `vacc_recolor` / `vacc_describe_schema` / `vacc_list_presets` が現れれば成功。

## 呼び出し経路（3 つ・すべて同一の中核を通る）

中核は `Code/Automation/CamereoAutomation.cs`。`ExportView` の実出力経路（ディスクの PNG を直接読み、
`PixelProcessor.ProcessPixelsArray` に通す）を同期で再現するので、製品の出力と一致する。

### 1. 静的 API（生 C# 実行が可能なクライアント / EditMode テスト）

```csharp
Camereo.CamereoAutomation.RecolorByPreset(
    "Assets/Textures/body.png", "MyPreset", "Assets/Textures/body_recolored.png");

Camereo.CamereoAutomation.RecolorWithZones(
    "Assets/Textures/body.png",
    "{\"zones\":[{\"sample\":[1,1,1],\"target\":[0.1,0.3,0.8],\"tolerance\":0.25}],\"settings\":{}}",
    "Assets/Textures/body_recolored.png");

Camereo.CamereoAutomation.DescribeSchema();  // 呼び出し方を自己発見
Camereo.CamereoAutomation.ListPresets();
Camereo.CamereoAutomation.GetVersion();
```

戻り値は JSON 文字列（`RecolorResult`）。例外は投げず `{"ok":false,"error":"..."}` で返す。

### 2. MCPForUnity カスタムツール（`execute_custom_tool`・メニュー非依存）

生 C# 実行に非対応のクライアントでも駆動できる、第一級の経路。`Code/McpIntegration/` の
カスタムツール（MCPForUnity 導入時のみコンパイル）が静的 API を MCP に公開する。
Tools メニューには何も追加しない（旧「Automation」サブメニューは撤去済み）。

- `execute_custom_tool("vacc_recolor", { "source": ..., "output": ..., "preset": "MyPreset" })`
  — プリセット経路。`preset` の代わりに `zones`（フラットなゾーン設定 JSON 文字列）でその場指定も可（両者は排他）：
  ```json
  { "source": "Assets/Textures/body.png",
    "output": "Assets/Textures/body_recolored.png",
    "zones": "{\"zones\":[{\"sample\":[1,1,1],\"target\":[0.1,0.3,0.8],\"tolerance\":0.25}],\"settings\":{}}" }
  ```
- `execute_custom_tool("vacc_describe_schema")` — 入力スキーマ・既定値・フィールド説明を返す（呼び出し方の自己発見）。
- `execute_custom_tool("vacc_list_presets")` — プリセット一覧を返す。

成功時は再着色結果 JSON（`RecolorResult`）が success data に載る。エラーは `ErrorResponse` で返る。
利用可能ツールは `mcpforunity://custom-tools` リソースで発見できる。

### 3. batchmode CLI（MCP を介さない完全ヘッドレス・CI 用）

```
Unity.exe -batchmode -quit -projectPath <host> \
  -executeMethod Camereo.CamereoAutomation.RunFromCommandLine \
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
  マスクを反映したい場合は当面 UI（CamereoWindow）から実行する。
- カスタム MCP ツール登録（`vacc_recolor` / `vacc_describe_schema` / `vacc_list_presets` を第一級ツールとして出す）は
  `Code/McpIntegration/` で実装済み。MCPForUnity（`com.coplaydev.unity-mcp`）導入時のみ `CAMEREO_MCP_PRESENT`
  ゲートでコンパイルされ、配布パッケージ本体は依存ゼロを維持する。
