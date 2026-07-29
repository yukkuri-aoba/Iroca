# コードベース全体パフォーマンス評価 — 2026-07-29

対象: `Code/` 配下の C# 全体。①コア再着色アルゴリズム（`Code/Core/PixelProcessor*` ほか）、
②AI マスク提案（`Code/MaskSuggest/` + `Code/SentisIntegration/`）、③UI・プレビュー・IO
（`Code/UI/` + `Code/Infra/`）の 3 系統に分けて評価した。行番号は評価時点（develop, 76e4df8）のもの。

**方法**: 静的監査＋実測。①は実 C# ハーネス（`scripts/headless-run`、製品組み込みの
`PerfReport` フェーズ計測）で 4096² 実テクスチャを計測。②は Sentis 2.1.3 のパッケージソース
（`Worker.cs` / `Tensor.cs` / `ComputeTensorData.cs`）まで当たってブロッキング性の裏を取った
静的監査（実レイテンシの実測は Unity 実機が必要なため未実施）。③は静的監査。

前回レビュー: `docs/csharp_performance_review_2026-06-12.md`（13 項目）。P0-1 ArrayPool /
P1-3 OkLab Pow / P1-4 アンカー 3 パス / P1-6 デコンタミバッファ / P2-7 bbox / P2-10 エクスポート
同期などは**対応済みで実測でも効いている**ことを確認した。本評価はその後の残課題と新規発見。

## 対応状況（2026-07-29 追記）

①コアの F1 / F2 / F5 / F6 / F8 を実装した。**すべて出力ビット不変**で、実 C# ハーネスの
4096² 実テクスチャ 4 条件（紺サンプル＝achroma 経路 / 高彩度サンプル / 小 match 200×200 /
除外マスクあり）で変更前後の `out.raw` が **byte 完全一致**することを確認済み。

| 条件（4K・1 ゾーン） | 対応前 | 対応後 |
|---|---:|---:|
| 紺サンプル + anchor ON + FF ON（≒製品既定） | 2530 ms | **1224 ms**（-52%） |
| 小 match（200×200 ロゴ相当） | 829 ms | **521 ms**（-37%） |
| 高彩度サンプル（achroma≈0） | 966 ms | **890 ms** |

紺サンプルのフェーズ別:

| フェーズ | 前 | 後 | 効いた変更 |
|---|---:|---:|---|
| RegionStats | 681.8 | **73.2** | F1（3 関数のチャンク並列ヒストグラム化） |
| FloodFill | 327.1 | **150.9** | F5（BFS の除算除去・座標パック化・label/スタックのプール化・bbox 走査並列化） |
| Decontaminate | 305.1 | **205.9** | F2（bbox 化）+ F8（窓和 垂直パスの 16 列ブロック化） |
| Recolor | 279.4 | **160.4** | `CleanAchromaFringe` の bbox 化 + 再着色 bbox 走査の並列化 |
| Highlight | 99.5 | **66.2** | F5（帯成長 BFS の除算除去）+ pot bbox 走査の並列化 |
| HoleFill | 144.1 | **123.9** | （副次） |
| 合計 | 2529.8 | **1224.2** | |

不変性の根拠（各変更）:
- **F1**: ヒストグラムは整数カウントの加算のみ＝集計順非依存。アンカー推定は圧縮配列を
  やめて元画素インデックスに保存し、pass2/3 が同じコア判定で選び直す形にした（OkLab 変換は
  1 回のまま）。
- **F2 / CleanAchromaFringe**: α 分解が触るのは `0<strength<threshold` の画素だけ＝定義上
  後段 bbox 内。窓和の入力は bbox±radius だけ用意すれば足りる。`BoxFilterSum` の矩形版は
  スライディング和の開始位置が変わるが、入力が整数値（0..255 / 0-1）なので窓和は float の
  整数精度に収まり丸め差が生じない。
- **F5**: DFS 化しても連結成分の分割・採番（外側走査順）・ヒストグラムは探索順に依存しない。
- **F8**: 各列の加算順序は従来と同一（初期窓を昇順 → 行ごとに sub → add）。

未対応（次回）: F3 メモリ・F4 ZoneAutoTuner 並列化・F7 非プール配列・F9〜F11、②AI 側 A1〜A13、
③UI 側 U1〜U11。F12（選択キャッシュへの RegionStats 同梱）は F1 で RegionStats が 682→73ms に
落ちたため**費用対効果が消えた**（`regMidMapFull` 67MB をキャッシュに常駐させる副作用のほうが
大きい）。以降の優先順位は本文の見積りではなく再計測値で決め直すこと。

## 総合サマリ

- **①コア**: 基盤は良好（専用 ArrayPool・bbox 限定・選択キャッシュ・OkLab LUT が正しく機能）。
  最大ボトルネックは**単スレッドのまま残っている統計系の段（RegionStats）**で、無彩寄りサンプル
  （紺・黒・茶・グレー＝アバターで最頻）では**全体の 45%** を占める。パス数律速でもアロケーション
  律速でもなく、明確に**シングルスレッド律速**。
- **②AI マスク提案**: 設計方針（BG スレッド分離・フレーム分割・二層 LRU キャッシュ）は正しいが、
  実装が一歩足りず**体感の固まりがほぼ全部残っている**。最大の問題はフレーム分割ポンプが
  「ディスパッチ」しか分割しておらず、実推論の待ちが同期 readback に一括で乗る構造。
