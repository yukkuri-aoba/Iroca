"""インタラクティブマニュアル用の画像素材を作る。

生成するもの:
  img/demo/*.webp  … 実 C# エンジン（headless ハーネス）でパラメータを振った再着色結果。
                     マニュアルのスライダー実演が参照する。

**製品 C# が唯一の正** なので、デモ画像も Python 再実装ではなく
`scripts/headless-run` の実ハーネス経由で作る（CLAUDE.md / docs/testing-architecture.md）。

被写体・サンプル色・パレットは **販促素材と同じ**（`dev_safe/scripts/build_promo_textures.py`）:
HAOLAN スニーカーの青 (32,0,144) を伝統色へ。色見本は実機の「自動調整」と同じ
`--autotune` 経路で作るので、ユーザーがスポイト 1 クリック → 自動調整を押した結果と一致する。
パラメータ実演（許容範囲・模様保持・出力彩度）だけは効果を単独で見せるため tolerance を固定する。

前提:
  - dev_safe/（プライベート側）の素材と `Tests/regression/fixtures.py`
  - .NET SDK 8 と Unity CoreModule DLL（ハーネスのビルドに必要）

実行:
  .venv\\Scripts\\python.exe docs/manual/build_assets.py
"""
from __future__ import annotations

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "dev_safe"))
sys.path.insert(0, str(ROOT / "dev_safe" / "Tests"))

import numpy as np
from PIL import Image

from regression import fixtures as F
from regression import headless_io as hio

TEXTURE = ROOT / "dev_safe" / "texture_sample" / "HAOLAN" / "Texture" / "HAOLAN_Sneakers.png"
OUT = ROOT / "docs" / "manual" / "img" / "demo"

SAMPLE = (32, 0, 144)          # 青。販促・GT 検証と同じスポイト色
SHU = (220, 75, 46)            # 朱。実演の既定の変更先

# 販促と同じ伝統色パレット（元が青なので青系は入れない）。
PALETTE = [
    ("shu", "朱", SHU),
    ("akane", "茜", (166, 42, 55)),
    ("yamabuki", "山吹", (242, 163, 60)),
    ("wakakusa", "若草", (111, 191, 91)),
    ("tokiwa", "常磐", (34, 118, 87)),
    ("seiji", "青磁", (110, 186, 168)),
    ("sumire", "菫", (122, 74, 178)),
    ("sakura", "桜", (238, 169, 186)),
]

# 青いトゲ・白い靴本体・黒いアッパーが同時に入る左上の正方形。
CROP = (0, 0, 2200, 2200)
PX = 560
WEBP = dict(format="WEBP", quality=86, method=5)

BASE_TOLERANCE = 0.25
TOLERANCES = [0.08, 0.16, 0.25, 0.40, 0.60]
BLENDS = [0.0, 0.25, 0.50, 0.75, 1.0]
SATURATIONS = [0.50, 0.70, 0.85, 1.00]


def zone(target_rgb, tolerance=BASE_TOLERANCE, **over) -> dict:
    """販促スクリプトと同一の zone 設定（tolerance だけ呼び出し側で決める）。"""
    z = {
        "name": "manual-demo",
        "sample": [c / 255.0 for c in SAMPLE],
        "target": [c / 255.0 for c in target_rgb],
        "tolerance": tolerance,
        "valueBlend": 1.0,
        "edgeSoftness": 0.30,
        "saturationStrictness": 0.50,
        "saturationGuard": 0.0,
        "chromaThreshold": 0.05,
        "shadowDesaturation": 0.0,
        "shadowForgivenessSatMin": 0.05,
        "outputSaturation": 1.0,
        "highlightRecovery": True,
        "highlightBandExpand": True,
        "applyHighlightWash": False,
        "autoRecolorAnchor": True,
        "useFloodFill": True,
        "layerIndex": 0,
    }
    z.update(over)
    return z


def autotune(rgba: np.ndarray, target_rgb, tag: str) -> np.ndarray:
    """実機の「自動調整」と同じ --autotune 経路で走らせる（販促と同一）。"""
    dll = F.ensure_harness()
    work = F._CSHARP_WORK
    work.mkdir(parents=True, exist_ok=True)
    in_raw, mask_raw = work / f"{tag}_in.raw", work / f"{tag}_mask.raw"
    out_raw, zones_json = work / f"{tag}_out.raw", work / f"{tag}_zones.json"
    hio.write_raw(in_raw, rgba)
    hio.write_raw(mask_raw, np.zeros(rgba.shape[:2], np.uint8))
    hio.write_zones_json(zones_json, [zone(target_rgb)], F._settings_to_harness(F.default_settings()))
    r = hio.run(["dotnet", str(dll), str(in_raw), str(mask_raw), str(out_raw),
                 "--zones", str(zones_json), "--autotune"])
    if r.returncode != 0:
        raise RuntimeError(f"harness failed ({tag}):\n{r.stderr}")
    if r.stdout.strip():
        print("   ", r.stdout.strip().replace("\n", " / "))
    return hio.read_raw_rgba(out_raw)


