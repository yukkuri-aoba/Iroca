"""中断中(ドメインのアンロード)かを見るのは、ヒープの外のメモリの持ち主だけ(docs/architecture.md の 4 章)。

Mono のドメインのアンロードは残っているスレッドを中断する。並列で写している途中に呼び出し元のスレッドだけが
中断されると、ほかのスレッドがまだその領域を読み書きしているので、そのときは解放しない(アンロードの後の
ファイナライザに任せる)。この判定(`IrocaNativeMemory.CurrentThreadAborting`)は、解放する側の
`NativeBuffer`(最後の Release)と作業配列のプールの `Return`(貸し出し中のまま残す)が持つ。借りた側
(WorkScratch・finally で返す所)は見ずに返す。借りた側が自分で見ると、同じ判定が散らばり、見落とした所だけ
中断中に返してしまう(2026-11-10 まで Histogram・WorkScratch・AI 提案の後処理の 4 か所が自分で見ていた)。

コメントと文字列は除いてから見る。C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"

# (ファイル, 判定を置いてよいメソッド。None ならファイルのどこでもよい) → 回数。
ALLOWED = {
    "Code/Core/IrocaNativeMemory.cs": (None, 1),       # 定義
    "Code/Core/NativeBuffer.cs": (None, 1),            # 最後の Release は中断中なら解放しない
    "Code/Core/NativeWorkPool.cs": ("public void Return(T* p)", 1),   # 中断中は貸し出し中のまま残す
}

# 行コメント / ブロックコメント / 文字列リテラル(test_core_no_editor_dependency.py と同じ)。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)
_ABORTING = re.compile(r"\bCurrentThreadAborting\b")


def _strip_comments(src: str) -> str:
    """コメントと文字列リテラルを空白にする。改行は残して行番号を保つ。"""
    return _COMMENT.sub(lambda m: "\n" * m.group().count("\n") or " ", src)


def _body_span(code: str, signature: str) -> tuple[int, int] | None:
    """signature の後の最初の { から、対応する } までの位置。"""
    start = code.find(signature)
    if start < 0:
        return None
    open_ = code.find("{", start)
    depth = 0
    for i in range(open_, len(code)):
        if code[i] == "{":
            depth += 1
        elif code[i] == "}":
            depth -= 1
            if depth == 0:
                return open_, i
    return None


def _uses(code: str) -> list[int]:
    return [m.start() for m in _ABORTING.finditer(code)]


def _line(code: str, pos: int) -> int:
    return code.count("\n", 0, pos) + 1


def test_detector_ignores_comments_and_finds_method_body():
    """検出器が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    src = (
        "class P {\n"
        "    // CurrentThreadAborting はコメント\n"
        "    public void Return(T* p) {\n"
        "        if (IrocaNativeMemory.CurrentThreadAborting) { return; }\n"
        "    }\n"
        "    void Other() { var s = \"CurrentThreadAborting\"; if (IrocaNativeMemory.CurrentThreadAborting) { } }\n"
        "}\n"
    )
    code = _strip_comments(src)
    uses = _uses(code)
    assert [_line(code, p) for p in uses] == [4, 6]
    a, b = _body_span(code, "public void Return(T* p)")
    assert a < uses[0] < b and not (a < uses[1] < b)


def test_only_memory_owners_check_aborting():
    found = {}
    for path in sorted(CODE_DIR.rglob("*.cs")):
        code = _strip_comments(path.read_text(encoding="utf-8-sig"))
        uses = _uses(code)
        if uses:
            found[path.relative_to(REPO_ROOT).as_posix()] = (code, uses)
    # 回帰の前提。定義が見つからなければ、検出が壊れている。
    assert "Code/Core/IrocaNativeMemory.cs" in found, "CurrentThreadAborting の定義が見つかりません(検出の退行?)"
    bad = []
    for rel, (code, uses) in found.items():
        if rel not in ALLOWED:
            bad += [f"{rel}:{_line(code, p)}" for p in uses]
            continue
        method, count = ALLOWED[rel]
        if method is not None:
            span = _body_span(code, method)
            assert span, f"{rel} に {method} が見つかりません(名前を変えたなら ALLOWED も直す)"
            bad += [f"{rel}:{_line(code, p)}({method} の外)" for p in uses if not span[0] < p < span[1]]
        if len(uses) != count:
            bad.append(f"{rel}: {len(uses)} 回(決まりは {count} 回)")
    assert not bad, (
        "中断中(CurrentThreadAborting)を、ヒープの外のメモリの持ち主以外が見ています:\n  " + "\n  ".join(bad) + "\n"
        "→ 借りた側は見ずに返す(プールの Return が見て、中断中は貸し出し中のまま残す。docs/architecture.md の 4 章)。\n"
        "  持ち主を足したなら ALLOWED に理由つきで載せる。")
    missing = sorted(set(ALLOWED) - set(found))
    assert not missing, f"ALLOWED のファイルに判定がありません(移した・消したなら ALLOWED も直す): {missing}"
