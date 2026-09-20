"""MANUAL.md から docs/manual/index.html（インタラクティブ版）を生成する。

原稿の正は **リポジトリ直下の MANUAL.md**。このスクリプトは原稿を書き換えず、
見出し構成をそのまま HTML へ移したうえで、スクリーンショットと
「パラメータを動かすと実 C# の出力が切り替わる」実演を所定の位置へ挿し込む。

    .venv\\Scripts\\python.exe docs/manual/build_manual.py

画像素材は build_assets.py が作る（img/ui/*.webp と img/demo/*.webp）。
"""
from __future__ import annotations

import html
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "MANUAL.md"
OUT = ROOT / "docs" / "manual" / "index.html"

# 日英で共通の安定 ID（言語を切り替えても同じ位置に留まるため）。原稿の ### の並び順。
SECTION_IDS = [
    "install", "basics", "zones", "processing", "preview",
    "masks", "ai", "presets", "export", "troubleshooting", "faq",
]

LANGS = ("ja", "en")
LANG_HEADING = {"ja": "日本語", "en": "English"}

# ── 挿し込み定義 ────────────────────────────────────────────────
# 原稿の見出し（#### か **太字** 行）の直後に入れる。キーは (セクションID, 見出しテキスト)。
# 日英で見出し語が違うので両方を列挙する。

SHOT = "shot"
DEMO = "demo"


def shot(name: str, caption_ja: str, caption_en: str, wide: bool = False) -> dict:
    return {"kind": SHOT, "src": f"img/ui/{name}.webp",
            "ja": caption_ja, "en": caption_en, "wide": wide}


def demo(demo_id: str) -> dict:
    return {"kind": DEMO, "id": demo_id}


W_MAIN = shot("window", "いろかウィンドウ。① から ④ へ上から順に進みます",
              "The Iroca window. Work top-down, from ① to ④.", wide=True)

INSERTS: dict[tuple[str, str], list[dict]] = {
    ("zones", "基本の項目（常に表示）"): [
        shot("zone-card", "ゾーン 1 枚ぶんの基本の項目",
             "The core controls of a single zone")],
    ("zones", "Core controls (always shown)"): [
        shot("zone-card", "ゾーン 1 枚ぶんの基本の項目",
             "The core controls of a single zone")],

    ("zones", "変更先カラー"): [demo("palette")],
    ("zones", "Target Color"): [demo("palette")],

    ("zones", "許容範囲"): [demo("tolerance")],
    ("zones", "Tolerance"): [demo("tolerance")],

    ("zones", "模様保持"): [demo("blend")],
    ("zones", "Pattern Preserve"): [demo("blend")],

    ("zones", "出力彩度"): [demo("saturation")],
    ("zones", "Output Saturation"): [demo("saturation")],

    ("zones", "詳細の項目（「詳細設定」を開くと表示）"): [
        shot("zone-detail", "詳細設定。上から処理の走る順に並んでいます",
             "Details, laid out in the order the processing runs")],
    ("zones", 'Detail controls (open "Details" to show them)'): [
        shot("zone-detail", "詳細設定。上から処理の走る順に並んでいます",
             "Details, laid out in the order the processing runs")],

    ("processing", "エッジぼかし"): [
        shot("processing", "加工設定と、その詳細設定",
             "Processing settings, with details expanded")],
    ("processing", "Edge Feather"): [
        shot("processing", "加工設定と、その詳細設定",
             "Processing settings, with details expanded")],

    ("preview", "表示モード"): [
        shot("preview-compare", "前後比較。変更前と変更後を左右に並べます",
             "Side-by-side: before on the left, after on the right"),
        shot("preview-diff", "差分表示。変わったピクセルだけが色で浮きます",
             "Diff view: only the changed pixels are highlighted")],
    ("preview", "View modes"): [
        shot("preview-compare", "前後比較。変更前と変更後を左右に並べます",
             "Side-by-side: before on the left, after on the right"),
        shot("preview-diff", "差分表示。変わったピクセルだけが色で浮きます",
             "Diff view: only the changed pixels are highlighted")],

    ("masks", "使い方"): [
        shot("mask-window", "マスク編集ウィンドウ。対象・種類・ツールをここで切り替えます",
             "The mask editing window: target, type and tool all live here")],
    ("masks", "How to use"): [
        shot("mask-window", "マスク編集ウィンドウ。対象・種類・ツールをここで切り替えます",
             "The mask editing window: target, type and tool all live here")],

    ("presets", "保存と読み込み"): [
        shot("presets", "プリセット。保存先はプロジェクト内とユーザー共通の 2 つ",
             "Presets. Two destinations: in-project and per-user")],
    ("presets", "Save and load"): [
        shot("presets", "プリセット。保存先はプロジェクト内とユーザー共通の 2 つ",
             "Presets. Two destinations: in-project and per-user")],
}

