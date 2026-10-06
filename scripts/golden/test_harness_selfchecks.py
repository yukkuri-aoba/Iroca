"""Harness の自己検査モードを pytest から回す（自動の呼び出し元が無かった 2 モードの配線）。

  - --previewselftest: RecolorPreview（自動化の目視検証パネルとメトリクス）を、合成した before/after で
    実 C# 実行し、既知の不変量（変化画素数・連結成分・パネル寸法・マゼンタ着色）を確かめる。入力は不要。
  - --samops-aainclude-crop: AA 遷移帯の包含をクロップして実行する経路（AI 提案のマスク確定段が使う）。
    全画像で実行する --samops-aainclude と、出力がビット同一であることを確かめる。

Harness.cs は変えない。環境不備は skip（CI の IROCA_REQUIRE_HARNESS=1 では fail）、環境が揃っているのに
ビルドが失敗したら fail。どちらも golden と同じ規則（golden_lib.require_toolchain / build_harness_or_fail）。

実行: pytest scripts/golden/test_harness_selfchecks.py -q
"""
from __future__ import annotations

import re
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402


@pytest.fixture(scope="module")
def harness() -> Path:
    G.require_toolchain()
    G.build_harness_or_fail()
    return G.HARNESS_DLL


def _run(harness: Path, *args) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["dotnet", str(harness), *(str(a) for a in args)],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=G.RUN_TIMEOUT_S)


def test_preview_selftest(harness):
    """RecolorPreview の不変量がすべて成り立つ（1 件でも崩れると Harness が非 0 で終わる）。"""
    r = _run(harness, "--previewselftest")
    lines = r.stdout.splitlines()
    assert r.returncode == 0, (
        f"--previewselftest が失敗 (exit={r.returncode}):\n{r.stdout}\n{r.stderr}")
    assert lines and lines[-1] == "PREVIEWSELFTEST ALL PASS", (
        f"最終行が ALL PASS ではありません:\n{r.stdout}")
    failed = [ln for ln in lines if " FAIL " in ln]
    assert not failed, "不変量に反した項目があります:\n" + "\n".join(failed)


# ─────────────── AA 遷移包含: クロップ実行と全画像実行の一致 ───────────────
_N = 1024          # 全体より小さいクロップ矩形が取れる大きさ（帯幅 d=3、パディング 15d）
_RADIUS = 60.0
_RED = np.array([200, 30, 30], np.float32)
_GRAY = np.array([150, 150, 150], np.float32)
_AACROP = re.compile(r"AACROP rect=\((\d+),(\d+)\) (\d+)x(\d+) d=(\d+)")


def _disc_on_gray(cx: float, cy: float) -> tuple[np.ndarray, np.ndarray]:
    """灰地に、縁が約 3px の混色帯になった赤い円を置いた RGBA と、その芯だけのマスクを返す。

    芯（r <= R-1.5）は帯の内側なので、AA 遷移包含が帯の画素を足す余地がある。
    """
    yy, xx = np.mgrid[0:_N, 0:_N].astype(np.float32)
    r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2)
    cov = np.clip((_RADIUS + 1.5 - r) / 3.0, 0.0, 1.0)[..., None]
    rgb = cov * _RED + (1.0 - cov) * _GRAY
    rgba = np.concatenate([np.round(rgb).astype(np.uint8), np.full((_N, _N, 1), 255, np.uint8)], axis=-1)
    mask = (r <= _RADIUS - 1.5).astype(np.uint8)
    return rgba, mask


def _read_mask(path: Path) -> np.ndarray:
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        return np.frombuffer(f.read(), np.uint8).reshape(h, w)


@pytest.mark.parametrize("cx,cy,clamped", [
    pytest.param(500.3, 500.3, False, id="center"),
    # 角に寄せると矩形の左上が画像の端（0）で切り詰められる経路を踏む。
    pytest.param(40.3, 40.3, True, id="corner"),
])
def test_aainclude_crop_matches_full(harness, tmp_path, cx, cy, clamped):
    rgba, mask = _disc_on_gray(cx, cy)
    img_raw, mask_raw = tmp_path / "img.raw", tmp_path / "mask.raw"
    G.write_raw(img_raw, rgba)
    G.write_raw(mask_raw, mask)
    full_raw, crop_raw = tmp_path / "full.raw", tmp_path / "crop.raw"

    full = _run(harness, "--samops-aainclude", mask_raw, img_raw, full_raw)
    assert full.returncode == 0, f"--samops-aainclude 失敗 (exit={full.returncode}):\n{full.stderr}"
    crop = _run(harness, "--samops-aainclude-crop", mask_raw, img_raw, crop_raw)
    assert crop.returncode == 0, f"--samops-aainclude-crop 失敗 (exit={crop.returncode}):\n{crop.stderr}"

    # クロップ矩形が導出できないとき（マスクが空など）、crop 側はマスクを無加工で書き出す。
    # AACROP 行・全体より小さい矩形・追加画素を確かめないと、クロップ経路を通らずに一致して緑になる。
    m = _AACROP.search(crop.stderr)
    assert m, f"AACROP 行がありません（クロップ経路を通っていない）:\n{crop.stderr}"
    rx0, ry0, rw, rh, _ = (int(g) for g in m.groups())
    assert rw < _N and rh < _N, f"クロップ矩形が全面と同じ大きさです: {m.group(0)}"
    if clamped:
        assert rx0 == 0 and ry0 == 0, f"矩形の左上が端で切り詰められていません: {m.group(0)}"
    else:
        assert rx0 > 0 and ry0 > 0, f"矩形の左上が端に当たっています（中央ケースの前提が崩れた）: {m.group(0)}"

    assert full_raw.read_bytes() == crop_raw.read_bytes(), (
        f"クロップ実行と全画像実行の出力が一致しません（{m.group(0)}）")
    added = int(_read_mask(full_raw).astype(bool).sum()) - int(mask.astype(bool).sum())
    assert added > 0, "AA 遷移帯の画素が 1 つも足されていません（検出力のない入力）"
