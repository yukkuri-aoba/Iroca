# コードの形（層・依存・メモリの持ち主）

いろかのコードの層と依存の向き、メモリの持ち主、寿命の決まりの**正**。計画（`docs/plan-0.4.0.md`）は作り替えの
進め方で、ここは作り替えたあとも残る決まりそのもの。テストの構成は `docs/testing-architecture.md`、アルゴリズムの
定数は `docs/algorithm-constants.md` が正。

0.4.0 の作り替えの途中なので、**目標の形と今の形が違う所がある**。その差は、下の検査が持つ「今ある違反の一覧」に
すべて載っている（一覧は減らすことしかできない）。目標と書いた部分は、作り替えが進んだら今の形の説明へ書き換える。
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
| `Code/Infra`・`Code/Text`（目標） | Infra | Core・Runtime・Infra・Text |
| `Code/MaskSuggest`（Ops を除く） | Infra と同じ段（AI 提案の準備・窓口） | 上に MaskSuggest を足したもの |
| `Code/Application`（目標） | Application | 上に Application を足したもの |
| `Code/Automation` | Automation | 上に Automation を足したもの |
| `Code/UI` | UI | 上に UI を足したもの |
| `Code/NdmfIntegration`・`Code/SentisIntegration` | 統合 | Application まで（UI と自動化は使わない） |
| `Code/McpIntegration` | 統合 | Automation まで（UI は使わない） |
| `Code/Debug` | 統合（デバッグ表示） | UI まで |

- 統合（NDMF・Sentis・MCP・Debug）は別の asmdef で、任意のパッケージが入ったときだけコンパイルされる。本体から
  統合を呼ぶことはできない（asmdef が参照しないので、コンパイルが止める）。
- 名前空間は `Iroca` のまま変えない（変えても何も縛らず、ハーネス・テスト・Debug の書き換えが増えるだけ）。縛るのは
  フォルダと検査と asmdef。
- 守り方: `scripts/source_checks/test_layer_dependencies.py` が上の表と同じ許可表を持ち、フォルダごとに宣言した型の名前を
  ほかのフォルダのコードが書いていないかを見る。`test_core_no_editor_dependency.py` は Core が UnityEditor を参照しない
  ことを見る。0.4.0 のフェーズ 6 で asmdef（`Editor.Core`・`Editor.Services`・`Editor`）に分けて、コンパイルで止める。

## 2. Core

Core は再着色のアルゴリズムと、それを支えるデータと計算の置き場。**製品の C# がアルゴリズムの唯一の正**で、
headless ハーネス（`scripts/headless-run/Harness.csproj`、.NET 8）は Core のファイルを `UnityEngine.CoreModule` だけを
参照してそのままコンパイルし、Unity なしで出力を測る。そのため:

- UnityEditor・文言（`Localization`）・ウィンドウを参照しない。
- UnityEngine は CoreModule の型（`Color32`・`Mathf`・`Vector*` など）だけを使う。`NativeArray` はハーネスに Unity の
  実行環境が無いので使えない（ヒープの外のメモリは `IrocaNativeMemory` が `Marshal.AllocHGlobal` で取る）。
- 作り替え（移す・分ける・名前を変える）で出力を変えない。golden・abcheck（全モードのバイト比較）・Unity の
  `RuntimeParityTests` で一致を示す（`docs/testing-architecture.md`）。

### 2.1 目標のフォルダ（0.4.0 の 3A で移す）

```
Code/Core/
  Foundation/  並列度・画素の決まり・ヒープの外のメモリ・ビット列・ヒストグラム・PixelRect
  Color/       色空間（OkLab）・PixelHsv・色の表とそのキャッシュ
  Model/       ColorZone（データだけ）・RecolorSettings・セッション / プリセット / DTO・マスクの形と符号化
  Pipeline/    PixelProcessor（今の入口の形のまま残す窓口）・RecolorPipeline（段を順に呼ぶだけ）・
               RunContext / ZoneState / ZoneBuffers・SelectionResult（選択と再着色の境目）・SelectionCache
  Selection/   Legacy（旧の選択。書き換えずに移すだけ）・Regions / Material / Decision（再設計の 3 層）
  Masks/       除外・含めるの適用（製品の契約。旧と新で共通）
  Recolor/     再着色の段（無彩・デコンタミ・アンカーと明るさ・混色帯・カーネル・合成）
  Filters/     ぼかし
  AutoTune/    Legacy（旧の自動調整）
  Diagnostics/ 時間計測・デバッグ記録
  Preview/     座標変換・縮小・RecolorPreview
  Uv/          UvChartMap
```

