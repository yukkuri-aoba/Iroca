"""改善サイクル用視覚レビュースクリプト(出荷ゲート)。

使い方:
    python tools/visual_review.py snapshot                 # 変更前の出力を保存(既定で HEAD のコードから)
    python tools/visual_review.py snapshot --worktree      # 作業ツリーのコードから(変更前の作業ツリーを撮るとき)
    python tools/visual_review.py compare                  # 作業ツリーのコードで、窓で行き着く状態のパネルを比べる
    python tools/visual_review.py compare --index          # ステージした中身のコードで比べる(他人の未コミットを入れない)
    python tools/visual_review.py approve --note "<目視した範囲と根拠>"  # 承認(測った中身に結び付く)
    python tools/visual_review.py check [--index]          # 承認が有効か、今の中身をコミットしてもフックを通るか

  エンジンは csharp のみ(--engine csharp は書いても書かなくてもよい)。AI 提案に ONNX が要る。

パネルは窓で行き着く状態だけを塗る(2026-11-21、C6 の 5 章。それまでの「固定設定」= 許容 0.20・明度の保持 0.85・
境界のぼかし 0.30・台帳のサンプル色は、利用者が窓で行き着かない状態なので外した)。入口はどれも dev_safe/scripts/oneshot.py:
  - ここ(visual_review/compare): 状態 (b) クリック → AI の証拠つき自動調整 → 漏れの確かめ → 塗る。被写体ごとに p50 の
    クリック(面の内側。measure_click_position.pick_clicks)× 塗り先 5 色(fixtures.json の tests)。`autotune__<被写体>__<色>`。
    MASKED_SUBJECT_CASES の被写体は、同じ状態に除外マスク(GT の左半分)を与えた変種も(`…+masked`。マスク契約)。
    再現データ(dev_safe/Tests/repro_cases。実機の書き出し)は test_repro_cases と同じ組み立てで(`repro__<名前>__…`)。
  - workflow_review/compare: 状態 (a) クリック直後(自動調整なし)と、(d) (b) の後に同じ所を右クリック「ここも塗る」
    (p50・塗り先 1 色。含めるかは製品の右クリックが決める)。
  基準との突き合わせ(状態 (b) の塗り先の 1 色目と (d))は、1 つの基準 dev_safe/Tests/Baselines/generalization_reference.json
  を pytest の床と同じ節・同じ余裕で読む(workflow_review.review_baselines)。
  全条件の目視後に approve する。

承認は時刻ではなく「測った中身」に結び付く(2026-11-10、点検 B-1・B-4・B-7①)。compare は測るものを決めるファイル
(コード・測定器・dev_safe の基準。tools/review_state.py)の中身を測り始めと終わりに取り、食い違えばやり直させる。
pre-commit は、ステージした範囲のファイルが「HEAD と同じ」か「承認した比較で測った中身と同じ」なら通す(ファイル単位)。
snapshot は既定で HEAD のコードをビルドして撮る(「変更前」がいつも HEAD になり、共有の作業ツリーにある他人の未コミットも
入らない)。approve は、変更前と同じ中身を測った比較(変化 0 が何も示さない)を断る。

改善サイクルでの手順:
    1. 変更を加える前に(HEAD が変更前なら後でもよい) `snapshot` を実行。測定器(Python)や dev_safe の基準を変えるときは、
       変える前に撮る(snapshot は測定器を作業ツリーのまま動かすので、変えた後に撮ると変更前にならない)
    2. 変更を加えてテストを実行
    3. `compare` を実行 → 一覧に出た(変化があった)PNG を Read ツールで1枚ずつ確認
         - 元テクスチャにない質感・ノイズが追加されていないか
         - 変換対象エリアに変換漏れがないか
         - 関係ない部分が変わっていないか（全体サムネイル必須）
         - テクスチャを切り出して作業した場合も、全体サムネイルで切り出し外を確認
       `+masked` 付きのケースは除外マスクを与えて走らせたもので、compare が
       「守った画素が 1 つも変わっていないか」を機械で数える（目視では見えないため）。
    4. 問題がなければ `approve` を実行（マスク契約・α・選択の使い回しの食い違い・欠けたケース・基準からの回帰が
       あると approve は失敗する）
    5. コミット（pre-commit フックが、ステージした中身と承認を突き合わせる）
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path

# numpy より前に、回帰(dev_safe/Tests/conftest.py)と同じメモリの設定をする(2026-11-09)。どれも確保の仕方だけで、
# 出力は変わらない。立てる前の compare は OpenBLAS が読み込むだけで 1GB のコミットを持ち(numpy と scipy が
# それぞれスレッド数ぶんの作業領域を取る。1 にすると 40MB)、出力のプロセス内メモも 12 件(4K で 1 件 64MB)まで
# 溜めていた。外から値を渡したときはそちらを使う。
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("IROCA_HARNESS_MEM_ENTRIES", "2")
os.environ.setdefault("DOTNET_gcConcurrent", "0")
os.environ.setdefault("DOTNET_GCConserveMemory", "7")

import numpy as np
from PIL import Image, ImageDraw

# --- sys.path 設定 ---
_ROOT = Path(__file__).resolve().parent.parent          # リポジトリルート

sys.path.insert(0, str(Path(__file__).resolve().parent))
import review_state as rs                                # noqa: E402
from workflow_contract import MASKED_REVIEW_CASES, SUBJECTS   # noqa: E402
_DEV_SAFE = _ROOT / "dev_safe"
_TESTS = _DEV_SAFE / "Tests"
for _p in [str(_DEV_SAFE), str(_TESTS)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

from regression.fixtures import (
    SUBJECT_REGISTRY,
    load_cases,
    load_subject_inputs,
)

# --- 出力ディレクトリ ---
REVIEW_DIR = _TESTS / "visual_review"
SNAPSHOT_BEFORE_DIR = REVIEW_DIR / "snapshot_before"
COMPARE_DIR = REVIEW_DIR / "compare"
APPROVED_JSON = REVIEW_DIR / "approved.json"
CSHARP_WORK = REVIEW_DIR / "_csharp_work"
# snapshot で PNG を並べて保存するスレッドの数(4K の出力 1 枚 64MB。待ちはこの 2 倍まで)。
SNAPSHOT_SAVE_WORKERS = 4
# snapshot の PNG の圧縮レベル(既定 6)。画素は変わらない(可逆)。4K で書き出し 1.03 → 0.61 秒・読み込みは同じ・
# ファイルは約 3% 増(性能の候補 T2 の手軽な代案。レベル 1 は読み込みが 2 倍遅いので使わない)。
SNAPSHOT_PNG_LEVEL = 3

THUMB_HEIGHT = 512  # 比較パネルの列高さ（px）

# 除外マスク付きレビューケース（subject_id -> 使うケース数）。
#
# 「ユーザーが手で守った画素は絶対に変わらない」は「プレビュー＝出力」と並ぶ中核保証だが、
# 2026-08-07 まで視覚レビューの全ケースが np.zeros(= マスク無し)でハーネスを呼んでおり、
# **出荷ゲートにマスク経路が 1 件も無かった**。実際 N-1(CleanAchromaFringe が除外マスクを
# 参照せず保護画素を上書きする契約違反)はこのゲートを素通りし、修正後も全ケースがビット一致で
# 「何も検証できていない」状態だった(レビュー 2026-08-06 の N-12)。
#
# 被写体は経路の違いで選ぶ: 有彩サンプルの通常マッチ経路と、暗い無彩サンプル(グレーモード +
# 無彩フチ消し = N-1 の現場)を 1 被写体ずつ。具体名は dev_safe/Tests/subjects.json。
# 色数は実行コスト(1 ケース = dotnet 1 プロセス × フル解像度)とのバランスで先頭 2 色に絞る。
MASKED_SUBJECT_CASES = MASKED_REVIEW_CASES

# マスク付きケースの case_id 接尾辞。compare 側の契約検査もこれで判定する。
MASKED_SUFFIX = "+masked"

# マスク契約の検査結果（compare が書き、approve が読む）。
MASK_CONTRACT_JSON = REVIEW_DIR / "mask_contract.json"

# 変化画素の内訳（compare が書く。目視の当たりを付けるための数値）。
CHANGE_SUMMARY_JSON = REVIEW_DIR / "change_summary.json"


# ---------------------------------------------------------------------------
# ユーティリティ
# ---------------------------------------------------------------------------
def _run_all_cases() -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """(引退) Python 再実装は削除済み。csharp エンジンを使うこと。"""
    raise RuntimeError(
        "Python エンジン(vacc_python)は完全引退しました。"
        "実 C# 出力で確認してください: python tools/visual_review.py <cmd> --engine csharp")


def _build_review_mask(gt_mask: np.ndarray) -> np.ndarray:
    """GT 領域の左半分だけを守る除外マスク(uint8, 1=除外)を作る。

    マスクは「変換対象になるはずの領域の一部」を守る形でないと検出力が無い
    (対象外を守っても出力は何も変わらないので契約違反が現れない)。
    分割位置は **GT 自身の bbox 中央** から決めるので、被写体ごとの座標を焼き込まずに
    「守った側」と「変換される側」が必ず両方存在する。
    """
    xs = np.where(gt_mask.any(axis=0))[0]
    if xs.size < 2:
        raise RuntimeError("GT マスクが空か 1 列しかなく、マスクケースを作れません")
    mid = int((int(xs[0]) + int(xs[-1])) // 2)
    excl = gt_mask.copy()
    excl[:, mid:] = False
    if not excl.any():
        raise RuntimeError("生成した除外マスクが空です")
    return excl.astype(np.uint8)


# ---------------------------------------------------------------------------
# 窓で行き着く状態のケース(2026-11-21、C6 の 5 章)
#
# 以前の「固定設定」(fixtures.make_zone: 許容 0.20・明度の保持 0.85・境界のぼかし 0.30・台帳のサンプル色・クリックなし)
# は、利用者が窓で行き着かない状態だった(手でスライダーを合わせない限り出ない値)。ここで塗るのは状態 (b) = 窓で
# クリック → AI の証拠つき自動調整 → 漏れの確かめ → 塗る(dev_safe/scripts/oneshot.py の state="autotune"。ハーネスの
# --oneshot が製品の段取りをそのまま動かす)。クリックは REVIEW_CLICK(面の内側。measure_click_position.pick_clicks)、
# 塗り先は被写体の fixtures.json の 5 色。塗り先の 1 色目は measure_evidence_autotune.run_case の B と同じ呼び出しなので、
# 回帰の結果キャッシュがそのまま当たり、基準(workflow_review.review_baselines = generalization_reference.json の cases の
# p50 の行。test_oneshot_floor と同じ)と突き合わせる。
# ---------------------------------------------------------------------------
REVIEW_CLICK = "p50"
AUTOTUNE_PREFIX = "autotune"
REPRO_PREFIX = "repro"
# 試験用に出力をわざと変える口(ゲートの自己点検用。カンマ区切りの case_id。その状態 (b) のケースだけ塗り先を
# PERTURB_SHIFT ずらす)。立てた比較は approve できない(change_summary.json の perturbed)。
PERTURB_ENV = "IROCA_REVIEW_PERTURB"
PERTURB_SHIFT = 0.08


def autotune_case_id(sid: str, suffix: str, masked: bool = False) -> str:
    return f"{AUTOTUNE_PREFIX}__{sid}__{suffix}" + (MASKED_SUFFIX if masked else "")


def _perturbed_ids() -> set[str]:
    return {s.strip() for s in os.environ.get(PERTURB_ENV, "").split(",") if s.strip()}


def _perturb_target(target) -> tuple:
    """塗り先を PERTURB_SHIFT ずらす(上に余地が無ければ下へ)。"""
    t = [float(c) for c in target]
    t[0] = t[0] + PERTURB_SHIFT if t[0] + PERTURB_SHIFT <= 1.0 else t[0] - PERTURB_SHIFT
    return tuple(t)


def _registry() -> dict:
    """SUBJECTS(subjects.json。回帰の床・ワークフローの審査と同じ並び)の被写体。"""
    missing = [sid for sid in SUBJECTS if sid not in SUBJECT_REGISTRY]
    if missing:
        raise RuntimeError(f"SUBJECTS にあって fixtures に無い被写体: {missing}")
    return {sid: SUBJECT_REGISTRY[sid] for sid in SUBJECTS}


def _repro_specs() -> list[tuple[str, Path, str, int | None]]:
    """再現データのケース (case_id, フォルダ, "replay"|"autotune", ゾーンの番号)。組み立ては test_repro_cases と同じ。"""
    from regression import test_repro_cases as trc
    specs = []
    for d in trc._case_dirs():
        specs.append((f"{REPRO_PREFIX}__{d.name}__replay", d, "replay", None))
        for a in trc.autotune_inputs(d):
            specs.append((f"{REPRO_PREFIX}__{d.name}__autotune-z{a['index']}", d, "autotune", int(a["index"])))
    return specs


def window_case_ids() -> list[str]:
    """このパネルの一覧(作る順)。approve が欠けを見る。"""
    ids = []
    for sid, subject in _registry().items():
        cases = load_cases(subject)
        ids += [autotune_case_id(sid, c.suffix) for c in cases]
        ids += [autotune_case_id(sid, c.suffix, True) for c in cases[:MASKED_SUBJECT_CASES.get(sid, 0)]]
    ids += [cid for cid, *_ in _repro_specs()]
    return ids


def _u8(rgb01) -> list[int]:
    return [int(round(float(c) * 255)) for c in rgb01]


def _window_subject_items(sid: str, detail: bool) -> list[dict]:
    """1 被写体ぶんの状態 (b) のケースを順に並べて返す(背景のスレッドか、キャッシュを温める子のプロセスで呼ぶ)。

    detail=True(compare)なら、パネルの 2 段目に要る GT の採点の材料(GT の内側・評価領域・GT 許容)と、
    塗り先の 1 色目の採点の行(基準と突き合わせる)も付ける。
    """
    import workflow_review
    from regression import fixtures as fx
    mea, _mcp, _fx, gtt = workflow_review._backend()
    import oneshot                     # dev_safe/scripts(_backend が sys.path に足す)
    subject = _registry()[sid]
    dll = fx.ensure_harness()
    rgba, gt = load_subject_inputs(subject)
    if gt is None:
        raise RuntimeError(f"{sid}: 状態 (b) のパネルには single_mask 戦略の GT が要ります(クリックを GT の内側から選ぶ)")
    cases = load_cases(subject)
    # 採点の行(1 色目 = run_case の B)。汎化レポート・test_oneshot_floor・workflow_review と同じ呼び出し(行のキャッシュの鍵も同じ)
    row = mea.run_case(dll, subject, REVIEW_CLICK, conventional=False)
    xy = (int(row["xy"][0]), int(row["xy"][1]))
    ctx = None
    if detail:
        region = fx.load_eval_region(subject)
        inside = gt & (rgba[..., 3] >= 128)
        if region is not None:
            inside &= region
        tolerated, _ = gtt.tolerance_for_subject(subject, rgba, inside, region, gtt.load_overrides())
        ctx = {"inside": inside, "region": region, "tolerated": tolerated}
    n_masked = MASKED_SUBJECT_CASES.get(sid, 0)
    exclude = _build_review_mask(gt).astype(bool) if n_masked > 0 else None
    perturbed = _perturbed_ids()
    items = []
    for i, case in enumerate(cases):
        for masked in ((False, True) if i < n_masked else (False,)):
            cid = autotune_case_id(sid, case.suffix, masked)
            ex = exclude if masked else None
            target = _perturb_target(case.target_rgb) if cid in perturbed else case.target_rgb
            r = oneshot.run(rgba, xy, target, state="autotune", emit=True, exclude=ex)
            ev = r["evidence"] or {}
            leak = r["leak"] or {}
            cond = {"state": "autotune", "subject": sid, "color": case.suffix, "click": REVIEW_CLICK,
                    "xy": list(xy), "sample_u8": r["sample_u8"], "target_u8": _u8(target),
                    "tolerance": (r["zone"] or {}).get("tolerance"), "method": r["method"],
                    "evidence_used": oneshot.evidence_used(r), "evidence_flood": bool(ev.get("flood")),
                    "evidence_zoomed": bool(ev.get("zoomed")),
                    "leak_excluded_parts": leak.get("excludedParts") if leak.get("checked") else None,
                    "exclude_px": int(ex.sum()) if ex is not None else 0,
                    "exclude_click_inside": bool(ex[xy[1], xy[0]]) if ex is not None else None,
                    "perturbed": cid in perturbed}
            first = i == 0 and not masked and cid not in perturbed
            items.append({"case_id": cid, "kind": "autotune", "input": rgba, "output": r["out"], "exclude": ex,
                          "shared_diff": None, "cond": cond, "evidence": r["evidence_mask"], "ctx": ctx,
                          "metrics": row["evidence"] if (detail and first) else None,
                          "baseline_key": f"{sid}/{REVIEW_CLICK}" if first else None})
    return items


def _repro_items() -> list[dict]:
    """再現データのケース。組み立て(テクスチャ・ゾーン・マスク・向き)は test_repro_cases と同じ。

    書き出したゾーンの設定のまま塗る分(replay)は、書き出しと同じく選択を使い回さずに測り、回帰が使い回して
    測った出力がキャッシュにあれば突き合わせる。自動調整の入力が書き出されたゾーンは、その入力から導き直して塗る
    (--autotune + 証拠。test_repro_case_autotune と同じ)。
    """
    from regression import fixtures as fx
    from regression import test_repro_cases as trc
    items = []
    for cid, d, kind, index in _repro_specs():
        if kind == "replay":
            rgba, zones, settings, exclude = trc.load_case(d)
            specs = [(zones, settings, f"repro_{d.name}")]
            out = fx.run_harness_many(rgba, specs, exclude=exclude, share_selection=False)[0]
            shared = fx.cached_outputs(rgba, specs, exclude=exclude, share_selection=True)[0]
            shared_diff = None if shared is None else int(np.any(out != shared, axis=-1).sum())
            cond = {"state": "repro_replay", "case": d.name, "zones": len(zones)}
        else:
            rgba, zones, settings, exclude, zm, _dumped = trc.load_autotune_case(d, index)
            out, params = fx.run_harness_autotune(rgba, zones, settings, exclude=exclude,
                                                  tag=f"repro_at_{d.name}_z{index}")
            shared_diff = None
            cond = {"state": "repro_autotune", "case": d.name, "zone": index,
                    "tolerance": params.get("tolerance"), "evidence_used": bool(params.get("evidence")),
                    "target_u8": _u8(zones[0]["target"]), "sample_u8": _u8(zones[0]["sample"])}
        ex = exclude.astype(bool) if exclude is not None else None
        cond["exclude_px"] = int(ex.sum()) if ex is not None else 0
        if ex is not None and ex.shape != rgba.shape[:2]:
            # 書き出しのマスクの解像度がテクスチャと違う(ハーネスが引き伸ばす)。引き伸ばし方を写さずに画素で突き合わせると
            # 縁で食い違うので、マスク契約は数えない(パネルの下の行に書く)
            cond["exclude_note"] = (f"マスク {ex.shape[1]}x{ex.shape[0]} がテクスチャ {rgba.shape[1]}x{rgba.shape[0]} と"
                                    "違うのでマスク契約は数えない")
            ex = None
        items.append({"case_id": cid, "kind": "repro", "input": rgba, "output": out, "exclude": ex,
                      "shared_diff": shared_diff, "cond": cond, "evidence": None, "ctx": None,
                      "metrics": None, "baseline_key": None})
    return items


def _iter_cases_window(detail: bool):
    """全ケースを **実 C# ハーネス**(窓の状態。上の節)で実行し、ケースの dict を順に返す。

    dict: case_id・kind("autotune" | "repro")・input・output・exclude(bool or None)・shared_diff(選択を使い回した
    出力との差 or None)・cond(どの状態で塗ったか。パネルの下の行と change_summary.json の conditions)・evidence
    (AI の証拠の範囲 or None)・ctx(GT の採点の材料。detail のときだけ)・metrics(基準と突き合わせる採点の行)・baseline_key。

    計算は先に子のプロセスで並べて結果キャッシュに置き(_warm_in_children)、ここではそれを被写体ごとに読む。
    被写体ごとに返す(呼び出し側が使い終えた出力は次の被写体の前に手放せる)。呼び出し側が 1 つの被写体の縮小・
    パネル・PNG の保存をしている間に、次の被写体を背景のスレッドで読む(先に求めるのは 1 被写体だけ。同時に持つ
    出力は、渡している被写体と計算中の被写体の 2 つぶんまで。全ケースを抱えて 10GB を超えた事故の対策を崩さない)。
    ハーネスを呼ぶのはこのスレッド 1 本だけで、渡す順・中身は直列のときと同じ。
    """
    from regression import fixtures as fx
    try:
        fx.ensure_harness()
    except fx.HarnessUnavailable as e:
        raise RuntimeError(f"dotnet / Unity DLL が利用できません(--engine csharp は使えません): {e}")
    except fx.HarnessBuildFailed as e:
        raise RuntimeError(f"Harness ビルド失敗:\n{e}")
    sids = list(_registry())
    _warm_in_children(sids)

    def _load(i: int) -> list[dict]:
        if i < len(sids):
            items = _window_subject_items(sids[i], detail)
        else:
            items = _repro_items()
        fx._mem_cache.clear()
        return items

    n = len(sids) + 1          # 最後は再現データ
    with ThreadPoolExecutor(max_workers=1) as pool:
        ahead = pool.submit(_load, 0)
        for i in range(n):
            items = ahead.result()
            ahead = pool.submit(_load, i + 1) if i + 1 < n else None
            yield from items
            del items


def _warm_in_children(sids: list[str]) -> None:
    """状態 (b) の出力を子のプロセスで並べて求め、結果キャッシュ(oneshot.py・run_case の行)に置く。

    1 ケースは dotnet 1 プロセス(AI の推論と自動調整と 4K の塗り)で、165 件を 1 本で回すと長い。被写体を
    workflow_review と同じ決め方の本数(IROCA_WORKFLOW_JOBS か空きメモリと CPU。上限 3)の子へ配る(配り方は汎化レポートと
    同じ)。子は _window_subject_items をそのまま呼ぶので、親が読むときの鍵と同じ。子は親と同じ来歴の DLL を固定する。
    結果キャッシュを切っている(IROCA_RESULT_CACHE=0・IROCA_ROW_CACHE=0)・1 本しか回せないときは何もしない(親が直列に求める)。
    """
    import workflow_review
    from regression import fixtures as fx
    if os.environ.get("IROCA_RESULT_CACHE", "1") == "0" or os.environ.get("IROCA_ROW_CACHE", "1") == "0":
        return
    workflow_review._backend()          # dev_safe/scripts を sys.path へ(_jobs が generalization_report を読む)
    jobs = min(workflow_review._jobs(), len(sids))
    print(f"[visual_review] 状態 (b) を {jobs} 本の子のプロセスで求めてキャッシュに置きます", flush=True)
    if jobs <= 1:
        return
    import generalization_report as gr
    import subprocess
    import time
    t0 = time.perf_counter()
    work = REVIEW_DIR / "_warm"
    work.mkdir(parents=True, exist_ok=True)
    info = fx.harness_info()
    info_path = work / "harness_info.json"
    if info is not None:
        info.save(info_path)
    threads = str(max(2, (os.cpu_count() or 4) // jobs))
    procs = []
    for i in range(jobs):
        part = gr.shard_subjects(list(sids), i, jobs)
        cmd = [sys.executable, str(Path(__file__).resolve()), "_warm", "--subjects", *part]
        if info is not None:
            cmd += ["--info", str(info_path)]
        env = dict(os.environ, PYTEST_XDIST_WORKER=f"vr{i}", IROCA_HARNESS_THREADS=threads, PYTHONIOENCODING="utf-8")
        procs.append(subprocess.Popen(cmd, env=env, cwd=str(_ROOT)))
    codes = [p.wait() for p in procs]
    info_path.unlink(missing_ok=True)
    if any(codes):
        raise RuntimeError(f"状態 (b) のキャッシュを温める子のプロセスが失敗しました: {codes}")
    print(f"[visual_review] 状態 (b) のキャッシュを温め終えました({time.perf_counter() - t0:.0f} 秒)", flush=True)


def _warm_main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--subjects", nargs="*", default=[])
    ap.add_argument("--info", default=None)
    a = ap.parse_args(argv)
    if os.name == "nt":
        import ctypes   # 親と同じく低い優先度で
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x00004000)
    from regression import fixtures as fx
    if a.info:
        import harness_build as hb
        fx.pin_harness(hb.BuildInfo.load(Path(a.info)))
    for sid in a.subjects:
        items = _window_subject_items(sid, detail=False)
        print(f"[warm] {sid}: {len(items)} ケース", flush=True)
        del items
        fx._mem_cache.clear()
    return 0


def _iter_engine(engine: str, detail: bool = False):
    """ケースの dict(_iter_cases_window)を順に返す。C# は被写体ごとに実行する。"""
    if engine == "csharp":
        yield from _iter_cases_window(detail)
        return
    _run_all_cases()


