"""ワンショット / AI提案を含める適用後の独立した出荷レビュー。

測る・承認するのは tools/visual_review.py の snapshot / compare / approve(固定設定と一緒に回す。ここだけを回す口は
2026-11-10 に外した。2 つの承認は同じ比較の実行(run_id)と同じ中身(state)に結び付く)。ここは
python tools/workflow_review.py check [--index]
で、承認が有効か(と、今の作業ツリー・index をコミットしたらフックを通るか)を確かめる。
compare は全ケース(SUBJECTS × 2 条件)必須で、欠落や回帰は承認不可。
満足基準の未達は回帰と分けて記録する(既知の限界を成功と呼ばない)。
"""
from __future__ import annotations

import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import sys

import review_state as rs
from workflow_contract import (SUBJECTS, WORKFLOWS, SELECTION_METRICS, load_baseline,
                               selection_violations, sha256, sha256_many)

ROOT = Path(__file__).resolve().parents[1]
REVIEW = ROOT / "dev_safe/Tests/workflow_review"
FIXED_APPROVED = ROOT / "dev_safe/Tests/visual_review/approved.json"
MODES = ("oneshot", "assisted_include")
# 2: state を git の blob の id にし、測り元(state_source)・比較の実行(run_id)・変更前の来歴(snapshot)を持つ(2026-11-10)
SCHEMA = 2


def expected_cases() -> set[str]:
    return {f"{mode}/{sid}/p50" for mode in MODES for sid in SUBJECTS}


def state(code: str = "worktree") -> dict:
    """時刻ではなく、測る C#・測定器・基準の中身(git の blob の id)に結果を結びつける(tools/review_state.py)。

    code はコードの部分をどこから読むか(worktree / index / rev:<rev>)。測定器と dev_safe はいつも作業ツリー。
    """
    return rs.state(code)


def validate_review(check_current: bool = True) -> dict:
    """review.json が完全で、回帰が無く、パネルと入力資産が比較のときのままであることを確かめて返す。

    check_current=True なら、比較で測った中身(state)が今の同じ測り元の中身と同じことも求める(approve・check)。
    pre-commit は False で呼び、ステージしたファイルごとに確かめる(review_state.check_staged)。
    """
    doc = json.loads((REVIEW / "review.json").read_text(encoding="utf-8"))
    if doc.get("schema") != SCHEMA:
        raise ValueError("ワークフローの比較結果が古い形式です: visual_review.py compare をやり直す")
    if doc.get("dll_source") != "built":
        raise ValueError("比較結果が、ビルドした来歴の分かる DLL で測ったものではありません"
                         f"(dll_source={doc.get('dll_source')!r}。IROCA_HARNESS_DLL を外して compare をやり直す)")
    if check_current:
        changed = rs.diff(state(doc["state_source"]), doc["state"])
        if changed:
            raise ValueError(f"比較の後に、測るものを決めるファイルが変わりました({doc['state_source']}): "
                             f"{', '.join(changed[:6])}{' …' if len(changed) > 6 else ''}。visual_review.py compare をやり直す")
    if set(doc.get("cases", {})) != expected_cases():
        raise ValueError(f"ワンショット/追加操作後の{len(expected_cases())}ケースが揃っていません")
    for cid, row in doc["cases"].items():
        if row["regressions"]:
            raise ValueError(f"{cid}: 回帰 {row['regressions']}")
        if row["workflow"]["id"] != cid.split("/")[0]:
            raise ValueError(f"{cid}: 評価条件が不一致")
    # パネルと入力資産は毎回すべて読み直す(承認は中身に結び付く)。読むのはスレッドで並べる(試-4)。
    panels = {cid: REVIEW / "compare" / row["panel"] for cid, row in doc["cases"].items()}
    digests = sha256_many(list(panels.values()) + [Path(p) for p in doc["assets"]])
    for cid, row in doc["cases"].items():
        if digests[str(panels[cid])] != row["panel_sha256"]:
            raise ValueError(f"{cid}: パネルが欠落/変更されています")
    for path, digest in doc["assets"].items():
        if digests[str(Path(path))] != digest:
            raise ValueError(f"入力資産が変更されています: {path}")
    return doc


