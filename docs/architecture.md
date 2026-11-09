# コードの形（層・依存・メモリの持ち主）

いろかのコードの層と依存の向き、メモリの持ち主、寿命の決まりの**正**。計画（`docs/plan-0.4.0.md`）は作り替えの
進め方で、ここは作り替えたあとも残る決まりそのもの。テストの構成は `docs/testing-architecture.md`、アルゴリズムの
定数は `docs/algorithm-constants.md` が正。

決まりを変えるときは、このファイルと検査を同じコミットで直す。

## 1. 層と依存の向き

```
UI           IrocaWindow・各ビュー・インスペクタ・プレビューの描画（表示と入力だけ）
  ↓
Automation   IrocaAutomation（MCP・コマンドラインの窓口。JSON を下の層へつなぐだけ）
  ↓
Application  編集中の文書・色替えの入力の組み立て・プレビューの計算・自動調整・書き出し・登録（操作の流れ。IMGUI を知らない）
  ↓
Infra / Text Unity とファイルとの境目（TextureIO・各 Store・ジョブ・LivePreview・NDMF 用のビルダー・設定のキー）と文言
  ↓
Core         計算だけ（選択・マスクの適用・再着色・自動調整・UV の部位・ヒープの外のメモリ）。UnityEditor も文言も知らない
```

上の層は下の層を使ってよい。**下の層は上の層を知らない。** 上の層へ知らせたいときは、下の層にイベントや
インターフェースを置き、上の層がそれを購読・実装する。

| フォルダ | 層 | 使ってよい層（自分を含む） |
|---|---|---|
| `Code/Core` | Core | Core |
| `Code/MaskSuggest/Ops` | Core（AI 提案の後処理の計算） | Core |
| `Code/Runtime` | 実行時（アバターに付けるコンポーネント） | Runtime |
| `Code/Infra`・`Code/Text` | Infra | Core・Runtime・Infra・Text |
| `Code/MaskSuggest`（Ops を除く） | Infra と同じ段（AI 提案の準備・窓口。待ち行列の持ち主 `AiSuggestBroker` もここ） | 上に MaskSuggest を足したもの |
| `Code/Application`（`SourcePixelCache`・`EditingSession`・`IrocaEditingDocument`・`SessionPersistence`・`MaskWorkspace`・`MaskRegionEdit`・`ProposalMaskRegion`・`MaskHistory`・`AutosaveSchedule`・`PresetApplier`・`RegistrationService`・`ExportService`・`AutoTuneService`・`PreviewPipeline`・`EditingWindows`） | Application | 上に Application を足したもの |
| `Code/Automation` | Automation | 上に Automation を足したもの |
| `Code/UI` | UI | 上に UI を足したもの |
| `Code/NdmfIntegration`・`Code/SentisIntegration` | 統合 | Application まで（UI と自動化は使わない） |
| `Code/McpIntegration` | 統合 | Automation まで（UI は使わない） |
| `Code/Debug` | 統合（デバッグ表示） | UI まで |

- 統合（NDMF・Sentis・MCP・Debug）は別の asmdef で、任意のパッケージが入ったときだけコンパイルされる。本体から
  統合を呼ぶことはできない（asmdef が参照しないので、コンパイルが止める）。
- 自動化がウィンドウを開く・表示中の編集を読むときは `EditingWindows.Current`（`IEditingWindows`。実体は UI が
  起動時に登録する）を通す。自動化は `IrocaWindow` の型を知らない。
- 名前空間は `Iroca` のまま変えない（変えても何も縛らず、ハーネス・テスト・Debug の書き換えが増えるだけ）。縛るのは
  フォルダと検査と asmdef。
- 守り方: `scripts/source_checks/test_layer_dependencies.py` が上の表と同じ許可表を持ち、フォルダごとに宣言した型の名前を
  ほかのフォルダのコードが書いていないかを見る。`test_core_no_editor_dependency.py` は Core が UnityEditor を参照しない
  ことを見る。Core・Services・本体の間の向きは asmdef でコンパイルが止める: `Editor.Core`（Core・MaskSuggest/Ops）・
  `Editor.Services`（Infra・Application・Text・MaskSuggest）・`Editor`（UI・Automation）。`scripts/build-check` の csproj も同じ境目で、
  層とアセンブリの対応は同じ検査の `ASSEMBLY_OF_LAYER` が持つ。フォルダを足すときは asmref も足す（忘れると本体の asmdef に吸われて境目が消える）。

