# パフォーマンス改善計画

`performance_optimization_cpu.md` の提案をコードと突き合わせて評価した結果をまとめ、実施順を決定した計画書です。

## 既存提案の妥当性評価

| # | 提案 | 評価 | 判断理由 |
|---|---|---|---|
| 1 | RecolorPixel の HSV 事前計算 | ◎ 採用 | `target`/`sample` は zone 内で定数なのに毎ピクセル `RGBToHSV` している。容易かつ効果大 |
| 2 | ArrayPool 化 | ○ 採用（限定的） | 効果あり。ただし `out` で返す配列の所有権が複雑なので寿命明確な配列から先行実施 |
| 3 | Job System + Burst | △ 見送り（Phase3以降） | 効果は大きいが Editor 拡張ツールへの Burst/Collections パッケージ追加は慎重に要検討 |
| 4-a | 暗黙キャスト排除 | ○ 採用 | HSV 配列事前計算（改善5）で自然に解決される |
| 4-b | Sqrt 回避 | × 不採用 | `CalculateHybridDistance` の `rgbDist` は HSV 距離との Lerp に使うため「二乗比較」に置換できない |
| 4-c | 軽量 HSV / 一括変換 | ◎ 採用 | 同一ピクセルで複数箇所が `RGBToHSV` を呼んでいる。事前 HSV 配列で全て統合可能 |
| 5 | Array.Copy 削減 | × 不採用 | `FillSmallHoles`/`RecoverBoundaryEdges` のダブルバッファは「書き込まれない位置の前回値保持」のためフルコピーが必須。差分追跡はコスト対効果が合わない |

## コードレビューで発見した追加改善点

| # | 箇所 | 問題 | 改善策 |
|---|---|---|---|
| A | `ConstrainBlur` [Processing.cs:526-552] | 各ピクセルで `(2r+1)²` の近傍走査 → O(N·r²) | `original>0` をfloatマスク化して `BoxFilterSum` に流す → O(N) |
| B | `Parallel.For(0, len, i => i%w, i/w)` [Processing.cs:87,143,166] | ホットループ内で毎ピクセル除算 | `Parallel.For(0, h, y => for x …)` の行ベースに変更 |
| C | `UpdateCacheIfNeeded()` [ColorZone.cs:141] | 全ピクセルのホットループ内で条件分岐 | zone ループ前に 1 回だけ呼ぶ |
| D | `GaussianBlur` 内 `Mathf.Clamp` [Processing.cs:495,510] | タップごとに分岐 | 境界行/列を別ループにし中央は無分岐化（Phase3で対処） |

---

## 実施計画

### Phase 1 — 即効・低リスク

#### [1-1] RecolorPixel の HSV を zone 外で事前計算
- **対象**: `VACCWindow.Processing.cs` の `RecolorPixel` 呼び出し箇所
- **変更内容**: `RecolorPixel(original, target, sample, valueBlend)` の引数を HSV 展開済みの形式に変更し、`target`/`sample` の `RGBToHSV` をゾーンループの外で1回だけ計算する
- **効果見込み**: 4K テクスチャで約 20〜30% 短縮

#### [1-2] `Parallel.For` を行ベース化
- **対象**: `ProcessPixelsArray` 内の 3 箇所の `Parallel.For(0, len, i => …)`
- **変更内容**: `Parallel.For(0, h, y => { for (int x = 0; x < w; x++) { … } })` に変更し、`i%w`/`i/w` を撤去
- **効果見込み**: 毎ピクセル除算削減で 5〜10% 短縮

#### [1-3] `UpdateCacheIfNeeded` を zone ループ前に移動
- **対象**: `ColorZone.GetMatchScores` → `UpdateCacheIfNeeded` の呼び出し
- **変更内容**: `ProcessPixelsArray` の zone ループ先頭で `zone.UpdateCacheIfNeeded()` を事前呼び出し。`GetMatchScores` 内ではキャッシュが有効なら不要な条件分岐を消す（または既存のキャッシュチェックをそのまま活かす）

---

### Phase 2 — 中規模・中リスク

#### [2-4] `ConstrainBlur` を BoxFilter ベースに置換
- **対象**: `VACCWindow.Processing.cs` の `ConstrainBlur`
- **変更内容**: `original > 0` を float マスク（0.0/1.0）配列に変換し、`BoxFilterSum` を適用。結果が 0 ならそのピクセルは `hasNearby == false` と判定
- **効果見込み**: `edgeFeather` が大きい場合に数倍の高速化

#### [2-5] HSV 配列の事前計算・共通化
- **対象**: zone ループ全体
- **変更内容**: zone ループ前に `float[] pixelH, pixelS, pixelV` を1回計算。`GetColorMatchScores`・`RecoverBoundaryEdges`・`ApplyFloodFillMask` 内の `RGBToHSV` 呼び出しを配列参照に置換
- **効果見込み**: zone 数が多いほど効果増大。`Color32 → Color` の暗黙キャスト問題も同時解決

#### [2-6] ArrayPool 化（寿命明確な配列）
- **対象**: 以下の配列（zone ループ内で確保・使用・破棄が明確）
  - `strength` / `highlightPot` — `new float[len]`
  - `BoxFilterSum` 内の `temp` / `result` — `new float[w*h]`
  - `GaussianBlur` 内の `temp` / `result` — `new float[w*h]`
  - `FillSmallHoles` / `RecoverBoundaryEdges` の `buffer` — `new float[len]`
- **除外**（寿命が複雑）: `aaMask`, `decontaminatedPixels`（`out` で外部に渡す）
- **効果見込み**: GC スパイク（プチフリーズ）の削減

---

### Phase 3 — 大規模（今回スコープ外）
- Burst Compiler + C# Job System への段階的移行
- `GaussianBlur` の境界分離による無分岐化
- 測定ハーネス（ベンチマーク）の `dev_safe/Tests/` 追加

---

## 採用/不採用の記録

| 変更 | 採用 | 理由 |
|---|---|---|
| Phase1-1 RecolorPixel HSV 事前計算 | ✅ | 効果明確・リスク小 |
| Phase1-2 Parallel.For 行ベース化 | ✅ | 毎ピクセル除算撤去 |
| Phase1-3 UpdateCacheIfNeeded 移動 | ✅ | ホットループ内条件分岐撤去 |
| Phase2-4 ConstrainBlur → BoxFilter | ✅ | O(N·r²)→O(N) |
| Phase2-5 HSV 配列事前計算 | ✅ | zone 多重で大きく効く |
| Phase2-6 ArrayPool 化 | ✅ | GC スパイク軽減 |
| Sqrt 回避 | ❌ | rgbDist は Lerp 係数と結合しており二乗比較不可 |
| Array.Copy 差分更新 | ❌ | ダブルバッファ構造の前回値保持にフルコピーが必須 |
| Job System + Burst | ⏸ Phase3 | 依存追加コスト・配布影響を要検討 |
