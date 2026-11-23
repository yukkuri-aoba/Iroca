"""tools/test_select.py（変更ファイル → 回すテストの選択）の判定を、偽のファイル一覧で確かめる。

範囲の正は tools/review_state.py（中で harness_scope.py）。ここでは一覧を写さず、同じ関数に聞いた結果と
道具の選択が食い違わないこと（フックが承認を求めるファイルで、回帰と視覚レビューを省かないこと）を見る。
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import review_state as rs  # noqa: E402
import test_select as ts  # noqa: E402


def keys(paths):
    steps, _ = ts.plan(paths)
    return {s.key for s in steps}


def cmds(paths):
    steps, _ = ts.plan(paths)
    return [" ".join(s.cmd) for s in steps]


def test_harness_source_needs_regression_golden_and_visual():
    k = keys(["Code/Core/Pipeline/PixelProcessor.cs"])
    assert {"2-self", "3-regression", "4-visual"} <= k
    assert "5-editmode" not in k


def test_ui_only_skips_regression_and_visual():
    p = "Code/UI/IrocaWindow.cs"
    assert not rs.in_main_scope(p)
    k = keys([p])
    assert "3-regression" not in k and "4-visual" not in k
    assert {"1-build", "2-self", "5-editmode"} <= k


def test_docs_only_runs_source_checks_only():
    assert cmds(["docs/algorithm-constants.md"]) == [
        "python tools/remote_test.py -- -m pytest scripts/source_checks -q"]


def test_unrelated_file_selects_nothing():
    assert ts.plan(["dev_safe/docs/note.md", "README.md"])[0] == []


def test_dev_safe_measurer_requires_visual_review():
    inner = rs.DEV_SAFE_FILES[0]
    assert rs.in_dev_safe_scope(inner[len("dev_safe/"):])
    assert {"3-regression", "4-visual"} <= keys([inner])


def test_regression_test_only_is_narrowed_to_that_file():
    c = cmds(["dev_safe/Tests/regression/test_repro_cases_extra.py"])
    assert len(c) == 1 and "dev_safe/Tests/regression/test_repro_cases_extra.py" in c[0]


def test_every_gate_scope_path_gets_visual_review():
    # フックが承認を求める本体のファイルは、必ず視覚レビューの手順を伴う
    for p in list(rs.MAIN_TOOLS) + ["scripts/headless-run/Harness.cs"]:
        assert rs.in_main_scope(p)
        assert "4-visual" in keys([p]), p


def test_editor_tests_file_filters_to_its_classes():
    steps, _ = ts.plan(["scripts/editor-tests/AutomationMaskTests.cs"])
    ed = [s for s in steps if s.key == "5-editmode"][0]
    assert "AutomationMaskTests" in ed.cmd[-1]
    assert any(s.key == "1-build-tests" and "IrocaEditorTests" in " ".join(s.cmd) for s in steps)


def test_meta_files_ignored():
    assert ts.plan(["Code/UI/Foo.cs.meta"])[0] == []


def test_selftests_dedupes_source_checks():
    c = cmds(["Code/UI/X.cs", "docs/a.md"])
    assert sum("scripts/source_checks" in x for x in c) == 1