- **③UI・IO**: 骨格（デバウンス・世代管理付き非同期ジョブ・段階的リファイン・非同期エクスポート）
  は良い。詳細プレビューの無駄なテクスチャアップロード等、個別ホットスポットが数点。

## 実測値（実 C# ハーネス / ProcessPixelsArray 単体）

| 条件 | 実測 |
|---|---|
| 4K・1 ゾーン・有彩サンプル（FF off / anchor off = テスト既定） | **約 1.3 秒** |
| 4K・1 ゾーン・フラッドフィル ON | 約 1.8 秒 |
| 4K・1 ゾーン・**無彩寄りサンプル(紺) + anchor ON + FF ON ≒ 製品既定** | **約 4.9 秒** |
| 4K・4 ゾーン（有彩） | 約 3.6〜3.8 秒（ほぼ +1 秒/ゾーンで線形） |
| 4K・2 ゾーン（紺・製品既定相当） | 約 8.9 秒（+4.0 秒/ゾーン） |
| 8K・1 ゾーン（合成） | 約 11 秒（≈6 Mpix/s） |
| `ZoneAutoTuner.Analyze` 4K・1 ゾーン | **約 5.5 秒**（完全単スレッド） |
| ピーク作業セット（4K・1 ゾーン） | **約 1.7 GB** |

1.3 秒と 4.9 秒の差の主因が RegionStats（下記 F1）。有彩サンプルでは 0.4ms、無彩寄りサンプル
では約 2.2 秒（anchor ON 込み）になる。

### フェーズ別内訳（4K・1 ゾーン・紺サンプル・製品既定相当）

| フェーズ | ms | 比率 | 並列性 |
|---|---:|---:|---|
| HSV | 88.6 | 1.8% | 並列（ゾーン間で共有・1 回のみ） |
| Match | 393.4 | 8.1% | 並列（行） |
| Highlight | 419.5 | 8.7% | **ほぼ単スレ** |
| FloodFill | 514.4 | 10.6% | **単スレ** |
| HoleFill | 190.1 | 3.9% | 並列 + bbox |
| BoundaryRecover | 104.0 | 2.1% | 並列 + bbox |
| Blur | 0.0 | 0% | （既定 edgeFeather=0 で不使用） |
| Decontaminate | 343.9 | 7.1% | 並列だが **bbox 非対応** |
| **RegionStats** | **2166.1** | **44.7%** | **完全単スレ** |
| Recolor | 597.8 | 12.3% | 並列 + bbox |
| 合計 | 4849.5 | | |

補助実験:
- `autoRecolorAnchor=false` → RegionStats 2166→1440ms（差 ≒ `TryComputeRecolorAnchor` = 630ms）
- 高彩度サンプル（achromaWeight≈0）+ anchor off → RegionStats **0.39ms**
- match 面積 0.24%（200×200 ロゴ）@4K → 総 827ms、うち **Decontam 296ms（36%）**。
  他段は正しくスケール（FloodFill 314→14 / HoleFill 110→38 / Recolor 457→50 / RegionStats 517→18）
- グレーモード `ApplyChromaCeilingGate` → +45ms（12 回の全画素 dilation。問題なし）

---

## ① コア再着色アルゴリズム

### 発見事項（影響度順）

#### [高] F1. RegionStats 段が完全単スレッドで最大 45%
- 証拠: `PixelProcessor.cs:673`（`TryComputeRecolorAnchor`）/ `:738`（`TryComputeRegionLRange`）/
  `:741`（`BuildComponentMedianLMap`）。実装 `PixelProcessor.Recolor.cs:59-133`、
  `PixelProcessor.Achroma.cs:224-306, 315-342` — いずれも `Parallel.For` を使わない素の `for`。
- 注意: achroma パスは無彩**ターゲット**だけでなく無彩寄り**サンプル**でも発火する
  （`ComputeAchromaWeight` が `max(achromaSample, ...)`、`Achroma.cs:183-193`）。紺サンプル
  （OkLab chroma≈0.04）で `achromaSample≈0.33` となりターゲットが純赤でも走る。**通常ケース**。
- 方向性: 3 つともヒストグラム集計なので、スレッドローカル 256bin + マージで
  `Parallel.For(0,h)` 化 → **出力ビット不変**（percentile は集計順非依存）。BFS 本体
  （`Achroma.cs:262-275`）は逐次のままでよい。
- 見積り: 2166ms → 500〜700ms。**全体 -30%**。

#### [高] F2. デコンタミが bbox 非対応 — match 面積に完全非依存の固定コスト
- 証拠: `PixelProcessor.cs:599-603` は `hasPostBox`（bool）しか渡さず矩形を渡していない。
  `PixelProcessor.Decontam.cs:54-57` の早期 return は「マッチ皆無」のみ。以降 `Array.Clear` ×4
  （各 67MB）と `BoxFilterSum` ×4（`Decontam.cs:99-102`）が常に全画素。
- 実測: match 0.24% でも 38% でも約 290ms で同一。小 match ゾーンでは全体の 36%。
- 安全性: `strength[i]=1f` 書き戻し（`Decontam.cs:129`）は `s>0` 画素のみ＝定義上 bbox 内。
  BG ドナー参照は ±radius なので `bbox+radius` で足りる。**出力ビット不変で bbox 化可能**。
- 方向性: `BoxFilterSum` に矩形引数を追加（`GaussianBlur` が既にやっている形、
  `Selection.cs:78-100`）し `ppMin/Max ± radius` を渡す。
- 見積り: 小〜中 match ゾーンで 296ms → 2〜10ms（**全体 -30%**）。大 bbox では利得ほぼ 0。

