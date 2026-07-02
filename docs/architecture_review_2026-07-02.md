# Iroca 設計レビュー（2026-07-02・5観点統合）

**対象**: リポジトリ全体（`Code/` 計 ~13,200 行 / 40 ファイル、テスト基盤、配布パイプライン、永続化層）
**方法**: 5 観点（コアアルゴリズム / UI / データモデル・永続化 / テスト・開発基盤 / 配布・リリース・運用)の並行レビュー。各観点は独立に実施し、[`architecture_review_2026-06-27.md`](architecture_review_2026-06-27.md) の指摘の追跡を含む。静的読解＋一部ネットワーク実測（VPM listing の URL 到達性）。
**位置づけ**: 前回レビューが「コード構造」に軸足を置いたのに対し、本レビューは**運用インフラ（リリース・強制機構・データ生存性）**まで対象を広げた。前回と重複する結論は追跡表に集約し再掲しない。

> 行番号は執筆時点の値。位置は file:line ではなくシンボル名で辿ること。
> 「07-02 対応済」と記した項目は本レビュー直後の修正コミットを指す。

---

## 0. 総評

**アルゴリズム層の設計品質と「出力ビット不変」の規律は引き続き一級。一方、それを支える「強制と配布のインフラ」がレビュー時点でほぼ全て機能停止していた。**

| 観点 | 評価 | 一言 |
|------|:----:|------|
| コア計算層の分離・性能・規律 | ★★★★★ | 前回から維持。ビット不変契約の文書化・ArrayPool 規律・閾値の由来トレーサビリティは模範的 |
| 非同期 UI 基盤（PreviewJob） | ★★★★★ | 8 ジョブ全てが同一パターン。BG から Unity API に触る経路が構造的に無い |
| Undo・ローカライズ・tooltip 規律 | ★★★★☆ | 「1 操作=1 ステップ」成立。tooltip 抜けは自明な 6 箇所のみ |
| コード物量管理 | ★★★☆☆ | partial 分割・RecolorParams 化は実体あり。ProcessPixelsArray は 600→757 行に**悪化** |
| View↔Window 結合 | ★★☆☆☆ | 逆依存 66→74 箇所に微増。`_host._maskView` の View→View 直接結合 8 箇所が最悪部 |
| データ生存性（マスク・プリセット） | ★★☆☆☆ | 互換イディオムは丁寧だが、**黙って消える経路が 3 つ**実在 |
| テストの強制機構 | ★☆☆☆☆ | 視覚レビュー起動不能（07-02 修復済）＋ pre-commit フック実体なし＋ CI 検証ゼロ |
| リリースパイプライン | ★☆☆☆☆ | release.yml が**構造的に壊れており**、VPM listing は公開すると全滅（実測 404） |

---

## 1. 🚨 最優先の発見（レビュー時点で壊れていたもの）

### 1.1 リリースパイプラインの破損（配布観点・テスト観点の両レビューが独立に検出）

- `.github/workflows/release.yml:49-50` が**存在しないステップ ID**（`steps.pkg` / `steps.zip`）の outputs を参照。空文字に評価されるため **zip / unitypackage は永久にリリースへ添付されない**（添付されるのは README/MANUAL のみ）。
- draft 作成ステップが **2 重**（L31-37 と L39-50、リリース名も "v0.2.0" / "Iroca v0.2.0" で不一致、changelog body は前者のみ）。
- tag ↔ `package.json` ↔ CHANGELOG 節 ↔ `docs/index.json` のバージョン整合チェックが一切ない。CHANGELOG に該当節がなくても awk は失敗せず**空ボディで黙って draft が立つ**。
- `.claude/instructions/git.md` のリリース手順（`Release/Camereo_Ver*.unitypackage` 配置）は **Release/ ディレクトリ自体が存在せず**完全に陳腐化。

### 1.2 VPM listing は公開した瞬間に全バージョンがインストール不能（実測）

- `docs/index.json` の zip URL は **0.1.0 / 0.2.0 とも 404**（ネットワーク実測）。
- **0.1.0 は修復不能**: 公開リリース資産は旧名（`com.yukkuri-aoba.vrc-avatar-color-changer-0.1.0.zip` / `VACC_Ver0.1.0.unitypackage`）のみで、zip 内 package.json も旧 ID のはず。記録にあった「SHA 再生成」では直らない。**エントリ削除が現実解**。
- **0.2.0 は draft 未公開＋（1.1 のバグにより）zip 未添付**。
- 救い: GitHub Pages 未公開のため**現時点で実害はまだ出ていない**。ただし「Pages を有効化すれば公開できる」状態ではない。

