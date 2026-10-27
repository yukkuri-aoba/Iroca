# EditMode テスト（Unity 実機）

headless ハーネスと golden は net8 で製品 C# を動かすので、Unity 固有の部分は検証できない。
ここは Unity Test Framework の EditMode テストで、その部分を実機（batchmode）で検査する。

| ファイル | 検査すること |
|---|---|
| `PersistenceTests.cs` | セッション・マスク・プリセットの保存と復元。読めないファイルを空保存で消さない、GUID が一時的に引けないだけのファイルを消さない（.orphan 退避と復元） |
| `WindowRestoreTests.cs` | ウィンドウが状態を失わない・古い状態に戻らない。プレビューの一時テクスチャがシーンを開いても破棄されない、Unity が落ちた後の起動ではレイアウトの古い編集内容でなく自動保存を読む（ドメインリロードではメモリ上の内容を使う） |
| `SampleUvUndoTests.cs` | スポイト位置（`ColorZone.sampleUV`）が Undo/Redo・ゾーン削除の Undo で色と対で戻る、プリセットには載らずセッションには残る（Undo の書き戻しは Unity のシリアライザなので実機で検査。対象は `SampleUvUndoHost.cs`） |
| `UndoRestoreSignalTests.cs` | ウィンドウが「関係ない Undo では作り直さない」ための前提。自分の Undo/Redo では `undoRedoPerformed` より前に `OnAfterDeserialize` が来て、別のオブジェクトの Undo/Redo では来ない（対象は `UndoRestoreSignalHost.cs`） |
| `PreviewDownsampleTests.cs` | 拡大表示のクロップをフル解像度の配列から直接縮小する `BoxDownsampleRegion` が、切り出してから `BoxDownsample` したのとビット単位で同じになる |
| `UvChartMapPaddingTests.cs` | メッシュ右クリックの UV チャート地図が、半径の外を落とした持ち主だけを持つ形(`AssignPaddingBaked`)でも、`AssignPadding` してから同じ半径で引いたのとどの画素・マスクも同じになる |
| `ExportPipelineTests.cs` | 書き出しの手順（原本の読み込み・PNG 化・書き込み・import 設定の引き継ぎ・出力先の決定）。単体書き出しと一括書き出しが共有する `ExportPipeline` を直接呼ぶ |
| `MeshUvLocatorTests.cs` | 編集中のテクスチャを使うメッシュの探索（シーン → 同じ素材フォルダの Prefab、書き出し名 `_recolored` の追跡、手動指定、Read/Write 無効のメッシュで落ちない） |
| `MeshPartCacheTests.cs` | メッシュの探索結果を捨てて探し直す条件（使っていた Renderer が消えた・自動の結果でシーンが替わった。手動の指定は残っている限りそのまま）。シーンを替えて同じテクスチャを開くと「メッシュ」欄が None になっていた不具合の再発防止 |
| `MeshIslandOutlineTests.cs` | プレビューに重ねる UV の島の輪郭（内側の対角線を描かない、UV を重ねた左右対称のパーツでも輪郭が残る、Tiling / Offset） |
| `SceneMeshPickerTests.cs` | Scene でクリックした場所を求める（`SceneMeshPicker`）とプレビューに重ねる島の組み立て。手前の Renderer・UV、マテリアルのカリング、非表示のものを除く、スキンメッシュはポーズ込みの形。テスト用の物は開いているシーンの遠く（座標 1000）に置いて片付ける |
| `MaterialAppearanceTests.cs` | 「アバターの色」表示。マテリアルの色の設定の読み取り（標準のメインカラー、lilToon のメインカラー・色調補正・グラデーション・2nd / 3rd と UV0 以外の層、2nd のテクスチャとして開いたとき、シーンからの探索）と、それをプレビューに掛ける描画。lilToon の焼き込みシェーダーで描いたメインカラーが自前の掛け算の表と一致する（色空間の扱いが同じ）、拡大表示の切り出しが重ね描きの層（タイル・デカール）の位置まで全体を描いたものと一致する。lilToon が無いホストでは lilToon の段、`-Graphics` でないときは GPU の段を Ignore |
| `NonDestructiveTests.cs` / `NdmfProcessAvatarTests.cs` | 非破壊の色替え（レシピ → テクスチャ → マテリアル差し替え）。後者は NDMF を通した結合で、NDMF があるホストでだけコンパイルされる |
| `WorkingResolutionTests.cs` | 編集の作業解像度（`WorkingResolution`）。辺が 8192 以下なら元ファイルのまま、超えるときだけ取り込み後の寸法へ縮める。拡大表示に映す寸法は取り込み後の寸法。書き出しは常に元の解像度。縮小をテクスチャの生データから帯ごとに読む経路が、Color32[] の経路と同じ画素になる（RGBA32・ARGB32・BGRA32・RGB24）|
| `NdmfSupportTests.cs` | NDMF が使えないときの扱い。asmdef の版の範囲（2.0 のベータ版も外す）を Unity がそのとおりに読んでいるか（入っているパッケージの版から各 asmdef の define を照らす）。NDMF が入っていない・古い・新しすぎるときに、色替えするものがあれば VRChat へのアップロードを止めて直し方を案内するか。VRChat SDK があれば「Iroca Recolor」が NDMF と関係なく `IEditorOnly` で、アップロードの確認が NDMF より先に走り、NDMF が使えるときは素通しするか |
| `OptionalPackageRangeTests.cs` | 任意の依存（AI の Sentis・Inference Engine、MCP for Unity）の asmdef の版の範囲（上限は次の版のベータ版も外す `-0`）を Unity がそのとおりに読んでいるか。入っているパッケージの版から、連携部分がコンパイルされるはずかを出して照らす |
| `RecipeRegistrationTests.cs` | 登録で `Iroca Recolor` が増えない（同じオブジェクトは 1 つにまとめる・同じテクスチャは置き換え・Undo）、旧保存形式の読み込み |
| `RecipeSyncTests.cs` | 外（Git の pull・バックアップからの復元など）で書き換わったレシピを、いろかの窓が古い内容で上書きせず読み直す。窓自身の保存・同じ中身での取り込み直し・Undo でレシピが前の中身へ戻ったときは、外からの変更と取り違えない |
| `LivePreviewTests.cs` / `NdmfPreviewFilterTests.cs` | シーンでのプレビュー（どこに何を映すかの規則、ウィンドウの結果を RenderTexture に書く受け渡し口、レシピの出来上がりの貸し借り、NDMF のフィルタの呼び出し口）。後者は NDMF があるホストでだけコンパイルされる |
| `RuntimeParityTests.cs` | 製品の実行環境（Unity の Mono）とテストの実行環境（ハーネスの net8）で再着色の出力が一致するか。golden の入力（`scripts/golden/cases/`）を製品経路（`IrocaAutomation.RecolorWithZones`）に通し、ハーネスの出力（`scripts/golden/expected/`）と比べる |
| `MaskSuggestInferenceTests.cs` | AI マスク提案の実推論（パッケージの読み込み・モデルの変換とロード・推論）が Unity 2022.3（Sentis）と Unity 6（Inference Engine）で通るか。灰地の赤い円の中心をクリックし、円と提案マスクの IoU を測る。パッケージ未導入・モデル未配置なら Ignore。`IROCA_AI_REPORT` にパスがあれば結果を 1 行追記する。いろかのウィンドウを閉じたとき（推論のワーカーを手放す）の後も、作り直して同じように取れるか。モデルの変換で Assets に残った一時フォルダは、いろかのものだけを片付ける |
| `MaskSuggestModelFilesTests.cs` | AI モデルのファイルの扱い（モデル置き場は全プロジェクト共有）。ほかの Sentis 版の変換済みキャッシュは長く使われていないものだけ消す、保存中に落ちて残った一時ファイルだけ消す、別の Unity が読んでいる最中のキャッシュも変換し直さずに読める。ダウンロードの途中で落ちた Unity の一時ファイルだけ消す（別の Unity がダウンロード中のものは消さない）。ウィンドウを描くときはモデルのファイルがあるかを見るだけで読み込まず、それでも準備済みとして扱う。Sentis の導入前に知らせる「.onnx の取り込みがぶつかるパッケージ」に、差し替えになる同じ名前の Sentis を数えない |
| `PerfBenchTests.cs` | 製品の実行環境（Unity の Mono）での再着色の速さ（`Perf` カテゴリ。品質は見ない）。`IROCA_PERF_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -PerfCases <json>` のときだけ回る（結果は出力フォルダの `perf.jsonl`） |
| `MemoryBenchTests.cs` | 製品の実行環境（Unity の Mono）でプレビュー相当の処理を繰り返したときの Mono ヒープとプロセスのメモリ（`Memory` カテゴリ）。Mono のヒープは縮まないので、見た目のメモリを決めるのはヒープの最大値。最後にプール・キャッシュを手放して GC し、OS へ返るかも記録する。`IROCA_MEM_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -MemCases <json>` のときだけ回る（結果は `mem.jsonl`。1 回の起動に 1 ケース） |
| `HiResSmokeTests.cs` | 大きなテクスチャ（8K・16K）を製品の経路に 1 回ずつ通す（`HiRes` カテゴリ。品質は見ない）。取り込み・原本の読み込み・マスクの保存形式・色替え・PNG の書き出しと読み戻し一致・シーンへのプレビュー（`-Graphics` のときだけ）・NDMF の差し替えテクスチャ・MCP の入口の、各段の時間とメモリを記録する。`IROCA_HIRES_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -HiResCases <json>` のときだけ回る（結果は `hires.jsonl`。16K は 20GB 前後使うので 1 回の起動に 1 ケース） |
| `WindowOutputParityTests.cs` | 色替えの経路どうしが同じ画素を出す（0.4.0 の計画の 0-2）。ウィンドウの確定段 = `SessionRecolor.Apply`（非破壊ビルドの入口。選択キャッシュに当たる 2 回目も）、ウィンドウの書き出し = 自動化 API の PNG。マスク（共通・ゾーンの塗らない・ここも塗る）を右クリックと同じ口で塗って比べる |
| `OldFormatLoadTests.cs` / `OldFormatFixtures.cs` | 古い版（v0.1.0・v0.3.0）のプリセット・セッションのファイル・レシピを、今の版が同じ中身として読む（0-2）。v0.1.0 のプリセットの優先度（layerIndex）の並べ替え、旧 bitpack のマスク。自動化 API がプリセットを並べ替えない今の食い違いも、4-3 で直すまで固定する |
| `DetailResolutionTests.cs` | 拡大表示の解像度の設定（「元の解像度」。0.4.0 の計画の 1-1）。取り込みで縮むテクスチャで、オフなら取り込み後の寸法、オンなら作業解像度で拡大表示を作り、変更後・変更前（「元を表示」）の切り出しとマスクの重ね表示が同じ格子・同じ範囲になる。切り替えても色替えをやり直さず、オフへ戻して縮めたものが確定段のジョブの縮めたものと同じ画素になる。確定段が走っている間に切り替えても、終わったときに設定どおりになる。差分表示の間の切り出しは、同じ範囲の変更前の切り出し（差分を描く相手。「元を表示」と同じ口で作るものと同じ画素）を持ってくる |
| `PreviewDiffShaderTests.cs` | 差分表示のシェーダー（`Hidden/Iroca/PreviewDiff`。0.4.0 の計画の 1-2）が、差分の定義（8 ビットの値で RGB の差の合計が 10 を超えた画素を塗る）どおりに塗るか。定義は製品ではシェーダーにだけあるので、テストに CPU で同じ定義を書いて比べる。しきい値の前後と暗い色（リニアのプロジェクトで 8 ビットの値へ戻すときの誤差が出やすい）を多めに入れる。シェーダーが配布物に入っているかも見る。描画の段は `-Graphics` でないときは Ignore |
| `NativeMemoryLifecycleTests.cs` | 手を止めたときの作業配列の縮小（0.4.0 の計画の 2-2）。計算が全部終わってからしばらく次の計算が無ければ、作業配列のプールの空きを返し、その間に計算が始まれば返さない（待つ時間はテストの間だけ短くする） |
| `BenchSeams.cs` | 計測（MemoryBench・HiResSmoke）が名前で探す製品のメンバーの一覧と、それが今の製品にあるかの検査（`BenchSeamTests`）。計測は同じテストを基準のコミット（メンバーが private・まだ無い版）にも当てて比べるので直接は呼ばない。名前を変えたら、計測を回す前にふだんのテストで気づく |

