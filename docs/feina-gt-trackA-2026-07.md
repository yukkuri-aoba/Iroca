# Track A 実施記録 — Feina 服の回帰編入（GT拡充）

`docs/accuracy-roadmap-2026-07.md` Track A の実施記録。2026-07-04。

## 1. 生成した GT と合成規則（PSD 構造＋leaf 実色で最終確定）

per-leaf マスク30枚（`dev_safe/texture_sample/ground_truth/feina_clothes/mask_*.png`）を
`dev_safe/scripts/build_feina_gt.py` で OR 合成 →（明色 overlay を減算）→ 可視域 AND。
根拠は PSD レイヤー構造（`Clothes.psd`）と各 leaf 下のテクスチャ median RGB/HSV。

| 被写体 | 合成 | median RGB / HSV | GT px |
|---|---|---|---|
| `feina-pants` | Pants ∪ Seam ∪ Shadow | (84,81,75) s0.11 v0.33 | 820,823 (4.9%) |
| `feina-boots` | Boots_1∪Boots_2∪Shadow∪Sole∪Sole_Pattern∪Shoelace∪Belt − (Metal∪Triangle∪Stitch) | (66,50,46) s0.30 v0.26 | 1,862,751 (11.1%) |
| `feina-tops` | Tops_1∪Tops2∪Shadow∪Seam − (Long_Sleeve∪Shadow_LongSleeve∪Ethnic_Pattern∪Ribbon) | (247,236,223) s0.10 v0.97 | 3,305,591 (19.7%) |

### ロードマップ提案からの変更（データで最終確定）
- **boots**: 提案は Belt/Shoelace/Sole を除外だったが、これらは色的に Boots_1/2 とほぼ双子
  （分離は色では不能＝マスク案件）で、PSD 上も単一 `Boots` グループの兄弟（upper/sole を分ける
  下位構造なし）。物理的にも「ブーツ全体」。→ dark-leather クラスタ全体を1材質とし、明色
  overlay（Metal 218 / Triangle 220 / Stitch）のみ除外。precision 試験は明色 Metal やマスク
  隣接の明色 Socks/Tops への滲みで成立。
- **tops**: 提案の `Shadow_LongSleeve` は実色 (80,76,71)=暗色（袖の影）で cream ではない → 除外。
  逆に除外指定の `Seam` (235,218,199) は cream 同族 → 追加。ロードマップ自身の「sample_rgb を
  正とする」指示に従った訂正。

GT の目視確認: `dev_safe/psd_analysis_out/Feina/*_overlay_1024.png`（GT を赤半透明で重畳）で
3被写体とも正しい材質域を被覆・別素材を除外できていることを確認済み。

## 2. 初回計測（実 C# Harness、2026-07-04）

### 2a. 固定 tolerance 0.20・マスク無し・flood-fill OFF（`test_feina_iou.py` の凍結値）
| 被写体 | IoU | precision | recall |
|---|---|---|---|
| feina-pants | 0.086 | 0.086 | 0.998 |
| feina-boots | 0.293 | 0.293 | 0.999 |
| feina-tops | 0.270 | 0.270 | 1.000 |

**recall ~1.0 だが precision 壊滅**。Feina Clothes.png は 20+ パーツが低彩度 warm/cream/brown を
共有するフルボディアトラスで、色のみ選択がアトラス全域へ溢れる（tops はアトラスの73%を選択）。

### 2b. 実路（自動調整 ZoneAutoTuner・no-mask）（`autotune_accuracy_baseline.json`）
| 被写体 | IoU | precision | recall | 導出 tol | 備考 |
|---|---|---|---|---|---|
| feina-pants | 0.198 | 0.198 | 0.999 | **0.312** | tolerance 過大導出 |
| feina-boots | 0.195 | 0.195 | 0.999 | 0.090 | hlRec=True で明色へ拡大 |
| feina-tops | **0.767** | 0.859 | 0.877 | 0.160 | 自動調整が機能・飽和域近い |

### 2c. tolerance sweep（固定 tol・過検出の性質切り分け）
| tol | pants prec/rec | boots prec/rec | tops prec/rec |
|---|---|---|---|
| 0.20 | 0.086/0.998 | 0.303/0.999 | 0.270/1.000 |
| 0.08 | 0.212/0.998 | 0.468/0.997 | **0.776/0.997** |
| 0.05 | 0.404/0.989 | 0.540/0.992 | 0.840/0.979 |
| 0.03 | 0.407/0.879 | **0.832/0.928** | 0.856/0.855 |

