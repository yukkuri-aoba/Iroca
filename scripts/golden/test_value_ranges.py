"""範囲外の値を Core の入口で丸めることのテスト（点検 A-8）。

zones JSON（静的 API・CLI・MCP）や手で書いたプリセット・セッション・ハーネスは、UI のスライダーの範囲の外の値を
そのまま渡せる。範囲の外では前提が崩れる所がある（デコンタミの半径 > 12・outputSaturation < 0 で補色に反転・
holeFillPasses < 0 で後段の範囲が選択より狭くなる など）。全体設定は RecolorSettings のコンストラクタ、ゾーンは
PixelProcessor の処理の入口で、Code/Core/RecolorValueRanges.cs の範囲へ丸める。

ここでは「範囲外の値を書いた JSON」と「同じ値を手で範囲へ丸めた JSON」を実 C# のハーネスに通し、出力がビット単位で
同じことを確かめる（丸めがどこかで抜けると、範囲外の値が後段まで届いて出力が変わる）。丸める前のコードでは
settings_above・settings_below・zone_fractions が食い違うことを確かめてある（ほかのケースはこの入力では範囲外でも
出力が変わらないが、丸めた後も同じであることを固定する）。範囲内の値の出力が変わらないことは golden の全ケース
（test_golden_csharp.py）が見る。NaN は JSON に書けないので自動化の EditMode テストが見る。

実行: pytest scripts/golden/test_value_ranges.py -q
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402
import synth_textures as S  # noqa: E402


def _n(rgb):
    return tuple(c / 255.0 for c in rgb)


# (名前, 入力, 範囲外のゾーン, 範囲外の設定, 丸めたゾーン, 丸めた設定)
def _cases():
    red, blue = _n((210, 50, 50)), _n((40, 70, 200))
    cloth = S.shaded_with_highlight((200, 40, 40))
    aa = S.two_color_aa((210, 50, 50), (50, 180, 70))
    on_white = S.chromatic_on_neutral((210, 50, 50), 235)
    return [
        # 全体設定: 上へ外れる（edgeFeather・境界クリーンアップ・穴埋め・近傍数・緩和の彩度・デコンタミ半径）
        ("settings_above", on_white,
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=9.0, antiAliasCleanup=9, holeFillPasses=25, holeFillMinNeighbors=12,
                    relaxedSatMin=1.7, relaxedSatRamp=3.0, decontaminationRadius=40),
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=5.0, antiAliasCleanup=5, holeFillPasses=10, holeFillMinNeighbors=8,
                    relaxedSatMin=1.0, relaxedSatRamp=1.0, decontaminationRadius=12)),
        # 全体設定: 下へ外れる（境界クリーンアップ・穴埋めは負、近傍数 0、デコンタミ半径 0）。境界ぼかしを入れて
        # 混色帯でない経路(デコンタミの半径が効く)を通す
        ("settings_below", on_white,
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=2.0, antiAliasCleanup=-3, holeFillPasses=-4, holeFillMinNeighbors=0,
                    relaxedSatMin=-0.5, relaxedSatRamp=-1.0, decontaminationRadius=0),
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=2.0, antiAliasCleanup=0, holeFillPasses=0, holeFillMinNeighbors=1,
                    relaxedSatMin=0.0, relaxedSatRamp=0.0, decontaminationRadius=1)),
        # 全体設定: 境界ぼかしが負
        ("settings_negative_feather", aa,
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=-2.0),
         G.zone(sample=red, target=blue, tolerance=0.30),
         G.settings(edgeFeather=0.0)),
        # ゾーン: 割合の項目と色の成分が上下に外れる。partHueBand は 0..1（UI は 0.5 まで）
        ("zone_fractions", cloth,
         G.zone(sample=(1.3, 0.12, 0.12), target=(-0.2, 0.27, 1.6), tolerance=1.4, valueBlend=1.5,
                edgeSoftness=-0.3, saturationStrictness=2.0, saturationGuard=-1.0, shadowDesaturation=1.8,
                shadowForgivenessSatMin=-0.2, outputSaturation=-0.5, partHueBand=1.25,
                highlightRecovery=True),
         G.settings(),
         G.zone(sample=(1.0, 0.12, 0.12), target=(0.0, 0.27, 1.0), tolerance=1.0, valueBlend=1.0,
                edgeSoftness=0.0, saturationStrictness=1.0, saturationGuard=0.0, shadowDesaturation=1.0,
                shadowForgivenessSatMin=0.0, outputSaturation=0.0, partHueBand=1.0,
                highlightRecovery=True),
         G.settings()),
        # ゾーン: 連続領域のゲートと彩度天井・穴埋めの余白が外れる（デコンタミ OFF で穴埋めを通す）
        ("zone_gates", cloth,
         G.zone(sample=_n((150, 30, 30)), target=blue, tolerance=0.45, chromaThreshold=1.5,
                shadowValueFloor=-0.4, partSatCeiling=1.3, chromaCeiling=-0.1, holeFillMargin=2.5),
         G.settings(useDecontamination=False),
         G.zone(sample=_n((150, 30, 30)), target=blue, tolerance=0.45, chromaThreshold=1.0,
                shadowValueFloor=0.0, partSatCeiling=1.0, chromaCeiling=0.0, holeFillMargin=1.0),
         G.settings(useDecontamination=False)),
    ]


_CASES = _cases()


@pytest.fixture(scope="module", autouse=True)
def _toolchain():
    G.require_toolchain()
    G.build_harness_or_fail()


@pytest.mark.parametrize("label,rgba,z_out,s_out,z_in,s_in", _CASES, ids=[c[0] for c in _CASES])
def test_out_of_range_equals_clamped(tmp_path, label, rgba, z_out, s_out, z_in, s_in):
    out_raw = G.run_csharp(rgba, z_out, s_out, tmp_path / "raw")
    out_clamped = G.run_csharp(rgba, z_in, s_in, tmp_path / "clamped")
    assert out_raw.shape == out_clamped.shape
    diff = int((out_raw != out_clamped).any(axis=-1).sum())
    assert diff == 0, (
        f"{label}: 範囲外の値と丸めた値で出力が {diff} 画素違います。"
        "RecolorValueRanges の丸め(RecolorSettings のコンストラクタ・ProcessPixelsArrayCore の先頭)が"
        "抜けているか、範囲が UI・ここと食い違っています")

