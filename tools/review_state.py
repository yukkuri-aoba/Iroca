"""視覚レビューの承認を「測った中身」に結び付ける(2026-11-10、点検 B-1・B-4・B-7①・D-4)。

state = 測るものを決めるファイル(リポジトリのルートからの相対パス) → git の blob の id。3 つに分かれる。
  - コード: ハーネスがコンパイルする製品ソース(tools/harness_scope.py。Code/Core/**、Code/MaskSuggest/Ops/*.cs)と
    scripts/headless-run の追跡ファイル(Harness.cs・csproj・props)。作業ツリーのほか、index とコミットからも測れる
    (visual_review.py compare --index と snapshot の既定 HEAD)。
  - 測定器(本体): tools の計測用(MAIN_TOOLS)。動くのはいつも作業ツリーの中身。
  - dev_safe: 測定スクリプト・fixtures・基準・被写体一覧(DEV_SAFE_FILES)。dev_safe は別のリポジトリで、作業ツリーの中身。

blob の id は git が足すときと同じ変換(core.autocrlf など)をかけて数える(tools/harness_build.py の blobs_*)。
作業ツリーは CRLF、index とコミットは LF なので、バイト列のハッシュでは index と比べられない。

pre-commit(本体の scripts/hooks/pre-commit と dev_safe の hooks/pre-commit)は、ステージしたファイルのうち範囲のものが
「HEAD と同じ」か「承認した比較で測った中身と同じ」なら通す(ファイル単位。判断 17 の (b))。hunk を混ぜて stage した
ファイルはどちらとも違うので落ちる。もう一方のリポジトリの分は、承認した state と今の中身が同じことを求める。
"""
from __future__ import annotations

import hashlib
from pathlib import Path

from harness_build import blobs_index, blobs_tree, blobs_worktree, git
from harness_scope import harness_source_files, harness_source_globs, is_harness_source

ROOT = Path(__file__).resolve().parents[1]
DEV_SAFE = ROOT / "dev_safe"
HEADLESS = "scripts/headless-run"

# 本体の測定器(作業ツリーの中身が動く)。ゲートそのもの(check_visual_review.py)は測るものを変えないので入れない。
MAIN_TOOLS = (
    "tools/visual_review.py", "tools/workflow_review.py", "tools/workflow_contract.py",
    "tools/harness_build.py", "tools/harness_scope.py", "tools/review_state.py",
)
# dev_safe の測定器と基準(リポジトリのルートからの相対パス)。変われば測る中身・採点・回帰の判定が変わる。
DEV_SAFE_FILES = (
    "dev_safe/scripts/measure_evidence_autotune.py", "dev_safe/scripts/sam_proposal.py",
    "dev_safe/scripts/freeze_sam_fixtures.py", "dev_safe/measure_residual.py",
    "dev_safe/scripts/measure_sam_e2e.py", "dev_safe/scripts/measure_operating_curve.py",
    "dev_safe/measure_click_position.py", "dev_safe/Tests/regression/fixtures.py",
    "dev_safe/Tests/regression/headless_io.py",
    # 出力にだけある線の物差し(パネルの 3 段目と cases の phantom)
    "dev_safe/Tests/regression/phantom_edges.py",
    # GT 許容(同色の別部位)の規則と理由つき上書き。変われば design 採点が変わる
    "dev_safe/Tests/regression/gt_tolerance.py", "dev_safe/Tests/regression/colorspace.py",
    "dev_safe/Tests/Baselines/gt_tolerance_overrides.json",
    "dev_safe/Tests/subjects.json",
    "dev_safe/Tests/Baselines/refactor-pre/evidence_autotune_baseline.json",
    "dev_safe/Tests/Baselines/refactor-pre/assisted_include_baseline.json",
)


# ─────────────────────────── 範囲 ───────────────────────────
def is_code_path(rel: str) -> bool:
    """ハーネスがコンパイルする製品ソースか、ハーネスそのもの(scripts/headless-run の追跡ファイル)か。"""
    rel = rel.replace("\\", "/")
    if rel.startswith(HEADLESS + "/"):
        return "/bin/" not in rel and "/obj/" not in rel
    return is_harness_source(rel)


def in_main_scope(rel: str) -> bool:
    """本体のコミットで承認を求めるファイルか(コードと本体の測定器)。"""
    return is_code_path(rel) or rel.replace("\\", "/") in MAIN_TOOLS


def in_dev_safe_scope(rel_in_dev_safe: str) -> bool:
    """dev_safe のコミットで承認を求めるファイルか(dev_safe の中の相対パス)。"""
    return "dev_safe/" + rel_in_dev_safe.replace("\\", "/") in DEV_SAFE_FILES


def code_pathspecs() -> list[str]:
    """index から読むときの pathspec(git ls-files は :(glob) を解する)。"""
    return [f":(glob){g}" for g in harness_source_globs()] + [HEADLESS]