## 2. Core

Core は再着色のアルゴリズムと、それを支えるデータと計算の置き場。**製品の C# がアルゴリズムの唯一の正**で、
headless ハーネス（`scripts/headless-run/Harness.csproj`、.NET 8）は Core のファイルを `UnityEngine.CoreModule` だけを
参照してそのままコンパイルし、Unity なしで出力を測る。そのため:

- UnityEditor・文言（`Localization`）・ウィンドウを参照しない。
- UnityEngine は CoreModule の型（`Color32`・`Mathf`・`Vector*` など）だけを使う。`NativeArray` はハーネスに Unity の
  実行環境が無いので使えない（ヒープの外のメモリは `IrocaNativeMemory` が `Marshal.AllocHGlobal` で取る）。
- 作り替え（移す・分ける・名前を変える）で出力を変えない。golden・abcheck（全モードのバイト比較）・Unity の
  `RuntimeParityTests` で一致を示す（`docs/testing-architecture.md`）。

### 2.1 フォルダ

```
Code/Core/
  Foundation/  並列度・画素の決まり・使っていないメモリを返す窓口・ヒストグラム・ビット数
  Color/       色空間（OkLab）・画素の HSV・色の表
  Pipeline/    PixelProcessor（入口）・ゾーン 1 つ分の流れ・RunContext / RunBuffers / ZoneState / ZoneBuffers・
               SelectionResult（選択と再着色の境目）・強度の範囲
  Selection/   Legacy（今の選択の段。`ColorZone` の照合・選択キャッシュの鍵）
  Masks/       除外・含めるの適用（製品の契約）
  Recolor/     再着色の段（無彩・デコンタミ・アンカーと明るさ・混色帯・カーネル・合成）
  Filters/     ぼかし
  （直下）     ヒープの外のメモリの部品・ColorZone・セッションとプリセット・マスクの形と符号化・自動調整（ZoneAutoTuner）・
               UV の部位（UvChartMap）・座標変換と RecolorPreview
```

選択の再設計（T5。領域・素材の模型・尤度比の 3 層）は 0.4.0 から外した。枝 `dev-0.4.0-t5` に残っていて、再開するときの
手がかりは dev_safe の `docs/t5_lessons.md`。下の `Selection/Legacy` の名前はそのときの名残りで、今の選択そのもの。

各フォルダの中身: `Selection/Legacy/`（旧の選択の段・`ColorZone` の照合・選択キャッシュの鍵）、
`Masks/`（マスクの契約と除外・含めるの判定）、`Filters/`（ぼかし）、`Foundation/`（並列度・画素の決まり・使っていない
メモリを返す窓口・ヒストグラムの集計と分位点 `Histogram`・ビット数 `Bits`。`PixelProcessor` の並列度などの同名のものは
転送役）、`Color/`（色空間の `OkLab`。自動化の手がかりも同じ変換を使う。画素の HSV と色の表は `PixelProcessor` の
partial のまま置いた）、`Pipeline/`（入口の `PixelProcessor.cs`・ゾーン 1 つ分の流れ `RunZone`・`RunContext` /
`RunBuffers` / `ZoneState` / `ZoneBuffers` / `SelectionResult`・強度の範囲）、`Recolor/`（再着色の段 6 つ
`AchromaSolidifyStage` → `DecontamStage` → `RecolorStatsStage` → `MixtureBandStage` → `ComposeStage` →
`AchromaFringeStage` と、その下回り・再着色の核・デバッグの分岐の観察者）。段は `PixelProcessor` の入れ子の static
クラスで、`Run(RunContext, ZoneState)` を持つ（入れ子にしたのは、移した本体が `PixelProcessor` の private の下回りを
そのまま呼べるようにするため）。強度の範囲（`PixelProcessor.Bounds.cs`）は再着色の段も使うので `Pipeline/` に置いた。

### 2.2 段の書き方

- パイプラインは `PixelProcessor.Pipeline.cs` が段を順に呼ぶだけにする（その 1 ファイルでパイプラインの仕様として読める）。
  段は `DecontamStage.Run(RunContext r, ZoneState z)` のような static メソッド。段の順と条件分岐は出力のビット一致に
  効くので、移すときに変えない。
- **選択と再着色の境目は `SelectionResult`。** 再着色が選択から読むもの（強度。内部は 1、縁は被覆率。混色帯の判定に
  要るもの）だけを持つ。
