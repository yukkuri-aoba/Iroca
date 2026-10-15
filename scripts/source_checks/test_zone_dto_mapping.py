"""zones JSON のゾーン設定（ZoneDto）が、ColorZone へ漏れなく写されていることを検査する。

ゾーン設定は次の 3 か所で ColorZone と行き来する:

  Code/Automation/IrocaAutomation.cs    BuildZone(ZoneDto)  … MCP・batchmode の入口
  scripts/headless-run/Harness.cs       BuildZone(ZoneCfg)  … テストが測るハーネスの入口
  Code/Debug/ReproDump.cs               ToDto(ColorZone)    … 現場の再現データ書き出し

JsonUtility は未知のフィールドを黙って捨てる。ZoneDto にフィールドを足して BuildZone へ
写し忘れると、zones JSON に書いた設定が MCP / batchmode でだけ黙って効かない。製品と
ハーネスの BuildZone は「同一の写像」とコメントでしか担保されておらず、片側だけ直すと
テストが製品でない設定を測る（過去の useFloodFill の乖離と同じ型）。ここで次を機械検査する:

  1. 製品の BuildZone が読む z.X の集合が ZoneDto のフィールド集合と一致する
  2. ハーネスの BuildZone が ZoneDto の全フィールドを読む（ハーネス専用の入力は余分に読んでよい）
  3. 両 BuildZone の代入先（ColorZone のフィールド）の差が、理由つきで許した分だけ
  4. ReproDump.ToDto が ZoneDto の全フィールドを書く

フィールド集合と既定値の一致（ZoneDto と ZoneCfg）は dev_safe 側の
test_zones_schema_parity.py が見ている。C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
AUTOMATION_CS = REPO_ROOT / "Code" / "Automation" / "IrocaAutomation.cs"
HARNESS_CS = REPO_ROOT / "scripts" / "headless-run" / "Harness.cs"
REPRO_CS = REPO_ROOT / "Code" / "Debug" / "ReproDump.cs"

# 製品とハーネスの BuildZone で、片側だけが代入してよい ColorZone のフィールド。ここに足すときは理由を書く。
TARGET_DIFF_ALLOWED = {
    # 製品だけが立てる。UI の手順案内と自動調整の可否判定にだけ使い、選択・再着色は参照しない
    # （ColorZone.cs のコメント）。ハーネスは出力だけを測るので立てなくてよい。
    "sampleColorSet",
    "targetColorSet",
    # ハーネスだけが写す。入力の ZoneCfg.samples（float[][]）は JsonUtility が扱えず、
    # 製品の ZoneDto には対応が無い（Harness.cs の「ハーネス専用フィールド」）。
    "extraSamples",
}

_JSONUTILITY_NOTE = (
    "JsonUtility は未知のフィールドを黙って捨てるので、ZoneDto のフィールドを BuildZone へ"
    "写し忘れると、zones JSON に書いた設定が MCP / batchmode で黙って効かない。")

# コメントと文字列リテラルを落とす（文字列中の波括弧やコメント記号で対応が狂わないように）。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)


def _src(path: Path) -> str:
    return _COMMENT.sub("", path.read_text(encoding="utf-8"))


def _block_after(src: str, pattern: str, what: str) -> str:
    """pattern に一致した位置の直後にある { ... } の中身を返す（波括弧の対応で切り出す）。"""
    m = re.search(pattern, src)
    assert m, f"{what} が見つかりません: {pattern}"
    start = src.index("{", m.end() - 1)
    depth, i = 1, start + 1
    while depth:
        depth += (src[i] == "{") - (src[i] == "}")
        i += 1
    return src[start + 1:i - 1]


def _top_level(body: str) -> str:
    """クラス本体のうちメンバ宣言の階層だけを残す（メソッド本体などネストした {} を落とす）。"""
    out, depth = [], 0
    for c in body:
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif depth == 0:
            out.append(c)
    return "".join(out)


def _initializer_targets(body: str, type_pattern: str, what: str) -> set[str]:
    """`new T { a = ..., b = ... }` のオブジェクト初期化子で代入しているメンバ名の集合。"""
    init = _block_after(body, rf"new\s+{type_pattern}\s*(?:\(\s*\))?\s*\{{", what)
    segments, depth, cur = [], 0, []
    for c in init:
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            segments.append("".join(cur))
            cur = []
        else:
            cur.append(c)
    segments.append("".join(cur))
    targets = set()
    for seg in segments:
        m = re.match(r"\s*(\w+)\s*=(?![=>])", seg)
        if m:
            targets.add(m.group(1))
    return targets


def _dto_fields() -> set[str]:
    decl = _top_level(_block_after(_src(AUTOMATION_CS), r"\bclass\s+ZoneDto\b[^{]*\{", "ZoneDto"))
    return set(re.findall(r"public\s+[\w.\[\]]+\s+(\w+)\s*=", decl))


def _product_build_zone() -> str:
    return _block_after(_src(AUTOMATION_CS),
                        r"static\s+ColorZone\s+BuildZone\s*\(\s*ZoneDto\b[^)]*\)\s*\{",
                        "IrocaAutomation.BuildZone(ZoneDto)")


def _harness_build_zone() -> str:
    return _block_after(_src(HARNESS_CS),
                        r"static\s+ColorZone\s+BuildZone\s*\(\s*ZoneCfg\b[^)]*\)\s*\{",
                        "Harness.BuildZone(ZoneCfg)")


def _repro_to_dto() -> str:
    return _block_after(_src(REPRO_CS),
                        r"ZoneDto\s+ToDto\s*\(\s*ColorZone\b[^)]*\)\s*\{",
                        "ReproDump.ToDto(ColorZone)")


def _reads(body: str) -> set[str]:
    return set(re.findall(r"\bz\.(\w+)", body))


def test_zone_dto_has_fields():
    """抽出が空になると以降の一致検査が空集合どうしで通ってしまうので、件数の下限を先に見る。"""
    fields = _dto_fields()
    assert len(fields) >= 20, f"ZoneDto のフィールドを読めていません: {sorted(fields)}"


def test_product_build_zone_reads_every_dto_field():
    dto = _dto_fields()
    reads = _reads(_product_build_zone())
    assert reads == dto, (
        "IrocaAutomation.BuildZone が読むフィールドと ZoneDto のフィールドが一致しません。\n"
        f"  BuildZone が読んでいない: {sorted(dto - reads)}\n"
        f"  ZoneDto に無いのに読んでいる: {sorted(reads - dto)}\n  " + _JSONUTILITY_NOTE)


def test_harness_build_zone_reads_every_dto_field():
    dto = _dto_fields()
    reads = _reads(_harness_build_zone())
    assert dto <= reads, (
        f"Harness.BuildZone が ZoneDto のフィールドを読んでいません: {sorted(dto - reads)}\n"
        "  製品とハーネスで写像が食い違うと、テストが製品でない設定を測る。")


def test_build_zone_targets_match_between_product_and_harness():
    product = _initializer_targets(_product_build_zone(), r"ColorZone", "製品 BuildZone の new ColorZone")
    harness = _initializer_targets(_harness_build_zone(), r"ColorZone", "ハーネス BuildZone の new ColorZone")
    n = len(_dto_fields())
    assert len(product) >= n and len(harness) >= n, (
        f"BuildZone の代入先を読めていません: 製品 {sorted(product)} / ハーネス {sorted(harness)}")
    diff = product ^ harness
    assert diff == TARGET_DIFF_ALLOWED, (
        "製品とハーネスの BuildZone で、ColorZone へ代入するフィールドが食い違っています。\n"
        f"  製品だけが代入: {sorted(product - harness)}\n"
        f"  ハーネスだけが代入: {sorted(harness - product)}\n"
        f"  許している差: {sorted(TARGET_DIFF_ALLOWED)}\n"
        "  片側だけに足したなら両方へ足すこと。意図した差なら TARGET_DIFF_ALLOWED に理由つきで加えること。")


def test_repro_dump_writes_every_dto_field():
    dto = _dto_fields()
    writes = _initializer_targets(_repro_to_dto(), r"IrocaAutomation\.ZoneDto", "ReproDump.ToDto の new ZoneDto")
    assert writes == dto, (
        "ReproDump.ToDto が書くフィールドと ZoneDto のフィールドが一致しません。\n"
        f"  ToDto が書いていない: {sorted(dto - writes)}\n"
        f"  ZoneDto に無いのに書いている: {sorted(writes - dto)}\n"
        "  書き忘れると、再現データを読み込んだときにその設定だけ既定値に戻る。")