# 原稿の [スクリーンショット: …] プレースホルダーの置換先。None は「画像なし」（行を落とす）。
PLACEHOLDERS: dict[str, dict | None] = {
    "テクスチャ選択後のウィンドウ全体": W_MAIN,
    "エクスプローラと Unity Editor": None,
    "Import ダイアログ": None,
    "Tools メニュー": None,
    "Read/Write 警告と有効化ボタン": None,
    "除外マスクを描いた状態のプレビュー": shot(
        "mask-section", "メインウィンドウのマスク欄。いま編集中の対象が出ます",
        "The mask row in the main window shows the current edit target"),
}

# 実演デモの定義（画像は build_assets.py が実 C# で出力したもの）。
DEMOS = {
    "palette": {
        "ja": {"title": "変更先カラーを変える", "label": "変更先カラー",
               "note": "どれもスポイト 1 回 → 自動調整の結果。色ごとに設定を変えてはいません"},
        "en": {"title": "Change the target color", "label": "Target color",
               "note": "Each is one eyedropper click plus Auto-tune — no per-color tuning"},
        "control": "swatch",
        "frames": [
            {"v": "shu", "img": "color-shu", "ja": "朱", "en": "Shu", "hex": "#dc4b2e"},
            {"v": "akane", "img": "color-akane", "ja": "茜", "en": "Akane", "hex": "#a62a37"},
            {"v": "yamabuki", "img": "color-yamabuki", "ja": "山吹", "en": "Yamabuki", "hex": "#f2a33c"},
            {"v": "wakakusa", "img": "color-wakakusa", "ja": "若草", "en": "Wakakusa", "hex": "#6fbf5b"},
            {"v": "tokiwa", "img": "color-tokiwa", "ja": "常磐", "en": "Tokiwa", "hex": "#227657"},
            {"v": "seiji", "img": "color-seiji", "ja": "青磁", "en": "Seiji", "hex": "#6ebaa8"},
            {"v": "sumire", "img": "color-sumire", "ja": "菫", "en": "Sumire", "hex": "#7a4ab2"},
            {"v": "sakura", "img": "color-sakura", "ja": "桜", "en": "Sakura", "hex": "#eea9ba"},
        ],
        "start": 0,
    },
    "tolerance": {
        "ja": {"title": "許容範囲を動かす（自動調整を使わない場合）", "label": "許容範囲",
               "note": "ハイライトの芯が消えるまで上げると、先に隣の黒い素材が染まります。"
                       "自動調整はここを上げずに、ハイライトだけを拾います"},
        "en": {"title": "Move the tolerance (without Auto-tune)", "label": "Tolerance",
               "note": "Raise it until the highlight cores go, and the black material next door "
                       "gets tinted first. Auto-tune picks up the highlights without raising it"},
        "control": "range",
        "frames": [
            {"v": "0.05", "img": "tol-005", "ja": "芯が大きく残る", "en": "Large cores left"},
            {"v": "0.20", "img": "tol-020", "ja": "ハイライトの芯が残る（新規ゾーンの初期値）",
             "en": "Highlight cores left (new-zone default)"},
            {"v": "0.30", "img": "tol-030", "ja": "まだ芯が残る", "en": "Cores still left"},
            {"v": "0.45", "img": "tol-045", "ja": "芯は消えたが、黒いアッパーが染まった",
             "en": "Cores gone, but the black upper is tinted"},
            {"v": "0.60", "img": "tol-060", "ja": "はみ出す", "en": "Spills over"},
        ],
        "start": 1,
    },
    "blend": {
        "ja": {"title": "模様保持を動かす", "label": "模様保持",
               "note": "下げるほど陰影が浅くなり、ベタ塗りに近づきます（既定 1.0）"},
        "en": {"title": "Move Pattern Preserve", "label": "Pattern Preserve",
               "note": "The lower it goes, the shallower the shading — closer to a flat fill (default 1.0)"},
        "control": "range",
        "frames": [
            {"v": "0.50", "img": "blend-050", "ja": "陰影が浅い", "en": "Shallow shading"},
            {"v": "0.75", "img": "blend-075", "ja": "", "en": ""},
            {"v": "1.00", "img": "blend-100", "ja": "元の陰影のまま（既定）", "en": "Original shading (default)"},
        ],
        "start": 2,
    },
    "saturation": {
        "ja": {"title": "出力彩度を動かす", "label": "出力彩度",
               "note": "純色がベタに見えるときは下げると陰影が戻ります"},
        "en": {"title": "Move Output Saturation", "label": "Output Saturation",
               "note": "Lower it when a pure color looks flat — the shading comes back"},
        "control": "range",
        "frames": [
            {"v": "0.50", "img": "sat-050", "ja": "淡い", "en": "Muted"},
            {"v": "0.70", "img": "sat-070", "ja": "", "en": ""},
            {"v": "0.85", "img": "sat-085", "ja": "", "en": ""},
            {"v": "1.00", "img": "sat-100", "ja": "そのまま（既定）", "en": "As-is (default)"},
        ],
        "start": 3,
    },
}

