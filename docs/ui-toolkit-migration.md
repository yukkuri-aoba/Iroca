# UI を UI Toolkit へ移す計画（0.4.0 の後）

いろかの UI（`Code/UI`）は IMGUI（`OnGUI`・`GUILayout`）で書いてある。これを UI Toolkit（UIElements。以下 UITK）へ
段階的に移す。**着手は 0.4.0 を出した後**（2026-11-18 ユーザー決定）。ここは、その時にすぐ始められるように、理由・
今の形・目標の形・進め方・確かめ方・決めておくことをまとめたもの。層と依存の決まりは `docs/architecture.md`、Unity の
版の対応は `docs/unity-version-migration.md` が正。

調べた時点は 2026-11-18（`dev-0.4.0` の 6dfb5383）。着手するときは「7. 着手するときの手順」で数え直す。
下調べの試作と撮影の道具は dev_safe にある（`_spike/uitk_shell/`・`scripts/ui_capture/`）。

## 1. なぜ移すか

- **Unity の推奨が UITK に変わっている。** Unity 6 では、エディタの標準のインスペクターも UITK が既定になった。
  VRChat の制作環境も、遠くないうちに Unity 6 が標準になる見込み（`docs/unity-version-migration.md`）。将来への投資として、
  なるべく望ましい形にしておく。
- **IMGUI に特有の回避策が多い**（数は「3. 今の形」）。同じフレームの Layout と Repaint で並ぶ要素を食い違わせない工夫、
  前のフレームで測った高さを次のフレームで使う仕組み、描画と対で保つ高さの見積もりなど。直すたびに見落としが出やすく、
  「書き出し欄が画面外へ押し出される」「スライダーをつまむと列がずれる」「設定列をスクロールするとプレビューが作り直される」
  などを何度も直してきた。UITK は要素を残しておく方式（保持モード）で、配置はフレックスボックスが決めるので、多くが
  そもそも要らなくなる。
- 見た目は利用者に分からない変更なので、0.4.0 の出荷を止めている課題とは並べない。0.4.0 の後、機能を足す前の土台として入れる。

## 2. 前提と制約

- **Unity 2022.3（VRChat の今の標準）と Unity 6 の両方で動かす。** 2022.3 に無い API（Unity 6 のランタイムのデータ
  バインディング、`[UxmlElement]`・`[UxmlAttribute]`）は使わない。Unity 6 で古いとされる API（`UxmlFactory`・`UxmlTraits`）も
  使わない。**独自の UXML 要素は作らず、要素の木は C# で組み、見た目は USS で書く**（UXML を使うなら組み込みの要素だけ）。
- 層の決まりは変えない。UITK を使うのは `Code/UI` だけ。Application 以下は今も IMGUI を知らず、UITK も知らない。
- 新しい操作要素にはツールチップを付ける決まりはそのまま（UITK では `tooltip` プロパティ）。
- 文言は `Localization` が正のまま。言語を切り替えたら、要素の文言を付け直す（木は作り直さない）。
- 守る契約（移行のたびに落ちないよう、先に固定する。「3.4」）: 保存名（`scripts/source_checks/serialized_field_names.json`）、
  テストが使う internal の入口、ソースを読む検査（`test_window_document_mirror`・`test_manual_tolerance_clears_cutoff`・
  `test_autotune_apply_single_source`・`test_no_pixel_sized_managed_arrays` の許可リスト）、`BenchSeams` の反射。

## 3. 今の形（2026-11-18 の棚卸し）

`Code/UI` は 40 ファイル・約 15,400 行。UITK の使用は 0。窓は `IrocaWindow`（partial 9 ファイル）・`IrocaPreviewWindow`・
`MaskBrushWindow`・`Debug/DebugWindow`、インスペクターは `IrocaRecipeEditor`・`IrocaRecolorEditor`、ほかに Preferences の
`SettingsProvider`（`UpdateNotice`）。

### 3.1 IMGUI に特有の回避策（grep と周辺の読みで数えた。±10% ほど）