# ---------------------------------------------------------------------------
# 変化の切り分け（N-11: 512px サムネイルだけでは 4K の変化を判定できない）
#
# 「元テクスチャにないノイズが出ていないか」を 512px 3 列パネルで見るのは、2K/4K では
# 物理的に無理がある。実際 2026-08-06〜07 の 2 回のレビューとも、判定のために
# 「変化画素の分類」と「等倍クロップ」を別途スクリプトで書き起こす必要があった。
# ここに組み込んで、数値で当たりを付けてから目視できるようにする。
# ---------------------------------------------------------------------------
CROP_SIZE = 384  # 等倍クロップの一辺（px）


def _changed_px(before: np.ndarray, after: np.ndarray) -> np.ndarray:
    """変更前と RGBA のどれかが違う画素(α だけの変化も変化として数える。点検 B-7⑤)。"""
    return np.any(before != after, axis=-1)


def _classify_change(orig: np.ndarray, before: np.ndarray, after: np.ndarray) -> dict:
    """変化画素を「元へ復帰 / 元から離れた」に分類する。

    元テクスチャからの L1 距離が縮んだ画素は **誤爆着色の除去**、伸びた画素は
    **新規/強化された着色**。総変化画素数だけでは、この 2 つが打ち消し合って
    「大きく変わった」以上のことが分からない。変化は RGBA の 4 チャンネルで数え、α が変わった画素は
    alpha_changed にも数える(2026-11-10 まで RGB だけを見ていて、α だけの変化は「ビット一致」になった)。
    変更前とビット一致なら(ほとんどのケース)、int16 の写しを作らずに 0 を返す(結果は同じ。性能の候補 T1)。
    """
    if before.shape == after.shape and np.array_equal(before, after):
        return {"changed": 0, "alpha_changed": 0, "toward_original": 0, "away_from_original": 0, "max_delta": 0}
    o = orig.astype(np.int16)
    b = before.astype(np.int16)
    a = after.astype(np.int16)
    ch = _changed_px(before, after)
    n = int(ch.sum())
    if n == 0:
        return {"changed": 0, "alpha_changed": 0, "toward_original": 0, "away_from_original": 0, "max_delta": 0}
    db = np.abs(b - o).sum(-1)[ch]
    da = np.abs(a - o).sum(-1)[ch]
    return {
        "changed": n,
        "alpha_changed": int((before[..., 3] != after[..., 3]).sum()),
        "toward_original": int((da < db).sum()),
        "away_from_original": int((da > db).sum()),
        "max_delta": int(np.abs(b - a).max()),
    }


