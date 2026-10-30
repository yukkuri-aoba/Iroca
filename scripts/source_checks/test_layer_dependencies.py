"""Code/ のフォルダ（層）の間の依存の向きを、許可表で機械検査する。

0.4.0 の計画（docs/plan-0.4.0.md）の 3.1 の層と、決まり 4（依存の向きは機械で守る。まず許可表つきの検査で縛り、
最後に asmdef で固める）。名前空間はほぼ全部 `Iroca` なので `using` では層が分からない。そこで、フォルダごとに
宣言している一番外側の型の名前を集め、ほかのフォルダのコードがその名前を書いている所を依存として数える。

今ある違反は KNOWN_VIOLATIONS に載せてある。この一覧は**減らすことしかできない**: 載っていない違反が現れたら落ち、
載っているのに無くなった違反も落ちる（一覧から外すこと）。ファイルを移したときは、一覧も同じコミットで直す。

見分け方の限り:
  - 型の名前で見るので、同じ名前の型が複数の層にあるとき（partial の型を層をまたいで分けているときなど）は、
    どれか 1 つが許可されていれば通す。層をまたぐ partial は SPLIT_TYPES に載せる（これも減らすことしかできない）。
  - `x.Name` のようにドットの後ろに書いた名前はメンバーとみなして数えない（入れ子の型・名前空間つきの参照は
    見逃す）。拡張メソッドだけを使う依存も見えない。asmdef で固めるフェーズ 6 までのつなぎ。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"

_BASE = {"Core", "MaskSuggest/Ops"}
_SERVICES = _BASE | {"Runtime", "Infra", "Text"}
# 層 → その層のコードが使ってよい層（自分を含む）。計画の 3.1 の図の上から下への向き。
# Application（計画の 3.3。4-2 で SourcePixelCache から作り始めた）・Text（文言。4-1 で UI の Localization を移した）。MaskSuggest（Ops を除く）は
# フェーズ 6 で Infra・Application・Text と同じ Services の asmdef に入る。
ALLOWED: dict[str, set[str]] = {
    "Core": {"Core"},
    "MaskSuggest/Ops": {"MaskSuggest/Ops", "Core"},
    "Runtime": {"Runtime"},
    "Infra": _SERVICES,
    "Text": _SERVICES,
    "MaskSuggest": _SERVICES | {"MaskSuggest"},
    "Application": _SERVICES | {"MaskSuggest", "Application"},
    "Automation": _SERVICES | {"MaskSuggest", "Application", "Automation"},
    "UI": _SERVICES | {"MaskSuggest", "Application", "Automation", "UI"},
    # 統合は上の層の決まった窓口だけを使う。NDMF と Sentis は Services まで、MCP は自動化 API まで。
    "NdmfIntegration": _SERVICES | {"MaskSuggest", "Application", "NdmfIntegration"},
    "SentisIntegration": _SERVICES | {"MaskSuggest", "Application", "SentisIntegration"},
    "McpIntegration": _SERVICES | {"MaskSuggest", "Application", "Automation", "McpIntegration"},
    "Debug": _SERVICES | {"MaskSuggest", "Application", "Automation", "UI", "Debug"},
    "(root)": {"(root)"},
}

# 今ある違反。(使う側のファイル, 使われる型) → 使われる型の層。**減らすことしかできない。**
KNOWN_VIOLATIONS: dict[tuple[str, str], str] = {
    # 4-2・4-3: 開いているウィンドウを探す・その状態を読む・窓口を借りるのを、EditingSessionRegistry・
    # EditingSession・AutoTuneService・AiSuggestBroker を通す形にする。
    ("Code/Automation/IrocaAutomation.Open.cs", "IrocaWindow"): "UI",
    ("Code/Automation/IrocaAutomation.Recolor.cs", "IrocaWindow"): "UI",
    # フェーズ 6 の前: MaskSuggest のうちウィンドウに描く部分（コントローラ・セクション）を UI 側へ分ける。
    ("Code/MaskSuggest/MaskSuggestController.cs", "IrocaWindow"): "UI",
    ("Code/MaskSuggest/MaskSuggestController.cs", "MaskPaintView"): "UI",
    ("Code/MaskSuggest/MaskSuggestController.cs", "MaskRegionEditResult"): "UI",
    ("Code/MaskSuggest/MaskSuggestSection.cs", "IrocaWindow"): "UI",
    ("Code/MaskSuggest/MaskSuggestSection.cs", "MaskPaintView"): "UI",
}

# 層をまたいで宣言している型（partial の型を分けている・同じ名前の型が 2 つの層にある）。型 → 層の集合。
SPLIT_TYPES: dict[str, set[str]] = {
    # 3-2 で DebugCaptureHooks(Core)と DebugWindowHooks(UI)に分けたので空。
}

# 行コメント / ブロックコメント / 逐語的文字列 / 文字列 / 文字リテラル。改行は残して行番号を保つ。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|@\"(?:\"\"|[^\"])*\"|\"(?:\\.|[^\"\\\n])*\"|'(?:\\.|[^'\\])'",
                      re.DOTALL)
_TYPE_DECL = re.compile(
    r"\b(?:class|struct|interface|enum|record|delegate\s+[\w<>\[\],.? ]+?)\s+([A-Z]\w*)")
_NAMESPACE = re.compile(r"\bnamespace\s+[\w.]+\s*$")
_IDENT = re.compile(r"(?<![\w.])([A-Z]\w*)\b")


def layer_of(rel: str) -> str:
    """`Code/` からの相対でない、リポジトリからの相対パス（`Code/UI/X.cs`）の層。"""
    parts = rel.split("/")
    if len(parts) <= 2:
        return "(root)"
    if parts[1] == "MaskSuggest" and len(parts) > 3 and parts[2] == "Ops":
        return "MaskSuggest/Ops"
    return parts[1]


def _strip(src: str) -> str:
    return _COMMENT.sub(lambda m: "\n" * m.group().count("\n") or " ", src)


def top_level_types(code: str) -> set[str]:
    """名前空間の直下（入れ子でない）に宣言した型の名前。code はコメントを除いたもの。"""
    names: set[str] = set()
    stack: list[bool] = []          # 開いている { が名前空間のものか（ファイル単位の名前空間なら空のまま）
    last = 0                        # 直前の ; { } の後ろ（宣言の頭の始まり）
    for i, ch in enumerate(code):
        if ch not in "{};":
            continue
        head = code[last:i]
        if all(stack):
            names.update(m.group(1) for m in _TYPE_DECL.finditer(head))
        if ch == "{":
            stack.append(bool(_NAMESPACE.search(head)))
        elif ch == "}" and stack:
            stack.pop()
        last = i + 1
    return names


def _sources() -> dict[str, str]:
    return {p.relative_to(REPO_ROOT).as_posix(): _strip(p.read_text(encoding="utf-8"))
            for p in sorted(CODE_DIR.rglob("*.cs"))}


def _type_layers(sources: dict[str, str]) -> dict[str, set[str]]:
    owners: dict[str, set[str]] = defaultdict(set)
    for rel, code in sources.items():
        for name in top_level_types(code):
            owners[name].add(layer_of(rel))
    return owners


def _violations(sources: dict[str, str], owners: dict[str, set[str]]) -> dict[tuple[str, str], str]:
    """(ファイル, 型) → 型の層。許可表で許されない参照だけ。"""
    out: dict[tuple[str, str], str] = {}
    for rel, code in sources.items():
        layer = layer_of(rel)
        allowed = ALLOWED.get(layer, {layer})
        for name in set(_IDENT.findall(code)):
            layers = owners.get(name)
            if layers and not (layers & allowed):
                out[(rel, name)] = "|".join(sorted(layers))
    return out


def test_detector():
    """見分け方が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    code = _strip(
        "namespace Iroca {\n"
        "  internal static partial class Outer {\n"
        "    private sealed class Inner { }\n"       # 入れ子は数えない
        "    private void F() { var s = \"class Fake\"; }\n"
        "  }\n"
        "  public enum Mode { A, B }\n"
        "  internal delegate void Done(int x);\n"
        "  // class Comment\n"
        "}\n")
    assert top_level_types(code) == {"Outer", "Mode", "Done"}
    assert _IDENT.findall("var x = Outer.Make(y.Outer, Mode.A);") == ["Outer", "Mode"]  # ドットの後ろは数えない
    assert layer_of("Code/MaskSuggest/Ops/A.cs") == "MaskSuggest/Ops"
    assert layer_of("Code/MaskSuggest/A.cs") == "MaskSuggest"
    assert layer_of("Code/AssemblyInfo.cs") == "(root)"