### 1.3 品質の強制機構が全停止

- `tools/visual_review.py:48` が**削除済み `vacc_python` をモジュールトップで import** し、snapshot / compare / approve 全コマンドが ImportError で即死（`--engine csharp` でも回避不能）。CLAUDE.md が「必須」と定める視覚レビューが物理的に実行不能だった。→ **07-02 修復済（d5fb5c6、実 C# エンジン既定化）**
- `.git/hooks/` には `*.sample` しかなく **pre-commit フックの実体が存在しない**。`tools/check_visual_review.py` は誰からも呼ばれない孤児コード。フックは git 追跡されておらずインストーラもないため、消えても誰も気づかない構造。傍証: `approved.json` の最終承認は **2026-06-24 で停止**し、以降の `Code/` 変更コミット（4183471, b32be13 等）は視覚レビューなしで通過している。
- CI は release.yml のみで、build-check すら回っていない（`scripts/build-check/README` は「CI で回す」と記すが実在しない）。

### 1.4 ユーザーデータ（マスク）が黙って消える経路 ×3

1. **解像度変更で全破棄**: `MaskPaintView.EnsureMasks()`（:203-210）は解像度が変わると共通・全ゾーンマスクを無警告破棄する。import Max Size を 2048→4096 に変えただけで発火し、1 ストローク描いた瞬間に破棄が確定、`SaveToSession()` で**破棄状態がファイルへ永続化**される。皮肉なことに処理側（`PixelProcessor.cs:2180-2182`）は最近傍スケーリングで解像度不一致を吸収できるため、この破棄は必要以上に破壊的。
2. **読込失敗 → 削除**: `MaskFileStore.LoadMask`（:74-88）はウイルススキャナ等の**一時的な IO 失敗でも** warning ログのみで null を返し、その状態でウィンドウを閉じると空保存判定（:47-55）が**無傷のファイルを削除**する。
3. **非アトミック書き込み**: PresetStore / MaskFileStore とも `File.WriteAllText` の直接上書き（`PresetStore.cs:132` / `MaskFileStore.cs:60`）。書き込み中クラッシュ・ディスクフルで既存データごと破損し、破損後は 2. の削除チェーンに合流する。

---

## 2. 前回レビュー（2026-06-27）指摘の追跡

| 当時の指摘 | 判定 | 根拠 |
|---|:---:|---|
| RecolorPixel ≈30 引数 | ✅ 解消 | `readonly struct RecolorParams`（PixelProcessor.cs:2717-2747）＋ `in` 渡し 7 引数を実コード確認。ただしコンストラクタは **26 個の位置引数**（同型 float の取り違えをコンパイラが検出できない）、`GetRelaxedMatchStrength` 20 引数・`RecoverBoundaryEdges` 16 引数が残存 |
| C#↔Python 二重実装 | ✅ 解消 | `algorithm.py` 参照コメント 0 件（grep 実測）。golden 自己回帰へ移行済み |
| ColorZone マジックナンバー | ✅ 解消 | named const + sweep 由来まで記載した根拠コメント（ColorZone.cs:18-87）。ただし複製側 `GetRelaxedMatchStrength` にはインライン残存（→ §3 W2） |
| IrocaWindow 神クラス化 | 🔶 部分解消 | partial 4 分割・`IrocaSessionState` 集約は実体あり。責務（レイアウト/ゾーン CRUD/D&D/View 統制/自動調整/Undo）は依然 1 クラスに同居 |
| View→Window 逆依存 66 箇所 | ❌ 未解消・微増 | 現在 **74 箇所**（PreviewView 31 / MaskPaintView 16 / PresetsView 10 / ExportView 9 / DetailPreviewView 8）。増加分は新機能での既存パターン踏襲であり無秩序な悪化ではない |
| PixelProcessor 肥大 | ❌ 悪化 | 2588→**2954 行**、本体オーバーロード 600→**757 行**（:291-1047）。増分は段の複雑化ではなく「選択キャッシュ・パリティ転写・フェーズ計測・レイヤー排他合成という**直交機能のオーケストレータへの集積**」 |
| §6.2.3 null/寸法ガード・ゾーン 0 件契約・完了済み計画コメント | ❌ 未解消（低優先のまま） | :486 に「(M4 で…する予定)」— 実装は :526-537 に既に存在する同種の陳腐コメントを新規発見 |

