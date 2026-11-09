"""選択キャッシュキー(BuildSelectionKey)の網羅性監査テスト。

PixelProcessor の選択キャッシュは「選択に影響する ColorZone フィールド」を手動列挙した
キー(BuildSelectionKey)で判定する。フィールドを追加してキーへの反映を忘れると、
キャッシュが誤ヒットして**古い選択のままのプレビュー**になる（Export はキャッシュ
不使用のため最終出力では気づけず、発見が遅れる）。

本テストは Harness の --selkey-audit で ColorZone の全 public フィールドを 1 つずつ
摂動し、キーが変化するかを機械検査する:

  - キーが変化しないフィールドは SELECTION_IRRELEVANT（選択に影響しないと明示分類済み）
    に載っていなければ fail → 新フィールドは既定で fail し、「キーに入れる」か
    「無関係と分類する」かの判断を強制する。
  - SELECTION_IRRELEVANT の記載が実在フィールドと食い違えば fail（台帳の陳腐化防止）。
  - キー側に「無関係」フィールドが入っているのは安全側（ミスヒットが増えるだけ）なので
    許容する（BuildSelectionKey の設計コメントと同じ方針）。
  - そのものは鍵に入れず、鍵に入れた派生の値（bool など）を通して選択に効くフィールドは
    SELECTION_VIA_DERIVED に分け、派生の値ごとの `SELKEY-DERIVED <名前> 1`（その値が変わる 2 つの
    ゾーンで鍵が違う）を要求する。1 つずつ動かす監査だけだと、派生の値を鍵から消しても気づけない
    （2026-11-10 まで塗り先の色と模様保持を「選択に無関係」と誤って分類していた。点検 B-7③）。

実行: pytest scripts/golden/test_selection_key_audit.py -q
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402

# 選択（マッチ→マスク再適用）に影響しないと分類済みのフィールド。
# ここに追加するときは「なぜ選択に影響しないか」を横に書くこと。
SELECTION_IRRELEVANT = {
    "name",            # 表示名のみ
    "id",              # 同一性識別のみ
    "outputSaturation",  # 再着色の出力彩度のみ
    "shadowDesaturation",  # 再着色の暗部脱彩のみ
    "applyHighlightWash",  # 再着色のハイライト合成のみ
    "autoHighlightSample",  # wash 用サンプル補正のみ(match/base 不変)
    "autoRecolorAnchor",   # 再着色アンカー正規化のみ(マッチはスポイト色のまま)
    "sampleColorSet",  # 自動調整の可否判定のみ(マッチ経路では未参照)
    "targetColorSet",  # UI の手順案内のみ(選択・再着色では未参照)
    "layerIndex",      # 非推奨(優先度はリスト並び順へ移行済み)
    "detailFoldout",   # UI 折りたたみ状態(NonSerialized)
}

# そのものは鍵に入れないが、鍵に入れた派生の値を通して選択に効くフィールド → その派生の値の名前。
# 派生の値はハーネスの --selkey-audit が `SELKEY-DERIVED <名前> <0|1>` で出す。
SELECTION_VIA_DERIVED = {
    # 白へ抜ける途中のほぼ白を 2 回目に戻すか(PixelProcessor.NearWhiteRecolorIsGentle)。
    # 塗り先の明るさ・彩度と、模様保持で下がる明るさで決まる。
    "targetColor": "nearWhiteGentle",
    "valueBlend": "nearWhiteGentle",
}


@pytest.fixture(scope="module")
def audit_output() -> list[str]:
    G.require_toolchain()
    G.build_harness_or_fail()

    res = subprocess.run(
        ["dotnet", str(G.HARNESS_DLL), "--selkey-audit"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=G.RUN_TIMEOUT_S)
    if res.returncode != 0:
        pytest.fail(f"--selkey-audit 実行失敗 (exit={res.returncode}):\n{res.stderr}")
    return res.stdout.splitlines()


@pytest.fixture(scope="module")
def audit_lines(audit_output) -> list[str]:
    return [ln for ln in audit_output if ln.startswith("SELKEY ")]


def _parse(lines: list[str]) -> dict[str, str]:
    fields: dict[str, str] = {}
    for ln in lines:
        _, field, flag = ln.split()
        fields[field] = flag
    return fields


def test_all_fields_covered_or_classified(audit_lines):
    """キー未反映のフィールドは分類台帳に載っていなければならない（追加忘れ検出）。"""
    fields = _parse(audit_lines)
    assert fields, "SELKEY 出力が空です（--selkey-audit の退行?）"

    unperturbable = [f for f, flag in fields.items() if flag == "?"]
    assert not unperturbable, (
        f"摂動方法が未定義の型のフィールドがあります: {unperturbable}\n"
        "→ Harness.TryPerturbField にその型の摂動を追加してください。")

    unclassified = [
        f for f, flag in fields.items()
        if flag == "0" and f not in SELECTION_IRRELEVANT and f not in SELECTION_VIA_DERIVED
    ]
    assert not unclassified, (
        f"BuildSelectionKey に含まれず、選択無関係の分類も無いフィールド: {unclassified}\n"
        "→ 選択に影響するなら PixelProcessor.BuildSelectionKey へ追加（キー漏れ＝"
        "キャッシュ誤ヒットで古い選択のプレビューになる）、\n"
        "   影響しないなら本テストの SELECTION_IRRELEVANT へ理由コメント付きで追加。\n"
        "   派生の値(bool など)を通して効くなら、その値を鍵に入れて SELECTION_VIA_DERIVED に載せ、\n"
        "   ハーネスの --selkey-audit に SELKEY-DERIVED の行を足す。")


def test_classification_ledger_is_fresh(audit_lines):
    """分類台帳に実在しないフィールドが残っていたら陳腐化なので落とす。"""
    fields = _parse(audit_lines)
    stale = (SELECTION_IRRELEVANT | set(SELECTION_VIA_DERIVED)) - set(fields)
    assert not stale, (
        f"SELECTION_IRRELEVANT / SELECTION_VIA_DERIVED に実在しないフィールドが残っています: {sorted(stale)}\n"
        "→ ColorZone から削除/改名されたなら台帳からも消してください。")
    both = SELECTION_IRRELEVANT & set(SELECTION_VIA_DERIVED)
    assert not both, f"選択に無関係と派生で効くの両方に載っています: {sorted(both)}"


def test_derived_selection_inputs_change_the_key(audit_output):
    """派生の値ごとに、その値が変わる 2 つのゾーンで鍵が違うこと(派生の値が鍵に入っていること)。"""
    derived = {}
    for ln in audit_output:
        if ln.startswith("SELKEY-DERIVED "):
            _, name, flag = ln.split()
            derived[name] = flag
    missing = sorted(set(SELECTION_VIA_DERIVED.values()) - set(derived))
    assert not missing, (
        f"--selkey-audit が SELKEY-DERIVED の行を出していません: {missing}\n"
        "→ ハーネスの RunSelectionKeyAudit に、その派生の値が変わる 2 つのゾーンの鍵を比べる行を足す。")
    not_in_key = sorted(n for n in set(SELECTION_VIA_DERIVED.values()) if derived[n] != "1")
    assert not not_in_key, (
        f"派生の値が変わっても鍵が変わりません: {not_in_key}\n"
        "→ その値を BuildSelectionKey から消していないか（消すと塗り先の色・模様保持を変えたときに古い選択の\n"
        "  プレビューになる）。消していないなら、ハーネスが選んだ 2 つのゾーンでその値が変わらなくなっている\n"
        "  (派生の値の式を変えた)ので、ゾーンを選び直す。")
