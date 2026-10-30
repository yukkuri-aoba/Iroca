"""いろかのウィンドウのコードが、保存の控えのフィールドを読み書きしないことを機械検査する。

0.4.0 のフェーズ 5 で、編集状態の持ち主をウィンドウから文書（`IrocaEditingDocument`）へ移した。ウィンドウの
`sourceTexture`・`_session`・`_boundRecipe` は、保存される名前を保つための控えで（`docs/architecture.md` の 6 章）、
保存の直前に文書を指し直すだけ。ほかのコードがこれを読むと古い内容を黙って見ることになり（Undo の書き戻しで文書の
中身は別のインスタンスに替わる）、書くと Undo に載らず保存もされない。コンパイラでは止められないのでここで見る。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
UI_DIR = REPO_ROOT / "Code" / "UI"
OWNER_FILE = "IrocaWindow.Document.cs"

# 行コメント / 文字列。
_COMMENT = re.compile(r"//[^\n]*|\"(?:\\.|[^\"\\\n])*\"")
# `recipe.sourceTexture` のように別の型のフィールドを読むものは対象外(手前が . のものを除く)。
# ウィンドウ自身のものを `win.` 越しに読むものは名前で拾う。
_MIRROR = re.compile(r"(?<![\w.])(sourceTexture|_session|_boundRecipe)\b|\b(?:win|w)\.(sourceTexture|_session|_boundRecipe)\b")


def test_only_the_owner_file_touches_the_saved_mirror_fields():
    files = sorted(UI_DIR.glob("IrocaWindow*.cs"))
    assert any(f.name == OWNER_FILE for f in files)
    found = []
    for path in files:
        if path.name == OWNER_FILE:
            continue
        text = _COMMENT.sub(lambda m: "\n" * m.group().count("\n"), path.read_text(encoding="utf-8-sig"))
        for lineno, line in enumerate(text.splitlines(), 1):
            if _MIRROR.search(line):
                found.append(f"{path.name}:{lineno}: {line.strip()}")
    assert not found, "保存の控えを直接使っている(SourceTexture・Session・Doc を使う):\n" + "\n".join(found)
