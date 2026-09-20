"""インタラクティブマニュアル用の画像素材を作る。

生成するもの:
  img/demo/*.webp  … 実 C# エンジン（headless ハーネス）でパラメータを振った再着色結果。
                     マニュアルのスライダー実演が参照する。

**製品 C# が唯一の正** なので、デモ画像も Python 再実装ではなく
`scripts/headless-run` の実ハーネス経由で作る（CLAUDE.md / docs/testing-architecture.md）。

被写体・サンプル色・パレットは **販促素材と同じ**（`dev_safe/scripts/build_promo_textures.py`）:
HAOLAN スニーカーの青を別の色へ。色見本は製品のワンショット経路
（スポイト位置の AI マスク提案を証拠にした自動調整）で作るので、ユーザーがスポイト 1 クリック →
自動調整を押した結果と一致する。**証拠なしの `--autotune` では作らない** — ハイライトの芯が
青く取り残される（2026-09-20 に一度それで出荷しかけた）。
模様保持・出力彩度の実演は自動調整の導出値を土台に 1 項目だけ動かす。許容範囲だけは素のゾーンで振る。

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
sys.path.insert(0, str(ROOT / "dev_safe" / "scripts"))
sys.path.insert(0, str(ROOT / "tools"))

import numpy as np
from PIL import Image

from regression import fixtures as F
from regression import headless_io as hio

TEXTURE = ROOT / "dev_safe" / "texture_sample" / "HAOLAN" / "Texture" / "HAOLAN_Sneakers.png"
OUT = ROOT / "docs" / "manual" / "img" / "demo"
SUBJECT_ID = "haolan-sneakers"

# スポイト位置（テクスチャ画素座標）。作者プリセット HAOLAN_Sneakers_Red のサンプル色
# (63,0,242) と同じ、トゲの明るい青。スクリーンショットの菱形もここに出す。
CLICK_XY = (700, 230)
# 実演の既定の変更先 = 作者プリセットと同じ純赤。ここは固定（販促の赤と食い違わせない）。
RED = (255, 0, 0)

# 色見本。色名は誰でも分かる一般的な呼び名にする（和名・伝統色名は使わない）。
# 元が青なので青は入れない。
PALETTE = [
    ("red", "赤", RED),
    ("orange", "オレンジ", (255, 128, 0)),
    ("yellow", "黄", (255, 210, 0)),
    ("green", "緑", (40, 170, 70)),
    ("cyan", "水色", (60, 190, 220)),
    ("purple", "紫", (140, 70, 200)),
    ("pink", "ピンク", (255, 130, 170)),
]

# 青いトゲと、変わらない黒いアッパーが入る左上の正方形。
# 右隣の UV にじみ（淡いラベンダーのグラデーション。GT 審査 2026-09-14 で放置と決めた既知の残り）は
# 枠に入れない。
CROP = (0, 0, 1240, 1240)
PX = 560
WEBP = dict(format="WEBP", quality=88, method=5)

# 許容範囲の実演だけは「自動調整を押していない素のゾーン」で振る。自動調整後は彩度ガードと
# 陰影の明度下限が入り、許容範囲を上げてもほぼはみ出さない（製品として正しいが、スライダーの
# 効き方は見えなくなる）。素のゾーンだと狭い側でハイライトの芯が残り、0.60 で黒いアッパーへ回る。
TOLERANCES = [0.05, 0.20, 0.30, 0.45, 0.60]
# 模様保持は 0.5 未満を出さない。0 に近づけると明度だけが平らになり、ハイライトの芯が灰色に
# 沈んで見える（製品の実出力だが、設定の説明図としては誤解を招く）。
BLENDS = [0.75, 0.90, 1.0]   # 0.90 = 純赤で自動調整が選ぶ値。0.5 以下は純赤だとハイライトが灰色がかる
SATURATIONS = [0.50, 0.70, 0.85, 1.00]


def evidence_mask(rgba: np.ndarray) -> Path:
    """製品の「自動調整」と同じく、スポイト位置の AI マスク提案（MobileSAM）を証拠にする。"""
    import measure_evidence_autotune as mea
    import sam_proposal as sp
    subject = next(v for v in vars(F).values()
                   if isinstance(v, F.RecolorSubject) and v.subject_id == SUBJECT_ID)
    seg, info = sp.proposal_for_click_full(
        F.ensure_harness(), subject, rgba, CLICK_XY[0], CLICK_XY[1],
        emb=sp.load_embedding(subject), work_dir=F._CSHARP_WORK / "manual_sam")
    if info["flood_warning"]:
        raise SystemExit("AI 提案が背景まで広がった（製品は証拠にしない）。スポイト位置を見直す")
    return mea._evidence_raw(seg, "manual_demo")


def autotuned(rgba, sample, target_rgb, ev: Path):
    zone = {"name": "manual-demo", "sample": list(sample),
            "target": [c / 255.0 for c in target_rgb], "evidenceMask": str(ev)}
    return F.run_harness_autotune(rgba, [zone], {}, tag="manual_demo_ev")


def explicit_zone(params: dict, target_rgb, **over) -> dict:
    """自動調整の導出値を、自動調整なしで再現するゾーン（1 項目だけ動かす実演の土台）。"""
    z = {
        "name": "manual-demo",
        "sample": list(params["sample"]),
        "samples": params.get("autoSampleColors") or None,
        "target": [c / 255.0 for c in target_rgb],
        "outputSaturation": 1.0,
    }
    # valueBlend も導出値を使う（純色の変更先では自動調整が 0.9 へ下げる。1.0 固定だと再現がずれる）
    for k in ("tolerance", "valueBlend", "saturationStrictness", "saturationGuard", "chromaThreshold",
              "highlightRecovery", "edgeSoftness", "shadowDesaturation",
              "shadowForgivenessSatMin", "shadowValueFloor", "chromaCeiling"):
        z[k] = params[k]
    z.update(over)
    return z


def save(rgb: np.ndarray, name: str) -> None:
    Image.fromarray(rgb).crop(CROP).resize((PX, PX), Image.LANCZOS).save(OUT / f"{name}.webp", **WEBP)


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
    ("zone-detail", "zone-detail.png", (6, 215, 402, 815)),
    ("processing", "processing-presets.png", (6, 437, 402, 692)),
    ("mask-section", "processing-presets.png", (6, 697, 402, 778)),
    ("presets", "processing-presets.png", (6, 778, 402, 962)),
    # 前後比較は 2 面並ぶので、窓を 1320pt 幅に広げて撮った原板から切り出す（800pt だと「変更後」が切れる）
    ("preview-compare", "preview-compare.png", (562, 112, 1660, 700)),
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
    for old in OUT.glob("*.webp"):
        old.unlink()
    rgba = np.array(Image.open(TEXTURE).convert("RGBA"))
    print(f"{TEXTURE.name} {rgba.shape[1]}x{rgba.shape[0]}")
    x, y = CLICK_XY
    sample = tuple(float(c) / 255.0 for c in rgba[y, x, :3])
    ev = evidence_mask(rgba)

    save(rgba[:, :, :3], "src")

    print("色見本（スポイト 1 回 → 自動調整。製品のワンショット経路）")
    base = None
    for slug, jp, target in PALETTE:
        out, params = autotuned(rgba, sample, target, ev)
        if not params.get("evidence"):
            raise SystemExit(f"{slug}: 証拠が使われていない: {params.get('evidenceDiag')}")
        save(out[:, :, :3], f"color-{slug}")
        print(f"   {jp} tol={params['tolerance']:.2f}")
        if slug == "red":
            base, base_out = params, out

    settings = F._settings_to_harness(F.default_settings())
    if base.get("applyGlobals"):
        settings["antiAliasCleanup"] = base["antiAliasCleanup"]

    # 導出値の再現が自動調整の出力と一致することを確かめてから、1 項目ずつ動かす
    ref = F.run_harness_many(rgba, [([explicit_zone(base, RED)], settings, "manual-ref")])[0]
    diff = int(np.any(ref[..., :3] != base_out[..., :3], axis=-1).sum())
    print(f"パラメータ実演の土台: tol={base['tolerance']:.2f} / 自動調整の出力との差 {diff:,} px")
    if diff > rgba.shape[0] * rgba.shape[1] * 0.001:
        raise SystemExit("導出値の再現が自動調整の出力と合わない（スキーマの写し漏れを疑う）")

    jobs: list[tuple[str, dict]] = []
    for t in TOLERANCES:
        plain = {"name": "manual-demo", "sample": list(sample),
                 "target": [c / 255.0 for c in RED], "tolerance": t}
        jobs.append((f"tol-{int(round(t * 100)):03d}", plain))
    for b in BLENDS:
        jobs.append((f"blend-{int(round(b * 100)):03d}", explicit_zone(base, RED, valueBlend=b)))
    for sv in SATURATIONS:
        jobs.append((f"sat-{int(round(sv * 100)):03d}", explicit_zone(base, RED, outputSaturation=sv)))

    plain_settings = F._settings_to_harness(F.default_settings())
    outs = F.run_harness_many(rgba, [([z], plain_settings if n.startswith("tol-") else settings,
                                      f"manual-{n}") for n, z in jobs])
    for (name, _), out in zip(jobs, outs):
        save(out[:, :, :3], name)
        print("  ", name)
    print(f"done -> {OUT}")


if __name__ == "__main__":
    main()
