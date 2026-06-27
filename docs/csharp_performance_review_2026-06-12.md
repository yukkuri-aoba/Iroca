# C# (Unity Editor 拡張) パフォーマンス評価 — 2026-06-12

対象: `Code/` 配下の C# 全体（約 9,300 行）。静的レビューによる評価であり、実測プロファイルは未取得。
行番号はレビュー時点 (develop, fb783bc) のもの。

想定ワークロード: VRChat アバターテクスチャは 2048² / 4096² が標準。4096² では
1 画素配列あたり `float[]`=67MB / `Color32[]`=67MB / `bool[]`=16.7MB になる点を前提に評価した。

## サマリ（優先度順）

| # | 優先度 | 問題 | 場所 |
|---|--------|------|------|
| 1 | **P0** | `ArrayPool<T>.Shared` は 2^20 要素超を**プールしない**ため、2K 超テクスチャでは Rent が毎回 LOH 新規確保になりプール戦略が無効化されている | PixelProcessor.cs 全域 |
| 2 | **P1** | プレビューが毎回フル解像度で全パイプラインを実行（512² に縮小するのは処理後） | PreviewView.cs:846-855 |
| 3 | **P1** | OkLab 変換が `Mathf.Pow` ベースで、再着色ホットループの画素あたり約 7〜12 回の Pow 呼び出し | PixelProcessor.cs:1370-1413 |
| 4 | **P1** | `TryComputeRecolorAnchor` が全画素 3 パス・シングルスレッドで、各パスで OkLab 変換を再計算 | PixelProcessor.cs:867-928 |
| 5 | **P1** | `PropagateHighlights` / `GrowHighlightBand` 候補判定がシングルスレッド全画素走査（既定 ON） | PixelProcessor.cs:741-812, 945-1021 |
| 6 | **P1** | `DecontaminateAaBoundary` がゾーンごとに非プール `new bool[len]` + `new Color32[len]` ＋ float バッファ 8 本 | PixelProcessor.cs:517-536 |
| 7 | **P2** | ゾーン後段パス（穴埋め/境界復元/ブラー/再着色）にバウンディングボックス制限がなく、マッチ領域が小さくても常に全画素を処理 | PixelProcessor.cs 全段 |
| 8 | **P2** | `BoxDownsample` がシングルスレッドで、キャッシュミス時はメインスレッドで実行される | PixelProcessor.cs:1550-1582, PreviewView.cs:803-805 |
| 9 | **P2** | マスクの deep clone（`BuildSnapshot` / `RebuildMaskOverlay`）がプレビュー生成・ペイント中にメインスレッドで頻発 | MaskPaintView.cs:370-383, 870-885 |
| 10 | **P2** | エクスポートの `EncodeToPNG` がメインスレッド実行＋不要な `Apply()`（GPU アップロード） | ExportView.cs:264-268 |
| 11 | **P2** | `RunBatchApply` が完全同期（フル解像度処理×枚数分メインスレッドをブロック） | ExportView.cs:396-504 |
| 12 | **P3** | OnGUI の毎フレームアロケーション（`new GUIContent` 多数、`new GUIStyle`）と `IsReadable` の GetPixel 呼び出し | IrocaWindow.cs ほか |
| 13 | **P3** | ストロークごとの全マスク RLE エンコード、詳細クロップの画素単位コピー等の小粒問題 | MaskPaintView.cs:559-581 ほか |

---

## P0-1: ArrayPool が大テクスチャで機能していない

`PixelProcessor.ProcessPixelsArray` とその下請け（`GaussianBlur` / `BoxFilterSum` /
`FillSmallHoles` / `RecoverBoundaryEdges` / `ConstrainBlur` / `DecontaminateAaBoundary`）は
一貫して `ArrayPool<float>.Shared.Rent(len)` を使っており、設計意図はヒープアロケーション回避になっている
（コメントにも「ヒープアロケーションなし」と明記）。

しかし `ArrayPool<T>.Shared` の既定実装はバケット上限が **2^20 = 1,048,576 要素** で、
それを超える Rent は毎回 `new T[]` を返し、Return は単に捨てる。つまり:

- 1024×1024 (len=1,048,576) — ぎりぎりプールされる
- 2048×2048 (len=4.2M) / 4096×4096 (len=16.8M) — **全 Rent が新規 LOH 確保**

4096² テクスチャで 1 ゾーン処理する場合の Rent 回数を数えると、
pixH/pixS/pixV/claimed (float×4) + strength + fillAllowed(bool) + decontamination 内 float×8 +
BoxFilterSum temp×4 + FillSmallHoles buffer + RecoverBoundaryEdges buffer +
（feather 時 preBlur/blurOut/GaussianBlur temp/ConstrainBlur mask+sum）で
**1 ゾーンあたり約 20 本 × 17〜67MB ≒ 1GB 超の一時確保** がプレビュー再生成（パラメータ変更のたび、
デバウンス 0.2 秒）ごとに発生する。Editor の GC スパイク・メモリ断片化の最有力候補。

