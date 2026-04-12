"""
gen_ground_truth.py — PSD レイヤー情報から教師データ (ground truth) を生成する
==============================================================================
対象:
  - AVATAR_A Hair   (texture_sample/AVATAR_A/PSD/AVATAR_A_Hair.psd)
  - AVATAR_A Costume(texture_sample/AVATAR_A/PSD/AVATAR_A_Costume.psd)
  - AvatarB Clothes (texture_sample/AvatarB/AvatarB_PSD_CLIP/Clothes.psd)

出力:
  texture_sample/AVATAR_A/ground_truth/
    hair_original.png       — Blue レイヤーをキャンバスサイズで描画 (α=マスク)
    silver_mask.png         — 合成画像で S<0.10 かつ opaque な領域マスク
    fixtures.json           — テストケース定義
  texture_sample/AVATAR_A/ground_truth/
    costume_blue_mask.png   — 全 Blue_Base レイヤーの合成マスク
    costume_fixtures.json   — Costume テストケース定義
  texture_sample/ground_truth/
    bandana_original.png    — Bandana グループ合成 (α=マスク)
    bandana_mask.png        — α 可視化 (白黒マスク)
    fixtures.json           — テストケース定義
    gt_*.png                — 各ターゲット色で recolor した正解画像
"""
import colorsys
import json
import os
import sys

import numpy as np
from PIL import Image
from psd_tools import PSDImage

# ── パス ───────────────────────────────────────────────────────────────────

BASE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AVATAR_A_PSD_DIR = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "PSD")
AVATAR_A_TEX_DIR = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "Texture")
AVATAR_A_GT_DIR  = os.path.join(BASE_DIR, "texture_sample", "AVATAR_A", "ground_truth")
AVATAR_B_PSD_DIR  = os.path.join(BASE_DIR, "texture_sample", "AvatarB", "AvatarB_PSD_CLIP")
AVATAR_B_TEX_DIR  = os.path.join(BASE_DIR, "texture_sample", "AvatarB", "PNG")
AVATAR_B_GT_DIR   = os.path.join(BASE_DIR, "texture_sample", "ground_truth")

# ── 共通ユーティリティ ──────────────────────────────────────────────────────

def ensure_dir(path):
    os.makedirs(path, exist_ok=True)

def layer_to_canvas(layer, canvas_w, canvas_h):
    """レイヤーをキャンバスサイズの RGBA numpy 配列に変換する"""
    img = layer.topil()
    if img is None:
        return np.zeros((canvas_h, canvas_w, 4), dtype=np.uint8)
    img = img.convert("RGBA")
    canvas = np.zeros((canvas_h, canvas_w, 4), dtype=np.uint8)
    left, top = layer.left, layer.top
    right, bottom = min(layer.right, canvas_w), min(layer.bottom, canvas_h)
    # クリップ
    lw = right - left
    lh = bottom - top
    if lw <= 0 or lh <= 0:
        return canvas
    arr = np.array(img)
    # レイヤーが負のオフセットを持つ可能性
    src_y0 = max(0, -top)
    src_x0 = max(0, -left)
    dst_y0 = max(0, top)
    dst_x0 = max(0, left)
    h = min(arr.shape[0] - src_y0, canvas_h - dst_y0)
    w = min(arr.shape[1] - src_x0, canvas_w - dst_x0)
    if h > 0 and w > 0:
        canvas[dst_y0:dst_y0+h, dst_x0:dst_x0+w] = arr[src_y0:src_y0+h, src_x0:src_x0+w]
    return canvas

def find_layer(psd, name, recursive=True):
    """名前でレイヤーを再帰的に検索 (最初にマッチしたものを返す)"""
    for layer in psd:
        if layer.name == name:
            return layer
        if recursive and hasattr(layer, "__iter__"):
            result = find_layer(layer, name, recursive=True)
            if result is not None:
                return result
    return None

def find_layers(psd, name, recursive=True):
    """名前でレイヤーを再帰的に検索 (全マッチを返す)"""
    results = []
    for layer in psd:
        if layer.name == name:
            results.append(layer)
        if recursive and hasattr(layer, "__iter__"):
            results.extend(find_layers(layer, name, recursive=True))
    return results

