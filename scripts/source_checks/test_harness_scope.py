"""視覚レビューのゲートの範囲（tools/harness_scope.py）が、Harness.csproj の Include と同じ意味で判定するか。

範囲の判定（is_harness_source）と、範囲のファイルの一覧（harness_source_files。pathlib の glob）が食い違うと、
ハーネスがコンパイルする製品ソースを変えてもゲートが動かない。2026-10-27 まで '**' を 1 階層として扱っていて、
サブフォルダへ移したファイルが黙ってゲートから外れていた（0.4.0 の作り替えでファイルを移す前に直した）。
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import harness_scope as hs  # noqa: E402


@pytest.mark.parametrize("glob, path, expected", [
    ("Code/Core/**/*.cs", "Code/Core/A.cs", True),            # '**' は 0 階層もまたぐ
    ("Code/Core/**/*.cs", "Code/Core/x/y/A.cs", True),        # 2 階層
    ("Code/Core/**/*.cs", "Code/CoreX/A.cs", False),
    ("Code/Core/PixelProcessor*.cs", "Code/Core/PixelProcessor.Hsv.cs", True),
    ("Code/Core/PixelProcessor*.cs", "Code/Core/Sub/PixelProcessor.cs", False),  # '*' は境界を越えない
    ("Code/Core/Mask?tate.cs", "Code/Core/MaskState.cs", True),
    ("Code/Core/Mask?tate.cs", "Code/Core/Mask/tate.cs", False),
])
def test_glob_semantics(glob, path, expected):
    assert bool(hs._glob_to_regex(glob).match(path)) is expected


def test_every_harness_file_is_in_scope():
    files = hs.harness_source_files()
    assert files, "Harness.csproj から製品ソースを 1 件も読めない"
    outside = [f.relative_to(ROOT).as_posix() for f in files
               if not hs.is_harness_source(f.relative_to(ROOT).as_posix())]
    assert not outside, f"一覧にあるのにゲートの範囲と判定されないファイル: {outside}"
