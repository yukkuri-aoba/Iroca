"""許容範囲を手で変える所が、自動調整の入れた照合の上限距離（ColorZone.matchCutoff）を外すことを機械検査する。

照合の上限距離は、自動調整の許容範囲の切り詰め（グレーモードの照合）が入れる、画面に出さない項目。利用者が許容範囲を
手で動かしたのに上限が残ると、許容範囲を上げても上限より先が選ばれず、理由が画面に見えない（ユーザー決定: 手で動かしたら
上限を外す）。そこで、自動調整の適用（Core の ZoneAutoTuner.TuneResult.ApplyTo）のほかに許容範囲を書く所は
`ColorZone.SetToleranceManually` を通す。ここでは次を見る:

  1. Core の外（Code/ の UI・自動化・Application・Infra・統合）に、ゾーンの許容範囲へ直接代入する所（`.tolerance =`）が
     無い。比べるための写しなど、利用者のゾーンを書かない所は理由つきで許す。
  2. SetToleranceManually が上限を外している（関数を空にしたら落ちる）。
  3. ウィンドウの許容範囲のスライダーが SetToleranceManually を通している。

新しいゾーンの初期化子（`new ColorZone { tolerance = … }`）は、まだ上限を持たないゾーンなので見ない（`.` の付かない代入）。
C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"
CORE_DIR = CODE_DIR / "Core"
COLORZONE_CS = CORE_DIR / "ColorZone.cs"
ZONE_LIST_CS = CODE_DIR / "UI" / "IrocaWindow.ZoneList.cs"

# `.tolerance =`（代入）・`ref x.tolerance`（書き換えに渡す）。比較の `==` は除く。
_WRITE = re.compile(r"\.tolerance\s*=(?!=)|\bref\s+[\w.]+\.tolerance\b")

# 直接書いてよい所（Code/ からの相対パス → 理由）。理由の無い追加はしない。
ALLOWED = {
    "Application/AutoTuneService.cs":
        "UserOverwrittenSettings が、比べるためだけに作った写し(probe)の許容範囲を既定へ均す。利用者のゾーンは書かない",
}


def _code_lines(path: Path):
    """コメントを除いた行（行番号つき）。行末の // 以降を落とす。"""
    for no, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        code = line.split("//", 1)[0]
        if code.strip():
            yield no, code


def _outside_core():
    for path in sorted(CODE_DIR.rglob("*.cs")):
        if CORE_DIR in path.parents:
            continue
        yield path


def test_tolerance_is_not_written_directly_outside_core():
    hits = []
    seen_allowed = set()
    for path in _outside_core():
        rel = path.relative_to(CODE_DIR).as_posix()
        for no, code in _code_lines(path):
            if not _WRITE.search(code):
                continue
            if rel in ALLOWED:
                seen_allowed.add(rel)
                continue
            hits.append(f"Code/{rel}:{no}: {code.strip()}")
    assert not hits, (
        "Core の外でゾーンの許容範囲を直接書いている。手で変える所は ColorZone.SetToleranceManually を通す"
        "（自動調整の入れた照合の上限距離を外すため）。利用者のゾーンを書かない所なら ALLOWED に理由つきで載せる:\n  "
        + "\n  ".join(hits))
    stale = sorted(set(ALLOWED) - seen_allowed)
    assert not stale, f"ALLOWED の所にもう直接の書き込みが無い（一覧から外す）: {stale}"


def test_set_tolerance_manually_clears_the_cutoff():
    src = "\n".join(code for _, code in _code_lines(COLORZONE_CS))
    m = re.search(r"public\s+void\s+SetToleranceManually\s*\(\s*float\s+(\w+)\s*\)\s*\{", src)
    assert m, "ColorZone.SetToleranceManually(float) が見つからない"
    i, depth = m.end(), 1
    while depth:
        depth += (src[i] == "{") - (src[i] == "}")
        i += 1
    body = src[m.end():i - 1]
    assert re.search(r"\bmatchCutoff\s*=\s*0f\s*;", body), "SetToleranceManually が照合の上限距離を外していない"
    assert re.search(rf"\btolerance\s*=\s*{re.escape(m.group(1))}\s*;", body), "SetToleranceManually が許容範囲を書いていない"


def test_window_slider_goes_through_set_tolerance_manually():
    src = "\n".join(code for _, code in _code_lines(ZONE_LIST_CS))
    assert re.search(r"\.SetToleranceManually\s*\(\s*UndoHelper\.Slider\s*\(", src), (
        "ウィンドウの許容範囲のスライダーが ColorZone.SetToleranceManually を通していない")