---

## 3. 観点別の新規発見

### 3.1 コアアルゴリズム層

**強み**
1. **「出力ビット不変」契約の徹底的な明文化** — 全性能最適化が「なぜ出力が変わらないか」の証明コメントを持つ（bbox 限定 :542-548、PropagateHighlights の bbox 証明 :1524-1528、GaussianBlur の矩形外ゼロ一致 :2213-2215）。
2. **並行実行の防御設計（プレビュー系）** — BG へは ColorZone を**クローンして**渡す（PreviewView.cs:1016-1019）、SelectionCache は lock＋Store 時コピー、ArrayPool は「null 初期化→try 内 Rent→finally 返却」が 10 本借用の `CleanAchromaFringe` まで貫徹。返却漏れ経路は確認できなかった。
3. **閾値の由来トレーサビリティ** — `CoreMatchDistance=0.14`「複数被写体の sweep で確定」等、改善サイクル指針「統計量から導く」がコードコメントで検証可能。

**弱み（重要度順）**

- **W1【高】選択キャッシュキーが手動ミラー** — `BuildSelectionKey`（PixelProcessor.cs:220-248）は選択に影響する ColorZone フィールドを手で列挙する。フィールド追加時にキー更新を忘れると**キャッシュ誤ヒットで古い選択のままのプレビュー**になる。Export は selectionCache 不使用（ExportView.cs:239-244）なので最終出力は無事＝かえって発見が遅れる。強制する仕組み（テスト/リフレクション照合）がない。
- **W2【高】マッチロジックの C# 内部二重実装** — `GetRelaxedMatchStrength`（:2518-2603）は ColorZone のグレーモード/ハイブリッド距離の複製で、同期はコメント頼み。**既に乖離がある**: ColorZone は `Mathf.Lerp(GrayModeBaseChromaThreshold, chromaThreshold, …)`（ColorZone.cs:474、`chromaThreshold` は**ユーザー可変**）だが、複製側は `Mathf.Lerp(0.30f, 0.05f, sV/0.20f)`（:2527）と**既定値 0.05 を焼き込み**。ユーザーが chromaThreshold を変えると主経路と穴埋め/境界回復でグレーモード判定が食い違う。`ChromaGate*` 3 定数も両ファイルに複製。
- **W3【中】Export だけゾーン未クローン** — ExportView.cs:215 は生の ColorZone リストを Task.Run へ渡す（プレビュー系のクローン防御と不整合）。「かんたんモードの自動調整はエクスポート中も裏で走る」設計（IrocaWindow.Layout.cs:56-58 コメント)のため、auto-tune の apply やグローバル Undo がゾーンを変異させると一部ゾーンだけ新旧混在の出力になり得る。クラッシュしない分気づきにくい。
- **W4【中】キャンセル粒度が粗く、コメントと実装が不一致** — ヘルパー段（Decontaminate/CleanAchromaFringe/Blur/FillSmallHoles/RecoverBoundaryEdges）は token なしの `ParallelOptions` を各自 new しており、:383-386 のコメント（内部もキャンセル例外を伝播し得る）と食い違う。4K で Decontam 単体 ~300ms が中断不能。
- **W5【低】死にフィールド** — `edgeStopThreshold`（ColorZone.cs:126）は選択キーへのハッシュ投入以外**どこからも読まれていない**（旧エッジ停止型 flood fill の遺物）。
- **W6【低】読み手負担** — 非インデント try 本体が複数箇所（:333-341→閉じ :1034 等）、再着色ループは実質 8 段ネスト。

### 3.2 UI 層

**強み**: PreviewJob 基盤（世代管理＋協調キャンセル＋`EditorApplication.update` ポンプ復帰）を**8 ジョブ全てが同一パターンで使用**し、UI フリーズの構造的リスクは低い。Undo は「変更検知時のみ記録」ラッパ＋「Sync→Register→Sync」プロトコルで「1 操作=1 ステップ」が成立。ローカライズはプロパティ方式でキー欠落がコンパイルエラーになり未翻訳キーが構造的に存在しない。

**弱み（重要度順）**

