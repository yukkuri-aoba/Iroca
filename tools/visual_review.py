"""改善サイクル用視覚レビュースクリプト。

使い方:
    python tools/visual_review.py snapshot    # 変更前スナップショット保存
    python tools/visual_review.py compare     # 比較パネル生成(Python 出力)
    python tools/visual_review.py compare --engine csharp  # 実 C# Harness 出力で比較
    python tools/visual_review.py approve     # 確認完了マーカー書き込み

  --engine csharp は出荷される実 C# 出力を描画する(Python 経路と乖離する gray mode 等を
  人間レビューでも見られるようにする)。dotnet/Unity DLL が必要。

改善サイクルでの手順:
    1. 変更を加える前に `snapshot` を実行（旧アルゴリズム出力を保存）
    2. 変更を加えてテストを実行
    3. `compare` を実行 → 生成された PNG を Read ツールで1枚ずつ確認
         - 元テクスチャにない質感・ノイズが追加されていないか
         - 変換対象エリアに変換漏れがないか
         - 関係ない部分が変わっていないか（全体サムネイル必須）
         - テクスチャを切り出して作業した場合も、全体サムネイルで切り出し外を確認
    4. 問題がなければ `approve` を実行
    5. コミット（pre-commit フックが approve を確認する）
"""
from __future__ import annotations

import json
import sys
from datetime import datetime
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

