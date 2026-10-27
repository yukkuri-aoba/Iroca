# 0.4.0 の計画

作成 2026-10-26。同日、7.1・7.2 の決めることはユーザーが決めた。同日、dev_safe の `docs/selection_redesign_0.4.0.md`
（選択・自動調整の再設計。10-24 のユーザー決定で 0.4.0 に予定）を読み、T5 として組み込んだ（1.5）。7.3 も同日に決まった。
この文書を正として作業を始める（始める前に 4 章の「作業する場所」の準備をする）。
並行するセッションは、作業前にこの文書の該当フェーズを読むこと。進み具合はフェーズごとのチェックボックスで残す。

## 0. 0.4.0 でやること

| # | やること | ユーザーの言葉 |
|---|---|---|
| T1 | 拡大表示を元の（作業）解像度で見られる設定を戻す | プレビューをフル解像度で表示する設定の復旧 |
| T2 | パン・ズーム・スライダーを激しく動かしても、差分表示が表示中の画像とずれないようにする | 依然としてプレビューを激しく動かすと差分表示の表示追従が怪しい |
| T3 | 画素数に比例する Mono ヒープの確保をなくす | Mono ヒープの使用をほぼ、できる限り廃止したい |
| T4 | 依存が適度に分かれた、保守しやすいひとかたまりのコードにする（出力を変えない作り替え） | アルゴリズムの寄せ集め・束から、保守性の高いひとかたまりへ。技術的負債がなるべくないように |
| T5 | 選択と自動調整の「規則の束」を、3 層の模型（領域・素材の模型・判断）で作り直す | 10-24 の依頼「規則の束を一塊の再設計されたきれいなコードに置き換えたい」（dev_safe `docs/selection_redesign_0.4.0.md`） |

**T3・T4 は出力をビット一致で保つ作り替え**で、改善サイクル（`.claude/instructions/improvement-cycle.md`）とは混ぜない。
わざと振る舞いを変えるもの（4-3 のプリセットの不一致の修正など）は、それだけで 1 コミットにして CHANGELOG に書く。
**T5 は選択の結果をわざと変える作業**で、改善サイクルと汎化の物差し（アバター別・未使用クリック・ホールドアウト）で採否を決める。
T4 は、T5 で置き換える部分（旧の選択・旧の自動調整）を作り直さず、境目の向こうへ移すだけにする（1.5）。

## 1. 今の状態（2026-10-26 の調査）

規模: `Code/` は 160 ファイル・46,300 行。Core 14,340 / UI 14,316 / Infra 5,770 / MaskSuggest 4,302 /
Automation 2,709 / Debug 1,974 / Sentis 1,647 / NDMF 916 / MCP 224 / Runtime 96。

### 1.1 T1: フル解像度の表示が消えた経緯

- 465cb3c（0.3.0）で、色替えは元の解像度（辺が 8192 以下なら）で行い、**拡大表示はインポート設定の Max Size まで
  縮めて映す**ようにした。それまでの「元の解像度で編集」（オンなら拡大表示も元の解像度）はこのとき消えた。
- 拡大表示の土台を縮めたものにするか作業解像度のものにするかは、`PreviewView.DetailSource` / `DetailW` / `DetailH` /
  `DetailOutput`（`Code/UI/PreviewView.cs:544-556`）の 1 か所で決まっている。拡大表示・前後比較・「元を表示」・
  マスクの重ね表示・アバターの色は、どれもここを通る。縮めたもの（`_displaySource` / `_displayOutput`）は
  `RememberTrueSourceBasis`（:530）と確定段（`PreviewView.Async.cs:270`）が作る。
- したがって **出力に影響しない「表示だけの設定」として戻せる**。処理の解像度（`WorkingResolution.Size`）は変えない。

### 1.2 T2: 差分表示がずれる原因

1. **全体の差分が別のジョブで遅れて届く。** プレビュー画像を差し替えたあとで `_diffJob` を予約する
   （`PreviewView.Async.cs:413`）。差分の反映は早くて次のフレームなので、画像が新しく差分が古いフレームが必ずある。
   スライダーのドラッグ中はプロキシの結果が次々届くので、差分はいつも 1 つ前の結果のものになる。
2. **拡大中は 2 種類の差分が継ぎ合わさる。** 切り出しの差分は 0.3.1（ca0bc4f）で切り出しと同じジョブにしたが、
   切り出しの外側は 1. の縮小表示用の差分で補っている。解像度の違う 2 つの差分が境目で接し、1. の遅れもそのまま残る。
3. **切り出しの範囲が 1 フレーム古い。** 切り出しは前のフレームの配置（`lastPreviewRect`）とスクロール位置から
   範囲を決め、走っているジョブがあれば終わるまで次を始めない（`PreviewView.cs:816-829`）。続けてパン・ズームすると、
   切り出しはいつも少し後ろを追い、端が縮小表示のまま残る。
4. **パンのたびに管理配列を 3 本作る**（切り出し・変更前・差分。`DetailPreviewView.cs:274-288`）。GC の停止で
   追従がさらに詰まる（T3 とつながる）。
5. ca0bc4f のコミット本文のとおり、0.3.1 の修正は実機の操作では確かめていない。

ユーザーの気づきの一覧（dev_safe `docs/多分バグ.md`）との対応:

| 気づき | 原因 | 直す所 |
|---|---|---|
| 前後比較のあと、直接差分表示を押すと差分の表示が切れる | 2.（切り出しの差分がまだ無い間は、縮小表示の差分で補うだけ） | 1-2 |
| 差分表示中に、端より外へ動かす・スクロールすると差分が荒ぶる | 1.・2.・3. | 1-2 |
| 拡大したとき、一部が概要（プロキシ）のまま表示される | 3. | 1-2（範囲をそのフレームで決め、先読みする）・4-4 |
| 解像度をテクスチャの解像度で表示する設定が消えている | 1.1 | 1-1 |

差分の定義は「変更前と変更後の画素ごとの RGB の差の合計が 10 を超えたら (255,220,0,160) で塗る」だけ
（`PreviewView.BuildDiffPixels`、`PreviewView.Async.cs:484`）。**描いている 2 枚のテクスチャから、同じ描画の中で
GPU（シェーダー）で作れば、ずれは原理的に起きない。** 別ジョブ・差分テクスチャ・管理配列も要らなくなる。

### 1.3 T3: Mono ヒープに残っている大きな確保

0.3.0 までに、Core の作業配列・色の表・選択キャッシュ・プレビューの原本と出力・NDMF の画素はヒープの外へ移した。
残っているもの（N = 画素数。4K で 1,680 万、8K で 6,710 万）:

| 段 | 何 | 主な場所 | 4K / 8K | 寿命 |
|---|---|---|---|---|
| A1 | 編集中のマスク（`bool[N]` を層ごと） | `MaskPaintView.cs:28-37` | 共通 1 + ゾーン 5×2 で 176MB / 704MB | ウィンドウを開いている間 |
| A2 | 重ね表示の作り直しで全層を複製 | `MaskPaintView.Overlay.cs:152` | 層ごと 16 / 64MB（ストロークのたび） | 一時 |
| A3 | 書き出し（読み込み・`GetPixels32`・PNG 化） | `ExportPipeline.cs:71-136`、一括書き出し | 64 / 256MB + ファイル + PNG | 書き出しごと |
| A4 | メッシュの部位（UV チャート） | `UvChartMap.cs:109, 260, 369` | 64 / 256MB ×2 + 右クリックごと 16 / 64MB | 部位を使う間 |
| A5 | 自動調整の HSV グリッドと証拠の配列 | `ZoneAutoTuner.cs:180`、`.Evidence.cs:277, 826`、`.Verify.cs` | 48 / 192MB + 数十 MB | 実行ごと |
| A6 | 自動化 API（MCP）の色替え | `IrocaAutomation.cs:378-390`、`.Recolor.cs:323-616` | 8K で約 900MB + PNG | 呼び出しごと |
| B1 | NDMF のビルド・シーンのプレビューでのマスク展開 | `MaskRle.cs:181`（`MaskStateCodec.ToSnapshot` 経由） | マスクごと 16 / 64MB | ビルドごと（ウィンドウを閉じていても） |
| B2 | Undo・ストロークのたびのマスクの符号化・復号 | `MaskPaintView.Persistence.cs:70-100`、`MaskRle.cs:62-110` | マスクの複雑さ次第 | 操作ごと |
| B3 | プレビューの要求ごとのマスクの詰め直し | `MaskSnapshot.Pack`（`MaskPaintView.cs:894`） | 層ごと 2 / 8MB | 要求ごと（ドラッグ中も） |
| B4 | AI 提案の前処理・後処理と埋め込みのキャッシュ | `SamImageOps.cs:104`、`SamMaskPostprocess.cs`、`SamZoomOps.cs`、`SamMaskRefine.cs`、`SentisMaskSuggestService.cs` | 数十〜100MB | クリックごと・常駐 25MB |
| B5 | 色の表の大きさの配列 | `PixelProcessor.Palette.cs:242, 308`、`PixelProcessor.cs:547-549` ほか | 最大 16MB 前後 | 実行ごと |
| B6 | 選択の残す・強制のビット列 | `PixelProcessor.cs:664, 734` | 2 / 8MB | ゾーンごと |
| C | 拡大表示の切り出し・プロキシ・デバッグの記録・文字列・OnGUI の細かい確保 | `DetailPreviewView.cs`、`PreviewView.Async.cs`、`DebugCaptureContext.cs` ほか | 数 MB 以下 | 頻繁 |

ヒープの外の部品（`IrocaNativeMemory` / `NativeBlock` / `NativeFloatBuffer` / `NativeWorkPool` / `NativePixels` /
`ColorPalette`）にも課題がある:

- 持ち主と後始末（ファイナライザ・スレッド中断時の扱い）の実装が 5 通りあり、少しずつ違う。
- 生の `T*` は長さを持たず、はみ出しは Windows のガードページ（`IROCA_NATIVE_GUARD`）でしか捕まらない。
- プールへの二重返却が、間に貸し出しを挟むと他人の塊を黙って返してしまう。
- プールに上限の量と、手を止めたときの縮小が無い（4K で 64MB の塊を最大 24 個まで持ちうる）。
- 量の記録が全体で 1 つの数だけ。用途別・最大値・貸し出し中と空きの区別が無い。

Unity 2022.3 の制約: `LoadImage`・`SetPixels32`・`EncodeToPNG` は管理配列しか受け取らない。
`GetPixelData<T>`・`SetPixelData`・`LoadRawTextureData(IntPtr)`・`ImageConversion.EncodeNativeArrayToPNG` は使える。
headless ハーネス（.NET 8）には Unity の実行環境が無いので、**Core は `NativeArray` を使えない**
（`Marshal.AllocHGlobal` のまま）。`NativeArray` に見せるのは Infra の境目だけにする。