#### [高] F3. ピーク 1.7GB — デコンタミ段で float[16.8M] を 15 本同時借用
- 証拠: 外側 `pixH/pixS/pixV/claimed`（`PixelProcessor.cs:182-185`）+ `strength`（`:262`）+
  `matchConf`（`:286`、ゾーン finally まで保持）+ デコンタミ 8 本（`Decontam.cs:68-75`）+
  `BoxFilterSum` temp（`Selection.cs:26`）= 15 本 × 67.1MB ≈ 1.0GB。プールは
  `maxArraysPerBucket:24`（`PixelProcessor.cs:104`）で自動トリムされない。
  無彩ゾーンの `CleanAchromaFringe` はさらに 10 本（`Decontam.cs:205-209`）。
- 方向性: (a) F2 の bbox 化で作業配列も bbox サイズへ、(b) `matchConf` を FF 直後
  （`PixelProcessor.cs:366` 以降）に返却（3 行で -67MB）、(c) in-place 化の検討。

#### [中] F4. ZoneAutoTuner が約 5.5 秒・単スレッド・17 全画面パス
- 証拠: `ZoneAutoTuner*.cs` に `Parallel.For` 0 件。`TryAnalyzePixels`(`ZoneAutoTuner.cs:288`) +
  `DeriveAutoTonalSamples`(`Tolerance.cs:336,400`) + `TryDeriveChromaticToleranceMulti`
  (`Tolerance.cs:535,574`) + Verify 系（`Verify.cs:55,120,161,227,254`）≒ 17 パス。
  コメント（`ZoneAutoTuner.cs:125`）も「最大 15 回前後の全画面走査」と自認。
- 方向性: 全パスがヒストグラム/カウンタ集計 → スレッドローカル集計 + マージで並列化
  （導出値不変）。`Color.RGBToHSV` を初回パスでキャッシュすれば 17 回の重複変換が 1 回に。
- 見積り: 5.5s → 0.6〜1.0s。

#### [中] F5. Highlight / FloodFill の逐次 BFS（合計 933ms）
- 証拠: `PropagateHighlights`（`Highlight.cs:22-115`）純逐次・最大 6 スイープ。
  `GrowHighlightBand`（`Highlight.cs:199-228`）シード収集が全画素逐次、BFS でデキューごとに
  `idx % w` / `idx / w` ×2、`Queue<int>` 初期容量なし（`:165`）。
  `ApplyConnectedComponentMask`（`FloodFill.cs:36-153`）は `new int[bw*bh]`（`:85`、非プール・
  最大 67MB）+ `TryEnq` 内の除算（`:117-118`）。
- 方向性: キュー要素の (x,y) パック化で除算除去、`Queue<int>` → `int[]` スタック
  （`RecolorPreview.cs:90` が既にやっている形）、label のプール化。
- 見積り: 933ms → 550〜650ms。並列 Union-Find は実装リスクが高いので後回し。

#### [中] F6. 単スレッド区間（約 3 秒分）がキャンセル不能 — ドラッグ中のゾンビ計算
- 証拠: `PreviewView.cs:351-359` はダーティ時に `Cancel()` を呼ぶが、
  `TryComputeStrengthBBox` / `PropagateHighlights` / `GrowHighlightBand`（BFS 部）/
  `ApplyConnectedComponentMask` / `TryComputeRecolorAnchor` / `TryComputeRegionLRange` /
  `BuildComponentMedianLMap` / 再着色 bbox スキャン（`PixelProcessor.cs:774-788`）は
  `CancellationToken` を受け取らない/見ない。
- 影響: スライダー操作中に旧ジョブが CPU を焼き続け新ジョブと並走 → 両方遅くなる。
- 方向性: 行単位に `if ((y & 63)==0) ct.ThrowIfCancellationRequested()`。数値ロジック不変。

#### [中] F7. 非プールの大配列 new が呼び出しごとに発生
- 証拠: `PixelProcessor.cs:212-213`（`new bool[len]` + `new Color32[len]`、呼び出しごと）、
  `:587`（マスクあり時 `new bool[len]`）、`FloodFill.cs:85` / `Achroma.cs:244`（`new int[bw*bh]`）、
  `Achroma.cs:295`（`new float[len]`）、`SelectionCache.cs:63`（`new float[len]`、ミスごと・
  ゾーンごと・**恒久保持**）。
- 影響: プレビュー再生成 1 回あたり ≈100MB の LOH ゴミ + ゾーン数 N で 134MB×N が常駐。
- 方向性: 既存プール（`s_boolPool` 等）へ寄せる。`SelectionCache.Store` は同ゾーン・同寸法なら
  既存配列へ `Array.Copy`。

#### [中] F8. BoxFilterSum 垂直パスが列走査でキャッシュライン 1/16 しか使わない
- 証拠: `Selection.cs:49-63` — `Parallel.For(0, w, ...)` で 1 反復が 1 列を縦走査
  （4K でストライド 16KB）。
- 方向性: 16 列ブロック単位化（`sum[16]` を持って行方向走査）。出力ビット不変。
- 見積り: デコンタミ 344ms → 200ms 前後（F2 併用でさらに縮小）。

#### [低] F9〜F11（小粒）
- F9: `Parallel.For(0, len, ...)` の per-index デリゲート（`PixelProcessor.cs:195` ほか 8 箇所）
  → 行並列へ統一で -40〜70ms。Match / Recolor は既に行並列で正しい。