| 種類 | 数 | 例 | UITK では |
|---|---|---|---|
| A. Layout と Repaint の食い違い対策（Layout のときに決める・次の Layout で行う・delayCall に回す・仮の矩形） | 約 45 | 上部の案内の出し分け、ゾーンの追加・削除・並べ替え、テクスチャの切り替え | ほぼ不要。「ドラッグ中は文面を据え置く」のような意図だけ残る |
| B. 前のフレームの実測を使う・追い再描画 | 14 | `_sideBySideTopHeight`・`_jobOverlayHeight`・`_chromeAboveViewportH`・ゾーンカードの矩形・プリセット欄の高さ | 多くが不要（flex・`GeometryChangedEvent`） |
| C. 高さ・幅の手計算（描画と対で保つ） | 約 25 | `ExportView.GetSectionHeight`・`NoticeBox.Height`・`SettingsLabelWidth`・`PreviewView.HeaderLayout` | ほぼ不要。ラベルの列の幅だけ USS で設計し直す |
| D. `GUI.changed` を戻す・ChangeCheck の範囲 | 12 | 折りたたみ・案内のボタン・スクロールバーで作り直しが走らないように | 不要（要素ごとの変更イベント） |
| E. ScrollView の癖 | 約 13 | 縦バーの幅を手で引く・横スクロールの禁止 | 不要。ただしプレビューのシェーダーの GUI クリップは残る |
| F. 再描画の管理 | 呼び出し約 108・仕組み約 10 | `RequestRepaint`・`TickRepaintWatch`・別窓の `DrawStamp`/`HostWatch` | 「4.3 更新のきっかけ」に置き換える |
| G. 入力 | `hotControl` 27・`Use` 16 ほか | ゾーンの並べ替え・プレビューのパン・ブラシ・スポイト | プレビュー内は IMGUIContainer に残す。並べ替えは PointerEvent で書き直す |

### 3.2 ファイルの分け方（移し方の見立て）

- **普通のフォーム（UITK で書き直す）**: 上部の案内（`IrocaWindow.Layout`・`MaterialColor`・`NoticeBox`・`UpdateNotice`）、
  ゾーンカード（`IrocaWindow.ZoneList`、1,270 行。並べ替えのドラッグは書き直し）、加工設定、マスク欄（`MaskPaintView` の欄・
  `MaskSuggestSection`・`MeshPartSection`）、プリセット（`PresetsView`）、書き出し（`ExportView`）、別窓 `MaskBrushWindow`、
  インスペクター 2 つ。
- **自前で描いて入力を取る面（IMGUIContainer に残す）**: プレビュー一式（`PreviewView`・`.Input`・`.Overlays`・
  `.SceneHighlight`・`.ContextMenu`・`PreviewRenderer`・`PreviewDiffDrawer`＋`PreviewDiff.shader`・`DetailPreviewView`・
  `UvDetailOverlay`）。シェーダーが IMGUI のクリップ（`_GUIClipTexture`）に依存し、`GUI.BeginClip`・`Handles`・`hotControl` が
  一体になっている。プレビューの見出しの行（比較・差分・ズーム）だけは UITK にしてもよい。
- **UI でない（手を付けない）**: `IrocaWindow.Recipe`・`.AutoTune`・`.SceneClick`・`.Document`・`.Autosave`、
  `MaskPaintView.Overlay`・`.Persistence`、`MaskSuggestController`、`MeshPartController`、`DetailTiles`、`RecipeImportWatcher`。

### 3.3 状態の持ち方

- 文書・編集の状態は `IrocaEditingDocument`（ScriptableObject。Undo の対象）と `EditingSession`。表示だけの状態（開閉・
  スクロール・武装中のスポイト・ズーム）は窓と各ビューが持つ。**UITK でも状態は今の置き場のまま**にし、要素の木には
  持たせない（木は `CreateGUI` で作り直されるので、持たせると再読み込みで消える）。`viewDataKey`（要素の状態の自動保存）は
  使わない（保存名を固定している検査と二重になる）。
- 文書は `SerializedObject` から全項目を読めることを確かめた（2026-11-18、`session.zones.Array.data[i].*` と加工設定）。
  ただし、値を変えたときに別の値も直す項目がある（許容範囲を手で動かしたら照合の上限を外す `ColorZone.SetToleranceManually`。
  検査あり）。**`Bind` で直接書かせず、今の書き込みの入口（`UndoHelper`・`EditingSession` の関数）を通す。**