### 1.4 T4: 構造の問題

**Core**

- `PixelProcessor` は 11 ファイルに分けた 1 つの static クラス（7,191 行・静的メソッド約 100）。パイプライン全体が
  `ProcessPixelsArrayCore` の 1 メソッド（`PixelProcessor.cs:283-1521`、約 1,240 行）にあり、16 の段と、
  選択キャッシュ・切り出しの検証・時間計測・デバッグ記録の 4 つの横断的な処理が混ざっている。分けたファイル同士で
  private の定数・型・プールを使い合っていて、分割は見た目だけ。
- 引数が多い: `AnalyzeMixtureBand` 23、`RecoverBoundaryEdges` 22、`GetRelaxedMatchStrength` 20 ほか、15 以上が 9 個。
- 同じ計算の二重実装: 色の表あり・なし（`test_palette_parity` だけが見張る）、連結成分のラベル付け 2 つ
  （FloodFill と Achroma）、マスクの最近傍の対応 2 つ、`PopCount` 2 つ、緩い照合と `ColorZone` の灰色の規則
  （「同期」のコメントで結んでいるだけ）、OkLab（Automation の `TextureColorSummary` が写しを持つ）。
- `ColorZone` が保存されるデータ・UI の開閉状態・照合のキャッシュ・照合そのものを全部持つ。`UpdateCacheIfNeeded`
  を照合の前に呼ぶ決まりがあり、PixelProcessor・自動調整の 7 か所・`ApplyTo`・Automation の 6 か所が呼ぶ。
- `ZoneAutoTuner`（3,038 行）はパイプラインを回さず照合だけを真似る。連結・穴埋め・境界などの段が変わると黙って
  ずれる。入力は `IrocaSessionState` 丸ごと、出力に UI の文言（`Localization`）を含む。**Core → UI の依存**で、
  ハーネスのスタブ（`scripts/headless-run/Harness.cs:12-33`）が隠している。
- Core にあるが Core でないもの: `TextureSlot`・`PreviewJobProgress`・`MeshRaycast`・`IrocaConsts.Layout/MenuPath`・
  常に true の `ExperimentalFeatures.EnableFloodFill`。

**エディタ側**

- `IrocaWindow` は 8 ファイル・4,023 行、`PreviewView` は 7 ファイル・3,537 行。分けたファイル同士で private の
  状態を使い合う。
- **色替えの経路が 5 本並んでいる**（プレビュー・書き出し・一括書き出し・NDMF・自動化 API）。それぞれが原本の読み込み・
  ゾーンの選び方・マスク・実行のしかたを自前で組み立てる。
- 同じ流れの二重実装: 自動調整（ウィンドウと Automation。待ち時間 180 秒の定数まで 2 つ）、アバターへの登録、
  新しいゾーン、原本の読み込み、PNG の書き込み、プリセットの読み込み。
  **プリセットは実害がある**: UI は古い優先順位を直す `MigrateLegacyLayerPriority`（`PresetsView.cs:194`）を通すが、
  MCP の `IrocaAutomation.SessionFromPreset`（`IrocaAutomation.cs:554`）は通さない。古いプリセットのゾーンの順が
  MCP 経由だけ違う。
- 逆向きの依存: Core → UI（`Localization`）、Infra → UI（`RecipeTextureBuilder` → `ExportPipeline`・`IrocaWindow`、
  `UpdateCheck` → `IrocaWindow`）、MaskSuggest → UI、Automation → UI。`FindObjectsOfTypeAll<IrocaWindow>` などで
  ウィンドウを探す所が 18 か所あり、場当たりの「今の編集」の窓口になっている。
- マスクを 2 か所で持つ（保存用の RLE 文字列とペイント用の `bool[]`）。手で置いた 22 か所の同期で合わせている。
- 「編集が変わった」の通知が実質 `previewDirty` の setter しかない（入力の時刻・自動保存・UI の版を兼ねる）。
  再計算は `OnGUI` の中から進め、シーンへの反映もその副産物。
- Undo はウィンドウ全体を記録するので、ズームや開閉状態まで巻き戻る（`s_viewState*` で回避している）。
- EditorPrefs / SessionState のキー約 30 個が 15 ファイルに散る。`Localization`（1,483 行・約 520 の文言）は
  全層から使われる。
- `scripts/build-check` は `Code/**` を 1 つのアセンブリとしてコンパイルするので、層の境目を CI が確かめられない。

**安全網**: 強いが、ファイルの場所と名前に結びついている。

- `Harness.csproj` は Core のファイルを名前で取り込む。
- `test_algorithm_constants` は `Code/Core` 直下の決まったファイルだけを見る。
- `test_autotune_apply_single_source` / `test_zone_cache_invalidation` / `test_recolor_settings` /
  `test_zone_dto_mapping` は、ファイルの場所とメソッド名を正規表現で読む。
- `test_selection_key_audit` と一部の EditMode テストは、private メソッドを名前の文字列で反射して呼ぶ。
- `tools/harness_scope.py` の `_glob_to_regex` は `**` を 1 階層として扱う。サブフォルダへ移したファイルは、
  **視覚レビューのゲートから黙って外れる**。

### 1.5 T5（選択・自動調整の再設計）との関係

dev_safe の `docs/selection_redesign_0.4.0.md`（10-24 の設計の下書き）の要点:

- 今の選択・自動調整は、観察した失敗ごとに段と閾値を足した作り（段 30・定数約 160）。1 段ずつ外して確かめた 10 段のうち
  外せたのは 1 段だけで、規則でロングテールを埋めている。
- 原則（10-24 のユーザー決定）: 選択は「色が同じ・近いか、それ以外か」に尽きる。「近い」はテクスチャの中で相対的に決める。
- 3 層にまとめる: (L1) 領域の層（色の境目で区切った領域と混色の画素）、(L2) 素材の模型（地色・陰影の線・揺れの幅）、
  (L3) 判断（領域ごとの色の尤度比）。
- **置き換えるもの**: 選択（照合 → 強度 → マスクの再適用まで）と自動調整。
  **置き換えないもの**: 再着色（アンカー・無彩・彩度の写し方・混色帯の再着色・デコンタミの再合成）、AI 提案の推論、製品の契約。
- 段取り: P0 下調べ（1 週。領域の純度で続けるかを決める）→ P1 選択（2〜3 週）→ P2 自動調整（2 週）→ P3 置き換え（1〜2 週）。
  旧の経路と新の経路を切り替えて並走させ、物差しのすべてで旧以上になったら置き換える。
- 9 章（10-25 のユーザー決定）: Mono ヒープの残りは再設計のついでに減らす。新しく書く層は、画素数に比例する配列を管理配列で作らない。

**この計画への影響**:

- **T5 で消えるものを、T4 で作り直さない。** 旧の選択の段（照合・彩度天井・閉領域・ハイライト・連結成分・穴埋め・境界・
  ぼかし・緩い照合）、`ColorZone` の照合、`ZoneAutoTuner` の中身は、書き換えずに「旧の選択」の向こうへ移すだけにする。
  T4 が作り直すのは、残る部分（再着色の段・パイプラインの骨組み・土台・エディタ側）。
- **先に選択と再着色の境目を決める（3-7a）。** 選択の出力（強度と混色の被覆率。再着色が読むもの）を型にし、旧の選択と
  新の選択（T5）が同じ形で出す。T5 はこの境目の向こうに新しいファイルで作る。
- **T3 の一部は T5 に任せる。** 旧の選択・旧の自動調整にしか無い管理配列（A5 の HSV グリッドと証拠、B6 の残す・強制のビット列、
  穴埋めと境界のリスト）は移さない（消えるので）。新しく書く層は、最初からヒープの外で持つ（2 章の 8）。
  10-25 の「単独の作業にはしない」は、10-26 の依頼で「残る部分も移す」に広げた（T3）。
- **切り出しの一致の作り直しは要らない。** 再設計の 5.1 は「切り出し・縮小プレビューの一致の仕組みを領域単位で作り直す」と
  しているが、3-8 で切り出しの検証経路を消す（製品は使っていない）ので、要るのは縮小プレビュー（概要段）との一致だけ。

## 2. 全体を通す決まり

1. **出力を変えない。** T3・T4 のコミットは、golden・abcheck（`dev_safe/scripts/refactor_abcheck/abcheck.py`、
   全モード約 1,400 単位をバイト比較）・Unity の `RuntimeParityTests` で一致を示す。メモリの貸し借りの順が変わる
   ものは `IROCA_POOL_POISON=1` / `IROCA_NATIVE_GUARD=1` でも回す。コミット本文に結果を書き、
   `SKIP_VISUAL_REVIEW=1` は abcheck の不一致 0 を確かめたときだけ使う（今の決まりのまま）。
2. **移すだけのコミットと書き換えるコミットを分ける。** 1 コミット = 1 つの変更。
3. **互換を壊さない。** 次は 0.3.x と同じに保つ。
   - ウィンドウとビューの保存されるフィールド名（`sourceTexture`・`_session`・`_boundRecipe`・各ビュー・
     ビューの public フィールド）。名前を変えたり別の入れ物に包んだりすると利用者の作業状態が消える。
     最悪の場合、空のセッションで保存して結びついたレシピを上書きする。
   - JSON の項目名（`IrocaSessionState`・`ColorZone`・`MaskState`・プリセット・レシピ・zones JSON）。
   - EditorPrefs / SessionState のキー。
   - 自動化 API（`IrocaAutomation`・`-executeMethod` の名前・MCP のツール名と JSON の形）。
   - `Code/Runtime/*.meta` の GUID。
   - ウィンドウの型とアセンブリの名前（レイアウトに保存される）。
4. **依存の向きは機械で守る。** まず許可表つきの検査（違反の一覧は減らすことしかできない）で縛り、
   最後に asmdef で固める。
5. **測ってから変える。** フェーズの初めと終わりに、メモリ（MemoryBench）・速さ（PerfBench・KernelBench）・
   操作から表示までの時間（PreviewLatency）を記録して比べる。
6. **Mono で遅くしない。** 画素を回すループの中は生の `T*` のローカル変数のまま。インターフェース・仮想呼び出し・
   `Span`・構造体のインデクサは使わない（KernelBench で Mono の Debug 最適化だと 2〜4 倍遅かった）。
   段は static なクラスにし、段の入口で引数の構造体をローカルへ展開する（`RecolorPixel` が既にそうしている）。