def validate_approval(check_current: bool = True) -> dict:
    """ワークフローと固定設定の両方の承認が、同じ比較の実行(run_id)と同じ中身(state)を指していることを確かめる。"""
    doc = validate_review(check_current)
    approved = json.loads((REVIEW / "approved.json").read_text(encoding="utf-8"))
    if approved.get("review_sha256") != sha256(REVIEW / "review.json"):
        raise ValueError("ワークフロー比較後の目視承認がありません")
    fixed = json.loads(FIXED_APPROVED.read_text(encoding="utf-8"))
    if fixed.get("run_id") != doc.get("run_id") or fixed.get("state") != doc.get("state"):
        raise ValueError("固定設定の承認が、ワークフローと同じ比較(visual_review.py compare の 1 回)を指していません")
    return doc


def approve(note: str) -> None:
    if not note or not note.strip():
        raise ValueError("--note に実際に目視した範囲と既知の未達を記録してください")
    doc = validate_review()
    marker = {"approved_at": datetime.now().isoformat(), "note": note, "run_id": doc["run_id"],
              "review_sha256": sha256(REVIEW / "review.json")}
    (REVIEW / "approved.json").write_text(json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"[workflow] {len(expected_cases())}ケースの目視承認を記録しました")


def check(index: bool = False) -> list[str]:
    """承認が有効で、今の中身(index=True ならステージした中身)をコミットしてもフックを通るかを確かめる。

    本体の範囲のファイルは「HEAD と同じ」か「承認で測った中身と同じ」なら通す(フックと同じ規則)。
    dev_safe の測定器と基準は、承認で測った中身と今の作業ツリーが同じことを求める。問題の一覧を返す(空なら通る)。
    """
    doc = validate_approval(check_current=False)
    approved = doc["state"]
    if index:
        paths = [p for p in rs.staged() if rs.in_main_scope(p)]
        problems = rs.check_staged(paths, approved)
    else:
        now = rs.main_part(state("worktree"))
        head = rs.main_part(rs.committed_state())
        problems = [f"{p}: 作業ツリーの中身が、承認した比較で測った中身とも HEAD とも違う"
                    for p in rs.diff(now, head) if now.get(p) != approved.get(p)]
    problems += [f"{p}: dev_safe の測定器・基準が、承認した比較のときと違う"
                 for p in rs.diff(rs.dev_safe_state(), rs.dev_safe_part(approved))]
    return problems


def _git_head() -> str:
    r = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True)
    return r.stdout.strip()


def _backend():
    for p in (ROOT / "dev_safe", ROOT / "dev_safe/Tests", ROOT / "dev_safe/scripts"):
        sys.path.insert(0, str(p))
    import measure_evidence_autotune as mea
    import measure_click_position as mcp
    from regression import fixtures as fx
    from regression import gt_tolerance as gtt
    return mea, mcp, fx, gtt


