"""ワンショット / AI提案を含める適用後の独立した出荷レビュー。

python tools/workflow_review.py snapshot|compare|approve [--note ...]
通常の visual_review からも呼ぶ。compare は全28ケース必須で、欠落や回帰は承認不可。
満足基準の未達は回帰と分けて記録する(既知の限界を成功と呼ばない)。
"""
from __future__ import annotations

import argparse
from datetime import datetime
import json
from pathlib import Path
import sys

from harness_scope import harness_source_files
from workflow_contract import SUBJECTS, WORKFLOWS, load_baseline, selection_violations, sha256

ROOT = Path(__file__).resolve().parents[1]
REVIEW = ROOT / "dev_safe/Tests/workflow_review"
BASE = ROOT / "dev_safe/Tests/Baselines/refactor-pre"
MODES = ("oneshot", "assisted_include")


def expected_cases() -> set[str]:
    return {f"{mode}/{sid}/p50" for mode in MODES for sid in SUBJECTS}


def state() -> dict:
    """時刻ではなく、測るC#・測定器・基準の内容に結果を結びつける。"""
    paths = set(harness_source_files())
    paths.update(ROOT / p for p in (
        "scripts/headless-run/Harness.cs", "scripts/headless-run/Harness.csproj",
        "tools/workflow_review.py", "tools/workflow_contract.py", "tools/visual_review.py",
        "dev_safe/scripts/measure_evidence_autotune.py", "dev_safe/scripts/sam_proposal.py",
        "dev_safe/scripts/freeze_sam_fixtures.py", "dev_safe/measure_residual.py",
        "dev_safe/scripts/measure_sam_e2e.py", "dev_safe/scripts/measure_operating_curve.py",
        "dev_safe/measure_click_position.py", "dev_safe/Tests/regression/fixtures.py",
        "dev_safe/Tests/regression/headless_io.py",
    ))
    paths.update(BASE / n for n in ("evidence_autotune_baseline.json", "assisted_include_baseline.json"))
    return {p.relative_to(ROOT).as_posix(): sha256(p) for p in sorted(paths)}


def validate_review() -> dict:
    doc = json.loads((REVIEW / "review.json").read_text(encoding="utf-8"))
    if doc.get("schema") != 1 or doc.get("state") != state():
        raise ValueError("ワークフローの比較結果が古い: workflow_review.py compare を実行")
    if set(doc.get("cases", {})) != expected_cases():
        raise ValueError("ワンショット/追加操作後の28ケースが揃っていません")
    for cid, row in doc["cases"].items():
        if row["regressions"]:
            raise ValueError(f"{cid}: 回帰 {row['regressions']}")
        if row["workflow"]["id"] != cid.split("/")[0]:
            raise ValueError(f"{cid}: 評価条件が不一致")
        panel = REVIEW / "compare" / row["panel"]
        if sha256(panel) != row["panel_sha256"]:
            raise ValueError(f"{cid}: パネルが欠落/変更されています")
    for path, digest in doc["assets"].items():
        if sha256(Path(path)) != digest:
            raise ValueError(f"入力資産が変更されています: {path}")
    return doc


def validate_approval() -> dict:
    doc = validate_review()
    approved = json.loads((REVIEW / "approved.json").read_text(encoding="utf-8"))
    if approved.get("review_sha256") != sha256(REVIEW / "review.json"):
        raise ValueError("ワークフロー比較後の目視承認がありません")
    return doc