7. **Core を作り替えている間（フェーズ 3A）は、旧の選択・再着色のアルゴリズムを変えない**（2026-10-26 決定）。
   T5 は新しいファイルで並走させ、旧の側は変えない。旧の側の改善は、フェーズ 3A の前か後に入れる。
8. **新しく書くコードは、画素数に比例する配列を管理配列で作らない**（`test_no_pixel_sized_managed_arrays` が止める）。
   ヒープの外の部品（3.4）を使う。

## 3. 目標の形

### 3.1 層と依存の向き

```
UI           IrocaWindow・各ビュー・インスペクタ・プレビューの描画（表示と入力だけ）
  ↓
Automation   IrocaAutomation（MCP・コマンドラインの窓口。JSON を下の層へつなぐだけ）
  ↓
Application  EditingSession・RecolorInput・PreviewPipeline・AutoTuneService・ExportService・
             RegistrationService・AiSuggestBroker・SessionPersistence（操作の流れ。IMGUI を知らない）
  ↓
Infra        TextureIO・各 Store・WorkingResolution・ジョブ・LivePreview・NDMF 用のビルダー・
             Text（文言）・IrocaPrefs（Unity とファイルとの境目）
  ↓
Core         Foundation / Color / Model / Pipeline / Selection（旧と T5）/ Masks / Recolor / Filters / AutoTune / Uv
             （計算だけ。UnityEditor も文言も知らない）

統合（NDMF・Sentis・MCP・Debug）は、上の層の決まった窓口だけを使う。
```

名前空間は 0.4.0 では `Iroca` のまま（変えても何も縛らず、ハーネス・テスト・Debug の書き換えが増えるだけ）。
縛るのはフォルダと asmdef と検査。

### 3.2 Core

```
Code/Core/
  Foundation/  並列度・画素の決まり（OpaqueAlphaMin）・ネイティブメモリ・ビット列・ヒストグラム・PixelRect
  Color/       色空間（OkLab を公開して写しをなくす）・PixelHsv・色の表とそのキャッシュ（L1 の領域の層も使う）
  Model/       ColorZone（データだけ）・RecolorSettings・セッション / プリセット / DTO・マスクの形と符号化
  Pipeline/    PixelProcessor（今の入口の形のまま残す窓口）・RecolorPipeline（選択 → マスクの契約 → 再着色の段を順に
               呼ぶだけ。約 200 行でパイプラインの仕様として読める）・RunContext / ZoneState / ZoneBuffers・
               SelectionResult（選択と再着色の境目）・SelectionCache
  Selection/
    Legacy/    旧の選択（今の段・ColorZone の照合・SelectionKey）。書き換えずに移すだけ。T5 の P3 で消す
    Regions/   T5 の L1 領域の層（テクスチャごとに 1 回。色の表と同じくキャッシュ）
    Material/  T5 の L2 素材の模型（地色・陰影の線・揺れの幅・周りの素材）
    Decision/  T5 の L3 判断（領域ごとの尤度比 → SelectionResult）
  Masks/       除外・含めるの適用（製品の契約。旧と新で共通）
  Recolor/     再着色の段: Achroma（無彩の固め・フチ消し）・Decontam・RecolorStats（アンカー・ウォッシュ・領域の明るさ）・
               MixtureBand（混色帯の再着色）・RecolorKernel・Compose
  Filters/     箱・ガウスのぼかし
  AutoTune/
    Legacy/    旧の自動調整（ZoneAutoTuner。入力は専用の記録、出力は項目の列挙にするだけ）。T5 の P3 で消す
               （T5 では自動調整は L2 の推定そのものになる）
  Diagnostics/ 時間計測・デバッグ記録（段の境目で呼ぶ観察者。既定は何もしない）
  Preview/     座標変換・縮小・RecolorPreview
  Uv/          UvChartMap
```

- 段は `DecontamStage.Run(RunContext r, ZoneState z)` のような static メソッドにする。段の順と条件分岐は今と同じ
  （ビット一致はこの順に依存する）。ゾーンごとの値は `ZoneParams` として 1 回だけ、今と同じ式で計算する。
- メモリの持ち主を決める: 実行全体（`RunBuffers`）・ゾーンごと（`ZoneBuffers`。8K の最大量を変えないよう、今と同じ
  所で早めに手放す）・段の中の一時（`WorkScratch`）。
- `SelectionResult` は、再着色が選択から読むもの（強度。内部 1・縁は被覆率。混色帯の判定に要るもの）だけを持つ。
  旧の選択から何を渡しているかは 3-7a で洗い出す。旧と新（T5）は同じ形で出し、ハーネスとデバッグの切り替えで比べる。
- 旧の選択の中の二重実装（緩い照合と `ColorZone` の灰色の規則、FloodFill の連結成分のラベル付け、マスクの対応）は、
  T5 で旧ごと消えるので一本化しない。再着色の側に残るもの（Achroma の連結成分のラベル付け、色の表あり・なしの二重実装）は
  フェーズ 3A で扱う。

### 3.3 Application（エディタ側の新しい層）

| 部品 | 役割 | 置き換えるもの |
|---|---|---|
| `EditingSession` | 編集中の文書（原本・結びついたレシピ・ゾーンと設定・マスク・自動調整の入力）。操作（ゾーンの追加・並べ替え・サンプル・プリセットの適用・テクスチャの切り替えなど）は Undo とマスクの同期をしてから `Changed(種類)` を出す | ウィンドウの各 partial に散った操作と `previewDirty` の通知の役 |
| `MaskWorkspace` | 編集中のマスク（1 画素 1 ビットのネイティブのマスク） | `MaskPaintView` の `bool[]` と 22 か所の同期 |
| `SessionPersistence` | 復元の優先順（レシピ → セッションのファイル → 既定）・自動保存・外での書き換えの検出 | ウィンドウと 3 つの Store にまたがる復元の順 |
| `EditingSessionRegistry` | 開いている編集の一覧（ウィンドウが OnEnable で登録・OnDisable で外す） | `FindObjectsOfTypeAll<IrocaWindow>` の 18 か所 |
| `SourcePixelCache` | 作業解像度の原本（参照の数で貸す） | `PreviewView` が持つ原本 |
| `RecolorInput` | 色替えの入力（ゾーン・マスク・設定・原本）を組み立てる唯一の場所 | 5 本の経路の自前の組み立て |
| `PreviewPipeline` | 概要段・確定段・ドラッグの追従の状態と、選択キャッシュ・出力バッファ・シーンへの反映 | `PreviewView.Async` の計算の部分 |
| `AutoTuneService` | 自動調整の流れ 1 本（ウィンドウ用と自動化用の薄い差し込み口を持つ） | `IrocaWindow.AutoTune` と `AutomationAutoTune` |
| `AiSuggestBroker` | AI 提案の待ち行列の唯一の持ち主 | 自動化がウィンドウの窓口（`EvidenceChannel`）を借りる形 |
| `ExportService` / `RegistrationService` | 書き出しと登録。登録は「計画」を返し、ウィンドウは確認画面に、自動化は警告にする | 2 本ずつある実装 |
| `PresetApplier` | プリセット → 編集状態（古い優先順位の移行を含む） | UI と自動化で食い違う 2 本 |

IMGUI のビューは今どおり毎フレーム状態を読む（MVVM にはしない）。`Changed` を購読するのは、非同期に動くもの
（プレビューの再計算・自動保存・UI の版・シーンへの反映・マスクの重ね表示）だけ。

### 3.4 ヒープの外のメモリの層（Core/Foundation と Infra の境目）

- **確保の窓口を 1 つにする**: `IrocaNativeMemory.Alloc(bytes, 用途)`。用途ごとに今の量と最大値を数える。
- **持ち主の型を 1 つにする**: `SafeHandle` から作る `NativeBuffer`。参照の数・ファイナライザ・スレッド中断時の扱いを
  ここだけに書く。`NativePixels`・色の表・キャッシュの塊はこれの上に作り直す（5 通り → 1 通り）。
- **境目では長さつきの `NativeSpan<T>`** を渡す。範囲の検査は検証用のビルド定義（`IROCA_NATIVE_CHECKS`）のときだけ。
  ループの中は `T*` のまま。
- **`NativeBitMask`**: 1 画素 1 ビット、参照の数で持ち、写しは書くときだけ作る。編集中のマスク・`MaskSnapshot`・
  選択の残す / 強制のビット列・`UvChartMap.MaskOf` を置き換える。RLE の符号化・復号はビットへ直接読み書きする。
- **プールに上限と縮小**: 量の上限（直近の最大量の 1.5 倍ほど）と、手を止めて数秒たったら空きを返す縮小を足す。
- **Unity との境目は Infra だけ**: `NativeArray` に見せる薄い変換（安全ハンドルの作成と解放を含む）と、`TextureIO`
  （原本を読んで `NativePixels` にする・PNG にしてファイルへ流す）。
- **記録と検証**: PerfView に用途ごとの量・最大値・プールの空きと貸し出し中・Mono ヒープの伸びを出す。ウィンドウを
  閉じたとき・リロードの前に残っているものを用途ごとにログへ出す。今の毒入れ・ガードに、返したあとに 0xDD で埋める
  モードを足す（返したあとに読む所を見つける）。

#### 3.4.1 今の後始末の違い（2026-10-27 にソースを読んで整理）

1.3 の「5 通り」の中身。どれも確保は `IrocaNativeMemory.Alloc` / `Free` を通るが、持ち方と後始末の決まりがそれぞれ違う。

| 部品 | 持ち方 | 返しすぎ・二重の後始末 | 中断中（ドメインのアンロード）の扱い | ファイナライザ |
|---|---|---|---|---|
| `NativeBlock`（色の表の中身） | 持ち主 1 つ、`Dispose` | 2 回目は何もしない（`Interlocked.Exchange`） | 解放せず static の一覧に入れてアンロードまで持つ | あり |
| `NativeFloatBuffer`（選択キャッシュの強さ） | 持ち主 1 つ、`Dispose` | 2 回目は何もしない | **自分では見ない**（持ち主の `SelectionCache` が見る） | あり |
| `NativeWorkPool<T>` の塊・`WorkScratch` | 貸し出し（`Rent` / `Return`） | 知らない番地は投げる。**返した後に同じ番地が他へ貸されていると、他人の塊を黙って返す** | 貸し出し中のまま残し、ファイナライザに任せる | 塊ごとにあり |
| `NativePixels`（原本・出力） | 参照の数（`AddRef` / `Release`） | 返しすぎは投げる。解放済みへの `AddRef` も投げる | 解放せず、ファイナライザに任せる（static には持たない） | あり |
| `ColorPalette`（色の表） | 参照の数。中身は `NativeBlock` 2 つ | 返しすぎは**黙って無視**（`_freed` の旗） | `NativeBlock` 任せ | `NativeBlock` 任せ |
| （`SelectionCache` の項目） | 貸し出しの数と「外した」の旗（`Leases` / `Detached`） | 最後に返した側が解放 | 返さない（解放しない） | `NativeFloatBuffer` 任せ |

