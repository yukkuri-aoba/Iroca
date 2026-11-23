"""変更したファイルから、回すべきテストを選んで 1 行ずつ出す。

    python tools/test_select.py                  # 作業ツリー＋index と HEAD の差（本体と dev_safe の両方）
    python tools/test_select.py --index          # ステージしたものだけ（コミット前の確認）
    python tools/test_select.py --rev A..B       # 本体のコミット範囲（dev_safe は --dev-rev A..B）
    python tools/test_select.py --files Code/UI/X.cs dev_safe/scripts/y.py   # 一覧を直に渡す（確かめ用）
    python tools/test_select.py --run            # 出した順に実行する（最初の失敗で止める）

範囲の判定は自前の一覧を持たない。ハーネスがコンパイルする製品ソース・測定器は tools/review_state.py
（in_main_scope / in_dev_safe_scope。中で tools/harness_scope.py が Harness.csproj を読む）に聞く。
視覚レビューの pre-commit フックが「範囲のファイルがステージされたら承認を求める」ので、範囲のファイルが
あれば必ず回帰と視覚レビューの compare を出す（フックが要求するものを省かない）。

選び方（変更の種類 → 回すもの）:
  ハーネス範囲の製品ソース・測定器 → golden・source_checks・unit-run、回帰、視覚レビューの compare
  範囲外の Code/（UI・Infra・Automation など）→ build-check の型チェック、source_checks・golden・unit-run、
                                              EditMode（型名の参照から、関係するテストのクラスへ絞る）
  EditMode のテスト自身                       → テストのコンパイル、EditMode（そのクラスだけ）
  docs だけ                                   → source_checks（定数の台帳などの検査）。マニュアルは組み立ても
  dev_safe の測定器・基準・回帰テスト         → 回帰（テストだけの変更なら、そのファイルだけ）。測定器なら視覚レビューも
出力は `python tools/remote_test.py -- …` の形（テスト用 PC があればそちらで回る）。
"""
from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEV_SAFE = ROOT / "dev_safe"
sys.path.insert(0, str(ROOT / "tools"))
import review_state as rs  # noqa: E402

RT = ["python", "tools/remote_test.py", "--"]
SELF_TESTS = ["scripts/golden", "scripts/source_checks", "scripts/unit-run"]
EDITOR_TESTS_DIR = "scripts/editor-tests"
# EditMode のクラスをこの数より多く挙げることになったら、絞りは意味が薄いので絞らない
MAX_FILTER_CLASSES = 12


@dataclass
class Step:
    key: str                      # 重複の判定と並びの鍵
    cmd: list[str]
    reasons: list[str] = field(default_factory=list)
    note: str = ""


def norm(p: str) -> str:
    return p.replace("\\", "/")


# ───────────────────────── 分類 ─────────────────────────
def classify(path: str) -> str:
    """リポジトリ(本体)のルートからの相対パス（dev_safe は 'dev_safe/' つき）を 1 つの種類へ。"""
    p = norm(path)
    if p.startswith("dev_safe/"):
        inner = p[len("dev_safe/"):]
        if rs.in_dev_safe_scope(inner):
            return "dev_measure"
        if inner.startswith("Tests/regression/test_") and inner.endswith(".py"):
            return "dev_test"
        if inner.startswith(("Tests/", "scripts/")) and inner.endswith((".py", ".json")):
            return "dev_other_test"
        return "none"
    if rs.in_main_scope(p):
        return "harness"
    if p.startswith(EDITOR_TESTS_DIR + "/"):
        return "editor_tests" if p.endswith(".cs") else "none"
    if p.startswith("Code/"):
        return "none" if p.endswith(".meta") else "code"
    if p.startswith(tuple(d + "/" for d in SELF_TESTS)):
        return "selftests"
    if p.startswith("scripts/build-check/"):
        return "build_check" if p.endswith((".csproj", ".props", ".sh")) else "none"
    if p.startswith("docs/manual/"):
        return "manual"
    if p.startswith("docs/"):
        return "docs"
    if p.startswith(("tools/", "scripts/hooks/", "pytest.ini", ".github/", "scripts/ci-requirements")):
        return "tools"
    return "none"


