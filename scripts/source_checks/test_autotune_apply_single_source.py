"""自動調整の「導出結果 → ゾーン適用」が単一の写像(TuneResult.ApplyTo)を通ることを検査する。

2026-09-02 のレビューで、製品 UI(Code/UI/IrocaWindow.AutoTune.cs の apply)と headless
ハーネス(scripts/headless-run/Harness.cs の --autotune)が **同じ 11 項目を手書きで並べる
二重実装** になっていた。UI はハーネスのコンパイル対象外なので、片方に項目を足し忘れても
どのゲートにも掛からず、「テストが測る自動調整」と「ユーザーが得る自動調整」が黙って別物になる。

対策として写像を Core の `ZoneAutoTuner.TuneResult.ApplyTo(ColorZone, IrocaSessionState)` に
一本化した(ハーネスのコンパイル対象 = 出荷ゲート対象)。本テストは C# ソースを直接読み、

  1. UI とハーネスの両方が ApplyTo を呼び、TuneResult のフィールドをゾーンへ直接代入していない
  2. ApplyTo 本体が TuneResult の「ゾーンへ写すべきフィールド」を全部代入している
     (TuneResult に per-zone フィールドを足して ApplyTo に書き忘れたら落ちる)
  3. ApplyTo のほかにゾーン単位の項目(tolerance … chromaCeiling)を手書きで並べている 8 か所
     (既定値・解析結果の初期化子、上書き確認のラベル、既定判定、閉ループ検証の模擬ゾーン、
     UI の手調整判定、ハーネスの AUTOTUNE 出力、詳細設定のリセット)が全項目を並べている
     (並べないのが意図の項目は理由つきの許可リストで明示し、許可リストの陳腐化も検出する)

を固定する。ハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
TUNER_CS = REPO_ROOT / "Code" / "Core" / "ZoneAutoTuner.cs"
VERIFY_CS = REPO_ROOT / "Code" / "Core" / "ZoneAutoTuner.Verify.cs"
COLORZONE_CS = REPO_ROOT / "Code" / "Core" / "ColorZone.cs"
UI_CS = REPO_ROOT / "Code" / "UI" / "IrocaWindow.AutoTune.cs"
HARNESS_CS = REPO_ROOT / "scripts" / "headless-run" / "Harness.cs"

_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)

# TuneResult のフィールドのうち、ゾーン/セッションへ写す先が名前どおりでないもの、または
# 写さないもの(診断・UI 表示用)。ここに無いフィールドは `zone.<名前> = <名前>;` で写す契約。
FIELD_MAP = {
    "normalizedSample": "sampleColor",        # hasNormalizedSample が真のときだけ
    "hasNormalizedSample": None,              # 適用条件(値ではない)
    "autoSamples": "extraSamples",
    "applyGlobals": None,                     # globals の適用条件
    "antiAliasCleanup": "session.antiAliasCleanup",
    "useDecontamination": "session.useDecontamination",
    "overwrittenLabels": None,                # UI の確認ダイアログ用
    "evidenceDiag": None,                     # 診断文字列
}

_FIELD_DECL = re.compile(
    r"^\s*public\s+(?!const\b|static\b|readonly\b)[A-Za-z_][A-Za-z0-9_<>\[\], .]*?\s+"
    r"([a-zA-Z_][A-Za-z0-9_]*)\s*(?:=[^;]*)?;", re.M)


def _strip(text: str) -> str:
    return _COMMENT.sub("", text)


def _block(text: str, header_regex: str) -> str:
    """header_regex にマッチした位置から始まる最初の {...} ブロック本体を返す。"""
    m = re.search(header_regex, text)
    assert m, f"見つからない: {header_regex}"
    i = text.index("{", m.end())
    depth = 0
    for j in range(i, len(text)):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                return text[i + 1:j]
    raise AssertionError("ブロックが閉じていない")


@pytest.fixture(scope="module")
def tuner_src() -> str:
    return _strip(TUNER_CS.read_text(encoding="utf-8"))


def test_apply_to_covers_every_tune_result_field(tuner_src):
    struct_body = _block(tuner_src, r"public\s+struct\s+TuneResult\b")
    fields = [m.group(1) for m in _FIELD_DECL.finditer(struct_body)]
    assert "tolerance" in fields and "autoSamples" in fields, f"TuneResult の解析に失敗: {fields}"
    apply_body = _block(struct_body, r"public\s+void\s+ApplyTo\s*\(")
    missing = []
    for f in fields:
        target = FIELD_MAP.get(f, f)
        if target is None:
            continue
        # 右辺は「そのフィールドで始まる式」を許す(例: autoSamples ?? new List<Color>())。
        if target.startswith("session."):
            pat = rf"{re.escape(target)}\s*=\s*{re.escape(f)}\b"
        else:
            pat = rf"zone\.{re.escape(target)}\s*=\s*{re.escape(f)}\b"
        if not re.search(pat, apply_body):
            missing.append(f"{f} → {target}")
    assert not missing, (
        "TuneResult のフィールドが ApplyTo で適用されていない(足したら ApplyTo にも書く。"
        "写さないなら FIELD_MAP に None で登録):\n  " + "\n  ".join(missing))


_DIRECT_ASSIGN = re.compile(
    r"\.(?:tolerance|saturationStrictness|saturationGuard|chromaThreshold|highlightRecovery"
    r"|valueBlend|edgeSoftness|shadowDesaturation|shadowForgivenessSatMin|shadowValueFloor|partSatCeiling|partHueBand|chromaCeiling|extraSamples"
    r"|sampleColor|antiAliasCleanup|useDecontamination)\s*=\s*(?:result|tune|r)\.")


@pytest.mark.parametrize("path", [UI_CS, HARNESS_CS], ids=["ui", "harness"])
def test_callers_use_apply_to_only(path: Path):
    src = _strip(path.read_text(encoding="utf-8"))
    assert re.search(r"\.ApplyTo\s*\(", src), f"{path.name} が TuneResult.ApplyTo を呼んでいない"
    direct = [m.group(0) for m in _DIRECT_ASSIGN.finditer(src)]
    assert not direct, (
        f"{path.name} に導出結果の直接代入が残っている(ApplyTo に一本化すること): {direct}")


# ─────────────────── ゾーン単位の項目の列挙箇所 ───────────────────
# ゾーン単位の項目は ApplyTo のほかにも 8 か所で 1 つずつ手書きされている。項目を足して
# 1 か所でも書き忘れると、上書き確認に出ない・グローバル提案の判定が壊れる・ハーネスの
# JSON に出ず回帰で測れない、といった漏れがどのゲートにも掛からずに入る。


def _nested_block(path: Path, *headers: str) -> str:
    """path を _strip し、headers の順に入れ子のブロック本体をたどって返す。"""
    body = _strip(path.read_text(encoding="utf-8"))
    for header in headers:
        body = _block(body, header)
    return body


def _harness_autotune_block() -> str:
    # _strip は文字列リテラルも消すので、"AUTOTUNE " の位置は素のテキストで取ってから strip する。
    # 切り出しをリテラルの開き引用符から始めるので、strip で引用符の対応はずれない。
    raw = HARNESS_CS.read_text(encoding="utf-8")
    marker = '"AUTOTUNE "'
    assert raw.count(marker) == 1, f"Harness.cs の {marker} 出力が 1 か所に定まらない"
    return _block(_strip(raw[raw.index(marker):]), r"\bnew\b")


# (id, 場所, ブロックの取り出し, 各項目が満たすべき正規表現({f} は項目名), 並べない項目 → 理由)。
# 並べないのが意図の項目は理由つきで載せる。載せた項目が実際には並んでいたら陳腐化として落とす。
_SITES = [
    ("heuristic-default", "ZoneAutoTuner.BuildHeuristicDefault の new TuneResult",
     lambda: _nested_block(TUNER_CS, r"TuneResult\s+BuildHeuristicDefault\s*\(", r"new\s+TuneResult\b"),
     (r"\b{f}\s*=(?!=)",), {}),
    # 本体全体だとローカル変数への代入にも一致するので、return の初期化子だけを見る。
    ("merge-analyzed", "ZoneAutoTuner.MergeAnalyzed の return new TuneResult",
     lambda: _nested_block(TUNER_CS, r"TuneResult\s+MergeAnalyzed\s*\(", r"return\s+new\s+TuneResult\b"),
     (r"\b{f}\s*=(?!=)",), {}),
    ("overwritten-labels", "ZoneAutoTuner.AddZoneOverwrittenLabels",
     lambda: _nested_block(TUNER_CS, r"void\s+AddZoneOverwrittenLabels\s*\("),
     (r"\bzone\.{f}\b",), {}),
    ("basics-at-default", "ZoneAutoTuner.IsZoneBasicsAtDefault",
     lambda: _nested_block(TUNER_CS, r"bool\s+IsZoneBasicsAtDefault\s*\("),
     (r"\bz\.{f}\b",), {}),
    ("sim-zone", "ZoneAutoTuner.Verify.cs の BuildSimZone",
     lambda: _nested_block(VERIFY_CS, r"ColorZone\s+BuildSimZone\s*\("),
     (r"\bsim\.{f}\s*=(?!=)",),
     {"valueBlend": "再着色の段だけで使い、閉ループ検証が測る選択には効かない",
      "shadowDesaturation": "再着色の段だけで使い、閉ループ検証が測る選択には効かない"}),
    ("ui-values-intact", "IrocaWindow.AutoTune.cs の AutoTuneValuesIntact",
     lambda: _nested_block(UI_CS, r"bool\s+AutoTuneValuesIntact\s*\("),
     (r"\bzone\.{f}\b", r"\br\.{f}\b"), {}),
    ("harness-json", "Harness.cs の AUTOTUNE 出力(JSON)",
     _harness_autotune_block,
     (r"\b{f}\s*=\s*z\.{f}\b",), {}),
    ("reset-tuning", "ColorZone.ResetTuningToDefault",
     lambda: _nested_block(COLORZONE_CS, r"void\s+ResetTuningToDefault\s*\("),
     (r"\b{f}\s*=\s*d\.{f}\b",),
     {"tolerance": "許容範囲はユーザーの明示的な選択として保持する(doc コメントのとおり)"}),
]


def _matches(block: str, field: str, patterns) -> list[bool]:
    return [bool(re.search(p.replace("{f}", re.escape(field)), block)) for p in patterns]


@pytest.fixture(scope="module")
def per_zone_fields(tuner_src) -> list[str]:
    """TuneResult のフィールドのうち、FIELD_MAP で名前どおりにゾーンへ写るもの。"""
    struct_body = _block(tuner_src, r"public\s+struct\s+TuneResult\b")
    fields = [m.group(1) for m in _FIELD_DECL.finditer(struct_body)]
    per_zone = [f for f in fields if FIELD_MAP.get(f, f) == f]
    # 解析が空振りして全箇所が素通りしないよう、件数の下限を置く(tolerance … chromaCeiling の 13 項目)。
    assert len(per_zone) >= 13, f"TuneResult のゾーン単位の項目の解析に失敗: {per_zone}"
    return per_zone


@pytest.mark.parametrize("site", _SITES, ids=[s[0] for s in _SITES])
def test_per_zone_fields_listed_at_every_site(site, per_zone_fields):
    _, where, get_block, patterns, allowed = site
    block = get_block()
    missing = [f for f in per_zone_fields if f not in allowed and not all(_matches(block, f, patterns))]
    assert not missing, (
        f"{where} にゾーン単位の項目が並んでいない(TuneResult に項目を足したらここにも書く。"
        f"並べないのが意図なら _SITES の許可リストへ理由つきで載せる): {missing}")


@pytest.mark.parametrize("site", [s for s in _SITES if s[4]], ids=[s[0] for s in _SITES if s[4]])
def test_site_allowlists_are_not_stale(site, per_zone_fields):
    _, where, get_block, patterns, allowed = site
    unknown = sorted(set(allowed) - set(per_zone_fields))
    assert not unknown, f"{where} の許可リストに TuneResult のゾーン単位の項目でない名前がある: {unknown}"
    block = get_block()
    stale = [f for f in sorted(allowed) if any(_matches(block, f, patterns))]
    assert not stale, f"{where} の許可リストが陳腐化している(実際には並んでいるので外す): {stale}"