- F10: `IsExcludedCombined` の per-pixel 整数除算（`PixelProcessor.cs:951-952`、マスク使用時
  3 段で 3350 万除算）→ 行ごと `my` + `int[texW]` LUT で -30〜80ms。
- F11: 同一情報の bbox スキャンが 1 ゾーンで 5 回（`Selection.cs:185` / `PixelProcessor.cs:774` /
  `Highlight.cs:29` / `FloodFill.cs:41` / `Achroma.cs:228`）→ 少なくとも `:409` と `:774` は統合可。

#### [高・体感] F12. 選択キャッシュがヒットしても後段 3.1 秒が走る
- 証拠: `PixelProcessor.cs:271-275` でヒット時 `strength` 復元、`:332,436,473,486,518,539` の
  各段はスキップされるが、RejectNeutral / Solidify / **Decontaminate / RegionStats / Recolor** /
  CleanAchromaFringe は毎回実行。
- 影響: ターゲット色をドラッグしても 1 回 3.1 秒（Decontam 344 + RegionStats 2166 + Recolor 598）。
- 方向性: `TryComputeRecolorAnchor` / `TryComputeRegionLRange` / `BuildComponentMedianLMap` は
  `strength` と元画素にしか依存しない（各シグネチャで確認可能）→ ヒット時は結果も同一なので
  `SelectionCache` のエントリに `ZoneRecolorStats` と `regMidMapFull` を同梱すれば丸ごと省略可。
  デコンタミは target に依存する再合成（`Decontam.cs:161-163`）があるため、α 分解結果のみ
  キャッシュすれば target 変更時は再合成だけで済む。
- 見積り: 色ドラッグ時 3.1s → 0.9s（**-70%**）。体感インパクト最大。

#### [参考] F13. メインプレビューは 384px 表示なのにフル解像度を毎回処理
- 証拠: `PreviewView.Async.cs:189`（フル解像度処理）→ `:195-197` で 384px へ縮小表示。
  プロキシ段（512px、`:147-153`）が概要を先に出し、フル段の実消費者は詳細プレビュー用
  `parityCache` と確定表示のバイト一致のみ。
- コメント（`PreviewView.Async.cs:96-99, 180-181`）に意図が明記されており**品質との
  トレードオフ＝ユーザー判断領域**。「詳細プレビューを開いていない間はフル段をアイドル
  1〜2 秒後へ遅延」なら品質を落とさず編集中の CPU 占有を消せる。

### 既に良く最適化されている点（改善余地と誤認しないこと）

1. 専用 `ArrayPool`（`PixelProcessor.cs:94-110`）— Shared の 2^20 上限問題を回避、2^24 まで
   プール。ゼロ初期化なしも `Array.Clear` で正しく補償。
2. HSV のゾーンループ外 1 回計算（`:195-198`）— ゾーン数 N に対し O(1)。
3. bbox 限定が広く効いている（`:399-418`）— match 0.24% ケースで FloodFill 314→14ms、
   Recolor 457→50ms 等、実測で正しくスケール。例外はデコンタミのみ（F2）。
4. `BoxFilterSum` の O(N) スライディングウィンドウ（`Selection.cs:31-63`）— 半径非依存。
   `decontaminationRadius` を下げても速くならない＝チューニング対象ではない。
5. OkLab の LUT 化（`PixelProcessor.ColorSpace.cs:30-81`）— per-pixel `Mathf.Pow` 9 回を除去済み。
6. `RecolorParams` の `in` 渡し + ゾーン定数事前算出（`Recolor.cs:138-168`、
   `PixelProcessor.cs:711-716`）— per-pixel sqrt/除算を除去済み。
7. 選択キャッシュのキー設計（`PixelProcessor.cs:63-92`）+ `--selkey-audit` の機械監査。
8. `PreviewParityCache` — クロップ側での大域統計再計算を回避、`sourceId` で誤転写も防止。
9. デコンタミバッファのゾーン間再利用（`PixelProcessor.cs:203-214`）。
10. マスクの bitpack（`MaskSnapshot`）と `MaskRle`（O(n) 1 パス）。
11. `PerfReport` フェーズ計測の製品組み込み（`PixelProcessor.cs:37-47, 922-926`）— 本評価が
    そのまま実測でき、改善後の検証も同手順で可能。

### パイプライン段構成（4K・1 ゾーン・既定設定）

| # | 段 | file:line | 範囲 | 並列 | 主なアロケーション(4K) | 実測 ms |
|---|---|---|---|---|---|---:|
| 1 | HSV 事前計算 | `PixelProcessor.cs:195-198` | 全画素 | 並列(per-index) | float[]×3=201MB(pool) | 88.6 |
| 2 | マッチング | `:293-309`→`ColorZone.Match.cs:157-339` | 全画素 | 並列(行) | float[]×3=201MB(pool) | 393.4 |
| 3 | 彩度天井ゲート | `:315`→`Selection.cs:321-400` | 全画素×14 | 並列 | bool[]×2(pool) | +45(グレー時) |
| 4-5 | ハイライト伝播/帯成長 | `:324,334`→`Highlight.cs` | pot bbox/全画素+BFS | **単スレ主体** | bool[]×2 + Queue | 419.5 |
| 6 | 連結成分(FF) | `:366`→`FloodFill.cs:36-153` | match bbox | **単スレ** | int[bw*bh]非プール | 514.4 |
| 8 | 穴埋め | `:444-461`→`Selection.cs:223-299` | bbox×5 | 並列 | float[]67MB(pool) | 190.1 |
| 9 | 境界回復 | `:475`→`Selection.cs:409-490` | bbox×3 | 並列 | float[]67MB(pool) | 104.0 |
| 12 | 選択キャッシュ保存 | `:540`→`SelectionCache.cs:59-66` | 全画素 | memcpy | **new float[len] 恒久保持** | — |
| 15 | デコンタミ | `:599-603`→`Decontam.cs:43-184` | **全画素(bbox 非対応)** | 並列(per-index) | float[]×8=537MB(pool)+非プール2本 | 343.9 |
| 17-19 | RegionStats | `:673,738,741`→`Recolor.cs`/`Achroma.cs` | 全画素 | **単スレ** | int[bw*bh]+float[len]非プール | 2166.1 |
| 21 | 再着色 | `:797-848`→`Recolor.cs:170-347` | bbox | 並列(行) | なし | 597.8 |