def composite_group(group, canvas_w, canvas_h):
    """グループの可視レイヤーを単純合成 (NORMAL blend のみ)"""
    result = np.zeros((canvas_h, canvas_w, 4), dtype=np.uint8)
    for layer in group:
        if not layer.visible:
            continue
        arr = layer_to_canvas(layer, canvas_w, canvas_h)
        alpha = arr[:, :, 3:4].astype(np.float32) / 255.0
        result[:, :, :3] = (result[:, :, :3].astype(np.float32) * (1 - alpha) +
                             arr[:, :, :3].astype(np.float32) * alpha).astype(np.uint8)
        result[:, :, 3] = np.clip(
            result[:, :, 3].astype(np.float32) + arr[:, :, 3].astype(np.float32) * (1 - result[:, :, 3].astype(np.float32) / 255.0),
            0, 255).astype(np.uint8)
    return result

def rgb_to_hsv_array(rgb):
    """RGB (0-255 uint8, HxWx3) → HSV (0-1 float, HxWx3)"""
    f = rgb.astype(np.float32) / 255.0
    r, g, b = f[:, :, 0], f[:, :, 1], f[:, :, 2]
    mx = np.maximum(r, np.maximum(g, b))
    mn = np.minimum(r, np.minimum(g, b))
    delta = mx - mn
    v = mx
    s = np.where(mx > 1e-5, delta / mx, 0.0)

    h = np.zeros_like(r)
    mask_r = (mx == r) & (delta > 1e-5)
    mask_g = (mx == g) & (delta > 1e-5) & ~mask_r
    mask_b = (mx == b) & (delta > 1e-5) & ~mask_r & ~mask_g
    h[mask_r] = ((g[mask_r] - b[mask_r]) / delta[mask_r]) / 6.0
    h[mask_g] = (2.0 + (b[mask_g] - r[mask_g]) / delta[mask_g]) / 6.0
    h[mask_b] = (4.0 + (r[mask_b] - g[mask_b]) / delta[mask_b]) / 6.0
    h[h < 0] += 1.0

    return np.stack([h, s, v], axis=-1)

def hsv_to_rgb_array(hsv):
    """HSV (0-1 float, HxWx3) → RGB (0-255 uint8, HxWx3)"""
    h, s, v = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    h6 = h * 6.0
    i = np.floor(h6).astype(int)
    f = h6 - i
    p = v * (1 - s)
    q = v * (1 - s * f)
    t = v * (1 - s * (1 - f))

    i_mod = i % 6
    r = np.where(i_mod == 0, v, np.where(i_mod == 1, q, np.where(i_mod == 2, p,
        np.where(i_mod == 3, p, np.where(i_mod == 4, t, v)))))
    g = np.where(i_mod == 0, t, np.where(i_mod == 1, v, np.where(i_mod == 2, v,
        np.where(i_mod == 3, q, np.where(i_mod == 4, p, p)))))
    b = np.where(i_mod == 0, p, np.where(i_mod == 1, p, np.where(i_mod == 2, t,
        np.where(i_mod == 3, v, np.where(i_mod == 4, v, q)))))

    # s ≈ 0 の場合はグレー
    gray_mask = s < 1e-5
    r = np.where(gray_mask, v, r)
    g = np.where(gray_mask, v, g)
    b = np.where(gray_mask, v, b)

    rgb = np.stack([r, g, b], axis=-1)
    return np.clip(rgb * 255 + 0.5, 0, 255).astype(np.uint8)

