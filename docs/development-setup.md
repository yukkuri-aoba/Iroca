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
| Node.js | 検証機は 24.19.0 | リリースのときだけ。`scripts/Build-Installer.ps1`（`Build-VpmPackage.ps1` から呼ばれる）が VPAI の `creator.mjs` を node で実行する。無いと止まる |

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
#     ホールドアウト被写体の埋め込みは --holdout を付けたときだけ作られる
#     （無いと dev_safe\scripts\holdout_eval.py が終了コード 2 で止まる）
.\.venv\Scripts\python.exe dev_safe\scripts\freeze_sam_fixtures.py --holdout
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

# NDMF 連携と「VRChat SDK あり・NDMF なし」の分岐の型チェック（scripts/build-check/README.md）。
# 前者は Iroca_Dev を一度 Unity で開いてできる Library/ScriptAssemblies の NDMF を、後者は Packages の VRChat SDK を借りる
dotnet build scripts\build-check\IrocaNdmfCheck.csproj
dotnet build scripts\build-check\IrocaVrcsdkCheck.csproj

# Sentis 検証をするときだけ（Iroca_MLDev）
.\scripts\Link-HostPackage.ps1 -HostProject "..\Avatar_Projects\Iroca_MLDev"
dotnet build scripts\build-check\IrocaSentisCheck.csproj
```

Unity 6 の確認用に、VRChat SDK も NDMF も入れない検証ホストを `Avatar_Projects/` に 2 つ置いている
（リポジトリは無く、手元で作る）: `Iroca_U6`（6000.0.67f1・`com.unity.ai.inference` 2.4.1・test-framework 1.4.6）と
`Iroca_U2022`（2022.3.22f1・`com.unity.sentis` 2.1.3・test-framework 1.1.33）。どちらも
`Link-HostPackage.ps1 -WithEditorTests` で繋ぎ、`Run-EditorTests.ps1 -HostProject <ホスト> -UnityExe <その版の Unity.exe>`
で EditMode テストを回す。6.5 以降で必要になる修正の一覧と、最新の Unity の DLL でコンパイルを確かめる手順は
`docs/unity-version-migration.md`。

詳細（Unity 未インストール環境での DLL シム、batchmode でのコンパイル確認、
`manifest.json` に本体ルートを書いてはいけない理由）は `docs/testing-architecture.md`。

## 6. クラウドのセッション（Linux。2026-10-26〜）

claude.ai のクラウドのセッション（Ubuntu のコンテナ）でも、Unity を起動しない範囲の検証は回せる:
ハーネスと build-check のコンパイル、golden・ソース構造検査・unit-run、dev_safe の回帰テスト、abcheck、
EditMode テストのコンパイル（`dotnet build scripts/build-check/IrocaEditorTests.csproj`。2026-10-27〜）。
Unity 本体が要るもの（EditMode テストの実行・`RuntimeParityTests`・MemoryBench・PerfBench・実機の確認）は手元で行う。

- 環境の設定で、リポジトリに `Iroca` と `Iroca_dev_safe` の両方を選ぶ。
- 環境の設定の「セットアップスクリプト」に `bash Iroca/scripts/cloud/setup.sh` を書く（リポジトリの 1 つ上で始まる）。
  - .NET SDK・Unity 2022.3.22f1 の DLL（Unity の配布元から約 3.2GB を取って `Editor/Data/Managed` だけ展開）・
    AI のモデル・Unity Test Framework と Unity の NUnit のパッケージ（EditMode テストのコンパイル用）を
    `/opt/iroca-env` に入れる。
  - dev_safe を本体の直下へ移し、元の場所にリンクを残す。dev_safe のスクリプトは自分の実体の場所から本体を探すので、
    本体の直下にリンクを張るだけでは動かない。
  - `.venv` を作り、フックを有効にし、SAM の埋め込みを作る。
  - 全部で 10 分ほどかかる。何度回してもよい（入っているものは飛ばす）。
- コマンドごとにシェルが新しくなるので、使う前に毎回 `. scripts/cloud/env.sh` を読み込む:
  ```bash
  . scripts/cloud/env.sh && .venv/bin/python -m pytest scripts/golden scripts/source_checks scripts/unit-run -q
  ```
- golden はハッシュで厳密に比べる。2026-10-26 にこの環境で 53 件すべてが Windows で固定したハッシュと一致した
  （CI は別の Linux イメージなので画素比較のまま）。ハーネスの出力は Windows と同じと見てよい。
- ディスクは約 30GB しか無い。回帰を回すと、作り直せる作業ディレクトリが数 GB ずつ増える。
  - 増えるもの: `dev_safe/_sam_e2e_work`（約 4GB）・`_sam_freeze_work`・`_iou_csharp_work`・`_csharp_gate_work`。
  - 回し終えたら消してよい（`cache_gc.py` が消してよいとしているもの）。
  - ハーネスの実行キャッシュは `env.sh` が上限を 8GB にしている。
- 2026-10-26 にこのセッションで確かめたこと:
  - abcheck は Linux でも回る。ただし `dev_safe/scripts/refactor_abcheck/abcheck.py` の `CRASH_RC`（.NET の未処理例外の
    終了コード）が Windows の値（0xE0434352）に固定されていて、自己検査 2 件が落ちる。Linux では SIGABRT（-6）になる。
    `os.name` で分けると自己検査が通り、作業ツリーとの比較（4 グループ 317 単位）も一致した。
  - dev_safe の回帰は 998 件が通り、78 件が落ちた。落ちたのは、0.3.0 の色の変更（3f074c4・c5c036e）の後の出力と
    比べる基準（画素数の完全一致・品質ゲート・グラデの継ぎ目・repro の期待値など）。その直前の C# に戻すと、
    グラデの継ぎ目と品質ゲートは通った。GitHub の dev_safe に、0.3.0 の作業でした基準の作り直しが入っていない
    ためと見ている（作業は dev-0.3.0 の worktree の dev_safe 複製で行われた）。
  - `freeze_sam_fixtures.py` は、追跡している凍結ロジット（`*_logits.bin` / `*_scores.bin`）も作り直す。Linux の
    onnxruntime では最下位ビットが Windows と違うので、`setup.sh` は作り直した後に追跡している版へ戻す。

## どのリポジトリにも入らないもの

| もの | 復元方法 |
|---|---|
| Unity 本体・VRChat SDK・lilToon 等の再配布物 | Unity Hub / VPM（版は `ProjectVersion.txt` と `vpm-manifest.json` で固定） |
| `mobile_sam_*.onnx` | `scripts/Fetch-Models.ps1`（Iroca-Models から sha256 検証つき） |
| SAM エンコーダ埋め込み・ハーネス実行キャッシュ | 上記 (5) と初回テスト実行で再生成 |
| `mobile_sam.pt`（モデル再エクスポート元） | 上流 MobileSAM から取得（`dev_safe/ml/README.md`） |
| `.claude/instructions/notify.md` | `dev_safe/setup/claude-notify.md` からコピー |
| Claude Code のメモリ（`~\.claude\projects\<パス>\memory\`） | private の `claude-memory` リポジトリで同期（手順はその README）。ディレクトリ名は本体の絶対パスから決まるので、**新しい PC でも同じパス（`C:\Users\<同じユーザー名>\Documents\Iroca`）に clone する**。パスが違うとメモリが紐付かない |