# ───────────────────────── EditMode の絞り込み ─────────────────────────
_CLASS_RE = re.compile(r"\bclass\s+(\w+)")


def editor_test_classes(rel: str, root: Path = ROOT) -> list[str]:
    """scripts/editor-tests/<rel> にある [TestFixture] らしいクラス名（Tests で終わるもの）。"""
    f = root / rel
    if not f.exists():
        return []
    text = f.read_text(encoding="utf-8-sig", errors="replace")
    return [c for c in _CLASS_RE.findall(text) if c.endswith("Tests") or "[Test" in text and c.endswith("Test")]


def related_editor_tests(code_paths: list[str], root: Path = ROOT) -> list[str] | None:
    """範囲外の Code/ のファイルから、型名（ファイル名の語幹）を参照している EditMode のクラスを推定する。

    None なら絞れない（該当なし・多すぎ）。
    """
    tdir = root / EDITOR_TESTS_DIR
    if not tdir.is_dir():
        return None
    files = {f: f.read_text(encoding="utf-8-sig", errors="replace") for f in sorted(tdir.glob("*.cs"))}
    classes: set[str] = set()
    for cp in code_paths:
        stem = Path(cp).stem.split(".")[0]      # Foo.Bar.cs（partial）は Foo
        pat = re.compile(r"\b" + re.escape(stem) + r"\b")
        for f, text in files.items():
            if pat.search(text):
                classes.update(editor_test_classes(f.relative_to(root).as_posix(), root))
    if not classes or len(classes) > MAX_FILTER_CLASSES:
        return None
    return sorted(classes)