def save(rgb: np.ndarray, name: str, crop: bool = True) -> None:
    im = Image.fromarray(rgb)
    im = im.crop(CROP) if crop else im
    im.resize((PX, PX), Image.LANCZOS).save(OUT / f"{name}.webp", **WEBP)


# ── Unity Editor のスクリーンショット ──────────────────────────────
# 原板は dev_safe/manual_shots/*.png（PrintWindow で撮った実ウィンドウ。撮影手順は
# dev_safe/scripts/unity_ui_shot/README.md）。ここでは切り出して WebP にするだけ。
# 座標は pixelsPerPoint 1.25 の物理ピクセル。
SHOTS_SRC = ROOT / "dev_safe" / "manual_shots"
SHOTS_OUT = ROOT / "docs" / "manual" / "img" / "ui"
SHOTS = [
    # (出力名, 原板, クロップ or None=全体)
    ("window", "window.png", None),
    ("zone-card", "window.png", (6, 112, 402, 500)),
    ("preview", "window.png", (400, 112, 1012, 682)),
    ("zone-detail", "zone-detail.png", (6, 215, 402, 815)),
    ("processing", "processing-presets.png", (6, 437, 402, 692)),
    ("mask-section", "processing-presets.png", (6, 697, 402, 778)),
    ("presets", "processing-presets.png", (6, 778, 402, 962)),
    ("preview-compare", "preview-compare.png", (400, 112, 1012, 682)),
    ("preview-diff", "preview-diff.png", (400, 112, 1012, 682)),
    ("mask-window", "mask-window.png", None),
]


def build_shots() -> None:
    if not SHOTS_SRC.exists():
        print(f"スクリーンショット原板なし（スキップ）: {SHOTS_SRC}")
        return
    SHOTS_OUT.mkdir(parents=True, exist_ok=True)
    print("スクリーンショット")
    for name, src, crop in SHOTS:
        path = SHOTS_SRC / src
        if not path.exists():
            print(f"   !! 原板なし: {src}")
            continue
        im = Image.open(path).convert("RGB")
        if crop:
            im = im.crop(crop)
        im.save(SHOTS_OUT / f"{name}.webp", format="WEBP", quality=90, method=5)
        print(f"   {name} {im.size[0]}x{im.size[1]}")


def main() -> None:
    build_shots()
    OUT.mkdir(parents=True, exist_ok=True)
    rgba = np.array(Image.open(TEXTURE).convert("RGBA"))
    settings = F._settings_to_harness(F.default_settings())
    print(f"{TEXTURE.name} {rgba.shape[1]}x{rgba.shape[0]}")

    save(rgba[:, :, :3], "src")
    save(rgba[:, :, :3], "src-full", crop=False)

    print("色見本（自動調整経路 = 販促と同一）")
    for slug, jp, target in PALETTE:
        out = autotune(rgba, target, tag=f"manual_color_{slug}")
        save(out[:, :, :3], f"color-{slug}")
        print(f"  {jp:<3} {slug}")

    print("パラメータ実演（tolerance 固定）")
    jobs: list[tuple[str, dict, bool]] = []
    for t in TOLERANCES:
        tag = f"tol-{int(round(t * 100)):03d}"
        jobs.append((tag, zone(SHU, tolerance=t), True))
        jobs.append((tag + "-full", zone(SHU, tolerance=t), False))
    for b in BLENDS:
        jobs.append((f"blend-{int(round(b * 100)):03d}", zone(SHU, valueBlend=b), True))
    for s in SATURATIONS:
        jobs.append((f"sat-{int(round(s * 100)):03d}", zone(SHU, outputSaturation=s), True))

    uniq: dict[str, dict] = {}
    for _, z, _crop in jobs:
        uniq.setdefault(hio.zones_json_text([z], settings), z)
    keys = list(uniq)
    outs = F.run_harness_many(rgba, [([uniq[k]], settings, f"manual-{i}") for i, k in enumerate(keys)])
    by_key = dict(zip(keys, outs))

    for name, z, crop in jobs:
        save(by_key[hio.zones_json_text([z], settings)][:, :, :3], name, crop=crop)
        print("  ", name)

    print(f"done -> {OUT}")


if __name__ == "__main__":
    main()