ゾーン数 N のスケーリング: #1 のみ償却、他は N 倍（実測 +4.0 秒/ゾーン@紺、+0.8〜1.0 秒/ゾーン@有彩）。

---

## ② AI マスク提案（Sentis + MobileSAM）

実装初期であることを踏まえ、「今すぐ直すべき応答性問題」と「安定後の最適化」を分けた。
体感の遅さ・固まりは A1〜A4 でほぼ説明できる。

### エンドツーエンド処理フロー（4096²・GPUCompute・初回クリック想定）

| # | 段 | 実行場所 | 概算コスト |
|---|---|---|---|
| 1 | AI モード ON → `TryEnsureModels` | メイン同期 | 初回のみ ONNX→.sentis 変換 数秒〜十数秒(進捗バーあり) |
| 2 | 毎 Layout の `PrepareSource` | メイン(毎フレーム) | ディスク stat ×2 + AssetDatabase 参照 |
| 3 | `SamImageOps.BuildEncoderInput` | BG(Task.Run) | 中間 50MB+出力 25MB、単スレ 2 パス、0.3〜1 秒 |
| 4 | エンコーダ `ScheduleIterable` を 8ms 予算で pump | メイン(分割) | **ディスパッチのみ**(GPU 実行待ちは含まれない) |
| 5 | `FinishEncode` の `ReadbackAndClone` | **メイン・ブロッキング** | GPU 推論の実時間がここで一括停止 |
| 6 | (初回のみ)デコーダ暖機 | **メイン・ブロッキング** | 数秒〜数十秒、進捗 UI なし |
| 7 | `TryRunDecoderCore` | **メイン・ブロッキング** | 埋め込み 4.2MB 再アップロード + 同期 readback×2 |
| 8 | stage1 `SelectAndUpscale`(フル解像度) | BG | **最重量**。transient 400〜670MB、ExtendFringe 全画素×25 セル≈4.2 億反復 |
| 9 | ズーム判定(bbox×4 < max(w,h)) | BG | 4K では小パーツでほぼ常に発火 → **8 の精密化結果は捨てられる** |
| 10 | クロップ再推論(4〜7 をもう 1 周) | BG+メイン | エンコーダ 2 回目(クロップ LRU ミス時) |
| 11 | `CommitProposalToMask` | **メイン・ブロッキング** | 解像度不一致時 GetPixels32+IncludeAaTransition 同期 |
| 12 | プレビュー再生成 | BG | 既存の再着色パイプライン(①の評価対象) |

### 発見事項（体感レイテンシへの影響順）

#### [高] A1. pump は「スケジュール」しか分割せず、実推論はブロッキング readback で一括して支払われる
- 証拠: Sentis 2.1.3 `Worker.cs:263-306`（`ScheduleIterable` の 1 MoveNext = 1 レイヤの
  ディスパッチ + yield。GPU 完了待ちは入らない）、`Tensor.cs:145-148`（`ReadbackAndClone` =
  "Blocking download"）、`ComputeTensorData.cs:170-185`（`WaitForCompletion()`）。
  呼び出し側: `SentisMaskSuggestService.cs:233`（エンコーダ）、`:439-440`（デコーダ）、
  `:525`（ズーム）。`EditorIteratorPump.cs:60-66` は完走時に `done` を Tick 内で呼ぶため
  8ms 予算の Tick が readback 待ちで数百 ms 化。
- 症状: 進捗率（`EditorIteratorPump.cs:69-70`）はディスパッチ速度で進むため
  **「100% に達した後にエディタが固まる」**。CPU フォールバック時は pump が実質無機能化し
  全計算が 1 回のブロッキング待ちになる（`CPUFallbackCalculator.cs:33-34` / Burst の
  fence 積みのみ）。
- 方向性: `ReadbackRequest()` を pump 完走時に発行し `IsReadbackRequestDone()` を
  `EditorApplication.update` でポーリング、完了後に `DownloadToArray()`。API は GPU/CPU 両
  バックエンドで揃っており、**これだけで最大の固まりが消える**。

#### [高] A2. マスク反映(commit)がメインスレッドで GetPixels32 + AA 遷移包含を同期実行
- 証拠: `MaskSuggestController.cs:184-195` — 解像度不一致パスで `tex.GetPixels32()`
  （毎コミット・キャッシュなし）→ `IncludeAaTransition`。到達経路は
  `PreviewJobMainThread.Drain` 経由＝メインスレッド。不一致は通常ケース（マスク座標系 =
  Unity インポート解像度 `MaskPaintView.cs:292-293`、提案 = 実ファイル解像度
  `PreviewView.Input.cs:407`）。
