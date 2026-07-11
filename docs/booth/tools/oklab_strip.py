# スライド7「色を変えても、濁らない。」の比較ストリップ用の色を実計算する。
# 同一の元色(赤の明暗5段=バンダナと同方向)を
#   A) 単純な HSV 色相回転(S/V据え置き)で青へ
#   B) OkLab で知覚明度・彩度を保ったまま色相だけ青へ
# 変換した結果を hex で出力する。模式値ではなく本スクリプトの計算値を
# slides.html に貼ること（誇大比較の防止）。
import colorsys
import math


def srgb_to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def linear_to_srgb(c: float) -> float:
    c = max(0.0, min(1.0, c))
    return 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def rgb_to_oklab(r: float, g: float, b: float):
    r, g, b = srgb_to_linear(r), srgb_to_linear(g), srgb_to_linear(b)
    l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b
    m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b
    s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b
    l, m, s = l ** (1 / 3), m ** (1 / 3), s ** (1 / 3)
    return (
        0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s,
    )


def oklab_to_rgb(L: float, a: float, b: float):
    l = L + 0.3963377774 * a + 0.2158037573 * b
    m = L - 0.1055613458 * a - 0.0638541728 * b
    s = L - 0.0894841775 * a - 1.2914855480 * b
    l, m, s = l ** 3, m ** 3, s ** 3
    r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
    g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
    bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
    return tuple(linear_to_srgb(c) for c in (r, g, bb))


def hexs(rgb) -> str:
    return "#" + "".join(f"{round(max(0, min(1, c)) * 255):02x}" for c in rgb)


def main() -> None:
    src_h, src_s = 8 / 360, 0.72            # 赤(バンダナ相当)
    tgt_h = 225 / 360                       # 青
    vs = [0.32, 0.49, 0.66, 0.83, 1.0]      # 明暗5段(シェーディング相当)

    # OkLab での目標色相(基準の黄から取る)
    ta, tb_ = rgb_to_oklab(*colorsys.hsv_to_rgb(tgt_h, src_s, 1.0))[1:]
    tgt_hue = math.atan2(tb_, ta)

    src, naive, ok = [], [], []
    for v in vs:
        rgb = colorsys.hsv_to_rgb(src_h, src_s, v)
        src.append(hexs(rgb))
        naive.append(hexs(colorsys.hsv_to_rgb(tgt_h, src_s, v)))
        L, a, b = rgb_to_oklab(*rgb)
        c = math.hypot(a, b)
        ok.append(hexs(oklab_to_rgb(L, c * math.cos(tgt_hue), c * math.sin(tgt_hue))))

    print("src  :", " ".join(src))
    print("naive:", " ".join(naive))
    print("oklab:", " ".join(ok))


if __name__ == "__main__":
    main()