def approve(note: str) -> None:
    if not note or not note.strip():
        raise ValueError("--note に実際に目視した範囲と既知の未達を記録してください")
    validate_review()
    marker = {"approved_at": datetime.now().isoformat(), "note": note,
              "review_sha256": sha256(REVIEW / "review.json")}
    (REVIEW / "approved.json").write_text(json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8")
    print("[workflow] 28ケースの目視承認を記録しました")


def _backend():
    for p in (ROOT / "dev_safe", ROOT / "dev_safe/Tests", ROOT / "dev_safe/scripts"):
        sys.path.insert(0, str(p))
    import measure_evidence_autotune as mea
    import measure_click_position as mcp
    from regression import fixtures as fx
    return mea, mcp, fx


def generate(snapshot: bool = False) -> None:
    import numpy as np
    from PIL import Image
    import visual_review as vr
    mea, mcp, fx = _backend()
    from freeze_sam_fixtures import DECODER, ENCODER, FIXTURE_DIR, texture_key

    # 失敗した再生成のあと、以前の比較/承認が残って通ることを防ぐ。
    REVIEW.mkdir(parents=True, exist_ok=True)
    if not snapshot:
        (REVIEW / "approved.json").unlink(missing_ok=True)
        (REVIEW / "review.json").unlink(missing_ok=True)
    baselines = {"oneshot": load_baseline(mea.BASELINE_PATH),
                 "assisted_include": load_baseline(mea.ASSISTED_BASELINE_PATH)}
    initial_state = state()
    dll, registry = fx.ensure_harness(), mcp._subject_registry()
    folder = REVIEW / ("snapshot" if snapshot else "compare")
    folder.mkdir(parents=True, exist_ok=True)
    cases, assets = {}, {}
    for sid in SUBJECTS:
        subject = registry[sid]
        for path in (subject.texture_path, subject.fixtures_json, subject.gt_mask_path,
                     subject.eval_region_path, DECODER, ENCODER,
                     FIXTURE_DIR / f"{texture_key(subject)}_embedding.bin"):
            if path is not None and str(path) not in assets:
                assets[str(path)] = sha256(path)
        r = mea.run_case(dll, subject, "p50", include=True, conventional=False, return_outputs=True)
        rgba, gt = fx.load_subject_inputs(subject)
        region = fx.load_eval_region(subject)
        inside = gt & (rgba[..., 3] >= 128)
        if region is not None:
            inside &= region
        for mode, output, metric_key, workflow_key in (
            ("oneshot", r["_out_b"], "evidence", "workflow"),
            ("assisted_include", r["_out_b2"], "evidence_include", "assisted_workflow"),
        ):
            cid = f"{mode}/{sid}/p50"
            name = f"{mode}__{sid}__p50.png"
            if snapshot:
                Image.fromarray(output).save(folder / name)
                continue
            metrics = r[metric_key]
            regressions = selection_violations(metrics, baselines[mode][f"{sid}/p50"])
            if mode == "assisted_include" and r["include_applied"] != baselines[mode][f"{sid}/p50"]["include_applied"]:
                regressions.append("include_applied")
            previous = REVIEW / "snapshot" / name
            before = np.array(Image.open(previous).convert("RGBA")) if previous.exists() else None
            def tile(arr, label):
                return vr._add_label(vr._make_thumb(arr), label, (40, 65, 95))
            before_tile = tile(before, "変更前") if before is not None else tile(np.full_like(output, 64), "変更前なし(新規条件)")
            label = WORKFLOWS[mode]["label"]
            top = vr._hstack_with_sep([tile(rgba, f"元 / {sid} p50 {r['xy']}"),
                                       before_tile, tile(output, label)])
            # AIマスクの役割とGT採点を別々の画像にする。GTを処理入力へ渡さない。
            evidence = rgba.copy()
            evidence[r["_seg"], :3] = (0, 200, 255)
            changed = fx.recolored_mask(rgba, output)
            if region is not None:
                changed &= region
            overlay = rgba.copy()
            overlay[changed & inside, :3] = (0, 200, 80)
            overlay[changed & ~inside, :3] = (255, 40, 40)
            overlay[~changed & inside, :3] = (40, 100, 255)
            focus = (before != output).any(axis=-1) if before is not None else (changed | inside)
            cy, cx = vr._densest_change_window(focus, vr.CROP_SIZE)
            crop = vr._add_label(vr._crop(output, cy, cx, vr.CROP_SIZE),
                                 f"等倍出力 @({cx},{cy})", (40, 65, 95))
            bottom = vr._hstack_with_sep([tile(evidence, f"AI証拠 / include={mode == 'assisted_include' and r['include_applied']}"),
                                         tile(overlay, "GT採点: 緑=一致 赤=過検出 青=見逃し"), crop])
            panel = vr._vstack_with_sep([top, bottom])
            Image.fromarray(panel).save(folder / name)
            cases[cid] = {"workflow": r[workflow_key], "metrics": {k: metrics[k] for k in ("iou", "precision", "recall", "island")},
                          "satisfied": mea.satisfied(metrics), "regressions": regressions,
                          "panel": name, "panel_sha256": sha256(folder / name),
                          "changed_from_snapshot": int((before != output).any(axis=-1).sum()) if before is not None else None}
            print(f"[workflow] {cid}: regression={regressions} satisfied={mea.satisfied(metrics)}", flush=True)
        fx._mem_cache.clear()
        del r, rgba, output
    if not snapshot:
        if state() != initial_state or any(sha256(Path(p)) != h for p, h in assets.items()):
            raise ValueError("計測中にコード/入力が変更されました。compare を再実行してください")
        doc = {"schema": 1, "generated_at": datetime.now().isoformat(),
               "dll_sha256": sha256(Path(dll)), "state": initial_state,
               "assets": assets, "cases": cases}
        (REVIEW / "review.json").write_text(json.dumps(doc, indent=2, ensure_ascii=False), encoding="utf-8")
        validate_review()
        print(f"[workflow] パネル28枚を目視: {folder}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("snapshot", "compare", "approve", "check"))
    parser.add_argument("--note", default="")
    args = parser.parse_args()
    if args.command in ("snapshot", "compare"):
        generate(snapshot=args.command == "snapshot")
    elif args.command == "approve":
        approve(args.note)
    else:
        validate_approval()


if __name__ == "__main__":
    main()
