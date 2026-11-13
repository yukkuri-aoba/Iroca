"""ゾーンの照合(`ColorZone.Matcher`)が「作ってから書き換えないスナップショット」であることを検査する。

以前は、照合が導く値(サンプルごとの定数・強度のレンジ)を `ColorZone` 自身が `[NonSerialized]` のキャッシュとして持ち、
使う前に `UpdateCacheIfNeeded()` を呼ぶ決まりだった。無効化の条件に載せ忘れた欄があると、**値は変えたのに派生値だけ古い**
状態で照合が走る(実際 `chromaThreshold` が抜けていた。レビュー 2026-08-06 の §4 高)。この検査は、その載せ忘れを見張っていた
(旧 `test_zone_cache_invalidation.py`)。

2026-11-13 に照合を `Matcher` へ移してキャッシュそのものを無くしたので、見張るのは次の 3 つに変わった:

  1. `ColorZone` に `[NonSerialized]` の欄が無い(導いた値をゾーンに持たせない。ゾーンは設定だけ。UI の開閉だけは下の一覧で認める)。
  2. `Matcher` のインスタンスの欄は全部 `readonly`(作った後に書き換えない。並列のループからそのまま呼べる)。
  3. `Matcher` は、コンストラクタの外でゾーンを読まない(ゾーンの設定を変えた後は、作り直すしかない形にする)。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
ZONE_CS = REPO_ROOT / "Code" / "Core" / "ColorZone.cs"
# 照合は選択の一部(Selection に置く)。
MATCH_CS = REPO_ROOT / "Code" / "Core" / "Selection" / "ColorZone.Match.cs"

# 行コメント / ブロックコメント / 文字列リテラルを落とす。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)


def _strip(src: str) -> str:
    return _COMMENT.sub(" ", src)


def _match_brace(src: str, open_idx: int) -> int:
    """src[open_idx] == '{' に対応する '}' の位置を返す。"""
    assert src[open_idx] == "{", src[open_idx - 40:open_idx + 40]
    depth = 0
    for i in range(open_idx, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    raise AssertionError("対応する '}' が見つかりません")


def _matcher_body() -> str:
    """`Matcher` クラスの本体(波括弧の中身。コメントと文字列を落としたもの)。"""
    src = _strip(MATCH_CS.read_text(encoding="utf-8-sig"))
    m = re.search(r"\binternal\s+sealed\s+class\s+Matcher\s*\{", src)
    assert m, "ColorZone.Matcher が見つかりません(名前・修飾子を変えたなら、この検査も直す)"
    open_idx = m.end() - 1
    return src[open_idx + 1:_match_brace(src, open_idx)]


def _top_level(body: str) -> str:
    """クラス本体のうち、入れ子(メソッド・struct)の中身を落としたもの(= 欄とメソッドの宣言だけが残る)。"""
    out, depth = [], 0
    for ch in body:
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
        elif depth == 0:
            out.append(ch)
    return "".join(out)


# ゾーンに置いてよい、保存しない欄(理由つき)。照合や選択が導いた値は、ここに足さない。
ALLOWED_NONSERIALIZED = {
    "detailFoldout": "UI の詳細設定の開閉(編集データ・プリセットに残さない)",
}


def test_zone_holds_no_nonserialized_state():
    """ゾーンに `[NonSerialized]` の欄(保存されない、導いた値や一時の旗)が無いこと。"""
    hits = []
    for path in (ZONE_CS, MATCH_CS):
        src = _strip(path.read_text(encoding="utf-8-sig"))
        for m in re.finditer(r"\[(?:System\.)?NonSerialized\]([^;]*);", src):
            name = re.split(r"\s*=", m.group(1).strip())[0].split()[-1]
            if name not in ALLOWED_NONSERIALIZED:
                hits.append(f"{path.name}: {' '.join(m.group(0).split())}")
    assert not hits, (
        "ColorZone に [NonSerialized] の欄があります:\n  " + "\n  ".join(hits)
        + "\n→ 照合が導く値は ColorZone.Matcher(作ったときの設定の写し)が持ちます。ゾーンに持たせると、設定を変えたのに"
        "\n  導いた値だけ古い、という状態を別の仕組みで守ることになります。")


def test_matcher_fields_are_all_readonly():
    """`Matcher` の欄は全部 `readonly`(か `const`)であること。"""
    decls = [" ".join(d.split()) for d in _top_level(_matcher_body()).split(";")]
    # 欄の宣言 = かっこの無い宣言(メソッドの頭は ( を含む。struct の頭は本体を落とした後に `private struct SampleCache` が残る)。
    fields = [d for d in decls if d and "(" not in d]
    fields = [re.sub(r"^(?:private struct \w+\s+)+", "", d) for d in fields]
    fields = [d for d in fields if d]
    assert len(fields) >= 8, f"Matcher の欄を拾えていません({len(fields)} 件): {fields}"
    bad = [d for d in fields if not re.search(r"\b(?:readonly|const)\b", d)]
    assert not bad, (
        "Matcher に書き換えられる欄があります:\n  " + "\n  ".join(bad)
        + "\n→ Matcher は作ってから書き換えないスナップショットです(並列のループが同時に読む)。readonly にしてください。")


def test_matcher_reads_the_zone_only_in_its_constructor():
    """コンストラクタの外では、ゾーンを読まないこと。"""
    body = _matcher_body()
    m = re.search(r"\binternal\s+Matcher\s*\(\s*ColorZone\s+zone\b[^)]*\)\s*\{", body)
    assert m, "Matcher のコンストラクタ(ColorZone zone, ...)が見つかりません"
    open_idx = m.end() - 1
    ctor = body[open_idx + 1:_match_brace(body, open_idx)]
    assert re.search(r"\bzone\.tolerance\b", ctor) and re.search(r"\bzone\.extraSamples\b", ctor), (
        "コンストラクタの本体を取れていません")
    rest = body[:m.start()] + body[_match_brace(body, open_idx) + 1:]
    hits = sorted(set(re.findall(r"\bzone\b[.\w]*", rest)))
    assert not hits, (
        "Matcher がコンストラクタの外でゾーンを読んでいます: " + ", ".join(hits)
        + "\n→ ゾーンの設定は、コンストラクタで写しを取ってから使います(後から変えられても照合は変わらない)。")