食い違って危ないのは次の 3 つ。

1. **中断中の扱い。** static に持つもの（`NativeBlock`）と、ファイナライザに任せるもの（`NativePixels`・プール）が
   混ざっている。後者は、アンロードより前に GC が回収すると、まだ並列で読み書きしているスレッドがいる塊を解放しうる
   （`NativeBlock` の注釈が書いている危険と同じ）。
2. **返しすぎの扱い。** 投げるもの・黙って無視するもの・他人の塊を返してしまうものがある。
3. **中断を自分で見ない型がある。** `NativeFloatBuffer` は持ち主が見るのを前提にしていて、単独で使うと守られない。

#### 3.4.2 揃え方（`NativeBuffer` の決まり）

- 持ち方は **「持ち主 1 つ」か「参照の数」の 2 つだけ**にし、どちらも `NativeBuffer`（`SafeHandle` から作る）の上に作る。
  プールの貸し出しも、借りた側に `NativeBuffer`（または貸し出しの札）を渡して、番地だけで返す形をやめる
  （返した後の札は使えなくなるので、他人の塊を返すことが起きない）。
- **返しすぎ・解放後の使用は、いつも投げる**（黙って無視しない）。製品では投げた先でログに出し、検証用のビルド定義
  （`IROCA_NATIVE_CHECKS`）では確保した場所の呼び出し履歴も出す。
- **中断中は解放しない、を 1 か所に書く。** `NativeBuffer.Release` が `CurrentThreadAborting` を見て、static の一覧に
  入れてアンロードまで持つ（今の `NativeBlock` のやり方に揃える）。持ち主の側では中断を見ない。
- ファイナライザは取りこぼしの最後の守りとしてだけ残し、ファイナライザで解放したら用途つきで記録する
  （取りこぼしを見つけるため。閉じたときの残りの報告と合わせて、0 であることをテストで確かめる）。
- `SelectionCache` の「貸し出し中・外した」は、項目が `NativeBuffer` の参照を 1 つ持ち、写す側が参照を足す形に
  置き換える（独自の数え方をなくす）。
- 移すのは 1 種類ずつ別コミット（2-2）。出力は変わらないので abcheck の一致に加え、`IROCA_POOL_POISON=1`・
  `IROCA_NATIVE_GUARD=1`・返したあとに埋めるモードで回す。

#### 3.4.3 ヒープの外に置くもの・置かないもの

決め方: **大きさがテクスチャの画素数に比例するか**で分ける。比例するものは、短い間しか持たなくても置く
（Mono のヒープは山の大きさまで広がって戻らないので、1 回の確保でも残る）。比例しないものは置かない
（数を数える・後始末を書く手間に見合わない）。

| 区分 | 例 | どうする |
|---|---|---|
| 置く: 画素数に比例し、長く持つ | 編集中のマスク（A1）・原本と出力（済み）・選択キャッシュ（済み）・UV チャート（A4）・AI の埋め込みのキャッシュ（B4） | `NativeBuffer` / `NativeBitMask` / `NativePixels` |
| 置く: 画素数に比例し、一時的 | 書き出し（A3）・自動化 API の色替え（A6）・NDMF のマスク展開（B1）・重ね表示の作り直し（A2）・マスクの詰め直し（B3）・作業配列（済み） | プールか `NativeBuffer`。写しは書くときだけ作る |
| 置く（新しく書くとき）: 色の表の大きさ | 色の表の大きさの配列（B5、最大 16MB 前後） | 色の表の大きさは画像によっては画素数に近くなるので置く（3-12） |
| T5 に任せる | 旧の選択・旧の自動調整の中の配列（A5・B6・穴埋めと境界のリスト） | 移さない（P3 で消える）。新しい層は最初から置く |
| 置かない: 表示の大きさで決まる | 拡大表示の切り出し・プロキシ・パネル（表示用は 384² 以下。`RecolorPreview` のパネル、`DetailPreviewView` の切り出し） | 管理配列のまま。ただし案 A のタイル（3.5）はアップロードの置き場をヒープの外に持つ |
| 置かない: 小さく固定 | ゾーンの設定・色の一覧・ヒストグラム・OnGUI の確保・文字列・デバッグの記録の見出し | 管理配列のまま（減らすのは 4-6、優先度は低い） |
| 置かない: Unity の API が管理配列しか受け取らない | 2022.3 の `LoadImage` に渡す圧縮されたファイルの中身（7.2 の 2） | 別の口で同じ画素にできなければ、T3 の例外として理由つきで残す |
| 置かない: ハーネスとの境目 | Core が `NativeArray` を使うこと | Core は `Marshal.AllocHGlobal`（`IrocaNativeMemory`）。`NativeArray` に見せるのは Infra だけ |

まだ決めていないこと:

- `Code/Debug`（`DebugImaging`・`ReproDump`・`DebugCaptureContext`）は開発者だけが使う。T3 の例外にするか、ヒープの外へ
  移すかを、2-2 の前に決める（例外にするなら `pixel_sized_managed_arrays.json` に理由を書く）。
- 表の大きさの数字は式からの見積もり（8 章）。フェーズ 0 の MemoryBench で測った値で、置く順（大きく長く持つものから）を
  確かめる。

### 3.5 プレビューの表示

`PreviewPipeline`（計算）と `PreviewRenderer`（GPU の表示: テクスチャ・差分のシェーダー・拡大表示）に分ける。
差分は描画のときにシェーダーで作る（1-2）。差分のシェーダーの負担は、今も描いている画像の上に四角を 1 枚描き、
2 枚のテクスチャを読むだけなので小さい。代わりに、差分のジョブ・差分のテクスチャのアップロード・管理配列がなくなる。

拡大表示の作り方は 2 案あった。最初は「案 B を計測し、目安に収まらなければ案 A を最適化する」と決めたが、内蔵 GPU を
判定に入れると案 B は目安（CPU 側のメモリが増えない）を原理的に満たせないので、**案 B の試作を省いて案 A にする**
（2026-10-26 決定。7.3 の 2）。案 B の検討は記録として残す。

- **案 B（GPU に置いたまま見る）**: 確定段の出力（作業解像度または取り込み後の寸法）をまるごと GPU に置き、パン・ズームは
  描くときの UV だけで行う。縮小はミップマップ。パン・ズームの追従は原理的に遅れず、拡大表示のジョブがなくなる。
  GPU を使うことで増える負担は、描くことよりも次の 3 つ:
  1. **確定のたびのアップロード。** 4K で 64MB、8K で 256MB を CPU から GPU へ送る。メインスレッドの写しと、
     ミップマップを CPU で作るとその時間がかかる。ミップマップは GPU で作る（RenderTexture の自動生成）。
  2. **VRAM。** 変更前と変更後の 2 枚にミップマップを足すと、4K で約 170MB、8K で約 680MB。シーンビューや
     AI 提案（Sentis も GPU を使う）と取り合う。
  3. **CPU 側の写し。** 読み書きできる Texture2D は、同じ大きさの写しを Unity のメモリ（Mono の外）に持つ。
     アップロード後に写しを手放せる形（読み書きできない GPU 専用のテクスチャへ写す）にする。
- **案 A（CPU で切り出す。今の形の最適化）**:
  - 画像をタイル（例: 表示の 256 画素四方）に分けて、作ったタイルを覚えておく。パンでは新しく見えたタイルだけを作る。
  - 見えている範囲より少し外まで先読みする。
  - 範囲はそのフレームのスクロール位置から決める。古い要求は捨て、最新の要求だけを処理する。
  - タイルはヒープの外の置き場から `LoadRawTextureData(IntPtr)` で上げる（管理配列を使わない）。

**計測と判断の目安**（4K と 8K、MemoryBench と PreviewLatency に場面を足して測る）:

| 測るもの | 案 B を採る目安 |
|---|---|
| 確定 1 回のアップロードでメインスレッドが止まる時間 | 4K で 16ms（1 フレーム）以下、8K で 50ms 以下 |
| 増える VRAM | 4K で 200MB 以下、8K で 800MB 以下 |
| 増える CPU 側のメモリ（Unity のメモリを含む） | 増えない（写しを持ち続けない） |
| パン・ズーム中の描画の時間 | 今と同じか短い |
| 操作から表示までの時間（PreviewLatency） | 案 A より短い |

どれかを外したら案 A にする（目安の数字は 2026-10-26 に決定）。

**内蔵 GPU（iGPU）では VRAM がそのまま CPU 側のメモリになる。** 案 B の増分（4K で約 170MB、8K で約 680MB）は、
T3 で Mono ヒープから減らす量と同じ桁で、ほぼ帳消しになる。そこで:

- 判定は単体 GPU と内蔵 GPU の両方で行う。内蔵 GPU では VRAM の増分を CPU 側のメモリの増分として数える
  （上の表の「増えない」で判定する）。テクスチャの大きさで量は決まるので、内蔵 GPU の機械が無ければ計算で判定し、
  時間だけ単体 GPU で測る。
- 環境で方式を切り替える二本立ては作らない（保守の負担が残るため）。両方で収まらなければ案 A に一本化する。
- 案 B を試すときは、量を減らす工夫も一緒に測る。
  - 変更前のテクスチャは、差分・前後比較・「元を表示」の間だけ置く。
  - T1 がオフの間は、取り込み後の寸法で置く。

## 4. 進め方

ブランチは 0.3.0 と同じ形にする（2026-10-26 決定）: 統合ブランチ `dev-0.4.0`、作業ごとに `dev-0.4.0-<作業>` を
worktree で切る。`develop` は 0.3.x の急ぎの修正用に残し、`dev-0.4.0` へ随時取り込む。T1・T2 も 0.4.0 で出す
（0.3.2 は作らない）。

### 作業する場所（2026-10-26 に確認）

- **クラウドのセッションで回せるもの**（`scripts/cloud/setup.sh` で用意する。手順は `docs/development-setup.md` の 6 章）:
  - ハーネス・build-check・unit-run のビルド
  - golden（53 件のハッシュが Windows と完全一致した）・ソース構造検査・unit-run
  - abcheck
  - dev_safe の回帰（下の dev_safe の同期の後）
  - 視覚レビュー（比較パネルを作って 1 枚ずつ目視する。Linux ではまだ試していない）
  - EditMode テストのコンパイル（`scripts/build-check/IrocaEditorTests.csproj`。2026-10-27 から。実行は手元）

  移すだけの手順と、出力のビット一致の確認（フェーズ 0 の検査づくり・2・3・4 の大半）はクラウドで進められる。
