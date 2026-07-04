# OKLab ベース選択距離 — 実験計画

対象: `Code/Core/ColorZone.cs` / `Code/Core/ColorZone.Match.cs`（ピクセル選択＝マッチングエンジン）
非対象（変更しない）: `Code/Core/PixelProcessor.Recolor.cs` の OkLab 発色本体（`RecolorPixel`）。本計画は「どの画素を選ぶか」だけを扱う。

関連ドキュメント:
- [`image_processing_math_review_2026-06-10.md`](image_processing_math_review_2026-06-10.md) — OkLab 変換の数学的正しさの検証（再着色側）
- [`recolor_design_rationale.md`](recolor_design_rationale.md) — 再着色パイプライン全体の設計思想（本計画はこれの「選択」側の対応物）
- [`testing-architecture.md`](testing-architecture.md) — 実 C# ハーネスでの計測方法（本計画の評価はすべてこれに従う）
- [`flood-fill-reactivation-plan.md`](flood-fill-reactivation-plan.md) — 選択精度改善の別アプローチ（連結性）。本計画は「色距離そのもの」の改善を扱い、独立している

---

## 0. 背景・経緯

再着色（発色）は `RecolorPixel` で OkLab を中核に据えているが、選択（どの画素をゾーンの対象とみなすか）は `ColorZone.Match.cs` の HSV+RGB ハイブリッド距離で行われており、**2 つの処理が別々の色空間・別々の数式で動いている**。

この非対称性についてユーザーと検討した結果:
- OKLab 距離の方が理論的に優れた性質を持つ（後述）が、
- 現行の HSV/RGB ハイブリッド距離は多数の改善サイクルで個別にチューニングされた極めて繊細なコードであり、
- 全面置換は大規模な較正コストとプリセット互換性リスクを伴う。

そのため「まず束縛された実験として試し、数値と視覚レビューで有効性を確認してから採否を判断する」という方針になった。**本ドキュメントはその実験を実行するための設計・手順書**であり、実装そのものはまだ行っていない。

> 補足（隣接する過去の意思決定との混同注意）: 発色側で検討された「ガマットマッピング（chroma 二分圧縮）」は本計画とは**別件**であり、既に「鮮やかな target 色の明部が脱彩してテクスチャを壊す」という理由で不採用が確定している（§5 参照）。本計画はそれとは独立に、選択（マッチング）側の距離式のみを対象とする。

---

## 1. 現状の選択エンジン（as-is）

### 1.1 処理フロー

`ColorZone.GetColorMatchScores` (`ColorZone.Match.cs:157`) → 各サンプル（主サンプル＋追加スポイト）に対して `MatchOneSample` (`:182`) を呼び、最大強度を採る（和集合マッチング）。

`MatchOneSample` は分岐する:

| 分岐 | 条件 | 距離の測り方 |
|---|---|---|
| **グレー抽出モード** | `sc.sS <= effectiveChromaThreshold`（サンプル彩度が動的しきい値以下） | Hue/Sat を無視し純 RGB ユークリッド距離のみ（`:208-212`）。暗いサンプルでは彩度自体を距離指標に混ぜる特殊処理あり（`:219-223`） |
| **有彩モード** | 上記以外 | `CalculateHybridDistance` (`:330`)：Hue 環状距離＋Sat 距離＋Value 距離（HSV側）と、RGB ユークリッド距離を `chromaConfidence` で線形補間（`:343`） |

### 1.2 なぜこの形になっているか

コード中のコメントに明記されている通り、**HSV の Hue は低彩度領域で数学的に不安定**（彩度が 0 に近づくと色相角がノイズで暴れる）。これに対処するため、以下の"パッチ"が積み重なっている:

- `effectiveChromaThreshold`（`:205`）: サンプルが暗いほどグレーモードの適用範囲を動的に広げる（暗い色は Hue が特に信用できないため）
- `chromaConfidence`（`ColorZone.cs` の `BuildSampleCache`, `ColorZone.Match.cs:90`）: Hue ベース距離と RGB 距離を線形補間する重み。彩度が低い・明度が低いサンプルほど RGB 距離側に寄せる
- `hueRelevance`（`:265-266`）: 画素自身の彩度が低いときも Hue の寄与を減衰させる
- グレーモード内の暗サンプル特殊処理（`:219-223`）: 「無彩色なら明度差があっても同素材」とみなす

