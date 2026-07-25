# -*- coding: utf-8 -*-
"""booth-promo-comps.html の各ボードを 1280x1280 PNG に書き出す。

使い方:
    .venv\\Scripts\\python.exe docs/booth_new/promo/export_slides.py

前提: playwright と chromium が入っていること
    .venv\\Scripts\\python.exe -m playwright install chromium
"""
import os
import pathlib

from playwright.sync_api import sync_playwright

D = os.path.dirname(os.path.abspath(__file__))
HTML = pathlib.Path(D, "booth-promo-comps.html").as_uri()
OUT = os.path.join(D, "out")
os.makedirs(OUT, exist_ok=True)

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page(viewport={"width": 1400, "height": 1500}, device_scale_factor=1)
    page.goto(HTML)
    # ボードを等倍(1280x1280)へ固定し、角丸・枠線を外す
    page.add_style_tag(content=(
        ".stage{width:1280px!important;height:1280px!important;"
        "border-radius:0!important;outline:none!important;}"
        ".board{transform:none!important;}"
    ))
    page.wait_for_timeout(500)
    stages = page.locator(".stage")
    for i in range(stages.count()):
        stages.nth(i).scroll_into_view_if_needed()
        path = os.path.join(OUT, f"slide-{i + 1}.png")
        stages.nth(i).screenshot(path=path)
        print("exported", path)
    browser.close()