def recolor_image(image_rgb, mask_alpha, sample_hsv, target_hsv, value_blend=0.85):
    """
    マスク内ピクセルを HSV recolor する (C# RecolorPixel と同じロジック)。
    image_rgb: HxWx3 uint8
    mask_alpha: HxW uint8 (>30 が対象)
    sample_hsv: (h, s, v) 0-1
    target_hsv: (h, s, v) 0-1
    """
    result = image_rgb.copy()
    hsv = rgb_to_hsv_array(image_rgb)
    mask = mask_alpha > 30

    s_h, s_s, s_v = sample_hsv
    t_h, t_s, t_v = target_hsv

    o_h = hsv[:, :, 0]
    o_s = hsv[:, :, 1]
    o_v = hsv[:, :, 2]

    new_h = np.full_like(o_h, t_h)
    new_s = np.where(s_s > 0.001, np.clip(o_s * t_s / s_s, 0, 1), t_s)
    new_v = np.clip(t_v * (1 - value_blend) + o_v * value_blend, 0, 1)

    new_hsv = np.stack([new_h, new_s, new_v], axis=-1)
    new_rgb = hsv_to_rgb_array(new_hsv)

    result[mask] = new_rgb[mask]
    return result

def average_color_of_layer(layer_rgba):
    """レイヤーの不透明ピクセルの平均 RGB / HSV を計算"""
    alpha = layer_rgba[:, :, 3]
    valid = alpha > 30
    if not np.any(valid):
        return (128, 128, 128), (0, 0, 0.5)

    r = layer_rgba[:, :, 0][valid].astype(np.float64)
    g = layer_rgba[:, :, 1][valid].astype(np.float64)
    b = layer_rgba[:, :, 2][valid].astype(np.float64)

    avg_r = int(r.mean() + 0.5)
    avg_g = int(g.mean() + 0.5)
    avg_b = int(b.mean() + 0.5)

    h, s, v = colorsys.rgb_to_hsv(avg_r / 255.0, avg_g / 255.0, avg_b / 255.0)
    return (avg_r, avg_g, avg_b), (round(h, 4), round(s, 4), round(v, 4))

def bbox_from_alpha(alpha, margin=30):
    """α > 30 の領域のバウンディングボックスを返す"""
    ys, xs = np.where(alpha > 30)
    if len(ys) == 0:
        return {"top": 0, "left": 0, "bottom": 0, "right": 0}
    return {
        "top": int(ys.min()) - margin,
        "left": int(xs.min()) - margin,
        "bottom": int(ys.max()) + margin,
        "right": int(xs.max()) + margin,
    }

# ── テストケース定義 ────────────────────────────────────────────────────────

# AVATAR_A (blue → 各色)
AVATAR_A_TESTS = [
    {"suffix": "red",    "h": 0.00, "s": 0.85, "v": 0.85},
    {"suffix": "green",  "h": 0.33, "s": 0.90, "v": 0.70},
    {"suffix": "purple", "h": 0.75, "s": 0.80, "v": 0.75},
    {"suffix": "teal",   "h": 0.55, "s": 0.70, "v": 0.50},
    {"suffix": "orange", "h": 0.08, "s": 0.90, "v": 0.90},
]

# AvatarB (red → 各色)
AVATAR_B_TESTS = [
    {"suffix": "blue",   "h": 0.60, "s": 0.85, "v": 0.85},
    {"suffix": "green",  "h": 0.33, "s": 0.90, "v": 0.70},
    {"suffix": "purple", "h": 0.75, "s": 0.80, "v": 0.75},
    {"suffix": "teal",   "h": 0.55, "s": 0.70, "v": 0.50},
    {"suffix": "orange", "h": 0.08, "s": 0.90, "v": 0.90},
]

def hsv_to_rgb_single(h, s, v):
    r, g, b = colorsys.hsv_to_rgb(h, s, v)
    return [int(r * 255 + 0.5), int(g * 255 + 0.5), int(b * 255 + 0.5)]

# ── AVATAR_A Hair 教師データ生成 ───────────────────────────────────────────────

