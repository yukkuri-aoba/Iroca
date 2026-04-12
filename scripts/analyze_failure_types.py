"""
analyze_failure_types.py — フェーズ1: 失敗ピクセルの分類分析
=============================================================
PSD レイヤー情報をもとに、アルゴリズムの FN / FP を由来レイヤー別に分類する。

カテゴリ:
  FN-AA       : マスク内・変換漏れ・低彩度 (S < satMin)    — AA 境界の低彩度合成由来
  FN-Shadow   : マスク内・変換漏れ・中彩度 (satMin ≤ S < 0.45)  — Shadow/AO/Multiply 由来
  FN-Other    : マスク内・変換漏れ・それ以外
  FP-AOShadow : マスク外・誤変換・同色相              — AO/Shadow レイヤーの周縁由来
  FP-Outline  : マスク外・誤変換・アウトライン近傍     — outline レイヤーの滲み由来
  FP-Other    : マスク外・誤変換・その他

入力:
  - PSD ファイル (レイヤー情報)
  - フラット PNG テクスチャ (アルゴリズム入力)
  - 教師マスク (ground truth)

出力:
  各カテゴリの FN/FP ピクセル数と割合
  レイヤー別 FP 構成比
"""
import json
import os
import sys

import numpy as np
from PIL import Image
from psd_tools import PSDImage

# gen_ground_truth.py の共通関数を再利用
sys.path.insert(0, os.path.dirname(__file__))
from gen_ground_truth import (
    layer_to_canvas, find_layer, find_layers,
    rgb_to_hsv_array, BASE_DIR
)

# ── 設定 ───────────────────────────────────────────────────────────────────

CHANGE_THRESHOLD = 20  # RGB 差がこれを超えたら「変化した」と判定

# ── C# アルゴリズムのマッチング再現 ────────────────────────────────────────

def simulate_algorithm_matching(tex_rgb, tex_alpha, sample_hsv, tolerance=0.20):
    """
    C# GetColorMatchStrength の HSV 距離判定を再現し、
    各ピクセルのマッチ状態を返す (True=マッチ)。
    ※ FillSmallHoles/RecoverBoundaryEdges/ConstrainBlur は含まない単純判定。
    """
    hsv = rgb_to_hsv_array(tex_rgb)
    pH, pS, pV = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    sH, sS, sV = sample_hsv

    # 動的彩度閾値
    sat_min = max(0.02, sS * 0.50)
    sat_ramp = max(0.08, sS * 0.10)
    sat_confidence = np.clip((pS - sat_min) / sat_ramp, 0, 1)

    # Hue 距離
    h_dist = np.abs(pH - sH)
    h_dist = np.where(h_dist > 0.5, 1.0 - h_dist, h_dist)

    # 彩度距離
    s_dist = np.abs(pS - sS)
    dist = h_dist + s_dist * 0.15

    opaque = tex_alpha > 30
    matched = (dist < tolerance) & (sat_confidence > 0) & opaque
    return matched, hsv

# ── AVATAR_A Hair 分析 ────────────────────────────────────────────────────────