### 3.4 テスト・自動化との接点

- EditMode テストは `CreateInstance<IrocaWindow>()` で窓を作り、internal を直接呼ぶ。**`OnGUI` を呼ぶテストは無い**
  （確認済み）。`CreateGUI` も `CreateInstance` では呼ばれないので、UI の作り直しはテストにほぼ響かない。響くのは internal の
  入口: `_win.Preview`・`_maskView`（ストローク・`ApplyRegion`・`ClearMaskAt`）・`_exportView`・`_presetsView.LoadPreset`・
  `ClearModesOnEscape`・`BeginZoneDrag`/`IsDraggingZone`・`RequestNewZoneFromSample`・`DrawStamp`。
- 自動化・MCP は `IEditingWindows` 経由で UI の型を知らない（`IrocaWindow.Recipe` の `AutomationWindows`）。窓の
  `OnEnable`/`OnDisable` での登録（`EditingSessionRegistry`）は残す。
- Debug の asmdef は `DebugWindowHooks.OnDrawFoldout` で本体の設定列へ IMGUI で描く。本体側に IMGUIContainer を 1 つ置いて
  そこから呼べば足りる。

## 4. 目標の形

### 4.1 窓の殻

```
IrocaWindow.CreateGUI()
├ Toolbar（言語・リセット・マニュアル・クレジット）
├ 上部: テクスチャ欄・案内の列（HelpBox＋ボタン。出し分けは style.display）
├ 本体（.side-by-side / .stacked を窓の幅で切り替える。境は今の 600）
│ ├ 設定列: ScrollView（縦バーは常に表示して幅を動かさない）
│ │   ゾーンカードの列・加工設定・マスク欄・プリセット・Debug の IMGUIContainer
│ └ プレビュー列: IMGUIContainer（flex-grow: 1。枠の大きさは contentRect から）
├ 書き出し欄（下端。高さは中身なり）
└ ジョブの帯（position: absolute で下端に重ねる。ほかは SetEnabled(false)、帯だけ有効）
```

左右の幅は、今の「正方形のプレビューがカラムの高さいっぱいに収まる幅を右へ、残りを左へ（320〜450）」を、本体の
`GeometryChangedEvent` で決める。これで今の `availableContentH`・`WindowChromeMargin`・`GetSectionHeight`・`horizH`・
`_sideBySideTopHeight`・`_jobOverlayHeight`・`_settingsContentWidth`・`JobOverlayReserve` が要らなくなる。

### 4.2 欄の作り

- 欄ごとに `VisualElement` を組む C# のクラス（`TextureSection`・`ZoneListSection`／`ZoneCard`・`ProcessingSection`・
  `MaskSection`・`PresetsSection`・`ExportSection`）。独自の UXML 要素にはしない。
- 欄は「状態を読んで要素を合わせる」`Refresh()` を持つ（今の「毎フレーム状態を読む」をそのまま、要素へ写す形にする。
  MVVM にはしない）。値を書くのは要素の変更イベントから、今の書き込みの入口を呼ぶ。
- 見た目は USS（`Code/UI/Uss/*.uss`）。色は Unity の変数（`--unity-colors-*`）を使い、ダーク・ライトの両方に合わせる。
  `IrocaColors` の色は USS の変数へ移す。

### 4.3 更新のきっかけ（いちばんの分かれ目）

今の `RequestRepaint()`（約 108 か所から呼ばれる）は「表示に効く状態が変わった」という合図でもある（`BumpUiState` で版を
進める）。**これを「UI の版が進んだ」合図として残し、殻がその版を見て、欄の `Refresh()` と IMGUIContainer の
`MarkDirtyRepaint()` を呼ぶ。** 版を進めるのは、今の呼び出しに加えて `EditingSession.Changed`・`Undo.undoRedoPerformed`・
言語の切り替え。別窓（`IrocaPreviewWindow`・`MaskBrushWindow`）も同じ版を見る（今の `DrawStamp`/`HostWatch` と同じ発想）。
背景の処理の進み（自動調整・書き出し・AI の準備）は、今どおり値が変わったときだけ合図する。

