#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""いろか BOOTH 販促スライドを実寸 PNG に一括書き出しする（任意の補助スクリプト）。

主手段はブラウザ開発者ツールの "Capture node screenshot"（README 参照・依存ゼロ）。
こちらは一括で書き出したい人向けの補助で、Playwright を使う。

使い方:
    pip install playwright
    playwright install chromium
    python capture.py

出力:
    docs/booth/out/slide-1.png 〜 slide-8.png （各スライド実寸）
"""

from pathlib import Path

try:
    from playwright.sync_api import sync_playwright
except ImportError:  # pragma: no cover - 任意依存
    raise SystemExit(
        "Playwright が見つかりません。次を実行してください:\n"
        "    pip install playwright\n"
        "    playwright install chromium"
    )

HERE = Path(__file__).resolve().parent
INDEX = HERE / "index.html"
OUT_DIR = HERE / "out"
SLIDE_IDS = [f"slide-{i}" for i in range(1, 9)]


def main() -> None:
    OUT_DIR.mkdir(exist_ok=True)
    url = INDEX.as_uri()

    with sync_playwright() as p:
        browser = p.chromium.launch()
        # deviceScaleFactor=1 で CSS ピクセル＝出力ピクセル（実寸）にする
        page = browser.new_page(viewport={"width": 1400, "height": 900},
                                device_scale_factor=1)
        page.goto(url)
        # 実寸モードに切り替え（縮小 transform を解除して 1280px 実寸で描画）
        page.evaluate("document.body.classList.add('actual-size')")
        page.wait_for_timeout(300)  # フォント・レイアウト確定待ち

        for sid in SLIDE_IDS:
            element = page.query_selector(f"#{sid}")
            if element is None:
                print(f"  ! {sid} が見つかりませんでした。スキップします。")
                continue
            element.scroll_into_view_if_needed()
            dest = OUT_DIR / f"{sid}.png"
            element.screenshot(path=str(dest))
            box = element.bounding_box()
            size = f"{int(box['width'])}x{int(box['height'])}" if box else "?"
            print(f"  ✓ {dest.name}  ({size})")

        browser.close()

    print(f"\n完了: {OUT_DIR}")


if __name__ == "__main__":
    main()