def code_dirs() -> list[str]:
    """コミットから読むときのディレクトリ(git ls-tree は glob を解さないので、glob の手前のディレクトリで引いて絞る)。"""
    dirs = set()
    for g in harness_source_globs():
        segs = g.split("/")
        cut = next(i for i, s in enumerate(segs) if any(c in s for c in "*?["))
        dirs.add("/".join(segs[:cut]))
    return sorted(dirs) + [HEADLESS]


def _headless_files_worktree() -> list[str]:
    raw = git(["ls-files", "-co", "--exclude-standard", "-z", "--", HEADLESS])
    return [p for p in raw.decode("utf-8").split("\0") if p and is_code_path(p)]


# ─────────────────────────── state ───────────────────────────
def code_state(source: str = "worktree") -> dict[str, str | None]:
    """コードの部分。source は worktree / index / rev:<rev>。"""
    if source == "worktree":
        rels = sorted({p.relative_to(ROOT).as_posix() for p in harness_source_files()} | set(_headless_files_worktree()))
        return {k: v for k, v in blobs_worktree(rels).items() if v is not None}
    if source == "index":
        return {k: v for k, v in blobs_index(code_pathspecs()).items() if is_code_path(k)}
    if source.startswith("rev:"):
        return {k: v for k, v in blobs_tree(source[4:], code_dirs()).items() if is_code_path(k)}
    raise ValueError(f"知らない測り元: {source}")


def tools_state() -> dict[str, str | None]:
    return {k: v for k, v in blobs_worktree(list(MAIN_TOOLS)).items() if v is not None}


def dev_safe_state() -> dict[str, str | None]:
    rels = [p[len("dev_safe/"):] for p in DEV_SAFE_FILES]
    got = blobs_worktree(rels, repo=DEV_SAFE)
    return {"dev_safe/" + k: v for k, v in got.items() if v is not None}


def state(code: str = "worktree") -> dict[str, str]:
    """測るものを決めるファイルの中身(コードは code から、測定器と dev_safe は作業ツリーから)。"""
    out = code_state(code)
    out.update(tools_state())
    out.update(dev_safe_state())
    return dict(sorted(out.items()))


def committed_state() -> dict[str, str]:
    """コミット済みの中身(本体の HEAD と dev_safe の HEAD)。これと同じものを測った承認は、HEAD を確かめただけ。"""
    out = code_state("rev:HEAD")
    out.update({k: v for k, v in blobs_tree("HEAD", list(MAIN_TOOLS)).items()})
    rels = [p[len("dev_safe/"):] for p in DEV_SAFE_FILES]
    out.update({"dev_safe/" + k: v for k, v in blobs_tree("HEAD", rels, repo=DEV_SAFE).items()})
    return dict(sorted(out.items()))


def code_part(st: dict) -> dict:
    return {k: v for k, v in st.items() if is_code_path(k)}


def main_part(st: dict) -> dict:
    return {k: v for k, v in st.items() if not k.startswith("dev_safe/")}


def dev_safe_part(st: dict) -> dict:
    return {k: v for k, v in st.items() if k.startswith("dev_safe/")}


def digest(st: dict) -> str:
    h = hashlib.sha256()
    for k in sorted(st):
        h.update(f"{k}\0{st[k]}\n".encode("utf-8"))
    return h.hexdigest()


def diff(a: dict, b: dict) -> list[str]:
    """中身の違うパス(片方にしか無いものを含む)。"""
    return sorted(k for k in set(a) | set(b) if a.get(k) != b.get(k))


# ─────────────────────────── フック ───────────────────────────
def staged(repo: Path = ROOT) -> list[str]:
    """ステージしたファイル(HEAD と index の違い。GIT_INDEX_FILE が立っていればその index。消したファイルも含む)。"""
    try:
        raw = git(["diff", "--cached", "--name-only", "--no-renames", "-z"], cwd=repo)
    except RuntimeError:            # 最初のコミット(HEAD が無い)
        raw = git(["ls-files", "-z"], cwd=repo)
    return [p for p in raw.decode("utf-8").split("\0") if p]


def check_staged(paths: list[str], approved: dict, repo: Path = ROOT, prefix: str = "") -> list[str]:
    """paths(repo の中の相対パス)のうち、index の中身が HEAD とも承認の中身とも違うものを理由つきで返す。

    approved のキーはリポジトリ(本体)のルートからの相対パス。dev_safe では prefix="dev_safe/" を付けて引く。
    """
    if not paths:
        return []
    idx = blobs_index([":(literal)" + p for p in paths], repo=repo)
    try:
        head = blobs_tree("HEAD", paths, repo=repo)
    except RuntimeError:
        head = {}
    bad = []
    for p in paths:
        now = idx.get(p)
        if now == head.get(p) or now == approved.get(prefix + p):
            continue
        if prefix + p not in approved and head.get(p) is None:
            why = "承認した比較の後に足したファイル(測っていない)"
        elif now is None:
            why = "消したが、承認した比較はこのファイルがある中身を測った"
        else:
            why = "ステージした中身が、承認した比較で測った中身とも HEAD とも違う(hunk を分けた・比較の後に編集した)"
        bad.append(f"{prefix}{p}: {why}")
    return bad
