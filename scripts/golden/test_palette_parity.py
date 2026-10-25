"""色の表あり・なしのパリティテスト。

PixelProcessor は元画素の「色の種類」の表（PixelProcessor.Palette.cs）があると、色だけで決まる計算を
色ごとに 1 回だけ行い、画素へは番号で配る。表が作れない画像（色の種類が多い写真調など）では画素ごとの
計算に戻る。つまり同じ式が 2 通りに実装されていて（HSV・照合・帯成長の候補判定・無彩の領域 L・
再着色アンカー・再着色の色ごとのメモ）、片方だけ直すと、色の種類が多いテクスチャでだけ出力が変わる。
コードのコメントは「両経路を同時に直すこと」と書くだけで、これまで比べるテストが無かった。

本テストは golden の全ケースを、通常の実行（表を使う）と IROCA_NO_PALETTE=1（表を作らない）の 2 通りで
回し、出力がビット単位で同じことを確かめる。表を使う経路を通らないケースばかりだと検出力が無いので、
表が作られる条件（色の種類 ≤ max(256, 画素数/4)。PixelProcessor.TryBuildPalette）を満たすケースが
十分にあることも確かめる。

実行: pytest scripts/golden/test_palette_parity.py -q
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402

_NO_PALETTE = {"IROCA_NO_PALETTE": "1"}


def _highlight_band_cloth() -> np.ndarray:
    """赤い布に、地色から白へ抜けるツヤが 2 か所ある画像（灰の地）。

    golden のケースではハイライト帯の成長（GrowHighlightBand）が 1 画素も足さないので（候補を全部
    捨てても出力が同じ）、帯の色ごとの判定（IsHighlightBandCandidate）を比べるにはこれが要る。
    上の帯は地色→白の軸の上。下の領域は白に近い明るさ（ハイライト復帰が拾わず、帯の成長だけが
    拾う範囲）で、色相は地色のまま軸からの残差を 0〜0.18 にばらつかせ、判定の境目（HlBandAxisEps）を
    またがせる。許容 0.10〜0.15 では本体の照合が帯の明るい側を取れず、帯の成長が足す。
    """
    n = 128
    s = np.array([0.60, 0.12, 0.12])
    d = 1.0 - s                                   # 地色 → 白
    # d と (1,0,0) が張る面（g = b、色相は赤のまま）の中で d に直交する単位ベクトル
    e = np.array([1.0, 0.0, 0.0]) - d * (d[0] / (d @ d))
    e /= np.linalg.norm(e)
    yy, xx = np.mgrid[0:n, 0:n]
    rgba = np.zeros((n, n, 4), np.uint8)
    rgba[..., 3] = 255
    rgba[..., :3] = 90
    body = (xx > 16) & (xx < 112) & (yy > 8) & (yy < 120)
    col = np.broadcast_to(s, (n, n, 3)).copy()
    t = (np.clip(1.0 - np.abs(yy - 36) / 18.0, 0.0, 1.0) * 0.85)[..., None]
    band = np.abs(yy - 36) < 18
    col[band] = (s + t * d)[band]
    rng = np.random.default_rng(7)
    spread = (yy >= 72) & (yy < 104)
    k = int(spread.sum())
    tt = rng.uniform(0.55, 0.85, k)[:, None]
    rr = (rng.uniform(0.0, 0.18, k) * rng.choice([-1.0, 1.0], k))[:, None]
    col[spread] = np.clip(s + tt * d + rr * e, 0.0, 1.0)
    rgba[body, :3] = np.round(col[body] * 255).astype(np.uint8)
    return rgba


def _extra_cases() -> list[tuple[str, np.ndarray, dict, dict]]:
    band = _highlight_band_cloth()
    cases = []
    for tol in (0.10, 0.15):
        z = G.zone(sample=(0.60, 0.12, 0.12), target=(0.16, 0.27, 0.78), tolerance=tol,
                   highlightRecovery=True, highlightBandExpand=True)
        cases.append((f"hlband_tol{tol:.2f}", band, z, G.settings()))
    return cases


_CASES = G.build_cases() + _extra_cases()


def _palette_expected(rgba: np.ndarray) -> bool:
    """TryBuildPalette が表を作る画像か（色の種類が上限以下か）。"""
    flat = np.ascontiguousarray(rgba).view(np.uint32).reshape(-1)
    n = flat.size
    limit = min(1 << 20, max(256, n // 4))
    return len(np.unique(flat)) <= limit


@pytest.fixture(scope="module")
def harness():
    G.require_toolchain()
    G.build_harness_or_fail()
    return True


def test_cases_exercise_palette_path():
    """表を使う経路を通るケースが無いと、このテストは何も比べていないのと同じ。"""
    with_palette = [label for label, rgba, _, _ in _CASES if _palette_expected(rgba)]
    assert len(with_palette) >= 10, (
        f"色の表が作られるケースが {len(with_palette)} 件しかありません（golden のケースが変わった?）: {with_palette}")


@pytest.mark.parametrize("label,rgba,zone,settings",
                         [pytest.param(*c, id=c[0]) for c in _CASES])
def test_palette_and_per_pixel_paths_match(label, rgba, zone, settings, harness, tmp_path):
    with_palette = G.run_csharp(rgba, zone, settings, tmp_path / "palette")
    per_pixel = G.run_csharp(rgba, zone, settings, tmp_path / "nopalette", env=_NO_PALETTE)
    diff = np.any(with_palette != per_pixel, axis=-1)
    n = int(diff.sum())
    if n:
        ys, xs = np.nonzero(diff)
        y, x = int(ys[0]), int(xs[0])
        pytest.fail(
            f"{label}: 色の表あり・なしで出力が {n} 画素違います（最初の画素 ({x},{y}): "
            f"表あり {tuple(with_palette[y, x])} / 表なし {tuple(per_pixel[y, x])}）。\n"
            "  同じ式の 2 つの実装（色ごと・画素ごと）の片方だけが変わっています。両方を同じに直してください。")