この形なら、呼び出し側を書き換えずに段ごとに移せる。`Refresh()` が重くならないよう、欄の中で「前と同じなら触らない」を守る。

### 4.4 残すもの

- プレビュー一式は IMGUIContainer の中で今のまま動かす。外から渡すのはカラムの寸法（今の `availableColumnWidth/Height` を
  `contentRect` から）だけ。入力（パン・ブラシ・スポイト・右クリック・Ctrl+スクロール）もそのまま。
- Esc の全体フック（`EditorApplication.globalEventHandler` の反射）と、色の窓の持ち主の判定（`ColorPicker` の反射）は残る。
  ゾーンの色を UITK の `ColorField` に替えると、後者は形が変わる（4 段で確かめる）。

## 5. 進め方

各段は単独で出荷できる形で終える（途中の段でも窓は全部動く）。段ごとに「6. 確かめ方」を通してからコミットする。
段の順は、効果が大きく独立している所から。

| 段 | 中身 | 消えるもの | 主なファイル |
|---|---|---|---|
| 0 | **準備（製品のコードは変えない）**: 守る契約の一覧を固め（2 章の末尾）、撮影の道具で基準を撮る（6 章）。手動で確かめる操作の一覧を作る | — | dev_safe |
| 1 | **殻**: 4.1 の木を作り、中の欄は今の描画関数を呼ぶ IMGUIContainer のまま入れる。4.3 の更新のきっかけを入れる。ジョブの帯を absolute に | B1・B2・B14、C の窓全体の配置、E の設定列の幅、`WindowChromeMargin`・`GetSectionHeight`（書き出し欄は中身なりの高さの IMGUIContainer になる） | `IrocaWindow.Layout`・`.Repaint`・`ExportView` |
| 2 | **上部と書き出し欄**: 案内（`NoticeBox` の置き換え）・テクスチャ欄・書き出し欄を UITK に | A-1 の上部の案内・`NoticeBox.Height`・D の案内のボタン | `IrocaWindow.Layout`・`.MaterialColor`・`NoticeBox`・`ExportView`・`UpdateNotice`・`MaskSuggestSection` の帯 |
| 3 | **小さい窓とインスペクター**: `MaskBrushWindow`・`IrocaRecipeEditor`・`IrocaRecolorEditor`（`CreateInspectorGUI`） | 別窓の `HostWatch` の一部・`ExitGUI` | 3 ファイル |
| 4 | **ゾーンカード**: カードを `VisualElement` に。並べ替えは PointerEvent と PointerCapture で書き直す。色は `ColorField`（持ち主の判定の反射が要るかを確かめる） | A-3 の遅延の仕組み・B7・B8（実高の記録）・D・G の並べ替え | `IrocaWindow.ZoneList`（検査 2 つが読むので、検査も同じコミットで直す） |
| 5 | **残りの欄**: 加工設定・マスク欄・プリセット | B10・C の残り・`UndoHelper`（UITK 側では要素のイベントで `Undo.RecordObject`） | `IrocaWindow.Layout`・`MaskPaintView`・`MeshPartSection`・`PresetsView` |
| 6 | **プレビューの見出しの行（任意）**: 比較・差分・元を表示・ズームを UITK に。1 行か 2 行かは flex-wrap に任せる | `PreviewView.HeaderLayout` の手計算・`_chromeAboveViewportH` | `PreviewView` |

段 1 と 2 で手計算と実測の大半が消える。段 4 が最も重い。プレビュー本体（描画と入力）を UITK で描き直すのは、この計画に
含めない（効果に比べて危ない。必要になったら別に考える）。

## 6. 確かめ方

Code/UI はハーネスにも視覚レビューのゲートにも入らないので、次を段ごとに通す。

1. **build-check**: `dotnet build scripts/build-check/IrocaEditor.csproj` と `IrocaNdmfCheck.csproj`（NDMF あり・なし）。
   Unity 6 は `-p:UnityVersion=6000.0.67f1` などで同じ csproj を通す（2026-11-18 の試作は両方でエラー・警告 0）。