- **手元の Windows の PC でしかできないもの**（Unity が要る）:
  - EditMode テスト・`RuntimeParityTests`・MemoryBench・PerfBench・KernelBench
  - 実機の操作と GPU の計測
  - NDMF・ML（Sentis）・MCP の各ホストでのコンパイル

  クラウドの作業は、区切りごとに手元でこれらをまとめて回す（フェーズの終わりの確認）。
- **手元でやること（フェーズ 0 の残り。2026-10-27 時点）**:
  1. 取り込む: 本体は `git fetch origin` → `git switch dev-0.4.0` → `git pull`、dev_safe は `git -C dev_safe pull`。
  2. 新しいソースの検査と golden を手元でも回す: `python -m pytest scripts/source_checks scripts/golden -q -n 0`
     （改行コードと BOM に依らない作りにしたが、Windows では初めて回す）。
  3. EditMode テスト一式: `.\scripts\Run-EditorTests.ps1`（0.3.0 のときは U2022 で 159 / 166）。2026-10-27 にクラウドで
     書いた次のテストは、コンパイルしか確かめていないので、ここで初めて動く。落ちたら、製品の振る舞いの方が正しいかを
     見てからテストを直す（振る舞いを固定するテストなので、今の製品が正）。
     - `WindowOutputParityTests`（0-2: ウィンドウの確定段 = `SessionRecolor.Apply`、書き出し = 自動化 API の PNG）
     - `OldFormatLoadTests`（0-2: v0.1.0・v0.3.0 のプリセット・セッションのファイル・レシピ）
     - `BenchSeamTests`（計測が名前で探す製品のメンバーがあるか）
     - `RecipeSyncTests`・`WindowRestoreTests`（反射をやめて直接呼ぶ形にした）
  4. 0-4 の残りは無い（2026-10-27 にクラウドで済ませた。MemoryBench・HiResSmoke の名前での反射は、基準の版にも
     当てるための役目なので残し、`BenchSeams` にまとめてふだんのテストで名前を確かめる形にした）。
  5. 0-1 の基準を測る。**2026-10-27 から `.\dev_safe\scripts\run_baseline_0.4.0.ps1` 1 本で、下の全部を順に回して
     dev_safe の `Tests/Baselines/perf-0.4.0-base/` に残せる**（ケースは `make_bench_cases.py` が作る）。以下は中身の説明
     （dev-0.4.0 の今の先頭で。製品のコードは 0.3.x と同じで、変えたのは `BuildSelectionKey` を
     internal にしただけ）。1 回の起動に 1 種類ずつ:
     - PerfBench: `python dev_safe/scripts/perf_bench.py unity-cases` → `.\scripts\Run-EditorTests.ps1 -PerfCases
       $env:TEMP\iroca-perf\unity\unity_cases.json` → `python dev_safe/scripts/perf_bench.py unity-report <出力>\perf.jsonl`。
     - KernelBench: `-KernelBench`。
     - MemoryBench: 0.3.0 のときのケースの JSON（dev_safe `docs/memory-0.3.0-plan.md`）で 4K と 8K を `-MemCases`。
       2026-10-27 に足した場面（ケースに `"scenario"` を書く。`maskPaint`・`recipeBuild`・`autoTune`・`mcpRecolor`）も
       4K と 8K で 1 回ずつ。例: `{"name":"4k-mask","png":"…","zones":"…","scenario":"maskPaint"}`（steps は 3〜4 で足りる）。
     - 8K の 2 手目の遅さ: 各手の `sub`（20ms 以上の段の時間）を基準 7d2bf6f と段ごとに比べて切り分ける。ただし今の
       テストは 7d2bf6f の Code ではコンパイルできない（`WorkingResolution.Size` の引数が 465cb3c で変わった）。
       7d2bf6f の側は、19aa342 の `MemoryBenchTests.cs` だけを置いたテストのフォルダで回す（クラウドでコンパイルを確かめた）。
       今の側は今のテストで回す。同じテストの写しを両側には当てられない。
     - HiResSmoke: 8K を `-HiResCases`。
     - 結果は dev_safe の `Tests/Baselines/perf-0.4.0-base/` に、コミットの番号・PC・Unity の版と一緒に置く。
  6. まだ書いていない計測: MemoryBench のメッシュの部位の右クリック（シーンにテクスチャを使うメッシュが要る）と AI 提案の
     準備とクリック（Sentis とモデルが要る）、PreviewLatency の再生（ウィンドウを表示して OnGUI を回す必要があり、
     batchmode では描かれない）。どれも手元の Unity で動きを見ながら作る。
  7. 交互 A/B の前に、基準の Code で今のテストがコンパイルできるかを `bash scripts/build-check/ab-compile.sh <基準>` で
     確かめられる（クラウドでも回る）。0-2 のテストが internal の口を使うので、基準は 567b0a0 以降にする。
- **手元で確かめること（フェーズ 1。2026-10-27〜）**:
  1. 取り込んでから EditMode テスト一式（`.\scripts\Run-EditorTests.ps1`）。新しい `DetailResolutionTests`（1-1）が通ること。
  2. 1-1 の見た目: Max Size を元の画像より小さくしたテクスチャ（例: 4096 の画像を Max Size 2048）を開き、拡大する。
     - 「なめらか」の右に「元の解像度」が出て、ツールチップが出る。オフ（既定）は今までと同じ見え方。
     - オンにすると拡大表示が細かくなる（4096 の画素）。前後比較・「元を表示」・差分表示・マスクの重ね表示・
       「アバターの色」も同じ細かさで、位置がずれない。オフへ戻すと元の粗さに戻り、色替えはやり直さない
       （生成中の表示が出ない）。
     - 書き出した画像・シーンのプレビューは設定で変わらない。
     - ウィンドウが狭いとき、見出し行のリセットが隠れない（ズーム率の「(Ctrl+スクロール)」が省かれる）。
- **dev_safe の同期（2026-10-26 に済み）。**
  - dev-0.3.0 の worktree の dev_safe 複製にあった 0.3.0 の作業（基準の作り直し・ホールドアウトの入れ替え・
    `generalization_report.py` ほか）を、本体の dev_safe の `main` へ取り込んだ（d3e3e68）。`abcheck.py` の `CRASH_RC` を
    Linux でも合うように直した（869f572）。開発側へ移した 14 被写体の凍結ロジットを手元の Windows で作って足した（7957d6c）。
    `test_dark_target_brightness` の作業ファイルを追跡から外した（d5f3e72）。
  - クラウドと手元の両方で dev_safe に書くときは、片方で push してから、もう片方で pull する。
- **クラウドだけで 1 被写体のワンショットの床が落ちた件（2026-10-26 に直した）。**
  - 選択の出力は Windows と同じで、違ったのは採点の側の「GT の許容」の画素数（Windows 631,407・クラウド 537,892）。
  - 原因は測定器（dev_safe のテスト側の Python）の OkLab。float32 の `** 2.4` と `np.cbrt` の最下位ビットが OS・CPU で違い、
    0.01 の bin の境目に乗る色の bin が入れ替わっていた。`dev_safe/scripts/diag_gt_tolerance_env.py` で、入力・画素は同じで
    最初に違うのが OkLab の値だと突き止めた。float32 と float64 の違いだけでも 33 被写体のうち 11 で許容の画素数が変わる、
    計算の細かい差に弱い作りだった。
  - 直したこと（ユーザー判断で案 A）:
    - `colorspace.oklab_from_u8_portable` を足した。線形化の 256 値は `decimal` で求め、立方根は四則演算だけの
      ニュートン法で求める。Windows とクラウドで、この計算・bin の割り当て・許容の結果のハッシュがすべて一致した。
    - `gt_tolerance` の bin の割り当てをこれに替え、規則の版を 2 にした。
    - 許容を使う基準（ワンショット・追加操作後・汎化の物差し）は、製品側の値（IoU・precision・recall・島・
      自動調整の値・診断）を Windows で作った版のまま残し、許容から決まる値だけを差し替えた。design IoU が動いたのは
      ワンショット 99 件中 28、追加操作後 33 件中 10、汎化の物差し 198 件中 54。
    - `holdout_eval.py` は記録に規則の版を書き、前回との比べを同じ版の記録だけにした。見た回数は版に関係なく数える。
  - **残る環境の違い**: クラウドの AI 提案の埋め込みは Linux で作るので、証拠つき自動調整の結果が Windows と数画素ずれる
    ことがある（ズームしての再推論はモデルをその場で回すため）。床の余裕の中に収まるので、テストは通る。
    ただし、AI 提案の証拠を使う基準をクラウドで作り直すと、そのずれが基準に入る。**作り直しは手元の Windows で行うか、
    今回のように製品側の値を残して差し替える。**

### フェーズ 0: 安全網と基準の計測（製品のコードは変えない）

- [ ] 0-1 **基準を測る**（dev_safe に記録する）。**2026-10-27 に手元の Unity で取った**（dev_safe `Tests/Baselines/perf-0.4.0-base/`、
  まとめは同じ所の README）。手放した後も残る Mono ヒープの増えは、8K でマスクの塗り +1.6GB・自動調整 +1.9GB・
  自動化 API の書き出し +1.5GB・マスクつきのレシピのビルド +1.4GB（ウィンドウのプレビューは +0.1GB）。
  残り: メッシュの部位・AI 提案・PreviewLatency の再生（手元で作る）と、8K の 2 手目の遅さの切り分け:
  - MemoryBench: 4K と 8K で今のケースを測る。さらに次のケースを足す。
    - マスク N 層のペイントと Undo / Redo
    - マスクつきの NDMF のビルドとシーンのプレビュー
    - 自動調整
    - メッシュの部位の右クリック
    - AI 提案の準備とクリック
    - MCP の色替え

    （2026-10-27: マスクの塗りと Undo / Redo・マスクつきのレシピのビルド・自動調整・MCP の色替えは
    `MemoryBenchTests.Scenarios.cs` に書いた。シーンのプレビュー・メッシュの部位・AI 提案は手元で足す。HiResSmoke が
    シーンのプレビューを 1 回通している）
  - PerfBench・KernelBench。
  - PreviewLatency: パン・ズーム・スライダーのドラッグを決まった手順で再生できるようにして測る。
  - 0.3.0 から持ち越した速さの懸念: 8K を元の解像度で編集した 2 手目（選択キャッシュに当たる手）が基準より遅い
    （dev_safe `docs/memory-0.3.0-plan.md` の「速さの懸念」の 3。未着手）。基準を測るついでに段ごとの時間で切り分ける。
