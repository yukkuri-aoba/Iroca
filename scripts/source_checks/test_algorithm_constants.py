"""アルゴリズムの定数を台帳（docs/algorithm-constants.md）と突き合わせる。

再着色・選択・自動調整・AI 提案の後処理には、名前の付いた数値定数が 300 近くある。多くは手元の被写体で
試して選んだ値で、足すのは簡単だが、どれが何の根拠で入ったかが散らばると、減らすことも、新しい被写体で
外れたときに疑う先を絞ることもできない（2026-10-24 の外部レビュー「閾値の堆積」）。

そこで台帳を正にし、次の食い違いで落とす:
  - 台帳に無い定数がある（新しく足した）  → 種類と根拠を台帳に書く。
  - 台帳の値とソースの値が違う（値を変えた）→ 台帳の値と根拠（測り直した記録）を更新する。
  - 台帳にあってソースに無い（消した・改名した）→ 台帳から消す。
  - 名前の無い小数のリテラルの数が、ファイルごとの記録と違う → 増えたなら名前を付けて台帳へ、
    減ったなら台帳の数を下げる（増やさないための歯止め）。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import fnmatch
import re
from collections import Counter
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
LEDGER = REPO_ROOT / "docs" / "algorithm-constants.md"

# 台帳の対象。出力（選択・再着色・自動調整・AI 提案のマスク）に効くファイルだけ。UI の寸法・メモリ管理・
# 保存形式・利用者が変える設定の既定値（RecolorSettings / ZonesJsonDefaults / ColorZone のフィールド）は外す。
SCOPE_GLOBS = [
    "Code/Core/ColorZone*.cs",
    "Code/Core/PixelProcessor*.cs",
    "Code/Core/ZoneAutoTuner*.cs",
    "Code/Core/HighlightSampleCorrector.cs",
    "Code/Core/UvChartMap.cs",
    "Code/MaskSuggest/Ops/*.cs",
]

KINDS = {"定義", "量子化", "計算", "近似", "設計", "実測", "不明"}
# 選択の段（dev_safe の切除計測で 1 つずつ外して測った単位）。台帳の「段」の欄に使う記号。
STAGES = {
    "BF", "SF", "MS", "DG", "CG", "QS", "CC", "EN", "HP", "HB", "EH", "FF", "CR", "SG", "PS", "PH", "DT",
    "FH", "BE", "RN", "SA", "EV", "NM", "AS", "FC", "HE", "EG", "XS", "TB", "CV", "—",
}

_NUM_TYPES = r"(?:float|int|double|long|byte|uint|ulong|short)"
# コメント（/// を含む）・文字列・文字リテラルを空白に置き換える（改行は残して行番号を保つ）。
_STRIP = re.compile(r"//[^\n]*|/\*.*?\*/|@\"(?:\"\"|[^\"])*\"|\$?\"(?:\\.|[^\"\\\n])*\"|'(?:\\.|[^'\\\n])'",
                    re.DOTALL)
_DECL = re.compile(
    r"\b(?:const|static\s+readonly)\s+" + _NUM_TYPES + r"(?:\[\])?\s+([A-Za-z_]\w*\s*=[^;]*);", re.DOTALL)
_FLOAT_LIT = re.compile(r"(?<![\w.])(\d+\.\d+(?:[eE][-+]?\d+)?[fFdDmM]?|\d+[eE][-+]?\d+[fFdDmM]?|\d+[fF])(?![\w.])")
# 名前を付けなくてよい小数: 0・1・2・3・4・0.5・255・256 と、ゼロ割り防止などの ε（1e-4 以下）。
_TRIVIAL = {0.0, 1.0, 2.0, 3.0, 4.0, 0.5, 255.0, 256.0}


def _blank(m: re.Match) -> str:
    return re.sub(r"[^\n]", " ", m.group(0))


def strip_comments(src: str) -> str:
    return _STRIP.sub(_blank, src)


def scope_files() -> list[Path]:
    out: set[Path] = set()
    for g in SCOPE_GLOBS:
        out.update(REPO_ROOT.glob(g))
    return sorted(out)


def _rel(p: Path) -> str:
    return p.relative_to(REPO_ROOT).as_posix()


def _split_top(text: str, sep: str = ",") -> list[str]:
    """括弧・波括弧の外の sep で分ける（宣言子の並び `A = 1, B = Max(2, 3)` 用）。"""
    parts, depth, cur = [], 0, []
    for ch in text:
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == sep and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return parts


def _norm(expr: str) -> str:
    return " ".join(expr.split())


def source_constants() -> dict[tuple[str, str], list[str]]:
    """(ファイル, 定数名) → 初期化式（空白を詰めたもの）の並び。

    メソッドの中の局所定数（`const float matchThr = ...;`）も数える。同じ名前の局所定数が 1 つのファイルに
    いくつもあることがあるので、値は並びで持ち、台帳とは値の多重集合で突き合わせる。
    """
    out: dict[tuple[str, str], list[str]] = {}
    for p in scope_files():
        code = strip_comments(p.read_text(encoding="utf-8-sig"))
        for m in _DECL.finditer(code):
            for part in _split_top(m.group(1)):
                name, _, expr = part.partition("=")
                out.setdefault((_rel(p), name.strip()), []).append(_norm(expr))
    return out


def is_trivial_literal(tok: str) -> bool:
    v = float(tok.rstrip("fFdDmM"))
    return v in _TRIVIAL or abs(v) <= 1e-4


def inline_literals(p: Path) -> list[tuple[int, str]]:
    """定数の宣言の外にある、名前の無い小数のリテラル（自明なものを除く）を (行, 字句) で返す。"""
    code = strip_comments(p.read_text(encoding="utf-8-sig"))
    code = _DECL.sub(_blank, code)
    out = []
    for m in _FLOAT_LIT.finditer(code):
        if not is_trivial_literal(m.group(1)):
            out.append((code.count("\n", 0, m.start()) + 1, m.group(1)))
    return out


# ───────────────────────────── 台帳の読み取り ─────────────────────────────

def _cells(line: str) -> list[str]:
    body = line.strip()[1:-1]
    return [c.strip().replace("\\|", "|") for c in re.split(r"(?<!\\)\|", body)]


def _unquote(cell: str) -> str:
    return cell[1:-1] if len(cell) >= 2 and cell[0] == cell[-1] == "`" else cell


def parse_ledger() -> tuple[dict[str, list[dict]], dict[str, int]]:
    """({ファイル: [行]}, {ファイル: 名前の無い小数の数})。同じ名前の行が複数あってよい（局所定数）。"""
    rows: dict[str, list[dict]] = {}
    counts: dict[str, int] = {}
    section = None
    for ln in LEDGER.read_text(encoding="utf-8").splitlines():
        if ln.startswith("## "):
            title = ln[3:].strip().strip("`")
            section = title if title.startswith("Code/") else ("@counts" if "名前の無い小数" in title else None)
            continue
        if section is None or not ln.startswith("|") or set(ln.replace("|", "").strip()) <= set("-: "):
            continue
        cells = _cells(ln)
        if section == "@counts":
            if cells[0] in ("ファイル",):
                continue
            counts[_unquote(cells[0])] = int(cells[1])
            continue
        if cells[0] == "定数":
            continue
        assert len(cells) == 5, f"{section}: 列の数が 5 ではありません: {ln}"
        name, value, kind, stage, basis = cells
        rows.setdefault(section, []).append({
            "name": _unquote(name), "value": _norm(_unquote(value)), "kind": kind, "stage": stage, "basis": basis})
    return rows, counts


def _exact(rows: dict[str, list[dict]], file: str, name: str) -> list[dict]:
    return [r for r in rows.get(file, []) if r["name"] == name]


def _globbed(rows: dict[str, list[dict]], file: str, name: str) -> dict | None:
    for r in rows.get(file, []):
        if "*" in r["name"] and fnmatch.fnmatchcase(name, r["name"]):
            return r
    return None


@pytest.fixture(scope="module")
def ledger():
    assert LEDGER.exists(), f"台帳がありません: {LEDGER}"
    return parse_ledger()


@pytest.fixture(scope="module")
def constants():
    c = source_constants()
    assert sum(len(v) for v in c.values()) > 100, f"定数の抽出が少なすぎます（{len(c)} 個。パーサの退行?）"
    return c


def test_every_constant_is_in_ledger(ledger, constants):
    rows, _ = ledger
    missing = []
    for (f, n), vals in constants.items():
        if _exact(rows, f, n) or _globbed(rows, f, n):
            continue
        missing += [f"{f}: {n} = {v}" for v in vals]
    assert not missing, (
        "台帳（docs/algorithm-constants.md）に無い定数があります:\n  " + "\n  ".join(missing) + "\n"
        "→ そのファイルの節に行を足してください（種類・段・根拠）。手元の被写体で試して選んだ値なら種類は「実測」で、\n"
        "  根拠に何で測ったかを書きます（被写体名は書かない）。足す前に、既存の定数・統計量から導けないかを考えてください。")


def test_ledger_values_match_source(ledger, constants):
    rows, _ = ledger
    wrong = []
    for (f, n), vals in constants.items():
        ex = _exact(rows, f, n)
        if ex and Counter(r["value"] for r in ex) != Counter(vals):
            wrong.append(f"{f}: {n}  台帳 {sorted(r['value'] for r in ex)}  ソース {sorted(vals)}")
    # まとめ行（計測の段の番号など）は値の代わりに「N 個」と書き、当たる定数の数を突き合わせる。
    # 同じ接頭辞の定数を後から足したときに、黙ってまとめ行に吸い込まれないようにするため。
    for f, sec in rows.items():
        for r in sec:
            if "*" not in r["name"]:
                continue
            got = sum(len(v) for (ff, n), v in constants.items()
                      if ff == f and not _exact(rows, f, n) and fnmatch.fnmatchcase(n, r["name"]))
            m = re.fullmatch(r"(\d+) 個", r["value"])
            if not m or int(m.group(1)) != got:
                wrong.append(f"{f}: {r['name']}  台帳 {r['value']!r}  ソース {got} 個（まとめ行の値は「N 個」）")
    assert not wrong, (
        "台帳と値が違う定数があります:\n  " + "\n  ".join(wrong) + "\n"
        "→ 値を変えたなら台帳の値と根拠（測り直した記録）も直してください。")


def test_ledger_has_no_stale_rows(ledger, constants):
    rows, _ = ledger
    stale = []
    for f, sec in rows.items():
        names = [n for (ff, n) in constants if ff == f]
        for r in sec:
            if not any(fnmatch.fnmatchcase(n, r["name"]) for n in names):
                stale.append(f"{f}: {r['name']}")
    assert not stale, (
        "台帳にあってソースに無い定数があります（消した・改名した・ファイルを移した）:\n  " + "\n  ".join(stale))


def test_ledger_kinds_and_stages(ledger):
    rows, _ = ledger
    bad = []
    for f, sec in rows.items():
        for r in sec:
            n = r["name"]
            if r["kind"] not in KINDS:
                bad.append(f"{f}: {n} の種類 {r['kind']!r}（{sorted(KINDS)} のどれか）")
            if r["stage"] not in STAGES:
                bad.append(f"{f}: {n} の段 {r['stage']!r}")
            if not r["basis"]:
                bad.append(f"{f}: {n} の根拠が空")
            if "*" in n and r["kind"] not in ("計算", "定義"):
                bad.append(f"{f}: {n} はまとめ行（*）なので種類は 計算 か 定義 だけ（値を突き合わせないため）")
    assert not bad, "台帳の書き方の誤り:\n  " + "\n  ".join(bad)


def test_unnamed_float_literals_do_not_grow(ledger):
    _, counts = ledger
    diffs = []
    for p in scope_files():
        f = _rel(p)
        lits = inline_literals(p)
        rec = counts.get(f)
        if rec is None:
            diffs.append(f"{f}: 台帳の「名前の無い小数」の表に行がありません（いま {len(lits)} 個）")
        elif len(lits) > rec:
            diffs.append(f"{f}: {rec} → {len(lits)} 個に増えました（例: "
                         + ", ".join(f"{ln} 行 {t}" for ln, t in lits[-5:]) + "）。"
                         "調整した値なら名前を付けて台帳へ載せてください")
        elif len(lits) < rec:
            diffs.append(f"{f}: {rec} → {len(lits)} 個に減りました。台帳の数を {len(lits)} に下げてください")
    stale = sorted(set(counts) - {_rel(p) for p in scope_files()})
    diffs += [f"{f}: 台帳の「名前の無い小数」の表にあるが対象のファイルではありません" for f in stale]
    assert not diffs, "名前の無い小数のリテラルの数が台帳と違います:\n  " + "\n  ".join(diffs)