def analyze_avatar_a_hair():
    print("=" * 70)
    print("AVATAR_A Hair: 失敗ピクセル分類分析")
    print("=" * 70)

    psd_path = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "PSD", "AVATAR_A_Hair.psd")
    tex_path = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "Texture", "AVATAR_A_Hair.png")
    gt_dir = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "ground_truth")
    fixtures_path = os.path.join(gt_dir, "fixtures.json")

    if not os.path.exists(fixtures_path):
        print("  [SKIP] fixtures.json が見つかりません。先に gen_ground_truth.py を実行してください。")
        return

    with open(fixtures_path, "r") as f:
        fixtures = json.load(f)

    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]
    tex_alpha = tex_img[:, :, 3]

    # 教師マスク
    hair_mask_img = np.array(Image.open(os.path.join(gt_dir, "hair_original.png")).convert("RGBA"))
    gt_mask = hair_mask_img[:, :, 3] > 30  # Blue レイヤーの α

    sample_hsv = (
        fixtures["sample_color_hsv"]["h"],
        fixtures["sample_color_hsv"]["s"],
        fixtures["sample_color_hsv"]["v"],
    )
    sS = sample_hsv[1]
    sat_min = max(0.02, sS * 0.50)

    # アルゴリズムのマッチング結果 (単純判定)
    algo_matched, hsv_map = simulate_algorithm_matching(
        tex_rgb, tex_alpha, sample_hsv, tolerance=0.20
    )

    # PSD レイヤーを読み込み
    layers_of_interest = {}
    for name in ["AO", "Shadow1", "Shadow2", "outline", "gradation_Shadow"]:
        layer = find_layer(psd, name, recursive=True)
        if layer is not None:
            arr = layer_to_canvas(layer, cw, ch)
            layers_of_interest[name] = arr[:, :, 3] > 30  # α > 30

    opaque = tex_alpha > 30
    pS = hsv_map[:, :, 1]

    # ── FN 分類 (マスク内 & アルゴリズム未マッチ) ──
    fn_mask = gt_mask & ~algo_matched & opaque

    fn_aa = fn_mask & (pS < sat_min)                    # S < satMin (AA 境界)
    fn_shadow = fn_mask & (pS >= sat_min) & (pS < 0.45)  # 中彩度 (Shadow)
    fn_other = fn_mask & (pS >= 0.45)                    # 高彩度 (なぜ漏れた?)

    fn_total = int(fn_mask.sum())
    print(f"\n  FN 合計 (マスク内・未マッチ): {fn_total:,}")
    print(f"    FN-AA      (S < {sat_min:.3f}): {int(fn_aa.sum()):,}  "
          f"({int(fn_aa.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Shadow  ({sat_min:.3f} ≤ S < 0.45): {int(fn_shadow.sum()):,}  "
          f"({int(fn_shadow.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Other   (S ≥ 0.45): {int(fn_other.sum()):,}  "
          f"({int(fn_other.sum()) / max(1, fn_total) * 100:.1f}%)")

    # ── FP 分類 (マスク外 & アルゴリズムがマッチ) ──
    fp_mask = ~gt_mask & algo_matched & opaque

    fp_total = int(fp_mask.sum())
    print(f"\n  FP 合計 (マスク外・誤マッチ): {fp_total:,}")

    # レイヤー別 FP 構成
    fp_accounted = np.zeros_like(fp_mask)
    for name, layer_mask in layers_of_interest.items():
        fp_in_layer = fp_mask & layer_mask
        count = int(fp_in_layer.sum())
        fp_accounted |= fp_in_layer
        if count > 0:
            print(f"    FP-{name}: {count:,}  "
                  f"({count / max(1, fp_total) * 100:.1f}%)")

    fp_unaccounted = fp_mask & ~fp_accounted
    if int(fp_unaccounted.sum()) > 0:
        print(f"    FP-Other: {int(fp_unaccounted.sum()):,}  "
              f"({int(fp_unaccounted.sum()) / max(1, fp_total) * 100:.1f}%)")

    # サマリ
    gt_total = int(gt_mask.sum())
    fn_rate = fn_total / max(1, gt_total)
    fp_rate = fp_total / max(1, int((~gt_mask & opaque).sum()))
    print(f"\n  サマリ:")
    print(f"    教師マスク内ピクセル: {gt_total:,}")
    print(f"    FN 率 (見逃し): {fn_rate:.2%}")
    print(f"    FP 率 (誤検出): {fp_rate:.2%}")
    print(f"    主な FN 原因: {'AA境界の低彩度' if int(fn_aa.sum()) > int(fn_shadow.sum()) else 'Shadow/AO層'}")
    print()

# ── AVATAR_A Costume 分析 ─────────────────────────────────────────────────────