**改善案**（効果が大きい順）:
1. `ArrayPool<float>.Create(maxArrayLength: 64*1024*1024 / 4, ...)` 等で**専用プールを作る**。
   変更は `ArrayPool<float>.Shared` → 静的フィールド 1 個の差し替えで済む。
2. もしくはゾーンループの外で `strength` / `fillAllowed` 等を 1 回だけ確保してゾーン間で再利用する
   （per-zone Rent/Return をやめる）。decontamination の 8 本も同様に外出し可能。
3. 注意点: 専用プール化すると Rent がゼロ初期化済みでなくなるため、既存の `Array.Clear` を消さないこと
   （現状のコードは Clear 済みなのでそのまま安全）。

## P1-2: プレビューが常時フル解像度処理

`PreviewView.GeneratePreviewAsync` はフル解像度ソース（最大 4096²）を clone →
`ProcessPixelsArray` をフル解像度で実行 → 最後に 512² へ `BoxDownsample` する
([PreviewView.cs:846-855](../Code/UI/PreviewView.cs#L846-L855))。
「プレビュー＝エクスポート結果の一致」という意図的設計（コメントあり）だが、表示は 512² なので
**計算量の約 98%（4K 時）は表示に寄与しない**。スライダー操作のたびに全ゾーン×全段がフル解像度で走る。

**改善案**: 通常プレビューは 512² へ縮小してから処理し、フル解像度品質は既にある
詳細プレビュー（可視クロップのみフル解像度処理する `DetailPreviewView`）とエクスポートに任せる構成が考えられる。
ただしマッチングは解像度依存（AA 縁の比率、穴埋めパス数の効き方）なので、
プレビュー忠実度とのトレードオフ判断が必要。**採用するなら視覚レビュー必須**。
代替として「最後の操作から一定時間後にフル解像度版で再仕上げする 2 段階方式」もある。

## P1-3: OkLab 変換の Mathf.Pow 依存

`SrgbToLinear` / `LinearToSrgb` / `Cbrt` がすべて `Mathf.Pow`
([PixelProcessor.cs:1370-1385](../Code/Core/PixelProcessor.cs#L1370-L1385))。
`RecolorPixel` はマッチ画素ごとに `RgbToOklab`（Pow×6）＋`OklabToRgb`（Pow×3 ＋立方は乗算）を呼ぶため、
マッチ領域が広いと画素あたり ~9 回の Pow になる。Pow は乗算の数十倍のコスト。

**改善案**:
- 入力は `Color32` 由来なので `SrgbToLinear` は **256 エントリの LUT** で厳密に置換できる
  （`op.r / 255f` の入力は 256 通りしかない）。`RecolorPixel` の入力を 0..255 byte のまま受ければ表引き 1 回。
- `Cbrt` は `MathF.Cbrt`（Unity 2021+ / .NET Standard 2.1 で利用可）か、
  ビットハック初期値＋ニュートン 1〜2 回の近似に置換可能（Python 参照実装と一致精度の確認は必要）。
- `LinearToSrgb` は出力側で量子化されるため、4096 エントリ程度の LUT＋線形補間で視覚的に無損失にできる。
- `TryComputeRecolorAnchor` / Anchor 用の `RgbToOklab` も同じ LUT の恩恵を受ける。

## P1-4: TryComputeRecolorAnchor の 3 重全画素パス

[PixelProcessor.cs:867-928](../Code/Core/PixelProcessor.cs#L867-L928)。
pass1/2/3 がそれぞれ全画素をシングルスレッドで走査し、**各パスで同じ画素の `RgbToOklab` を再計算**する
（4K で計 50M 回の OkLab 変換 ≒ 300M Pow）。`autoRecolorAnchor` は既定 OFF なので常時の問題ではないが、
ON にしたゾーンではプレビュー毎に数秒級の追加コストになり得る。

**改善案**:
- 対象は `strength >= 0.9` の画素のみなので、pass1 で **(L, C) を一度だけ計算して圧縮配列に保存**し、
  pass2/3 はその配列を読む（メモリはマッチ画素数×8B で済む）。
- ヒストグラム構築は `Parallel.For` + スレッドローカル集計で並列化可能。
- そもそも `strength < AnchorStrengthMin` での early-continue は α チェックより先に行う（現状は OK）。

## P1-5: ハイライト系の空間処理がシングルスレッド

- `PropagateHighlights`: 3 パス × 双方向スイープ = 全画素 6 回走査、逐次依存があるため並列化困難だが、
  `highlightPot > 0` の画素のバウンディングボックスに限定すれば実効コストを大幅に削れる
  （ハイライト候補は通常テクスチャの一部に偏在する）。`highlightRecovery` は**既定 ON** なので毎回走る。
- `GrowHighlightBand` の候補判定ループ ([PixelProcessor.cs:967-998](../Code/Core/PixelProcessor.cs#L967-L998))
  は画素独立なので `Parallel.For` 化できる（BFS 本体は逐次のままでよい）。
  こちらも `highlightBandExpand` が**既定 ON**。
- `HighlightSampleCorrector.ComputeWashSample` も全画素ヒストグラム 1 パスのシングルスレッドだが、
  precomputed HSV を読むだけなので相対的に軽い（applyHighlightWash 既定 OFF でゲート済み）。

## P1-6: DecontaminateAaBoundary のアロケーション

[PixelProcessor.cs:517-518](../Code/Core/PixelProcessor.cs#L517-L518) で
`new bool[len]`（17MB）＋ `new Color32[len]`（67MB）を**ゾーンごとに**確保（プール外）。
さらに float バッファ 8 本（P0-1 の通り実質プールされない）。`useDecontamination` は既定 ON。

**改善案**: aaMask / decontaminatedPixels もゾーン間で再利用するバッファにする。
out で所有権を返す現 API のままでも、呼び出し側（ProcessPixelsArray）はゾーンループ内でしか使わないため
ループ外確保 + 渡し込みに変えられる。

## P2-7: ゾーン後段パスにバウンディングボックスがない

マッチ強度マップ構築後、`FillSmallHoles`（既定 5 パス、パス毎に 67MB の `Array.Copy`）、
`RecoverBoundaryEdges`（既定 3 パス）、`GaussianBlur`、`ConstrainBlur`、decontamination、再着色ループの
すべてが全画素を対象にする。小さいパーツ（ロゴ等）を 1 ゾーンで扱う場合、
マッチ画素が 1% でも 100% 分のコストを払う。

**改善案**: 強度マップ構築パスで `Parallel.For` のスレッドローカル min/max から
**マッチ領域の bbox（＋穴埋め/ブラー半径ぶんの余白）を求め、後段パスを bbox 内に限定**する。
出力同一性は余白を「パス数＋blur 半径＋decontaminationRadius」以上取れば保証できる。
多ゾーン運用時の体感に最も効く構造的改善。

## P2-8: BoxDownsample

- シングルスレッドの 4 重ループ。4K→512 で 16.8M 画素読み。`Parallel.For (y)` 化は安全で簡単。
- `GeneratePreviewAsync` のキャッシュミス時（テクスチャ切り替え直後）は
  [PreviewView.cs:803-805](../Code/UI/PreviewView.cs#L803-L805) で**メインスレッド実行**になり、
  数十〜百 ms 級のヒッチになる。rawDisplay の生成もジョブ側へ移せる
  （processed と同様に work デリゲート内で実行し、apply で受け取る）。

## P2-9: マスク clone のメインスレッド負荷

- `BuildSnapshot` はプレビュー生成のたびに共通＋全ゾーンマスクを deep clone（4K で各 16.7MB）。
- `RebuildMaskOverlay` もペイント中 10Hz（スロットル済み）で編集対象マスクを clone。
- ペイント中は両者が重なり、メインスレッドの GC 圧の主因になる。

**改善案**: マスクを「書き込み時に新規配列へ差し替える immutable 運用」にすればスナップショットは参照コピーで済む。
もしくは clone をジョブの work 側に移す…はデータレースになるため不可（現コメントの通り）。
現実的には **ストローク中はスナップショットを取らない**（既にスロットルで近い挙動）＋
`bool[]` → `BitArray`/`ulong[]` ビットパック化で clone コストを 1/8 にする案が安全。

## P2-10/11: エクスポート

- `apply` 内の `outTex.Apply()` ([ExportView.cs:266](../Code/UI/ExportView.cs#L266)) は
  GPU へのアップロードであり、直後の `EncodeToPNG`（CPU 側データを読む）には**不要**。4K で 67MB の無駄な転送。
- `EncodeToPNG` 自体もメインスレッドで 4K だと秒単位かかる。`ImageConversion.EncodeArrayToPNG` は
  `Texture2D` を要しないため、ジョブの work 側（バックグラウンド）へ移せる可能性が高い
  （スレッド安全性は要検証。移せれば「適用＆保存」終盤のフリーズが消える）。
- `RunBatchApply` は読み込み→フル解像度処理→PNG エンコードまで**全て同期**で、
  進捗バー以外 Editor が固まる。単体エクスポートで確立済みの `PreviewJob` パターンへ載せ替えるのが筋
  （現状 UI 非公開機能なので優先度は低い）。

## P3-12: OnGUI のフレーム毎アロケーション・冗長呼び出し

- `DrawZoneList` はゾーンごとに `new GUIContent(...)` を 20 個以上／フレーム生成し、
  `new GUIStyle(EditorStyles.label)` ([IrocaWindow.cs:455](../Code/IrocaWindow.cs#L455)) も毎フレーム。
  静的キャッシュ（`static readonly GUIContent`）化でゼロにできる。Localization 切替時のみ再構築すればよい。
- `IsReadable` は `GetPixel(0,0)` + 例外捕捉で判定しており
  ([IrocaWindow.cs:983-1001](../Code/IrocaWindow.cs#L983-L1001))、
  `PreviewView.Draw`（毎フレーム）＋ `DrawTextureField`（毎フレーム）＋ **ゾーンごとの canTune 判定**
  ([IrocaWindow.cs:516-517](../Code/IrocaWindow.cs#L516-L517)) から呼ばれる。
  `Texture2D.isReadable` プロパティならネイティブ呼び出し 1 回で済み、例外コストもない
  （`LoadImage` で作った一時テクスチャには使えないが、アセット参照には使える）。
- `DrawHeader` の `new[] { labels }` も毎フレーム。微小。

## P3-13: その他の小粒

- `MaskPaintView.SyncBuffersToState` は**ストローク開始・終了のたび**に全マスクを RLE エンコード
  （4K = 16.7M bool 走査 + `List<uint>` 成長 + Base64 文字列生成）。連続ペイント時の GC 要因。
  ストローク開始時は「前回エンコード結果のキャッシュ」を使い回せるケースが多い。
- `PaintMask` のブラシスタンプは O(r²)/ステップで、4K マスク（maskScale=8）×ブラシ 64 で
  1 スタンプ 26 万画素 × ドラッグ補間ステップ数。円の走査を `dx` 範囲計算（行ごとの span fill）にすれば半減。
- `DetailPreviewView` のクロップコピー ([DetailPreviewView.cs:163-174](../Code/UI/DetailPreviewView.cs#L163-L174))
  は画素単位 2 配列書き込み。行単位 `Array.Copy` ＋ processed は `rawCrop` から clone で十分。
- `RebuildDetailMaskOverlay` はメインスレッドで全クロップ画素ループ（他のオーバーレイは非同期化済みなのに、ここだけ同期）。
- `ComputeOverlayPixels` の `i % w` / `i / w` は行ループ化で除算を消せる（バックグラウンドなので優先度低）。
- `BoxFilterSum` 垂直パスは stride=w の列歩きでキャッシュ非効率。実測で問題になったら転置 or タイル化。
- `IsExcludedCombined` はマスク解像度＝テクスチャ解像度のとき乗除算を省略する fast-path を入れられる（毎画素×2 回呼ばれる）。

## 良い点（既に対処済みの設計）

- 計算本体はすべて `PreviewJob`（世代管理＋CancellationToken）でバックグラウンド化されており、
  メインスレッドは SetPixels32/Apply のみ。キャンセル経路の ArrayPool 返却も try/finally で防御済み。
- `MaxDegreeOfParallelism = ProcessorCount - 2` で Editor のスレッドプール圧迫を回避。
- HSV の事前一括計算（ゾーン数に関わらず 1 回）、`ColorZone` のパラメータキャッシュ、
  ゾーン定数の per-pixel ループ外事前計算は適切。
- `ConstrainBlur` の BoxFilterSum 化（O(N·r²)→O(N)）、`FillSmallHoles` のダブルバッファ化、
  ペイント中オーバーレイ再構築の 10Hz スロットル、プレビューのデバウンス 0.2 秒、
  詳細プレビューの可視範囲クロップ限定など、過去の改善が効いている。
- diff 生成・オーバーレイ生成も非同期化済み。

## 推奨着手順

1. **専用 ArrayPool（P0-1）** — 数行の変更で 2K/4K 時の GC churn を桁で削減。リスク極小。
2. **sRGB/Cbrt の LUT 化（P1-3）** — 再着色ループの最大コストを除去。Python 参照との一致検証＋視覚レビュー必須。
3. **decontamination バッファのループ外確保（P1-6）** — 機械的なリファクタ。
4. **bbox 制限（P2-7）** — 構造変更としては最大の効果。出力同一性のテスト（exact-count / IoU）で検証可能。
5. **BoxDownsample 並列化＋ジョブ側移動（P2-8）**、**Export の Apply() 除去（P2-10）** — 小さく確実。
6. UI 系（P3）は体感ヒッチの報告があれば。

※ アルゴリズム出力に影響し得る変更（2,4 など）は improvement-cycle の手順
（snapshot → compare → Read で全数確認 → approve）を必ず通すこと。