テストは配布パッケージ（`Code/`）の外に置いてあるので、ユーザーのプロジェクトには入らない。

## 実行

```powershell
# 1 回だけ: ホストに本体とテストをリンクする
.\scripts\Link-HostPackage.ps1 -HostProject "$env:USERPROFILE\Documents\Avatar_Projects\Iroca_Dev" -WithEditorTests

# 実行（ホストを Unity Editor で開いていないこと）
.\scripts\Run-EditorTests.ps1
```

`Run-EditorTests.ps1` は Unity を batchmode・BelowNormal 優先度で起動し、結果の要約と、
Unity とハーネスの出力が完全一致した件数を表示する。終了コードは失敗件数。
Editor を開いたまま回したいときは、Unity の Test Runner ウィンドウから `Iroca.EditorTests` を実行してもよい
（その場合 `RuntimeParityTests` は `IROCA_REPO` が無いので Ignore になる）。

テストは `Assets/IrocaEditorTests_Temp/` に一時アセットを作り、終わったら消す。

### コンパイルだけ確かめる（Unity を起動しない）

テストを書いたら、Unity を開く前に型の誤りを見つけられる。クラウドのセッションと CI でもこれは回る。

```powershell
dotnet build scripts/build-check/IrocaEditorTests.csproj
```

参照はテストの asmdef と同じ（本体・Unity Test Framework・Unity の NUnit）。Unity Test Framework は Unity と同じく
パッケージのソースから作る。パッケージはホストの `Library/PackageCache` から借りる（ホストを持たない環境は
`scripts/build-check/fetch-unity-test-framework.sh` で取る）。NDMF を使うテスト（`#if IROCA_NDMF_PRESENT`）は
NDMF が無いので外れる。詳しくは `scripts/build-check/README.md`。
