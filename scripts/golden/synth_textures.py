"""決定論的な合成テクスチャ群（C# 自己ゴールデン回帰テスト用）。

画像バイナリはリポジトリに置かず、ここで再現生成する。すべて座標式（乱数なし）で
生成するので実行ごとに同一（Date/乱数の非決定性なし）。各関数は (H,W,4) uint8 RGBA を返す
（α は常に 255 = 不透明）。

狙い: 再着色パイプラインの各経路（ベタ塗り再着色 / 明度リマップ / AA 境界デコンタミ /
ハイライト帯 / 無彩背景の過検出 / グレーモード）を小さな入力で個別に踏ませ、C# 実装の
挙動変化（リグレッション）をゴールデンで捕捉する。
"""
from __future__ import annotations

import numpy as np


def _rgba(h: int, w: int) -> np.ndarray:
    img = np.zeros((h, w, 4), np.uint8)
    img[..., 3] = 255
    return img


def solid(rgb, h: int = 40, w: int = 40) -> np.ndarray:
    """単色ベタ。再着色（色相/彩度/明度写像）のみを踏む最小ケース。"""
    img = _rgba(h, w)
    img[..., 0], img[..., 1], img[..., 2] = rgb
    return img


def vertical_gradient(rgb_top, rgb_bottom, h: int = 48, w: int = 48) -> np.ndarray:
    """縦方向の線形グラデ。OkLab の 2 区間 L リマップ（陰影保持）を踏む。"""
    img = _rgba(h, w)
    t = np.linspace(0.0, 1.0, h)[:, None]
    for c in range(3):
        img[..., c] = np.clip(rgb_top[c] * (1.0 - t) + rgb_bottom[c] * t, 0, 255).astype(np.uint8)
    return img


def shaded_with_highlight(base_rgb, h: int = 48, w: int = 48) -> np.ndarray:
    """中央に明部スペキュラ（白寄り）を持つ球状の陰影。

    暗いターゲットでの明部の白暴走（OkLab ハイライト L キャップ）や、ハイライト帯/復元の
    経路を踏ませる。周辺=暗いシャドウ、中央=明るいハイライト、中心付近はさらに白へ寄る。
    """
    img = _rgba(h, w)
    yy, xx = np.mgrid[0:h, 0:w]
    cy, cx = (h - 1) / 2.0, (w - 1) / 2.0
    d = np.sqrt(((yy - cy) / (h / 2.0)) ** 2 + ((xx - cx) / (w / 2.0)) ** 2)
    v = np.clip(1.0 - d, 0.15, 1.0)                  # 明度プロファイル 0.15..1.0
    spec = np.clip(1.0 - d * 2.5, 0.0, 1.0) ** 2     # 中心付近だけ白へ
    for c in range(3):
        base = base_rgb[c] / 255.0 * v
        val = base * (1.0 - spec) + spec
        img[..., c] = np.clip(val * 255.0, 0, 255).astype(np.uint8)
    return img


def two_color_aa(rgb_a, rgb_b, h: int = 48, w: int = 48, ramp: int = 3) -> np.ndarray:
    """左 rgb_a / 右 rgb_b、境界に線形ブレンドの AA 帯。AA 境界デコンタミ経路を踏む。"""
    img = _rgba(h, w)
    edge = w // 2
    for x in range(w):
        if x < edge - ramp:
            t = 0.0
        elif x > edge + ramp:
            t = 1.0
        else:
            t = (x - (edge - ramp)) / float(2 * ramp)
        for c in range(3):
            img[:, x, c] = int(round(rgb_a[c] * (1.0 - t) + rgb_b[c] * t))
    return img


def chromatic_on_neutral(fg_rgb, bg_gray: int = 235, h: int = 48, w: int = 48) -> np.ndarray:
    """無彩（白っぽい）背景に有彩の円。白背景巻き込み（彩度整合ゲート）の検証用。"""
    img = _rgba(h, w)
    img[..., 0] = img[..., 1] = img[..., 2] = bg_gray
    yy, xx = np.mgrid[0:h, 0:w]
    cy, cx = (h - 1) / 2.0, (w - 1) / 2.0
    r = min(h, w) / 3.0
    inside = ((yy - cy) ** 2 + (xx - cx) ** 2) <= r * r
    for c in range(3):
        ch = img[..., c]
        ch[inside] = fg_rgb[c]
    return img
