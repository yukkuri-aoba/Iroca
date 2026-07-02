"""C# 自己ゴールデン回帰テストの共有ロジック。

実 C# 実装（scripts/headless-run の Harness, AssemblyName=IrocaHeadless）を合成入力で走らせ、
出力ハッシュを golden（コミット済み golden_hashes.json）と照合する。

このプロジェクトでは **C# が製品の唯一の正** であり、Python(dev_safe/vacc_python) はアルゴリズム
試作のための使い捨てスキャフォールドである。よって「C#↔Python 一致」ではなく、
**C# 自身の出力が黙って変化していないか**（過去に実在した FillSmallHoles 段の消失・OkLab ハイライト
L キャップ欠落のようなリグレッション）を検出するのが本テストの目的。Python には一切依存しない
（必要なのは dotnet + Unity CoreModule DLL のみ）。

ゴールデンは toolchain（Unity 2022.3.22f1 / dotnet ランタイム）に紐づく。意図的にアルゴリズムを
変更したとき、または toolchain を更新したときは `python scripts/golden/golden_lib.py` で再生成する。
"""
from __future__ import annotations

import hashlib
import json
import struct
import subprocess
from pathlib import Path

import numpy as np

import synth_textures as S

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
HARNESS_CSPROJ = REPO / "scripts" / "headless-run" / "Harness.csproj"
HARNESS_DLL = REPO / "scripts" / "headless-run" / "bin" / "Release" / "IrocaHeadless.dll"
GOLDEN_FILE = HERE / "golden_hashes.json"

# ── 正準 zone パラメータ（Harness の JSON スキーマ ZoneCfg と同名フィールド） ──
# 全フィールドを明示し、ケースごとに必要分だけ上書きする（既定値依存を避ける）。
CANON_ZONE = dict(
    name="Z",
    sample=(1.0, 1.0, 1.0),
    target=(0.0, 0.0, 0.0),
    tolerance=0.30,
    valueBlend=1.0,
    edgeSoftness=0.0,
    saturationStrictness=0.50,
    saturationGuard=0.0,
    chromaThreshold=0.05,
    shadowDesaturation=0.0,
    shadowForgivenessSatMin=0.05,
    outputSaturation=1.0,
    highlightRecovery=False,
    highlightBandExpand=True,
    applyHighlightWash=False,
    autoRecolorAnchor=False,
    layerIndex=0,
)

# ── 正準 settings パラメータ（Harness の SettingsCfg と同名フィールド） ──
CANON_SETTINGS = dict(
    edgeFeather=0.0,
    antiAliasCleanup=3,
    holeFillPasses=5,
    holeFillMinNeighbors=4,
    relaxedSatMin=0.02,
    relaxedSatRamp=0.08,
    useDecontamination=True,
    decontaminationRadius=4,
)


def zone(**overrides) -> dict:
    z = dict(CANON_ZONE)
    z.update(overrides)
    return z


def settings(**overrides) -> dict:
    s = dict(CANON_SETTINGS)
    s.update(overrides)
    return s


# ─────────────────────────── raw I/O（自己完結） ───────────────────────────
def write_raw(path: Path, arr: np.ndarray) -> None:
    """[int32 w][int32 h][payload] 形式で書き出す。"""
    h, w = arr.shape[:2]
    with open(path, "wb") as f:
        f.write(struct.pack("<ii", w, h))
        f.write(np.ascontiguousarray(arr).tobytes())


def read_raw_rgba(path: Path) -> np.ndarray:
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        b = np.frombuffer(f.read(), np.uint8)
    return b.reshape(h, w, 4)


# ─────────────────────────── C# 経路 ───────────────────────────
def dotnet_available() -> bool:
    try:
        return subprocess.run(["dotnet", "--version"], capture_output=True, text=True).returncode == 0
    except (FileNotFoundError, OSError):
        return False


def unity_dll_missing() -> str | None:
    """Harness が参照する Unity CoreModule DLL が無ければそのパスを返す（環境不備 = skip 対象）。

    Harness.csproj の既定 (UnityVersion / UnityManaged、環境変数で上書き可) をミラーする。
    これで「環境不備 (skip してよい)」と「Code/ のコンパイルエラー (fail すべき)」を
    テスト側で区別できる — 過去に DLL 名の陳腐化で実 C# を走らせないまま
    テストが緑に見えるサイレント skip 事故が起きている。
    """
    import os

    version = os.environ.get("UnityVersion", "2022.3.22f1")
    managed = os.environ.get(
        "UnityManaged",
        rf"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Data\Managed")
    dll = Path(managed) / "UnityEngine" / "UnityEngine.CoreModule.dll"
    return None if dll.exists() else str(dll)