# ───────────────────────── 計画 ─────────────────────────
def plan(paths: list[str], root: Path = ROOT) -> tuple[list[Step], dict[str, list[str]]]:
    """変更ファイルの一覧から、回す手順（順序つき）と、種類ごとのファイル一覧を返す。"""
    by: dict[str, list[str]] = {}
    for p in paths:
        by.setdefault(classify(p), []).append(norm(p))
    steps: dict[str, Step] = {}

    def add(key: str, cmd: list[str], reason: str, note: str = "") -> None:
        s = steps.setdefault(key, Step(key, cmd, [], note))
        if s.cmd != cmd:
            # 同じ入口で絞りが食い違うなら絞らない（EditMode の全件）
            s.cmd = [a for a in s.cmd if a != "-TestFilter"][:len(RT) + 1]
            s.note = "絞りが食い違うので全件"
        if reason not in s.reasons:
            s.reasons.append(reason)

    def files(kind: str, limit: int = 4) -> str:
        fs = by.get(kind, [])
        head = ", ".join(fs[:limit])
        return head + (f" ほか {len(fs) - limit} 件" if len(fs) > limit else "")

    self_cmd = RT + ["-m", "pytest", *SELF_TESTS, "-q"]
    source_cmd = RT + ["-m", "pytest", "scripts/source_checks", "-q"]
    code_dll = ["dotnet", "build", "scripts/build-check/IrocaEditor.Debug.csproj", "-c", "Release", "-nologo"]
    tests_dll = ["dotnet", "build", "scripts/build-check/IrocaEditorTests.csproj", "-c", "Release", "-nologo"]

    if "harness" in by:
        add("1-build", code_dll, f"ハーネス範囲の製品ソース・測定器 ({files('harness')}) は CI の型チェックにも入る")
        add("2-self", self_cmd, f"ハーネス範囲 ({files('harness')}): golden（実 C# 出力の固定）と構造検査")
        add("3-regression", RT + ["-m", "pytest", "dev_safe/Tests/regression/", "-q"],
            f"ハーネス範囲 ({files('harness')}): 出力に効く",
            "採否判断なら IROCA_GATE_FULL=1 を付ける。案を試す 1 回目は -x を付けてよい")
        add("4-visual", ["python", "tools/visual_review.py", "compare"],
            f"ハーネス範囲 ({files('harness')}): pre-commit が視覚レビューの承認を要求する",
            "変更前の snapshot が要る（コードを変える前、または --rev HEAD）。compare のあと approve")
    if "dev_measure" in by:
        add("3-regression", RT + ["-m", "pytest", "dev_safe/Tests/regression/", "-q"],
            f"dev_safe の測定器・基準 ({files('dev_measure')}): 採点・基準が変わる")
        add("4-visual", ["python", "tools/visual_review.py", "compare"],
            f"dev_safe の測定器・基準 ({files('dev_measure')}): dev_safe の pre-commit が視覚レビューの承認を要求する",
            "測定器は変える前に snapshot を撮る。compare のあと approve")
    if "dev_other_test" in by:
        add("3-regression", RT + ["-m", "pytest", "dev_safe/Tests/regression/", "-q"],
            f"dev_safe のテスト・スクリプト ({files('dev_other_test')})")
    if "dev_test" in by and "3-regression" not in steps:
        tests = ["dev_safe/" + p[len("dev_safe/"):] for p in by["dev_test"]]
        add("3-regression", RT + ["-m", "pytest", *tests, "-q"], f"回帰テスト自身の変更 ({files('dev_test')})",
            "テストだけの変更なのでそのファイルだけ。台帳 roles.json の役目も確かめる")
    if "code" in by:
        add("1-build", code_dll, f"範囲外の Code/ ({files('code')}): 型チェック（CI の build-check）")
        add("2-self", self_cmd, f"範囲外の Code/ ({files('code')}): CI が golden・構造検査・unit-run を回す")
        classes = related_editor_tests(by["code"], root)
        if classes:
            add("5-editmode", RT + ["tools/editor_tests.py", "-TestFilter", ";".join(classes)],
                f"範囲外の Code/ ({files('code')}): UI・Infra は EditMode で確かめる",
                "クラスは型名の参照から推定。絞りすぎが心配なら -TestFilter を外して全件")
        else:
            add("5-editmode", RT + ["tools/editor_tests.py"],
                f"範囲外の Code/ ({files('code')}): UI・Infra は EditMode で確かめる",
                "関係するテストのクラスを絞れなかったので全件")
    if "editor_tests" in by:
        add("1-build-tests", tests_dll, f"EditMode のテスト ({files('editor_tests')}): コンパイル（CI と同じ）")
        classes = sorted({c for p in by["editor_tests"] for c in editor_test_classes(p, root)})
        if classes and len(classes) <= MAX_FILTER_CLASSES:
            add("5-editmode", RT + ["tools/editor_tests.py", "-TestFilter", ";".join(classes)],
                f"EditMode のテスト ({files('editor_tests')})")
        else:
            add("5-editmode", RT + ["tools/editor_tests.py"], f"EditMode のテスト ({files('editor_tests')})")
    for kind, label in (("selftests", "golden・構造検査・unit-run 自身"), ("tools", "tools/・CI・フックの設定")):
        if kind in by:
            add("2-self" if kind == "selftests" else "2-source", self_cmd if kind == "selftests" else source_cmd,
                f"{label} ({files(kind)})")
    if "build_check" in by:
        add("1-build", code_dll, f"build-check の設定 ({files('build_check')})")
        add("1-build-tests", tests_dll, f"build-check の設定 ({files('build_check')})")
    if "docs" in by:
        add("2-source", source_cmd, f"docs ({files('docs')}): 定数の台帳などを source_checks が検査している")
    if "manual" in by:
        add("2-source", source_cmd, f"マニュアル ({files('manual')})")
        add("2-manual", ["python", "docs/manual/build_manual.py", "--out", "<出力先>"],
            f"マニュアル ({files('manual')}): CI が組み立てを確かめる", "<出力先> は空の一時フォルダ")

    # 2-self は 2-source を含む（同じ source_checks を 2 回回さない）
    if "2-self" in steps:
        src = steps.pop("2-source", None)
        if src:
            for r in src.reasons:
                if r not in steps["2-self"].reasons:
                    steps["2-self"].reasons.append(r)
    return [steps[k] for k in sorted(steps)], by


