"""Unity が保存するフィールドの名前を、今の一覧から黙って変えないことを機械検査する。

0.4.0 の計画（docs/plan-0.4.0.md）の決まり 3「互換を壊さない」と 0-2。ウィンドウ（`IrocaWindow` ほか）と
ビューのフィールドは、エディタのレイアウトとドメインの読み直しをまたいで Unity が名前で保存し、名前で読み戻す。
`IrocaSessionState`・`ColorZone`・`MaskState`・プリセット・自動化 API の DTO は JsonUtility が同じ規則で JSON の項目名にし、
`IrocaRecolor`・`IrocaRecipe` はアセットに名前で書かれる。名前を変えたり別の入れ物へ移したりすると、利用者の作業状態・
レシピ・プリセットが読めなくなる（最悪、空のセッションで保存して結びついたレシピを上書きする）。0.4.0 のフェーズ 4 で
ウィンドウを部品に分けるときに、これを踏まないための検査。

`serialized_field_names.json` に、型ごとの保存されるフィールドの名前を載せてある。
  - 一覧の名前が、今のフィールドにも `[FormerlySerializedAs("…")]` にも無ければ落ちる（名前が消えた・変わった）。
    名前を変えるなら `[FormerlySerializedAs("古い名前")]` を付け、一覧には新しい名前を足す（古い名前は残す）。
  - 一覧に無い保存されるフィールドが現れたら落ちる（一覧に足す。一覧を今の保存の形の目録として保つため）。
保存されるフィールドは Unity の規則で見分ける: `[Serializable]` の型と、EditorWindow・ScriptableObject・MonoBehaviour を
継ぐ型の、static・const・readonly でなく、public か `[SerializeField]` で、`[NonSerialized]` でないフィールド。
型そのものの名前は保存されない（`[SerializeReference]` は使っていない）ので、型の名前を変えたときは一覧の見出しを直す。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"
LIST_PATH = Path(__file__).with_name("serialized_field_names.json")

_FSA = re.compile(r"FormerlySerializedAs\s*\(\s*\"((?:\\.|[^\"\\])*)\"\s*\)")
# 行コメント / ブロックコメント / 逐語的文字列 / 文字列 / 文字リテラル。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|@\"(?:\"\"|[^\"])*\"|\"(?:\\.|[^\"\\\n])*\"|'(?:\\.|[^'\\])'",
                      re.DOTALL)
_TYPE_HEAD = re.compile(r"\b(class|struct|interface|enum|record)\s+([A-Za-z_]\w*)")
_NAMESPACE = re.compile(r"\bnamespace\s+[\w.]+\s*$")
_CONTAINER_BASE = re.compile(r":\s*[^{]*\b(EditorWindow|ScriptableObject|MonoBehaviour|ScriptableSingleton)\b")
_ATTRS = re.compile(r"^\s*\[((?:[^\[\]]|\[[^\[\]]*\])*)\]")
_NOT_SERIALIZED_MODIFIERS = {"static", "const", "readonly", "event", "delegate", "using", "abstract", "extern",
                             "operator"}


def _prepare(src: str) -> str:
    """FormerlySerializedAs の古い名前だけ残して、コメントと文字列を消す。"""
    src = _FSA.sub(lambda m: f"FormerlySerializedAs(__FSA_{m.group(1)}__)", src)
    return _COMMENT.sub(lambda m: "\n" * m.group().count("\n") or " ", src)


def _split_top(text: str, sep: str) -> list[str]:
    """括弧（() [] {} <>）の外の sep で分ける。"""
    parts, depth, cur = [], 0, []
    for ch in text:
        if ch in "([{<":
            depth += 1
        elif ch in ")]}>":
            depth -= 1
        if depth == 0 and ch == sep:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return parts


def _has_assignment(text: str) -> bool:
    """括弧の外に代入の = があるか（=> == <= >= != は数えない）。"""
    depth = 0
    for i, ch in enumerate(text):
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        elif ch == "=" and depth == 0:
            prev = text[i - 1] if i else ""
            nxt = text[i + 1] if i + 1 < len(text) else ""
            if nxt not in "=>" and prev not in "=<>!":
                return True
    return False


def _parse_field(stmt: str) -> tuple[list[str], bool, list[str]] | None:
    """型の本体の文 1 つ（; の前まで）がフィールドなら (名前の並び, 保存されるか, 古い名前の並び)。"""
    attrs = []
    while True:
        m = _ATTRS.match(stmt)
        if not m:
            break
        attrs.append(m.group(1))
        stmt = stmt[m.end():]
    attr_text = " ".join(attrs)
    decl = stmt.strip()
    if not decl or "=>" in decl:
        return None
    declarators = _split_top(decl, ",")
    head = _split_top(declarators[0], "=")[0]
    if "(" in head:                      # メソッド・抽象メンバー・デリゲート
        return None
    words = re.findall(r"[A-Za-z_][\w.]*", re.sub(r"<[^<>]*(?:<[^<>]*>[^<>]*)*>", " ", head))
    if len(words) < 2:
        return None
    if _NOT_SERIALIZED_MODIFIERS & set(words[:-1]):
        return None
    names = [words[-1]] + [re.findall(r"[A-Za-z_]\w*", _split_top(d, "=")[0])[-1]
                           for d in declarators[1:] if re.search(r"[A-Za-z_]", d)]
    serialized = ("public" in words[:-1] or re.search(r"\bSerializeField\b", attr_text) is not None) \
        and re.search(r"\bNonSerialized\b", attr_text) is None
    formers = re.findall(r"__FSA_(.*?)__", attr_text)
    return names, serialized, formers


def scan(code: str) -> tuple[dict[str, bool], dict[str, list[tuple[str, bool, list[str]]]]]:
    """(型 → 保存の入れ物か, 型 → フィールドの並び)。code は _prepare したもの。型は入れ子を . でつなぐ。"""
    containers: dict[str, bool] = {}
    fields: dict[str, list] = defaultdict(list)
    stack: list[tuple] = []             # ("ns",) / ("type", 名前) / ("init",) / ("block",)
    start = 0
    for i, ch in enumerate(code):
        if ch not in "{};":
            continue
        top = stack[-1] if stack else None
        at_container = top is None or top[0] in ("ns", "type")
        if ch == "{":
            head = code[start:i]
            if not at_container:
                stack.append(("block",))
                continue
            m = _TYPE_HEAD.search(head)
            if m and not _has_assignment(head[:m.start()]):
                outer = top[1] + "." if top and top[0] == "type" else ""
                name = outer + m.group(2)
                serializable = m.group(1) in ("class", "struct") and (
                    re.search(r"\[[^\]]*\bSerializable\b[^\]]*\]", head[:m.start()]) is not None
                    or _CONTAINER_BASE.search(head[m.end():]) is not None)
                containers[name] = containers.get(name, False) or serializable
                stack.append(("type", name))
                start = i + 1
            elif _NAMESPACE.search(head):
                stack.append(("ns",))
                start = i + 1
            elif top and top[0] == "type" and _has_assignment(head):
                stack.append(("init",))         # フィールドの初期化子の { }。文は ; まで続く
            else:
                stack.append(("block",))        # メソッド・プロパティの本体など
        elif ch == "}":
            popped = stack.pop() if stack else None
            if popped and popped[0] == "init":
                continue
            new_top = stack[-1] if stack else None
            if new_top is None or new_top[0] in ("ns", "type"):
                start = i + 1
        else:  # ;
            if top and top[0] == "type":
                parsed = _parse_field(code[start:i])
                if parsed:
                    names, serialized, formers = parsed
                    for n in names:
                        fields[top[1]].append((n, serialized, formers))
                start = i + 1
            elif at_container:
                start = i + 1
    return containers, fields


def current() -> dict[str, dict[str, set[str]]]:
    """保存の入れ物の型 → {"fields": 保存されるフィールドの名前, "formers": FormerlySerializedAs の古い名前}。"""
    containers: dict[str, bool] = {}
    fields: dict[str, list] = defaultdict(list)
    for path in sorted(CODE_DIR.rglob("*.cs")):
        c, f = scan(_prepare(path.read_text(encoding="utf-8")))
        for k, v in c.items():
            containers[k] = containers.get(k, False) or v
        for k, v in f.items():
            fields[k].extend(v)
    out = {}
    for name, is_container in containers.items():
        if not is_container:
            continue
        out[name] = {"fields": {n for n, s, _ in fields.get(name, []) if s},
                     "formers": {old for n, s, fs in fields.get(name, []) if s for old in fs}}
    return out


def test_detector():
    """見分け方が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    code = _prepare(
        "namespace Iroca {\n"
        "  public partial class W : EditorWindow {\n"
        "    public Texture2D sourceTexture;\n"
        "    [SerializeField] private IrocaSessionState _session = new IrocaSessionState { a = 1 };\n"
        "    [NonSerialized] public bool previewDirty;\n"
        "    private int _notSaved;\n"
        "    public static int s_x; public const int K = 1; public readonly int r;\n"
        "    [SerializeField, FormerlySerializedAs(\"oldName\")] private float newName = 0.5f, second;\n"
        "    public int Prop { get; set; } = 3;\n"
        "    public int Expr => 1;\n"
        "    public event System.Action E;\n"
        "    public void F() { int local = 0; }\n"
        "    [Serializable] internal class Inner { public List<Dictionary<int, int>> map = new(); }\n"
        "  }\n"
        "  internal class NotSaved { public int x; }\n"
        "}\n")
    containers, fields = scan(code)
    assert containers == {"W": True, "W.Inner": True, "NotSaved": False}
    saved = {k: sorted(n for n, s, _ in v if s) for k, v in fields.items()}
    assert saved["W"] == ["_session", "newName", "second", "sourceTexture"]
    assert saved["W.Inner"] == ["map"]
    assert [fs for n, s, fs in fields["W"] if n == "newName"] == [["oldName"]]


