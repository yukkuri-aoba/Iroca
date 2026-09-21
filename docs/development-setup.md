# 開発環境セットアップ（clone から全テスト緑まで）

新しいマシン、または環境を失ったときに **クローンだけで開発・検証環境を復元する**ための手順。
テスト構成そのものの説明は `docs/testing-architecture.md` が正。ここは「何をどこに置き、
何を 1 回だけ実行するか」を並べる。

2026-09-21 に、実際に本体 + dev_safe を空のディレクトリへクローンして全テストを走らせ、
この手順で緑になることを確認した（所要: 初回 8 分 16 秒 / 698 tests）。

## 0. 構成リポジトリ

| リポジトリ | 公開 | 内容 | 無いとどうなる |
|---|---|---|---|
| `yukkuri-aoba/Iroca` | 公開 | 製品 C#・ハーネス・視覚レビューツール | — |
| `yukkuri-aoba/Iroca_dev_safe` | private | PSD・GT・ベースライン・pytest 本体・視覚レビュー承認 | テストが 1 件も無い |
| `yukkuri-aoba/Iroca-Models` | 公開 | AI マスク提案の ONNX（配布元。`scripts/Fetch-Models.ps1` が取得） | 主経路ゲートが skip |
| `yukkuri-aoba/Iroca_Unity` | private | VRChat 側ホスト Unity プロジェクト（撮影台・実操作確認） | UI 実機確認と販促/マニュアル撮影ができない |
| `yukkuri-aoba/Iroca_MLDev` | private | Sentis 検証用ホスト（`IrocaSentisCheck` の前提） | `Code/SentisIntegration/` の型チェックができない |

## 1. 前提ソフト

| 要素 | 版 | 備考 |
|---|---|---|
| Unity | **2022.3.22f1** | ハーネスは `UnityEngine.CoreModule.dll` だけあれば動く（未インストール時の代替は testing-architecture.md 参照） |
| .NET SDK | 8.0+ | ハーネス・build-check のビルド |
| Python | 3.13+（検証機は 3.14.6） | リポジトリ直下に `.venv` |
| PowerShell | Windows PowerShell 5.1 で可 | `scripts/*.ps1` |

## 2. 配置

`dev_safe` は**本体リポジトリの直下にその名前で**クローンする（本体の `.gitignore` が
`/dev_safe` を除外しているのが前提の構成）。ホスト Unity プロジェクトは本体の 1 つ上の
`Avatar_Projects/` に置くと、`IrocaSentisCheck.csproj` の既定パスがそのまま通る。

```
<任意の親フォルダ>/
├─ Iroca/                      ← yukkuri-aoba/Iroca（develop）
│   └─ dev_safe/               ← yukkuri-aoba/Iroca_dev_safe（main）
└─ Avatar_Projects/
    ├─ Iroca_Dev/              ← yukkuri-aoba/Iroca_Unity
    └─ Iroca_MLDev/            ← yukkuri-aoba/Iroca_MLDev（Sentis 検証をするときだけ）
```

```powershell
git clone https://github.com/yukkuri-aoba/Iroca                Iroca
git clone https://github.com/yukkuri-aoba/Iroca_dev_safe       Iroca\dev_safe
git clone https://github.com/yukkuri-aoba/Iroca_Unity          ..\Avatar_Projects\Iroca_Dev
cd Iroca
git switch develop
```

## 3. clone 直後に 1 回だけ実行する 5 つ

**どれも飛ばすと「壊れる」のではなく「静かに検査されなくなる」。** 順に実行する。