def generate_avatar_a_hair():
    print("=== AVATAR_A Hair 教師データ生成 ===")
    ensure_dir(AVATAR_A_GT_DIR)

    psd_path = os.path.join(AVATAR_A_PSD_DIR, "AVATAR_A_Hair.psd")
    tex_path = os.path.join(AVATAR_A_TEX_DIR, "AVATAR_A_Hair.png")
    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    # 1. Blue レイヤーを抽出 → hair_original.png
    blue_layer = find_layer(psd, "Blue")
    if blue_layer is None:
        raise RuntimeError("Blue レイヤーが見つかりません")
    blue_rgba = layer_to_canvas(blue_layer, cw, ch)
    print(f"  Blue レイヤー: shape={blue_rgba.shape}, "
          f"opaque pixels={np.sum(blue_rgba[:,:,3] > 30):,}")

    hair_orig_path = os.path.join(AVATAR_A_GT_DIR, "hair_original.png")
    Image.fromarray(blue_rgba, "RGBA").save(hair_orig_path)
    print(f"  → {hair_orig_path}")

    # 2. Silver マスク: 合成画像で S<0.10
    composite_img = psd.composite()
    composite_rgba = np.array(composite_img.convert("RGBA"))
    composite_rgb = composite_rgba[:, :, :3]
    composite_alpha = composite_rgba[:, :, 3]
    hsv_map = rgb_to_hsv_array(composite_rgb)

    silver_mask = ((hsv_map[:, :, 1] < 0.10) & (composite_alpha > 30)).astype(np.uint8) * 255
    silver_count = int(np.sum(silver_mask > 0))
    silver_img = np.zeros((ch, cw, 4), dtype=np.uint8)
    silver_img[:, :, 0] = 255  # R channel filled for visibility
    silver_img[:, :, 1] = 255
    silver_img[:, :, 2] = 255
    silver_img[:, :, 3] = silver_mask
    silver_path = os.path.join(AVATAR_A_GT_DIR, "silver_mask.png")
    Image.fromarray(silver_img, "RGBA").save(silver_path)
    print(f"  Silver マスク: {silver_count:,} pixels → {silver_path}")

    # 3. サンプルカラー (Blue レイヤーの平均色)
    sample_rgb, sample_hsv = average_color_of_layer(blue_rgba)
    print(f"  サンプルカラー: RGB={sample_rgb}, HSV=({sample_hsv[0]:.4f}, {sample_hsv[1]:.4f}, {sample_hsv[2]:.4f})")

    # 4. Hair bbox
    hair_bbox = bbox_from_alpha(blue_rgba[:, :, 3])
    roi = {
        "top": hair_bbox["top"] + 30,
        "left": hair_bbox["left"] + 30,
        "bottom": hair_bbox["bottom"] - 30,
        "right": hair_bbox["right"] - 30,
    }

    # 5. fixtures.json
    tests_json = []
    for t in AVATAR_A_TESTS:
        target_rgb = hsv_to_rgb_single(t["h"], t["s"], t["v"])
        tests_json.append({
            "suffix": t["suffix"],
            "target_hsv": {"h": t["h"], "s": t["s"], "v": t["v"]},
            "target_rgb": target_rgb,
            "gt_file": f"ground_truth/gt_{t['suffix']}.png",
        })

    fixtures = {
        "canvas_size": [cw, ch],
        "hair_bbox": hair_bbox,
        "roi": roi,
        "sample_color_hsv": {"h": sample_hsv[0], "s": sample_hsv[1], "v": sample_hsv[2]},
        "sample_color_rgb": list(sample_rgb),
        "silver_pixel_count": silver_count,
        "tests": tests_json,
    }
    fixtures_path = os.path.join(AVATAR_A_GT_DIR, "fixtures.json")
    with open(fixtures_path, "w", encoding="utf-8") as f:
        json.dump(fixtures, f, indent=2, ensure_ascii=False)
    print(f"  → {fixtures_path}")

    # 6. GT 画像 (マスク内のみ recolor)
    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]
    blue_alpha = blue_rgba[:, :, 3]

    for t in AVATAR_A_TESTS:
        gt_rgb = recolor_image(
            tex_rgb, blue_alpha,
            sample_hsv,
            (t["h"], t["s"], t["v"]),
            value_blend=0.85)
        gt_rgba = np.dstack([gt_rgb, tex_img[:, :, 3]])
        gt_path = os.path.join(AVATAR_A_GT_DIR, f"gt_{t['suffix']}.png")
        Image.fromarray(gt_rgba, "RGBA").save(gt_path)
        print(f"  GT {t['suffix']}: → {gt_path}")

    print()

# ── AVATAR_A Costume 教師データ生成 ────────────────────────────────────────────

