"""Harness の自己検査モードを pytest から回す（自動の呼び出し元が無かった 2 モードの配線）。

  - --previewselftest: RecolorPreview（自動化の目視検証パネルとメトリクス）を、合成した before/after で
    実 C# 実行し、既知の不変量（変化画素数・連結成分・パネル寸法・マゼンタ着色）を確かめる。入力は不要。
  - --samops-aainclude-crop: AA 遷移帯の包含をクロップして実行する経路（AI 提案のマスク確定段が使う）。
    全画像で実行する --samops-aainclude と、出力がビット同一であることを確かめる。
  - --samops-refineall: 精密化 4 段の製品の入口（提案の外接矩形＋余白だけで回す）が、全画像で 4 段を回したのと
    ビット同一であることを確かめる。
  - --selcache-mask: マスクだけを変えた再生成が、選択キャッシュを持ち越してもフル再計算と同じ出力になること。

Harness.cs は変えない。環境不備は skip（CI の IROCA_REQUIRE_HARNESS=1 では fail）、環境が揃っているのに
ビルドが失敗したら fail。どちらも golden と同じ規則（golden_lib.require_toolchain / build_harness_or_fail）。

実行: pytest scripts/golden/test_harness_selfchecks.py -q
"""
from __future__ import annotations

import json
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


def test_native_memory_selftest(harness):
    """作業配列・色の表(Mono のヒープの外)が処理の後に全部解放でき、使用中の色の表を追い出しても出力が変わらない。"""
    r = _run(harness, "--nativememselftest")
    lines = r.stdout.splitlines()
    assert r.returncode == 0, (
        f"--nativememselftest が失敗 (exit={r.returncode}):\n{r.stdout}\n{r.stderr}")
    assert lines and lines[-1] == "NATIVEMEMSELFTEST ALL PASS", (
        f"最終行が ALL PASS ではありません:\n{r.stdout}")


def test_mask_codec_selftest(harness):
    """保存形式のマスクの書き込み・展開。展開で中身の同じマスクは前の配列をそのまま使い、違うものは展開し直す。"""
    r = _run(harness, "--maskcodecselftest")
    lines = r.stdout.splitlines()
    assert r.returncode == 0, (
        f"--maskcodecselftest が失敗 (exit={r.returncode}):\n{r.stdout}\n{r.stderr}")
    assert lines and lines[-1] == "MASKCODECSELFTEST ALL PASS", (
        f"最終行が ALL PASS ではありません:\n{r.stdout}")


def test_bit_mask_selftest(harness):
    """ヒープの外の 1 画素 1 ビットのマスクと、保存形式との直接の変換が bool[] の口と同じ結果になる。"""
    r = _run(harness, "--bitmaskselftest")
    lines = r.stdout.splitlines()
    assert r.returncode == 0, (
        f"--bitmaskselftest が失敗 (exit={r.returncode}):\n{r.stdout}\n{r.stderr}")
    assert lines and lines[-1] == "BITMASKSELFTEST ALL PASS", (
        f"最終行が ALL PASS ではありません:\n{r.stdout}")


def test_region_map_selftest(harness):
    """領域の地図(L1): 平塗り・縁の混色・グラデの帯・透明に囲まれた島・細い線の扱いと、メモリが返ること。"""
    r = _run(harness, "--regionselftest")
    lines = r.stdout.splitlines()
    assert r.returncode == 0, (
        f"--regionselftest が失敗 (exit={r.returncode}):\n{r.stdout}\n{r.stderr}")
    assert lines and lines[-1] == "REGIONSELFTEST ALL PASS", (
        f"最終行が ALL PASS ではありません:\n{r.stdout}")


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


# ─────────────── 精密化 4 段: 切り出しと全画像の一致 ───────────────
_REFINEALL = re.compile(r"REFINEALL OK \d+x\d+ crop=(\S+) ms=[\d.]+ full_ms=[\d.]+ diff=(\d+)")