def analyze_avatar_a_costume():
    print("=" * 70)
    print("AVATAR_A Costume: 失敗ピクセル分類分析")
    print("=" * 70)

    psd_path = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "PSD", "AVATAR_A_Costume.psd")
    tex_path = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "Texture", "AVATAR_A_Costume.png")
    gt_dir = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "ground_truth")
    fixtures_path = os.path.join(gt_dir, "costume_fixtures.json")

    if not os.path.exists(fixtures_path):
        print("  [SKIP] costume_fixtures.json が見つかりません。先に gen_ground_truth.py を実行してください。")
        return

    with open(fixtures_path, "r") as f:
        fixtures = json.load(f)

    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]
    tex_alpha = tex_img[:, :, 3]

    # 教師マスク
    mask_img = np.array(Image.open(os.path.join(gt_dir, "costume_blue_mask.png")).convert("RGBA"))
    gt_mask = mask_img[:, :, 0] > 128  # R チャンネル (CostumeTextureTests.cs と合わせる)

    sample_hsv = (
        fixtures["sample_color_hsv"]["h"],
        fixtures["sample_color_hsv"]["s"],
        fixtures["sample_color_hsv"]["v"],
    )
    sS = sample_hsv[1]
    sat_min = max(0.02, sS * 0.50)

    algo_matched, hsv_map = simulate_algorithm_matching(
        tex_rgb, tex_alpha, sample_hsv, tolerance=0.20
    )

    opaque = tex_alpha > 30
    pS = hsv_map[:, :, 1]

    fn_mask = gt_mask & ~algo_matched & opaque
    fn_aa = fn_mask & (pS < sat_min)
    fn_shadow = fn_mask & (pS >= sat_min) & (pS < 0.45)
    fn_other = fn_mask & (pS >= 0.45)

    fn_total = int(fn_mask.sum())
    print(f"\n  FN 合計: {fn_total:,}")
    print(f"    FN-AA      (S < {sat_min:.3f}): {int(fn_aa.sum()):,}  "
          f"({int(fn_aa.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Shadow  ({sat_min:.3f} ≤ S < 0.45): {int(fn_shadow.sum()):,}  "
          f"({int(fn_shadow.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Other   (S ≥ 0.45): {int(fn_other.sum()):,}  "
          f"({int(fn_other.sum()) / max(1, fn_total) * 100:.1f}%)")

    # FP
    fp_mask = ~gt_mask & algo_matched & opaque
    fp_total = int(fp_mask.sum())
    print(f"\n  FP 合計: {fp_total:,}")

    gt_total = int(gt_mask.sum())
    fn_rate = fn_total / max(1, gt_total)
    fp_rate = fp_total / max(1, int((~gt_mask & opaque).sum()))
    print(f"\n  サマリ:")
    print(f"    教師マスク内ピクセル: {gt_total:,}")
    print(f"    FN 率: {fn_rate:.2%}")
    print(f"    FP 率: {fp_rate:.2%}")
    print()

# ── AvatarB Bandana 分析 ──────────────────────────────────────────────────────

