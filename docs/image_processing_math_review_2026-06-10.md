# 画像処理コード 数学的レビュー (2026-06-10)

対象: `Code/Core/PixelProcessor.cs` / `Code/ColorZone.cs` / `Code/Core/ZoneAutoTuner.cs` / `Code/Core/HighlightSampleCorrector.cs`

再着色パイプラインの中核を「数学的に正しいか」の観点でレビューした記録。
結論: **中核（OkLab 変換・α分解・ブラー・L リマップ・多ゾーン合成）は数学的に正しい**。
直すべきは「ガマット外色の後処理」と「本体マッチとエッジ系ヘルパー間の式の同期ズレ」に集約される。

---

## 1. 数学的に正しいと検証できた点

| 項目 | 場所 | 検証内容 |
|------|------|----------|
| OkLab 変換行列 | `PixelProcessor.cs` RgbToOklab / OklabToRgb | RGB↔OkLab の行列係数 9+9 個、LMS→Lab 係数、sRGB 伝達関数の閾値 (0.04045 / 0.0031308 / 12.92 / 2.4) を Björn Ottosson のリファレンス実装と照合し**全桁一致**。負値の立方根処理も正しい |
| L の 2 区間線形リマップ | `PixelProcessor.cs` RecolorPixel | 単調・端点保存 (0→0, 1→1, sL→tL)。リング除去の設計意図どおり。ゼロ除算ガードも適切 |
| α 分解 (decontamination) | `PixelProcessor.cs` DecontaminateAaBoundary | `α = dot/‖dir‖²` は BG→sample 直線への正射影として正しい。α をクランプ**してから**線分への距離で前提崩れを棄却する順序も正しい（線分の外側にある画素は正しく弾かれる） |
| 分離型ガウシアン | `PixelProcessor.cs` GaussianBlur | カーネルは 2.5σ 打ち切り後に正規化しているためエネルギー損失なし。BoxFilterSum のスライディングウィンドウも O(N) で正しい |
| 色相の環状距離 | 全箇所 | `d > 0.5 ? 1-d : d` が ColorZone / GrowHighlightBand / ZoneAutoTuner / HighlightSampleCorrector で一貫 |
| 多ゾーン合成 | `PixelProcessor.cs` Recolor 段 | front-to-back の「残り room へ delta 加算」方式は、展開すると `orig·(1−Σes) + Σ recolorₖ·esₖ` の凸結合 (partition of unity) で整合。単一ゾーン時のバイト単位互換も維持 |
| V スケーリング | `HighlightSampleCorrector.cs` ComputeWashSample | RGB の一律スケールは HSV では V のみ変化し H・S 不変、という性質を正しく利用 |

距離式 `hDist + sDist·w₁ + vDist·w₂·(1−sRatio)` は単位の異なる量の線形結合（ヒューリスティック）だが、
tolerance を ZoneAutoTuner が**同一の距離式**で P99.9 から導出しているため内部整合は取れている。

---

## 2. 問題点・改善点（影響が大きい順）

### 2.1 ガマット外色のチャンネル別クランプ【最重要・未対応】

- 場所: `PixelProcessor.cs` `LinearToSrgb`（`Clamp01` でチャンネル毎に切っている）
- 問題: OkLab 上で計算した色がガマット外に出ると **L と色相の両方が歪む**。出やすい条件:
  - 高彩度 target × 元画素の chroma が sample より大きい場合（`mag = oC·osat/sC` で chroma が target 超え）
  - 純色 target の明部（高 L + 大 chroma は sRGB ガマットの「尖り」の外）
- これは「純色 target で陰影が潰れる」現象（outputSaturation スライダーで対処済み, b234409）の数学的背景そのもの。
- **改善案: L と色相を固定して chroma だけ縮めるガマットマッピング**。
  `(L, t·a, t·b)` の係数 t を in-gamut になるまで二分探索（10 回程度の反復で十分）。
  L 保存（=リング無し）の設計保証がガマット外でも維持され、outputSaturation を手で下げる必要性も減る。
  改善サイクルの次の仮説として有力。

### 2.2 BoxDownsample が透明画素の RGB を無加重平均【未対応】

- 場所: `PixelProcessor.cs` `BoxDownsample`
- 問題: RGB と α を独立に平均しているため、a=0 画素の RGB ゴミ（多くは黒）が混入し、
  **透明境界のプレビューに暗いフリンジ**が出る。