さらに、同色相の陰影を拾うための非対称なヒューリスティックが乗っている:

- 暗部許容（`:269-286`）: サンプルより暗く同色相なら彩度ゲートを緩和（※旧・距離自体の短縮版は**廃止済み** — 巻き込みの主因だったため。`ColorZone.Match.cs:345-350` のコメント参照）
- 明部許容（`:293-306`, `CalculateHybridDistance` 内 `:355-365`）: サンプルより明るく同色相ならハイライト芯を同素材とみなし、彩度ゲート・距離の両方を緩和（暗部の対称形として残存）
- ハイライト回復（`CalculateHighlightRecovery`, `:370`）: 別軸のハイライト専用マッチ

### 1.3 主要定数と較正の結合点（`ColorZone.cs:18-91`）

| 定数 | 値 | 役割 |
|---|---|---|
| `CoreMatchDistance` | 0.14 | flood-fill のコア判定に使う固定半径。**現在の距離式のスケールに直接依存**（`ColorZone.cs:49-58`） |
| `chromaThreshold`（ゾーンごと） | 既定 0.05 | グレーモード切替のしきい値。`ZoneAutoTuner` が自動導出 |
| `GrayModeBaseChromaThreshold` / `GrayModeChromaConfidenceRamp` | 0.30 / 0.20 | 暗いサンプルでのグレーモード適用範囲拡張 |
| `ForgivenessHueGate` / `ForgivenessRangeFrac` / `*HeadroomFrac` | 0.15 / 0.6 / 0.25 | 陰影許容ヒューリスティックのゲート・範囲 |
| `BrightDistanceForgivenessMin` | 0.3 | ハイライト距離免除の下限（最大 70% 短縮） |
| `ChromaGateActivateSat` / `ChromaGateFloorFrac` | 0.02 / 0.5 | 無彩色ターゲットへの中性画素巻き込み防止ゲート |

さらに `ZoneAutoTuner.Tolerance.cs` の `TryDeriveChromaticTolerance`（percentile ベースで tolerance を自動導出）は、**この距離式そのものを使って実テクスチャの距離分布をサンプリングし、しきい値を導出している**。距離式の土台を変えると、この自動導出ロジックも数値スケールごと再較正が必要になる。

### 1.4 このモジュールがどれだけ繊細か（過去の教訓）

このマッチング距離は、プロジェクト史上もっとも多くの改善サイクルを経たコードである。代表例:
- 暗部の距離短縮を実装 → 別マテリアルへの巻き込みが実測で主因と判明し**削除**（明部は温存、非対称は意図的）
- flood-fill のコア判定を `strength`（tolerance 依存で不安定）から `matchConf`（固定半径・tolerance 非依存）へ変更（`testing-architecture.md` は関与しないが `flood-fill-reactivation-plan.md §8.1` に詳細）
- 有彩→無彩ターゲットで中性画素（白背景等）を巻き込み黒化するバグを、相対彩度床のリジェクトゲートで修正（複数回の閾値調整を経て収束）
- relaxed マッチ（`PixelProcessor.Selection.cs` 側の緩和経路）と本体マッチでグレー判定が非同期になっていた不整合を修正

**この事実が意味すること**: 距離式を変えると、これら個別に潰してきた症状の一部がぶり返す可能性が高い。全面置換は「1 つの改善」ではなく「これまでの全チューニングのやり直し」に等しいコストを持つ。

---

## 2. 仮説

OKLab の chroma `C = sqrt(a²+b²)` は彩度が下がるにつれ `a, b` ごと滑らかに 0 へ縮み、HSV の Hue のような「彩度 0 で角度が定義不能になる」特異点を持たない。したがって:

- グレーモード／有彩モードの**ハードな分岐**（`effectiveChromaThreshold` での切替）が、OKLab 距離では単一の連続式に統合できる可能性がある。
- `chromaConfidence` によるブレンドや `hueRelevance` 減衰といった「Hue 不安定性を後から抑える」パッチ群が、そもそも不要になる、または単純化できる可能性がある。
- 暗いサンプルでの特殊処理（`:219-223` 等）も、L（知覚明度）ベースで測ればより素直に表現できる可能性がある。