def _measure_subjects(sids, snapshot: bool) -> dict:
    """被写体の並び sids について、ワンショットと追加操作後を測ってパネルを書き、ケースの行を返す(snapshot なら出力を
    保存するだけで空)。generate が直列で、または子のプロセス(_shard)が自分の分だけ呼ぶ。DLL は呼ぶ側が
    fixtures に固定してある。"""
    import numpy as np
    from PIL import Image
    import visual_review as vr
    mea, mcp, fx, gtt = _backend()
    baselines = {"oneshot": load_baseline(mea.BASELINE_PATH),
                 "assisted_include": load_baseline(mea.ASSISTED_BASELINE_PATH)}
    dll, registry = fx.ensure_harness(), mcp._subject_registry()
    folder = REVIEW / ("snapshot" if snapshot else "compare")
    folder.mkdir(parents=True, exist_ok=True)
    overrides = gtt.load_overrides()
    cases = {}
    for sid in sids:
        subject = registry[sid]
        r = mea.run_case(dll, subject, "p50", include=True, conventional=False, return_outputs=True)
        rgba, gt = fx.load_subject_inputs(subject)
        region = fx.load_eval_region(subject)
        inside = gt & (rgba[..., 3] >= 128)
        if region is not None:
            inside &= region
        tolerated, tol_info = gtt.tolerance_for_subject(subject, rgba, inside, region, overrides)
        orig_thumb = None   # 元の縮小は 2 つのモードで同じ(性能の候補 T5。1 枚約 0.13 秒)
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
            if vr.alpha_violations(rgba, output):     # 再着色は色だけを変える(点検 B-7⑤)
                regressions.append("alpha")
            previous = REVIEW / "snapshot" / name
            before = np.array(Image.open(previous).convert("RGBA")) if previous.exists() else None
            def tile(arr, label, thumb=None):
                # thumb: 使い回す縮小(ラベルは写しに描く)。無ければ arr を縮小する。
                return vr._add_label((thumb if thumb is not None else vr._make_thumb(arr)).copy(), label, (40, 65, 95))
            # 変更前との違い(変化の密な窓・線の物差し・changed_from_snapshot が使う)は 1 回だけ求める。
            # ビット一致なら変更前の縮小は出力の縮小と同じなので作り直さない(パネルの画素は同じ。性能の候補 T5)。
            diff_px = (before != output).any(axis=-1) if before is not None else None
            out_thumb = vr._make_thumb(output)
            if orig_thumb is None:
                orig_thumb = vr._make_thumb(rgba)
            if before is not None:
                before_tile = tile(before, "変更前", out_thumb if not diff_px.any() else None)
            else:
                before_tile = tile(np.full_like(output, 64), "変更前なし(新規条件)")
            label = WORKFLOWS[mode]["label"]
            top = vr._hstack_with_sep([tile(rgba, f"元 / {sid} p50 {r['xy']}", orig_thumb),
                                       before_tile, tile(output, label, out_thumb)])
            # AIマスクの役割とGT採点を別々の画像にする。GTを処理入力へ渡さない。
            evidence = rgba.copy()
            evidence[r["_seg"], :3] = (0, 200, 255)
            changed = fx.recolored_mask(rgba, output)
            if region is not None:
                changed &= region
            overlay = rgba.copy()
            overlay[changed & inside, :3] = (0, 200, 80)
            overlay[changed & ~inside & ~tolerated, :3] = (255, 40, 40)
            overlay[changed & ~inside & tolerated, :3] = (150, 150, 150)   # 許容(同色の別部位): 設計上の巻き込み
            overlay[~changed & inside, :3] = (40, 100, 255)
            focus = diff_px if before is not None else (changed | inside)
            cy, cx = vr._densest_change_window(focus, vr.CROP_SIZE)
            crop = vr._add_label(vr._crop(output, cy, cx, vr.CROP_SIZE),
                                 f"等倍出力 @({cx},{cy})", (40, 65, 95))
            # 元に無い線が増えた所(変更前があり、出力が変わったときだけ数える)
            phantom = ph_row = None
            if before is not None and focus.any():
                ph = vr.phantom_change(rgba, before, output)
                phantom = {k: v for k, v in ph.items() if not k.startswith("_")}
                ph_row = vr.phantom_crop_row(rgba, before, output, ph)
                del ph
            bottom = vr._hstack_with_sep([tile(evidence, f"AI証拠 / include={mode == 'assisted_include' and r['include_applied']}"),
                                         tile(overlay, f"GT採点: 緑=一致 赤=過検出 灰=許容(同色) 青=見逃し  "
                                                       f"適合 {metrics['precision']:.3f}→設計 {metrics['precision_design']:.3f}"), crop])
            panel = vr._vstack_with_sep([top, bottom] + ([ph_row] if ph_row is not None else []))
            Image.fromarray(panel).save(folder / name)
            cases[cid] = {"workflow": r[workflow_key], "metrics": {k: metrics[k] for k in SELECTION_METRICS},
                          "satisfied": mea.satisfied(metrics), "satisfied_strict": mea.satisfied_strict(metrics),
                          "tolerance_gt": r["tolerance_gt"], "regressions": regressions,
                          "panel": name, "panel_sha256": sha256(folder / name),
                          "changed_from_snapshot": int(diff_px.sum()) if before is not None else None,
                          "phantom": phantom}
            print(f"[workflow] {cid}: regression={regressions} satisfied={mea.satisfied(metrics)} "
                  f"(strict {mea.satisfied_strict(metrics)}; 適合 {metrics['precision']:.3f}→設計 "
                  f"{metrics['precision_design']:.3f}, 許容 {metrics['tolerated_frac']*100:.0f}%)", flush=True)
        fx._mem_cache.clear()
        del r, rgba, output
    return cases


# 被写体の行の並び(直列で測ったときと同じ: 被写体の順に、ワンショット → 追加操作後)。
_MODES = ("oneshot", "assisted_include")