### 2d. flood-fill / seed 効果（固定 tol 0.20）
| 被写体 | FF off prec | FF auto prec | FF+GTシード prec |
|---|---|---|---|
| pants | 0.086 | 0.102 | 0.102（巨大連結成分で無効）|
| boots | 0.303 | 0.308 | 0.308（同上）|
| tops | 0.270 | 0.279 | 0.000（座標系未校正で非マッチ画素へ）|

## 3. 所見（＝開いた新失敗面）

1. **feina-tops は色分離可能**（auto-tune で IoU 0.767）。新失敗面ではなく飽和域に近い＝番兵化。
2. **feina-pants は真の色双子**（long-sleeve 88,84,79 / goggles-belt 66,66,66 と分離不能）。
   固定 tol 0.03 でも precision 天井 ~0.40。色距離では解けない＝**空間分離（Track C）の案件**。
3. **feina-boots は色改善余地大**（固定 tol 0.03 で prec 0.83）だが、auto-tune が
   tolerance 過大＋hlRec 過剰リーチで prec 0.195 に落とす。tolerance/hlRec の導出改善で
   実路が改善しうるが、色天井の先はやはり空間分離。
4. **flood-fill auto は無効**（低彩度パーツが広域マッチ＋AA橋で1巨大連結成分になる）。
   単一シードでも巨塊が残る（pants/boots FF+seed == FF auto）。tops のシードは prec/rec=0＝
   **座標系校正が未固定**（Track C Phase 0 が最初に固定すると明記した課題そのもの）。

→ **Feina アトラスの支配的失敗は「色双子＋空間ブリッジによる過検出」＝ Track C
（seed-aware tolerance 再導出＋座標系校正）の直接の対象**。feina-pants/boots は Track C の
理想的な検証・before/after 対照被写体になる。色距離側の Track A-7 単独改善は、
色天井（pants ~0.40）と既存4被写体とのトレードオフで頭打ちが見込まれる。

## 4. テスト編入（実施内容と方針）

| 編入先 | 内容 | 方針 |
|---|---|---|
| `fixtures.py` | `FEINA_{PANTS,BOOTS,TOPS}_SUBJECT` を `SUBJECT_REGISTRY` に登録（single_mask, R ch）| ロードマップ通り |
| `measure_autotune.py` | `SUBJECT_NAMES` に feina 3件追加＋ベースライン merge（既存4件保持）| 実路計測 |
| `test_feina_autotune.py`（新） | 実路 autotune の選択精度を degradation-only 床で守る（compute_all_metrics 不使用＝安価）。`VACC_FEINA_AUTOTUNE=1` で有効化 | 過検出番兵＋Track C の before/after |
| `test_feina_iou.py`（新） | 固定 tol の IoU/px 決定論凍結＋precision 床 | 退行検出 |
| `test_autotune_accuracy.py` | GATE 対象を明示リストへ固定し `mat.SUBJECT_NAMES` から切り離し（既存挙動不変）| feina を高コスト品質ゲートから除外 |

### ロードマップ A-5 からの調整（理由付き）
- **quality_gate / quality_report への編入は見送り**。`compute_all_metrics` は 4096² で
  **~114s/case**（連結成分解析）＝ feina 15ケースで常時ゲートに 30分超を足すのは非現実的。
  さらに固定 tol の品質指標（contrast 2.5 / noise 2.9 / out_of_mask 0.66）は**過検出の副産物**で
  独立した再着色バグではない。過検出は autotune 番兵＋固定 tol precision 床で意味ある形で捕捉済み。
- **test_false_positive への編入も見送り**、precision 床は `test_feina_iou.py` に集約
  （同じ固定 tol 経路で重い 4096² アトラスケースを二重実行しないため。false_positive は
  色分離可能な単一素材被写体に focus を維持）。

## 5. 受け入れ状況
- 新3被写体が回帰スイートに編入され、床・ベースラインが揃い全テスト green
  （`test_feina_iou.py` 全パス / `VACC_FEINA_AUTOTUNE=1` で `test_feina_autotune.py` 全パス /
  `test_autotune_accuracy.py` 既存不変）。
- 初回計測サマリ（本書 §2）を記録。
- **改善サイクルの本丸は Track C**（§3 の結論）。Track A は GT 資産＋失敗面の特定＋番兵化で完了。