# セクション冒頭に置く導入（原稿にはない、HTML 版だけの案内）。
INTRO = {
    "ja": {
        "basics": {"before_after": True},
    },
    "en": {
        "basics": {"before_after": True},
    },
}


# ── Markdown パース ────────────────────────────────────────────

@dataclass
class Heading:
    level: int           # 3 = ####, 4 = **太字**
    text: str
    anchor: str


@dataclass
class Section:
    sid: str
    title: str
    html: str
    headings: list[Heading] = field(default_factory=list)
    text: str = ""       # 検索インデックス用のプレーンテキスト


def id_attr(anchor: str, lang: str) -> str:
    """id は表示中の言語にだけ付ける（日英で同じアンカーを共有するため）。

    生成時は日本語側にだけ実 id を置き、英語側は data-id のみ。言語を切り替えたとき
    JS が付け替える。こうすると URL のハッシュが言語に依らず同じまま使える。
    """
    if lang == "ja":
        return f' id="{anchor}" data-id="{anchor}"'
    return f' data-id="{anchor}"'


def gh_slug(text: str) -> str:
    s = text.strip().lower()
    s = re.sub(r"[^\w\s\-ぁ-んァ-ヶ一-龥ー]", "", s)
    s = re.sub(r"\s+", "-", s)
    return s


def split_languages(md: str) -> dict[str, list[str]]:
    blocks: dict[str, list[str]] = {}
    cur: str | None = None
    for line in md.splitlines():
        m = re.match(r"^## (.+)$", line)
        if m:
            title = m.group(1).strip()
            cur = next((k for k, v in LANG_HEADING.items() if v == title), None)
            if cur:
                blocks[cur] = []
            continue
        if cur:
            blocks[cur].append(line)
    missing = [l for l in LANGS if l not in blocks]
    if missing:
        raise SystemExit(f"MANUAL.md に言語ブロックがありません: {missing}")
    return blocks


def split_sections(lines: list[str]) -> list[tuple[str, list[str]]]:
    out: list[tuple[str, list[str]]] = []
    cur_title: str | None = None
    buf: list[str] = []
    for line in lines:
        m = re.match(r"^### (.+)$", line)
        if m:
            if cur_title is not None:
                out.append((cur_title, buf))
            cur_title, buf = m.group(1).strip(), []
            continue
        if cur_title is not None:
            buf.append(line)
    if cur_title is not None:
        out.append((cur_title, buf))
    # 「目次 / Table of Contents」はサイドバーを自前で作るので捨てる
    return [(t, b) for t, b in out if t not in ("目次", "Table of Contents")]


def inline(text: str, anchors: dict[str, str]) -> str:
    """インライン Markdown → HTML。リンクの内部アンカーは安定 ID へ差し替える。"""
    # コードを先に退避（中の記号をエスケープ対象から外さないため、順序に注意）
    codes: list[str] = []

    def stash(m: re.Match) -> str:
        codes.append(m.group(1))
        return f"\x00{len(codes) - 1}\x00"

    text = re.sub(r"`([^`]+)`", stash, text)
    text = html.escape(text, quote=False)

    def link(m: re.Match) -> str:
        label, url = m.group(1), m.group(2)
        if url.startswith("#"):
            target = anchors.get(url[1:], None)
            return f'<a href="#{target}" class="xref">{label}</a>' if target else label
        return f'<a href="{url}" target="_blank" rel="noopener">{label}</a>'

    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", link, text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<!\*)\*([^*]+)\*(?!\*)", r"<em>\1</em>", text)
    for i, c in enumerate(codes):
        text = text.replace(f"\x00{i}\x00", f"<code>{html.escape(c, quote=False)}</code>")
    return text