# ───────────────────────── 差分の取得 ─────────────────────────
def _git(args: list[str], cwd: Path) -> list[str]:
    r = subprocess.run(["git", *args], cwd=cwd, capture_output=True, check=False)
    if r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} が失敗: {r.stderr.decode('utf-8', 'replace').strip()}")
    return [p for p in r.stdout.decode("utf-8").split("\0") if p]


def changed_files(mode: str, rev: str | None, dev_rev: str | None) -> list[str]:
    """本体と dev_safe の変更ファイル（dev_safe のものは 'dev_safe/' つき）。"""
    out: list[str] = []
    for repo, prefix, r in ((ROOT, "", rev), (DEV_SAFE, "dev_safe/", dev_rev)):
        if not (repo / ".git").exists():
            continue
        if r:
            ps = _git(["diff", "--name-only", "--no-renames", "-z", r], repo)
        elif mode == "index":
            ps = _git(["diff", "--cached", "--name-only", "--no-renames", "-z"], repo)
        else:
            ps = _git(["diff", "--name-only", "--no-renames", "-z", "HEAD"], repo)
            ps += _git(["ls-files", "--others", "--exclude-standard", "-z"], repo)
        out += [prefix + p for p in ps]
    return sorted(set(out))


# ───────────────────────── 表示・実行 ─────────────────────────
def fmt(cmd: list[str]) -> str:
    return " ".join(f'"{a}"' if (" " in a or ";" in a) else a for a in cmd)


def render(steps: list[Step], by: dict[str, list[str]], n_files: int) -> str:
    if not steps:
        return f"変更 {n_files} 件。回すべきテストはありません（テストに影響する種類のファイルが無い）。"
    lines = [f"変更 {n_files} 件から選んだテスト（{len(steps)} 手順）:"]
    for s in steps:
        lines.append("")
        lines.append(fmt(s.cmd))
        for r in s.reasons:
            lines.append(f"    理由: {r}")
        if s.note:
            lines.append(f"    注意: {s.note}")
    return "\n".join(lines)


def run(steps: list[Step]) -> int:
    for s in steps:
        if any(a.startswith("<") for a in s.cmd):
            print(f"[test_select] 手で回す手順なので飛ばす: {fmt(s.cmd)}")
            continue
        cmd = [sys.executable if a == "python" else a for a in s.cmd]
        print(f"\n[test_select] 実行: {fmt(s.cmd)}", flush=True)
        rc = subprocess.run(cmd, cwd=ROOT, env=os.environ).returncode
        if rc != 0:
            print(f"[test_select] 失敗（終了コード {rc}）。ここで止める。", file=sys.stderr)
            return rc
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--index", action="store_true", help="ステージしたものだけを見る")
    ap.add_argument("--rev", help="本体の差分の元（例: HEAD~3..HEAD、abc123）")
    ap.add_argument("--dev-rev", help="dev_safe の差分の元（--rev と同じ書き方）")
    ap.add_argument("--files", nargs="+", help="変更ファイルの一覧を直に渡す（dev_safe のものは dev_safe/ つき）")
    ap.add_argument("--run", action="store_true", help="出した順に実行する（最初の失敗で止める）")
    a = ap.parse_args(argv)
    for st in (sys.stdout, sys.stderr):
        if hasattr(st, "reconfigure"):
            st.reconfigure(encoding="utf-8")
    paths = a.files if a.files else changed_files("index" if a.index else "worktree", a.rev, a.dev_rev)
    steps, by = plan(paths)
    print(render(steps, by, len(paths)))
    return run(steps) if a.run and steps else 0


if __name__ == "__main__":
    sys.exit(main())