def analyze_avatar_b_bandana():
    print("=" * 70)
    print("AvatarB Bandana: 失敗ピクセル分類分析")
    print("=" * 70)

    psd_path = os.path.join(BASE_DIR, "texture_sample", "AvatarB", "AvatarB_PSD_CLIP", "Clothes.psd")
    tex_path = os.path.join(BASE_DIR, "texture_sample", "AvatarB", "PNG", "Clothes.png")
    gt_dir = os.path.join(BASE_DIR, "texture_sample", "ground_truth")
    fixtures_path = os.path.join(gt_dir, "fixtures.json")

    if not os.path.exists(fixtures_path):
        print("  [SKIP] fixtures.json が見つかりません。先に gen_ground_truth.py を実行してください。")
        return

    with open(fixtures_path, "r") as f:
        fixtures = json.load(f)

    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]
    tex_alpha = tex_img[:, :, 3]

    # 教師マスク
    mask_img = np.array(Image.open(os.path.join(gt_dir, "bandana_original.png")).convert("RGBA"))
    gt_mask = mask_img[:, :, 3] > 30

    sample_hsv = (
        fixtures["sample_color_hsv"]["h"],
        fixtures["sample_color_hsv"]["s"],
        fixtures["sample_color_hsv"]["v"],
    )
    sS = sample_hsv[1]
    sat_min = max(0.02, sS * 0.50)

    algo_matched, hsv_map = simulate_algorithm_matching(
        tex_rgb, tex_alpha, sample_hsv, tolerance=0.15
    )

    # PSD レイヤーでの FP 分類
    layers_of_interest = {}
    for name in ["Shadow"]:
        # Bandana グループ内の Shadow
        bandana = find_layer(psd, "Bandana", recursive=True)
        if bandana and hasattr(bandana, "__iter__"):
            layer = find_layer(bandana, name)
            if layer is not None:
                arr = layer_to_canvas(layer, cw, ch)
                layers_of_interest[f"Bandana/{name}"] = arr[:, :, 3] > 30

    # Boots, Tops 等のグループ内 Shadow
    for group_name in ["Boots", "Tops", "Hair Ribbon", "Goggles"]:
        group = find_layer(psd, group_name, recursive=True)
        if group and hasattr(group, "__iter__"):
            for sub in group:
                if "Shadow" in sub.name or "Seam" in sub.name:
                    arr = layer_to_canvas(sub, cw, ch)
                    layers_of_interest[f"{group_name}/{sub.name}"] = arr[:, :, 3] > 30

    opaque = tex_alpha > 30
    pS = hsv_map[:, :, 1]

    fn_mask = gt_mask & ~algo_matched & opaque
    fn_aa = fn_mask & (pS < sat_min)
    fn_shadow = fn_mask & (pS >= sat_min) & (pS < 0.45)
    fn_other = fn_mask & (pS >= 0.45)

    fn_total = int(fn_mask.sum())
    print(f"\n  FN 合計: {fn_total:,}")
    print(f"    FN-AA      (S < {sat_min:.3f}): {int(fn_aa.sum()):,}  "
          f"({int(fn_aa.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Shadow  ({sat_min:.3f} ≤ S < 0.45): {int(fn_shadow.sum()):,}  "
          f"({int(fn_shadow.sum()) / max(1, fn_total) * 100:.1f}%)")
    print(f"    FN-Other   (S ≥ 0.45): {int(fn_other.sum()):,}  "
          f"({int(fn_other.sum()) / max(1, fn_total) * 100:.1f}%)")

    fp_mask = ~gt_mask & algo_matched & opaque
    fp_total = int(fp_mask.sum())
    print(f"\n  FP 合計: {fp_total:,}")

    fp_accounted = np.zeros_like(fp_mask)
    for name, layer_mask in layers_of_interest.items():
        fp_in_layer = fp_mask & layer_mask
        count = int(fp_in_layer.sum())
        fp_accounted |= fp_in_layer
        if count > 0:
            print(f"    FP-{name}: {count:,}  "
                  f"({count / max(1, fp_total) * 100:.1f}%)")

    fp_unaccounted = fp_mask & ~fp_accounted
    if int(fp_unaccounted.sum()) > 0:
        print(f"    FP-Other: {int(fp_unaccounted.sum()):,}  "
              f"({int(fp_unaccounted.sum()) / max(1, fp_total) * 100:.1f}%)")

    gt_total = int(gt_mask.sum())
    fn_rate = fn_total / max(1, gt_total)
    fp_rate = fp_total / max(1, int((~gt_mask & opaque).sum()))
    print(f"\n  サマリ:")
    print(f"    教師マスク内ピクセル: {gt_total:,}")
    print(f"    FN 率: {fn_rate:.2%}")
    print(f"    FP 率: {fp_rate:.2%}")
    print()

# ── メイン ──────────────────────────────────────────────────────────────────

def main():
    print("失敗ピクセル分類分析 (フェーズ 1)\n")
    analyze_avatar_a_hair()
    analyze_avatar_a_costume()
    analyze_avatar_b_bandana()
    print("=== 分析完了 ===")

if __name__ == "__main__":
    main()
