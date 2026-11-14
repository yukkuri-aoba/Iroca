"""照合の上限距離（ColorZone.matchCutoff）が、zones JSON から実 C# まで届いて選択を切ることのテスト。

上限は自動調整の許容範囲の切り詰めが、グレーモードの照合（無彩のサンプル）で入れる。許容範囲（= 縁のぼかしの幅）は
変えず、距離が上限以上の画素だけを選ばない。主の照合だけでなく、穴埋めの許可と境界回復の緩和マッチも同じ上限を守る
（守らないと、選択に隣り合う切り落とした側の画素を境界回復が数列ぶん戻す。緩和マッチの上限を外したコードでは、
下の上限ありのケースで右半分の 3 列が塗られることを確かめてある）。

入力は左右に分けた 2 つの灰色（左が暗い灰色 = サンプル、右がそれより明るい灰色。RGB 距離は約 0.16）。許容範囲 0.30 は
両方を覆うので、上限が無ければ両方が塗られる。

実行: pytest scripts/golden/test_match_cutoff.py -q
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402

_H, _W = 32, 48
_LEFT, _RIGHT = 60, 100
_SAMPLE = (_LEFT / 255.0,) * 3
_TARGET = (0.8, 0.1, 0.1)
_TOL = 0.30


def _two_grays() -> np.ndarray:
    img = np.zeros((_H, _W, 4), np.uint8)
    img[..., 3] = 255
    img[:, :_W // 2, :3] = _LEFT
    img[:, _W // 2:, :3] = _RIGHT
    return img


def _changed(img: np.ndarray, out: np.ndarray) -> np.ndarray:
    return (out != img).any(axis=-1)


@pytest.fixture(scope="module", autouse=True)
def _toolchain():
    G.require_toolchain()
    G.build_harness_or_fail()


# デコンタミ ON（既定）と OFF（穴埋め・境界回復の緩和マッチが必ず走る）の両方で見る。
@pytest.mark.parametrize("decontam", [True, False], ids=["decontam", "no-decontam"])
def test_cutoff_drops_the_farther_gray_and_keeps_the_sampled_one(tmp_path, decontam):
    img = _two_grays()
    s = G.settings(useDecontamination=decontam)
    plain = _changed(img, G.run_csharp(img, G.zone(sample=_SAMPLE, target=_TARGET, tolerance=_TOL,
                                                   highlightRecovery=True), s, tmp_path / "plain"))
    assert plain.all(), "テストの前提: 上限が無ければ、許容範囲が両方の灰色を覆う"

    cut = _changed(img, G.run_csharp(img, G.zone(sample=_SAMPLE, target=_TARGET, tolerance=_TOL,
                                                 highlightRecovery=True, matchCutoff=0.10), s, tmp_path / "cut"))
    assert cut[:, :_W // 2].all(), "上限より内側(サンプルの灰色)が塗られていない"
    n = int(cut[:, _W // 2:].sum())
    assert n == 0, (
        f"上限より遠い灰色が {n} 画素塗られた。zones JSON の matchCutoff が届いていないか、穴埋め・境界回復の"
        "緩和マッチが上限を守っていない(PixelProcessor.GetRelaxedMatchStrengthWithinCutoff)")


def test_cutoff_not_below_tolerance_changes_nothing(tmp_path):
    # 上限が許容範囲以上なら、上限より遠い画素は許容範囲でも外れるので、出力は上限なしとビット単位で同じ。
    # (ハイライトの候補は許容範囲の外の画素にも立つので、ここでは切って比べる。)
    img = _two_grays()
    s = G.settings()
    base = G.zone(sample=_SAMPLE, target=_TARGET, tolerance=0.12, highlightRecovery=False)
    a = G.run_csharp(img, base, s, tmp_path / "off")
    b = G.run_csharp(img, dict(base, matchCutoff=0.12), s, tmp_path / "equal")
    c = G.run_csharp(img, dict(base, matchCutoff=0.5), s, tmp_path / "above")
    assert np.array_equal(a, b), "上限 = 許容範囲で出力が変わった"
    assert np.array_equal(a, c), "上限 > 許容範囲で出力が変わった"
