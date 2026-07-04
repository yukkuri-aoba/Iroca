# Track C PoC 記録 — seed-aware tolerance 再導出の到達点(feina アトラス)

`docs/accuracy-roadmap-2026-07.md` Track C の着手前 PoC。2026-07-04。製品コード変更なし・実 C# Harness 計測。
目的: Track C の核心仮説「シード指定後に tolerance を成分限定で再導出すれば過検出が直る」を、
Phase 1 実装の前に **測って** 検証・de-risk する(improvement-cycle の「作る前に測る」)。

## 実験と結果(feina-pants/boots/tops, red ターゲット)

### 実験1: 成分限定導出の**上界**(GT マスク = 完全な成分知識, `run_autotune_masked(excluded=~GT)`)
| 被写体 | nomask IoU | GT成分導出 IoU | tol | precision |
|---|---|---|---|---|
| feina-pants | 0.198 | **0.998** | 0.312→0.120 | 0.198→1.000 |
| feina-boots | 0.195 | **0.998** | 0.090→0.080 | 0.195→0.999 |
| feina-tops | 0.767 | **0.818** | 0.160→0.137 | 0.859→0.975 |

→ **成分を知っていれば過検出はほぼ完全に直る**。ただし `~GT` 除外マスクは導出だけでなく
**選択も成分内に限定**する(除外画素は処理されない)ので、これは「フルマスク解」の上界であり
シード解ではない。

### 実験2: realistic なシード機構(成分限定で導出した tight tol を**全画像選択**に使い FF+seed で成分分離)
座標校正: harness の raw は PIL top-down なので `seedUV=(x/(w-1), y_top/(h-1))`(**flip なし**)。
(Track A の probe が flip して tops シードを非マッチ画素へ落としていた誤りを訂正 = Phase 0 校正)
| 被写体 | tighttol/noFF | tighttol/FF+seed | 上界(実験1) |
|---|---|---|---|
| feina-pants | IoU 0.224 | IoU 0.224(**FF+seed==noFF**) | 0.998 |
| feina-boots | IoU 0.462 | IoU 0.462(**同上**) | 0.998 |
| feina-tops | IoU 0.768 | IoU 0.768(**同上**) | 0.818 |

→ **FF+seed が noFF と完全一致 = シードを打っても何も分離されない**。tight tol でも選択が
**1つの巨大ブリッジ連結成分**(pants と色双子の long-sleeve(88,84,79≈pants 82,78,73)等が AA/隣接で
連結)になり、シードはその巨塊に落ちて全体を残す。tight tol 単独でも色双子は tolerance 内なので
過検出が残る(pants 0.224)。

## 結論(Track C スコープの精緻化)

1. **成分限定導出の効果は絶大だが、それには「選択の成分限定」が必要**で、feina アトラスでは
   それはフルマスク(ロードマップの明示スコープ外)に等しい。
2. **realistic なシード機構(tight tol 再導出 + FF CC-keep seed)は feina アトラスでは上界に届かない**。
   真因は (a) 色双子が tight tol 内で選択される、(b) 選択が空間的にブリッジしてシードで分離不能。
   これはロードマップ自身が Track C スコープ外とした「**完全画素連結の同色相隣接 = 原理的に
   マスクのみが解**」の case に feina アトラスが該当することを示す。
3. **Track C のシード機構の本来の対象は gen2 の navy(UV ギャップ/アウトライン/透明で**画素非連結**)**。
   feina アトラス(パーツが密で選択がブリッジ)は非連結 navy とは別問題。
4. Track A の「feina 過検出 = Track C の領域」という引き継ぎは **半分正しく半分外れ**: Track C は
   *非連結* navy を直すが、feina アトラスの *ブリッジ* over-selection は直さない(マスク案件)。

## Phase 1 実装への含意(推奨)

- **Phase 1(seed-aware 再導出の製品実装)は、その本来の対象である gen2 navy(画素非連結)で
  受け入れ基準を満たすことを先に確認してから着手する**。feina アトラスで直ると仮定して実装すると、
  本 PoC の測定に反して「motivating case が直らない」結果になる。
- feina アトラス(pants/boots)の実運用解は、ロードマップが navy のユーザー解として挙げた
  **Shift+クリックシード + 除外マスク**(= 選択の成分限定 = 本 PoC 実験1の上界を実現する経路)であり、
  tolerance 再導出だけでは不足。
- feina-boots は tight 導出で hlRec 過剰リーチが消え IoU 0.195→0.462 の**部分改善**は得られる
  (明色パーツへの溢れの抑制)。ただし color-twin 由来の残差(0.462 止まり)は空間分離が必要。

## 計測スクリプト(dev_safe, ローカル)
- `dev_safe/scripts/measure_feina.py` — feina 3被写体の固定tol 計測
- PoC 本体は scratchpad の使い捨てスクリプト(製品でも dev_safe 追跡でもない)。再現は
  `run_autotune_masked(rgba, sample, target, ~gt)` と `run_harness(..., useFloodFill, seedUV)` で可能。
