# BOOTH 宣伝画像用の実写素材を dev_safe から切り出して images/ に置く。
# 入力 (dev_safe/) は gitignore 対象なので、出力 PNG をコミットして自己完結させる。
#   python docs/booth/tools/crop_assets.py
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parents[3]
SRC = REPO / "dev_safe" / "slides" / "slides_img"
DST = Path(__file__).resolve().parents[1] / "images"

# (出力名, 元ファイル, クロップ箱 L,T,R,B)
# bandana: 1100x556。バンダナは中央上部、右上に Unity ギズモ(x>980)、左に床グリッド。
CROPS = [
    ("ba-before.png", SRC / "bandana" / "before.png", (310, 0, 726, 555)),  # 416x555 ≒ 3:4
    ("ba-after.png", SRC / "bandana" / "after.png", (310, 0, 726, 555)),
    # 表紙ヒーロー用のワイド版（バンダナ全体+肩まで、ギズモ・床グリッドを除外）
    ("ba-before-wide.png", SRC / "bandana" / "before.png", (346, 0, 970, 468)),  # 624x468 = 4:3
    ("ba-after-wide.png", SRC / "bandana" / "after.png", (346, 0, 970, 468)),
]


def main() -> None:
    DST.mkdir(parents=True, exist_ok=True)
    for name, src, box in CROPS:
        img = Image.open(src).convert("RGB")
        out = img.crop(box)
        out.save(DST / name, optimize=True)
        print(f"{name}: {out.size[0]}x{out.size[1]} <- {src.name} {box}")


if __name__ == "__main__":
    main()