def plain(text: str) -> str:
    text = re.sub(r"`([^`]+)`", r"\1", text)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
    return re.sub(r"\*{1,2}([^*]+)\*{1,2}", r"\1", text)


TABLE_SEP = re.compile(r"^\|[\s:|-]+\|$")


def render_table(rows: list[str], anchors: dict[str, str]) -> str:
    def cells(row: str) -> list[str]:
        return [c.strip() for c in row.strip().strip("|").split("|")]

    head, body = cells(rows[0]), [cells(r) for r in rows[2:]]
    th = "".join(f"<th>{inline(c, anchors)}</th>" for c in head)
    trs = "".join(
        "<tr>" + "".join(f"<td>{inline(c, anchors)}</td>" for c in r) + "</tr>"
        for r in body)
    return f'<div class="table-wrap"><table><thead><tr>{th}</tr></thead><tbody>{trs}</tbody></table></div>'


_dims: dict[str, tuple[int, int] | None] = {}


def dims_attr(rel: str) -> str:
    """画像の実寸を width/height 属性にする（遅延読み込みでのレイアウトずれ防止）。"""
    if rel not in _dims:
        try:
            from PIL import Image as _Im
            with _Im.open(OUT.parent / rel) as im:
                _dims[rel] = im.size
        except Exception:
            _dims[rel] = None
    wh = _dims[rel]
    return f' width="{wh[0]}" height="{wh[1]}"' if wh else ""


def render_figure(spec: dict, lang: str) -> str:
    cap = spec.get(lang) or ""
    cls = "figure wide" if spec.get("wide") else "figure"
    cap_html = f"<figcaption>{html.escape(cap)}</figcaption>" if cap else ""
    dims_attr(spec["src"])  # サイズをキャッシュへ
    wh = _dims.get(spec["src"])
    # 原板を引き伸ばさない（Unity のスクリーンショットは pixelsPerPoint 1.25 の実ピクセル）
    cap_w = f' style="max-width:{wh[0]}px"' if wh else ""
    return (f'<figure class="{cls}"{cap_w}>'
            f'<button class="shot" type="button" data-zoom="{spec["src"]}">'
            f'<img src="{spec["src"]}" alt="{html.escape(cap)}"{dims_attr(spec["src"])} loading="lazy">'
            f"</button>{cap_html}</figure>")


def render_demo(demo_id: str, lang: str) -> str:
    d = DEMOS[demo_id]
    t = d[lang]
    frames = d["frames"]
    def on(i: int) -> str:
        return " on" if i == d["start"] else ""

    # フレームは 1 枚の <img> を差し替える（全コマを DOM に並べるとデコード負荷で重くなる）。
    srcs = [f'img/demo/{f["img"]}.webp' for f in frames]
    srcs_full = [f'img/demo/{f["img"]}-full.webp' for f in frames] if d.get("toggle_full") else None
    start_src = srcs[d["start"]]
    wh = dims_attr(start_src)

    if d["control"] == "swatch":
        ctl = '<div class="swatches" role="radiogroup">' + "".join(
            f'<button type="button" class="sw{on(i)}" data-i="{i}"'
            f' style="--sw:{f["hex"]}" title="{html.escape(f[lang])}">'
            f'<span>{html.escape(f[lang])}</span></button>'
            for i, f in enumerate(frames)) + "</div>"
    else:
        ctl = (f'<div class="slider">'
               f'<span class="lab">{html.escape(t["label"])}</span>'
               f'<input type="range" min="0" max="{len(frames) - 1}" step="1"'
               f' value="{d["start"]}" aria-label="{html.escape(t["label"])}">'
               f'<output>{frames[d["start"]]["v"]}</output></div>')

    view_toggle = ""
    if d.get("toggle_full"):
        view_toggle = ('<div class="viewtog">'
                       '<button type="button" class="on" data-view="crop">'
                       + ("拡大" if lang == "ja" else "Close-up") + "</button>"
                       '<button type="button" data-view="full">'
                       + ("全体" if lang == "ja" else "Whole") + "</button></div>")

    tags = "".join(
        f'<span class="tag{on(i)}" data-i="{i}">{html.escape(f.get(lang, ""))}</span>'
        for i, f in enumerate(frames) if f.get(lang))

    def attr(obj) -> str:
        return html.escape(json.dumps(obj, ensure_ascii=False), quote=True)

    full_attr = f' data-frames-full="{attr(srcs_full)}"' if srcs_full else ""
    return (f'<figure class="demo" data-demo="{demo_id}"'
            f' data-values="{attr([f["v"] for f in frames])}"'
            f' data-frames="{attr(srcs)}"{full_attr} data-start="{d["start"]}">'
            f'<figcaption class="demo-head"><span class="demo-title">{html.escape(t["title"])}</span>'
            f'{view_toggle}</figcaption>'
            f'<div class="stage"><img class="fr on" src="{start_src}" alt=""{wh} loading="lazy"></div>'
            f"{ctl}"
            f'<p class="demo-note"><span class="frame-tag">{tags}</span>'
            f'{html.escape(t["note"])}</p>'
            f"</figure>")


