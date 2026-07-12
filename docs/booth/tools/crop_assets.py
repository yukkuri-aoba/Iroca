# BOOTH 宣伝画像用に、Unity 実 UI のスクリーンショットから「見せたい箇所」を切り出す。
#
# 生のダークな UI ウィンドウをまるごと貼ると、情報量が多すぎて何を見ればいいか伝わらない。
# 各スライドで訴えたい 1 点（スポイト / ブラシ / 差分表示）だけを切り出して使う。
#
#   python docs/booth/tools/crop_assets.py
from pathlib import Path

from PIL import Image

IMG = Path(__file__).resolve().parents[1] / "images"

# (出力名, 元ファイル, クロップ箱 L,T,R,B, 説明)
CROPS = [
    # ゾーンカード。スポイトのボタンが青く武装している状態。
    ("ui-zone.png", "zone-eyedropper.png", (0, 150, 380, 470)),
    # プレビュー上にピンクのブラシストロークでマスクを塗っている状態。
    ("ui-mask.png", "mask-brush.png", (383, 195, 905, 765)),
    # 「差分表示」タブがアクティブで、変更画素がオレンジに塗られている状態。
    ("ui-preview.png", "preview-diff.png", (383, 190, 960, 765)),
]


def main() -> None:
    for name, src, box in CROPS:
        img = Image.open(IMG / src).convert("RGB")
        out = img.crop(box)
        out.save(IMG / name, optimize=True)
        print(f"{name}: {out.size[0]}x{out.size[1]} <- {src} {box}")


if __name__ == "__main__":
    main()
