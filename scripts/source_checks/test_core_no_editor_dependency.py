"""Code/Core が UnityEditor に依存しないことを機械検査する。

Code/Core は再着色アルゴリズムと、それを支える純粋なデータ・計算の置き場で、
PixelProcessor.cs や RecolorPreview.cs の doc が掲げるとおり UnityEditor に依存しない。
この境界があるから、headless ハーネス（scripts/headless-run/Harness.csproj）と
scripts/unit-run は UnityEngine.CoreModule だけを参照して Core のファイルをそのまま
コンパイルし、Unity なしで実 C# の出力を測れる。

IMGUI・Undo・EditorApplication.update など UnityEditor を使う部品は Code/UI か
Code/Infra に置く（以前は UndoHelper.cs と PreviewJob.cs が Core にあり、境界と
フォルダ構成が食い違っていた）。Core に UnityEditor が 1 行でも入ると、いまハーネスに
載っていないファイルでも、後で取り込もうとした時点で初めてコンパイルが壊れる。

コメントと文字列リテラルは除いてから見る（doc の「UnityEditor 非依存」を拾わない）。
C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CORE_DIR = REPO_ROOT / "Code" / "Core"

# 行コメント / ブロックコメント / 文字列リテラル（test_event_subscription_symmetry.py と同じ）。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)
# UnityEditor 名前空間への参照。`using UnityEditor;`・`using static UnityEditor.X;`・
# `UnityEditor.X`・`global::UnityEditor.X`・別名 using・UnityEditorInternal をまとめて拾う。
_EDITOR_REF = re.compile(r"\bUnityEditor(?:Internal)?\b")


def _strip_comments(src: str) -> str:
    """コメントと文字列リテラルを空白にする。改行は残して行番号を保つ。"""
    return _COMMENT.sub(lambda m: "\n" * m.group().count("\n") or " ", src)


def _editor_refs(src: str) -> list[tuple[int, str]]:
    """コードとしての UnityEditor 参照を (行番号, その行の内容) で返す。"""
    code = _strip_comments(src)
    lines = code.splitlines()
    hits = []
    for m in _EDITOR_REF.finditer(code):
        line_no = code.count("\n", 0, m.start()) + 1
        hits.append((line_no, lines[line_no - 1].strip()))
    return hits


def test_detector_finds_code_refs_and_ignores_comments():
    """検出器が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    src = (
        "using UnityEditor;\n"
        "using static UnityEditor.EditorGUI;\n"
        "using Util = UnityEditorInternal.InternalEditorUtility;\n"
        "class A { void F() { global::UnityEditor.Undo.RecordObject(null, \"x\"); } }\n"
        "// UnityEditor に依存しない（コメントは数えない）\n"
        "/* UnityEditor.Undo\n"
        "   UnityEditor */ class B { string s = \"UnityEditor.Undo\"; }\n"
        "class UnityEditorPathHolder { }\n"
        "using UnityEditor.SceneManagement;\n"
    )
    assert [line for line, _ in _editor_refs(src)] == [1, 2, 3, 4, 9]


def test_core_does_not_reference_unity_editor():
    """Code/Core の .cs がコードとして UnityEditor を参照しないこと。"""
    files = sorted(CORE_DIR.rglob("*.cs"))
    # 回帰の前提。パスが変わって 0 件になると空一致で通ってしまう。
    assert len(files) >= 10, f"Code/Core の .cs の列挙が異常です({len(files)} 件)"

    offenders = []
    for path in files:
        rel = path.relative_to(REPO_ROOT).as_posix()
        for line_no, text in _editor_refs(path.read_text(encoding="utf-8")):
            offenders.append(f"  {rel}:{line_no}: {text}")
    assert not offenders, (
        "Code/Core が UnityEditor を参照しています:\n"
        + "\n".join(offenders)
        + "\n  Core はハーネス（Harness.csproj）や unit-run で UnityEngine.CoreModule だけを"
        "\n  参照してコンパイルされ得るため、UnityEditor に依存できない。IMGUI・Undo・"
        "\n  EditorApplication などを使う部品は Code/UI か Code/Infra に置くこと。")