### 2.2 段の書き方

- パイプラインは `RecolorPipeline` が段を順に呼ぶだけにする（その 1 ファイルでパイプラインの仕様として読める）。
  段は `DecontamStage.Run(RunContext r, ZoneState z)` のような static メソッド。段の順と条件分岐は出力のビット一致に
  効くので、移すときに変えない。ゾーンごとの値は `ZoneParams` として 1 回だけ計算する。
- **選択と再着色の境目は `SelectionResult`。** 再着色が選択から読むもの（強度。内部は 1、縁は被覆率。混色帯の判定に
  要るもの）だけを持つ。旧の選択と再設計の選択は同じ形で出し、切り替えて比べる。
- 時間計測とデバッグ記録は、段の境目で呼ぶ観察者にする。既定は何もしない。
- 画素を回すループの中は、生の `T*` のローカル変数のまま書く。インターフェース・仮想呼び出し・`Span`・構造体の
  インデクサは使わない（Mono の Debug 最適化で 2〜4 倍遅かった。KernelBench）。段の入口で引数の構造体をローカルへ
  展開する。
- 定数は `docs/algorithm-constants.md` の台帳に種類と根拠を書く（`test_algorithm_constants.py` が Core と
  `MaskSuggest/Ops` のサブフォルダまで見る）。

## 3. エディタ側（目標。0.4.0 のフェーズ 4 で作る）

| 部品 | 役割 |
|---|---|
| `EditingSession` | 編集中の文書（原本・結びついたレシピ・ゾーンと設定・マスク・自動調整の入力）。操作は Undo とマスクの同期をしてから `Changed(種類)` を出す |
| `MaskWorkspace` | 編集中のマスク（1 画素 1 ビットのヒープの外のマスク） |
| `SessionPersistence` | 復元の優先順（レシピ → セッションのファイル → 既定）・自動保存・外での書き換えの検出 |
| `EditingSessionRegistry` | 開いている編集の一覧。ウィンドウを型で探す（`FindObjectsOfTypeAll<IrocaWindow>`）代わり |
| `SourcePixelCache` | 作業解像度の原本（参照の数で貸す） |
| `RecolorInput` | 色替えの入力を組み立てる唯一の場所。プレビュー・書き出し・一括書き出し・NDMF・自動化が共通で使う |
| `PreviewPipeline` / `PreviewRenderer` | プレビューの計算（概要段・確定段・ドラッグの追従・選択キャッシュ）と、GPU での表示 |
| `AutoTuneService`・`AiSuggestBroker`・`ExportService`・`RegistrationService`・`PresetApplier` | 自動調整・AI 提案の待ち行列・書き出し・登録・プリセットの読み込みを、それぞれ 1 本にする |

- IMGUI のビューは毎フレーム状態を読む（MVVM にはしない）。`Changed` を購読するのは、非同期に動くもの（プレビューの
  再計算・自動保存・UI の版・シーンへの反映・マスクの重ね表示）だけ。
- 登録は「計画」を返し、ウィンドウは確認画面に、自動化は警告にする（同じ判断を 2 か所に書かない）。

### 3.1 プレビューの表示

- 差分（変更前と変更後の画素ごとの差）は、描いている 2 枚のテクスチャからシェーダーで同じ描画の中で作る。別の
  ジョブや差分のテクスチャを持たない（持つと画像と差分の世代がずれる）。0.4.0 の 1-2 で `PreviewDiffDrawer` と
  `Code/UI/PreviewDiff.shader`（`Hidden/Iroca/PreviewDiff`）にした。
- エディタで使うシェーダーは `Code/` の下に置き、`Shader.Find` で引く（配布の zip は `Code/` の追跡ファイルを全部入れる）。
  IMGUI の中で描くシェーダーは、スクロールビューの切り抜き（`_GUIClipTexture`・`unity_GUIClipTextureMatrix`）を
  Unity の GUI のシェーダーと同じように受け取る。受け取らないと枠の外へはみ出す。
- 拡大表示は CPU でタイルに切り出す。作ったタイルを覚えておき、パンでは新しく見えたタイルだけを作る。範囲はその
  フレームのスクロール位置から決め、古い要求は捨てる。タイルはヒープの外から `LoadRawTextureData(IntPtr)` で上げる。
- 拡大表示の解像度（作業解像度で見るか、インポート設定の大きさに縮めて見るか）は表示だけの設定で、出力
  （NDMF・書き出し・自動化）には効かない。

