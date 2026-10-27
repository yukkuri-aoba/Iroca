"""画素数に比例する大きさの管理配列（Mono のヒープ）を、新しいコードで作らないことを機械検査する。

0.4.0 の計画（docs/plan-0.4.0.md）の決まり 8。8K の原本は 6,700 万画素あり、`new bool[w * h]` で 67MB、
`new Color32[w * h]` で 268MB が Mono のヒープに載る。Mono の GC は大きな塊を動かせず、ヒープは縮まないので、
1 回の確保がその後ずっとエディタの常駐量になる。画素数の大きさのバッファはヒープの外の部品
（`NativeWorkPool`・`NativePixels`。計画の 3.4）で持つ。

今ある分は PIXEL_SIZED_ALLOWED（`pixel_sized_managed_arrays.json`）に載せてある。この一覧は**減らすことしか
できない**: 一覧に無い確保が現れたら落ち、一覧にあるのに無くなった（または減った）確保も落ちる（一覧を減らすこと）。
ファイルを移したときは、一覧のパスも同じコミットで直す。

画素数の大きさかどうかは、大きさの式の形で見分ける（静的には正確に決められないので、このコードベースの書き方に
合わせた見分け方）:
  - 定数でない 2 つ以上の項の積（`w * h`・`gw * gh * 4`・`(x1 - x0 + 1) * (y1 - y0 + 1)`・`h * wpr`）
  - 画素数を入れる名前 `len`（`int len = w * h;` がこのコードの決まり）・`nWords`（1 画素 1 ビットの語数）と、
    配列の長さ `Length`
  - Unity の API が返す画素の管理配列（`GetPixels32()`・`GetPixels()`・`GetRawTextureData()`・`EncodeToPNG()` など）
この見分け方が拾うのに画素数の大きさでないもの（ヒストグラムの長さ・小さな窓など）は、
NOT_PIXEL_SIZED に理由つきで載せる。名前を変えて大きさを隠せば通ってしまうので、決まり 8 はレビューでも見る。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import json
import re
from collections import Counter
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"
ALLOWED_PATH = Path(__file__).with_name("pixel_sized_managed_arrays.json")

# 行コメント / ブロックコメント / 文字列リテラル / 文字リテラル（test_event_subscription_symmetry.py に文字を足したもの）。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"|'(?:\\.|[^'\\])'", re.DOTALL)

_ELEMENT_TYPES = ("bool|byte|sbyte|short|ushort|int|uint|long|ulong|float|double|half|"
                  "Color32|Color|Vector2|Vector3|Vector4")
# new T[大きさ]。大きさの中の添字 1 段（`new int[rowOff[h]]`）まで読む。
_NEW_ARRAY = re.compile(r"\bnew\s+(" + _ELEMENT_TYPES + r")\s*\[([^\[\]]*(?:\[[^\[\]]*\][^\[\]]*)*)\]")
# Unity の API が返す画素の管理配列。GetRawTextureData<T>() は NativeArray を返すので数えない。
_UNITY_PIXEL_API = re.compile(r"\.(GetPixels32|GetPixels|GetRawTextureData|EncodeToPNG|EncodeToTGA|EncodeToJPG)\s*\(")

_NUMBER = re.compile(r"^\d+(?:\.\d+)?[fFdDuUlL]*$")
_CAST = re.compile(r"\((?:int|long|uint|ulong)\)")
# len は画素数（`int len = w * h;`）、nWords は 1 画素 1 ビットの語数（`(len + 63) >> 6`）を入れるこのコードの名前。
_PIXEL_NAMES = re.compile(r"\blen\b|\bnWords\b|\bLength\b")

# 見分け方が拾うが画素数の大きさでないもの。(ファイル, 式) → 理由。**理由を必ず書くこと。**
NOT_PIXEL_SIZED: dict[tuple[str, str], str] = {
    ("Code/Automation/TextureColorSummary.cs", "new int[small.Length]"):
        "長辺 AnalysisMaxSide(256) 以下に縮めた画像の画素数。原本の大きさに依らず 6.6 万以下",
    ("Code/Automation/TextureColorSummary.cs", "new int[label.Length]"):
        "label は上の縮めた画像の塊の番号。同じく 6.6 万以下",
    ("Code/Core/PixelProcessor.MixtureBand.cs", "new int[(2 * B + 1) * (2 * B + 1)]"):
        "混色帯の判定の正方形の窓(半径 B)。画素数でなく半径の 2 乗",
    ("Code/Core/PixelProcessor.cs", "new int[hist.Length]"):
        "ヒストグラムの bin の数",
    ("Code/Core/ZoneAutoTuner.Normalize.cs", "new double[SB * VB]"):
        "彩度 × 明度の 2 次元ヒストグラムの bin の数",
    ("Code/Core/ZoneAutoTuner.Normalize.cs", "new int[SB * VB]"):
        "彩度 × 明度の 2 次元ヒストグラムの bin の数",
    ("Code/UI/MaskRegionEdit.cs", "new float[strengths.Length]"):
        "ゾーンの数(strengths はゾーンごとの配列の配列)",
    ("Code/UI/PreviewView.Overlays.cs", "new Vector3[segs.Length]"):
        "重ね表示の線分の数",
}


def _strip(src: str) -> str:
    return _COMMENT.sub(" ", src)


def _split_top(expr: str, seps: str) -> list[str]:
    """括弧の外にある 1 文字の区切り（seps のどれか）で分ける。"""
    parts, depth, cur = [], 0, []
    for ch in expr:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if depth == 0 and ch in seps:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p.strip() for p in parts if p.strip()]


def _is_product_of_variables(expr: str) -> bool:
    """定数でない 2 つ以上の項の積を含むか。括弧の中もたどる。"""
    expr = _CAST.sub("", expr).replace(">>", "+").replace("<<", "+")
    for term in _split_top(expr, "+-"):
        factors = _split_top(term, "*")
        variables = [f for f in factors if not _NUMBER.match(f.split("/")[0].strip())]
        if len(variables) >= 2:
            return True
        for f in factors:
            if f.startswith("(") and f.endswith(")") and _is_product_of_variables(f[1:-1]):
                return True
    return False


def _is_pixel_sized(size: str) -> bool:
    return _is_product_of_variables(size) or bool(_PIXEL_NAMES.search(size))


def _allocations(src: str) -> list[tuple[str, bool]]:
    """(正規化した確保の式, 画素数の大きさか) を出てきた順に返す。"""
    code = _strip(src)
    out = []
    for m in _NEW_ARRAY.finditer(code):
        size = " ".join(m.group(2).split())
        out.append((f"new {m.group(1)}[{size}]", _is_pixel_sized(size)))
    for m in _UNITY_PIXEL_API.finditer(code):
        out.append((f".{m.group(1)}()", True))
    return out


def _pixel_sized_in_code() -> Counter:
    found: Counter = Counter()
    for path in sorted(CODE_DIR.rglob("*.cs")):
        rel = path.relative_to(REPO_ROOT).as_posix()
        for expr, pixel in _allocations(path.read_text(encoding="utf-8")):
            if pixel and (rel, expr) not in NOT_PIXEL_SIZED:
                found[(rel, expr)] += 1
    return found


def _allowed() -> Counter:
    doc = json.loads(ALLOWED_PATH.read_text(encoding="utf-8"))
    return Counter({(rel, expr): n for rel, exprs in doc["allowed"].items() for expr, n in exprs.items()})


def test_detector():
    """見分け方が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    src = (
        "var a = new bool[w * h];\n"              # 積
        "var b = new long[gw * gh * 4];\n"        # 定数を掛けた積
        "var c = new int[(x1 - x0 + 1) * (y1 - y0 + 1)];\n"
        "var d = new ulong[(len + 63) >> 6];\n"   # 画素数の名前（1 画素 1 ビット）
        "var e = new Color32[src.Length];\n"
        "var f = tex.GetPixels32();\n"
        "var g = new int[256];\n"                 # 定数
        "var h2 = new float[n * 3];\n"            # 定数を掛けただけ
        "var i = new int[h + 1];\n"               # 1 辺
        "var j = new int[rowOff[h]];\n"
        "var k = tex.GetRawTextureData<Color32>();\n"  # NativeArray
        "// new bool[w * h] はコメントなので数えない\n"
        "var s = \"new bool[w * h]\";\n"
    )
    got = _allocations(src)
    assert [e for e, p in got if p] == [
        "new bool[w * h]", "new long[gw * gh * 4]", "new int[(x1 - x0 + 1) * (y1 - y0 + 1)]",
        "new ulong[(len + 63) >> 6]", "new Color32[src.Length]", ".GetPixels32()"]
    assert [e for e, p in got if not p] == [
        "new int[256]", "new float[n * 3]", "new int[h + 1]", "new int[rowOff[h]]"]