```powershell
# (1) pre-commit フック（視覚レビューの出荷ゲート）を有効化
#     未設定だと test_hooks_path_is_configured が fail する（唯一 fail で気づける項目）
git config core.hooksPath scripts/hooks

# (2) Python 環境
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r dev_safe\Tests\requirements.txt

# (3) 通知ルール（本体リポジトリには置けないローカル設定。dev_safe/setup/README.md 参照）
Copy-Item dev_safe\setup\claude-notify.md .claude\instructions\notify.md

# (4) AI マスク提案の ONNX モデルを取得（%LOCALAPPDATA%\Iroca\Models へ）
#     sha256 は製品コードから読むので、ここでハッシュを書き写す必要はない
powershell -ExecutionPolicy Bypass -File scripts\Fetch-Models.ps1

# (5) SAM エンコーダ埋め込みを再生成（4MB × テクスチャ数。リポジトリには入らない）
.\.venv\Scripts\python.exe dev_safe\scripts\freeze_sam_fixtures.py
```

### (5) を飛ばしたときに何が起きるか（2026-09-21 実測）

埋め込み（`texture_sample/ground_truth/sam_fixtures/*_embedding.bin`）は
「モデル + テクスチャから決定的に再生成できるキャッシュ」なので `.gitignore` 対象。
だが**テストは自動生成せず skip する**ため、クローン直後に全テストを回すと:

```
1 failed, 697 passed, 81 skipped   ← 81 のうち 79 が「埋め込みがありません」
```

skip したのは `test_autotune_evidence`（ワンショット）・`test_assisted_include`
（追加操作後）・`test_oneshot_vs_preset`（プリセット比較）、つまり**主経路の品質ゲート全部**。
`freeze_sam_fixtures.py` 実行後は同じ 2 ファイルが `57 passed, 12 xfailed`（skip 0）になる。
**「緑に見えるのにゲートが走っていない」状態を避けるため、(5) は必須**。

なお再生成しても追跡済みの凍結ロジット（`*_logits.bin` / `*_scores.bin`）はバイト一致する。
`index.json` だけ生成時刻と行順が変わる（コミットしない）。

## 4. 確認

```powershell
.\.venv\Scripts\python.exe -m pytest -q
```

正常時に残る skip は次の 2 件だけ（どちらも仕様どおりの未計測）:

- `test_csharp_quality_gate` … 既定は bandana のみ。全被写体は `IROCA_CSHARP_GATE_FULL=1`
- `test_repro_cases` … 調査中で expected 未凍結のケース 1 件

## 5. Unity 側（UI 実機確認・撮影・Sentis）

pytest は Unity を起動しないので、ここまでで再着色アルゴリズムの検証は完結する。
Editor UI の実機確認と `#if UNITY_EDITOR` のコンパイル検証はホスト経由でしか行えない。

```powershell
# VRChat 側ホスト（Iroca_Unity）
cd ..\Avatar_Projects\Iroca_Dev
vpm resolve project .                       # Packages/ の実体を復元（VCC からでも可）
cd ..\..\Iroca
.\scripts\Link-HostPackage.ps1 -HostProject "..\Avatar_Projects\Iroca_Dev"

# Sentis 検証をするときだけ（Iroca_MLDev）
.\scripts\Link-HostPackage.ps1 -HostProject "..\Avatar_Projects\Iroca_MLDev"
dotnet build scripts\build-check\IrocaSentisCheck.csproj
```

詳細（Unity 未インストール環境での DLL シム、batchmode でのコンパイル確認、
`manifest.json` に本体ルートを書いてはいけない理由）は `docs/testing-architecture.md`。

## どのリポジトリにも入らないもの

| もの | 復元方法 |
|---|---|
| Unity 本体・VRChat SDK・lilToon 等の再配布物 | Unity Hub / VPM（版は `ProjectVersion.txt` と `vpm-manifest.json` で固定） |
| `mobile_sam_*.onnx` | `scripts/Fetch-Models.ps1`（Iroca-Models から sha256 検証つき） |
| SAM エンコーダ埋め込み・ハーネス実行キャッシュ | 上記 (5) と初回テスト実行で再生成 |
| `mobile_sam.pt`（モデル再エクスポート元） | 上流 MobileSAM から取得（`dev_safe/ml/README.md`） |
| `.claude/instructions/notify.md` | `dev_safe/setup/claude-notify.md` からコピー |