**ただし** 陰影許容（暗部/明部フォギブネス）は色空間を変えても必要な概念（「同じ地色で明るさだけ違う画素を同一素材とみなす」）であり、OKLab に移行しても L と C を使って類似のヒューリスティックを再実装することになる。**複雑さそのものが消えるわけではなく、より安定した基盤の上に載り直す、というのが期待できる効果の上限**と捉えるべき。

---

## 3. 実験のスコープと非破壊の原則

**最重要: 本番のデフォルト距離式（`CalculateHybridDistance` とその周辺）は変更しない。** 新しい OKLab 距離を、既存経路と並行して検証できる隔離された経路として実装する。

### 3.1 実装方針（提案）

1. `ColorZone.Match.cs` に `MatchOneSampleOklab`（仮称）を新設し、既存の `MatchOneSample` とシグネチャを揃える。呼び出し側で切替可能にするが、**既定は必ず従来経路**（`ContainsPixel` / `GetMatchScores` 等の公開 API の既定動作は不変であること）。
2. `scripts/headless-run/Harness.cs` に `--matchDistance=hsv|oklab` のような CLI フラグを追加し、`dev_safe/Tests/regression/fixtures.py` からどちらの経路でも同じ GT に対して計測できるようにする（`testing-architecture.md` の計測経路一本化の原則を維持したまま、経路を選べるようにする）。
3. 非回帰ガード: フラグ既定値（`hsv`）で全 golden / 回帰テストが**バイト単位で不変**であることを最初に確認する（新コードパスを追加しただけで既存出力が 1 bit も変わらないことの証明）。

### 3.2 OKLab 距離式のたたき台

以下は出発点であり、実装者が改善サイクルの中で調整してよい。

```
distOklab = wL * |L_p - L_s| + wC * |C_p - C_s| + wH * hueDistAB(p, s) * hueRelevanceOklab
```

- `L_p, L_s`: 画素・サンプルの OKLab L（発色側 `RgbToOklab` を再利用可能、`PixelProcessor.ColorSpace.cs:97`）
- `C_p, C_s`: chroma `sqrt(a²+b²)`
- `hueDistAB`: a,b 平面上の角度差（`atan2(b,a)` の環状距離）。**低 chroma で角度が不安定になる問題を、HSV 同様どう抑えるかが検証ポイント**（仮説では「HSV より緩やかに劣化する」が「特異点が完全に消える」わけではない可能性があるため、実測で確認する）
- `hueRelevanceOklab`: 上記の角度不安定性を抑えるための重み。`min(C_p, C_s)` ベースなど、既存の `hueRelevance`（`ColorZone.Match.cs:265`）に相当する仕組みが結局要るかどうかも検証項目に含める
- 重み `wL, wC, wH` は現行の `valueWeight` / `satDistWeight` と同様、ゾーンごとの調整可能パラメータとして残すか、OKLab の知覚均等性を活かして固定係数にできるかも論点

暗部/明部フォギブネス（`:269-306`）は `V` の代わりに `L` を使って同型のロジックを再実装する。

### 3.3 評価手順（`improvement-cycle` 準拠）

