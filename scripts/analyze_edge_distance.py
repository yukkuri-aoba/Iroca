"""
analyze_edge_distance.py — フェーズ2: エッジ距離ベースの定量評価
================================================================
PSD マスクのα輪郭を距離変換 (distance transform) し、
FN (見逃し) ピクセルがエッジからどの距離にあるかを集計する。

これにより RecoverBoundaryEdges の回収能力を数値化し、
antiAliasCleanup パラメータの適正値を導出する根拠を得る。

出力指標:
  AA回収率_エッジ1px以内 : > 95% 目標
  AA回収率_エッジ2px以内 : > 85% 目標
  AA回収率_エッジ3px以内 : > 70% 目標
"""
import json
import os
import sys

import numpy as np
from PIL import Image
from scipy.ndimage import distance_transform_edt

sys.path.insert(0, os.path.dirname(__file__))
from gen_ground_truth import rgb_to_hsv_array, BASE_DIR
from analyze_failure_types import simulate_algorithm_matching

# ── 分析関数 ────────────────────────────────────────────────────────────────

def analyze_edge_distance(name, tex_path, mask_path, sample_hsv,
                          tolerance, mask_mode="alpha"):
    """
    マスクのエッジからの距離ごとに FN を集計する。

    mask_mode: "alpha" → α > 30 でマスク判定
               "red"   → R > 128 でマスク判定
    """
    print(f"\n{'=' * 70}")
    print(f"{name}: エッジ距離ベース評価")
    print(f"{'=' * 70}")

    if not os.path.exists(tex_path) or not os.path.exists(mask_path):
        print("  [SKIP] ファイルが見つかりません")
        return

    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]
    tex_alpha = tex_img[:, :, 3]

    mask_img = np.array(Image.open(mask_path).convert("RGBA"))
    if mask_mode == "alpha":
        gt_mask = mask_img[:, :, 3] > 30
    else:
        gt_mask = mask_img[:, :, 0] > 128

    # マスクのエッジからの距離を計算
    # distance_transform_edt: マスク内ピクセルのエッジからの距離
    # 境界 = マスクの端 → 内部方向に距離が増加
    edge_distance_inside = distance_transform_edt(gt_mask)
    # マスク外ピクセルのエッジからの距離
    edge_distance_outside = distance_transform_edt(~gt_mask)

    # アルゴリズムのマッチング
    algo_matched, hsv_map = simulate_algorithm_matching(
        tex_rgb, tex_alpha, sample_hsv, tolerance=tolerance
    )

    opaque = tex_alpha > 30
    fn_mask = gt_mask & ~algo_matched & opaque
    fp_mask = ~gt_mask & algo_matched & opaque

    # ── FN のエッジ距離分布 ──
    fn_distances = edge_distance_inside[fn_mask]
    gt_distances = edge_distance_inside[gt_mask & opaque]

    print(f"\n  マスク内ピクセル (opaque): {int((gt_mask & opaque).sum()):,}")
    print(f"  FN 合計: {int(fn_mask.sum()):,}")

    print(f"\n  --- FN のエッジ距離分布 ---")
    print(f"  (エッジ = マスクの境界、距離はマスク内方向)")

    distance_bins = [
        (0, 1, "0-1px  (境界上)"),
        (1, 2, "1-2px  (境界近傍)"),
        (2, 3, "2-3px  (近傍)"),
        (3, 5, "3-5px"),
        (5, 10, "5-10px"),
        (10, float("inf"), "10px+  (内部)")
    ]

    for lo, hi, label in distance_bins:
        if hi == float("inf"):
            count = int(np.sum(fn_distances >= lo))
            total_in_band = int(np.sum(gt_distances >= lo))
        else:
            count = int(np.sum((fn_distances >= lo) & (fn_distances < hi)))
            total_in_band = int(np.sum((gt_distances >= lo) & (gt_distances < hi)))

        miss_rate = count / max(1, total_in_band)
        recovery_rate = 1 - miss_rate
        print(f"    {label}: FN={count:>7,} / 全{total_in_band:>8,}  "
              f"回収率={recovery_rate:.1%}")

    # ── AA 回収率指標 (テストで使用する値) ──
    print(f"\n  --- AA 回収率指標 (RecoverBoundaryEdges 評価) ---")
    targets = [
        (1, 0.95, "エッジ 1px以内"),
        (2, 0.85, "エッジ 2px以内"),
        (3, 0.70, "エッジ 3px以内"),
    ]
    for max_dist, target_rate, label in targets:
        fn_in_range = int(np.sum(fn_distances < max_dist))
        total_in_range = int(np.sum(gt_distances < max_dist))
        recovery = 1 - fn_in_range / max(1, total_in_range)
        status = "OK" if recovery >= target_rate else "NG"
        print(f"    {label}: 回収率={recovery:.1%}  目標={target_rate:.0%}  [{status}]")

    # ── FP のエッジ距離分布 ──
    fp_distances = edge_distance_outside[fp_mask]
    print(f"\n  --- FP のエッジ距離分布 ---")
    print(f"  (エッジからマスク外方向の距離)")
    print(f"  FP 合計: {int(fp_mask.sum()):,}")

    for lo, hi, label in distance_bins:
        if hi == float("inf"):
            count = int(np.sum(fp_distances >= lo))
        else:
            count = int(np.sum((fp_distances >= lo) & (fp_distances < hi)))
        print(f"    {label}: FP={count:>7,}")

    # ── 彩度分布 (satMin 最適化の参考) ──
    print(f"\n  --- エッジ境界 (0-2px) の彩度分布 ---")
    edge_band = gt_mask & opaque & (edge_distance_inside < 2)
    edge_sat = hsv_map[:, :, 1][edge_band]
    if len(edge_sat) > 0:
        for threshold in [0.02, 0.05, 0.10, 0.15, 0.20, 0.30, 0.40, 0.50]:
            below = np.sum(edge_sat < threshold)
            print(f"    S < {threshold:.2f}: {int(below):,} / {len(edge_sat):,}  "
                  f"({below / len(edge_sat):.1%})")

    print()

