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
    "targetColor",     # 再着色(出力色)のみ
    "valueBlend",      # 再着色の明度ブレンドのみ
    "outputSaturation",  # 再着色の出力彩度のみ
    "shadowDesaturation",  # 再着色の暗部脱彩のみ
    "applyHighlightWash",  # 再着色のハイライト合成のみ
    "autoHighlightSample",  # wash 用サンプル補正のみ(match/base 不変)
    "autoRecolorAnchor",   # 再着色アンカー正規化のみ(マッチはスポイト色のまま)
    "sampleColorSet",  # 自動調整の可否判定のみ(マッチ経路では未参照)
    "layerIndex",      # 非推奨(優先度はリスト並び順へ移行済み)
    "detailFoldout",   # UI 折りたたみ状態(NonSerialized)
}


@pytest.fixture(scope="module")
def audit_lines() -> list[str]:
    if not G.dotnet_available():
        pytest.skip("dotnet が利用できません")
    missing = G.unity_dll_missing()
    if missing:
        pytest.skip(f"Unity CoreModule DLL がありません: {missing}")
    ok, r = G.build_harness()
    if not ok:
        pytest.fail(
            "Harness ビルド失敗。dotnet と Unity DLL は存在するため、Code/ の"
            "コンパイルエラーの可能性が高い（サイレント skip にしない）:\n"
            f"{((r.stdout or '') + (r.stderr or ''))[-1500:]}")

    res = subprocess.run(
        ["dotnet", str(G.HARNESS_DLL), "--selkey-audit"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if res.returncode != 0:
        pytest.fail(f"--selkey-audit 実行失敗 (exit={res.returncode}):\n{res.stderr}")
    return [ln for ln in res.stdout.splitlines() if ln.startswith("SELKEY ")]


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
        if flag == "0" and f not in SELECTION_IRRELEVANT
    ]
    assert not unclassified, (
        f"BuildSelectionKey に含まれず、選択無関係の分類も無いフィールド: {unclassified}\n"
        "→ 選択に影響するなら PixelProcessor.BuildSelectionKey へ追加（キー漏れ＝"
        "キャッシュ誤ヒットで古い選択のプレビューになる）、\n"
        "   影響しないなら本テストの SELECTION_IRRELEVANT へ理由コメント付きで追加。")


def test_classification_ledger_is_fresh(audit_lines):
    """分類台帳に実在しないフィールドが残っていたら陳腐化なので落とす。"""
    fields = _parse(audit_lines)
    stale = SELECTION_IRRELEVANT - set(fields)
    assert not stale, (
        f"SELECTION_IRRELEVANT に実在しないフィールドが残っています: {sorted(stale)}\n"
        "→ ColorZone から削除/改名されたなら台帳からも消してください。")