2. **見た目の前後比較**: `python dev_safe/scripts/ui_capture/ui_capture.py run --out <前>`（段の前）→ 変更 →
   `run --out <後>` → `compare <前> <後> --out <比較>`。窓の大きさ 4 つ（728×700・1024×900・1400×700・520×760）×
   日本語・英語。Iroca_Lite で背面のまま撮れる（UITK の窓も撮れることを試作で確かめた）。変わった画を目視する。
   NDMF ありの書き出し欄は Lite に無いので、Iroca_Dev で窓の大きさも中身も変えずに 1 枚撮って見る。
3. **EditMode**: `scripts/Run-EditorTests.ps1 -HostProject <Iroca_U2022_ifx>` と Unity 6 のホスト（`Iroca_U6`）。
4. **手で確かめる操作**（撮影では見えない入力。段 0 で一覧にする）: スポイト（変えたい色・別の色を追加）・右クリックの
   メニュー・Ctrl+スクロールのズームとパン・ブラシ・Esc で武装の解除・Ctrl+Z/Ctrl+Y・テキスト欄の入力中のショートカット・
   ゾーンの並べ替え（窓の外で離す）・ジョブ中のキャンセル・別窓のプレビュー・言語の切り替え・スクリプトの再読み込みの後・
   Play モードの出入り・ライトのスキン・表示倍率 125%。
5. **source_checks**: `python -m pytest scripts/source_checks`（ソースを読む検査が UI の書き換えで落ちないか）。

## 7. 着手するときの手順

1. 3 章の数を取り直す（`Code/UI` の行数、`Layout` で決める・`GetLastRect`・`hotControl`・`RequestRepaint` の grep）。
   この文書の数と大きく違えば、5 章の段の中身を見直す。
2. 段 0 を済ませる（契約の一覧、基準の撮影、手で確かめる操作の一覧）。
3. 段 1 は、まず dev_safe の試作（`_spike/uitk_shell/UitkShellSpike.cs`）の形を本体に移すところから始める。
4. 段ごとに `docs/architecture.md` の UI の節（「IMGUI のビューは毎フレーム状態を読む」）を、4.3 の形に合わせて直す。

## 8. 危ないところ

- **入力が IMGUIContainer をまたぐ**: キーボードのフォーカス（テキスト欄に入力中の Ctrl+Z）、`hotControl` を取ったまま
  別の要素へ出たとき。プレビューの外で離したドラッグ。試作では実際の入力を確かめていない（段 1 で最初に見る）。
- **再読み込みと Undo**: 要素の木は再読み込みで作り直される。状態は窓のフィールドにあるので戻るが、`Refresh()` が
  再読み込みの直後に 1 回走ることを確かめる。Undo の後の表示は 4.3 の `Undo.undoRedoPerformed` が頼り。
  Unity の版で Undo の挙動が違う件（`docs/architecture.md`）は UI の作り替えと独立だが、段ごとに両方の版で見る。
- **2022.3 の UITK の癖**: ScrollView のスクロールバーの幅・ScrollView の中の IMGUIContainer の幅。試作では問題なかったが、
  ゾーンが多いときの性能（要素の数）と、言語の切り替えでの幅の変化は見ていない。
- **見た目が少し変わる**: UITK の標準の余白・フォントの行の高さは IMGUI と少し違う。マニュアルの画面写真の撮り直しが要る。
- **途中の段の混在**: IMGUI と UITK が窓の中に混ざる期間がある。各段で窓全体が動くことを確かめてから次へ。

## 9. 決めておくこと（ユーザーの判断）

1. 段ごとに出すか（0.4.x・0.5.0 に分けて入れる）、全部済んでから出すか。おすすめは段ごと（各段で窓全体が動く形を守るので、
   問題が出たときに戻す範囲が小さい）。
2. 見た目を今の IMGUI にできるだけそろえるか、UITK の標準の見た目に寄せるか。
3. Unity 2022.3 への対応をいつ切るか（切れば Unity 6 のランタイムのデータバインディングと `[UxmlElement]` が使える）。
   VRChat の制作環境が Unity 6 になり、2022.3 の利用者が十分減ってからでよい。
