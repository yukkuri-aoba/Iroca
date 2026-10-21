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
| `NonDestructiveTests.cs` / `NdmfProcessAvatarTests.cs` | 非破壊の色替え（レシピ → テクスチャ → マテリアル差し替え）。後者は NDMF を通した結合で、NDMF があるホストでだけコンパイルされる |
| `WorkingResolutionTests.cs` | 編集の作業解像度（`WorkingResolution`）。既定は元ファイルを取り込み後の寸法へ縮め、「元の解像度で編集」なら元のまま。書き出しは常に元の解像度。縮小をテクスチャの生データから帯ごとに読む経路が、Color32[] の経路と同じ画素になる（RGBA32・ARGB32・BGRA32・RGB24）|
| `NdmfSupportTests.cs` | NDMF が使えないときの扱い。asmdef の版の範囲（2.0 のベータ版も外す）を Unity がそのとおりに読んでいるか（入っているパッケージの版から各 asmdef の define を照らす）。NDMF が入っていない・古い・新しすぎるときに、色替えするものがあれば VRChat へのアップロードを止めて直し方を案内するか。VRChat SDK があれば「Iroca Recolor」が NDMF と関係なく `IEditorOnly` で、アップロードの確認が NDMF より先に走り、NDMF が使えるときは素通しするか |
| `RecipeRegistrationTests.cs` | 登録で `Iroca Recolor` が増えない（同じオブジェクトは 1 つにまとめる・同じテクスチャは置き換え・Undo）、旧保存形式の読み込み |
| `RecipeSyncTests.cs` | 外（Git の pull・バックアップからの復元など）で書き換わったレシピを、いろかの窓が古い内容で上書きせず読み直す。窓自身の保存・同じ中身での取り込み直し・Undo でレシピが前の中身へ戻ったときは、外からの変更と取り違えない |
| `LivePreviewTests.cs` / `NdmfPreviewFilterTests.cs` | シーンでのプレビュー（どこに何を映すかの規則、ウィンドウの結果を RenderTexture に書く受け渡し口、レシピの出来上がりの貸し借り、NDMF のフィルタの呼び出し口）。後者は NDMF があるホストでだけコンパイルされる |
| `RuntimeParityTests.cs` | 製品の実行環境（Unity の Mono）とテストの実行環境（ハーネスの net8）で再着色の出力が一致するか。golden の入力（`scripts/golden/cases/`）を製品経路（`IrocaAutomation.RecolorWithZones`）に通し、ハーネスの出力（`scripts/golden/expected/`）と比べる |
| `MaskSuggestInferenceTests.cs` | AI マスク提案の実推論（パッケージの読み込み・モデルの変換とロード・推論）が Unity 2022.3（Sentis）と Unity 6（Inference Engine）で通るか。灰地の赤い円の中心をクリックし、円と提案マスクの IoU を測る。パッケージ未導入・モデル未配置なら Ignore。`IROCA_AI_REPORT` にパスがあれば結果を 1 行追記する。いろかのウィンドウを閉じたとき（推論のワーカーを手放す）の後も、作り直して同じように取れるか。モデルの変換で Assets に残った一時フォルダは、いろかのものだけを片付ける |
| `MaskSuggestModelFilesTests.cs` | AI モデルのファイルの扱い（モデル置き場は全プロジェクト共有）。ほかの Sentis 版の変換済みキャッシュは長く使われていないものだけ消す、保存中に落ちて残った一時ファイルだけ消す、別の Unity が読んでいる最中のキャッシュも変換し直さずに読める。ダウンロードの途中で落ちた Unity の一時ファイルだけ消す（別の Unity がダウンロード中のものは消さない）。ウィンドウを描くときはモデルのファイルがあるかを見るだけで読み込まず、それでも準備済みとして扱う |
| `PerfBenchTests.cs` | 製品の実行環境（Unity の Mono）での再着色の速さ（`Perf` カテゴリ。品質は見ない）。`IROCA_PERF_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -PerfCases <json>` のときだけ回る（結果は出力フォルダの `perf.jsonl`） |
| `MemoryBenchTests.cs` | 製品の実行環境（Unity の Mono）でプレビュー相当の処理を繰り返したときの Mono ヒープとプロセスのメモリ（`Memory` カテゴリ）。Mono のヒープは縮まないので、見た目のメモリを決めるのはヒープの最大値。最後にプール・キャッシュを手放して GC し、OS へ返るかも記録する。`IROCA_MEM_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -MemCases <json>` のときだけ回る（結果は `mem.jsonl`。1 回の起動に 1 ケース） |
| `HiResSmokeTests.cs` | 大きなテクスチャ（8K・16K）を製品の経路に 1 回ずつ通す（`HiRes` カテゴリ。品質は見ない）。取り込み・原本の読み込み・マスクの保存形式・色替え・PNG の書き出しと読み戻し一致・シーンへのプレビュー（`-Graphics` のときだけ）・NDMF の差し替えテクスチャ・MCP の入口の、各段の時間とメモリを記録する。`IROCA_HIRES_CASES` が無ければ Ignore で、`Run-EditorTests.ps1 -HiResCases <json>` のときだけ回る（結果は `hires.jsonl`。16K は 20GB 前後使うので 1 回の起動に 1 ケース） |

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
