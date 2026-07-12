# BOOTH スライド/表紙候補を PNG に撮影する。
#   python docs/booth/tools/capture.py --file candidates.html --ids cover-1,cover-2,cover-3 --out out/candidates
#   python docs/booth/tools/capture.py            (slides.html の slide-1..8 → out/)
# system Chrome を使用 (channel="chrome")。Google Fonts の読込が必須のため、
# ウェブフォントが取得できていない場合は撮影せずエラーで止める。
import argparse
from pathlib import Path

from playwright.sync_api import sync_playwright

BOOTH = Path(__file__).resolve().parents[1]


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--file", default="slides.html")
    ap.add_argument("--ids", default=",".join(f"slide-{i}" for i in range(1, 9)))
    ap.add_argument("--out", default="out")
    # ページ内で実際に使うフォントのみ指定する（未使用フォントは遅延ロードされず false になる）
    ap.add_argument("--fonts", default="Noto Sans JP")
    args = ap.parse_args()
    required_fonts = [f.strip() for f in args.fonts.split(",") if f.strip()]
    ids = [s for s in args.ids.split(",") if s]
    out_dir = BOOTH / args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    with sync_playwright() as p:
        browser = p.chromium.launch(channel="chrome")
        page = browser.new_page(viewport={"width": 1500, "height": 1000}, device_scale_factor=1)
        page.goto((BOOTH / args.file).as_uri())
        page.evaluate("document.fonts.ready.then(() => true)")
        loaded = page.evaluate(
            "Array.from(document.fonts)"
            ".filter(f => f.status === 'loaded')"
            ".map(f => f.family.replace(/[\"']/g, ''))"
        )
        missing = [f for f in required_fonts if f not in loaded]
        if missing:
            raise SystemExit(
                f"ウェブフォント未ロード: {missing} — ネットワーク接続を確認（フォールバック字体での撮影は禁止）"
            )
        for el_id in ids:
            el = page.locator(f"#{el_id}")
            el.scroll_into_view_if_needed()
            path = out_dir / f"{el_id}.png"
            el.screenshot(path=str(path))
            print(f"{path.relative_to(BOOTH)} ({path.stat().st_size // 1024} KB)")
        browser.close()


if __name__ == "__main__":
    main()
