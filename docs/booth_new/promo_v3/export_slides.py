# -*- coding: utf-8 -*-
"""booth-promo-v3.html の各ボードを 1280x1280 PNG に書き出す（headless Chrome / Edge）。

    python docs/booth_new/promo_v3/export_slides.py          # 全ボード -> dev_safe/promo/booth_new_v3/out/slide-N.png
    python docs/booth_new/promo_v3/export_slides.py 3        # 指定ボードのみ

先に build_html.py を実行して HTML を作っておくこと。フォントは HTML と同じ階層の fonts/ を相対参照する。
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

D = Path(__file__).resolve().parent
REPO = D.parent.parent.parent
OUT_DIR = REPO / "dev_safe" / "promo" / "booth_new_v3"
HTML = OUT_DIR / "booth-promo-v3.html"
OUT = OUT_DIR / "out"
BOARD_COUNT = 6

BROWSERS = [
    Path("C:/Program Files/Google/Chrome/Application/chrome.exe"),
    Path("C:/Program Files (x86)/Google/Chrome/Application/chrome.exe"),
    Path("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"),
    Path("C:/Program Files/Microsoft/Edge/Application/msedge.exe"),
]


def find_browser() -> Path:
    for p in BROWSERS:
        if p.exists():
            return p
    sys.exit("chrome.exe / msedge.exe が見つかりません")


def shoot(browser: Path, board: int, out: Path) -> None:
    url = HTML.as_uri() + f"?board={board}"
    out.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        str(browser), "--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--force-device-scale-factor=1", "--window-size=1280,1280",
        "--virtual-time-budget=4000",   # @font-face の読み込みを待つ
        f"--screenshot={out}", url,
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, timeout=90)
    if not out.exists():
        sys.exit(f"board {board} の書き出しに失敗:\n{r.stderr}")
    print(f"board {board} -> {out}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("board", nargs="?", type=int)
    ap.add_argument("--out", type=Path)
    a = ap.parse_args()
    browser = find_browser()
    if a.board:
        shoot(browser, a.board, a.out or OUT / f"slide-{a.board}.png")
    else:
        for n in range(1, BOARD_COUNT + 1):
            shoot(browser, n, OUT / f"slide-{n}.png")
            time.sleep(0.3)


if __name__ == "__main__":
    main()
