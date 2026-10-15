"""AI マスク提案モデルのファイル名の二重定義が、食い違わずに Fetch-Models.ps1 から読めることを検査する。

モデルのファイル名は 2 か所にある:

  Code/MaskSuggest/MaskSuggestBridge.cs          EncoderFileName / DecoderFileName（パスを組む唯一の正）
  Code/MaskSuggest/MaskSuggestModelDownload.cs   Files（ファイル名・sha256・バイト数のダウンロード表）

二重定義を定数参照に寄せられないのは、開発用の取得スクリプト scripts/Fetch-Models.ps1 が
ダウンロード表を ("x.onnx", "sha256", sizeL) のリテラル形式のまま正規表現で読んでいるため
（ハッシュを書き写さないための設計）。ここでは ps1 に書かれた正規表現そのものを取り出して
C# ソースに当て、次を機械検査する:

  1. Files からモデルがちょうど 2 件取れ、ModelRepo と ModelBranch も取れる
  2. そのファイル名の集合が MaskSuggestBridge の EncoderFileName / DecoderFileName と一致する

C# ソースと ps1 を直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
FETCH_PS1 = REPO_ROOT / "scripts" / "Fetch-Models.ps1"
BRIDGE_CS = REPO_ROOT / "Code" / "MaskSuggest" / "MaskSuggestBridge.cs"

_LITERAL_NOTE = (
    "Fetch-Models.ps1 は MaskSuggestModelDownload.cs の Files を (\"x.onnx\", \"sha256\", sizeL) の"
    "リテラル形式のまま正規表現で読む。Files を MaskSuggestBridge.EncoderFileName などの定数参照に"
    "書き換えないこと（取得スクリプトが壊れ、モデルが揃わないと主経路ゲートは fail でなく skip で緑になる）。")


def _ps1() -> str:
    # ps1 は UTF-8 BOM 付き。
    return FETCH_PS1.read_text(encoding="utf-8-sig")


def _ps1_patterns() -> list[tuple[str, str]]:
    """ps1 の [regex]::Match / Matches($src, '...') の (メソッド名, 正規表現) を書かれた順に返す。

    PowerShell の単一引用符文字列は '' で ' を表すので戻す。.NET と Python の re はこの範囲の
    構文（\\s \\d [^"] {64}）で同じ意味になる。"""
    return [(m.group(1), m.group(2).replace("''", "'"))
            for m in re.finditer(r"\[regex\]::(Matches|Match)\(\$src,\s*'((?:[^']|'')*)'\)", _ps1())]


def _pattern(kind: str, prefix: str = "") -> str:
    found = [p for k, p in _ps1_patterns() if k == kind and p.startswith(prefix)]
    assert len(found) == 1, (
        f"Fetch-Models.ps1 から [regex]::{kind}($src, '{prefix}...') を 1 つに特定できません: {found}\n"
        "  ps1 の読み取り方を変えたなら、このテストの抽出も合わせること。")
    return found[0]


def _download_cs() -> Path:
    """ps1 が読む C# ソース（$srcPath = Join-Path $PSScriptRoot '...'）。"""
    m = re.search(r"\$srcPath\s*=\s*Join-Path\s+\$PSScriptRoot\s+'([^']+)'", _ps1())
    assert m, "Fetch-Models.ps1 が読む C# ソースのパス（$srcPath）を特定できません"
    path = (FETCH_PS1.parent / m.group(1).replace("\\", "/")).resolve()
    assert path.is_file(), f"Fetch-Models.ps1 が読む C# ソースがありません: {path}"
    return path


def _download_src() -> str:
    # ps1 と同じく、コメントを落とさない生のテキストに当てる。
    return _download_cs().read_text(encoding="utf-8-sig")


def _download_file_names() -> list[str]:
    return [m.group(1) for m in re.finditer(_pattern("Matches"), _download_src())]


def _bridge_name(const: str) -> str:
    m = re.search(rf"const\s+string\s+{const}\s*=\s*\"([^\"]+)\"", BRIDGE_CS.read_text(encoding="utf-8-sig"))
    assert m, f"MaskSuggestBridge.{const} のリテラルが見つかりません"
    return m.group(1)


def test_fetch_script_reads_two_models():
    names = _download_file_names()
    assert len(names) == 2, (
        f"Fetch-Models.ps1 の正規表現で Files から取れたモデルが 2 件ではありません: {names}\n  " + _LITERAL_NOTE)


def test_fetch_script_reads_repo_and_branch():
    src = _download_src()
    for prefix in ("ModelRepo", "ModelBranch"):
        assert re.search(_pattern("Match", prefix), src), (
            f"Fetch-Models.ps1 の正規表現で {prefix} を読めません。配布元の URL を組めなくなる。\n"
            "  ModelRepo / ModelBranch は \"...\" のリテラルのまま書くこと。")


def test_model_file_names_match_bridge_constants():
    names = set(_download_file_names())
    expected = {_bridge_name("EncoderFileName"), _bridge_name("DecoderFileName")}
    assert names == expected, (
        "ダウンロード表のファイル名と MaskSuggestBridge の EncoderFileName / DecoderFileName が一致しません。\n"
        f"  ダウンロード表: {sorted(names)}\n  Bridge: {sorted(expected)}\n"
        "  片方だけ改名すると、ダウンロードしたモデルを製品が見つけられない。\n  " + _LITERAL_NOTE)