1. **現状計測**: `python -m pytest dev_safe/Tests/regression/ -q` で現行（HSV/RGB）のベースラインスコアを確認。
2. **OKLab 経路を実装**（1 コミットで完結させない。まずは隔離実装のみ）。
3. **同一 GT・同一しきい値**で `--matchDistance=oklab` を全 subject（bandana / costume / sneakers 系列、`false_positive` 系含む）に対して実行し、**現行との差分**を IoU / precision / recall で比較。この時点では `CoreMatchDistance` や `chromaThreshold` 等の定数は流用でよい（後述 §3.4 で再較正）。
4. **弱点ケースの重点確認**: グレー〜低彩度境界（暗い紺色などの near-black 別素材、bandana の低彩度領域）、ハイライト境界、三角抽出（Feina）のような小連結・非白背景ケース。ここが仮説の効き所であり、同時に既存の非対称ヒューリスティックが最も密に張られている場所でもある。
5. **定数の再較正**（有望な場合のみ）: `CoreMatchDistance` 相当値、`chromaThreshold` 系のしきい値、`ZoneAutoTuner` のパーセンタイル導出を、OKLab 距離のスケールに対して**同じ方法論**（実テクスチャの距離分布から導出）でゼロから再導出する。既存の数値をそのまま流用しない。
6. **視覚レビュー必須**: `python tools/visual_review.py snapshot` → 変更 → `compare --engine csharp` → 生成 PNG を Read で 1 枚ずつ確認 → `approve`。選択境界の変化は最終出力の色にも波及するため、境界部のクロップを重点的に見る。
7. **判定**: 複数被写体・複数 tolerance 帯で**現行を明確に上回り、かつ新規の失敗モードが出ない**場合のみ「採用候補」とする。横並びで同等、または一部改善一部悪化なら、無理に置き換えず知見を記録して現状維持。

### 3.4 採否判定後の話（本計画のスコープ外）

もし採用と判断された場合、本番のデフォルトを切り替える作業（プリセット `tolerance` 値の意味変化に対するマイグレーション方針の決定含む）は**別の作業として、ユーザーの仕様判断を仰いでから**行う。本計画は「判断材料を揃える実験」までを範囲とする。

---

## 4. 非目標

- **ガマットマッピング（発色側のchroma二分圧縮）の再検討はしない。** 既に「L 保持の chroma 縮小だと鮮やかな target の明部が大きく脱彩しテクスチャを壊す」という仕様判断で不採用が確定している（`flood-fill-reactivation-plan.md:10`）。本計画とは完全に独立した話題であり、混同しないこと。
  - 補足: `recolor_design_rationale.md` のロードマップ状態表（§8）は Tier1 ガマットマッピングを「着手」のままにしており、この不採用決定を反映できていない。本計画の実装者が触れる範囲ではないが、気づいた場合はそちらのドキュメントも「不採用（仕様判断）」に更新しておくと良い。
- **本番デフォルトの即時置換はしない。** まず計測・視覚レビューで有効性を確認するフェーズ。
- **マスク/連結性による改善（`flood-fill-reactivation-plan.md`）とは独立。** 本計画は「色距離そのもの」の精度を扱う。

---

## 5. 実装者への申し送り・未決定論点

- グレー/有彩のハードな分岐は完全に撤廃できるか、それとも OKLab でも何らかの分岐や重み減衰が必要か（§2 の仮説の核心。まず小規模な合成テクスチャで Hue 角度の低 chroma 挙動を可視化して確認するのが安全）。
- 暗部/明部フォギブネスを L 基準に翻訳する際、既存の非対称性（暗部の距離短縮は廃止済み・明部のみ温存）をそのまま踏襲するか、OKLab の知覚均等性により対称に戻せる余地があるか。
- `wL, wC, wH` の重みづけは、現行の `valueWeight`/`satDistWeight` のようにユーザー調整可能なゾーンパラメータとして残すか、それとも知覚均等性を活かして固定にするか。
- ハイライト回復（`CalculateHighlightRecovery`）・彩度ガード（`saturationGuard`）など、Hue/Sat に依存する周辺機能をどう翻訳するか、あるいは当面 HSV 版のまま残し主距離式だけ差し替えるハイブリッド構成にするか。

---

## 6. 参考資料

- `Code/Core/ColorZone.Match.cs` — 現行マッチングエンジン本体
- `Code/Core/ColorZone.cs:18-91` — マッチング関連定数
- `Code/Core/PixelProcessor.ColorSpace.cs` — OKLab 変換ヘルパー（`RgbToOklab` 等、再利用可能）
- `Code/Core/ZoneAutoTuner.Tolerance.cs` — tolerance 自動導出（距離式のスケールに依存）
- `docs/testing-architecture.md` — 実 C# ハーネスでの計測方法（本計画の評価は必ずこれに従う）
- `docs/flood-fill-reactivation-plan.md` — 選択精度改善の別アプローチ、およびガマットマッピング不採用の経緯（§0 補足）
