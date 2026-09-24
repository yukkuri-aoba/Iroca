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

を固定する。ハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
TUNER_CS = REPO_ROOT / "Code" / "Core" / "ZoneAutoTuner.cs"
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
    r"|valueBlend|edgeSoftness|shadowDesaturation|shadowForgivenessSatMin|shadowValueFloor|chromaCeiling|extraSamples"
    r"|sampleColor|antiAliasCleanup|useDecontamination)\s*=\s*(?:result|tune|r)\.")


@pytest.mark.parametrize("path", [UI_CS, HARNESS_CS], ids=["ui", "harness"])
def test_callers_use_apply_to_only(path: Path):
    src = _strip(path.read_text(encoding="utf-8"))
    assert re.search(r"\.ApplyTo\s*\(", src), f"{path.name} が TuneResult.ApplyTo を呼んでいない"
    direct = [m.group(0) for m in _DIRECT_ASSIGN.finditer(src)]
    assert not direct, (
        f"{path.name} に導出結果の直接代入が残っている(ApplyTo に一本化すること): {direct}")