def alpha_violations(orig: np.ndarray, after: np.ndarray) -> int:
    """出力の α が入力の α と違う画素の数。再着色は色だけを変える不変条件(golden の test_alpha_is_preserved が
    合成入力で見ている)なので、実物の被写体でもマスク契約と同じく機械で数え、1 画素でもあれば承認しない。"""
    return int((orig[..., 3] != after[..., 3]).sum())


def _densest_change_window(changed: np.ndarray, size: int) -> tuple[int, int]:
    """変化画素が最も密な size×size 窓の左上座標。積分画像 + 粗いステップで探す。

    候補の窓(左上が step おきの格子)の和を numpy でまとめて求める(性能の候補 T7。以前は Python の二重ループ)。
    同点なら行優先で最初の窓(y が小さい方、同じ y なら x が小さい方)を選ぶ。argmax は最初の最大を返すので、
    以前のループ(厳密な > で更新)と同じ窓になる。
    """
    h, w = changed.shape
    size = min(size, h, w)
    ii = np.pad(np.cumsum(np.cumsum(changed.astype(np.int64), axis=0), axis=1),
                ((1, 0), (1, 0)))
    step = max(1, size // 4)
    ys = np.arange(0, h - size + 1, step)
    xs = np.arange(0, w - size + 1, step)
    win = (ii[np.ix_(ys + size, xs + size)] - ii[np.ix_(ys, xs + size)]
           - ii[np.ix_(ys + size, xs)] + ii[np.ix_(ys, xs)])
    iy, ix = np.unravel_index(int(np.argmax(win)), win.shape)
    return int(ys[iy]), int(xs[ix])


def phantom_change(orig: np.ndarray, before: np.ndarray, after: np.ndarray) -> dict:
    """出力にだけある線(元では同じ色の隣が出力で分かれた所。dev_safe の regression/phantom_edges.py)の変化。

    変化画素の数では、塗る範囲が広がった・狭まったのと、元に無い線が増えたのとを見分けられない。
    線の組の数を変更前後で数え、変更後にだけある線の画素(new)を返す。等倍クロップはそこを中心にする。
    """
    from regression import phantom_edges as pe
    pb = pe.analyze(orig, before, want_marks=True)
    pa = pe.analyze(orig, after, want_marks=True)
    new = pa["marks"] & ~pb["marks"]
    return {"before": pb["pairs"], "after": pa["pairs"],
            "same_before": pb["same_color_split"], "same_after": pa["same_color_split"],
            "new_px": int(new.sum()), "_new": new, "_marks_after": pa["marks"]}


def _mark_lines(rgba: np.ndarray, marks: np.ndarray, new: np.ndarray) -> np.ndarray:
    """線の画素に印を付けた複製(変更後にだけある線 = 赤紫、前からある線 = 黄)。"""
    out = rgba.copy()
    out[marks & ~new, :3] = (255, 220, 0)
    out[new, :3] = (255, 0, 200)
    return out


def phantom_crop_row(orig: np.ndarray, before: np.ndarray, after: np.ndarray, ph: dict) -> np.ndarray | None:
    """変更後にだけある線がいちばん密な所の等倍クロップ(元・変更前・変更後・印つき)。無ければ None。"""
    from regression import phantom_edges as pe
    if ph["new_px"] == 0:
        return None
    cy, cx = pe.densest_window(ph["_new"], CROP_SIZE)
    marked = _mark_lines(after, ph["_marks_after"], ph["_new"])
    return _hstack_with_sep([
        _add_label(_crop(orig, cy, cx, CROP_SIZE), f"等倍 元 @({cx},{cy}) 元に無い線が増えた所", (120, 40, 120)),
        _add_label(_crop(before, cy, cx, CROP_SIZE), "等倍 変更前", (160, 80, 30)),
        _add_label(_crop(after, cy, cx, CROP_SIZE), "等倍 変更後", (30, 140, 60)),
        _add_label(_crop(marked, cy, cx, CROP_SIZE), "印: 赤紫=増えた線 黄=前からの線", (120, 40, 120)),
    ])


def _crop(rgba: np.ndarray, y: int, x: int, size: int) -> np.ndarray:
    size = min(size, rgba.shape[0], rgba.shape[1])
    return rgba[y:y + size, x:x + size]


def _vstack_with_sep(rows: list[np.ndarray], sep_px: int = 4) -> np.ndarray:
    """行を縦に結合。幅が異なる場合は最大幅に合わせて右側を黒で埋める。"""
    max_w = max(r.shape[1] for r in rows)
    out: list[np.ndarray] = []
    sep = np.full((sep_px, max_w, 4), 200, dtype=np.uint8)
    for i, row in enumerate(rows):
        if i > 0:
            out.append(sep)
        if row.shape[1] < max_w:
            pad = np.zeros((row.shape[0], max_w - row.shape[1], 4), dtype=np.uint8)
            row = np.hstack([row, pad])
        out.append(row)
    return np.vstack(out)


def _make_thumb(rgba: np.ndarray, height: int = THUMB_HEIGHT) -> np.ndarray:
    """RGBA (H,W,4) を指定高さにリサイズ。アスペクト比維持。"""
    h, w = rgba.shape[:2]
    new_w = max(1, int(w * height / h))
    img = Image.fromarray(rgba).resize((new_w, height), Image.LANCZOS)
    return np.array(img)


_LABEL_FONT: object | None = None
_LABEL_FONT_LOADED = False


def _label_font():
    """ラベル用の日本語フォント。見つからなければ PIL 既定（英数のみ）へフォールバック。

    PIL の既定ビットマップフォントは CJK グリフを持たず、ラベルが全部豆腐（□）になる。
    ラベルはこのパネルで「どの列が何か」「違反何 px か」を伝える唯一の手段なので、
    OS 同梱の日本語フォントを探して使う。
    """
    global _LABEL_FONT, _LABEL_FONT_LOADED
    if _LABEL_FONT_LOADED:
        return _LABEL_FONT
    _LABEL_FONT_LOADED = True
    from PIL import ImageFont
    candidates = [
        (r"C:\Windows\Fonts\meiryo.ttc", 0),
        (r"C:\Windows\Fonts\YuGothM.ttc", 0),
        (r"C:\Windows\Fonts\msgothic.ttc", 0),
        ("/System/Library/Fonts/ttf/HiraginoSans-W4.ttc", 0),
        ("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", 0),
    ]
    for path, index in candidates:
        try:
            _LABEL_FONT = ImageFont.truetype(path, 15, index=index)
            return _LABEL_FONT
        except OSError:
            continue
    print("[compare] 警告: 日本語フォントが見つからず、ラベルが豆腐表示になります。")
    return None


LABEL_H = 24  # ラベルバーの高さ（px）


def _add_label(arr: np.ndarray, text: str, bg_rgb: tuple[int, int, int]) -> np.ndarray:
    """上部にカラーバー＋白テキストラベルを追加。"""
    img = Image.fromarray(arr)
    draw = ImageDraw.Draw(img)
    draw.rectangle([0, 0, img.width - 1, LABEL_H - 1], fill=(*bg_rgb, 220))
    draw.text((6, 3), text, fill=(255, 255, 255, 255), font=_label_font())
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
# 窓の状態のパネルの部品(ここと workflow_review が使う)
# ---------------------------------------------------------------------------
CAPTION_LINE_H = 22


def mark_click(thumb: np.ndarray, xy, shape) -> np.ndarray:
    """縮小にクリックの位置(上原点の画素)の印を付けた写し(赤紫の輪と十字)。"""
    if xy is None:
        return thumb.copy()
    h, w = shape[:2]
    th, tw = thumb.shape[:2]
    x, y = (float(xy[0]) + 0.5) * tw / w, (float(xy[1]) + 0.5) * th / h
    img = Image.fromarray(thumb.copy())
    d = ImageDraw.Draw(img)
    for r, col in ((9, (0, 0, 0, 255)), (8, (255, 0, 200, 255))):
        d.ellipse([x - r, y - r, x + r, y + r], outline=col, width=2)
    d.line([x - 13, y, x - 4, y], fill=(255, 0, 200, 255), width=2)
    d.line([x + 4, y, x + 13, y], fill=(255, 0, 200, 255), width=2)
    d.line([x, y - 13, x, y - 4], fill=(255, 0, 200, 255), width=2)
    d.line([x, y + 4, x, y + 13], fill=(255, 0, 200, 255), width=2)
    return np.array(img)


def mark_right_click(thumb: np.ndarray, xy, shape) -> np.ndarray:
    """縮小に右クリックの位置(上原点の画素)の印を付けた写し(水色の菱形。クリックの赤紫の輪と見分ける)。"""
    if xy is None:
        return thumb.copy()
    h, w = shape[:2]
    th, tw = thumb.shape[:2]
    x, y = (float(xy[0]) + 0.5) * tw / w, (float(xy[1]) + 0.5) * th / h
    img = Image.fromarray(thumb.copy())
    d = ImageDraw.Draw(img)
    for r, col in ((10, (0, 0, 0, 255)), (9, (0, 230, 255, 255))):
        d.polygon([(x, y - r), (x + r, y), (x, y + r), (x - r, y)], outline=col, width=2)
    d.point([(x, y)], fill=(0, 230, 255, 255))
    return np.array(img)


def caption_strip(width: int, lines: list[str], swatch_u8=None) -> np.ndarray:
    """パネルの下に付ける説明の行(どの状態で塗ったか)。swatch_u8 があれば左に塗り先の色の見本を置く。"""
    h = CAPTION_LINE_H * len(lines) + 8
    img = Image.new("RGBA", (width, h), (32, 32, 36, 255))
    d = ImageDraw.Draw(img)
    x0 = 6
    if swatch_u8 is not None:
        d.rectangle([6, 4, 6 + h - 12, h - 8], fill=(*[int(c) for c in swatch_u8], 255), outline=(255, 255, 255, 255))
        x0 = h + 2
    for i, text in enumerate(lines):
        d.text((x0, 4 + i * CAPTION_LINE_H), text, fill=(255, 255, 255, 255), font=_label_font())
    return np.array(img)


def gt_overlay(rgba: np.ndarray, out: np.ndarray, ctx: dict) -> tuple[np.ndarray, dict]:
    """GT 採点の色分け(緑=一致 赤=過検出 灰=許容(同色の別部位) 青=見逃し)と、適合・設計の適合・再現(島は数えない)。

    数え方は measure_evidence_autotune._score と同じ(変わった画素 = fixtures.recolored_mask、評価領域の中だけ)。
    """
    from regression import fixtures as fx
    inside, region, tolerated = ctx["inside"], ctx["region"], ctx["tolerated"]
    changed = fx.recolored_mask(rgba, out)
    if region is not None:
        changed &= region
    overlay = rgba.copy()
    overlay[changed & inside, :3] = (0, 200, 80)
    overlay[changed & ~inside & ~tolerated, :3] = (255, 40, 40)
    overlay[changed & ~inside & tolerated, :3] = (150, 150, 150)
    overlay[~changed & inside, :3] = (40, 100, 255)
    inter = int((changed & inside).sum())
    n_changed = int(changed.sum())
    n_tol = int((changed & tolerated).sum())
    m = {"precision": inter / max(1, n_changed), "precision_design": inter / max(1, n_changed - n_tol),
         "recall": inter / max(1, int(inside.sum()))}
    return overlay, m


def crop_around(xy, shape, size: int = CROP_SIZE) -> tuple[int, int]:
    """クリックの位置を中心にした size 四方の窓の左上 (y, x)(画像の内側に寄せる)。"""
    h, w = shape[:2]
    size = min(size, h, w)
    x = min(max(0, int(xy[0]) - size // 2), w - size)
    y = min(max(0, int(xy[1]) - size // 2), h - size)
    return y, x


def _fmt_u8(rgb) -> str:
    return "RGB(" + ",".join(str(int(c)) for c in rgb) + ")" if rgb is not None else "-"


def condition_lines(cond: dict) -> list[str]:
    """パネルの下の行: どの状態で塗ったか(クリックの位置・自動調整の有無・導いた許容範囲・塗り先・除外の有無)。"""
    st = cond.get("state")
    tol = cond.get("tolerance")
    tol_s = f"{tol:.3f}" if isinstance(tol, (int, float)) else "-"
    ex_px = cond.get("exclude_px") or 0
    ex_s = (f"除外あり {ex_px:,} px" + ("(クリックは守った側)" if cond.get("exclude_click_inside") else "")
            if ex_px else "除外なし")
    if st in ("autotune", "click", "assisted_include"):
        head = {"autotune": "状態 (b) クリック → AI の証拠つき自動調整 → 漏れの確かめ → 塗る",
                "click": "状態 (a) クリック直後(新しいゾーンの既定のまま。自動調整なし)",
                "assisted_include": "状態 (d) 状態 (b) の後に、塗り残しの所を右クリック「ここも塗る」(製品の右クリックの手)"}[st]
        lines = [f"{head} / {cond.get('subject')}",
                 f"クリック {cond.get('click')} @({cond['xy'][0]},{cond['xy'][1]}) 上原点  スポイト {_fmt_u8(cond.get('sample_u8'))}"
                 f"  塗り先 {cond.get('color', '')} {_fmt_u8(cond.get('target_u8'))}"]
        if st == "click":
            lines.append(f"許容 {tol_s}(新しいゾーンの既定)  {ex_s}")
        else:
            ev = ("証拠 使った" if cond.get("evidence_used") else "証拠 使わず") + \
                 ("(背景まで広がった)" if cond.get("evidence_flood") else "") + \
                 ("・拡大して推論" if cond.get("evidence_zoomed") else "")
            leak = cond.get("leak_excluded_parts")
            leak_s = f"漏れの確かめ 外した部品 {leak}" if leak is not None else "漏れの確かめ なし"
            extra = ""
            if st == "assisted_include":
                extra = (f"  右クリックで含めた {'はい' if cond.get('include_applied') else 'いいえ'}"
                         + (f"({cond['include_skip']})" if not cond.get("include_applied") and cond.get("include_skip")
                            else ""))
            lines.append(f"導いた許容 {tol_s}  方法 {cond.get('method')}  {ev}  {leak_s}  {ex_s}{extra}")
            if st == "assisted_include":
                depth = cond.get("residual_depth") or 0.0
                rxy = cond.get("right_click_xy")
                if rxy is not None:
                    lines.append(f"右クリック(水色の菱形) @({rxy[0]},{rxy[1]}) 上原点: 塗り残しの所 = 正解の中でまだ塗れていない"
                                 f"画素 {cond.get('residual_px', 0):,} のいちばん内側(深さ {depth:.1f} 画素)")
                else:
                    lines.append(f"塗り残しが無いので右クリックしない(正解の中でまだ塗れていない画素 {cond.get('residual_px', 0):,}・"
                                 f"いちばん深い所 {depth:.1f} 画素 < 2)。出力は状態 (b) と同じ")
        if cond.get("perturbed"):
            lines.append("※ 試験用に塗り先をずらした(IROCA_REVIEW_PERTURB。この比較は承認できない)")
        return lines
    note = [cond["exclude_note"]] if cond.get("exclude_note") else []
    if st == "repro_replay":
        return [f"再現データ {cond.get('case')}: 書き出したゾーンの設定のまま塗る(ゾーン {cond.get('zones')} 個)",
                f"向き: 書き出した PNG のまま(test_repro_cases と同じ)  {ex_s}"] + note
    if st == "repro_autotune":
        return [f"再現データ {cond.get('case')}: 書き出した自動調整の入力(ゾーン {cond.get('zone')})から導き直して塗る",
                f"スポイト {_fmt_u8(cond.get('sample_u8'))}  塗り先 {_fmt_u8(cond.get('target_u8'))}  導いた許容 {tol_s}"
                f"  証拠 {'使った' if cond.get('evidence_used') else '無し'}  {ex_s}"] + note
    return [json.dumps(cond, ensure_ascii=False)]


# ---------------------------------------------------------------------------
# 測る DLL と中身(snapshot・compare 共通。2026-11-10、点検 B-1・B-4・B-5・B-7①)
# ---------------------------------------------------------------------------
SNAPSHOT_META = SNAPSHOT_BEFORE_DIR / "meta.json"


def _prepare_harness(source: str, allow_external: bool, cmd: str):
    """source(worktree / index / rev:<rev>)のコードでハーネスをビルドして写し、このプロセスで測る DLL に決める。

    返すのは来歴(tools/harness_build.BuildInfo)。IROCA_HARNESS_DLL(来歴の無い外の DLL)は、allow_external の
    ときだけ使い(None を返す)、その結果は承認できない。
    """
    from regression import fixtures as fx
    if fx.external_dll():
        if not allow_external:
            print(f"[{cmd}] IROCA_HARNESS_DLL={fx.external_dll()} が立っています。出荷ゲートは来歴の分かる DLL"
                  "(このツールがビルドしたもの)で測ります。外して回すか、見るだけなら --allow-external-dll を付けてください"
                  "(その結果は承認できません)。")
            sys.exit(1)
        fx.ensure_harness()
        return None
    try:
        info = fx.build_harness_info(source)
    except fx.HarnessUnavailable as e:
        raise RuntimeError(f"dotnet / Unity DLL が利用できません(--engine csharp は使えません): {e}")
    except fx.HarnessBuildFailed as e:
        raise RuntimeError(f"Harness ビルド失敗:\n{e}")
    fx.pin_harness(info)
    print(f"[{cmd}] ハーネス {info.dll_sha256[:16]} を測ります(コードは {source} から)")
    return info


def _measured_state(source: str, info) -> dict:
    """測り始めの中身。ビルドした DLL の来歴と食い違えば(ビルドの後に編集された)止める(点検 B-7①)。"""
    st = rs.state(source)
    if info is not None:
        bad = sorted(k for k, v in info.sources.items() if st.get(k) != v)
        if bad:
            raise RuntimeError(f"ビルドした後にソースが変わりました: {', '.join(bad[:6])}。やり直してください")
    return st


def _git_head() -> str:
    import subprocess
    r = subprocess.run(["git", "rev-parse", "HEAD"], cwd=_ROOT, capture_output=True, text=True)
    return r.stdout.strip()


# ---------------------------------------------------------------------------
# snapshot
# ---------------------------------------------------------------------------
def cmd_snapshot(engine: str = "csharp", source: str = "rev:HEAD", allow_external: bool = False) -> None:
    """変更前の出力を全ケース分 snapshot_before(と workflow_review/snapshot)に保存する。

    既定は HEAD のコードをビルドして撮る(点検 B-4。「変更前」がいつも HEAD になり、共有の作業ツリーにある他人の
    未コミットの変更も入らない。作業ツリーと HEAD のコードが同じなら作業ツリーの DLL を使うので、実行キャッシュに当たる)。
    meta.json に、どのコード・測定器・基準から撮ったか(state)を残し、compare と approve が「変更前と同じ中身を
    測っていないか」を確かめる。
    """
    info = _prepare_harness(source, allow_external, "snapshot")
    st0 = _measured_state(source, info) if info is not None else None
    SNAPSHOT_BEFORE_DIR.mkdir(parents=True, exist_ok=True)
    # 途中で止まった snapshot を、来歴つきの完全なものと取り違えないように、先に古い meta を消す
    SNAPSHOT_META.unlink(missing_ok=True)
    import workflow_review
    (workflow_review.REVIEW / "snapshot" / "meta.json").unlink(missing_ok=True)
    print(f"[snapshot] 全ケースをスナップショット中 (engine={engine}, コード={source}) …")
    # 1 件ずつ保存して手放す(全ケースの出力を同時に抱えるとメモリが尽きる。compare と同じ)。
    # 4K の PNG の圧縮(1 枚 0.5 秒前後)はスレッドで並べる(圧縮の間は GIL を放す)。待ちの数に上限を付けて、
    # 保存を待つ出力を持ちすぎない。中身は 1 本で保存したときと同じバイト列。
    saved: list[str] = []
    pending: list = []
    with ThreadPoolExecutor(max_workers=SNAPSHOT_SAVE_WORKERS) as pool:
        for item in _iter_engine(engine):
            case_id = item["case_id"]
            pending.append(pool.submit(Image.fromarray(item["output"]).save, SNAPSHOT_BEFORE_DIR / f"{case_id}.png",
                                       compress_level=SNAPSHOT_PNG_LEVEL))
            saved.append(case_id)
            print(f"  保存: {case_id}.png")
            del item
            while len(pending) > 2 * SNAPSHOT_SAVE_WORKERS:
                pending.pop(0).result()
        for f in pending:
            f.result()
    # 一覧から外れたケースの古い画(2026-11-21 より前の固定設定の分など)を消す(相手の PC に 1GB 超が残っていた)
    for old in SNAPSHOT_BEFORE_DIR.glob("*.png"):
        if old.stem not in set(saved):
            old.unlink()
    print(f"[snapshot] {len(saved)} ケース完了 → {SNAPSHOT_BEFORE_DIR}")
    workflow_review.generate(snapshot=True, measured={"source": source, "state": st0, "run_id": None})
    if st0 is not None:
        changed = rs.diff(rs.state(source), st0)
        if changed:
            raise RuntimeError(f"snapshot の途中で中身が変わりました: {', '.join(changed[:6])}。撮り直してください")
    meta = {
        "timestamp": datetime.now().isoformat(),
        "cases": sorted(saved),
        # 来歴(点検 B-4)。state が無い(外の DLL・2026-11-10 より前の)snapshot は、何から撮ったか分からない
        "source": source,
        "state": st0,
        "state_digest": rs.digest(st0) if st0 is not None else None,
        "head": _git_head(),
        "dirty": (rs.code_state("worktree") != rs.code_state(source)) if st0 is not None else None,
        "dll_sha256": info.dll_sha256 if info is not None else None,
    }
    text = json.dumps(meta, indent=2, ensure_ascii=False)
    SNAPSHOT_META.write_text(text, encoding="utf-8")
    (workflow_review.REVIEW / "snapshot" / "meta.json").write_text(text, encoding="utf-8")
    if meta["dirty"]:
        print("[snapshot] 注意: 作業ツリーのコードは、撮ったコードと違います(compare で、その違いが変化として出ます)。")


def snapshot_provenance(st: dict) -> dict:
    """今の snapshot が、測る中身 st に対してどんな「変更前」か(compare が review.json に残し、approve が見る)。"""
    if not SNAPSHOT_META.exists():
        return {"present": False}
    meta = json.loads(SNAPSHOT_META.read_text(encoding="utf-8"))
    out = {"present": True, "timestamp": meta.get("timestamp"), "source": meta.get("source"),
           "head": meta.get("head"), "state_digest": meta.get("state_digest")}
    snap_state = meta.get("state")
    out["legacy"] = snap_state is None
    out["same_as_compare"] = snap_state == st if snap_state is not None else None
    out["code_same_as_compare"] = (rs.code_part(snap_state) == rs.code_part(st)) if snap_state is not None else None
    return out


def snapshot_rule(doc: dict, committed: dict) -> str | None:
    """approve してよいか(点検 B-4)。断るときは理由を返す。

    - 比較で測った中身がコミット済みの中身(本体と dev_safe の HEAD)と同じなら、HEAD を確かめただけなので通す。
    - snapshot が無いなら、全パネルが「変更前なし」として目視に回っているので通す。
    - 来歴の無い(古い形の)snapshot は、コードが HEAD と同じ比較だけ通す(コードを変えたのに変更前の中身が分からない)。
    - 変更前と同じ中身を測った比較は、変化 0 が何も示さないので断る。
    """
    st = doc["state"]
    if st == committed:
        return None
    snap = doc.get("snapshot") or {}
    if not snap.get("present"):
        return None
    if snap.get("legacy"):
        if rs.code_part(st) == rs.code_part(committed):
            return None
        return ("変更前(snapshot)がどのコードから撮られたか分かりません(古い形)。コードを変えた比較の承認には、"
                "来歴のある snapshot が要ります: python tools/visual_review.py snapshot(既定で HEAD から)→ compare")
    if snap.get("same_as_compare"):
        return ("変更前(snapshot)と同じ中身を測っています。変化 0 は何も示しません。HEAD から撮り直すか"
                "(python tools/visual_review.py snapshot)、測定器・基準を変えたときは変える前に撮った snapshot と比べてください")
    return None


# ---------------------------------------------------------------------------
# compare
# ---------------------------------------------------------------------------
def cmd_compare(engine: str = "csharp", source: str = "worktree", allow_external: bool = False) -> None:
    """3列比較パネル（元テクスチャ | 変更前 | 変更後）を全ケース分生成する。

    engine="csharp" のとき「変更後」を実 C# Harness の出力で描画する(出荷物を忠実に確認)。
    source="index" ならステージした中身のコードをビルドして測る(共有の作業ツリーにある他人の未コミットを入れない)。
    測り始めと終わりの中身(state)、比較の実行の id(run_id)、変更前の来歴を review.json に残す。approve と pre-commit は
    これを見る(時刻は見ない)。
    """
    info = _prepare_harness(source, allow_external, "compare")
    st0 = _measured_state(source, info) if info is not None else rs.state(source)
    run_id = uuid.uuid4().hex
    snap = snapshot_provenance(st0)
    if snap.get("legacy"):
        print("[compare] 注意: 変更前(snapshot)の来歴がありません(古い形か外の DLL)。何のコードから撮ったか分からないので、"
              "コードを変えた比較は承認できません(python tools/visual_review.py snapshot で HEAD から撮り直す)。")
    elif snap.get("same_as_compare"):
        print("[compare] 注意: 変更前(snapshot)と同じ中身を測っています。変化 0 は何も示しません"
              "(HEAD を確かめるだけなら承認できます)。")
    elif snap.get("present") and snap.get("code_same_as_compare") is False and snap.get("source") == "worktree":
        print("[compare] 注意: 変更前は作業ツリーから撮っています。共有の作業ツリーでは他人の未コミットが入りえます。")
    COMPARE_DIR.mkdir(parents=True, exist_ok=True)

    # snapshot_before は、そのケースを描くときに 1 枚ずつ読む。4K の全ケース(1 件 64MB × 100 超)と
    # 変更後の全出力を同時に抱えると 10GB を超え、Unity などと並べるとメモリが尽きた(2026-10-04)。
    before_paths: dict[str, Path] = {}
    meta_path = SNAPSHOT_BEFORE_DIR / "meta.json"
    if meta_path.exists():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        for cid in meta["cases"]:
            p = SNAPSHOT_BEFORE_DIR / f"{cid}.png"
            if p.exists():
                before_paths[cid] = p
        print(f"[compare] 変更前スナップショット: {len(before_paths)} ケース")
    else:
        print("[compare] 警告: snapshot_before が見つかりません（変更前との比較なし）。")

    # 現在のアルゴリズムで全ケース実行(被写体ごとに受け取り、描いたら手放す)
    print(f"[compare] 現在のアルゴリズムで全ケースを実行中 (engine={engine}) …")
    import workflow_review
    baselines = workflow_review.review_baselines()

    generated: list[Path] = []
    to_review: list[Path] = []             # 目視が要るパネル（変化あり、または変更前なし）
    contract: dict[str, int] = {}          # マスク付きケース -> 契約違反画素数
    change_summary: dict[str, dict] = {}   # ケース -> 変化画素の内訳
    conditions: dict[str, dict] = {}       # ケース -> どの状態で塗ったか(パネルの下の行と同じ中身)
    share_checked: list[str] = []          # 回帰が選択を使い回して測った出力と突き合わせたケース
    share_leaks: dict[str, int] = {}       # そのうち出力が違ったケース -> 違う画素数(選択キャッシュの鍵の漏れ)
    alpha_contract: dict[str, int] = {}    # α が入力と違う画素があったケース -> 画素数(B-7⑤)
    baseline_checked: list[str] = []       # 基準と突き合わせたケース(状態 (b) の塗り先の 1 色目)
    baseline_regs: dict[str, list[str]] = {}   # そのうち基準から退行したケース -> 指標
    perturbed: list[str] = []              # 試験用に出力をわざと変えたケース(承認できない)
    orig_thumb_src: np.ndarray | None = None   # 元テクスチャの縮小を作った入力(同じ被写体のケースで使い回す)
    orig_thumb_raw: np.ndarray | None = None
    def _read_before(cid: str) -> list:
        bp = before_paths.get(cid)
        return [np.array(Image.open(bp).convert("RGBA")) if bp else None]

    def _with_before(items):
        """ケースと、その変更前の画(PNG のデコード 4K で約 0.2 秒)を、次のケースの分を背景のスレッドで先に読みながら返す
        (デコードは GIL を放す。同時に持つのは今のケースと次のケースの変更前の 2 枚まで。性能の候補 T1)。
        変更前は 1 要素のリストに入れて渡す(受け取る側が pop して手放せるように。Future も持ち続けない)。"""
        with ThreadPoolExecutor(max_workers=1) as pool:
            prev = None
            for it in items:
                cur = (it, pool.submit(_read_before, it["case_id"]))
                if prev is not None:
                    yield prev[0], prev[1].result()
                prev = cur
            if prev is not None:
                yield prev[0], prev[1].result()

    for item, before_box in _with_before(_iter_engine(engine, detail=True)):
        before_rgba: np.ndarray | None = before_box.pop()
        case_id, input_rgba, after_rgba = item["case_id"], item["input"], item["output"]
        exclude, shared_diff, cond = item["exclude"], item["shared_diff"], item["cond"]
        if shared_diff is not None:
            share_checked.append(case_id)
            if shared_diff:
                share_leaks[case_id] = shared_diff
        n_alpha = alpha_violations(input_rgba, after_rgba)
        if n_alpha:
            alpha_contract[case_id] = n_alpha
        if cond.get("perturbed"):
            perturbed.append(case_id)
        lines = condition_lines(cond)
        if item["kind"] == "autotune":
            after_label = f"変更後: 自動調整の後 / {cond['color']}"
        else:
            after_label = "変更後: 再現データ"
        if exclude is not None:
            # マスク契約: 除外指定した画素は RGBA が 1 ステップも変わってはならない。
            # 目視(512px サムネイル)では絶対に検出できないので、ここで機械的に数える。
            violated = int(np.any(input_rgba[exclude] != after_rgba[exclude], axis=-1).sum())
            contract[case_id] = violated
            after_label += f" + 除外(違反 {violated:,})"
            lines.append(f"マスク契約: 守った画素 {int(exclude.sum()):,} のうち変わった画素 {violated:,}(0 でなければ承認できない)")
        if item["baseline_key"] is not None and item["metrics"] is not None:
            regs = workflow_review.baseline_regressions("autotune", item["baseline_key"], item["metrics"], baselines)
            if regs is not None:
                baseline_checked.append(case_id)
                if regs:
                    baseline_regs[case_id] = regs
                m = item["metrics"]
                lines.append(f"基準({workflow_review.baseline_label('autotune')})との突き合わせ: "
                             f"{'退行 ' + ', '.join(regs) if regs else '退行なし'}  "
                             f"(設計 IoU {m['iou_design']:.3f} 適合 {m['precision_design']:.3f} 再現 {m['recall']:.3f} "
                             f"島 {m['island'] * 100:.2f}%)")
        conditions[case_id] = cond

        # 縮小(4K から LANCZOS で 1 枚約 0.13 秒)は使い回す(性能の候補 T1。パネルの画素は同じ):
        # 元テクスチャは同じ被写体のケースで同じ配列なので被写体ごとに 1 回、変更前が変更後とビット一致なら
        # 変更後の縮小をそのまま使う。ラベルは写しに描く(使い回す縮小を書き換えない)。
        if input_rgba is not orig_thumb_src:
            orig_thumb_src, orig_thumb_raw = input_rgba, _make_thumb(input_rgba)
        xy = cond.get("xy")
        orig_thumb = _add_label(
            mark_click(orig_thumb_raw, xy, input_rgba.shape),
            "元テクスチャ" + (f" / クリック @({xy[0]},{xy[1]})" if xy else ""),
            (30, 80, 160),
        )
        after_thumb_raw = _make_thumb(after_rgba)
        after_thumb = _add_label(
            after_thumb_raw.copy(),
            after_label,
            (30, 140, 60) if exclude is None or not contract[case_id] else (190, 30, 30),
        )

        diff_px = None
        if before_rgba is not None:
            same_as_before = before_rgba.shape == after_rgba.shape and np.array_equal(before_rgba, after_rgba)
            before_thumb = _add_label(
                (after_thumb_raw if same_as_before else _make_thumb(before_rgba)).copy(),
                "変更前 (Before / Old)",
                (160, 80, 30),
            )
            rows = [_hstack_with_sep([orig_thumb, before_thumb, after_thumb])]
        else:
            placeholder = np.full_like(orig_thumb, 64)
            placeholder = _add_label(placeholder, "変更前なし (snapshot に無いケース)", (80, 80, 80))
            rows = [_hstack_with_sep([orig_thumb, placeholder, after_thumb])]

        # ── 2 段目(状態 (b)): AI の証拠の範囲・GT 採点・クリックの周りの等倍 ──
        if item["kind"] == "autotune" and item["ctx"] is not None:
            evidence = input_rgba.copy()
            if item["evidence"] is not None:
                evidence[item["evidence"], :3] = (0, 200, 255)
            if exclude is not None:
                evidence[exclude, :3] = (evidence[exclude, :3] // 2 + np.array([90, 0, 90], np.uint8))
            overlay, qm = gt_overlay(input_rgba, after_rgba, item["ctx"])
            cy, cx = crop_around(xy, input_rgba.shape)
            ev_label = "AI 証拠(水色)" + (" / 除外(紫)" if exclude is not None else "") + \
                ("" if item["evidence"] is not None else " / 証拠なし")
            rows.append(_hstack_with_sep([
                _add_label(_make_thumb(evidence), ev_label, (40, 65, 95)),
                _add_label(_make_thumb(overlay), f"GT採点 適合 {qm['precision']:.3f}→設計 {qm['precision_design']:.3f} "
                                                 f"再現 {qm['recall']:.3f}", (40, 65, 95)),
                _add_label(_crop(after_rgba, cy, cx, CROP_SIZE), f"等倍 変更後 @({cx},{cy}) クリックの周り", (40, 65, 95)),
            ]))
            lines.append("GT採点の色: 緑=一致 赤=過検出 灰=許容(同色の別部位) 青=見逃し(島は数えない。基準の行は島まで数える)")
            del evidence, overlay

        # ── 等倍クロップ行(N-11): 変化が最も密な領域を 1:1 で並べる ──
        # サムネイルは 4096px を 512px へ 1/8 に縮小するため、細い縁のノイズや
        # 質感の潰れは原理的に見えない。変化があるケースだけ足す。
        if before_rgba is not None:
            stats = _classify_change(input_rgba, before_rgba, after_rgba)
            change_summary[case_id] = stats
            if stats["changed"] > 0:
                diff_px = _changed_px(before_rgba, after_rgba)
                cy, cx = _densest_change_window(diff_px, CROP_SIZE)
                rows.append(_hstack_with_sep([
                    _add_label(_crop(input_rgba, cy, cx, CROP_SIZE),
                               f"等倍 元 @({cx},{cy}) 変化の密な所", (30, 80, 160)),
                    _add_label(_crop(before_rgba, cy, cx, CROP_SIZE),
                               "等倍 変更前", (160, 80, 30)),
                    _add_label(_crop(after_rgba, cy, cx, CROP_SIZE),
                               f"等倍 変更後 (最大差 {stats['max_delta']})", (30, 140, 60)),
                ]))
                # 次の段: 元に無い線が増えた所(変化の密な所とは別の場所のことが多い)
                ph = phantom_change(input_rgba, before_rgba, after_rgba)
                stats["phantom"] = {k: v for k, v in ph.items() if not k.startswith("_")}
                ph_row = phantom_crop_row(input_rgba, before_rgba, after_rgba, ph)
                if ph_row is not None:
                    rows.append(ph_row)
                del ph
                lines.append(f"変更前から変わった画素 {stats['changed']:,}(元へ戻った {stats['toward_original']:,} / "
                             f"元から離れた {stats['away_from_original']:,})  元に無い線の組 "
                             f"{stats['phantom']['before']:,} → {stats['phantom']['after']:,}")
        panel = _vstack_with_sep(rows)
        swatch = cond.get("target_u8")
        panel = _vstack_with_sep([panel, caption_strip(panel.shape[1], lines, swatch)])

        out_path = COMPARE_DIR / f"{case_id}_comparison.png"
        Image.fromarray(panel).save(out_path)
        generated.append(out_path)
        if before_rgba is None or change_summary[case_id]["changed"] > 0:
            to_review.append(out_path)
        print(f"  → {out_path.name}")
        del item, input_rgba, after_rgba, before_rgba, panel, rows, diff_px

    # 古いファイルを掃除（今回生成していないもの）
    for old in COMPARE_DIR.glob("*_comparison.png"):
        if old not in generated:
            old.unlink()

    # マスク契約の検査結果を残す（approve が読んで違反時にブロックする）。
    MASK_CONTRACT_JSON.write_text(
        json.dumps({"checked_at": datetime.now().isoformat(), "run_id": run_id, "cases": contract},
                   indent=2, ensure_ascii=False),
        encoding="utf-8")

    CHANGE_SUMMARY_JSON.write_text(
        json.dumps({"generated_at": datetime.now().isoformat(), "run_id": run_id, "cases": change_summary,
                    # 選択を使い回さずに測った出力と、回帰が使い回して測った出力(キャッシュにあるもの)の突き合わせ
                    "selection_share_check": {"checked": len(share_checked), "leaks": share_leaks},
                    # 出力の α が入力と違う画素(全ケースを数える。載っているのは違反のあったケースだけ)
                    "alpha_contract": {"checked": len(generated), "violations": alpha_contract},
                    # 基準(workflow_review.review_baselines)との突き合わせ(状態 (b) の塗り先の 1 色目。退行は承認不可)
                    "baseline_check": {"checked": len(baseline_checked), "regressions": baseline_regs},
                    # 試験用に出力をわざと変えたケース(IROCA_REVIEW_PERTURB。1 件でもあれば承認不可)
                    "perturbed": perturbed,
                    # どの状態で塗ったか(全ケース。approve が一覧の欠けを見る)
                    "conditions": conditions},
                   indent=2, ensure_ascii=False, default=_json_default), encoding="utf-8")

    print(f"[compare] {len(generated)} ケース完了")
    print(f"\n[compare] {len(generated)} 枚の比較パネルを生成しました。")
    print(f"保存先: {COMPARE_DIR}\n")

    # 数値で当たりを付けてから目視する（N-11）。変化の大きい順に並べる。
    changed_cases = {k: v for k, v in change_summary.items() if v["changed"]}
    if change_summary:
        total = sum(v["changed"] for v in change_summary.values())
        print(f"[変化サマリ] 変化ケース {len(changed_cases)}/{len(change_summary)} / "
              f"変化画素 合計 {total:,}")
        if changed_cases:
            print(f"  {'case':32}{'変化':>12}{'うちα':>8}{'元へ復帰':>12}{'元から離れた':>14}{'最大差':>7}")
            for cid, v in sorted(changed_cases.items(),
                                 key=lambda kv: -kv[1]["changed"]):
                print(f"  {cid:32}{v['changed']:>12,}{v.get('alpha_changed', 0):>8,}{v['toward_original']:>12,}"
                      f"{v['away_from_original']:>14,}{v['max_delta']:>7}")
            print("  ※ 変化の大きいケースから等倍クロップ(パネルの「変化の密な所」の段)を確認すること。")
            _print_phantom_table(changed_cases)
        else:
            print("  すべてのケースが変更前とビット一致（出力に影響しない変更）。")
        print()
    print(f"[選択の使い回し] 使い回さずに測った出力を、回帰が使い回して測った出力(キャッシュにある "
          f"{len(share_checked)}/{len(generated)} ケース)と突き合わせ: 食い違い {len(share_leaks)} ケース")
    for cid, n in sorted(share_leaks.items()):
        print(f"  NG  {cid}: {n:,} px(選択キャッシュの鍵に漏れがある。approve はブロックされます)")
    print(f"[α] 出力の α が入力と違うケース {len(alpha_contract)}/{len(generated)}")
    for cid, n in sorted(alpha_contract.items()):
        print(f"  NG  {cid}: {n:,} px(再着色は色だけを変える。approve はブロックされます)")
    print(f"[基準] 状態 (b) の塗り先の 1 色目を基準と突き合わせ {len(baseline_checked)} ケース: 退行 {len(baseline_regs)} ケース")
    for cid, regs in sorted(baseline_regs.items()):
        print(f"  NG  {cid}: {', '.join(regs)}(approve はブロックされます)")
    if perturbed:
        print(f"[試験] {PERTURB_ENV} で出力をわざと変えたケース {len(perturbed)}: {', '.join(perturbed)}(この比較は承認できません)")
    print()
    if contract:
        total = sum(contract.values())
        print(f"[マスク契約] {len(contract)} ケースを検査 / 違反 合計 {total:,} px")
        for cid, v in sorted(contract.items()):
            print(f"  {'NG' if v else 'ok'}  {cid}: 違反 {v:,} px")
        if total:
            print("  → 除外マスクで守った画素が書き換わっています。approve はブロックされます。")
        print()
    # 変更前とビット一致のケースは目視しなくてよい（2026-10-18 ユーザー決定）。
    # 変更前が無いケースは変化の有無が分からないので目視に回す。
    # 元に無い線が増えた順(次に変化の大きい順)に並べる。線の増えは IoU にも変化画素の数にも出ない。
    def _review_order(p: Path):
        st = change_summary.get(p.name[:-len("_comparison.png")], {})
        ph = st.get("phantom") or {}
        return (-(ph.get("after", 0) - ph.get("before", 0)), -st.get("changed", 0))
    to_review.sort(key=_review_order)
    print("=" * 60)
    print(f"【エージェントへ】変化があったケース {len(to_review)}/{len(generated)} 枚を "
          "Read ツールで 1 枚ずつ確認してください（変更前とビット一致のケースは読まなくてよい）。"
          "元に無い線が増えた順に並べています。「元に無い線が増えた所」の段があるパネルは、増えた線の所を必ず見ること。")
    print("=" * 60)
    for p in to_review:
        print(f"  {p}")
    print()
    # Windows コンソール(cp932)で例外にならないよう ASCII 記号を使う。
    print("各画像で以下を確認:")
    print("  [v] 元テクスチャにない質感・ノイズが追加されていないか")
    print("  [v] 変換対象エリアに変換漏れがないか")
    print("  [v] 関係ない部分（全体サムネイルで確認）が変わっていないか")
    print("  [v] 全体サムネイルで切り出し範囲外のエリアも確認したか")
    print()
    print("問題がなければ:")
    print('  python tools/visual_review.py approve --note "<目視した範囲と根拠>"')
    import workflow_review
    workflow_review.generate(measured={"source": source, "state": st0, "run_id": run_id, "snapshot": snap})


def _json_default(v):
    if isinstance(v, np.generic):
        return v.item()
    if isinstance(v, np.ndarray):
        return v.tolist()
    raise TypeError(f"JSON にできない値: {type(v)}")


def _print_phantom_table(changed_cases: dict) -> None:
    """変化したケースの、出力にだけある線の組の増減(増えた順)。"""
    rows = [(cid, v["phantom"]) for cid, v in changed_cases.items() if v.get("phantom")]
    if not rows:
        return
    rows.sort(key=lambda kv: -(kv[1]["after"] - kv[1]["before"]))
    up = sum(1 for _, p in rows if p["after"] > p["before"])
    tb, ta = sum(p["before"] for _, p in rows), sum(p["after"] for _, p in rows)
    print(f"[元に無い線] 変化ケースの線の組 {tb:,} → {ta:,}(増えた {up} / {len(rows)} ケース)")
    print(f"  {'case':32}{'変更前':>10}{'変更後':>10}{'増えた線の画素':>14}")
    for cid, p in rows[:20]:
        print(f"  {cid:32}{p['before']:>10,}{p['after']:>10,}{p['new_px']:>14,}")
    print("  ※ 増えたケースはパネルの「元に無い線が増えた所」の段(赤紫の印)を確認すること。")


# ---------------------------------------------------------------------------
# approve
# ---------------------------------------------------------------------------
def _load_run_json(path: Path, run_id: str, what: str) -> dict:
    """compare が書いた検査結果(mask_contract.json・change_summary.json)を、同じ比較の実行のものとして読む。"""
    if not path.exists():
        print(f"[approve] エラー: {what}の結果がありません。compare を実行してください:")
        print("  python tools/visual_review.py compare")
        sys.exit(1)
    doc = json.loads(path.read_text(encoding="utf-8"))
    if doc.get("run_id") != run_id:
        print(f"[approve] エラー: {what}の結果が、ワークフローと同じ比較(compare の 1 回)のものではありません。"
              "compare をやり直してください。")
        sys.exit(1)
    return doc


def summary_blockers(summary: dict, expected_ids: list[str]) -> list[str]:
    """change_summary.json のうち、目視では分からないので承認を止める項目(一覧の欠け・基準からの回帰・試験用の変更)。

    空なら止めない。マスク契約・α・選択の使い回しは cmd_approve が別に見る。
    """
    problems = []
    conds = summary.get("conditions")
    if conds is None:
        return ["どの状態で塗ったか(conditions)の記録がありません。compare をやり直してください"]
    missing = [c for c in expected_ids if c not in conds]
    extra = sorted(set(conds) - set(expected_ids))
    if missing:
        problems.append(f"一覧のケースが欠けています({len(missing)} 件): {', '.join(missing[:6])}"
                        f"{' …' if len(missing) > 6 else ''}")
    if extra:
        problems.append(f"一覧に無いケースを測っています({len(extra)} 件): {', '.join(extra[:6])}")
    bc = summary.get("baseline_check")
    if bc is None:
        problems.append("基準との突き合わせの結果がありません。compare をやり直してください")
    else:
        if bc.get("checked", 0) == 0:
            problems.append("基準と突き合わせたケースが 0 件です(基準のファイルか一覧が壊れている)")
        for cid, regs in sorted((bc.get("regressions") or {}).items()):
            problems.append(f"{cid}: 基準から退行 {', '.join(regs)}")
    if summary.get("perturbed"):
        problems.append(f"{PERTURB_ENV} で出力をわざと変えた比較です(試験用): {', '.join(summary['perturbed'])}")
    return problems


def cmd_approve(note: str | None = None) -> None:
    """視覚レビューの承認を、比較で測った中身(state)と比較の実行(run_id)に結び付けて書く。

    比較の後に測るものが変わっていれば断る(時刻ではなく中身で見る。点検 B-1・B-7①)。マスク契約・α・選択の
    使い回しの食い違いは目視では分からないので、compare が数えた結果を承認の前提にする。変更前と同じ中身を
    測った比較(変化 0 が何も示さない)は断る(点検 B-4。snapshot_rule)。
    """
    import workflow_review
    # 自動調整の後のパネルだけの承認ではクリック直後・追加操作後を検証できない。別条件の比較も必須。
    # 測った中身が今と同じことも確かめる。
    doc = workflow_review.validate_review()
    if not note or not note.strip():
        raise ValueError("--note に自動調整の後のパネルとワークフロー全ケースの目視範囲を記録してください")
    run_id = doc["run_id"]
    panels = sorted(COMPARE_DIR.glob("*_comparison.png"))
    if not panels:
        print("[approve] エラー: 比較パネルがありません。先に compare を実行してください:")
        print("  python tools/visual_review.py compare")
        sys.exit(1)

    # マスク契約(除外指定した画素は絶対に変わらない)は目視では検出できないので、
    # compare が数えた違反を承認の前提条件にする。人間の注意力に委ねない項目。
    contract = _load_run_json(MASK_CONTRACT_JSON, run_id, "マスク契約の検査")
    cases = contract.get("cases") or {}
    if not cases:
        print("[approve] エラー: マスク付きケースが 1 件も検査されていません"
              "(MASKED_SUBJECT_CASES が空 or compare が古い)。")
        sys.exit(1)
    violations = {k: v for k, v in cases.items() if v}
    if violations:
        print("[approve] エラー: 除外マスクで守った画素が書き換わっています(マスク契約違反):")
        for cid, v in sorted(violations.items()):
            print(f"    {cid}: {v:,} px")
        print("  これは目視では見えない退行です。承認せず原因を直してください。")
        sys.exit(1)
    # 選択の使い回し(回帰とプレビュー)と使い回さない経路(書き出し)の食い違いも、目視では分からない(点検 D-3)。
    summary = _load_run_json(CHANGE_SUMMARY_JSON, run_id, "変化の内訳")
    share = summary.get("selection_share_check")
    if share is None:
        print("[approve] エラー: 選択の使い回しの突き合わせの結果がありません。compare を実行してください。")
        sys.exit(1)
    if share["leaks"]:
        print("[approve] エラー: 選択を使い回した出力と使い回さない出力が食い違うケースがあります(選択キャッシュの鍵の漏れ):")
        for cid, v in sorted(share["leaks"].items()):
            print(f"    {cid}: {v:,} px")
        sys.exit(1)
    alpha = summary.get("alpha_contract")
    if alpha is None:
        print("[approve] エラー: α の検査結果がありません。compare を実行してください。")
        sys.exit(1)
    if alpha["violations"]:
        print("[approve] エラー: 出力の α が入力の α と違うケースがあります(再着色は色だけを変える):")
        for cid, v in sorted(alpha["violations"].items()):
            print(f"    {cid}: {v:,} px")
        sys.exit(1)
    # 一覧の欠け・基準からの回帰・試験用の出力の変更(2026-11-21〜)
    problems = summary_blockers(summary, window_case_ids())
    if problems:
        print("[approve] エラー:")
        for msg in problems:
            print(f"    {msg}")
        sys.exit(1)
    # 変更前が何から撮られたか(点検 B-4)
    refuse = snapshot_rule(doc, rs.committed_state())
    if refuse:
        print(f"[approve] エラー: {refuse}")
        sys.exit(1)
    snap = doc.get("snapshot") or {}
    if snap.get("legacy"):
        print("[approve] 注意: 変更前(snapshot)の来歴がありません。コードは HEAD と同じなので承認しますが、"
              "測定器・基準の変更は、変える前に撮った snapshot との比較でしか確かめられません。")

    REVIEW_DIR.mkdir(parents=True, exist_ok=True)
    # 既定の注記は「何をどこまで見たか」を書かない定型文だった。ツールが検証できない主張
    # （全パネル確認済み）を勝手に記録すると、承認記録そのものが信用できなくなるので、
    # 実際に見た範囲と根拠を --note で残せるようにしてある。
    marker = {
        "approved_at": datetime.now().isoformat(),
        "note": note,
        # 承認は時刻でなく、この比較の実行で測った中身に結び付く(pre-commit はこれとステージした中身を突き合わせる)
        "run_id": run_id,
        "state_source": doc["state_source"],
        "state": doc["state"],
        "panels": len(panels),
        "mask_contract_cases": len(cases),
        "mask_contract_violations": 0,
        "selection_share_checked": share["checked"],
        "alpha_checked": alpha["checked"],
        "baseline_checked": summary["baseline_check"]["checked"],
    }
    APPROVED_JSON.write_text(
        json.dumps(marker, indent=2, ensure_ascii=False), encoding="utf-8"
    )
    workflow_review.approve(note)
    print(f"[approve] マーカーを書き込みました: {APPROVED_JSON} (panels={len(panels)}, 測り元={doc['state_source']})")
    print("  コミットできるのは、範囲のファイルが「HEAD と同じ」か「この比較で測った中身と同じ」ものだけです"
          "(確かめるなら python tools/visual_review.py check --index)。")


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------
# テスト用の PC で snapshot / compare するときに持ち帰るもの(本体からの相対)。approve・check・pre-commit が読む分と、
# 目視するパネル。変更前の画(snapshot_before。1GB 超)は相手に置いたままにする(次の compare が相手で読む)。
REMOTE_COMPARE_FETCH = (
    "dev_safe/Tests/visual_review/compare",
    "dev_safe/Tests/visual_review/change_summary.json",
    "dev_safe/Tests/visual_review/mask_contract.json",
    "dev_safe/Tests/workflow_review/compare",
    "dev_safe/Tests/workflow_review/review.json",
)
_LOCAL_ENV = "IROCA_VISUAL_REVIEW_LOCAL"


def _run_on_remote(argv: list[str], command: str) -> int | None:
    """snapshot / compare をテスト用の PC で回す(2026-11-19)。使えなければ None(手元で回る)。

    相手は作業ツリー・索引・HEAD を手元と同じにしてから回すので(tools/remote_test.py)、測った中身(state)は手元で
    数え直しても同じになり、approve は手元で書ける。変更前(snapshot)は相手に残るので、snapshot と compare は
    同じ所で回す。相手で snapshot したら、手元の古い snapshot の印(meta.json)を消して、手元の compare が
    それと比べないようにする。
    """
    if os.environ.get(_LOCAL_ENV) == "1":
        return None
    import remote_test as rt
    t = rt.make_transport(None)
    info = rt.resolve(t, "auto", manifest=True)
    if info is None:
        return None
    fetch = list(REMOTE_COMPARE_FETCH) if command == "compare" else []
    rt.sweep_local_caches()
    try:
        rc = rt.run_remote(t, ["tools/visual_review.py", *argv], fetch, [f"{_LOCAL_ENV}=1"], info)
    except rt.Unavailable as e:
        rt.log(f"{e} → 手元で実行します")
        return None
    if rc == 0 and command == "compare":
        # compare は前の承認を消す(相手の分は持ち帰れないので、手元でも同じように消す)
        import workflow_review
        (workflow_review.REVIEW / "approved.json").unlink(missing_ok=True)
    if rc == 0 and command == "snapshot":
        import workflow_review
        SNAPSHOT_META.unlink(missing_ok=True)
        (workflow_review.REVIEW / "snapshot" / "meta.json").unlink(missing_ok=True)
        print("[snapshot] 変更前はテスト用の PC に置きました(手元の古い snapshot の印は消しました)。"
              "compare もテスト用の PC で回してください(既定でそうなります)。")
    return rc


def main() -> None:
    if len(sys.argv) > 1 and sys.argv[1] == "_warm":
        # 状態 (b) のキャッシュを温める子のプロセス(_warm_in_children が起こす)
        sys.exit(_warm_main(sys.argv[2:]))
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=("snapshot", "compare", "approve", "check"))
    ap.add_argument("--engine", default="csharp", choices=("python", "csharp"),
                    help="csharp のみ(Python 再実装は引退済み)")
    src = ap.add_mutually_exclusive_group()
    src.add_argument("--index", action="store_true",
                     help="compare / check: 作業ツリーでなく、ステージした中身のコードで測る・確かめる")
    src.add_argument("--worktree", action="store_true", help="snapshot: HEAD でなく作業ツリーのコードから撮る")
    src.add_argument("--rev", default=None, help="snapshot: このコミットのコードから撮る(既定 HEAD)")
    ap.add_argument("--allow-external-dll", action="store_true",
                    help="IROCA_HARNESS_DLL の DLL で測る(見るだけ。来歴が無いので承認できない)")
    ap.add_argument("--note", default=None, help="approve: 実際に目視した範囲と根拠")
    ap.add_argument("--local", action="store_true",
                    help="snapshot / compare: テスト用の PC が使えても手元で回す(既定は使えればテスト用の PC)")
    args = ap.parse_args()
    if args.engine == "python":
        _run_all_cases()
    if args.command in ("snapshot", "compare") and not args.local and not args.allow_external_dll:
        fwd = [args.command]
        if args.index:
            fwd.append("--index")
        if args.worktree:
            fwd.append("--worktree")
        if args.rev:
            # 相手にはブランチ名・タグが無いので、手元で SHA にしてから渡す
            import subprocess
            r = subprocess.run(["git", "rev-parse", "--verify", args.rev + "^{commit}"], cwd=_ROOT,
                               capture_output=True, text=True)
            if r.returncode != 0:
                ap.error(f"--rev {args.rev} がコミットを指していません")
            fwd += ["--rev", r.stdout.strip()]
        rc = _run_on_remote(fwd, args.command)
        if rc is not None:
            sys.exit(rc)
    if args.command == "snapshot":
        if args.index:
            ap.error("snapshot に --index は使えません(--worktree か --rev)")
        source = "worktree" if args.worktree else f"rev:{args.rev or 'HEAD'}"
        cmd_snapshot(args.engine, source, args.allow_external_dll)
    elif args.command == "compare":
        if args.worktree or args.rev:
            ap.error("compare は作業ツリー(既定)か --index で測ります")
        cmd_compare(args.engine, "index" if args.index else "worktree", args.allow_external_dll)
    elif args.command == "approve":
        cmd_approve(args.note)
    else:
        import workflow_review
        problems = workflow_review.check(index=args.index)
        if problems:
            print("[check] 承認と中身が合いません(compare → 目視 → approve をやり直す):")
            for p in problems:
                print(f"  {p}")
            sys.exit(1)
        print(f"[check] 承認は有効で、{'ステージした中身' if args.index else '作業ツリー'}をコミットしてもフックを通ります。")


if __name__ == "__main__":
    main()