- **①【Major】マスクペイント/スポイトの 1 機能が 2〜4 ファイルに分割** — 最悪部は `_host._maskView` 経由の View→View 結合 8 箇所（PreviewView.cs:322,700,765,930,1012,1188、DetailPreviewView.cs:141,295）。PreviewView が `maskView.isPainting` / `_maskStrokeStarted` / `lastPaintUV` を直接書き換え、`BeginStroke→PaintMask→lastPaintUV` の内部プロトコルを順序依存で操作する（:704-737, :928-959）。
- **②【Major】previewDirty トリガが粗い** — `BeginChangeCheck` が左カラム全体を包む（IrocaWindow.Layout.cs:98-144, 195-203）ため、ブラシサイズ・ペイントモード切替・フォールドアウト開閉でも 4K フル再生成がスケジュールされる。さらに `OnUndoRedoPerformed`（IrocaWindow.cs:125-136）はグローバル `Undo.undoRedoPerformed` 購読のため、**Iroca と無関係なシーン編集の Ctrl+Z でも**無条件 `MarkPreviewDirty()`。
- **③【Medium】マスク状態の三重表現と手動同期規約** — `bool[]` バッファ ↔ RLE 文字列（session）↔ MaskCache ファイルを Sync 系 4 メソッドの「呼び忘れゼロ前提」で整合。同期呼び出しは 9 箇所に散在し、新機能追加のたびに人手で再現が必要。
- **④【Medium】index 参照と id 参照の混在** — `EyedropperZoneIndex`（IrocaWindow.cs:38-39）は範囲チェックのみで**並べ替え・削除の index シフトに追従しない**。スポイト武装中にゾーンをドラッグ並べ替えすると別ゾーンに色が入るエッジケースあり。自動調整は GUID 再ルックアップ（IrocaWindow.AutoTune.cs:17-19,190）で正しく解いており、ツール内で流儀が割れている。
- **⑤【Minor】死んだ SerializedObject 基盤** — `_windowSerializedObject/_sessionProperty/_zonesProperty`（IrocaWindow.cs:58-60）は生成・破棄のみで使用箇所ゼロ。コメントは存在しないクラス `ColorZoneDrawer` に言及。
- **⑥【Minor】メインスレッドヒッチ残存** — `EnsureTrueSource`（PreviewView.cs:201-240）がフル解像度 PNG の読込＋デコードを OnGUI 中に実行（テクスチャ切替時 1 回、4K で可視ヒッチ）。

tooltip 規約の抜けは 6 箇所のみ（EnableReadWrite / AddZone / キャンセル×2 / Credit / Toolbar 2 箇所は string[] 渡しで tooltip 不能）。かんたん/上級分岐は 6 箇所に限定され散っていない。

### 3.3 データモデル・永続化・後方互換

**強み**: GUID ベースのマスク追従＋二段構えの orphan 清理。JsonUtility の「欠落フィールド＝初期化子値」仕様を明示的に設計へ組み込み（PresetsView.cs:190-194 のコメントは秀逸)、`layerIndex` の一度きり移行・`"R:"` プレフィックスのフォーマット自己識別など互換イディオムは水準以上。失敗の通知化も概ね徹底。

**弱み（§1.4 の消失経路 3 件に続き）**

- **スキーマバージョンフィールドが存在しない**（IrocaPresetData.cs:9-33、MaskState.cs:15-22）— 「欠落＝新既定を採用」ポリシーは一貫しているが（autoRecolorAnchor false→true、useFloodFill 新設既定 true が**旧プリセットの出力を黙って変える**ことを意図的に許容)、バージョン刻印がないため「旧 JSON を検出して警告」「将来ポリシー変更」の選択肢が永久に取れない。
- **リネームは完全な切り捨て** — 旧フォルダ（`Assets/Camereo/Presets`、`%APPDATA%/CamereoPresets`、`UserSettings/Camereo/MaskCache`）を読むコードはゼロ。さらに CHANGELOG:15 の「以前のバージョンのマスクデータから自動で移行します」は、移行コードが探すキーが `Iroca_Mask*`（MaskPaintView.cs:571-573）である一方、実際の旧リリース（Camereo 期）が書いたキーは `Camereo_Mask*` のはずで、**現実の旧データに対して一度も発火しない**。旧名での配布実績の有無で対応要否が決まる（要ユーザー判断）。
- **enum の int シリアライズ**（SelectionMode）に「末尾追加のみ」の制約が未記載。`advancedMode` は死にフィールドなのに非推奨表記なし。Automation の `layerIndex` 受理は**サイレント無視**でプリセット経路と挙動が食い違う。
- **orphan ゾーンマスクの無限蓄積** — プリセット読込でゾーン総入れ替え時、旧 ID のマスクがファイルに残り続ける（SyncBuffersToState は現存ゾーンと突き合わせない）。
- **Assets 外テクスチャのマスクは「成功」を返しつつ永続化されない**（MaskFileStore.cs:44-45）— 失敗通知が発火せず、エディタ再起動でマスクが黙って消える。

