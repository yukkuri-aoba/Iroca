"""評価条件の定義（tools/workflow_contract.py）の整合を検査する。資産不要。"""
from __future__ import annotations

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "tools"))

import workflow_contract as wc  # noqa: E402


def test_holdout_is_disjoint_from_tuning_subjects():
    """ホールドアウトが SUBJECTS（回帰の床・審査ページ・視覚レビュー）に混ざると、
    改善サイクル中に個別の数字と画を見てしまい、汎化の確認にならない。"""
    wc.validate_subject_split()


def test_subjects_are_unique():
    assert len(set(wc.SUBJECTS)) == len(wc.SUBJECTS)
