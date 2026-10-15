"""マニュアルの書体（Zen Maru Gothic）を、マニュアルに出る文字だけに絞った WOFF2 にする。

宣伝画像・紹介動画と同じ丸ゴシック。全部の字を入れると 1 太さで 3.8 MB あるので、原稿に出る字と
かな・英数字・記号だけに絞る（1 太さ 130 KB ほど）。原稿に新しい漢字を足すと build_manual.py が
「フォントに無い字」で止まるので、そのときにこれを流す。

    .venv\\Scripts\\python.exe docs/manual/build_fonts.py

Zen Maru Gothic には仮名を詰める機能（palt）が無く、そのままだと仮名の周りが空いて間延びする。宣伝画像と
紹介動画の字幕は字ごとに詰めているので（dev_safe/promo の make_plain4.py・make_video.py）、同じ規則の palt を
ここで書体に足す: 仮名と約物は字面の幅＋GAP で送り、句読点の後ろは少し空け、漢字は少しだけ詰める。
CSS の font-feature-settings: "palt" で効く。

fontTools と brotli が要る（.venv\\Scripts\\python.exe -m pip install fonttools brotli）。
出力（docs/manual/fonts/）は追跡する。Pages の CI は標準ライブラリだけで build_manual.py を流し、
ここで作った WOFF2 をそのまま載せる。元の TTF は Google Fonts のリポジトリから取って一時フォルダに置く。
"""
from __future__ import annotations

import io
import re
import sys
import tempfile
import urllib.request
from pathlib import Path

from fontTools import subset
from fontTools.otlLib import builder as otl
from fontTools.ttLib.tables import otTables as ot

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_manual import FONT_WEIGHTS, FONTS, font_chars, manual_chars  # noqa: E402

SOURCE_URL = "https://raw.githubusercontent.com/google/fonts/main/ofl/zenmarugothic/"
CACHE = Path(tempfile.gettempdir()) / "iroca-manual-fonts"

# 原稿に無くても入れておく字（かな・英数字・約物）。漢字だけは原稿に出るものに限る。
BASE = "".join(chr(c) for r in ((0x20, 0x7E), (0x3000, 0x30FF), (0xFF01, 0xFF5E)) for c in range(r[0], r[1] + 1))
BASE += "→←↑↓…‥―–—‐・×÷±°✓①②③④⑤⑥⑦⑧⑨⑩"

# 詰めの規則（宣伝画像の make_plain4.py・字幕の draw_text と同じ。字の大きさに対する割合）
KANA = re.compile(r"[　-ヿ！-／：-＠]")   # 仮名・約物・全角の記号
KANJI = re.compile(r"[㐀-鿿]")
GAP, PUNCT_AFTER, KANJI_TRIM = 0.10, 0.30, 0.03


def fetch(name: str) -> Path:
    CACHE.mkdir(parents=True, exist_ok=True)
    p = CACHE / name
    if not p.is_file():
        print(f"download {name}")
        p.write_bytes(urllib.request.urlopen(SOURCE_URL + name, timeout=120).read())
    return p


def palt_values(font) -> dict[str, ot.ValueRecord]:
    """字ごとの詰め（左を詰める量・送りを減らす量）を、書体の字面から測る。"""
    upem = font["head"].unitsPerEm
    glyf, hmtx = font["glyf"], font["hmtx"]
    values = {}
    for code, name in sorted(font.getBestCmap().items()):
        ch = chr(code)
        if KANJI.match(ch):
            left = right = KANJI_TRIM / 2 * upem
        elif KANA.match(ch) and ch != "・":   # 中黒は前後の空白とそろえたいので詰めない
            g = glyf[name]
            g.recalcBounds(glyf)
            adv = hmtx[name][0]
            if g.numberOfContours == 0:       # 全角の空き
                left, right = 0, 0.3 * upem
            else:
                left = g.xMin - GAP * upem / 2
                right = (adv - g.xMax) - GAP * upem / 2
            if ch in "。、":
                right -= PUNCT_AFTER * upem
        else:
            continue
        left, right = round(left), round(right)
        if left or right:
            v = ot.ValueRecord()
            v.XPlacement, v.XAdvance = -left, -(left + right)
            values[name] = v
    return values


def add_palt(font) -> None:
    """GPOS に palt の機能を足す（既存の kern などはそのまま残す）。"""
    gpos = font["GPOS"].table
    sub = otl.buildSinglePosSubtable(palt_values(font), font.getReverseGlyphMap())
    gpos.LookupList.Lookup.append(otl.buildLookup([sub]))
    gpos.LookupList.LookupCount = len(gpos.LookupList.Lookup)

    feat = ot.FeatureRecord()
    feat.FeatureTag = "palt"
    feat.Feature = ot.Feature()
    feat.Feature.FeatureParams = None
    feat.Feature.LookupListIndex = [gpos.LookupList.LookupCount - 1]
    feat.Feature.LookupCount = 1
    old = gpos.FeatureList.FeatureRecord
    new = sorted(old + [feat], key=lambda r: r.FeatureTag)     # 機能の並びはタグ順（仕様）
    pos = {id(r): i for i, r in enumerate(new)}
    remap = {i: pos[id(r)] for i, r in enumerate(old)}
    gpos.FeatureList.FeatureRecord = new
    gpos.FeatureList.FeatureCount = len(new)
    for rec in gpos.ScriptList.ScriptRecord:
        langs = [rec.Script.DefaultLangSys] + [r.LangSys for r in rec.Script.LangSysRecord]
        for ls in filter(None, langs):
            ls.FeatureIndex = sorted([remap[i] for i in ls.FeatureIndex] + [pos[id(feat)]])
            ls.FeatureCount = len(ls.FeatureIndex)


def main() -> int:
    chars = "".join(sorted(manual_chars() | set(BASE)))
    FONTS.mkdir(parents=True, exist_ok=True)
    for weight, style in FONT_WEIGHTS.items():
        opts = subset.Options()
        opts.flavor = "woff2"
        opts.layout_features = ["*"]
        opts.name_IDs = ["*"]          # 著作権表示とライセンスの名前を残す（OFL）
        opts.name_languages = ["*"]
        opts.notdef_outline = True
        font = subset.load_font(str(fetch(f"ZenMaruGothic-{style}.ttf")), opts)
        font.recalcTimestamp = False   # 同じ字からは毎回同じバイト列にする
        sub = subset.Subsetter(opts)
        sub.populate(text=chars)
        sub.subset(font)
        add_palt(font)
        buf = io.BytesIO()
        subset.save_font(font, buf, opts)
        out = FONTS / f"zen-maru-gothic-{weight}.woff2"
        out.write_bytes(buf.getvalue())
        print(f"done -> {out.relative_to(FONTS.parents[2])} ({len(buf.getvalue()) / 1024:.0f} KB)")
    (FONTS / "OFL.txt").write_bytes(fetch("OFL.txt").read_bytes())
    font_chars_path = FONTS / "chars.txt"
    font_chars_path.write_text(chars, encoding="utf-8", newline="\n")
    assert font_chars() == set(chars)
    print(f"{len(chars)} chars")
    return 0


if __name__ == "__main__":
    sys.exit(main())