- 症状: 2048² マスクでも距離変換 4.2M×int 最大 6 本 + 全画素数パスで数百 ms〜秒の停止。
  「クリック後しばらくして急に固まる」の原因。
- 方向性: 転写 + AA 包含を BG（`PreviewJob`）へ。メインは OR 合成と Undo 登録のみに。

#### [高] A3. ズーム発火時、フル解像度の精密化チェーンが丸ごと無駄になる
- 証拠: stage1 は `pixelsBottomUp: px` 付き `SelectAndUpscale`（`SentisMaskSuggestService.cs:379-380`
  → `SamMaskPostprocess.cs:104-113` で SnapBoundary + ExtendFringe + IncludeAaTransition が
  フル解像度実行）。結果は bbox 測定とフォールバック用のみ（`:392-401`）で、`o.hasCrop` なら
  破棄（`:407`）。発火条件 bbox×4 < max(w,h)（`SamZoomOps.cs:117-119`）= 4K では
  bbox<1024px でほぼ常に発火。
- 方向性: stage1 は `pixelsBottomUp: null`（生の拡大のみ）で bbox 判定に使い、精密化は
  実際に配信するマスクにだけ遅延実行（`FallbackToStage1` 時のみ stage1 を精密化）。
  小パーツクリックのレイテンシとメモリが概ね半減。変更が小さく安全。

#### [高] A4. 後処理が全段フル解像度・全画素走査、1 クリックあたり 400〜670MB を transient 確保
- 証拠: `SamMaskRefine.cs:798-834` `DistanceToOpposite` が `int[w*h]`（4K で 67MB/本）を
  SnapBoundary×2 / ExtendFringe×2 / IncludeAaTransitionPass×2×最大 3 パスで計 6〜10 本。
  主犯は far/nearBg 判定の全画素×5×5 セル窓 ≈ 4.2 億回 double 演算・単スレッド
  （`SamMaskRefine.cs:190-229`）。結果は境界近傍（reach≈50px）しか使われない（`:240`）。
- 影響: 提案領域がアトラスの数 % でもコストは常に全画素分。クリック→表示の秒数の支配項。
  67MB 級配列の頻繁な確保で Editor 常駐メモリが単調増加しやすい。
- 方向性: マスク bbox+マージンへの限定 / 距離値の上限クランプで `ushort[]` 化（67→33MB）/
  far/nearBg を境界近傍画素に限定。精度検証が要るため**安定後の課題**。

#### [中] A5〜A9
- A5 キャンセル不通: 前処理・後処理クロージャが `ct` を一切参照しない
  （`SentisMaskSuggestService.cs:377,492`、`PreviewJob.cs:79-86` は完了後にしか見ない）。
  連打・テクスチャ切替で数秒級の処理がスレッドプールに滞留。→ 各パス外側ループに
  `ThrowIfCancellationRequested()`。
- A6 デコーダ暖機が無告知でメインスレッド長時間停止（`SentisMaskSuggestService.cs:296-318`、
  進捗バーなし）。前倒し設計(1fb0458)自体は正しい。→ 最低限 `DisplayProgressBar` 表示。
- A7 AI モード中は毎 Layout にディスク stat ×2 + `AssetDatabase.GetAssetPath`
  （`PreviewView.Input.cs:428-449`、Phase ガードより先に評価される）。→ ソース変更時のみ再計算。
- A8 エンコード進捗が毎 Tick(8ms) 全ウィンドウ Repaint 要求
  （`EditorIteratorPump.cs:69-70`→`IrocaWindow.cs:50`、スロットルなし）。→ 100ms 間引き。
- A9 埋め込みテンソル 4.2MB をデコードのたびに再構築 + 同一データを 3 回コピー
  （`SentisMaskSuggestService.cs:435` の ctor コピー、`:233-235,525-527` の
  `ReadbackAndClone().DownloadToArray()`）。→ `Tensor<float>` のままキャッシュ。

#### [低] A10〜A13
- A10 モデル二重保持（Model 側重み + バックエンドコピー、`Worker.cs:97-100` の
  takeoverWeights 既定 false / `Model.DisposeWeights()` は internal）で常駐 ≈90MB。AI モード
  OFF でも解放パスなし。Sentis API 制約もあり Memory Profiler 実測を推奨。
- A11 中断エンコードの中間テンソルが次回 Schedule まで残る（`EditorIteratorPump.Stop` は
  イテレータを捨てるだけ）。
- A12 NoModel 状態で毎 Layout に `File.Exists`×2（`MaskSuggestSection.cs:101`）+ 成功時
  OnGUI 内 `ForceSynchronousImport`（`SentisModelRepository.cs:93-94`）。
- A13 OnGUI 内の毎フレーム GUIContent/string.Format 生成（`MaskSuggestSection.cs:58-63,84-90`）。

### 分類

**今すぐ直すべき（応答性・メモリ / 品質に無関係）**: A1 非同期 readback 化 → A2 commit の
BG 化 → A3 stage1 精密化の遅延 → A5 CancellationToken 貫通 → A7 stat キャッシュ(3 行) →
A6 暖機の進捗表示。これらは推論自体を速くしなくても体感を大きく変える。

**実装が安定してから**: A4 後処理の bbox 化+型縮小+並列化（順序非依存に書かれており並列化の
障壁はない）、A8 進捗間引き、A9 テンソルキャッシュ、A10〜A13、キャンセル UI（現状、進行中の
提案を止める導線がない — `MaskSuggestController.cs:223-231` はテクスチャ切替/破棄時のみ）。

### 適切に実装できている点