# ── メイン ──────────────────────────────────────────────────────────────────

def main():
    print("エッジ距離ベース評価 (フェーズ 2)\n")

    avatar_a_gt = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "ground_truth")
    avatar_b_gt = os.path.join(BASE_DIR, "texture_sample", "ground_truth")

    # AVATAR_A Hair fixtures
    hair_fix_path = os.path.join(avatar_a_gt, "fixtures.json")
    if os.path.exists(hair_fix_path):
        with open(hair_fix_path) as f:
            fix = json.load(f)
        sample = (fix["sample_color_hsv"]["h"],
                  fix["sample_color_hsv"]["s"],
                  fix["sample_color_hsv"]["v"])
        analyze_edge_distance(
            "AVATAR_A Hair",
            os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "Texture", "AVATAR_A_Hair.png"),
            os.path.join(avatar_a_gt, "hair_original.png"),
            sample, tolerance=0.20, mask_mode="alpha"
        )

    # AVATAR_A Costume fixtures
    costume_fix_path = os.path.join(avatar_a_gt, "costume_fixtures.json")
    if os.path.exists(costume_fix_path):
        with open(costume_fix_path) as f:
            fix = json.load(f)
        sample = (fix["sample_color_hsv"]["h"],
                  fix["sample_color_hsv"]["s"],
                  fix["sample_color_hsv"]["v"])
        analyze_edge_distance(
            "AVATAR_A Costume",
            os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "Texture", "AVATAR_A_Costume.png"),
            os.path.join(avatar_a_gt, "costume_blue_mask.png"),
            sample, tolerance=0.20, mask_mode="red"
        )

    # AvatarB Bandana fixtures
    avatar_b_fix_path = os.path.join(avatar_b_gt, "fixtures.json")
    if os.path.exists(avatar_b_fix_path):
        with open(avatar_b_fix_path) as f:
            fix = json.load(f)
        sample = (fix["sample_color_hsv"]["h"],
                  fix["sample_color_hsv"]["s"],
                  fix["sample_color_hsv"]["v"])
        analyze_edge_distance(
            "AvatarB Bandana",
            os.path.join(BASE_DIR, "texture_sample", "AvatarB", "PNG", "Clothes.png"),
            os.path.join(avatar_b_gt, "bandana_original.png"),
            sample, tolerance=0.15, mask_mode="alpha"
        )

    print("=== 分析完了 ===")

if __name__ == "__main__":
    main()