def build_harness() -> tuple[bool, subprocess.CompletedProcess]:
    r = subprocess.run(
        ["dotnet", "build", str(HARNESS_CSPROJ), "-c", "Release", "-nologo", "-v", "quiet"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (r.returncode == 0 and HARNESS_DLL.exists()), r


def _zone_json(z: dict) -> dict:
    out = dict(z)
    out["sample"] = list(z["sample"])
    out["target"] = list(z["target"])
    return out


def run_csharp(rgba: np.ndarray, z: dict, s: dict, work: Path) -> np.ndarray:
    """C# Harness を 1 ゾーン・除外なしで走らせ、出力 RGBA を返す。"""
    work = Path(work)
    work.mkdir(parents=True, exist_ok=True)
    in_raw, mask_raw, out_raw, zj = work / "in.raw", work / "mask.raw", work / "out.raw", work / "z.json"
    write_raw(in_raw, rgba)
    write_raw(mask_raw, np.zeros(rgba.shape[:2], np.uint8))  # 全 0 = 除外なし
    zj.write_text(json.dumps({"zones": [_zone_json(z)], "settings": s}, ensure_ascii=False),
                  encoding="utf-8")
    r = subprocess.run(
        ["dotnet", str(HARNESS_DLL), str(in_raw), str(mask_raw), str(out_raw), "--zones", str(zj)],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        raise RuntimeError(f"Harness 実行失敗: {r.stderr}\n{r.stdout}")
    return read_raw_rgba(out_raw)


def output_hash(arr: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(arr).tobytes()).hexdigest()


# ─────────────────────────── テストケース定義 ───────────────────────────
def build_cases() -> list[tuple[str, np.ndarray, dict, dict]]:
    """(label, rgba, zone, settings) のリスト。C# の主要経路を広く踏ませる。

    各ケースが異なるコード経路（基底再着色 / 無彩ターゲット / グレーモード / L リマップ /
    ハイライト復元 / ハイライト L / AA デコンタミ / 彩度ゲート / 暗部脱彩 / 出力彩度 / wash /
    アンカー正規化 / 彩度ガード）を主に踏むよう選んでいる。
    """
    def n(rgb):
        return tuple(c / 255.0 for c in rgb)

    cases: list[tuple[str, np.ndarray, dict, dict]] = []

    # 基底再着色（有彩→有彩）
    cases.append(("solid_red_to_blue",
                  S.solid((200, 50, 50)),
                  zone(sample=n((200, 50, 50)), target=n((40, 70, 200)), tolerance=0.35), settings()))
    cases.append(("solid_blue_to_orange",
                  S.solid((40, 70, 200)),
                  zone(sample=n((40, 70, 200)), target=n((230, 140, 30)), tolerance=0.35), settings()))

    # 有彩→無彩ターゲット（黒 / 白）
    cases.append(("solid_red_to_black",
                  S.solid((200, 50, 50)),
                  zone(sample=n((200, 50, 50)), target=n((10, 10, 10)), tolerance=0.35), settings()))
    cases.append(("solid_red_to_white",
                  S.solid((200, 50, 50)),
                  zone(sample=n((200, 50, 50)), target=n((240, 240, 240)), tolerance=0.35), settings()))

    # 無彩（グレー）サンプル → 有彩（C# グレーモード = 純 RGB 距離）
    cases.append(("solid_gray_to_red",
                  S.solid((130, 130, 130)),
                  zone(sample=n((130, 130, 130)), target=n((200, 40, 40)), tolerance=0.30), settings()))

    # 明度リマップ（グラデ）
    cases.append(("grad_red_to_green",
                  S.vertical_gradient((230, 60, 60), (70, 20, 20)),
                  zone(sample=n((150, 40, 40)), target=n((40, 160, 60)), tolerance=0.45), settings()))
    cases.append(("grad_blue_to_white",
                  S.vertical_gradient((40, 70, 200), (10, 18, 60)),
                  zone(sample=n((30, 52, 150)), target=n((240, 240, 240)), tolerance=0.50), settings()))

    # ハイライト復元（明部スペキュラ）
    cases.append(("hl_red_to_bright_yellow",
                  S.shaded_with_highlight((200, 40, 40)),
                  zone(sample=n((150, 30, 30)), target=n((230, 210, 40)), tolerance=0.50,
                       highlightRecovery=True), settings()))
    # ハイライト + 暗ターゲット（OkLab ハイライト L キャップ領域）
    cases.append(("hl_red_to_dark_navy",
                  S.shaded_with_highlight((200, 40, 40)),
                  zone(sample=n((150, 30, 30)), target=n((20, 25, 70)), tolerance=0.50,
                       highlightRecovery=True), settings()))
    # ハイライト wash 経路
    cases.append(("hl_red_to_blue_wash",
                  S.shaded_with_highlight((200, 40, 40)),
                  zone(sample=n((150, 30, 30)), target=n((40, 70, 200)), tolerance=0.50,
                       highlightRecovery=True, applyHighlightWash=True), settings()))

    # AA 境界デコンタミ
    cases.append(("aa_red_green_to_blue",
                  S.two_color_aa((210, 50, 50), (50, 180, 70)),
                  zone(sample=n((210, 50, 50)), target=n((40, 70, 200)), tolerance=0.30), settings()))

    # 無彩背景の過検出抑制（彩度整合ゲート）
    cases.append(("chroma_on_white_to_blue",
                  S.chromatic_on_neutral((210, 50, 50), 235),
                  zone(sample=n((210, 50, 50)), target=n((40, 70, 200)), tolerance=0.30), settings()))

    # 暗部脱彩
    cases.append(("solid_red_to_blue_shadowdesat",
                  S.vertical_gradient((230, 60, 60), (60, 16, 16)),
                  zone(sample=n((150, 40, 40)), target=n((40, 70, 200)), tolerance=0.45,
                       shadowDesaturation=0.35), settings()))

    # 出力彩度スケール
    cases.append(("solid_red_to_blue_outsat",
                  S.solid((200, 50, 50)),
                  zone(sample=n((200, 50, 50)), target=n((40, 70, 200)), tolerance=0.35,
                       outputSaturation=0.5), settings()))

    # 再着色アンカー正規化（既定 ON 経路を明示）
    cases.append(("grad_red_to_blue_anchor",
                  S.vertical_gradient((230, 60, 60), (70, 20, 20)),
                  zone(sample=n((90, 24, 24)), target=n((40, 70, 200)), tolerance=0.45,
                       autoRecolorAnchor=True), settings()))

    # 彩度ガード（高彩度サンプルで中性画素を hard reject）
    cases.append(("chroma_on_white_satguard",
                  S.chromatic_on_neutral((210, 50, 50), 235),
                  zone(sample=n((210, 50, 50)), target=n((40, 70, 200)), tolerance=0.40,
                       saturationGuard=0.5), settings()))

    # 非既定 chromaThreshold の出力ロック（W2）。他の全ケースは既定 chromaThreshold=0.05 のため、
    # chromaThreshold の plumbing（主経路 ColorZone と緩和経路 GetRelaxedMatchStrength の双方が
    # zone.chromaThreshold を使う）が壊れても検出できなかった。微かに tint した明るいグレー（sS≈0.10）を
    # 非既定 0.15 でグレーモードに入れる（sS=0.10 は 0.05 なら有彩分岐、0.15 ならグレー分岐）。W2 は
    # 緩和経路の chromaThreshold 焼き込み（0.05）を撤去し zone 値へ追従させた修正で、これはその値を
    # 通した出力を固定する。※このフィクスチャ自体の出力は主に主経路の分岐で決まる（緩和経路の寄与は
    # 小さい）ため、緩和経路専用のロックではなく chromaThreshold 全体の非既定回帰ガードとして機能する。
    cases.append(("nondefault_chromathreshold_graymode",
                  S.two_color_aa((200, 190, 180), (150, 60, 60)),
                  zone(sample=n((200, 190, 180)), target=n((40, 160, 60)), tolerance=0.35,
                       chromaThreshold=0.15), settings()))

    return cases


# ─────────────────────────── ゴールデン I/O ───────────────────────────
def load_golden() -> dict:
    if not GOLDEN_FILE.exists():
        return {}
    return json.loads(GOLDEN_FILE.read_text(encoding="utf-8")).get("cases", {})


def regenerate() -> int:
    """全ケースを C# で走らせ golden_hashes.json を書き出す。toolchain 変更/意図的変更時に実行。"""
    import tempfile

    if not dotnet_available():
        print("dotnet が見つかりません。中止。")
        return 1
    ok, r = build_harness()
    if not ok:
        print("Harness ビルド失敗（Unity DLL 不在?）:\n" + (r.stdout or "")[-800:])
        return 1

    out_cases: dict = {}
    with tempfile.TemporaryDirectory() as td:
        work = Path(td)
        for label, rgba, z, s in build_cases():
            arr = run_csharp(rgba, z, s, work)
            out_cases[label] = {"sha256": output_hash(arr), "shape": list(arr.shape)}
            print(f"  {label:32s} {out_cases[label]['sha256'][:16]} shape={out_cases[label]['shape']}")

    doc = {
        "_meta": {
            "note": "C# 自己ゴールデン（製品 C# の出力ハッシュ）。Python 非依存の回帰検出。"
                    "意図的変更・toolchain 更新時は `python scripts/golden/golden_lib.py` で再生成。",
            "unity": "2022.3.22f1",
            "assembly": "IrocaHeadless",
        },
        "cases": dict(sorted(out_cases.items())),
    }
    GOLDEN_FILE.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"\n{len(out_cases)} 件を {GOLDEN_FILE} に書き出しました。")
    return 0


if __name__ == "__main__":
    import sys
    sys.exit(regenerate())