- [x] 0-2 **振る舞いを固定する EditMode テスト**（2026-10-27 に全部書き、同じ日に手元の Unity 2022 で 290 件中 274 成功・失敗 0・スキップ 16（計測用と -nographics で見られない描画）。Unity とハーネスの一致 25 / 25）:
  - ウィンドウの確定段の出力 = `SessionRecolor.Apply`（同じセッション・マスクつき）。**（`WindowOutputParityTests`）**
  - ウィンドウの書き出し = 自動化 API の PNG。**（`WindowOutputParityTests`）**
  - EditorPrefs / SessionState のキーの一覧。**（済み 2026-10-27。ソースから決まるので、Unity の要らない
    `scripts/source_checks/test_editor_prefs_keys.py` にした）**
  - ウィンドウとビューの保存されるフィールド名の一覧。`[FormerlySerializedAs]` なしで名前が変わったら落とす。
    **（済み 2026-10-27。同じく `test_serialized_field_names.py`。JSON の項目名・レシピ・コンポーネントも含めた 37 型・284 個）**
  - 古い版で保存したレシピ・セッション・プリセットの読み込み結果。**（`OldFormatLoadTests`。v0.1.0 のプリセットの優先度の
    並べ替え、v0.3.0 のプリセット・セッションのファイル・レシピ、旧 bitpack のマスク。自動化 API がプリセットを並べ替えない
    今の食い違いも、4-3 で直すまで固定した）**
- [x] 0-3 **構造の検査**（`scripts/source_checks/`）（2026-10-27 に済み。今ある違反: 層の向き 25 件・層をまたぐ型 1・
  画素数の大きさの管理配列 106 件。どれも一覧に載せ、一覧は減らすことしかできない）:
  - `test_layer_dependencies.py`: フォルダ間の許可表。今の違反は一覧に載せ、一覧は減らすことしかできない。
  - `test_no_pixel_sized_managed_arrays.py`: `new (bool|byte|int|float|Color32|ulong)[…]` のうち画素数の大きさの
    もの。これも許可の一覧つきで、一覧は減らすことしかできない。
  - `test_event_subscription_symmetry` の長生きのイベントの一覧に、次の 6 つを足す: `duringSceneGui`・
    `TargetChanged`・`BeforeApply`・`OnDrawFoldout`・`OnCaptureComplete`・`OnReport`。
- [x] 0-4 **移動に弱い道具を先に直す**（2026-10-27 に済み）:
  - `tools/harness_scope.py` の `**` の扱い。**（済み d3e112c）**
  - `test_algorithm_constants` の対象をサブフォルダまで広げる。**（済み de14c1b。Core と `MaskSuggest/Ops` の中を既定にし、
    外すファイルは理由つきの一覧にした）**
  - 名前の文字列で反射して呼んでいるテストを、internal の窓口を通す形に替える（`test_selection_key_audit`・
    RecipeSyncTests・WindowRestoreTests・MemoryBenchTests・HiResSmokeTests）。**`test_selection_key_audit` は済み
    （fcf3a5a。abcheck 1397 / 1397）。RecipeSyncTests・WindowRestoreTests は d8bf8c8 で直接呼ぶ形にした。
    MemoryBench・HiResSmoke は基準の版にも当てる計測なので名前で探すまま残し、`BenchSeams` にまとめて、ふだんのテスト
    （`BenchSeamTests`）で名前が今の製品にあることを確かめる（2f07a60）。**
  - dev_safe 側でハーネスの約束事を一覧にする: コマンドラインの形、`PHASE` / `SUBPHASE` / `AUTOTUNE` / `SELKEY`
    の出力、abcheck が名前で呼ぶ private メソッド。**（済み。dev_safe `docs/harness-contract-2026-10-27.md`）**
- [x] 0-5 **設計の記録** `docs/architecture.md` を書く（2026-10-27 に済み。AGENTS.md から参照する）（3 章の層・依存の決まり・メモリの持ち主の決まり）。以後のセッションはこれを正にする。
- [x] 0-6 **測定器の環境依存を点検する**（2026-10-26 の件の続き）。**2026-10-27 に済み。直す所は無かった**
  （dev_safe `docs/env-dependence-audit-2026-10-27.md`）。素朴な基準線（ΔE の球）は境目から十分離れていて、品質ゲートの
  指標もしきい値から離れている。AI 提案の推論の違いは、低解像度のロジットの符号が変わる画素が 0、汎化の物差しの製品側の
  値で最大 1.2e-5 で、採否の目安より 2 桁以上小さい。埋め込みを Windows とそろえる仕組みは作らず、基準の製品側の値は
  これまでどおり手元の Windows で作る。
  - `colorspace` の他の関数（ΔE・OkLab の L・HSV）も float32 の `np.power` / `np.cbrt` を使う。しきい値で数える所
    （品質ゲート・ΔE の球）は、境目の画素が環境で入れ替わりうる。値への効き方を測り、要る所は環境に依らない計算へ替える。
  - クラウドで AI 提案の埋め込みを Windows と同じにする方法（Windows で作った埋め込みを置く・推論を決定的にする）を検討する。
    T5 の採否は design IoU で決めるので、手元とクラウドで同じ数字が出ることが要る。

### フェーズ 1: 表示の 2 件（T1・T2）。今の構造の上で小さく直す

- [ ] 1-1 **T1 拡大表示の解像度の設定**（2026-10-27 にクラウドでコードとテストを書いた。コンパイル・ソースの検査・
  マニュアルの組み立てまで確かめた。残りは手元の Unity でテストを回すことと、見た目の確認。下の「手元で確かめること」）
  - 設定は `PreviewView.DetailFullResolution`（キー `Iroca.PreviewDetailFullResolution`）。各ウィンドウは描く前に
    `SyncDetailResolution` で土台を設定に合わせる。確定段が走っている間に切り替えても、受け取るときに合わせる。
  - テストは `DetailResolutionTests`。見えている範囲から切り出しを決める所は GUI の中でしか動かない
    （`GUI.skin` を読む）ので、切り出しの予約を `DetailPreviewView.ScheduleCrop` に分け、範囲を指定して呼ぶ。
  - プレビューの見出し行の「なめらか」の隣にトグルを置き、既定はオフ（2026-10-26 決定。ツールチップ必須）。この PC のいろか全体の表示の設定として
    EditorPrefs に新しいキーで持つ。表示だけの設定なので、NDMF・書き出し・自動化には効かない（以前の
    「元の解像度で編集」と違う点）。
  - オンなら `DetailSource` / `DetailOutput` は作業解像度のもの。縮めた原本・縮めた出力は作らない（その分メモリが減る）。
    オフは今と同じ。
  - 切り替えたら拡大表示を捨てて作り直す。オフへ戻すときは、色替えをやり直さず、確定段の出力から縮めたものを作る。
  - 文言を直す: `ComparisonModeTooltip` と `PeekOriginalTooltip` は「Max Size の大きさで表示」と書いている。
    マニュアル（08-preview ほか）と CHANGELOG も直す。
  - テスト: 両方の設定で、拡大表示の寸法と、変更前・変更後・マスクの重ね表示の範囲がそろうこと（EditMode）。出力は変わらない。
- [ ] 1-2 **T2 差分を GPU で描く**
  - 隠しシェーダー（`Hidden/Iroca/PreviewDiff`）: 変更前と変更後を同じ UV で読み、8 ビットの値で RGB の差の合計が
    10 を超えたら (255,220,0,160)、それ以外は透明。今の `BuildDiffPixels` と同じ定義。テクスチャを sRGB として
    読むと値が変わるので、線形で作るかシェーダーで戻す。
  - 全体は `previewTexture` と `rawPreviewTexture` から、拡大表示は切り出しと同じ範囲の変更前の切り出しから描く。
    変更前の切り出しは、今も差分表示中のジョブが作っている。それを「元を表示」と共用のテクスチャに上げる。
    切り出しの外側は全体の 2 枚から同じシェーダーで描く。
  - 消すもの: `_diffJob`・`BuildDiffPixels`・`diffTexture`・`detailDiffTexture`・`ApplyPendingDiff`・差分のための管理配列。
  - 切り出しの範囲をそのフレームのスクロール位置・倍率から決め、上下左右に少し余白を足す（小さなパンで端が粗く
    ならないように）。
  - 配布: `.shader` を `Code/UI/` に置けば追跡ファイルとして zip に入る（`Build-VpmPackage.ps1` は `Code/` の追跡
    ファイルを全部入れる）。`Shader.Find` で引く。インストーラ（unitypackage）にも入ることを
    release-smoke-test で確かめる。
  - テスト:
    - 合成した画像の組で、シェーダーの出力（RenderTexture に描いて読み戻す）が CPU の定義と一致すること（EditMode）。
      CPU の定義はテストの中にだけ残す。
    - 実機で、4K・8K の画像を差分表示にしてパン・ズーム・スライダーのドラッグを激しく行い、ずれと古い差分が
      出ないこと（release-smoke-test に手順を足す）。

### フェーズ 2: メモリの土台（T3 の基礎）

- [ ] 2-1 B1: `MaskRle` に詰めたビットへ直接復号する口を足し、NDMF のビルドとシーンのプレビューで使う
  （小さな変更で大きく効く。最初にやる）。
- [ ] 2-2 3.4 の `NativeBuffer`・用途つきの確保・プールの上限と縮小・返したあとに埋めるモード・閉じたときの残りの報告。
  既存の 5 通りをこの上へ移す（1 種類ずつ別コミット）。
- [ ] 2-3 `NativeBitMask` と、RLE からビットへ・ビットから RLE への直接の符号化。
- [ ] 2-4 Infra の境目: `NativeArray` への変換、`TextureIO`。
  - 読む: `GetPixelData` から `NativePixels`。
  - 書く: `EncodeNativeArrayToPNG` で PNG にし、小さな置き場を通してファイルへ流す。
- [ ] 2-5 PerfView に用途ごとの量を出す。
- 確かめること:
  - `EncodeNativeArrayToPNG` を別スレッドで呼べるか（今の `EncodePng_RunsOnBackgroundThreadAndMatchesMainThread` を広げる）。
  - `LoadImage` の代わりに管理配列を使わない読み込み方があるか（Unity 6 の `ReadOnlySpan` の口、
    `UnityWebRequestTexture` など）。今と同じ画素になるときだけ替える。出力が変わり、直しても合わせられないなら
    見送り、圧縮されたファイルの中身の分は T3 の例外として残す（2026-10-26 決定）。
  - Mono の `FileStream.Write(ReadOnlySpan)` は管理配列へ写すので使わない。