### 3.4 テスト・開発基盤

**強み**: 実 C# 直測アーキテクチャの一貫性（Harness が Code/ を同一アセンブリに取り込み internal アクセス、IoU を出力 RGBA 差分で定義、決定論性テスト付き）。`KNOWN_BROKEN` の strict xfail は「直った瞬間 xpass→fail でエントリ削除を強制」する優れたパターン。baseline は再生成拒否・golden は git 追跡で上書き防御。

**弱み（§1.3 の強制機構停止に続き）**

- **「ハーネスビルド失敗 → pytest.skip」の広域サイレント skip** — `Code/Core` のコンパイルエラー（＝製品退行そのもの)でも環境不備と区別せず skip するテストが 8 ファイル（test_csharp_quality_gate.py:74-78 ほか）。`-k "quality"` のような部分実行では壊れたコードが「緑（全部 skip）」に見える。**DLL パス・ビルド手順が 10 ファイル 13 箇所にコピペ**されており、VACCHeadless.dll サイレント skip 事故と同型の再発温床。
- **品質ゲートと視覚レビューが「別パラメータ」を測っている** — `Harness.cs` の `ZoneCfg` 既定値と `fixtures.ZoneSpec` 既定値が乖離（shadowDesaturation 0.35 vs 0.0、valueBlend 1.0 vs 0.85、autoRecolorAnchor true vs false 等）。IoU 経路は全フィールド明示送信で安全だが、quality_gate と visual_review は 7 フィールドしか送らず**残りは Harness 既定にフォールバック**。
- **既知 fail の台帳がコード外** — exact-count baseline 15 件の常時 fail、DLL 名修正で露呈した 3 件がコード内に xfail/コメントの痕跡なし（エージェントのメモリのみ）。「全パスでのみコミット」ルールと矛盾が常態化し、新規 fail が埋もれるアラーム疲れ構造。
- **dev_safe 全部 gitignore** — GT 画像は妥当だが、`fixtures.py`・`test_*.py`・しきい値などテストコード自体も履歴・バックアップなし。ディスク事故で回帰基盤が全損する。
- **UI/Automation/Infra 層のテスト皆無** — build-check の型チェックのみ。Unity Test Runner の asmdef も存在しない。
- **Harness の Unity 更新脆弱性** — 2022.3.22f1 ハードコード既定・DLL 存在チェック Target なし（build-check 側にはある）・Code/Core の明示ファイルリストが新規ファイル追加で壊れ skip 連鎖へ。

### 3.5 配布・リリース・運用

**強み**: MCP 衛星の三重ゲート分離（versionDefines + defineConstraints + `#if`、本体 asmdef references 空）は模範的。Automation は静的 API / MCP / batchmode CLI の 3 経路全てが `RunRecolorCore` に収斂し、構造化 JSON で結果報告。ユーザー可視層のリネームは徹底（Code/・README・package.json に VACC/Camereo ゼロ、EditorPrefs も `Iroca.*` で一貫）。

**弱み（§1.1-1.2 に続き）**

- **unitypackage ビルドが再現不能・検証不能** — `BuildHelper.cs` は `Assets/Iroca` を再帰エクスポートするが、その Unity プロジェクトはリポ外で同期手順不明（コメントが参照する `build/ExportUnityPackage.ps1` は存在しない）。開発プロジェクト側に `Code_Archive/`（旧名コード）が紛れていればそのまま出荷され CI で検知不能。
- **Debug 衛星が全ユーザーに配布される** — `Build-VpmPackage.ps1` は Code/ を再帰同梱、`IrocaEditor.Debug.asmdef` は defineConstraints なし・autoReferenced=true で常時コンパイル → Debug ウィンドウが全ユーザーに見える。`AssemblyInfo.cs` のコメント（非同梱運用想定）と配布スクリプトが矛盾。
- **`RunFromCommandLine` が失敗時も exit code 0** — `EditorApplication.Exit(1)` を呼ばず result.json とログのみ。batchmode 呼び出し側が失敗を終了コードで検知できない。
- **README が 0.2.0 の UI と矛盾** — 「UV 矩形」「レイヤー番号で優先度」を案内するが、0.2.0 は「優先度=並び順」「UV 矩形は UI 非表示」。MANUAL のスクショはプレースホルダ 6 箇所。
- **`Build-VpmPackage.ps1` の SHA 事故導線** — `-UnityPackagePath` なし実行→再実行忘れで SHA 不一致 zip が世に出る（警告のみで強制なし）。zip に .meta なし（VCC 展開時 GUID 不安定）、zip 内 unitypackage 同梱の二重ペイロード。