def test_every_layer_is_in_the_table():
    """フォルダを足したら許可表にも足す（載っていない層は自分しか使えない扱いで、黙って広がらないように）。"""
    layers = {layer_of(rel) for rel in _sources()}
    missing = sorted(layers - set(ALLOWED))
    assert not missing, f"ALLOWED に載っていない層があります: {missing}"


def test_no_new_layer_violations():
    """許可表に無い向きの参照が、KNOWN_VIOLATIONS に載っている分しか無いこと。"""
    sources = _sources()
    owners = _type_layers(sources)
    # 回帰の前提。読み方が壊れて型が集まらないと、空一致で通ってしまう。
    assert len(owners) >= 100, f"型の収集が異常です({len(owners)} 件)"
    found = _violations(sources, owners)
    new = sorted((k, v) for k, v in found.items() if k not in KNOWN_VIOLATIONS)
    assert not new, (
        "許可されていない向きの依存があります(使う側のファイル → 型 [型の層]):\n"
        + "\n".join(f"  {rel} → {name} [{lay}]" for (rel, name), lay in new)
        + "\n  下の層は上の層を知らない(計画の 3.1)。上の層へ知らせるのはイベントか、下の層の窓口を通す。")


def test_known_violations_only_shrink():
    """直した違反は一覧から外す（一覧が実際より大きいと、また増やせてしまう）。"""
    sources = _sources()
    found = _violations(sources, _type_layers(sources))
    stale = sorted(k for k in KNOWN_VIOLATIONS if k not in found)
    assert not stale, (
        "KNOWN_VIOLATIONS に、もう無い違反が載っています(一覧から外すこと):\n"
        + "\n".join(f"  {rel} → {name}" for rel, name in stale))


def test_split_types_only_shrink():
    """層をまたいで宣言している型が、SPLIT_TYPES の分から増えも減りもしないこと。"""
    owners = _type_layers(_sources())
    split = {name: layers for name, layers in owners.items() if len(layers) > 1}
    assert split == SPLIT_TYPES, (
        "層をまたいで宣言している型が SPLIT_TYPES と食い違います:\n"
        f"  今: {dict(sorted(split.items()))}\n  一覧: {dict(sorted(SPLIT_TYPES.items()))}\n"
        "  新しく分けたなら、層ごとの型に分ける。直したなら一覧から外す。")