## 4. メモリの持ち主

**画素数に比例する大きさのバッファは、Mono のヒープに置かない。** Unity の Mono のヒープは一度広がると OS へ返らず、
GC は大きな塊を動かせない。8K の原本は 6,700 万画素あり、`bool[]` 1 本で 67MB、`Color32[]` 1 本で 268MB が、
1 度使っただけでエディタを閉じるまで常駐する。

- 新しく書くコードは、画素数の大きさの配列を管理配列で作らない。`test_no_pixel_sized_managed_arrays.py` が
  `new T[w * h]` の形と Unity の API が返す画素の管理配列（`GetPixels32()` など）を数え、今ある分の一覧
  （`scripts/source_checks/pixel_sized_managed_arrays.json`）から増えたら落とす。
- ヒープの外の部品（今）: 確保の窓口 `IrocaNativeMemory.Alloc` / `Free`、作業配列のプール `NativeWorkPool<T>`
  （`Rent` / `Return`。中身は前の持ち主の値が残るので、借りた側が書いてから読む）、原本と出力の `NativePixels`、
  色の表。
- 目標（0.4.0 のフェーズ 2）:
  - 確保の窓口は `IrocaNativeMemory.Alloc(bytes, 用途)` の 1 つ。用途ごとに今の量と最大値を数える。
  - 持ち主の型は `SafeHandle` から作る `NativeBuffer` の 1 つ。参照の数・ファイナライザ・スレッド中断時の扱いをここだけに
    書き、ほかの部品はこの上に作る。
  - 境目では長さつきの `NativeSpan<T>` を渡す。範囲の検査は検証用のビルド定義（`IROCA_NATIVE_CHECKS`）のときだけ。
    ループの中は `T*` のまま。
  - マスクとビット列は `NativeBitMask`（1 画素 1 ビット、参照の数で持ち、書くときだけ写す）。
  - プールには量の上限と、手を止めて数秒たったら空きを返す縮小を持たせる。
- 持ち主の範囲を決めて書く: 実行全体（`RunBuffers`）・ゾーンごと（`ZoneBuffers`。8K の最大量を増やさないよう、使い
  終わった所で早めに手放す）・段の中の一時（`WorkScratch`）。借りたものは同じ範囲の `finally` で返す。
- **Unity との境目は Infra だけ。** `NativeArray` に見せる薄い変換と、`TextureIO`（原本を読んで `NativePixels` にする・
  PNG にしてファイルへ流す）。Unity 2022.3 では `LoadImage`・`SetPixels32`・`EncodeToPNG` が管理配列しか受け取らない
  ので、`GetPixelData<T>`・`SetPixelData`・`LoadRawTextureData(IntPtr)`・`ImageConversion.EncodeNativeArrayToPNG` を使う。
- 検証: 貸し借りの順が変わる変更は `IROCA_POOL_POISON=1`（貸す塊を 0xFF で埋め、書く前に読む所を出力の違いで
  見つける）と `IROCA_NATIVE_GUARD=1`（ガードページではみ出しを捕まえる。Windows）でも回す。

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
| `scripts/source_checks/test_layer_dependencies.py` | 1 章の依存の向き（許可表と、今ある違反の一覧） |
| `scripts/source_checks/test_core_no_editor_dependency.py` | Core が UnityEditor を参照しない |
| `scripts/source_checks/test_no_pixel_sized_managed_arrays.py` | 4 章の、画素数の大きさの管理配列を増やさない |
| `scripts/source_checks/test_event_subscription_symmetry.py` | 5 章の、static のイベントの購読と解除の対 |
| `scripts/source_checks/test_serialized_field_names.py` | 6 章の、Unity が保存するフィールドの名前（一覧は `serialized_field_names.json`） |
| `scripts/source_checks/test_editor_prefs_keys.py` | 6 章の、EditorPrefs / SessionState のキー |
| `scripts/source_checks/test_algorithm_constants.py` | 定数の台帳と、直書きの小数を増やさない |
| `scripts/source_checks/test_harness_scope.py` | 視覚レビューのゲートの範囲が、ハーネスのコンパイルする製品ソースと一致する |
| golden・abcheck・`RuntimeParityTests` | 作り替えで出力が変わらない（`docs/testing-architecture.md`） |

`scripts/source_checks` は C# のソースを読むだけで、ハーネスも Unity も要らない。CI（`.github/workflows/ci.yml`）で
毎回回る。違反の一覧は、直したら同じコミットで減らす（減ったのに一覧がそのままだと落ちる）。