def render_before_after(lang: str) -> str:
    left = "変更前" if lang == "ja" else "Before"
    right = "変更後" if lang == "ja" else "After"
    hint = ("つまんで左右に動かすと、変更前後を見比べられます"
            if lang == "ja" else "Drag the handle to compare before and after")
    return ('<figure class="ba">'
            '<div class="ba-stage">'
            '<img class="ba-after" src="img/demo/color-shu.webp" alt="">'
            '<img class="ba-before" src="img/demo/src.webp" alt="">'
            '<div class="ba-handle" role="slider" aria-valuemin="0" aria-valuemax="100"'
            ' aria-valuenow="50" tabindex="0"><span></span></div>'
            f'<span class="ba-tag left">{left}</span>'
            f'<span class="ba-tag right">{right}</span>'
            "</div>"
            f'<figcaption>{hint}</figcaption></figure>')


def render_section(sid: str, title: str, lines: list[str], lang: str,
                   anchors: dict[str, str]) -> Section:
    out: list[str] = []
    headings: list[Heading] = []
    texts: list[str] = [title]
    i, n = 0, len(lines)
    used_anchors: set[str] = set()

    def anchor_for(text: str) -> str:
        base = f"{sid}--{gh_slug(text)}" or f"{sid}--h"
        a, k = base, 2
        while a in used_anchors:
            a, k = f"{base}-{k}", k + 1
        used_anchors.add(a)
        return a

    # 挿し込みは見出しの直後ではなく、見出しに続く最初のブロック（説明文）の後に置く。
    # 「何の設定か」を読んでから実演を見る順にするため。
    pending: list[dict] = []

    def queue_inserts(heading_text: str) -> None:
        nonlocal pending
        pending = list(INSERTS.get((sid, heading_text), []))

    def flush_pending() -> None:
        nonlocal pending
        for spec in pending:
            if spec["kind"] == SHOT:
                out.append(render_figure(spec, lang))
            else:
                out.append(render_demo(spec["id"], lang))
        pending = []

    while i < n:
        line = lines[i]
        s = line.strip()

        if not s or s == "---":
            i += 1
            continue

        m = re.match(r"^#### (.+)$", s)
        if m:
            text = m.group(1).strip()
            a = anchor_for(text)
            headings.append(Heading(3, text, a))
            texts.append(text)
            flush_pending()
            out.append(f'<h3{id_attr(a, lang)}>{inline(text, anchors)}</h3>')
            i += 1
            queue_inserts(text)
            continue

        m = re.match(r"^\*\*([^*]+)\*\*$", s)
        if m:
            text = m.group(1).strip()
            a = anchor_for(text)
            headings.append(Heading(4, text, a))
            texts.append(text)
            cls = "qa" if re.match(r"^Q[:：]", text) else "term"
            flush_pending()
            out.append(f'<h4{id_attr(a, lang)} class="{cls}">{inline(text, anchors)}</h4>')
            i += 1
            queue_inserts(text)
            continue

        m = re.match(r"^\[スクリーンショット[:：]\s*(.+?)\]$", s)
        if m:
            spec = PLACEHOLDERS.get(m.group(1).strip(), None)
            if spec:
                out.append(render_figure(spec, lang))
            i += 1
            continue

        if s.startswith("> "):
            buf = []
            while i < n and lines[i].strip().startswith("> "):
                buf.append(lines[i].strip()[2:])
                i += 1
            texts.append(plain(" ".join(buf)))
            out.append(f'<blockquote>{inline(" ".join(buf), anchors)}</blockquote>')
            flush_pending()
            continue

        if s.startswith("|"):
            rows = []
            while i < n and lines[i].strip().startswith("|"):
                rows.append(lines[i].strip())
                i += 1
            if len(rows) >= 2 and TABLE_SEP.match(rows[1]):
                texts.extend(plain(r) for r in rows)
                out.append(render_table(rows, anchors))
                flush_pending()
            continue

        if re.match(r"^[-*] ", s) or re.match(r"^\d+\. ", s):
            i, block, plain_texts = read_list(lines, i, anchors, lang)
            out.append(block)
            texts.extend(plain_texts)
            flush_pending()
            continue

        # 段落（続く非空行をまとめる）
        buf = []
        while i < n:
            cur = lines[i].strip()
            if (not cur or cur.startswith(("#", "|", "- ", "> ", "---", "["))
                    or re.match(r"^\d+\. ", cur) or re.match(r"^\*\*[^*]+\*\*$", cur)):
                break
            buf.append(cur)
            i += 1
        if buf:
            para = " ".join(buf)
            texts.append(plain(para))
            out.append(f"<p>{inline(para, anchors)}</p>")
            flush_pending()

    flush_pending()

    # セクション冒頭の追加コンテンツ
    extra = INTRO.get(lang, {}).get(sid, {})
    if extra.get("before_after"):
        out.insert(0, render_before_after(lang))

    return Section(sid=sid, title=title, html="\n".join(out),
                   headings=headings, text=" ".join(texts))


