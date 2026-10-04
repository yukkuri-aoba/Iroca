# EditMode テスト（Unity 実機）

headless ハーネスと golden は net8 で製品 C# を動かすので、Unity 固有の部分は検証できない。
ここは Unity Test Framework の EditMode テストで、その部分を実機（batchmode）で検査する。

| ファイル | 検査すること |
|---|---|
| `PersistenceTests.cs` | セッション・マスク・プリセットの保存と復元。読めないファイルを空保存で消さない、GUID が一時的に引けないだけのファイルを消さない（.orphan 退避と復元） |
| `SampleUvUndoTests.cs` | スポイト位置（`ColorZone.sampleUV`）が Undo/Redo・ゾーン削除の Undo で色と対で戻る、プリセットには載らずセッションには残る（Undo の書き戻しは Unity のシリアライザなので実機で検査。対象は `SampleUvUndoHost.cs`） |
| `ExportPipelineTests.cs` | 書き出しの手順（原本の読み込み・PNG 化・書き込み・import 設定の引き継ぎ・出力先の決定）。単体書き出しと一括書き出しが共有する `ExportPipeline` を直接呼ぶ |
| `MeshUvLocatorTests.cs` | 編集中のテクスチャを使うメッシュの探索（シーン → 同じ素材フォルダの Prefab、書き出し名 `_recolored` の追跡、手動指定、Read/Write 無効のメッシュで落ちない） |
| `MeshIslandOutlineTests.cs` | プレビューに重ねる UV の島の輪郭（内側の対角線を描かない、UV を重ねた左右対称のパーツでも輪郭が残る、Tiling / Offset） |
| `NonDestructiveTests.cs` / `NdmfProcessAvatarTests.cs` | 非破壊の色替え（レシピ → テクスチャ → マテリアル差し替え）。後者は NDMF を通した結合で、NDMF があるホストでだけコンパイルされる |
| `RecipeRegistrationTests.cs` | 登録で `Iroca Recolor` が増えない（同じオブジェクトは 1 つにまとめる・同じテクスチャは置き換え・Undo）、旧保存形式の読み込み |
| `LivePreviewTests.cs` / `NdmfPreviewFilterTests.cs` | シーンでのプレビュー（どこに何を映すかの規則、ウィンドウの結果を RenderTexture に書く受け渡し口、レシピの出来上がりの貸し借り、NDMF のフィルタの呼び出し口）。後者は NDMF があるホストでだけコンパイルされる |
| `RuntimeParityTests.cs` | 製品の実行環境（Unity の Mono）とテストの実行環境（ハーネスの net8）で再着色の出力が一致するか。golden の入力（`scripts/golden/cases/`）を製品経路（`IrocaAutomation.RecolorWithZones`）に通し、ハーネスの出力（`scripts/golden/expected/`）と比べる |

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
