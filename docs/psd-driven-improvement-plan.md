# PSD教師データを活用したアルゴリズム改善計画

## 概要

VACC（VRC AvatarColorChanger）はPSDなしのフラットPNGに対して動作するツールだが、
PSDが手元にある場合はレイヤー情報を「**教師データ（ground truth）**」として活用できる。
教師データを使ってアルゴリズムが抱える問題を数値で特定し、体系的に改善を進める。

---

## 1. PSDから何が得られるか

PSDのレイヤーを展開すると、フラットPNGだけでは決して得られない情報が手に入る。

| 情報 | 取得方法 | 使い道 |
|---|---|---|
| **対象領域の完全マスク** | 「Blue」「衣装」等のベースカラーレイヤーのα | 何を変換すべきかの正解ピクセルセット |
| **エッジの正確な位置** | マスクのα輪郭（≈アンチエイリアス境界） | AA境界回復の効果検証 |
| **AO/Shadow/Back レイヤーの影響範囲** | 合成前後の差分 | bleed被疑ピクセルの由来特定 |
| **レイヤーブレンドモード** | `psd-tools` で取得 | multiply/overlay等が彩度を下げる量の計算 |
| **アウトライン領域** | outline レイヤーのマスク | アウトライン部分は変換すべきでないか判断 |

---

## 2. 現状の実装状況

### 2-1. 既に実装済みの部分

```
PSD → gen_ground_truth.py → ground_truth/ → テスト
```

| ステップ | ファイル | 内容 |
|---|---|---|
| PSD → マスクPNG | `AVATAR_A/gen_ground_truth.py` | Blueレイヤーのα → `hair_mask.png` |
| PSD → 正解画像 | 同上 | HSV recolor で `gt_{suffix}.png` 生成 |
| PSD → fixtures.json | 同上 | サンプルカラー・bbox・テストケース |
| テスト（AvatarA） | `AvatarATextureTests.cs` | VividCoverage / OutsideBlueBleed / SilverBleed |
| テスト（AvatarB） | `RealTextureTests.cs` | HueError / Coverage / BleedOutsideTolerance |
| 分析スクリプト | `analyze_tradeoff.py` 他 | satMin閾値のトレードオフ数値化 |

### 2-2. 現状の限界

1. **教師データは「最終合否判定」にしか使われていない**  
   テストは「出力が正解と一致しているか」を測定するが、  
   「どのピクセルが失敗しているか」「なぜ失敗するか」は個別に追わない。

2. **失敗パターンが分類されていない**  
   - AA境界の取りこぼし（低彩度エッジ）
   - AO/Shadow レイヤーによる誤検出（bleed）
   - アウトライン・グラデーション領域の特殊挙動  
   …が区別されないまま「total FN/FP」としか計測されない。

3. **パラメータ調整が経験則に頼っている**  
   `satMin = sS * 0.50` は `analyze_tradeoff.py` で数値化されたが、  
   他のパラメータ（`antiAliasCleanup` のパス数、`tolerance` の適正値）は  
   PSDの実データで体系的に最適化されていない。

4. **複数アバター・複数パーツへの汎化が未検証**  
   AVATAR_Aの髪とAvatarBのバンダナで2種類しかテストされていない。

---

## 3. 改善計画

### フェーズ 1: 失敗ピクセルの分類分析

**目的**: 現在の失敗（FN/FP）がどのレイヤー由来かを特定する。

#### 実装内容

```
analyze_failure_types.py を新規作成（各アバターの analyze_* 統合版）
```

分析する失敗カテゴリ:

| カテゴリ | 定義 | 由来レイヤー |
|---|---|---|
| FN-AA | マスク内・変換漏れ・S ∈ [0.05, 0.15] | AA境界の低彩度合成 |
| FN-Shadow | マスク内・変換漏れ・S ∈ [0.15, 0.45] | AO/Shadow/multiply |
| FP-AOShadow | マスク外・誤変換・同色相・低彩度 | AO/Shadow レイヤーの周縁 |
| FP-Outline | マスク外・誤変換・背景色 | outline レイヤーの滲み |

```python
# analyze_failure_types.py のスケッチ
for layer_name in ["AO", "Shadow1", "Shadow2", "Back", "outline"]:
    layer_pixels = load_layer_mask(layer_name)   # psd-tools で取得
    fp_in_layer  = fp_mask & layer_pixels        # どのFPがどのレイヤーから来るか
    print(f"FP由来 {layer_name}: {fp_in_layer.sum():,} px")
```

**成果物**: 各失敗の原因レイヤー別構成比（円グラフ/棒グラフ）  
→ これにより「**修正すべき問題の優先順位**」が明確になる。

---

### フェーズ 2: エッジ距離ベースの定量評価

**目的**: AA境界での回収状況をピクセル単位で評価する。

#### 考え方

```
PSDマスクのα輪郭 = 正確なエッジ座標
```

マスクのαを距離変換（distance transform）すると各ピクセルの「エッジからの距離（px）」が得られる。
距離別に FN 数を集計することで、「**RecoverBoundaryEdges が何px先まで回収できているか**」を検証できる。

```python
from scipy.ndimage import distance_transform_edt

edge_distance = distance_transform_edt(hair_mask_alpha > 128)
# FN ピクセルの edge_distance 分布
fn_distances = edge_distance[fn_mask]
print("距離 0-1px:", (fn_distances < 1).sum())
print("距離 1-2px:", ((fn_distances >= 1) & (fn_distances < 2)).sum())
print("距離 2-3px:", ((fn_distances >= 2) & (fn_distances < 3)).sum())
```