def read_list(lines: list[str], i: int, anchors: dict[str, str],
              lang: str) -> tuple[int, str, list[str]]:
    """箇条書き / 番号リストを 1 ブロック読む（インデント継続行と空行行送りに対応）。"""
    n = len(lines)
    ordered = bool(re.match(r"^\d+\. ", lines[i].strip()))
    items: list[list[str]] = []      # 各項目の行（1 行目 + 継続行）
    sub: list[list[str] | None] = []  # ネストした箇条書き
    texts: list[str] = []

    while i < n:
        raw = lines[i]
        s = raw.strip()
        indent = len(raw) - len(raw.lstrip(" "))

        if not s:
            # 次に継続行かリスト項目が来るなら続行、でなければ終わり
            j = i + 1
            while j < n and not lines[j].strip():
                j += 1
            if j >= n:
                break
            nxt = lines[j]
            nxt_indent = len(nxt) - len(nxt.lstrip(" "))
            is_item = bool(re.match(r"^\d+\. ", nxt.strip()) or re.match(r"^[-*] ", nxt.strip()))
            if nxt_indent >= 2 or (is_item and nxt_indent == 0):
                i = j
                continue
            break

        m_item = re.match(r"^\d+\. (.+)$", s) if ordered else re.match(r"^[-*] (.+)$", s)
        if m_item and indent == 0:
            items.append([m_item.group(1)])
            sub.append(None)
            i += 1
            continue

        if indent >= 2 and items:
            inner = re.match(r"^[-*] (.+)$", s)
            if inner:
                if sub[-1] is None:
                    sub[-1] = []
                sub[-1].append(inner.group(1))
            else:
                items[-1].append(s)
            i += 1
            continue

        break

    tag = "ol" if ordered else "ul"
    parts = []
    for body, children in zip(items, sub):
        texts.extend(plain(b) for b in body)
        inner = f"<p>{inline(body[0], anchors)}</p>" if len(body) > 1 else inline(body[0], anchors)
        for cont in body[1:]:
            m = re.match(r"^\[スクリーンショット[:：]\s*(.+?)\]$", cont)
            if m:
                spec = PLACEHOLDERS.get(m.group(1).strip(), None)
                if spec:
                    inner += render_figure(spec, lang)
                continue
            inner += f"<p>{inline(cont, anchors)}</p>"
        if children:
            texts.extend(plain(c) for c in children)
            inner += "<ul>" + "".join(f"<li>{inline(c, anchors)}</li>" for c in children) + "</ul>"
        parts.append(f"<li>{inner}</li>")
    return i, f"<{tag}>" + "".join(parts) + f"</{tag}>", texts