### フェーズ 3A: Core の作り替え（T4 の Core と、Core に残る T3。出力を変えない）

各手順を 1 つ以上のコミットにする。どれもビット一致（abcheck・golden・`test_palette_parity`・`test_selection_key_audit`）。
T5 で消える部分は作り直さない（1.5）。

- [ ] 3-1 `ZoneAutoTuner` が文言でなく項目の列挙を返すようにし、ハーネスのスタブを消す（Core → UI の依存をなくす）。
  入力を専用の記録にし、`IrocaSessionState` 丸ごとはやめる（T5 の自動調整と同じ窓口にするため）。中身は変えない。
- [ ] 3-2 `DebugCaptureHooks` を Core の部分と UI の部分（開閉の描画）に分ける。
- [ ] 3-3 Core でないものを外へ移す: `TextureSlot`・`PreviewJobProgress`・`MeshRaycast` は Infra へ、
  `IrocaConsts.Layout/MenuPath` は UI へ。常に true の `EnableFloodFill` を消す。
- [ ] 3-4 Foundation を切り出す: 並列度・`OpaqueAlphaMin`・`CoreMemory.ReleaseIdle`。今の名前は転送役として残す。
- [ ] 3-5 移すだけの切り出し: 色空間（OkLab を公開し、Automation の写しを消す）・`PixelHsv`・色の表・
  ヒストグラム・ビット列・ぼかし。定数の台帳の節も一緒に移す（台帳の行は消さずに移す）。
- [ ] 3-7a **選択と再着色の境目を決める（T5 の前提。フェーズ 3A でいちばん先に済ませたい手順）。**
  - `ProcessPixelsArrayCore` のうち、選択（照合 → 強度 → ぼかし・選択キャッシュへの保存まで）が後段へ渡している値を洗い出し、
    `SelectionResult` にまとめる。
  - パイプラインを「旧の選択」「マスクの契約（除外・含める）」「再着色の段」の 3 つの呼び出しに分ける。
  - 旧の選択は中身を書き換えずに `Selection/Legacy/` へ移す（段への分割はしない）。`ColorZone` の照合と `SelectionKey` も
    `Legacy` へ移し、ハーネスの反射呼び出しを直接の呼び出しにする。`ColorZone` はデータだけになる（照合のキャッシュは
    `[NonSerialized]` なので、移しても保存の形は変わらない）。
  - `test_zone_cache_invalidation`・`test_recolor_settings`・`test_selection_key_audit` を同じコミットで新しい場所に向ける。
- [ ] 3-7b 再着色の側を段に分ける。
  - 先に `RunContext` / `ZoneState` / `ZoneBuffers` を入れ、段はパイプラインの順に 1 段 1 コミットで分ける
    （無彩の固め・デコンタミ・再着色の統計・混色帯・再着色の核・合成・無彩のフチ消し）。
  - 最後に残る `RecolorPipeline` は約 200 行で読めるようにし、デバッグと時間計測は段の境目の観察者にする。
  - 色の表あり・なしの二重実装は、段ごとに 1 本にできるかを KernelBench で速さを見ながら決める。
- [ ] 3-8 切り出しの検証経路（`PreviewParityCache` と切り出しの処理。製品は使っていない）を消す（2026-10-26 決定）。
  ハーネスの `--ffcheck` など使う側のモードと、dev_safe のテスト・abcheck のモードも同じ時に消す。
- [ ] 3-12 Core に残る管理配列のうち、T5 の後も残るものをなくす: `UvChartMap`（A4）、色の表の大きさの配列（B5）。
  旧の選択・旧の自動調整の中の配列（A5・B6・穴埋めと境界のリスト）は移さない（T5 の P3 で消える）。
- [ ] 3-13 `Harness.csproj` の取り込みをフォルダ単位にする。
- フェーズの終わり: Unity の `RuntimeParityTests`（Mono とハーネスの一致）、MemoryBench、PerfBench、KernelBench。
- 前の版の計画にあった次の手順は、T5 で旧ごと消えるのでやらない: 照合の作り直し（`ZoneMatcher`）、`ZoneAutoTuner` の
  中身の作り直し（`SelectionSimulator` など）、FloodFill と Achroma の連結成分のラベル付けの一本化。

### フェーズ 3B: 選択・自動調整の再設計（T5）

設計は dev_safe の `docs/selection_redesign_0.4.0.md` が正。ここには、この計画の中での置き場所と決まりだけを書く。

- **始める時期**: P0 はフェーズ 2（メモリの土台）の後に始めてよい（新しいファイルだけで、L1 の領域の地図を作って測る）。
  P1 は 3-7a（境目）の後。P3 の UI とレシピの整理は、フェーズ 4 の Application 層（`EditingSession` など）の上に作る。
- **置き場所**: `Code/Core/Selection/Regions`・`Material`・`Decision`（3.2）。旧の段と共有しない。
- **切り替え**: P1・P2 の間は、新旧の切り替えをハーネスの引数と開発者向けの設定（EditorPrefs）にだけ持たせ、ゾーン
  （レシピ）には保存しない（P3 で旧を消すので、保存の形に残さない）。
- **採否**: 改善サイクルと汎化の物差し（`generalization_report.py`・ホールドアウト・回帰 FULL・縮小プレビュー・視覚レビュー）。
  旧の値を床にする。成功の基準は設計の下書きの 5.3。
- **P0 の判断**: 領域の純度（正解の部品が領域の和で表せるか）が低ければ、設計を見直すか、T5 を 0.4.0 から外す（7.3 の 1）。
- **新しいコードの決まり**: 画素数に比例する配列は管理配列で作らない（2 章の 8）。定数は台帳に書き、選択・自動調整で
  調整する値は 10 個以下（設計の下書き 4.4）。
- **P3 で消すもの**: `Selection/Legacy`・`AutoTune/Legacy`・関係する定数と台帳の行・詳細設定の項目・番兵のテスト
  （仕様の文に書き直したものを除く）。レシピを開いたときの移行（戻せるものは戻し、戻せないものはダイアログ）を足す。

### フェーズ 4: エディタ側の層（T4 のエディタ側と、エディタ側に残る T3）

- [ ] 4-1 移すだけ:
  - `ExportPipeline` を Infra へ移す（`TextureIO` の土台にする）。
  - `Localization` を Text フォルダへ移し、機能ごとの partial に分ける（呼び出し側は変えない）。
  - キーの一覧 `IrocaPrefs` を作る（文字列は同じ）。
  - `IrocaWindow` にある static の道具（`IsReadable`・`RecordAsSeparateUndo` など）を外へ出す。
  - `UnitRun.csproj` のパスを同じコミットで直す。
- [ ] 4-2 部品を切り出す。ウィンドウは当面、切り出した部品へ転送する。順は次のとおり:
  1. `SourcePixelCache`
  2. `RecolorInput`
  3. `EditingSessionRegistry`
  4. `EditingSession`: 今の保存される `_session` を包むだけで、フィールド名は変えない。
  5. `SessionPersistence` と自動保存
  6. `MaskWorkspace`: マスクを `NativeBitMask` に替える（A1・A2・B3）。
  7. ゾーンの操作
  8. `Changed` が `previewDirty` の通知の役を引き継ぐ
- [ ] 4-3 並んだ経路を 1 本にする:
  - `AutoTuneService`: `test_autotune_apply_single_source` を同じコミットで直す。Core の自動調整の中身は T5 で替わるが、
    流れ（証拠の待ち・適用・Undo・自動化からの呼び出し）は残るので、先に 1 本にしておく。
  - `AiSuggestBroker`
  - `ExportService`: ヒープの外の書き出し（A3・A6）。一括書き出しをメインスレッドの外へ出すかも検討する。
  - `RegistrationService`
  - `PresetApplier`: 自動化の古い優先順位の扱いを UI に合わせる。わざと振る舞いを変えるので、別コミットにして
    CHANGELOG に書く。
- [ ] 4-4 プレビューを `PreviewPipeline` と `PreviewRenderer` に分ける。
  - 3.5 の案 A（CPU のタイル・先読み・最新の要求だけ・管理配列を使わないアップロード）で作る。パン・ズームの追従を
    PreviewLatency で、今の作りと比べる。
  - 残る管理配列（概要段・表示用・切り出し）をなくす。
  - 再計算は当面ウィンドウの描画から進める（`EditorApplication.update` へ移すと、隠れている間の CPU の使い方と
    シーンへの反映が変わる）。
- [ ] 4-5 AI 提案の前処理・後処理・埋め込みのキャッシュをヒープの外へ移す（B4）。
  - Sentis 2.x の `Tensor<float>(TensorShape, NativeArray<float>)` と `DownloadToNativeArray` を使う。
  - Inference Engine 2.2 でも使えるか確かめる。
  - Sentis の asmdef は unsafe を許していないので、ポインタは今どおり `SamNativeOps` に置く。
- [ ] 4-6 OnGUI の細かい確保（毎フレームの `new GUIContent`・プレビューの要求ごとの LINQ）を減らす。優先度は低い。

### フェーズ 5: Undo の作り直し（0.4.0 に入れる。2026-10-26 決定）

- 文書を `HideAndDontSave` の ScriptableObject に移し、Undo の対象にする。ズームなど表示の状態が巻き戻らなくなり、
  `s_viewState*` の回避が要らなくなる（`UndoRestoreSignalTests` が ScriptableObject を対象に使っているので確かめられる）。
- マスクの履歴は版の番号だけを保存し、中身はヒープの外の RLE の履歴に持つ（B2 の、Undo のたびの base64 文字列の
  読み直しがなくなる）。
- **スクリプトの再読み込みをまたいだ Undo は今と同じに保つ。** 今はマスクの文字列がウィンドウごと Undo に載るので、
  再読み込みの後もマスクのストロークを取り消せるはず（フェーズ 0 で確かめ、固定するテストにする）。ヒープの外の履歴は
  再読み込みで消えるので、再読み込みの前に一時ファイル
  （`Library/` の下）へ退避して読み戻す。退避できなかったときは `MaskFileStore` の最新の状態へ戻し、取り消せないことを
  知らせる。
- 利用者の作業状態の保存と復元に直接かかわるので、他のフェーズより確かめることが多い。PersistenceTests・
  WindowRestoreTests・SampleUvUndoTests・SeparateUndoTests・UndoRestoreSignalTests・UndoBenchTests に、
  再読み込みと Play をまたぐ場面を足す。

### フェーズ 6: asmdef で固める（0.4.0 に入れる。2026-10-26 決定）

- `com.yukkuri-aoba.iroca.Editor.Core`（Core と MaskSuggest/Ops。unsafe 可）と
  `com.yukkuri-aoba.iroca.Editor.Services`（Infra・Application・Text と MaskSuggest の UI でない部分）を足す。