def generate_avatar_a_costume():
    print("=== AVATAR_A Costume 教師データ生成 ===")
    ensure_dir(AVATAR_A_GT_DIR)

    psd_path = os.path.join(AVATAR_A_PSD_DIR, "AVATAR_A_Costume.psd")
    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    # Blue_Base レイヤーを全て検索して合成マスクを作成
    blue_bases = find_layers(psd, "Blue_Base")
    print(f"  Blue_Base レイヤー数: {len(blue_bases)}")

    combined_mask = np.zeros((ch, cw), dtype=np.uint8)
    all_blue_rgba = np.zeros((ch, cw, 4), dtype=np.uint8)

    for bl in blue_bases:
        arr = layer_to_canvas(bl, cw, ch)
        alpha = arr[:, :, 3]
        combined_mask = np.maximum(combined_mask, alpha)
        # α 合成で色も蓄積
        a = alpha.astype(np.float32) / 255.0
        all_blue_rgba[:, :, :3] = (
            all_blue_rgba[:, :, :3].astype(np.float32) * (1 - a[:, :, None]) +
            arr[:, :, :3].astype(np.float32) * a[:, :, None]
        ).astype(np.uint8)
        all_blue_rgba[:, :, 3] = np.maximum(all_blue_rgba[:, :, 3], alpha)
        print(f"    {bl.name} (parent: {bl.parent.name if bl.parent else 'root'}): "
              f"opaque={np.sum(alpha > 30):,} px")

    mask_pixel_count = int(np.sum(combined_mask > 30))
    print(f"  合計 Blue マスク: {mask_pixel_count:,} pixels")

    # マスク画像保存
    mask_img = np.zeros((ch, cw, 4), dtype=np.uint8)
    mask_img[:, :, 0] = combined_mask  # R=mask value for test to read
    mask_img[:, :, 1] = combined_mask
    mask_img[:, :, 2] = combined_mask
    mask_img[:, :, 3] = combined_mask  # α=mask for general use
    mask_path = os.path.join(AVATAR_A_GT_DIR, "costume_blue_mask.png")
    Image.fromarray(mask_img, "RGBA").save(mask_path)
    print(f"  → {mask_path}")

    # サンプルカラー
    sample_rgb, sample_hsv = average_color_of_layer(all_blue_rgba)
    print(f"  サンプルカラー: RGB={sample_rgb}, HSV=({sample_hsv[0]:.4f}, {sample_hsv[1]:.4f}, {sample_hsv[2]:.4f})")

    # costume_fixtures.json
    tests_json = []
    for t in AVATAR_A_TESTS:
        target_rgb = hsv_to_rgb_single(t["h"], t["s"], t["v"])
        tests_json.append({
            "suffix": t["suffix"],
            "target_hsv": {"h": t["h"], "s": t["s"], "v": t["v"]},
            "target_rgb": target_rgb,
        })

    fixtures = {
        "canvas_size": [cw, ch],
        "sample_color_rgb": list(sample_rgb),
        "sample_color_hsv": {"h": sample_hsv[0], "s": sample_hsv[1], "v": sample_hsv[2]},
        "blue_mask_pixel_count": mask_pixel_count,
        "tests": tests_json,
    }
    fixtures_path = os.path.join(AVATAR_A_GT_DIR, "costume_fixtures.json")
    with open(fixtures_path, "w", encoding="utf-8") as f:
        json.dump(fixtures, f, indent=2, ensure_ascii=False)
    print(f"  → {fixtures_path}")
    print()

# ── AvatarB Bandana 教師データ生成 ─────────────────────────────────────────────