- BG/メインの役割分担方針そのものは正しい（クラスコメント `SentisMaskSuggestService.cs:12-22`
  に設計意図明記）。A1 は方針の実装が一歩足りないだけ。
- 埋め込みキャッシュの二層構成（テクスチャ単位 LRU `:25,640-657` + クロップ単位 LRU
  `:28,588-605`、クロップ矩形の side/8 グリッドスナップ `SamZoomOps.cs:28-31,121-123`）。
- 入力バックログを作らない（進行中は最新クリックのみ保持 `:346-352`、配信直前に保留再確認
  `:464-470`）。`PreviewJob` の世代管理で古い結果の適用事故を構造的に防止。
- Tensor の Dispose 規律（`TryRunDecoderCore` は全テンソル using、`_encInput` は全経路で解放）。
- 退化出力の安価な検出（`IsDegenerate` `:273-285`、4096 点間引き）— Burst 壊れ世代の
  「例外なく空が返る」をほぼゼロコストで検出。
- ズーム判定コストが有界（`ComponentBBoxLongWindowed` の窓 cap）、GPU 失敗時の CPU
  フォールバックが 1 回限り、モデル変換キャッシュが Sentis 版込みキー、Sentis 不在時の
  asmdef レベル完全分離。
- 後処理が順序非依存（スナップショット読み・追加のみ書き）に書かれており、決定性と同時に
  将来の並列化の障壁がない。

**バックエンド補足**: `SentisMaskSuggestService.cs:138` は
`supportsComputeShaders ? GPUCompute : CPU`。AMD GPU でもコンピュートシェーダは使えるため
通常 GPUCompute が選ばれる（過去の「AMD→DirectML/CPU」の知見は別スタックの話でここには
当てはまらない）。CPU に落ちるのは GPU 経路で例外が出たときのみだが、その場合 A1 の症状が
最悪化するため A1 の修正は両経路に効く。

---

## ③ UI・プレビュー・IO

### 発見事項（体感影響順）

#### [高] U1. 詳細プレビューが「描画されないテクスチャ」を含む 3 枚をメインスレッドでアップロード
- 証拠: `DetailPreviewView.cs:239-241` で `rawDetailPreviewTexture` を確保・SetPixels32・Apply
  しているが**描画箇所が存在しない**（PreviewView 側の参照は `detailPreviewTexture`:615,620 と
  `detailDiffTexture`:623 のみ。比較モードは `PreviewView.cs:462` で詳細と排他）。さらに
  `:244` の diff 生成は `diffMode` に関係なく無条件。
- 規模: クロップは可視範囲基準（`:121-139`）でズームが低いほど巨大化。4K・zoom=1.25 で
  約 3482×3413 ≈ 47.5MB/枚 × 3 枚 ≈ **143MB が 1 フレームに集中**。ズーム閾値直上
  （ユーザーがズームインして最初に到達する場所）が最悪ケース。クロップ寸法が頻繁に変わるため
  `TextureSlot.Resize` が Destroy→new を繰り返す。
- 方向性: `rawDetailPreviewTexture` 削除(1/3 削減・数行)。diff は diffMode 時のみ生成。
  クロップ長辺に上限を設け超過時は間引き。

#### [高] U2. ブラシストロークごとに全マスク RLE エンコード + ウィンドウ全体 Undo シリアライズ
- 証拠: `MaskPaintView.cs:795-807` — Begin/End の両方で `SyncBuffersToState()`
  （共通 + 全ゾーンの `AnyTrue` 全走査 + `MaskRle.Encode`、
  `MaskPaintView.Persistence.cs:65-87`）+ `Undo.RegisterCompleteObjectUndo(_host,...)`
  （base64 マスク込みでウィンドウ丸ごとシリアライズ、ストローク数だけスタックに蓄積）。
- 補足: ディスク書き込みはストロークごとには走らない（確認済み）。問題はエンコード + Undo。
- 方向性: Begin 側は前回終了時と同一なので変更フラグで再エンコード省略。Undo 対象を
  maskState のみの軽量オブジェクトへ分離。

#### [高] U3. SelectionCache がゾーンあたり float 67MB(4K) を常駐保持
- ①の F7/F12 と同件（`SelectionCache.cs:62-65`、コメント自身が 67MB/ゾーンと明記 `:78`）。
  `PreviewView.Suspend()`（`PreviewView.cs:304-317`）はテクスチャのみ解放し、
  `_selectionCache` / `_trueSourcePixels`(67MB) / `_cachedSrcPixels` は解放しない。
- 方向性: byte 量子化(1/4) or LRU 保持数制限（`RetainOnly` の枠組みを拡張）。

#### [中] U4〜U8
- U4 プリセット一覧が毎 OnGUI パスで `Directory.GetFiles`（`PresetsView.cs:90`→
  `PresetStore.cs:115-120`、キャッシュなし。プレビュー生成中は連続 Repaint で最大レート実行。
  エクスポート中も `blocking` 下の強制 Repaint で回り続ける `IrocaWindow.Layout.cs:56-81`）。
  → 一覧キャッシュ + 保存/削除時のみ再列挙。
- U5 ゾーンカード本体の GUIContent が毎パス生成（`IrocaWindow.ZoneList.cs` DrawZoneCard/
  DrawZoneAdvancedParams でゾーンあたり約 25 個。5 ゾーン×2 パスで約 250 個/フレーム）。
  ヘッダ行は既にキャッシュ済み（`:105-126`）で不統一 → 同方式へ寄せる。