def test_no_new_pixel_sized_managed_arrays():
    """一覧に無い（または一覧より多い）画素数の大きさの管理配列が無いこと。"""
    found, allowed = _pixel_sized_in_code(), _allowed()
    # 回帰の前提。パスや見分け方が壊れて 0 件になると、一覧の陳腐化の検査だけが落ちて原因が分かりにくい。
    assert sum(found.values()) >= 20, f"画素数の大きさの確保の列挙が異常です({sum(found.values())} 件)"
    over = sorted((k, n, allowed.get(k, 0)) for k, n in found.items() if n > allowed.get(k, 0))
    assert not over, (
        "画素数に比例する大きさの管理配列が増えました(ファイル, 式: 今の数 / 一覧の数):\n"
        + "\n".join(f"  {rel}: {expr}: {n} / {m}" for (rel, expr), n, m in over)
        + "\n  画素数の大きさのバッファは NativeWorkPool・NativePixels などヒープの外で持つ(計画の決まり 8)。"
        "\n  画素数の大きさでないのに拾われたなら、NOT_PIXEL_SIZED に理由つきで載せること。")


def test_allowed_list_only_shrinks():
    """一覧にあるのに無くなった・減った確保があれば、一覧も減らす（一覧が実際より大きいと、また増やせてしまう）。"""
    found, allowed = _pixel_sized_in_code(), _allowed()
    stale = sorted((k, m, found.get(k, 0)) for k, m in allowed.items() if found.get(k, 0) < m)
    assert not stale, (
        f"{ALLOWED_PATH.name} の数が実際より多い項目があります(ファイル, 式: 一覧の数 / 今の数):\n"
        + "\n".join(f"  {rel}: {expr}: {m} / {n}" for (rel, expr), m, n in stale)
        + "\n  減らした・移したなら、一覧の数を今の数へ下げる(0 なら項目ごと消す)。")


def test_exempt_folders():
    """T3 の対象外にしたフォルダ(exempt)が実在し、理由が書いてあること。対象外でも allowed で数え、増やさない。"""
    doc = json.loads(ALLOWED_PATH.read_text(encoding="utf-8"))
    for prefix, reason in doc.get("exempt", {}).items():
        assert prefix.endswith("/") and (REPO_ROOT / prefix).is_dir(), f"exempt のフォルダがありません: {prefix}"
        assert reason.strip(), f"exempt に理由がありません: {prefix}"


def test_not_pixel_sized_entries_still_exist():
    """NOT_PIXEL_SIZED の陳腐化を検出する（該当の確保が消えたら外す）。"""
    present = set()
    for path in sorted(CODE_DIR.rglob("*.cs")):
        rel = path.relative_to(REPO_ROOT).as_posix()
        present |= {(rel, e) for e, p in _allocations(path.read_text(encoding="utf-8")) if p}
    stale = sorted(set(NOT_PIXEL_SIZED) - present)
    assert not stale, f"NOT_PIXEL_SIZED に実在しない項目があります: {stale}"
