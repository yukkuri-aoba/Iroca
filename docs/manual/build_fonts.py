"""マニュアルの書体（Noto Sans JP）を、マニュアルに出る文字だけに絞った WOFF2 にする。

宣伝画像・紹介動画と同じ書体（2026-10-17 までは Zen Maru Gothic）。配布元の Noto Sans JP は太さを変えられる
1 ファイル（9 MB 余り）なので、原稿に出る字とかな・英数字・記号だけに絞り、本文と見出しの太さ（build_manual.py の
FONT_WEIGHTS）に固めて、太さごとの WOFF2 にする。原稿に新しい漢字を足すと build_manual.py が「フォントに無い字」で
止まるので、そのときにこれを流す。

    .venv\\Scripts\\python.exe docs/manual/build_fonts.py

仮名の詰め（palt）は、宣伝画像と紹介動画の字幕と同じ規則にそろえる（dev_safe/promo の make_plain4.py の tighten・
make_video.py の draw_text）: 仮名と約物は字面の幅＋GAP で送り、句読点の後ろは少し空け、漢字は少しだけ詰める。
Noto Sans JP にはもともと palt があるが、詰め方が違うので外して、この規則の palt に置き換える。
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
import urllib.parse
import urllib.request
from pathlib import Path

from fontTools import subset
from fontTools.otlLib import builder as otl
from fontTools.ttLib import TTFont
from fontTools.ttLib.tables import otTables as ot
from fontTools.varLib import instancer

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_manual import FONT_FILE, FONT_WEIGHTS, FONTS, font_chars, manual_chars  # noqa: E402

SOURCE_URL = "https://raw.githubusercontent.com/google/fonts/main/ofl/notosansjp/"
SOURCE_FONT = "NotoSansJP[wght].ttf"
# 配布元ごとに分ける（同じ名前の OFL.txt を、前の書体の写しと取り違えないように）
CACHE = Path(tempfile.gettempdir()) / "iroca-manual-fonts" / "notosansjp"

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
        p.write_bytes(urllib.request.urlopen(SOURCE_URL + urllib.parse.quote(name), timeout=300).read())
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


def set_palt(font) -> None:
    """GPOS の palt を、この規則の 1 つに置き換える（書体にもとからある palt は外す。kern などはそのまま）。"""
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
    keep = [r for r in old if r.FeatureTag != "palt"]
    new = sorted(keep + [feat], key=lambda r: r.FeatureTag)     # 機能の並びはタグ順（仕様）
    pos = {id(r): i for i, r in enumerate(new)}
    remap = {i: pos[id(r)] for i, r in enumerate(old) if id(r) in pos}
    gpos.FeatureList.FeatureRecord = new
    gpos.FeatureList.FeatureCount = len(new)
    for rec in gpos.ScriptList.ScriptRecord:
        langs = [rec.Script.DefaultLangSys] + [r.LangSys for r in rec.Script.LangSysRecord]
        for ls in filter(None, langs):
            ls.FeatureIndex = sorted([remap[i] for i in ls.FeatureIndex if i in remap] + [pos[id(feat)]])
            ls.FeatureCount = len(ls.FeatureIndex)


def main() -> int:
    chars = "".join(sorted(manual_chars() | set(BASE)))
    FONTS.mkdir(parents=True, exist_ok=True)
    opts = subset.Options()
    opts.flavor = "woff2"
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]          # 著作権表示とライセンスの名前を残す（OFL）
    opts.name_languages = ["*"]
    opts.notdef_outline = True
    # 太さを変えられるまま字を絞ってから、太さごとに固める（固めてから絞るより速い）
    var = subset.load_font(str(fetch(SOURCE_FONT)), opts)
    sub = subset.Subsetter(opts)
    sub.populate(text=chars)
    sub.subset(var)
    buf = io.BytesIO()
    var.save(buf)
    for weight in FONT_WEIGHTS:
        buf.seek(0)
        font = instancer.instantiateVariableFont(TTFont(buf), {"wght": weight}, updateFontNames=True)
        font.recalcTimestamp = False   # 同じ字からは毎回同じバイト列にする
        set_palt(font)
        out_buf = io.BytesIO()
        subset.save_font(font, out_buf, opts)
        out = FONTS / f"{FONT_FILE}-{weight}.woff2"
        out.write_bytes(out_buf.getvalue())
        print(f"done -> {out.relative_to(FONTS.parents[2])} ({len(out_buf.getvalue()) / 1024:.0f} KB)")
    (FONTS / "OFL.txt").write_bytes(fetch("OFL.txt").read_bytes())
    font_chars_path = FONTS / "chars.txt"
    font_chars_path.write_text(chars, encoding="utf-8", newline="\n")
    assert font_chars() == set(chars)
    print(f"{len(chars)} chars")
    return 0


if __name__ == "__main__":
    sys.exit(main())