- U6 切り出しウィンドウが無条件 10Hz Repaint（`IrocaPreviewWindow.cs:140-143`、
  `MaskBrushWindow.cs:68-71`）→ 状態変化時のみに。
- U7 PaintMask がスタンプ行ごとに `new Color32[spanW]`（`MaskPaintView.cs:531`。素早い
  ドラッグで数百〜千個/イベントの小アロケーション）→ フィールドで 1 本使い回し。
- U8 `CleanupOrphans` が OnEnable（＝ドメインリロードのたび）に全件スキャン
  （`IrocaWindow.cs:126-127` → キャッシュ内全ファイルに `GUIDToAssetPath`。orphan 30 日保持で
  ファイル数は編集履歴に比例増）→ セッション 1 回に限定。

#### [低] U9〜U11
- U9 `EnsureTrueSource` のディスク読み込みがメインスレッド同期（`PreviewView.cs:245-249`、
  テクスチャ切替直後の初回 OnGUI で 4K PNG 数百 ms。LoadImage はメインスレッド必須のため
  完全非同期化は不可）。
- U10 Localization の全プロパティが毎回 Auto 言語判定（`Localization.cs:41-52`。実害は
  小さいが静的 bool 化 1 行で消える）。
- U11 一括適用 `RunBatchApply` だけ同期のまま（`ExportView.cs:439-555`）。ただし
  `DrawBatchSection()` はコメントアウト中（`IrocaWindow.Layout.cs:222,278`）で**現状 UI から
  到達不能**。再有効化時に単体エクスポートと同じ非同期パターン必須（4K フリーズが復活する）。

### 適切に実装できている点

- `PreviewJob` の世代管理 + キャンセル + 単一メインスレッドポンプ（`PreviewJob.cs`）。
- プレビュー再生成の 0.2 秒デバウンス + hotControl 待ち（`PreviewView.cs:361-369`）。
- プロキシ(512px)→フルの段階的リファイン（`PreviewView.Async.cs:105-177`、出力バイト不変）。
- ペイント中のオーバーレイ直接書き込み + イベント単位 Apply 集約 + 再構築 10Hz スロットル。
- 詳細プレビューのクロップ可視範囲限定 + 0.3 秒デバウンス、diff 計算の BG 化。
- 表示テクスチャは mipmap なし・同寸法再利用、メインプレビュー 384px 固定。
- 単体エクスポートの完全非同期化（処理 + PNG エンコード + 書き込みを BG、メインは
  `ImportAsset` のみ。`AssetDatabase.Refresh()` は Code/ 全体で 0 件）。
- `AtomicFile`（一時ファイル → `File.Replace`）、保存契機の限定（ウィンドウ閉じ・テクスチャ
  切替・プリセット適用のみ）、orphan のソフト削除 + 30 日猶予、`IrocaAssetWatcher` は
  削除確定時のみ発火（全インポートに乗らない）。
- `MaskRle` は O(n) 1 パス + run 数は境界長オーダー（典型マスクで軽量）。

---

## 推奨着手順

### アルゴリズム側（すべて「出力ビット不変」を主張できる変更）
1. **F12** 選択キャッシュに RegionStats 同梱 — 色ドラッグ 3.1s→0.9s。体感インパクト最大
2. **F1** RegionStats 並列化 — 全体 -30%。キャッシュミス時にも効く
3. **F2** デコンタミ bbox 化 — 小 match ゾーンで -30% + メモリ削減（F3 と同時改善）
4. **F6** キャンセルチェック — 実時間不変だがドラッグ体感が大きく変わる。数十行
5. **F4** AutoTuner 並列化 — 5.5s→1s 未満
6. F5 / F7 / F8 / F10 / F11 は上記の後

検証: improvement-cycle の手順（regression 全パス + `visual_review.py compare --engine csharp`
→ Read 目視 → approve）に加え、**変更前後の out.raw の直接 byte 比較**
（`scripts/headless-run`）がビット不変主張の最速検証。

### AI 側（今すぐ = 品質に無関係な応答性修正）
1. **A1** 非同期 readback 化（`ReadbackRequest` + `IsReadbackRequestDone` ポーリング）
2. **A2** commit の GetPixels32 + IncludeAaTransition を BG へ
3. **A3** stage1 を `pixelsBottomUp: null` で回し精密化を遅延
4. **A5** CancellationToken 貫通
5. **A7** stat キャッシュ（3 行）/ **A6** 暖機の進捗表示
- A4 後処理の bbox 化・並列化、A8〜A13 は実装安定後

### UI 側
1. **U1** `rawDetailPreviewTexture` 削除（数行で -47MB/回）+ diff の条件付き生成
2. **U2** ストローク開始時の再エンコード省略 + Undo 軽量化
3. **U4** プリセット一覧キャッシュ

## 未実施・制約

- Unity Editor 実機での計測は未実施（すべて headless dotnet 上の実 C#）。並列段は Editor の
  スレッドプール圧迫下で実効が変わり得る（`GetMaxParallelism()=ProcessorCount-2`、
  `PixelProcessor.cs:25-32`）。単スレッド段（F1/F4/F5）の数値は環境非依存。
- AI マスク提案の実レイテンシ実測（クリック→表示の秒数）は Unity 実機 + Memory Profiler が
  必要。本評価の②はコード構造 + Sentis ソースからの裏取りによる。
- 実測値は実行間ノイズ ±15〜30% があるため代表値。改善検証時は同条件で前後比較すること
  （`PROCESS_MS` / `PHASE` 行は stderr に出る）。