- 改善案: 非乗算 RGBA の正しい縮小は α 加重平均 `R = Σ(r·a)/Σa`（Σa=0 のときのみ無加重にフォールバック）。
- プレビュー専用だがユーザーが品質判断する画像なので修正価値あり。

### 2.3 relaxed マッチと本体マッチのグレー判定が不整合【未対応】

- 場所: `PixelProcessor.cs` `GetRelaxedMatchStrength` vs `ColorZone.GetColorMatchScores`
- 問題 (a): relaxed 版は `chromaThreshold` が **0.05 にハードコード**。本体は `zone.chromaThreshold` を使い、
  ZoneAutoTuner は低彩度サンプルでこれを最大 0.20 まで引き上げる。
  → **sS が 0.05〜0.20 のゾーンでは、本体はグレーモードなのに境界復元・穴埋めゲートは有彩モードで判定**される。
- 問題 (b): グレーモードの距離式が、本体は RGB ユークリッド距離（×1/√3 正規化）、relaxed 版は `|pV−sV|` と異なる。
  コメントは「ColorZone と同じ」と書いてあるが実際は違う。
- 改善案: `zone.chromaThreshold` を引数で渡し、距離式を揃える。意図的な緩和ならコメントを実態に合わせる。

### 2.4 シャドウ免除の係数・ゲートの細かい不整合【未対応】

- relaxed 版の暗部距離免除は係数 0.2（=最大 80% 免除）なのにコメントは「最大70%」。
  本体 `CalculateHybridDistance` は 0.3（=70%）。数値かコメントのどちらかを揃えるべき。
- `GetColorMatchScores` のゲート緩和は `effectiveHDist`（hueRelevance 減衰後）で判定するのに、
  `CalculateHybridDistance` の距離免除は生の `hDist` で判定。
  低彩度画素では effectiveHDist < hDist なので「ゲートは免除されるが距離は免除されない」非対称な中間状態が生じる。
  意図的でなければ統一を。

### 2.5 `chromaThreshold > 0.30` で動的閾値の補間方向が逆転【未対応】

- 場所: `ColorZone.GetColorMatchScores` の `Mathf.Lerp(0.30f, chromaThreshold, sV/0.20)`
- 「暗いほど閾値を広げて 0.30 に近づける」意図だが、スライダー上限は 1.0 なので
  0.30 を超える設定では**暗いサンプルほど閾値が下がる**逆挙動になる。
- 改善案: `Mathf.Lerp(Mathf.Max(0.30f, chromaThreshold), chromaThreshold, …)` で方向を保証。

### 2.6 軽微な点

| 項目 | 内容 |
|------|------|
| パーセンタイルの bin 下端バイアス | `ZoneAutoTuner.PercentileBin` は bin index をそのまま返すため最大 1/32 (≈0.03) の過小評価。bin 中心 `i+0.5` がより正確（mask-aware 版は `i+1` の上端を使っており安全側で正しい） |
| sRGB 空間での α 分解 | decontamination は AA を sRGB のまま分解しているが、テクスチャ作成ツール（Photoshop 既定等）はガンマ空間で合成するため**この入力データに対してはむしろ正しい仮定**。意図的な選択としてコメントに残す価値あり |
| 縮小平均のガンマ非考慮 | BoxDownsample が sRGB のまま平均するためプレビューがやや暗めに出る。用途上許容範囲 |
| Cbrt の性能 | `Mathf.Pow(x, 1/3)` は `System.Math.Cbrt`（.NET Standard 2.1 / Unity 2021.2+）へ置換可能。per-pixel ホットパスで往復 6 回呼ばれるため効く |

---

## 3. 対応状況の追跡

このレビュー時点ではすべて**記録のみ・コード未変更**。対応した項目はこの表を更新する。

| # | 項目 | 状態 |
|---|------|------|
| 2.1 | ガマットマッピング（chroma 縮小） | 未対応 |
| 2.2 | BoxDownsample α 加重平均 | 未対応 |
| 2.3 | relaxed/本体のグレー判定統一 | 未対応 |
| 2.4 | シャドウ免除係数・ゲート整合 | 未対応 |
| 2.5 | chromaThreshold Lerp 方向保証 | 未対応 |
| 2.6 | 軽微項目（bin 中心 / Cbrt 等） | 未対応 |