# ── ページ生成 ─────────────────────────────────────────────────

def build() -> str:
    md = SRC.read_text(encoding="utf-8")
    langs = split_languages(md)

    parsed: dict[str, list[tuple[str, list[str]]]] = {}
    anchors: dict[str, str] = {}
    for lang in LANGS:
        secs = split_sections(langs[lang])
        if len(secs) != len(SECTION_IDS):
            raise SystemExit(
                f"{lang}: ### セクション数 {len(secs)} が想定 {len(SECTION_IDS)} と違います\n"
                + "\n".join(f"  - {t}" for t, _ in secs))
        parsed[lang] = secs
        for sid, (title, _) in zip(SECTION_IDS, secs):
            anchors[gh_slug(title)] = sid

    sections: dict[str, list[Section]] = {}
    for lang in LANGS:
        sections[lang] = [render_section(sid, title, body, lang, anchors)
                          for sid, (title, body) in zip(SECTION_IDS, parsed[lang])]

    nav = {lang: [{"id": s.sid, "title": s.title,
                   "subs": [{"a": h.anchor, "t": h.text} for h in s.headings if h.level == 3]}
                  for s in sections[lang]] for lang in LANGS}
    index = {lang: [{"id": s.sid, "title": s.title, "text": s.text} for s in sections[lang]]
             for lang in LANGS}

    articles = []
    for lang in LANGS:
        body = "\n".join(
            f'<section{id_attr(s.sid, lang)} class="sec" data-sid="{s.sid}">'
            f'<h2>{html.escape(s.title)}</h2>\n{s.html}\n</section>'
            for s in sections[lang])
        articles.append(f'<article class="doc" data-lang="{lang}" lang="{lang}">{body}</article>')

    data = json.dumps({"nav": nav, "index": index}, ensure_ascii=False, separators=(",", ":"))

    return TEMPLATE.format(data=data, articles="\n".join(articles))


TEMPLATE = """<!DOCTYPE html>
<html lang="ja" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>いろか マニュアル</title>
<meta name="description" content="VRChat アバター用テクスチャを色替えする Unity Editor 拡張「いろか」のユーザーマニュアル。">
<link rel="icon" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><circle cx='16' cy='16' r='13' fill='%23dc4b2e'/></svg>">
<link rel="stylesheet" href="assets/manual.css">
</head>
<body>
<a class="skip" href="#main">本文へスキップ</a>
<header class="top">
  <div class="brand">
    <span class="mark" aria-hidden="true"></span>
    <span class="name">いろか</span>
    <span class="sub" data-t="manual">マニュアル</span>
  </div>
  <div class="tools">
    <div class="search">
      <input id="q" type="search" autocomplete="off" spellcheck="false" placeholder="検索">
      <button id="q-clear" type="button" aria-label="クリア" hidden>&times;</button>
    </div>
    <button id="lang" type="button" class="pill" aria-label="Switch language">EN</button>
    <button id="theme" type="button" class="pill icon" aria-label="Toggle theme"></button>
    <button id="menu" type="button" class="pill icon only-narrow" aria-label="Menu"></button>
  </div>
</header>
<div class="layout">
  <nav id="toc" class="toc" aria-label="目次"></nav>
  <main id="main">
    <div id="no-hit" class="no-hit" hidden></div>
{articles}
    <footer class="foot">
      <p class="foot-note" data-t="footNote"></p>
      <p class="foot-links">
        <a href="https://github.com/yukkuri-aoba/Iroca" target="_blank" rel="noopener">GitHub</a>
        <a href="https://github.com/yukkuri-aoba/Iroca/releases" target="_blank" rel="noopener" data-t="releases">Releases</a>
        <a href="https://github.com/yukkuri-aoba/Iroca/blob/main/CHANGELOG.md" target="_blank" rel="noopener" data-t="changelog">変更履歴</a>
      </p>
    </footer>
  </main>
</div>
<div id="zoom" class="zoom" hidden><img alt=""></div>
<script id="manual-data" type="application/json">{data}</script>
<script src="assets/manual.js"></script>
</body>
</html>
"""


def main() -> None:
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(build(), encoding="utf-8")
    kb = OUT.stat().st_size / 1024
    print(f"done -> {OUT.relative_to(ROOT)} ({kb:.0f} KB)")


if __name__ == "__main__":
    main()