def _blocky(mask: np.ndarray, cell: int) -> np.ndarray:
    """SAM の粗い提案に似せて、cell 角ごとに多数決で塗った形にする（境界が色の縁からずれる）。"""
    h, w = mask.shape
    m = mask.reshape(h // cell, cell, w // cell, cell).mean(axis=(1, 3)) >= 0.5
    return np.repeat(np.repeat(m, cell, axis=0), cell, axis=1).astype(np.uint8)


def _thin_line_on_cloth() -> tuple[np.ndarray, np.ndarray]:
    """生成りの地に暗い細い線（幅 3px）と、線の周りに地が滲んだ細い提案（幅 13px）。"""
    rgba = np.zeros((_N, _N, 4), np.uint8)
    rgba[..., :3] = (225, 215, 190)
    rgba[..., 3] = 255
    rgba[500:503, 300:700, :3] = (40, 35, 30)
    mask = np.zeros((_N, _N), np.uint8)
    mask[495:508, 296:704] = 1
    return rgba, mask


@pytest.mark.parametrize("case", ["disc-center", "disc-corner", "thin-line"])
def test_refineall_crop_matches_full(harness, tmp_path, case):
    """製品の精密化の入口（提案が画像の一部なら外接矩形＋余白だけで 4 段を回す）が、全画像で 4 段を回したのと
    ビット同一。--samops-refineall に回数 2 を渡すと、同じプロセスで両方を回して違う画素数を出す。"""
    if case == "thin-line":
        rgba, mask = _thin_line_on_cloth()
    else:
        rgba, core = _disc_on_gray(*((500.3, 500.3) if case == "disc-center" else (40.3, 40.3)))
        mask = _blocky(core, 8)
    img_raw, mask_raw, out_raw = tmp_path / "img.raw", tmp_path / "mask.raw", tmp_path / "out.raw"
    G.write_raw(img_raw, rgba)
    G.write_raw(mask_raw, mask)
    r = _run(harness, "--samops-refineall", mask_raw, img_raw, out_raw, 2)
    assert r.returncode == 0, f"--samops-refineall 失敗 (exit={r.returncode}):\n{r.stderr}"
    m = _REFINEALL.search(r.stdout)
    assert m, f"REFINEALL 行がありません:\n{r.stdout}"
    assert m.group(1) != "none", f"切り出さずに全画像で回しています（検出力のない入力）: {m.group(0)}"
    assert int(m.group(2)) == 0, f"切り出しと全画像の出力が違います: {m.group(0)}"
    assert not np.array_equal(_read_mask(out_raw), mask), "4 段が 1 画素も変えていません（検出力のない入力）"


# ─────────────── 選択キャッシュ: マスクだけを変えた再生成 ───────────────
_SELCACHE = re.compile(r"SELCACHE populate_diff=(\d+) hit_vs_fresh=(\d+) miss_vs_fresh=(\d+)")
_SELCACHE_MASK = re.compile(r"SELCACHE-MASK quiet=(\d+) busy=(\d+) back=(\d+) include=(\d+) reuse=(\d+)"
                            r" latest=(\d+) latest_excl=(\d+)")


@pytest.mark.parametrize("feather", [0.0, 2.0])
def test_selection_cache_mask_changes(harness, tmp_path, feather):
    """除外マスク・含めるマスクだけを変えた再生成が、選択キャッシュを持ち越してもフル再計算と同じ出力になる。
    筆が通った画素でゾーンの照合が 0 なら、選択を使い回す（reuse が 1 以上）。
    feather > 0 はぼかしが除外の画素へにじむ（保存した選択の除外の画素が 0 でない）ので、右クリックの宛先が
    読む値（TryReadLatest が除外を掛けて返すこと）の検査に効く。"""
    n = 256
    yy, xx = np.mgrid[0:n, 0:n]
    rgba = np.zeros((n, n, 4), np.uint8)
    rgba[..., :3] = 150
    rgba[..., 3] = 255
    rgba[(xx - 80) ** 2 + (yy - 80) ** 2 <= 40 ** 2, :3] = (200, 30, 30)
    rgba[(xx - 180) ** 2 + (yy - 170) ** 2 <= 45 ** 2, :3] = (30, 60, 200)
    img_raw, mask_raw, out_raw, zj = (tmp_path / "img.raw", tmp_path / "mask.raw", tmp_path / "out.raw",
                                      tmp_path / "z.json")
    G.write_raw(img_raw, rgba)
    G.write_raw(mask_raw, np.zeros((n, n), np.uint8))
    zones = [G.zone(name="red", sample=(200 / 255, 30 / 255, 30 / 255), target=(0.1, 0.7, 0.2)),
             G.zone(name="blue", sample=(30 / 255, 60 / 255, 200 / 255), target=(0.9, 0.8, 0.1))]
    zj.write_text(json.dumps({"zones": [G._zone_json(z) for z in zones], "settings": G.settings(edgeFeather=feather)}),
                  encoding="utf-8")
    r = _run(harness, img_raw, mask_raw, out_raw, "--zones", zj, "--selcache-mask")
    assert r.returncode == 0, f"--selcache-mask 失敗 (exit={r.returncode}):\n{r.stderr}"
    m = _SELCACHE.search(r.stderr)
    assert m and all(int(g) == 0 for g in m.groups()), f"選択キャッシュの基本の検証が崩れています:\n{r.stderr}"
    mm = _SELCACHE_MASK.search(r.stderr)
    assert mm, f"SELCACHE-MASK 行がありません:\n{r.stderr}"
    quiet, busy, back, include, reuse, latest, latest_excl = (int(g) for g in mm.groups())
    assert (quiet, busy, back, include) == (0, 0, 0, 0), f"フル再計算と出力が違います: {mm.group(0)}"
    assert reuse > 0, f"マスクを変えたときに選択を 1 度も使い回していません（検出力のない入力）: {mm.group(0)}"
    # 右クリックの宛先が読む値（TryReadLatest）: 持ち越しと作り直しで同じ、除外の画素は 0
    assert (latest, latest_excl) == (0, 0), f"右クリックの宛先が読む選択が表示と違います: {mm.group(0)}"