# --- sys.path 設定 ---
_ROOT = Path(__file__).resolve().parent.parent          # リポジトリルート
_DEV_SAFE = _ROOT / "dev_safe"
_TESTS = _DEV_SAFE / "Tests"
for _p in [str(_DEV_SAFE), str(_TESTS)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

from regression.fixtures import (
    SUBJECT_REGISTRY,
    load_cases,
    load_subject_inputs,
    make_zone,
    default_settings,
)
from vacc_python.algorithm import process_pixels

# --- 出力ディレクトリ ---
REVIEW_DIR = _TESTS / "visual_review"
SNAPSHOT_BEFORE_DIR = REVIEW_DIR / "snapshot_before"
COMPARE_DIR = REVIEW_DIR / "compare"
APPROVED_JSON = REVIEW_DIR / "approved.json"
CSHARP_WORK = REVIEW_DIR / "_csharp_work"

THUMB_HEIGHT = 512  # 比較パネルの列高さ（px）


# ---------------------------------------------------------------------------
# ユーティリティ
# ---------------------------------------------------------------------------
def _run_all_cases() -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """全ケースを **Python** アルゴリズムで実行。{case_id: (input_rgba, output_rgba)}"""
    results: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for subject in SUBJECT_REGISTRY.values():
        rgba, _ = load_subject_inputs(subject)
        settings = default_settings()
        for case in load_cases(subject):
            zone = make_zone(case)
            output = process_pixels(rgba, [zone], settings)
            results[case.case_id] = (rgba, output)
    return results


def _run_all_cases_csharp() -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """全ケースを **実 C# Harness** で実行。{case_id: (input_rgba, output_rgba)}

    視覚レビューが Python ではなく実際に出荷される C# 出力を見られるようにする
    (project_visual_review_washoff_blind の「視覚レビューが C# に盲目」を解消)。
    dotnet/Unity DLL が無ければ RuntimeError で中断する。
    """
    from regression import headless_io as hio

    csproj = _ROOT / "scripts" / "headless-run" / "Harness.csproj"
    dll = _ROOT / "scripts" / "headless-run" / "bin" / "Release" / "IrocaHeadless.dll"
    if hio.run(["dotnet", "--version"]).returncode != 0:
        raise RuntimeError("dotnet が利用できません(--engine csharp は使えません)")
    build = hio.run(["dotnet", "build", str(csproj), "-c", "Release", "-nologo"])
    if build.returncode != 0 or not dll.exists():
        raise RuntimeError(f"Harness ビルド失敗(Unity DLL 不在?):\n{build.stdout[-800:]}")
    CSHARP_WORK.mkdir(parents=True, exist_ok=True)

    settings = default_settings()
    settings_cfg = {
        "edgeFeather": settings.edge_feather,
        "antiAliasCleanup": settings.anti_alias_cleanup,
        "holeFillPasses": settings.hole_fill_passes,
        "holeFillMinNeighbors": settings.hole_fill_min_neighbors,
        "relaxedSatMin": settings.relaxed_sat_min,
        "relaxedSatRamp": settings.relaxed_sat_ramp,
        "useDecontamination": settings.use_decontamination,
        "decontaminationRadius": settings.decontamination_radius,
    }
    results: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for subject in SUBJECT_REGISTRY.values():
        rgba, _ = load_subject_inputs(subject)
        in_raw = CSHARP_WORK / f"{subject.subject_id}_in.raw"
        mask_raw = CSHARP_WORK / f"{subject.subject_id}_mask.raw"
        hio.write_raw(in_raw, rgba)
        hio.write_raw(mask_raw, np.zeros(rgba.shape[:2], np.uint8))  # 全画素処理
        for case in load_cases(subject):
            zone = make_zone(case)
            zones_json = CSHARP_WORK / f"{case.case_id}_zones.json"
            hio.write_zones_json(zones_json, [{
                "name": zone.name,
                "sample": list(case.sample_rgb),
                "target": list(case.target_rgb),
                "tolerance": zone.tolerance,
                "valueBlend": zone.value_blend,
                "edgeSoftness": zone.edge_softness,
                "saturationStrictness": zone.saturation_strictness,
            }], settings_cfg)
            out_raw = CSHARP_WORK / f"{case.case_id}_out.raw"
            r = hio.run(["dotnet", str(dll), str(in_raw), str(mask_raw),
                         str(out_raw), "--zones", str(zones_json)])
            if r.returncode != 0:
                raise RuntimeError(f"Harness 実行失敗 {case.case_id}: {r.stderr}\n{r.stdout}")
            results[case.case_id] = (rgba, hio.read_raw_rgba(out_raw))
    return results


def _run_engine(engine: str) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    if engine == "csharp":
        return _run_all_cases_csharp()
    return _run_all_cases()


def _make_thumb(rgba: np.ndarray, height: int = THUMB_HEIGHT) -> np.ndarray:
    """RGBA (H,W,4) を指定高さにリサイズ。アスペクト比維持。"""
    h, w = rgba.shape[:2]
    new_w = max(1, int(w * height / h))
    img = Image.fromarray(rgba).resize((new_w, height), Image.LANCZOS)
    return np.array(img)


def _add_label(arr: np.ndarray, text: str, bg_rgb: tuple[int, int, int]) -> np.ndarray:
    """上部 22px にカラーバー＋白テキストラベルを追加。"""
    img = Image.fromarray(arr)
    draw = ImageDraw.Draw(img)
    draw.rectangle([0, 0, img.width - 1, 21], fill=(*bg_rgb, 220))
    draw.text((6, 4), text, fill=(255, 255, 255, 255))
    return np.array(img)


def _hstack_with_sep(cols: list[np.ndarray], sep_px: int = 4) -> np.ndarray:
    """列を横並びに結合。高さが異なる場合は最大高さに揃える。"""
    max_h = max(c.shape[0] for c in cols)
    padded: list[np.ndarray] = []
    sep = np.full((max_h, sep_px, 4), 200, dtype=np.uint8)
    for i, col in enumerate(cols):
        if i > 0:
            padded.append(sep)
        if col.shape[0] < max_h:
            pad = np.zeros((max_h - col.shape[0], col.shape[1], 4), dtype=np.uint8)
            col = np.vstack([col, pad])
        padded.append(col)
    return np.hstack(padded)


# ---------------------------------------------------------------------------
# snapshot
# ---------------------------------------------------------------------------
def cmd_snapshot(engine: str = "python") -> None:
    """現在のアルゴリズム出力を全ケース分 snapshot_before に保存する。"""
    SNAPSHOT_BEFORE_DIR.mkdir(parents=True, exist_ok=True)
    print(f"[snapshot] 全ケースをスナップショット中 (engine={engine}) …")
    cases = _run_engine(engine)
    for case_id, (_, output_rgba) in cases.items():
        Image.fromarray(output_rgba).save(SNAPSHOT_BEFORE_DIR / f"{case_id}.png")
        print(f"  保存: {case_id}.png")
    meta = {
        "timestamp": datetime.now().isoformat(),
        "cases": sorted(cases.keys()),
    }
    (SNAPSHOT_BEFORE_DIR / "meta.json").write_text(
        json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print(f"[snapshot] {len(cases)} ケース完了 → {SNAPSHOT_BEFORE_DIR}")


# ---------------------------------------------------------------------------
# compare
# ---------------------------------------------------------------------------
def cmd_compare(engine: str = "python") -> None:
    """3列比較パネル（元テクスチャ | 変更前 | 変更後）を全ケース分生成する。

    engine="csharp" のとき「変更後」を実 C# Harness の出力で描画する(出荷物を忠実に確認)。
    """
    COMPARE_DIR.mkdir(parents=True, exist_ok=True)

    # snapshot_before を読み込む
    before_images: dict[str, np.ndarray] = {}
    meta_path = SNAPSHOT_BEFORE_DIR / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        for cid in meta["cases"]:
            p = SNAPSHOT_BEFORE_DIR / f"{cid}.png"
            if p.exists():
                before_images[cid] = np.array(Image.open(p).convert("RGBA"))
        print(f"[compare] 変更前スナップショット: {len(before_images)} ケース読み込み完了")
    else:
        print("[compare] 警告: snapshot_before が見つかりません（変更前との比較なし）。")

    # 現在のアルゴリズムで全ケース実行
    print(f"[compare] 現在のアルゴリズムで全ケースを実行中 (engine={engine}) …")
    current = _run_engine(engine)
    print(f"[compare] {len(current)} ケース完了")

    generated: list[Path] = []
    for case_id, (input_rgba, after_rgba) in sorted(current.items()):
        before_rgba: np.ndarray | None = before_images.get(case_id)

        orig_thumb = _add_label(
            _make_thumb(input_rgba),
            "元テクスチャ (Original)",
            (30, 80, 160),
        )
        after_thumb = _add_label(
            _make_thumb(after_rgba),
            "変更後 (After / New)",
            (30, 140, 60),
        )

        if before_rgba is not None:
            before_thumb = _add_label(
                _make_thumb(before_rgba),
                "変更前 (Before / Old)",
                (160, 80, 30),
            )
            panel = _hstack_with_sep([orig_thumb, before_thumb, after_thumb])
        else:
            placeholder = np.full_like(orig_thumb, 64)
            placeholder = _add_label(placeholder, "変更前 (snapshot 未実行)", (80, 80, 80))
            panel = _hstack_with_sep([orig_thumb, placeholder, after_thumb])

        out_path = COMPARE_DIR / f"{case_id}_comparison.png"
        Image.fromarray(panel).save(out_path)
        generated.append(out_path)
        print(f"  → {out_path.name}")

    # 古いファイルを掃除（今回生成していないもの）
    for old in COMPARE_DIR.glob("*_comparison.png"):
        if old not in generated:
            old.unlink()

    print(f"\n[compare] {len(generated)} 枚の比較パネルを生成しました。")
    print(f"保存先: {COMPARE_DIR}\n")
    print("=" * 60)
    print("【エージェントへ】以下の PNG を Read ツールで 1 枚ずつ確認してください。")
    print("=" * 60)
    for p in generated:
        print(f"  {p}")
    print()
    print("各画像で以下を確認:")
    print("  ✔ 元テクスチャにない質感・ノイズが追加されていないか")
    print("  ✔ 変換対象エリアに変換漏れがないか")
    print("  ✔ 関係ない部分（全体サムネイルで確認）が変わっていないか")
    print("  ✔ 全体サムネイルで切り出し範囲外のエリアも確認したか")
    print()
    print("問題がなければ:")
    print("  python tools/visual_review.py approve")


# ---------------------------------------------------------------------------
# approve
# ---------------------------------------------------------------------------
def cmd_approve() -> None:
    """視覚レビュー確認完了マーカーを書き込む。"""
    REVIEW_DIR.mkdir(parents=True, exist_ok=True)
    marker = {
        "approved_at": datetime.now().isoformat(),
        "note": "全比較パネルを Read ツールで確認済み",
    }
    APPROVED_JSON.write_text(
        json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    print(f"[approve] マーカーを書き込みました: {APPROVED_JSON}")
    print("  コミット可能です。")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
def _parse_engine(argv: list[str]) -> str:
    """argv から --engine python|csharp を取り出す(既定 python)。"""
    for i, a in enumerate(argv):
        if a == "--engine" and i + 1 < len(argv):
            eng = argv[i + 1]
            if eng not in ("python", "csharp"):
                print(f"不明な engine: {eng!r} (python|csharp)")
                sys.exit(1)
            return eng
        if a.startswith("--engine="):
            eng = a.split("=", 1)[1]
            if eng not in ("python", "csharp"):
                print(f"不明な engine: {eng!r} (python|csharp)")
                sys.exit(1)
            return eng
    return "python"


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    cmd = sys.argv[1]
    engine = _parse_engine(sys.argv[2:])
    if cmd == "snapshot":
        cmd_snapshot(engine)
    elif cmd == "compare":
        cmd_compare(engine)
    elif cmd == "approve":
        cmd_approve()
    else:
        print(f"不明なコマンド: {cmd!r}")
        print("使い方: python tools/visual_review.py [snapshot|compare|approve] [--engine python|csharp]")
        sys.exit(1)


if __name__ == "__main__":
    main()
