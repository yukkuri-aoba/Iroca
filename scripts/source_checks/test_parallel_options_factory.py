"""並列のループの設定(`ParallelOptions`)を作る所が `CoreParallelism.Options` の 1 つだけであることを検査する。

並列度は「メインスレッドと描画のぶんのコアをあける」規則(`CoreParallelism.Max`)で決める。設定を各所で `new` すると、
規則を変えるとき(本数の決め方・デバッグ時のスレッド数の指定・全体での頭打ち)に直す所が数十に散る。2026-11-13 に、
47 か所の `new ParallelOptions { ... }` を `CoreParallelism.Options(...)` へ置き換えた。

見るもの:
  1. `Code/` の中で `new ParallelOptions` を書いてよいのは `CoreParallelism.cs` だけ。
  2. 設定を渡さない `Parallel.For(from, to, body)`(並列度の上限なし)を書かない。

テストとハーネス(`scripts/`)は対象外。C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE = REPO_ROOT / "Code"
FACTORY = CODE / "Core" / "Foundation" / "CoreParallelism.cs"

# 行コメント / ブロックコメント / 文字列リテラルを落とす。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)
_NEW = re.compile(r"\bnew\s+(?:System\.Threading\.Tasks\.)?ParallelOptions\b")
_CALL = re.compile(r"\bParallel\.(?:For|ForEach)\s*(?:<[^>()]*>)?\s*\(")


def _sources():
    for path in sorted(CODE.rglob("*.cs")):
        yield path, _COMMENT.sub(" ", path.read_text(encoding="utf-8-sig"))


def _args(src: str, open_idx: int) -> list[str]:
    """src[open_idx] == '(' から、対応する ')' までの引数を、いちばん外側のカンマで分ける。"""
    depth, start, out = 0, open_idx + 1, []
    for i in range(open_idx, len(src)):
        ch = src[i]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
            if depth == 0:
                out.append(src[start:i])
                return out
        elif ch == "," and depth == 1:
            out.append(src[start:i])
            start = i + 1
    raise AssertionError("対応する ')' が見つかりません")


def test_parallel_options_are_made_only_by_the_factory():
    hits = []
    for path, src in _sources():
        if path == FACTORY:
            continue
        for m in _NEW.finditer(src):
            line = src.count("\n", 0, m.start()) + 1
            hits.append(f"{path.relative_to(REPO_ROOT).as_posix()}:{line}")
    assert not hits, (
        "`new ParallelOptions` を CoreParallelism.cs の外に書いています:\n  " + "\n  ".join(hits)
        + "\n→ CoreParallelism.Options(ct) で作ってください(分け方で本数が決まる所は Options(ct, degree))。")
    assert _NEW.search(_COMMENT.sub(" ", FACTORY.read_text(encoding="utf-8-sig"))), (
        "CoreParallelism.cs に作り口(new ParallelOptions)が見つかりません。置き場所を変えたなら、この検査も直す")


def test_parallel_loops_always_pass_options():
    """`Parallel.For` / `ForEach` は、設定(3 つ目か 2 つ目の引数)を必ず渡す。"""
    hits, seen = [], 0
    for path, src in _sources():
        for m in _CALL.finditer(src):
            seen += 1
            args = [a.strip() for a in _args(src, m.end() - 1)]
            # For(from, to, options, body…) は 4 つ以上、ForEach(source, options, body…) は 3 つ以上。
            is_for = "ForEach" not in m.group(0)
            if len(args) < (4 if is_for else 3):
                line = src.count("\n", 0, m.start()) + 1
                hits.append(f"{path.relative_to(REPO_ROOT).as_posix()}:{line}")
    assert seen >= 100, f"Parallel の呼び出しを拾えていません({seen} 件)"
    assert not hits, (
        "設定を渡していない Parallel の呼び出しがあります(並列度の上限が無くなる):\n  " + "\n  ".join(hits)
        + "\n→ CoreParallelism.Options(ct) を渡してください。")
