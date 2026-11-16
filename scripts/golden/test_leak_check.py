"""自動調整の後の漏れの確かめ（Code/Core/ZoneAutoTuner.Leak.cs）と、選択だけを回す入口（PixelProcessor.SimulateSelection）を、
Harness の --leak-probe / --leak-judge で合成の小さな画像に当てて確かめる。

  - 選択だけの入口: 縁に混色の無い平塗りの画像では、選ばれた画素 = 通常の色替えで変わった画素。
  - 塊と問い合わせ点: 証拠（最初の AI 提案）から L1 距離 8 までは塊に数えない・8 近傍で連結・面積の下限・大きい順に最大 6 個・
    問い合わせ点は塊のいちばん内側（同じ深さなら添字の小さい方）。
  - 判定: 同じ部品は外さない・一部だけ塗られた部品を外す（外すのは提案 ∖ 証拠）・細かい提案がほぼ塗られていれば外さない・
    境目が見える縁なら外さない・提案が背景まで広がった / 無いなら何もしない。

AI の提案は合成のマスクを渡す（判定は C# だけ。Python はデータを運ぶだけ）。環境不備は skip、ビルド失敗は fail（golden と同じ規則）。

実行: pytest scripts/golden/test_leak_check.py -q
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402

W, H = 256, 256
GRAY = (128, 128, 128)
RED = (200, 40, 40)


@pytest.fixture(scope="module")
def harness() -> Path:
    G.require_toolchain()
    G.build_harness_or_fail()
    return G.HARNESS_DLL


def _run(harness: Path, *args) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["dotnet", str(harness), *(str(a) for a in args)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=G.RUN_TIMEOUT_S)


def _json_line(stdout: str, tag: str) -> dict:
    for line in stdout.splitlines():
        if line.startswith(tag + " "):
            return json.loads(line[len(tag) + 1:])
    raise AssertionError(f"{tag} 行がありません:\n{stdout}")


def _read_mask(path: Path) -> np.ndarray:
    raw = path.read_bytes()
    w, h = np.frombuffer(raw[:8], "<i4")
    return np.frombuffer(raw[8:], np.uint8).reshape(h, w).astype(bool)


def _image(fill=GRAY) -> np.ndarray:
    rgba = np.zeros((H, W, 4), np.uint8)
    rgba[..., :3] = fill
    rgba[..., 3] = 255
    return rgba


def _rect(y0, y1, x0, x1) -> np.ndarray:
    """[y0, y1) × [x0, x1) が立ったマスク。"""
    m = np.zeros((H, W), bool)
    m[y0:y1, x0:x1] = True
    return m


class Leak:
    """1 つのテストの入出力ファイル（tmp_path の中）。ゾーンは赤の平塗りを指す（--sel を渡す判定・塊のテストでは選択に効かない）。"""

    def __init__(self, harness: Path, tmp: Path, rgba: np.ndarray, evidence: np.ndarray, settings: dict | None = None):
        self.harness, self.tmp = harness, tmp
        self.img, self.mask, self.ev, self.zones = tmp / "in.raw", tmp / "mask.raw", tmp / "ev.raw", tmp / "zones.json"
        G.write_raw(self.img, rgba)
        G.write_raw(self.mask, np.zeros((H, W), np.uint8))
        G.write_raw(self.ev, evidence.astype(np.uint8))
        zone = G.zone(sample=[c / 255 for c in RED], target=[0.1, 0.8, 0.2], evidenceMask=str(self.ev))
        self.zones.write_text(json.dumps({"zones": [zone], "settings": settings or G.settings()}), encoding="utf-8")

    def _write_sel(self, sel: np.ndarray | None) -> list[str]:
        if sel is None:
            return []
        p = self.tmp / "sel_in.raw"
        G.write_raw(p, sel.astype(np.uint8))
        return ["--sel", str(p)]

    def probe(self, sel: np.ndarray | None = None) -> tuple[dict, np.ndarray]:
        out = self.tmp / "sel_out.raw"
        r = _run(self.harness, "--leak-probe", self.img, self.mask, self.zones, out, *self._write_sel(sel))
        assert r.returncode == 0, f"--leak-probe 失敗 (exit={r.returncode}):\n{r.stderr}\n{r.stdout}"
        return _json_line(r.stdout, "LEAKPROBE"), _read_mask(out)

    def judge(self, sel: np.ndarray, blobs: list[tuple], extra: tuple = ()) -> tuple[dict, np.ndarray]:
        """blobs は塊ごとの (自動の提案, 背景まで広がったか, 細かい提案, 背景まで広がったか)。提案は None 可。
        extra は --leak-judge へ足す引数(閾値の差し替え)。"""
        entries = []
        for k, (p2, flood, p2f, ffl) in enumerate(blobs):
            e = {"flood": flood, "fineFlood": ffl, "proposal": None, "fine": None}
            for key, m in (("proposal", p2), ("fine", p2f)):
                if m is not None:
                    p = self.tmp / f"{key}{k}.raw"
                    G.write_raw(p, m.astype(np.uint8))
                    e[key] = str(p)
            entries.append(e)
        pj = self.tmp / "proposals.json"
        pj.write_text(json.dumps({"blobs": entries}), encoding="utf-8")
        out = self.tmp / "excl.raw"
        r = _run(self.harness, "--leak-judge", self.img, self.mask, self.zones, pj, out, *self._write_sel(sel), *extra)
        assert r.returncode == 0, f"--leak-judge 失敗 (exit={r.returncode}):\n{r.stderr}\n{r.stdout}"
        return _json_line(r.stdout, "LEAKJUDGE"), _read_mask(out)


# ─────────────── 選択だけの入口 ───────────────

def test_simulate_selection_matches_recolored_pixels(harness, tmp_path):
    """平塗り（縁に混色が無い）の画像では、選択だけの入口が選んだ画素 = 通常の色替えで変わった画素。"""
    rgba = _image()
    a, b = _rect(20, 80, 20, 100), _rect(150, 200, 120, 220)
    rgba[a, :3] = RED
    rgba[b, :3] = RED
    rgba[_rect(20, 80, 150, 230), :3] = (40, 60, 200)
    for decontam in (False, True):
        work = tmp_path / f"d{int(decontam)}"
        work.mkdir()
        st = G.settings(useDecontamination=decontam)
        lk = Leak(harness, work, rgba, a, settings=st)
        info, sel = lk.probe()
        z = G.zone(sample=[c / 255 for c in RED], target=[0.1, 0.8, 0.2])
        out = G.run_csharp(rgba, z, st, work / "plain")
        changed = np.any(out[..., :3] != rgba[..., :3], axis=-1)
        assert changed.sum() > 0
        assert np.array_equal(sel, changed), (
            f"decontam={decontam}: 選択 {int(sel.sum())} 画素 / 変わった画素 {int(changed.sum())}、"
            f"食い違い {int((sel != changed).sum())}")
        assert info["selPx"] == int(sel.sum())
        # 塊: 証拠 a の外の赤い四角 b(50×100 = 5000 画素)。
        assert [(bl["area"]) for bl in info["blobs"]] == [int(b.sum())]


# ─────────────── 塊と問い合わせ点 ───────────────

def test_blobs_margin_connectivity_area_and_point(harness, tmp_path):
    ev = _rect(10, 60, 10, 50)                      # 証拠（クリックした部品）
    strip = _rect(10, 60, 50, 70)                   # 証拠の右に接する帯。x ≥ 58（L1 距離 9 以上）は広げた所に接するので塊にしない
    big = _rect(120, 160, 120, 170)                 # 40×50 = 2000
    mid = _rect(200, 225, 20, 50)                   # 25×30 = 750
    tiny = _rect(230, 240, 200, 210)                # 100（面積の下限 400 より小さい）
    diag = _rect(80, 95, 200, 215) | _rect(95, 110, 215, 230)   # 15×15 が角だけで接する 2 つ = 8 近傍で 1 つ(450)
    sel = ev | strip | big | mid | tiny | diag
    lk = Leak(harness, tmp_path, _image(), ev)
    info, sel_out = lk.probe(sel)
    assert np.array_equal(sel_out, sel), "--sel で渡した選択がそのまま書き戻されていません"
    areas = [b["area"] for b in info["blobs"]]
    assert areas == [2000, 750, 450], f"塊の面積と順: {areas}"
    # 大きい塊(行 120..159・列 120..169)のいちばん内側: 上下の端から 20 画素。同じ深さの画素のうち添字の小さい方。
    b0 = info["blobs"][0]
    assert (b0["x"], b0["y"], b0["depthSq"]) == (139, 139, 400), b0
    # 斜めの塊の点は、どちらかの四角の中。
    b2 = info["blobs"][2]
    assert diag[b2["y"], b2["x"]], b2


def test_blobs_touching_the_widened_evidence_are_not_checked(harness, tmp_path):
    """証拠から選択のまま続いて、証拠を広げた所(L1 距離 8 以内)に接する塊は確かめない(AI の提案の境目が部品の境目と
    ずれた所。部品のぼかし・縁)。広げた所との間に選ばれていない画素が 1 列あれば、離れた塊として確かめる。"""
    ev = _rect(10, 60, 10, 50)                      # 証拠。右の端は列 49
    # 列 50..69 が続けて選ばれている: 列 58(距離 9)が広げた所(距離 8 以内)に接する → 塊にしない
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(ev | _rect(10, 60, 50, 70))
    assert info["blobs"] == [], info["blobs"]
    # 列 58 だけ選ばれていない: 列 59..69(距離 10 以上)は広げた所に接しない → 11 × 50 の塊
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(ev | _rect(10, 60, 50, 58) | _rect(10, 60, 59, 70))
    assert [b["area"] for b in info["blobs"]] == [11 * 50], info["blobs"]


def test_blobs_at_most_six_largest(harness, tmp_path):
    ev = _rect(0, 10, 0, 10)
    sel = ev.copy()
    sides = [21, 22, 23, 24, 25, 26, 27, 28]           # 面積 441〜784 の 8 つ
    for k, s in enumerate(sides):
        y0, x0 = 40 + (k // 4) * 100, 20 + (k % 4) * 58
        sel[y0:y0 + s, x0:x0 + s] = True
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(sel)
    assert [b["area"] for b in info["blobs"]] == [s * s for s in sorted(sides, reverse=True)[:6]]


def test_no_blobs_when_the_clicked_part_is_mostly_unpainted(harness, tmp_path):
    """証拠(クリックした部品)のうち選ばれている割合が半分未満なら、離れた塊があっても確かめない(ゾーンが部品の一部の
    明るさだけを選んでいる)。半分以上なら確かめる。"""
    ev = _rect(10, 60, 10, 50)                      # 50 行 × 40 列 = 2000
    far = _rect(120, 160, 120, 170)                 # 離れた塊 2000
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(_rect(10, 34, 10, 50) | far)    # 証拠の 24/50 = 0.48
    assert info["blobs"] == [], info["blobs"]
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(_rect(10, 35, 10, 50) | far)    # 証拠の 25/50 = 0.50
    assert [b["area"] for b in info["blobs"]] == [2000], info["blobs"]


def test_blobs_none_without_leak(harness, tmp_path):
    ev = _rect(10, 60, 10, 50)
    info, _ = Leak(harness, tmp_path, _image(), ev).probe(ev | _rect(10, 60, 50, 57))
    assert info["blobs"] == []


# ─────────────── 判定 ───────────────
# 部品 A(証拠。選ばれている)と部品 B(行 100..199・列 100..199)。B の上の 3 割(行 100..129)だけが選ばれている。
A = _rect(10, 60, 10, 60)
B = _rect(100, 200, 100, 200)
B_TOP = _rect(100, 130, 100, 200)
SAME = A | _rect(100, 120, 100, 200)        # 半分以上(2500 / 4500)が A と重なる提案


def _gradient_part() -> np.ndarray:
    """B を上から下へなだらかに明るくなる面にした画像(隣の行どうしの色の差は見分けられない)。"""
    rgba = _image()
    rgba[A, :3] = RED
    for y in range(100, 200):
        v = 60 + (y - 100)          # 1 行で 1 階調
        rgba[y, 100:200, :3] = (v, v // 2, 20)
    return rgba


def _two_color_part() -> np.ndarray:
    """B を、選ばれた上の 3 割と残りではっきり違う色にした画像(境目が見える縁)。"""
    rgba = _image()
    rgba[A, :3] = RED
    rgba[B_TOP, :3] = (40, 40, 200)
    rgba[B & ~B_TOP, :3] = (220, 200, 40)
    return rgba


def test_same_part_is_kept(harness, tmp_path):
    """提案の半分以上が証拠と重なる(クリックした部品の続き)なら外さない。"""
    sel = A | B_TOP
    info, excl = Leak(harness, tmp_path, _gradient_part(), A).judge(sel, [(SAME, False, B, False)])
    v = info["blobs"][0]
    assert v["decision"] == "same-part", v
    assert v["same"] >= 0.5
    assert not excl.any() and info["excludedPx"] == 0


def test_partly_painted_part_is_excluded(harness, tmp_path):
    """部品の一部だけが塗られ、境目が面の途中(見分けられない色の差)を通り、細かい提案でも一部だけなら、提案 ∖ 証拠を外す。"""
    sel = A | B_TOP
    p2 = B | _rect(40, 60, 40, 60)          # 証拠と少し重なる(重なった所は外さない)
    info, excl = Leak(harness, tmp_path, _gradient_part(), A).judge(sel, [(p2, False, B, False)])
    v = info["blobs"][0]
    assert v["decision"] == "exclude", v
    assert v["cover"] == pytest.approx(int((p2 & sel).sum()) / int(p2.sum()))
    assert v["same"] == pytest.approx(400 / int(p2.sum()))
    assert v["seam"] == pytest.approx(1.0) and v["outlinePairs"] == 100, v
    assert v["needsFine"] and v["fineCover"] == pytest.approx(0.3)
    assert np.array_equal(excl, p2 & ~A), "外すのは提案から証拠を除いた所"
    assert info["excludedPx"] == int((p2 & ~A).sum())


def test_fine_proposal_mostly_painted_is_kept(harness, tmp_path):
    """自動の提案では一部だけでも、同じ点の細かい提案がほぼ塗られていれば外さない(細い部品の上の点で服全体が返る形)。"""
    sel = A | B_TOP
    fine = _rect(100, 132, 100, 200)        # 細かい提案は塗られた帯そのもの(塗られた割合 0.94)
    info, excl = Leak(harness, tmp_path, _gradient_part(), A).judge(sel, [(B, False, fine, False)])
    v = info["blobs"][0]
    assert v["needsFine"] and v["fineCover"] > 0.5
    assert v["decision"] == "keep", v
    assert not excl.any()


def test_visible_edge_is_kept(harness, tmp_path):
    """塗りの境目が入力で見える縁に沿うなら(色で分かれた部品の一部)、外さない。"""
    sel = A | B_TOP
    info, excl = Leak(harness, tmp_path, _two_color_part(), A).judge(sel, [(B, False, B, False)])
    v = info["blobs"][0]
    assert v["outlinePairs"] == 100 and v["seamPairs"] == 0, v
    assert not v["needsFine"] and v["decision"] == "keep", v
    assert not excl.any()


def test_flood_or_missing_proposal_is_skipped(harness, tmp_path):
    """提案が背景まで広がった・無い・空なら何もしない。細かい提案が背景まで広がった・無いなら外さない。"""
    sel = A | B_TOP
    empty = np.zeros((H, W), bool)
    info, excl = Leak(harness, tmp_path, _gradient_part(), A).judge(sel, [
        (B, True, B, False),        # 自動の提案が背景まで広がった
        (None, False, B, False),    # 自動の提案が無い
        (empty, False, B, False),   # 空
        (B, False, B, True),        # 細かい提案が背景まで広がった
        (B, False, None, False),    # 細かい提案が無い
    ])
    d = [v["decision"] for v in info["blobs"]]
    assert d == ["skip", "skip", "skip", "keep", "keep"], d
    assert info["blobs"][3]["needsFine"] and info["blobs"][3]["fineCover"] is None
    assert not excl.any()


def test_thresholds_override_changes_decision(harness, tmp_path):
    """--cover-max / --seam-min / --fine-max(テストの掃引用)を渡すと判定が変わる。省略は製品の閾値(0.5・0.10・0.5)と同じ。"""
    sel = A | B_TOP
    blobs = [(B, False, B, False)]                       # 塗られた割合 0.3・細かい提案も 0.3
    rgba = _gradient_part()                              # 継ぎ目 1.0

    def decide(extra, sub, image=rgba):
        d = tmp_path / sub
        d.mkdir()
        info, excl = Leak(harness, d, image, A).judge(sel, blobs, extra)
        return info, info["blobs"][0]["decision"], excl

    info, base, _ = decide((), "default")
    assert base == "exclude"
    assert (info["coverMax"], info["seamMin"], info["fineMax"]) == (0.5, 0.10, 0.5)
    info, same, _ = decide(("--cover-max", "0.5", "--seam-min", "0.10", "--fine-max", "0.5"), "explicit")
    assert same == "exclude"
    assert decide(("--cover-max", "0.25"), "cover")[1] == "keep"        # 0.3 は 0.25 未満でない
    assert decide(("--fine-max", "0.25"), "fine")[1] == "keep"
    assert decide(("--seam-min", "1.01"), "seam")[1] == "keep"
    # 境目が見える縁(継ぎ目 0)でも、継ぎ目の下限を 0 にすれば外す。
    _, d0, excl = decide(("--seam-min", "0"), "edge", image=_two_color_part())
    assert d0 == "exclude" and np.array_equal(excl, B)
    assert decide((), "edge-default", image=_two_color_part())[1] == "keep"


def test_union_of_excluded_blobs(harness, tmp_path):
    """外す塊が 2 つあれば、除外マスクはそれぞれの提案 ∖ 証拠の和。外さない塊の提案は入らない。"""
    rgba = _gradient_part()
    c = _rect(210, 250, 100, 200)               # 3 つ目の部品(なだらかな面)。上の 1 割だけ塗られている
    for y in range(210, 250):
        v = 40 + (y - 210)
        rgba[y, 100:200, :3] = (20, v, v)
    c_top = _rect(210, 214, 100, 200)
    sel = A | B_TOP | c_top
    info, excl = Leak(harness, tmp_path, rgba, A).judge(sel, [
        (B, False, B, False),
        (c, False, c, False),
        (SAME, False, None, False),             # 同じ部品(外さない)
    ])
    assert [v["decision"] for v in info["blobs"]] == ["exclude", "exclude", "same-part"]
    assert np.array_equal(excl, B | c)
