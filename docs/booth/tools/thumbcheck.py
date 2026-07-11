# 表紙 PNG を BOOTH 一覧サムネ相当(150px幅)に縮小したコンタクトシートを作る。
#   python docs/booth/tools/thumbcheck.py [png...]
# 引数省略時は out/candidates/cover-*.png を対象にする。
import sys
from pathlib import Path

from PIL import Image

BOOTH = Path(__file__).resolve().parents[1]


def main() -> None:
    paths = [Path(p) for p in sys.argv[1:]]
    if not paths:
        paths = sorted((BOOTH / "out" / "candidates").glob("cover-*.png"))
    if not paths:
        raise SystemExit("対象PNGがありません")
    thumbs = []
    for p in paths:
        img = Image.open(p).convert("RGB")
        w = 150
        h = round(img.height * w / img.width)
        thumbs.append(img.resize((w, h), Image.LANCZOS))
    pad = 16
    sheet = Image.new(
        "RGB",
        (pad + sum(t.width + pad for t in thumbs), max(t.height for t in thumbs) + 2 * pad),
        (255, 255, 255),  # BOOTH 一覧の白地を模す
    )
    x = pad
    for t in thumbs:
        sheet.paste(t, (x, pad))
        x += t.width + pad
    out = paths[0].parent / "thumbs-150.png"
    sheet.save(out)
    print(out)


if __name__ == "__main__":
    main()