**成果物**: テスト追加

```
AA回収率_エッジ1px以内 : > 95%
AA回収率_エッジ2px以内 : > 85%
AA回収率_エッジ3px以内 : > 70%
```

→ これは `RecoverBoundaryEdges` のパス数（`antiAliasCleanup`）を適切に設定する根拠になる。

---

### フェーズ 3: レイヤー別彩度分布でパラメータを最適化

**目的**: `satMin` 等のパラメータをPSDの実データから自動的に最適化する。

#### 現状の問題

`satMin = sS * 0.50` は `analyze_tradeoff.py` の人手調整値だが、  
アバターやパーツによって最適値が異なる可能性がある。

#### 実装方針

```python
# optimize_satmin.py
# PSDマスク内・外それぞれの彩度分布を使い、
# FN (inside miss) と FP (outside bleed) が最小になる satMin を探索する

from scipy.optimize import minimize_scalar

def cost(mult):
    sat_min = sS * mult
    fn_rate = (s_inside < sat_min).mean()     # 見逃し率（小さいほど良い）
    fp_rate = (s_outside >= sat_min).mean()   # bleed率（小さいほど良い）
    return fn_rate + fp_rate * 2.0            # FPを2倍重視

result = minimize_scalar(cost, bounds=(0.05, 0.80), method='bounded')
print(f"最適 satMin 倍率: {result.x:.3f}")
```

また、`RecoverBoundaryEdges` の緩和threshold（現在固定 `satMin=0.02/satRamp=0.08`）も  
PSDの「AA境界のみのピクセル」彩度分布から適切な値を導出できる。

**成果物**:
- アバター別の最適パラメータテーブル
- `satMin` がパーツ別に異なる場合のゾーン単位設定UI（ColorZoneに追加）

---

### フェーズ 4: より多様な教師データの収集

**目的**: 汎化性能を高める。現在 2 アバター、2 パーツしかない。

#### 追加すべきケース

| 追加ケース | 理由 |
|---|---|
| 同アバター・別パーツ（衣装・靴） | 同じ色でも形状・レイヤー構成が異なる |
| 別アバター・同系色（青系2体目） | アバター固有のPSD構成への汎化 |
| 低彩度サンプル（灰・白・黒） | `satMin` が `0.02` 固定に落ちるケース |
| 混色パーツ（グラデーション） | tolerance が広くないと取りこぼすケース |

#### 手順

1. `.psd` から `layers.json` を `analyze_psd.py` で生成
2. `gen_ground_truth.py` を各アバターに対して実行
3. `ground_truth/fixtures.json` を `Tests/` に対応させてテスト追加

---

### フェーズ 5: 長期目標 — レイヤー情報をUIで活用

**目的**: PSDを直接読み込み、レイヤーマスクを Exclusion Mask に自動設定する。

これは「PSDなしで動く」というコアコンセプトから外れるが、  
PSDがある場合は圧倒的に精度を上げられる。

#### 構想

```
[PSD 読込] ボタン追加（Unity Editor）
    ↓
psd-tools (Python) または C# PSD ライブラリで解析
    ↓
対象レイヤーをユーザーが選択
    ↓
そのレイヤーのαマスクを Exclusion Mask に設定
    ↓
完璧なマスクによって FP = 0 保証
```

**注意**: これは Unity Editor 拡張として実装する必要があり、工数が大きい。  
フェーズ 1〜4 の成果でアルゴリズム自体を改善してから検討する。

---

## 4. 優先順位

```
高優先度 ──────────────────────────────────────────────────────────────
  フェーズ 1: 失敗ピクセルの分類分析         (1〜2日)
  フェーズ 2: エッジ距離ベース評価           (1日)
中優先度 ──────────────────────────────────────────────────────────────
  フェーズ 3: パラメータ最適化               (2〜3日)
  フェーズ 4: 教師データ追加                 (継続)
低優先度 ──────────────────────────────────────────────────────────────
  フェーズ 5: PSD直接読込UI                  (大工数、後回し)
```

---

## 5. 測定指標の現在と目標

| 指標 | 現状 | フェーズ1後 | フェーズ3後 |
|---|---|---|---|
| FN の原因レイヤー分類 | 未実装 | 分類済 | — |
| 失敗の 8 割を占める原因 | 不明 | **明確** | 対策済 |
| エッジ1px FN率 | 未計測 | 計測開始 | < 5% 目標 |
| 最適 satMin 倍率 | 経験則 0.50 | 変化なし | **PSD実測値** |
| SilverBleed | < 200 合格 | — | < 50 目標 |
| Visible FN (Costume) | 700〜800 | — | < 400 目標 |

---

## 付記: 現在の ground truth 生成の正確さについて

`gen_ground_truth.py` が生成する「正解画像」は  
**PSD の対象レイヤーを HSV変換した単純合成**であり、  
AO/Shadow/グラデーション等のレイヤーは「変換先の色でレイヤーを再合成」していない。

つまり正確なゴールは「PSD上でターゲット色に差し替えた後のフラット合成」だが、  
現在の gt は「マスク内ピクセルだけを HSV変換したもの + 元画像のマスク外ピクセル」になっている。

この誤差を認識した上で、測定指標を設計することが重要。  
→ AVATAR_A の VividCoverage が 98.8% に見えても、実際のゴールとのずれが含まれている可能性がある。

理想的な正解画像を生成するには:  
「**PSD内の対象レイヤーの色だけを差し替えて再フラット合成**」が必要であり、  
これをフェーズ 1 の分析スクリプトと合わせて実装することを推奨する。