- 時間計測とデバッグ記録は、段の境目で呼ぶ観察者にする。既定は何もしない。
- 画素を回すループの中は、生の `T*` のローカル変数のまま書く。インターフェース・仮想呼び出し・`Span`・構造体の
  インデクサは使わない（Mono の Debug 最適化で 2〜4 倍遅かった。KernelBench）。段の入口で引数の構造体をローカルへ
  展開する。
- 定数は `docs/algorithm-constants.md` の台帳に種類と根拠を書く（`test_algorithm_constants.py` が Core と
  `MaskSuggest/Ops` のサブフォルダまで見る）。

## 3. エディタ側

| 部品 | 役割 |
|---|---|
| `EditingSession` | 編集中の文書。文書の中身（`IrocaEditingDocument`）を包み、原本の画素（`SourcePixelCache`）・編集中のマスク（`MaskWorkspace`）・マスクの履歴（`MaskHistory`）の持ち主、ゾーンの操作（追加・削除・並べ替え・スポイト・起点・調整のリセット）、編集の通知 `Changed`（`Content`・`Masks`・`Replaced`。ウィンドウがプレビューと重ね表示へ回し、自動保存の予定 `AutosaveSchedule` は直接購読する）。保存に渡す編集状態は `ToSaveState()` が作る。新しいゾーンの規則（名前の番号・初期の許容範囲）は自動化もここを使う |
| `IrocaEditingDocument` | 文書の中身（ゾーン・設定・テクスチャ・レシピ・マスクの版の番号）。`HideAndDontSave` の ScriptableObject で、**Undo の対象はこれ**（ウィンドウではないので、ズーム・開閉などの表示の状態は巻き戻らない）。ウィンドウの `sourceTexture`・`_session`・`_boundRecipe` は保存の控えで、保存の直前に文書を指し直すだけ（6 章。`test_window_document_mirror`） |
| `MaskWorkspace` | 編集中のマスク（1 画素 1 ビットのヒープの外のマスク）。層は参照の数で共有し（重ね表示のワーカー・処理へ渡すスナップショット `MaskSnapshot`・選択キャッシュ）、書く前に写す。保存形式との変換・スナップショットは bool[] の経路と同じ結果。持ち主は `EditingSession`（4-2 の 7）で、ブラシ・範囲の操作はマスクの編集（`MaskPaintView`）が書く |
| `MaskHistory` | マスクの履歴。Undo に載るのは文書の版の番号だけで、中身はここがヒープの外に RLE で持つ。マスクを書き換える操作は `EditingSession.RecordMaskEdit` → 書き換え → `CommitMaskEdit` の順にする（記録を飛ばすと Undo で戻らない）。変わっていない層は前の版と共有する。再読み込みで Undo の記録が残るかは Unity の版で違う（2022.3 は捨て、Unity 6 は残す）ので、履歴はファイルへ退避して読み戻す（`UndoReloadScenario` を両方の版で回す） |
| `SessionPersistence` | 復元の優先順（レシピ → セッションのファイル → 既定）・自動保存・外での書き換えの検出。今（4-2 の 5）は読み書きと判断だけで、編集状態の差し替え・Undo・マスクの同期はウィンドウ。自動保存の予定は `AutosaveSchedule`（4-2 の 8） |
| `EditingSessionRegistry` | 開いている編集の一覧。ウィンドウを型で探す（`FindObjectsOfTypeAll<IrocaWindow>`）代わり。Infra の非破壊ビルドと更新の確認も使うので Infra に置いた（4-2） |
| `EditingWindows` | 自動化がウィンドウを開く・表示中の編集を読む・読み直させる窓口（`IEditingWindows`）。実体は UI が登録する |
| `SourcePixelCache` | 作業解像度の原本（参照の数で貸す） |
| `RecolorInput` | 色替えの入力を組み立てる唯一の場所。プレビュー・書き出し・一括書き出し・NDMF・自動化が共通で使う。非破壊ビルドの土台と `SessionRecolor` も使うので Core に置いた（4-2） |
| `PreviewPipeline` / `PreviewRenderer` | プレビューの計算（概要段・確定段・ドラッグの追従・選択キャッシュ・拡大表示の土台。`Code/Application/`）と、表示（テクスチャ・拡大表示のタイル・差分のシェーダー・「アバターの色」・画像の矩形への描画。`Code/UI/`）。4-4 で分けた。レイアウト・ズーム・入力は `PreviewView` |
| `AutoTuneService`・`AiSuggestBroker`・`ExportService`・`RegistrationService`・`PresetApplier` | 自動調整・AI 提案の待ち行列・書き出し・登録・プリセットの読み込みを、それぞれ 1 本にする。4-3 で `PresetApplier`（プリセット → 編集状態の規則）と `RegistrationService`（使うレシピの選び方と登録の計画。ウィンドウは確認画面に、自動化は警告にする）、`ExportService`（書き出しの画素と PNG をヒープの外に置く）、`AiSuggestBroker`（サービスを購読する唯一の所。求めた順に受け取り先へ返す。右クリックのコントローラも使うので `Code/MaskSuggest` に置いた）、`AutoTuneService`（1 つのゾーンの証拠の待ち・解析・当て方。ウィンドウは確認・通知・Undo、自動化は警告）を作った |

