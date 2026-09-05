"""評価条件の定義。画像処理や pytest に依存せず承認ゲートからも利用する。"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

SUBJECTS = (
    "haolan-sneakers", "haolan-costume", "bandana",
    "feina-tops", "feina-boots", "feina-pants", "feina-red",
    "feina-goggles", "feina-white", "quanstella-skirt", "quanstella-gold",
    "quanstella-black", "quanstella-white", "quanstella-eye",
)
CLICKS = ("p25", "p50", "p95")
PRESET_SUBJECTS = ("bandana", "haolan-costume", "haolan-hair", "haolan-sneakers")
WORKFLOWS = {
    "oneshot": {"label": "ワンショット", "mask_role": "evidence_only",
                "mask_source": "sam", "settings_source": "autotune",
                "additional_action": "none"},
    "assisted_include": {"label": "AI提案を含める適用後", "mask_role": "evidence_and_include",
                         "mask_source": "sam", "settings_source": "autotune",
                         "additional_action": "accept_include_unless_rejected"},
    "oracle_masked": {"label": "理想部位マスクによる上限性能", "mask_role": "exclude",
                      "mask_source": "psd_part_simulation", "settings_source": "legacy_autotune"},
    "fixed": {"label": "固定設定", "mask_role": "none", "mask_source": "none",
              "settings_source": "fixture"},
    "fixed_excluded": {"label": "固定設定の除外契約", "mask_role": "exclude",
                       "mask_source": "synthetic_from_gt", "settings_source": "fixture"},
    "fallback": {"label": "従来の自動調整", "mask_role": "none", "mask_source": "none",
                 "settings_source": "legacy_autotune"},
}
SELECTION_SLACK = {"iou": 0.02, "precision": 0.02, "recall": 0.03, "island": 0.005}
BASELINES = {
    "evidence_autotune_baseline.json": ({f"{s}/{c}" for s in SUBJECTS for c in CLICKS},
                                         ("iou", "precision", "recall", "island")),
    "assisted_include_baseline.json": ({f"{s}/p50" for s in SUBJECTS},
                                       ("iou", "precision", "recall", "island")),
    "oneshot_vs_preset_baseline.json": (set(PRESET_SUBJECTS), ("dE_p95", "dE_mean", "sel_iou")),
}


def load_baseline(path: Path) -> dict:
    """必須ファイル・ケース・有限な指標値の欠落は skip でなくエラー。"""
    doc = json.loads(path.read_text(encoding="utf-8"))
    return validate_baseline(doc, path.name)


def validate_baseline(doc: dict, name: str) -> dict:
    expected, metrics = BASELINES[name]
    cases = doc.get("cases", {})
    missing = expected - cases.keys()
    if missing:
        raise ValueError(f"{name}: 必須ケース不足: {sorted(missing)}")
    for key in expected:
        for metric in metrics:
            value = cases[key].get(metric)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError(f"{name}: {key}/{metric} が有限な数値ではありません")
        flag = "satisfied" if name == "evidence_autotune_baseline.json" else "include_applied" if name == "assisted_include_baseline.json" else None
        if flag and not isinstance(cases[key].get(flag), bool):
            raise ValueError(f"{name}: {key}/{flag} がboolではありません")
    return cases


def selection_violations(actual: dict, baseline: dict) -> list[str]:
    return [name for name, slack in SELECTION_SLACK.items()
            if not math.isfinite(actual[name]) or
            (actual[name] > baseline[name] + slack if name == "island"
             else actual[name] < baseline[name] - slack)]


def sha256(path: Path) -> str:
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()