def test_saved_names_are_not_removed_or_renamed():
    """一覧の名前が、今のフィールドか FormerlySerializedAs に残っていること。"""
    expected = json.loads(LIST_PATH.read_text(encoding="utf-8"))["types"]
    now = current()
    # 回帰の前提。読み方が壊れて 0 件になると、空一致で通ってしまう。
    assert sum(len(v["fields"]) for v in now.values()) >= 100, "保存されるフィールドの収集が異常です"
    lost = []
    for type_name, names in sorted(expected.items()):
        have = now.get(type_name, {"fields": set(), "formers": set()})
        lost += [f"{type_name}.{n}" for n in names if n not in have["fields"] | have["formers"]]
    assert not lost, (
        "保存されるフィールドの名前が消えた・変わりました:\n  " + "\n  ".join(lost)
        + "\n  利用者のウィンドウの状態・セッション・レシピ・プリセットが読めなくなる。名前を変えるなら"
        "\n  [FormerlySerializedAs(\"古い名前\")] を付ける。本当に要らなくなったなら、読めなくなってよい理由を"
        f"\n  コミットに書いて {LIST_PATH.name} から外す。")


def test_new_saved_fields_are_listed():
    """一覧に無い保存されるフィールドが無いこと（一覧を保存の形の目録として保つ）。"""
    expected = json.loads(LIST_PATH.read_text(encoding="utf-8"))["types"]
    new = sorted(f"{t}.{n}" for t, v in current().items() for n in v["fields"] if n not in expected.get(t, []))
    assert not new, (
        f"{LIST_PATH.name} に無い保存されるフィールドがあります:\n  " + "\n  ".join(new)
        + "\n  保存してよいものなら一覧に足す。保存しないつもりなら [NonSerialized] を付ける。")