- IMGUI のビューは毎フレーム状態を読む（MVVM にはしない）。`Changed` を購読するのは、非同期に動くもの（プレビューの
  再計算・自動保存・UI の版・シーンへの反映・マスクの重ね表示）だけ。
- 登録は「計画」を返し、ウィンドウは確認画面に、自動化は警告にする（同じ判断を 2 か所に書かない）。
- Undo が書き戻すのは文書だけで、文書の外の状態は戻らない。テクスチャが戻ったときに合わせるもの（読めなかった印・書き出しの既定の名前・AI 提案の要求・
  履歴に無い版のマスク）は `IrocaWindow.OnUndoRedoPerformed` が受け持つ。登録・自動化が新しく作ったレシピとの結び付きは編集内容ではないので、同じテクスチャの Undo では外さない
  （「いろかで開く」と、結び付いていない窓からほかのレシピを流用した登録は、その記録まで戻すと結び付きも戻る。流用した登録はレシピの中身も同じ記録で戻す）。
  走っている自動調整は、Undo・テクスチャの切り替え・原本の読み直しで止める（押したときの状態がもう無いので、結果を当てない）。

### 3.1 プレビューの表示

- 差分（変更前と変更後の画素ごとの差）は、描いている 2 枚のテクスチャからシェーダーで同じ描画の中で作る。別の
  ジョブや差分のテクスチャを持たない（持つと画像と差分の世代がずれる）。0.4.0 の 1-2 で `PreviewDiffDrawer` と
  `Code/UI/PreviewDiff.shader`（`Hidden/Iroca/PreviewDiff`）にした。
- エディタで使うシェーダーは `Code/` の下に置き、`Shader.Find` で引く（配布の zip は `Code/` の追跡ファイルを全部入れる）。
  IMGUI の中で描くシェーダーは、スクロールビューの切り抜き（`_GUIClipTexture`・`unity_GUIClipTextureMatrix`）を
  Unity の GUI のシェーダーと同じように受け取る。受け取らないと枠の外へはみ出す。
- 拡大表示は CPU でタイルに切り出す（`Code/UI/DetailTiles.cs`）。格子は倍率と土台の寸法だけで決まり、縮めたタイルは
  ヒープの外の控えに覚えて、パンでは新しく見えたタイルだけを作る（周り 1 枚分は先読みする）。範囲はそのフレームの
  スクロール位置から決める。テクスチャは見えている範囲のタイルを並べた 1 枚にし（差分・「元を表示」・見た目・
  マスクの重ね表示が同じ範囲と寸法で作れる）、並びが変わらなければ作り直さない。`LoadRawTextureData(IntPtr)` で上げる。
- プレビューの計算（概要段・確定段・ドラッグの追従・選択キャッシュ・拡大表示の土台）は `PreviewPipeline`
  （`Code/Application/`）。表示は計算の結果（`PreviewFrame`。ヒープの外）を受け取ってテクスチャへ上げる。
- 拡大表示の解像度（作業解像度で見るか、インポート設定の大きさに縮めて見るか）は表示だけの設定で、出力
  （NDMF・書き出し・自動化）には効かない。

## 4. メモリの持ち主