def _jobs() -> int:
    """子のプロセスの数。IROCA_WORKFLOW_JOBS で決められる(1 で直列)。無ければ汎化レポートと同じ決め方
    (空きメモリと CPU から、上限 3。1 本あたり AI 提案の鎖と 4K の入出力で約 3GB)。"""
    v = os.environ.get("IROCA_WORKFLOW_JOBS")
    if v:
        return max(1, int(v))
    try:
        import generalization_report as gr
        return gr.auto_jobs()
    except Exception:
        return 1


def _measure_in_children(jobs: int, snapshot: bool) -> dict:
    """被写体を jobs 個の子のプロセスに配って測る(試-3。以前は 33 被写体を 1 つずつ直列に)。配り方は汎化レポートと
    同じ(画素数の大きい順に軽い分割へ)。子は親と同じ来歴の DLL を固定し(BuildInfo を渡す。キャッシュの鍵も同じ)、
    ハーネスのスレッド数は「CPU ÷ 本数」。結果は親が被写体の順に並べ直すので、直列と同じ行・同じ並びになる。"""
    mea, mcp, fx, gtt = _backend()
    import generalization_report as gr
    work = REVIEW / "_shards"
    work.mkdir(parents=True, exist_ok=True)
    info = fx.harness_info()
    info_path = work / "harness_info.json"
    if info is not None:
        info.save(info_path)
    threads = str(max(2, (os.cpu_count() or 4) // jobs))
    parts = [work / f"shard{i}.json" for i in range(jobs)]
    procs = []
    for i in range(jobs):
        sids = gr.shard_subjects(list(SUBJECTS), i, jobs)
        cmd = [sys.executable, str(Path(__file__).resolve()), "_shard", "--out", str(parts[i]), "--subjects", *sids]
        if snapshot:
            cmd.append("--snapshot")
        if info is not None:
            cmd += ["--info", str(info_path)]
        env = dict(os.environ, PYTEST_XDIST_WORKER=f"wf{i}", IROCA_HARNESS_THREADS=threads, PYTHONIOENCODING="utf-8")
        procs.append(subprocess.Popen(cmd, env=env, cwd=str(ROOT)))
    codes = [p.wait() for p in procs]
    if any(codes):
        raise RuntimeError(f"ワークフローの審査の子のプロセスが失敗しました: {codes}")
    cases = {}
    for part in parts:
        cases.update(json.loads(part.read_text(encoding="utf-8")))
        part.unlink()
    info_path.unlink(missing_ok=True)
    return cases


def _shard_main(argv) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd")
    ap.add_argument("--out", required=True)
    ap.add_argument("--subjects", nargs="*", default=[])
    ap.add_argument("--snapshot", action="store_true")
    ap.add_argument("--info", default=None)
    a = ap.parse_args(argv)
    if os.name == "nt":
        import ctypes   # 親(visual_review)と同じく低い優先度で
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)
    mea, mcp, fx, gtt = _backend()
    if a.info:
        import harness_build as hb
        fx.pin_harness(hb.BuildInfo.load(Path(a.info)))
    cases = _measure_subjects(a.subjects, a.snapshot)
    Path(a.out).write_text(json.dumps(cases, ensure_ascii=False), encoding="utf-8")
    return 0


def generate(snapshot: bool = False, measured: dict | None = None) -> None:
    """ワンショットと追加操作後のパネルを作る(visual_review.py の snapshot / compare から呼ぶ)。

    measured は測るものの来歴 {"source": worktree/index/rev:<rev>, "state": 測り始めの中身, "run_id": 比較の実行,
    "snapshot": 変更前の来歴(compare のとき)}。DLL は呼ぶ側が fixtures に固定してある(fixtures.pin_harness)。
    """
    if measured is None:
        raise ValueError("workflow_review.generate は visual_review.py の snapshot / compare から呼ぶ")
    mea, mcp, fx, gtt = _backend()
    from freeze_sam_fixtures import DECODER, ENCODER, FIXTURE_DIR, texture_key

    # 失敗した再生成のあと、以前の比較/承認が残って通ることを防ぐ。
    REVIEW.mkdir(parents=True, exist_ok=True)
    if not snapshot:
        (REVIEW / "approved.json").unlink(missing_ok=True)
        (REVIEW / "review.json").unlink(missing_ok=True)
    initial_state = measured["state"]
    dll, registry = fx.ensure_harness(), mcp._subject_registry()
    folder = REVIEW / ("snapshot" if snapshot else "compare")
    folder.mkdir(parents=True, exist_ok=True)
    assets = {}
    overrides = gtt.load_overrides()
    # 結果に結びつける入力資産(上書きマスク・各被写体のテクスチャ・GT・評価領域・ONNX・埋め込み)は、最初に
    # まとめてスレッドで読む(試-4。以前は被写体ごとに 1 つずつ直列に読んでいた)。並びは以前と同じ。
    asset_paths = [o["mask_path"] for o in overrides]
    for sid in SUBJECTS:
        subject = registry[sid]
        asset_paths += [path for path in (subject.texture_path, subject.fixtures_json, subject.gt_mask_path,
                                          subject.eval_region_path, DECODER, ENCODER,
                                          FIXTURE_DIR / f"{texture_key(subject)}_embedding.bin")
                        if path is not None]
    assets.update(sha256_many(asset_paths))
    jobs = _jobs() if len(SUBJECTS) > 1 else 1
    found = (_measure_subjects(list(SUBJECTS), snapshot) if jobs <= 1
             else _measure_in_children(jobs, snapshot))
    cases = {f"{mode}/{sid}/p50": found[f"{mode}/{sid}/p50"]
             for sid in SUBJECTS for mode in _MODES if f"{mode}/{sid}/p50" in found}
    if not snapshot:
        # 測り始めてから(固定設定の分を含む)測るものが変わっていないか(点検 B-7①。時刻ではなく中身で見る)
        changed = rs.diff(state(measured["source"]), initial_state)
        now = sha256_many(Path(p) for p in assets)
        changed += [p for p, h in assets.items() if now[str(Path(p))] != h]
        if changed:
            raise ValueError(f"計測中にコード/入力が変更されました: {', '.join(changed[:6])}。compare を再実行してください")
        info = fx.harness_info()
        doc = {"schema": SCHEMA, "generated_at": datetime.now().isoformat(), "run_id": measured["run_id"],
               "state_source": measured["source"], "state": initial_state, "head": _git_head(),
               "dll_sha256": sha256(Path(dll)),
               # 来歴の分かる DLL(fixtures がビルドして写したもの)か、IROCA_HARNESS_DLL の外の DLL か(点検 B-5)
               "dll_source": "built" if info is not None else "external",
               "dll_built_from": rs.digest(info.sources) if info is not None else None,
               "snapshot": measured.get("snapshot"),
               "assets": assets, "cases": cases}
        (REVIEW / "review.json").write_text(json.dumps(doc, indent=2, ensure_ascii=False), encoding="utf-8")
        validate_review()
        # 変更前とビット一致のパネルは目視しなくてよい。変更前が無い(None)パネルは目視に回す。
        # 元に無い線が増えた順(次に変化の大きい順)。増えたパネルは 3 段目(赤紫の印)を見る。
        changed_rows = [c for c in cases.values() if c["changed_from_snapshot"] != 0]
        changed_rows.sort(key=lambda c: (-((c["phantom"] or {}).get("after", 0) - (c["phantom"] or {}).get("before", 0)),
                                         -(c["changed_from_snapshot"] or 0)))
        to_review = [c["panel"] for c in changed_rows]
        print(f"[workflow] 変化があったパネル {len(to_review)}/{len(cases)} 枚を目視(元に無い線が増えた順): {folder}")
        for c in changed_rows:
            if c["phantom"] and c["phantom"]["after"] > c["phantom"]["before"]:
                print(f"  線の組 {c['phantom']['before']:,} → {c['phantom']['after']:,}: {c['panel']}")
        for name in to_review:
            print(f"  {folder / name}")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "_shard":
        sys.exit(_shard_main(sys.argv[1:]))
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=("check",),
                        help="承認が有効か、今の中身をコミットしてもフックを通るかを確かめる")
    parser.add_argument("--index", action="store_true", help="作業ツリーでなく、ステージした中身で確かめる")
    args = parser.parse_args()
    problems = check(index=args.index)
    if problems:
        print("[workflow] 承認と中身が合いません(visual_review.py compare → 目視 → approve をやり直す):")
        for p in problems:
            print(f"  {p}")
        sys.exit(1)


if __name__ == "__main__":
    main()
