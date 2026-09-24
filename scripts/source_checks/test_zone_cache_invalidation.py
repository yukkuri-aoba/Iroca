"""`ColorZone` の事前計算キャッシュが、入力にしたゾーン設定の変更を必ず検知することを検査する。

`UpdateCacheIfNeeded()` は「追跡している値が全部同じなら早期 return」する。追跡リストに
載せ忘れたフィールドがあると、**値は変えたのに派生値だけ古い**という状態でマッチングが走る。

実際 `chromaThreshold` が抜けていた(レビュー 2026-08-06 の §4 高)。これは
`BuildSampleCache` が `SampleCache.chromaConfidence` を導出する入力であり、
`chromaConfidence` は `GetMatchScores` の RGB/HSV 距離ブレンドと彩度ゲートを直接支配する。
発覚時点では実害が出ていなかったが、それは照合経路が全て `Clone()` か新規生成で、
`Clone()` が `_cacheInitiated=false` にする(= 毎回作り直す)ためにすぎない。
`ResetTuningToDefault()` のようにライブインスタンスを in-place で書き換える経路は既にあり、
将来 `Clone()` を 1 つ外した時点で顕在化する。

検査の考え方: キャッシュ導出コード(`BuildSampleCache` の本体と、`UpdateCacheIfNeeded` の
`_cacheInitiated = true;` より後ろ = ゾーン共通の派生値)が読むゾーンフィールドは、
そのまま「変更を検知しなければならないフィールド」の集合になる。それが早期 return の
条件に全部載っていることを確かめる。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
ZONE_CS = REPO_ROOT / "Code" / "Core" / "ColorZone.cs"
MATCH_CS = REPO_ROOT / "Code" / "Core" / "ColorZone.Match.cs"

# 行コメント / ブロックコメント / 文字列リテラルを落とす。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)

# クラス直下のインスタンスフィールド宣言。const / static / readonly は
# ユーザーが動かせないので対象外(定数は PascalCase、フィールドは camelCase)。
_FIELD_DECL = re.compile(
    r"^\s*public\s+(?!const\b|static\b|readonly\b)"
    r"[A-Za-z_][A-Za-z0-9_<>\[\], .]*?\s+"
    r"([a-z][A-Za-z0-9_]*)\s*(?:=[^;]*)?;",
    re.MULTILINE,
)

# ネストした型(SampleCache 等)の本体。中のフィールドを外側と混同しないため落とす。
_NESTED_TYPE = re.compile(r"\b(?:struct|enum)\s+\w+\s*\{")


def _strip(src: str) -> str:
    return _COMMENT.sub(" ", src)


def _match(src: str, open_idx: int, opener: str = "{", closer: str = "}") -> int:
    """src[open_idx] == opener に対応する closer の位置を返す。"""
    assert src[open_idx] == opener, src[open_idx - 40:open_idx + 40]
    depth = 0
    for i in range(open_idx, len(src)):
        if src[i] == opener:
            depth += 1
        elif src[i] == closer:
            depth -= 1
            if depth == 0:
                return i
    raise AssertionError(f"対応する '{closer}' が見つかりません")


def _match_brace(src: str, open_idx: int) -> int:
    return _match(src, open_idx)


def _strip_nested_types(src: str) -> str:
    out = src
    while True:
        m = _NESTED_TYPE.search(out)
        if not m:
            return out
        out = out[:m.start()] + out[_match_brace(out, m.end() - 1) + 1:]


def _method_body(src: str, signature: str) -> str:
    """`signature` で始まるメソッドの本体(波括弧の中身)を返す。"""
    i = src.index(signature)
    open_idx = src.index("{", i + len(signature))
    return src[open_idx + 1:_match_brace(src, open_idx)]


@pytest.fixture(scope="module")
def zone_fields() -> set[str]:
    """`ColorZone` のインスタンスフィールド名。"""
    src = _strip_nested_types(_strip(ZONE_CS.read_text(encoding="utf-8")))
    fields = set(_FIELD_DECL.findall(src))
    # 抽出が壊れて空集合になると、以降の assert が空一致で全部通ってしまう。
    assert len(fields) >= 20, f"フィールド抽出が異常です({len(fields)} 件): {sorted(fields)}"
    for expected in ("tolerance", "chromaThreshold", "saturationGuard", "sampleColor"):
        assert expected in fields, f"{expected} が抽出できていません: {sorted(fields)}"
    return fields


@pytest.fixture(scope="module")
def cache_parts() -> tuple[str, str, str]:
    """(早期 return の条件式, スナップショット代入部, キャッシュ導出部) を返す。

    条件式は `if (...)` の中身**だけ**を取る。直後の代入ブロック
    (`_cChromaThreshold = chromaThreshold;` 等)まで含めると、条件に載せ忘れていても
    「監視している」と数えてしまい、検出したい不整合をそのまま見逃す。
    """
    src = _strip(MATCH_CS.read_text(encoding="utf-8"))
    body = _method_body(src, "public void UpdateCacheIfNeeded()")

    m = re.search(r"\bif\s*\(", body)
    assert m, "UpdateCacheIfNeeded の構造が変わっています(早期 return の if が無い)"
    cond_open = body.index("(", m.end() - 1)
    cond_close = _match(body, cond_open, "(", ")")
    condition = body[cond_open + 1:cond_close]
    assert "_cacheInitiated" in condition, (
        f"最初の if が早期 return ではありません: {condition[:120]!r}")

    marker = "_cacheInitiated = true;"
    assert marker in body, "UpdateCacheIfNeeded の構造が変わっています(_cacheInitiated の代入が無い)"
    head, after = body.split(marker, 1)
    assigns = head[_match_brace(body, body.index("{", cond_close)) + 1:]

    derive = after + _method_body(src, "private SampleCache BuildSampleCache(")
    assert "chromaConfidence" in derive, "BuildSampleCache の本体を取れていません"
    return condition, assigns, derive


def _referenced(text: str, fields: set[str]) -> set[str]:
    """`text` が素の識別子として読んでいるゾーンフィールド(`sc.satRamp` 等は除く)。"""
    return {f for f in fields if re.search(rf"(?<![\w.]){re.escape(f)}\b", text)}


def _watched(condition: str, zone_fields: set[str]) -> set[str]:
    watched = _referenced(condition, zone_fields)
    # extraSamples は個数・各色の比較を ExtraSamplesChanged() に切り出してある。
    if "ExtraSamplesChanged" in condition:
        watched.add("extraSamples")
    return watched


def test_cache_inputs_are_all_watched(zone_fields, cache_parts):
    """キャッシュ導出が読むフィールドは、全て早期 return の条件に載っていること。"""
    condition, _assigns, derive = cache_parts
    missing = sorted(_referenced(derive, zone_fields) - _watched(condition, zone_fields))
    assert not missing, (
        "キャッシュの入力なのに変更検知されていないフィールドがあります: "
        + ", ".join(missing)
        + "\n  UpdateCacheIfNeeded() は追跡値が全て同じなら早期 return するため、"
        "\n  これらを変えても派生値(SampleCache.chromaConfidence 等)が古いまま使われる。"
        "\n  _c<名前> フィールドを足して、条件と代入の両方に載せること。")


def test_watched_fields_are_actually_cache_inputs(zone_fields, cache_parts):
    """逆向き: 導出に使われていないフィールドを見張っていないこと(陳腐化の検出)。"""
    condition, _assigns, derive = cache_parts
    stale = sorted(_watched(condition, zone_fields) - _referenced(derive, zone_fields))
    assert not stale, (
        "キャッシュ導出に使われていないのに変更検知しているフィールドがあります: "
        + ", ".join(stale)
        + "\n  導出側から参照が消えたなら、無駄なキャッシュ再構築を招くので条件からも外すこと。")


def test_watched_fields_are_snapshotted(cache_parts):
    """条件で比較する `_c<名前>` は、必ず代入もされていること。

    代入を忘れると比較値が初期値のまま固定され、条件が永久に不成立 = 毎回フルに
    キャッシュを作り直す(出力は正しいが、無効化の意味が消えて静かに遅くなる)。
    """
    condition, assigns, _derive = cache_parts
    compared = set(re.findall(r"(_c[A-Z]\w*)\s*==", condition))
    assert compared, f"条件から _c 変数を抽出できません: {condition[:200]!r}"
    assigned = set(re.findall(r"(_c[A-Z]\w*)\s*=(?!=)", assigns))
    missing = sorted(compared - assigned)
    assert not missing, (
        "条件で比較しているのに代入されていないキャッシュ変数があります: "
        + ", ".join(missing)
        + "\n  比較値が更新されないため早期 return が永久に効かず、毎回作り直しになる。")