元ファイルを読むのは `TextureIO.DecodeSourceFile` だけ。PNG はヒープの外へ直接デコードする（`PngFileDecoder`。Unity の `LoadImage` は
管理配列しか受け取らない。結果は `LoadImage` と同じ画素で、扱わない形式と壊れたファイルは `LoadImage` へ回す）。JPG は `LoadImage`
（ファイルの大きさの管理配列が残る）。PNG・JPG でなければファイルの中身を読まない。取り込み済みテクスチャの圧縮形式は GPU で展開する
（`WorkingResolution.NativePixelsOf`。DXT は `GetPixels32` と最大 1 段違う。GPU が無いときと対象外の形式は `GetPixels32`）。

PNG は別スレッドで読む（`TextureIO.TryDecodePngOffMainThread`。プレビューは `SourcePixelCache.LoadInBackground`、書き出しと
シーンのプレビューはジョブの中）。描くたびに呼ぶ所は `PreviewPipeline.PollSource` で、待たずに状態だけを受け取る。今すぐ画素が要る操作
（自動調整・AI 提案）の `EnsureSource` は、読み終わるのを待って同じ読み込みを受け取る。読む途中で要らなくなったら、デコーダが行ごとに
取り消しを見て読みかけの画素を手放す（閉じる前に `WaitForCancelledLoads` で抜けるのを待つ）。読み込みは 1 本ずつなので、読みかけが
重ならない。JPG と GPU の展開はメインスレッド専用なので、読み込みの間だけ止まる（4K で約 100ms）。
`PngFileDecoder` は 1 枚の中でも、zlib の展開を自分で立てたスレッドで進め、呼んだスレッドがフィルタ戻しをする（約 64KB の束を
4 つ回す。スレッドプールは使わないので、プールが埋まっていても待たない）。返る前に展開のスレッドが抜けるのを待つ。

**画素数に比例する大きさのバッファは、Mono のヒープに置かない。** Unity の Mono のヒープは一度広がると OS へ返らず、
GC は大きな塊を動かせない。8K の原本は 6,700 万画素あり、`bool[]` 1 本で 67MB、`Color32[]` 1 本で 268MB が、
1 度使っただけでエディタを閉じるまで常駐する。

自前の実装と Unity の実装を並べた実測（2026-11-09、Unity 2022.3 の中、1 ケース 1 プロセス）:

| 部品 | 自前 / Unity の実装 | 自前にした理由 |
|---|---|---|
| PNG の読み込み（`PngFileDecoder` / `LoadImage`） | 4K で 155 / 298ms、私用メモリの山 183 / 338MB。8K で 420 / 645ms・342 / 757MB。全画素一致 | メモリ（LoadImage は開くたびにファイルの大きさの配列を Mono のヒープに作る）。速さは結果として上回った |
| 画素のバッファ（`NativePixels` / `Color32[]`・`NativeArray`） | 速さは 3 つとも同じ。10 回使って広がる Mono のヒープは管理配列が 4K で 480MB・8K で 1.4GB、ほかの 2 つは雑音の内 | メモリ。`NativeArray` との差は無く、使わないのはハーネス（.NET 8）で動かないため |
| sRGB の換算（`ColorTintLut` / `Mathf`） | 1 呼び出し 53 / 40ns。byte の表 256 段は全部一致 | Unity なしのテスト。表を作るときにしか呼ばないので、差は体感に出ない |
| クリック位置（`MeshRaycast` / `MeshCollider` + `Physics.Raycast`） | 7 万三角形で 1 クリック 4.1ms / 焼き直し + 1 本 16ms（形が動かなければ焼いた後の 1 本は 0.001ms） | 機能（コライダーの無いアバター）。ポーズのたびに焼き直す条件では自前の方が速い |

測り方と全表は dev_safe の `docs/builtin_vs_custom_bench_2026-11-09/`（`run_unity.ps1` で測り直せる）。

- 画素数の大きさの配列を管理配列で作らない。`test_no_pixel_sized_managed_arrays.py` が `new T[w * h]` の形と Unity の
  API が返す画素の管理配列（`GetPixels32()` など）を数え、一覧（`scripts/source_checks/pixel_sized_managed_arrays.json`）
  から増えたら落とす。一覧に残るのはテスト・ハーネス・デバッグの口と Unity の API の都合だけで、ファイルごとに理由を
  書く（`reasons`）。製品の通常の経路には残っていない（0.4.0 の T3）。解像度に依らない大きさ（粗いグリッド・表示の
  大きさ）は検査の `NOT_PIXEL_SIZED` に理由つきで載せる。