- `com.yukkuri-aoba.iroca.Editor` は UI と Automation のまま残す。ウィンドウの型の識別子（利用者のレイアウトに保存される）・
  `-executeMethod`・MCP / Debug の asmdef の参照を変えないため。NDMF と Sentis は Services を参照する。
- `InternalsVisibleTo` を足す。`DebugCaptureHooks` の分割は 3-2 で済ませておく。EditorTests の asmdef にも参照を足す。
- **`scripts/build-check` を asmdef ごとの csproj（ProjectReference つき）にする。** これをしないと CI は境目を見ない。
- NDMF・ML（Sentis）・MCP の各ホストで、Unity の batchmode でコンパイルを確かめる。

### フェーズ 7: 仕上げとリリース

- [ ] 許可の一覧が 0 になった検査は、一覧なしの決まりにする。
- [ ] 文書を直す: `docs/testing-architecture.md`（パス）・`docs/algorithm-constants.md`（ファイルの節）・
  `docs/development-setup.md`・`docs/architecture.md`、マニュアル、CHANGELOG、`docs/release-smoke-test.md`
  （差分の追従・解像度の設定・メモリの確認を足す）。
- [ ] 0.3.1（`v0.3.1` タグ）と abcheck で比べる。わざと変えたもの以外は一致すること。T5 は出力を変えるので、
  この比べは T5 の P1 を `dev-0.4.0` へ入れる前（フェーズ 3A・4 の区切り）に行い、結果を残す。
- [ ] T5 の P3 で消えた詳細設定と、新しい最終調整の操作をマニュアルに書く。
- [ ] フェーズ 0 の基準と、メモリ・速さ・表示までの時間を比べて CHANGELOG に書く。

## 5. 終わったと言える条件

| # | 条件 |
|---|---|
| T1 | トグルがあり、ツールチップがある。オンにすると、元が 4096・Max Size が 2048 のテクスチャを拡大したとき 4096 の画素で見える。オフは今と同じ。出力は変わらない |
| T2 | 差分は表示中の 2 枚から描画のときに作る（別のジョブで作る差分が残っていない）。4K・8K で、激しいパン・ズーム・スライダーのドラッグの間も、古い差分や位置のずれた差分が出ない（実機の手順で確認） |
| T3 | `Code/**` に、画素数に比例する管理配列の確保が無い。例外は一覧に理由つきで書いたものだけ（7.2 の 2 で残すと決めた場合の、2022.3 の `LoadImage` に渡す圧縮されたファイルの中身など）。MemoryBench で、Mono ヒープの最大値が 4K と 8K でほぼ同じ（テクスチャの大きさに比例しない）。ウィンドウを閉じたら、ヒープの外の量が開く前に戻る |
| T4 | 依存の向きが検査（または asmdef）で守られ、違反の一覧が 0。パイプラインが段を順に呼ぶ約 200 行で読める。色替えの入力の組み立て・自動調整・登録・書き出し・プリセットの読み込みがそれぞれ 1 本。Core に UI と UnityEditor の参照が無い |
| T5 | dev_safe の設計の下書きの 5.3: 汎化の物差しで旧以上（アバター別で −0.02 を超える悪化なし）、ホールドアウトで旧以上、2 トーンの髪の別色が塗り分けられ同じ色の別部品は一緒に塗られる（画で確認）、製品の契約のテストは全部通る、選択・自動調整の定数が約 160 → 40 以下で調整する値は 10 個以下、4K のプレビュー・自動調整が旧と同程度以下。旧の選択・旧の自動調整のコードが残っていない |
| 共通 | T3・T4 のコミットでは、わざと変えたもの以外の出力が 0.3.1 とビット一致（T5 を入れる前に確かめる）。PerfBench・KernelBench で 5% を超えて遅くならない。表示までの時間は同じか短い |

## 6. 危ないところと手当て

| 危ないところ | 手当て |
|---|---|
| 保存されるフィールド名を変えて、利用者の作業状態が消える・レシピを空で上書きする | 0-2 のフィールド名の固定テスト。`EditingSession` は今の `_session` を包むだけにし、中身の名前は変えない |
| Mono と .NET 8 で浮動小数の結果が違う（式を別のメソッドや入れ物へ移したとき） | `RuntimeParityTests` をフェーズ 3 の手順ごとに回す（ハーネスの一致だけでは足りない） |
| 作り替えで Mono の Debug 最適化のときに遅くなる | 2 章 6。KernelBench・PerfBench をフェーズの終わりに回す |
| 早めに手放していたメモリを手放さなくなり、8K の最大量が増える | `ZoneBuffers` の手放す所を今と同じにする。MemoryBench の 8K で確かめる |
| プールの貸す順が変わって、書く前に読む所が表に出る | 毒入れ・ガード・返したあとに埋めるモードで回す |
| 色の表あり・なしの片方だけを移して食い違う | 段ごとに両方を同じコミットで移す。`test_palette_parity` |
| dev_safe の約束事（ハーネスの出力の形・private メソッドの名前）を知らずに壊す | 0-4 で一覧にしてから動かす |
| 手順ごとにハーネスの DLL が変わり、キャッシュが冷えて回帰（約 7 分半）と abcheck（約 8 分）が毎回かかる | 移すだけの小さな手順はまとめて検証してよい（コミットは分けたまま、検証は数コミットごと）。ただし段の分割（3-7）は 1 段ごとに検証する |
| asmdef の境目は、任意のパッケージ（NDMF・Sentis・MCP）が入ったホストでしか確かめられない | フェーズ 6 で 3 つのホストの batchmode コンパイルを手順にする |
| 定数を移して台帳の由来の記録が消える | 台帳の行は消さずに移す。`test_algorithm_constants` の対象をサブフォルダまで広げる（0-4） |
| 長い作業の間に 0.3.x の修正が必要になる | `develop` で直して `dev-0.4.0` へ取り込む |
| T5 の P0 で領域の純度が足りず、設計が成り立たない | P0 の数字で続けるかを決める（7.3 の 1）。続けない場合も、T1〜T4 は単独で出せるように、T5 の変更は `dev-0.4.0-t5` の枝に留め、P1 の採用が決まるまで `dev-0.4.0` へ入れない |
| T5 と 3A が同じファイル（パイプライン）を同時に触ってぶつかる | 3-7a（境目）を先に `dev-0.4.0` へ入れてから T5 の P1 を始める。T5 は新しいフォルダだけを触る |
| 旧の選択の段が守っていた細部（縮小プレビューでの効き・継ぎ目・灰のツヤ・塗り残しの点）を新方式が落とす | 設計の下書きの 5.2: 番兵のテストを「何を守っていたか」の仕様の文に書き直し、新方式の受け入れ条件にする（P0 で仕分ける） |
| 旧レシピの見た目が変わる・詳細設定が消える | 設計の下書きの 4.5: 戻せるものは戻し、戻せないものはゾーンごとの一覧のダイアログで知らせる。CHANGELOG とマニュアルに書く |

## 7. 決めること

### 7.1 決まったこと（2026-10-26）

1. T1 の設定は、見出し行の「なめらか」の隣のトグル。既定はオフ（ゲームに出る大きさ）。
2. ブランチは `dev-0.4.0` を統合ブランチにする（0.3.0 と同じ形）。
3. T1・T2 も 0.4.0 で出す（0.3.2 は作らない）。
4. フェーズ 3 の間は、アルゴリズムの改善を止める。
5. Undo の作り直し（フェーズ 5）を 0.4.0 に入れる。
6. asmdef の分割（フェーズ 6）を 0.4.0 に入れる。
7. 切り出しの検証経路（`PreviewParityCache`）を消す。
8. 拡大表示は案 B（GPU に置いたまま見る）を計測し、目安に収まらなければ案 A（CPU の切り出し）を最適化する。

### 7.2 続けて決まったこと（2026-10-26）

1. 案 B を採る目安の数字は 3.5 の表のとおり。内蔵 GPU では VRAM が CPU 側のメモリになることを判定に入れる（3.5）。
2. 書き出しの読み込みの管理配列は、別の読み込み方で今と同じ画素になるときだけ替える。出力が変わり、直しても
   合わせられないなら見送る。
3. 並べ方: 0 → 1 と 2 を並行 → 3 と 4（4-1〜4-3）を並行 → 4-4・4-5 → 5 → 6 → 7。3 と 4 は触るフォルダが違うが、
   ハーネス・テスト・台帳は両方が触るので、そこを触る手順の前に互いに連絡する（`ListAgents`）。
4. わざと変わる振る舞いは次の 2 つだけ（作り替えの範囲では、これ以外は変えない）。
   - MCP で古いプリセットを読み込んだときのゾーンの順を UI に合わせる（不具合の修正）。
   - Undo / Redo でズーム・スクロール・開閉の状態が巻き戻らなくなる（フェーズ 5）。
5. この文書は公開リポの `docs/` に置いたままにする。

### 7.3 T5 を組み込んで決まったこと（2026-10-26）

1. T5 を 0.4.0 に含める。P0 の結果で続けるかを決め、続けない・長引く場合は、T1〜T4 とフェーズ 5・6 だけで 0.4.0 を出す。
   T5 は P1 の採用が決まるまで別の枝（`dev-0.4.0-t5`）に留める。
2. 拡大表示は、案 B（GPU）の試作を省いて案 A（CPU のタイル）にする。
3. 並べ方（7.2 の 3 を改める）: 0 → 1 と 2 を並行 → 3-7a（境目）を最初に → 3A の残り・4-1〜4-3・T5 の P0〜P2 を並行 →
   4-4・4-5 → 5 → T5 の P3 → 6 → 7。T5 の P0 はフェーズ 2 の後ならいつ始めてもよい（新しいファイルだけを触る）。

## 8. まだ確かめていないこと

- この計画の最初の版は、dev_safe と Unity の無い環境でソースを読んで作った。その後クラウドに検証の環境を作り、
  golden・abcheck・dev_safe の回帰は回した（4 章の「作業する場所」）。実機の挙動（Unity が要るもの）は確かめていない。
- 調査の数字（行数・引数の数・確保の大きさ）は 2026-10-26 の develop（6f93f0e）のもの。確保の大きさは式から
  見積もったもので、測ったものではない（フェーズ 0 で測る）。
- Unity の API で確かめること:
  - `EncodeNativeArrayToPNG` を別スレッドで呼べるか。
  - `LoadImage` の代わりになる読み込み方。
  - Inference Engine 2.2 の `NativeArray` の口。
  - パッケージに入れたシェーダーを `Shader.Find` で引けるか。