def generate_avatar_b_bandana():
    print("=== AvatarB Bandana 教師データ生成 ===")
    ensure_dir(AVATAR_B_GT_DIR)

    psd_path = os.path.join(AVATAR_B_PSD_DIR, "Clothes.psd")
    tex_path = os.path.join(AVATAR_B_TEX_DIR, "Clothes.png")
    psd = PSDImage.open(psd_path)
    cw, ch = psd.width, psd.height

    # Bandana グループを検索
    bandana_group = find_layer(psd, "Bandana", recursive=True)
    if bandana_group is None:
        raise RuntimeError("Bandana グループが見つかりません")

    # Bandana グループを合成
    bandana_rgba = composite_group(bandana_group, cw, ch)
    bandana_alpha = bandana_rgba[:, :, 3]
    opaque_count = int(np.sum(bandana_alpha > 30))
    print(f"  Bandana グループ: opaque={opaque_count:,} px")

    # bandana_original.png (グループ合成結果、α=マスク)
    orig_path = os.path.join(AVATAR_B_GT_DIR, "bandana_original.png")
    Image.fromarray(bandana_rgba, "RGBA").save(orig_path)
    print(f"  → {orig_path}")

    # bandana_mask.png (白黒マスク)
    mask_vis = np.zeros((ch, cw, 4), dtype=np.uint8)
    mask_vis[:, :, 0] = (bandana_alpha > 30).astype(np.uint8) * 255
    mask_vis[:, :, 1] = mask_vis[:, :, 0]
    mask_vis[:, :, 2] = mask_vis[:, :, 0]
    mask_vis[:, :, 3] = 255
    mask_path = os.path.join(AVATAR_B_GT_DIR, "bandana_mask.png")
    Image.fromarray(mask_vis, "RGBA").save(mask_path)
    print(f"  → {mask_path}")

    # Bandana ベースレイヤーからサンプルカラーを取得
    bandana_base = find_layer(bandana_group, "Bandana")
    if bandana_base is None:
        # フォールバック: グループ合成全体から
        bandana_base_rgba = bandana_rgba
    else:
        bandana_base_rgba = layer_to_canvas(bandana_base, cw, ch)
    sample_rgb, sample_hsv = average_color_of_layer(bandana_base_rgba)
    print(f"  サンプルカラー: RGB={sample_rgb}, HSV=({sample_hsv[0]:.4f}, {sample_hsv[1]:.4f}, {sample_hsv[2]:.4f})")

    # bbox / ROI
    bandana_bbox = bbox_from_alpha(bandana_alpha, margin=0)
    roi = bbox_from_alpha(bandana_alpha, margin=-30)

    # fixtures.json
    tests_json = []
    for t in AVATAR_B_TESTS:
        target_rgb = hsv_to_rgb_single(t["h"], t["s"], t["v"])
        tests_json.append({
            "suffix": t["suffix"],
            "target_hsv": {"h": t["h"], "s": t["s"], "v": t["v"]},
            "target_rgb": target_rgb,
            "gt_file": f"ground_truth/gt_{t['suffix']}.png",
        })

    fixtures = {
        "canvas_size": [cw, ch],
        "bandana_bbox": bandana_bbox,
        "roi": roi,
        "sample_color_hsv": {"h": sample_hsv[0], "s": sample_hsv[1], "v": sample_hsv[2]},
        "sample_color_rgb": list(sample_rgb),
        "tests": tests_json,
    }
    fixtures_path = os.path.join(AVATAR_B_GT_DIR, "fixtures.json")
    with open(fixtures_path, "w", encoding="utf-8") as f:
        json.dump(fixtures, f, indent=2, ensure_ascii=False)
    print(f"  → {fixtures_path}")

    # GT 画像
    tex_img = np.array(Image.open(tex_path).convert("RGBA"))
    tex_rgb = tex_img[:, :, :3]

    for t in AVATAR_B_TESTS:
        gt_rgb = recolor_image(
            tex_rgb, bandana_alpha,
            sample_hsv,
            (t["h"], t["s"], t["v"]),
            value_blend=0.85)
        gt_rgba = np.dstack([gt_rgb, tex_img[:, :, 3]])
        gt_path = os.path.join(AVATAR_B_GT_DIR, f"gt_{t['suffix']}.png")
        Image.fromarray(gt_rgba, "RGBA").save(gt_path)
        print(f"  GT {t['suffix']}: → {gt_path}")

    print()

# ── メイン ──────────────────────────────────────────────────────────────────

def main():
    print(f"BASE_DIR: {BASE_DIR}\n")
    generate_avatar_a_hair()
    generate_avatar_a_costume()
    generate_avatar_b_bandana()
    print("=== 全教師データ生成完了 ===")

if __name__ == "__main__":
    main()