- ヒープの外の部品（今）: 確保の窓口 `IrocaNativeMemory.Alloc(bytes, 用途)` / `Free`（用途 `NativeUsage` ごとに今の量・
  最大値・ファイナライザで解放した数を数える）、持ち主の型 `NativeBuffer`、作業配列のプール `NativeWorkPool<T>`
  （`Rent` / `Return`。中身は前の持ち主の値が残るので、借りた側が書いてから読む）、原本と出力の `NativePixels`、
  色の表、選択キャッシュの `NativeFloatBuffer`。どれも中身は `NativeBuffer` で持つ（0.4.0 の 2-2）。
- 作業配列のプールは、手元に持つ量（貸し出し中 + 空き）を直近の貸し出し中の最大量の 1.5 倍までにする（新しい塊を作る
  ときに、空きを大きい順に手放す）。`WorkPools` の 5 つのプール（float・bool・byte・Color32・int）は、空きの塊の置き場
  `NativeBlockStore` を共有し、区分はバイト数で切る。int のプールが返した 64MB の塊を float のプールがそのまま借りられ、
  1.5 倍の上限も 5 つの合計で数える（2026-11-08。型ごとに別だった頃は、4096² の 1 回の処理の山で int の空きが 130MB
  残ったまま float の塊を新しく作っていた）。中身は不定のままなので、借りた側は前の持ち主が別の型でも書いてから読む。返すのは番地（`Return(T*)`）のまま。返したものをもう一度返す誤りは、検証用の
  `IROCA_POOL_POISON=1` で見つける（返した塊を寝かせ、同じ番地をすぐ別の借り手へ貸さない）。借りた側が札を持つ形は、
  画素の処理を段に分けるとき（3-7b の `ZoneBuffers`）に、段の持ち物として入れる。
- 手を止めたら返す: 計算（`NativeMemoryLifecycle.WorkStarted` 〜 `WorkEnded`。作業配列を借りる処理は必ずこの間で走らせる）が
  10 秒のあいだ無ければ、作業配列のプールの空きを返す（色の表と選択キャッシュは残す）。窓が無い自動化は、依頼の終わりに全部返す。
  ウィンドウを閉じたとき・リロードの前は全部返し、持ち主が
  返し忘れてファイナライザが解放した塊があれば警告する。開発者向けの設定を表示しているときは、閉じた後に残っている
  量も用途ごとにログへ出す。
- `NativeBuffer` の決まり（新しくヒープの外に持つものは、この上に作る）:
  - 持ち方は参照の数。作った側が 1 つ目を持ち、ほかの持ち主・別スレッドの読み手は `AddRef` して持ち、使い終わったら
    `Release` する。返しすぎ・解放後の `AddRef` はいつも投げる。
  - 中断中（ドメインのアンロード）の最後の `Release` は解放せず、アンロードまで持つ（並列で使っている途中のスレッドが
    残っていることがある）。持ち主の側では中断を見ない。
  - 作業配列のプールの `Return` も中断を見て、中断中は貸し出し中のまま残す（同じ理由。解放はアンロードの後の
    ファイナライザ）。借りた側（`WorkScratch`・`finally` で返す所）は中断を見ずに返す。中断を見てよいのは
    `IrocaNativeMemory`（定義）・`NativeBuffer`・`NativeWorkPool.Return` だけ
    （`scripts/source_checks/test_current_thread_aborting_owners.py`）。
  - 返し忘れはファイナライザ（`SafeHandle`）が解放し、用途つきで数える（`IrocaNativeMemory.FinalizedCount`。
    `--nativememselftest` が 0 であることを確かめる）。
- マスクとビット列は `NativeBitMask`（1 画素 1 ビット、参照の数で持ち、書くときだけ写す）。
- 持ち主の範囲を決めて書く: 実行全体（`RunBuffers`）・ゾーンごと（`ZoneBuffers`。8K の最大量を増やさないよう、使い
  終わった所で早めに手放す）・段の中の一時（`WorkScratch`）。借りたものは同じ範囲の `finally` で返す。
- **Unity との境目は Infra だけ。** `NativeArray` に見せる薄い変換（`TextureIO.NativeView<T>`）と、`TextureIO`（原本を読んで
  `NativePixels` にする・PNG にしてファイルへ流す。`Code/Infra/TextureIO.cs`）。Unity 2022.3 では `LoadImage`・`SetPixels32`・`EncodeToPNG` が管理配列しか受け取らない
  ので、`GetPixelData<T>`・`SetPixelData`・`LoadRawTextureData(IntPtr)`・`ImageConversion.EncodeNativeArrayToPNG` を使う。
