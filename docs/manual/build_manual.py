"""docs/manual/src の原稿から、HTML マニュアル（ページと、そこから読む CSS・JS・書体・画像・動画）を作る。

原稿は章ごとの HTML 断片（src/<言語>/NN-<章ID>.html）。出力はフォルダ 1 つで、index.html と
manual.css・manual.js、原稿が参照する素材の写し（fonts/・img/・clips/）が入る。
配るのは GitHub Pages のオンライン版だけ（scripts/pages/build_site.py が同じものを manual/ に載せる）。

    .venv\\Scripts\\python.exe docs/manual/build_manual.py              # -> docs/manual/dist/（index.html をダブルクリックで確認）
    .venv\\Scripts\\python.exe docs/manual/build_manual.py --out x      # 出力先のフォルダを指定

標準ライブラリだけで動く（Pages の CI でも手元と同じ出力になるように）。
同じ原稿からは毎回同じバイト列を出す（作り直しても差分が出ないように）。CSS・JS・素材の URL には
中身のハッシュを付ける（?v=…）ので、作り直した版を開いたときにブラウザの古い写しが混ざらない。

原稿の書き方は docs/manual/README.md。画像素材は build_assets.py が作る。
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import shutil
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANUAL = ROOT / "docs" / "manual"
SRC = MANUAL / "src"
DEFAULT_OUT = MANUAL / "dist"

# 言語の並び。先頭が既定（id を実際に持つ側）。原稿フォルダがある言語だけを載せる。
LANG_ORDER = ("ja", "en")

# 載せてよい素材の形式
ASSET_SUFFIXES = (".webp", ".png", ".jpg", ".jpeg", ".mp4", ".webm", ".woff2")

# 出力のページに入れる印。出力先にこの印の無い index.html があれば、別物とみて消さずに止める。
GENERATOR = "Iroca docs/manual/build_manual.py"

# 書体は宣伝画像・紹介動画と同じ Zen Maru Gothic。原稿に出る字だけに絞った WOFF2 を build_fonts.py が作る。
FONTS = MANUAL / "fonts"
FONT_WEIGHTS = {500: "Medium", 700: "Bold"}     # 本文 / 見出しと強調


# ── 書体 ─────────────────────────────────────────────────────────

def manual_chars() -> set[str]:
    """マニュアルに出うる字（原稿・外枠・JS の文言・実演の文言）。フォントの部分集合はこれを覆う。"""
    text = "".join(html.unescape(p.read_text(encoding="utf-8"))
                   for p in [*sorted(SRC.glob("*/*.html")), SRC / "page.html", SRC / "manual.js"])
    text += json.dumps(json.loads((SRC / "demos.json").read_text(encoding="utf-8")), ensure_ascii=False)
    return {c for c in text if not c.isspace()}


def font_chars() -> set[str]:
    p = FONTS / "chars.txt"
    return set(p.read_text(encoding="utf-8")) - {"\n"} if p.is_file() else set()


def font_urls() -> list[str]:
    """書体の URL（FONT_WEIGHTS の順）。原稿の字がすべて部分集合にあることを確かめてから載せる。"""
    missing = "".join(sorted(manual_chars() - font_chars()))
    if missing:
        raise SystemExit(f"マニュアルの字がフォントの部分集合にありません: {missing}\n"
                         "  .venv\\Scripts\\python.exe docs/manual/build_fonts.py で作り直してください")
    return [asset_url(f"fonts/zen-maru-gothic-{weight}.woff2") for weight in FONT_WEIGHTS]


def font_css(urls: list[str]) -> str:
    rules = [('@font-face { font-family: "Zen Maru Gothic"; font-style: normal; '
              f'font-weight: {weight}; font-display: swap; src: url({url}) format("woff2"); }}')
             for weight, url in zip(FONT_WEIGHTS, urls)]
    return ("/* Zen Maru Gothic — Copyright 2021 The Zen Maru Gothic Project Authors"
            " (https://github.com/googlefonts/zen-marugothic). SIL Open Font License 1.1"
            " (https://openfontlicense.org). マニュアルに出る字だけに絞っている。 */\n"
            + "\n".join(rules))


# ── 素材 ─────────────────────────────────────────────────────────

# 参照された素材（docs/manual からの相対パス -> 中身）。出力にはこれだけを写す。
ASSETS: dict[str, bytes] = {}


def asset_path(rel: str) -> Path:
    p = (MANUAL / rel).resolve()
    if MANUAL.resolve() not in p.parents:
        raise SystemExit(f"docs/manual の外は参照できません: {rel}")
    if not p.is_file():
        raise SystemExit(f"素材がありません: {rel}")
    return p


def versioned(rel: str, data: bytes) -> str:
    """中身のハッシュを付けた URL。中身が変われば URL も変わる。"""
    return f"{rel}?v={hashlib.sha256(data).hexdigest()[:10]}"


def asset_url(rel: str) -> str:
    """素材を出力に載せ、ページから参照する URL を返す。"""
    if rel not in ASSETS:
        p = asset_path(rel)
        if p.suffix.lower() not in ASSET_SUFFIXES:
            raise SystemExit(f"載せられない形式です: {rel}")
        ASSETS[rel] = p.read_bytes()
    return versioned(rel, ASSETS[rel])


def image_size(rel: str) -> tuple[int, int] | None:
    """WebP / PNG の実寸を読む（Pillow に頼らない）。"""
    b = asset_path(rel).read_bytes()[:64]
    if b[:4] == b"RIFF" and b[8:12] == b"WEBP":
        kind = b[12:16]
        if kind == b"VP8 ":
            w, h = struct.unpack("<HH", b[26:30])
            return w & 0x3FFF, h & 0x3FFF
        if kind == b"VP8L":
            bits = int.from_bytes(b[21:25], "little")
            return (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1
        if kind == b"VP8X":
            return int.from_bytes(b[24:27], "little") + 1, int.from_bytes(b[27:30], "little") + 1
    if b[:8] == b"\x89PNG\r\n\x1a\n":
        return struct.unpack(">II", b[16:24])
    return None


def video_size(rel: str) -> tuple[int, int] | None:
    """MP4 の映像トラックの表示寸法（tkhd の width / height）を読む。"""
    data = asset_path(rel).read_bytes()
    pos = 0
    while True:
        i = data.find(b"tkhd", pos)
        if i < 0:
            return None
        ver = data[i + 4]
        off = i + 4 + (88 if ver == 1 else 76)
        w, h = struct.unpack(">II", data[off:off + 8])
        if w and h:
            return w >> 16, h >> 16
        pos = i + 4


# ── 原稿の読み込み ───────────────────────────────────────────────

@dataclass
class Chapter:
    cid: str
    title: str
    body: str                       # h2 を除いた本文（カスタムタグのまま）
    subs: list[tuple[str, str]] = field(default_factory=list)  # (id, 見出し) … h3 だけ
    ids: set[str] = field(default_factory=set)
    text: str = ""                  # 検索用


H2_RE = re.compile(r"^\s*<h2>(.*?)</h2>\s*", re.S)
ID_RE = re.compile(r'(?<![\w-])id="([^"]+)"')
H3_RE = re.compile(r'<h3 id="([^"]+)"[^>]*>(.*?)</h3>', re.S)
TAG_RE = re.compile(r"<[^>]+>")
XREF_RE = re.compile(r'href="#([^"]+)"')


def strip_tags(s: str) -> str:
    return html.unescape(TAG_RE.sub(" ", s))


def load_language(lang: str) -> list[Chapter]:
    folder = SRC / lang
    files = sorted(folder.glob("*.html"))
    chapters = []
    for f in files:
        m = re.match(r"^\d+-([a-z0-9-]+)\.html$", f.name)
        if not m:
            raise SystemExit(f"章のファイル名は NN-<章ID>.html にしてください: {f.relative_to(ROOT)}")
        src = f.read_text(encoding="utf-8")
        h = H2_RE.match(src)
        if not h:
            raise SystemExit(f"章の先頭に <h2>見出し</h2> がありません: {f.relative_to(ROOT)}")
        body = src[h.end():]
        ch = Chapter(cid=m.group(1), title=strip_tags(h.group(1)).strip(), body=body)
        ch.subs = [(a, strip_tags(t).strip()) for a, t in H3_RE.findall(body)]
        ch.ids = set(ID_RE.findall(body))
        # 実演・動画の中身（JSON や base64）は検索に入れない。キャプションは入れる。
        ch.text = re.sub(r"\s+", " ", strip_tags(ch.title + " " + body)).strip()
        chapters.append(ch)
    return chapters


def check(langs: dict[str, list[Chapter]]) -> None:
    base_lang = next(iter(langs))
    base = langs[base_lang]
    for lang, chs in langs.items():
        cids = [c.cid for c in chs]
        if len(set(cids)) != len(cids):
            raise SystemExit(f"{lang}: 章 ID が重複しています: {cids}")
        all_ids: dict[str, str] = {}
        for c in chs:
            for i in c.ids | {c.cid}:
                if i in all_ids and not (i == c.cid and all_ids[i] == c.cid):
                    raise SystemExit(f"{lang}: id が重複しています: {i}（{all_ids[i]} と {c.cid}）")
                all_ids[i] = c.cid
        for c in chs:
            for target in XREF_RE.findall(c.body):
                if target not in all_ids:
                    raise SystemExit(f"{lang}/{c.cid}: リンク先 #{target} がありません")
        if lang != base_lang:
            if cids != [c.cid for c in base]:
                raise SystemExit(f"{lang}: 章の並びが {base_lang} と違います（翻訳は章 ID と順番を揃える）\n"
                                 f"  {base_lang}: {[c.cid for c in base]}\n  {lang}: {cids}")
            for c, b in zip(chs, base):
                if c.ids != b.ids:
                    raise SystemExit(f"{lang}/{c.cid}: 見出しの id が {base_lang} と違います: "
                                     f"{sorted(c.ids ^ b.ids)}")


# ── カスタムタグの展開 ───────────────────────────────────────────

ATTR_RE = re.compile(r'([\w-]+)(?:="([^"]*)")?')


def attrs(s: str) -> dict[str, str]:
    return {k: (v if v is not None else "") for k, v in ATTR_RE.findall(s)}


def esc(s: str) -> str:
    return html.escape(s, quote=True)


class Renderer:
    def __init__(self, demos: dict, lang: str):
        self.demos = demos
        self.lang = lang

    def shot(self, a: dict[str, str], caption: str) -> str:
        src = a["src"]
        wh = image_size(src)
        size = f' width="{wh[0]}" height="{wh[1]}"' if wh else ""
        # 原板を引き伸ばさない（Unity のスクリーンショットは pixelsPerPoint 1.25 の実ピクセル）
        cap_w = f' style="max-width:{wh[0]}px"' if wh else ""
        cls = "figure wide" if "wide" in a else "figure"
        alt = esc(strip_tags(caption).strip())
        cap = f"<figcaption>{caption.strip()}</figcaption>" if caption.strip() else ""
        return (f'<figure class="{cls}"{cap_w}><button class="shot" type="button">'
                f'<img src="{asset_url(src)}" alt="{alt}"{size} loading="lazy"></button>{cap}</figure>')

    def demo(self, a: dict[str, str], _inner: str) -> str:
        demo_id = a["id"]
        if demo_id not in self.demos:
            raise SystemExit(f"実演 {demo_id} が src/demos.json にありません")
        d = self.demos[demo_id]
        t = d.get(self.lang) or d[LANG_ORDER[0]]
        frames = d["frames"]
        start = d["start"]
        srcs = [asset_url(f'img/demo/{f["img"]}.webp') for f in frames]
        wh = image_size(f'img/demo/{frames[start]["img"]}.webp')
        size = f' width="{wh[0]}" height="{wh[1]}"' if wh else ""

        def on(i: int) -> str:
            return " on" if i == start else ""

        def label(f: dict) -> str:
            return f.get(self.lang, f.get(LANG_ORDER[0], ""))

        if d["control"] == "swatch":
            ctl = '<div class="swatches" role="radiogroup">' + "".join(
                f'<button type="button" class="sw{on(i)}" data-i="{i}"'
                f' style="--sw:{f["hex"]}" title="{esc(label(f))}">'
                f"<span>{esc(label(f))}</span></button>"
                for i, f in enumerate(frames)) + "</div>"
        else:
            ctl = (f'<div class="slider"><span class="lab">{esc(t["label"])}</span>'
                   f'<input type="range" min="0" max="{len(frames) - 1}" step="1"'
                   f' value="{start}" aria-label="{esc(t["label"])}">'
                   f'<output>{frames[start]["v"]}</output></div>')
        tags = "".join(f'<span class="tag{on(i)}" data-i="{i}">{esc(label(f))}</span>'
                       for i, f in enumerate(frames) if label(f))
        values = esc(json.dumps([f["v"] for f in frames], ensure_ascii=False))
        return (f'<figure class="demo" data-demo="{demo_id}" data-values="{values}"'
                f' data-frames="{esc(json.dumps(srcs))}" data-start="{start}">'
                f'<figcaption class="demo-head"><span class="demo-title">{esc(t["title"])}</span></figcaption>'
                f'<div class="stage"><img class="fr on" src="{srcs[start]}" alt=""{size} loading="lazy"></div>'
                f"{ctl}"
                f'<p class="demo-note"><span class="frame-tag">{tags}</span>{esc(t["note"])}</p>'
                f"</figure>")

    def before_after(self, a: dict[str, str], hint: str) -> str:
        left, right = ("変更前", "変更後") if self.lang == "ja" else ("Before", "After")
        return ('<figure class="ba"><div class="ba-stage">'
                f'<img class="ba-after" src="{asset_url(a["after"])}" alt="" loading="lazy">'
                f'<img class="ba-before" src="{asset_url(a["before"])}" alt="" loading="lazy">'
                '<div class="ba-handle" role="slider" aria-valuemin="0" aria-valuemax="100"'
                ' aria-valuenow="50" tabindex="0"><span></span></div>'
                f'<span class="ba-tag left">{left}</span><span class="ba-tag right">{right}</span>'
                f"</div><figcaption>{hint.strip()}</figcaption></figure>")

    def clip(self, a: dict[str, str], caption: str) -> str:
        src = a["src"]
        wh = video_size(src)
        if not wh:
            raise SystemExit(f"動画の寸法を読めません（MP4 の映像トラックが要ります）: {src}")
        poster = f' poster="{asset_url(a["poster"])}"' if a.get("poster") else ""
        cap = f"<figcaption>{caption.strip()}</figcaption>" if caption.strip() else ""
        # wide は本文の幅を越えて元の大きさまで広げる（UI の文字を縮めて潰さないため。CSS の .clip.wide）
        if "wide" in a:
            cls, style = "clip wide", f"--clip-w:{wh[0]}px"
        else:
            cls, style = "clip", f"max-width:{wh[0]}px"
        return (f'<figure class="{cls}" style="{style}">'
                f'<div class="clip-stage" style="aspect-ratio:{wh[0]}/{wh[1]}">'
                # 画面に入るまで読み込まない（再生は manual.js が画面に入ったときに始める）
                f'<video src="{asset_url(src)}" muted loop playsinline controls preload="none"'
                f' width="{wh[0]}" height="{wh[1]}"{poster}></video>'
                f"</div>{cap}</figure>")

    TAGS = {"x-shot": "shot", "x-demo": "demo", "x-before-after": "before_after", "x-clip": "clip"}

    def render(self, body: str) -> str:
        def sub(m: re.Match) -> str:
            name, attr_s, inner = m.group(1), m.group(2), m.group(3)
            return getattr(self, self.TAGS[name])(attrs(attr_s), inner)

        out = re.sub(r"<(x-shot|x-demo|x-before-after|x-clip)\b([^>]*)>(.*?)</\1>", sub, body, flags=re.S)
        if "<x-" in out:
            raise SystemExit("閉じていないカスタムタグがあります: " + out[out.index("<x-"):][:80])
        return out


def ids_for_lang(body: str, lang: str, default: str) -> str:
    """id は表示中の言語にだけ置く（日英で同じアンカーを共有するため）。

    既定言語には実 id と data-id、ほかの言語は data-id だけを出し、言語を切り替えたときに
    JS が付け替える。こうすると URL のハッシュが言語に依らず同じまま使える。
    """
    if lang == default:
        return ID_RE.sub(lambda m: f'id="{m.group(1)}" data-id="{m.group(1)}"', body)
    return ID_RE.sub(lambda m: f'data-id="{m.group(1)}"', body)


def script_safe(s: str) -> str:
    """<script> の中に置く文字列から、要素を閉じてしまう並びを消す。"""
    return s.replace("</", "<\\/")


def build() -> dict[str, bytes]:
    """出力するファイル（出力フォルダからの相対パス -> 中身）。"""
    langs ={lang: load_language(lang) for lang in LANG_ORDER if any((SRC / lang).glob("*.html"))}
    if not langs:
        raise SystemExit("原稿がありません: docs/manual/src/<言語>/*.html")
    check(langs)
    default = next(iter(langs))
    demos = json.loads((SRC / "demos.json").read_text(encoding="utf-8"))
    version = json.loads((ROOT / "package.json").read_text(encoding="utf-8-sig"))["version"]

    ASSETS.clear()
    articles = []
    for lang, chs in langs.items():
        r = Renderer(demos, lang)
        secs = []
        for n, c in enumerate(chs, 1):
            body = ids_for_lang(r.render(c.body), lang, default)
            sid = (f'id="{c.cid}" data-id="{c.cid}"' if lang == default else f'data-id="{c.cid}"')
            # 章番号は目次と同じ 2 桁（検索で章が隠れても番号が詰まらないよう、CSS の counter にしない）
            secs.append(f'<section {sid} class="sec" data-sid="{c.cid}">'
                        f'<h2 data-num="{n:02d}">{esc(c.title)}</h2>\n{body}</section>')
        hidden = "" if lang == default else " hidden"
        articles.append(f'<article class="doc" data-lang="{lang}" lang="{lang}"{hidden}>\n'
                        + "\n".join(secs) + "\n</article>")

    data = {
        "langs": list(langs),
        "version": version,
        "nav": {lang: [{"id": c.cid, "title": c.title, "subs": [{"a": a, "t": t} for a, t in c.subs]}
                       for c in chs] for lang, chs in langs.items()},
        "index": {lang: [{"id": c.cid, "title": c.title, "text": c.text} for c in chs]
                  for lang, chs in langs.items()},
    }
    def text_file(s: str) -> bytes:
        return (s.strip().replace("\r\n", "\n") + "\n").encode("utf-8")

    css = text_file(font_css(font_urls()) + "\n\n" + (SRC / "manual.css").read_text(encoding="utf-8"))
    js = text_file((SRC / "manual.js").read_text(encoding="utf-8"))
    page = (SRC / "page.html").read_text(encoding="utf-8")
    parts = {
        "generator": esc(GENERATOR),
        "css": esc(versioned("manual.css", css)),
        "js": esc(versioned("manual.js", js)),
        "data": script_safe(json.dumps(data, ensure_ascii=False, separators=(",", ":"))),
        "articles": "\n".join(articles),
        "version": esc(version),
        "lang_hidden": "" if len(langs) > 1 else " hidden",
    }
    # 差し込む中身に {{…}} が含まれても再置換しないよう、テンプレートを 1 回だけ走査する
    page = re.sub(r"\{\{(\w+)\}\}", lambda m: parts[m.group(1)], page)
    return {"index.html": page.replace("\r\n", "\n").encode("utf-8"),
            "manual.css": css, "manual.js": js, **dict(sorted(ASSETS.items()))}


def write(files: dict[str, bytes], out: Path) -> None:
    """出力フォルダを作り直す（前の版の素材が残らないよう、中身を消してから書く）。"""
    out = out.resolve()
    src = SRC.resolve()
    if out == src or out in src.parents:
        raise SystemExit(f"原稿を含むフォルダには出力できません: {out}")
    if out.exists() and any(out.iterdir()):
        page = out / "index.html"
        if not (page.is_file() and GENERATOR in page.read_text(encoding="utf-8", errors="replace")):
            raise SystemExit(f"{out} はこのスクリプトの出力ではないので消せません"
                             "（空のフォルダか、前に出力したフォルダを指定してください）")
        shutil.rmtree(out)
    for rel, data in files.items():
        p = out / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="出力するフォルダ（中身は作り直す）")
    a = ap.parse_args()
    files = build()
    write(files, a.out)
    total = sum(len(d) for d in files.values()) / 1048576
    try:
        shown = a.out.resolve().relative_to(ROOT)
    except ValueError:
        shown = a.out
    print(f"done -> {shown} ({len(files)} files, {total:.2f} MB; index.html {len(files['index.html']) / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