---

## 4. 改善ロードマップ（優先度順）

### 高（リリース前に必須・計 2〜3 日）

| # | 項目 | 内容 | 状態 |
|---|------|------|------|
| H1 | release.yml 修正 | dangling 参照除去・draft 一本化・tag↔package.json↔CHANGELOG↔index.json 整合チェック（不一致 fail） | 07-02 対応 |
| H2 | listing 復旧 | index.json から 0.1.0 削除（修復不能）→ 0.2.0 zip 実アップロード→SHA 再計算→publish→Pages 有効化のチェックリスト化 | 07-02 一部対応（0.1.0 削除・手順書） |
| H3 | 視覚レビュー復旧 | vacc_python import 撤去・実 C# 既定化 | ✅ d5fb5c6 |
| H4 | pre-commit フック追跡化 | `scripts/hooks/` に実体を git 追跡し、設置手順を明文化 | 07-02 対応 |
| H5 | マスク生存性 3 点 | アトミック書き込み（temp+Replace）／解像度リスケール／読込失敗セッションの削除ガード | 07-02 対応 |
| H6 | ハーネス skip 分離 | 「dotnet/DLL 不在のみ skip、コンパイルエラーは fail」＋ DLL パス解決の一本化 | 07-02 対応（scripts 側） |
| H7 | 選択キー網羅性テスト | ColorZone 選択系フィールドと BuildSelectionKey の整合を機械検証 | 07-02 対応 |

### 中

- `GetRelaxedMatchStrength` の定数共有と `0.05f` 焼き込みの意図判定（golden でビット不変確認の上で）
- Export のゾーンクローン統一（ExportView.cs:215、1 行）
- previewDirty トリガ精緻化（`_maskView.Draw()` を ChangeCheck 外へ・Undo の自分向け判定）
- `EyedropperZoneIndex` / `activeMaskTarget` の zone.id 化（自動調整と同方式）
- `IrocaPresetData` / `MaskState` への schemaVersion 追加（書くだけなら 1 時間）
- Debug 衛星の出荷方針決定（除外 or AssemblyInfo コメント修正）
- `RunFromCommandLine` の exit code、README の 0.2.0 追随
- dev_safe のうち**テストコードのみ** git 追跡へ昇格（GT 画像は ignore 継続）
- quality_gate / visual_review のゾーン組み立てを fixtures 再利用にして全フィールド明示化
- 既知 fail の KNOWN_BROKEN + strict xfail 方式への統一

### 低

- ProcessPixelsArray の per-zone コンテキスト化（golden 17 件がある今が着手適期）
- `_host._maskView` 結合 8 箇所の解消（マスクペイント入力を MaskPaintView へ移す）
- 死にコード削除（`_windowSerializedObject` 一式、`edgeStopThreshold`）
- ヘルパー段への CancellationToken 伝搬（:383-386 のコメント修正込み）
- tooltip 抜け 6 箇所、自動調整進捗の刻み、マスク Sync プロトコルのヘルパー化
- .meta コミット、zip の unitypackage 同梱廃止、orphan ゾーンマスクの prune

---

## 5. 結論

前回レビューの結論「価値の中心（再着色アルゴリズム）を最も丁寧に設計し、周辺に負債を寄せている」は依然正しいが、本レビューで「周辺」の実態がより深刻であることが判明した。**コードの負債は管理されているが、運用の負債（リリース・強制機構・データ生存性）は管理外で腐敗が進行していた**。特に「視覚レビュー必須」「全テストパスでのみコミット」という自己規律の前提となるインフラが停止していた事実は、規律をドキュメントではなく**機械（フック・CI・追跡ファイル）で強制する**方向への転換が必要であることを示している。

*本レビューは 5 観点の独立レビューの統合。各指摘はシンボル名＋執筆時点の行番号で追跡可能。*