- 検証: 貸し借りの順が変わる変更は `IROCA_POOL_POISON=1`（貸す塊を 0xFF で埋め、書く前に読む所を出力の違いで
  見つける。返した塊は 0xDD で埋めて寝かせ、返したあとに読む所と二重の返却も見つける）と `IROCA_NATIVE_GUARD=1`
  （ガードページではみ出しを捕まえる。Windows）でも回す。

## 5. 寿命とイベント

- 発行側が static（ドメインと同じだけ生きる）のイベントを購読したら、同じファイルで解除する。解除しないと、捨てた
  購読者がイベント経由で生き残り、再オープン後に新旧の購読者が並んで旧い方が先に処理する。Unity の破棄済みの
  オブジェクトは `?.` をすり抜けるので、null 条件演算子では防げない。
- 書き方: `OnEnable` で `-=` してから `+=`（二重にならない）、`OnDisable` で `-=`。
- 対象のイベントの一覧と、解除が要らない例外（理由つき）は `test_event_subscription_symmetry.py` が持つ。新しく static の
  イベントを作ったら一覧に足す。

## 6. 互換の約束（変えると利用者の状態が壊れるもの）

次の名前は、移すときも変えない。変えるなら移行を書いて、テストで古い版の読み込みを確かめる。保存されるフィールドの
名前とキーは、ソースの検査が今の一覧と比べる（7 章）。

- ウィンドウとビューの保存されるフィールド名（`sourceTexture`・`_session`・`_boundRecipe`・各ビューとその public
  フィールド）。名前を変えたり別の入れ物に包んだりすると作業状態が消え、最悪、空のセッションで結びついたレシピを
  上書きする。変えるなら `[FormerlySerializedAs]` を付ける。
- JSON の項目名（`IrocaSessionState`・`ColorZone`・`MaskState`・プリセット・レシピ・zones JSON）。
- EditorPrefs / SessionState のキー。
- 自動化 API（`IrocaAutomation`・`-executeMethod` の名前・MCP のツール名と JSON の形）。
- `Code/Runtime/*.meta` の GUID。
- ウィンドウの型とアセンブリの名前（利用者のレイアウトに保存される）。
- ハーネスのコマンドラインと出力の行（dev_safe の検証が読む。一覧は dev_safe の `docs/harness-contract-2026-10-27.md`）。

## 7. 機械の検査

| 検査 | 守るもの |
|---|---|
| `scripts/source_checks/test_layer_dependencies.py` | 1 章の依存の向き（許可表。違反は 0 で、例外の一覧は持たない） |
| `scripts/source_checks/test_core_no_editor_dependency.py` | Core が UnityEditor を参照しない |
| `scripts/source_checks/test_no_pixel_sized_managed_arrays.py` | 4 章の、画素数の大きさの管理配列を増やさない |
| `scripts/source_checks/test_event_subscription_symmetry.py` | 5 章の、static のイベントの購読と解除の対 |
| `scripts/source_checks/test_serialized_field_names.py` | 6 章の、Unity が保存するフィールドの名前（一覧は `serialized_field_names.json`） |
| `scripts/source_checks/test_window_document_mirror.py` | ウィンドウのコードが保存の控え（`sourceTexture`・`_session`・`_boundRecipe`）を読み書きしない（編集状態は文書から読む） |
| `scripts/source_checks/test_editor_prefs_keys.py` | 6 章の、EditorPrefs / SessionState のキー |
| `scripts/source_checks/test_algorithm_constants.py` | 定数の台帳と、直書きの小数を増やさない |
| `scripts/source_checks/test_harness_scope.py` | 視覚レビューのゲートの範囲が、ハーネスのコンパイルする製品ソースと一致する |
| `scripts/source_checks/test_current_thread_aborting_owners.py` | 4 章の、中断中かを見るのはヒープの外のメモリの持ち主（`NativeBuffer`・プールの `Return`）だけ |
| golden・abcheck・`RuntimeParityTests` | 作り替えで出力が変わらない（`docs/testing-architecture.md`） |

`scripts/source_checks` は C# のソースを読むだけで、ハーネスも Unity も要らない。CI（`.github/workflows/ci.yml`）で
毎回回る。例外の一覧を持つ検査は、直したら同じコミットで減らす（減ったのに一覧がそのままだと落ちる）。
