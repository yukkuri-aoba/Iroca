"""ハーネス(scripts/headless-run)の DLL をビルドする共通の口(2026-11-10、点検 B-7②・B-6・D-5・B-1)。

使う所: dev_safe の fixtures.ensure_harness(回帰・視覚レビュー)、scripts/golden/golden_lib.build_harness、
tools/visual_review.py(index や HEAD から書き出したソースを測るとき)。

決まり:
- ビルドはプロセスをまたぐロック(%TEMP%/iroca_harness_build.lock)の中で行う。並列実行で同じ csproj を
  同時にビルドすると中間ファイルを奪い合って偽のビルド失敗になる。取れないまま上限を過ぎたら、取り残しと
  みなして進む(異常終了したプロセスのロックで全体が止まる方が害が大きい)。
- ビルドの前と後で、コンパイルするソースの中身(git の blob の id)を取る。食い違ったら(ビルド中に誰かが
  編集した)作り直す。DLL がどのソースから作られたかを来歴(sources)として返す。
- ソースの中身が前回のビルドと違うのに DLL のバイト列が同じなら、MSBuild が CoreCompile を飛ばした
  (mtime を保ったままのコピー・unzip など)とみなし、--no-incremental で作り直す。ソースを少しでも変えると
  DLL も変わる(決定論ビルドで PDB が文書のチェックサムを持ち、DLL がその PDB の id を持つ)ことを当てにしている。
- AI 提案の鍵用の DLL(SamOpsFingerprint)も、ハーネスのすぐ後に同じロックの中でビルドし、鍵をその場で
  計算する(以前は最初に提案を計算するとき、数分後に別にビルドしていて、その間の編集で鍵と DLL が食い違えた)。
- 作った DLL のフォルダは、DLL の sha256 の先頭 16 桁の名前で snap_root/<sha16>/ へ写し、来歴
  (provenance.json)を添える。写した DLL は、別のセッションが同じ作業ツリーで作り直しても変わらない。

blob の id は `git hash-object`(作業ツリー)・`git ls-files -s`(index)・`git ls-tree`(コミット)で取る。
core.autocrlf=true の作業ツリーは CRLF、blob は LF なので、ファイルのバイト列のハッシュでは index と比べられない。
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import time
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HEADLESS = "scripts/headless-run"
HARNESS_CSPROJ = f"{HEADLESS}/Harness.csproj"
HARNESS_DLL = f"{HEADLESS}/bin/Release/IrocaHeadless.dll"
SAMOPS_CSPROJ = f"{HEADLESS}/SamOpsFingerprint/SamOpsFingerprint.csproj"
SAMOPS_DLL = f"{HEADLESS}/SamOpsFingerprint/bin/Release/IrocaSamOpsFingerprint.dll"
HARNESS_CS = f"{HEADLESS}/Harness.cs"
# index・コミットから書き出すとき、ビルドに要るものが入るディレクトリ(ハーネスとそれが取り込む製品ソース)。
EXPORT_DIRS = ("Code/Core", "Code/MaskSuggest/Ops", HEADLESS)

LOCK = Path(tempfile.gettempdir()) / "iroca_harness_build.lock"
LOCK_WAIT_S = 900
BUILD_TIMEOUT_S = int(os.environ.get("VACC_HARNESS_BUILD_TIMEOUT", "600"))
RECORD_NAME = "iroca_build_record.json"     # obj/ に置く「ソースの中身 → DLL」の記録(B-7②)
PROVENANCE = "provenance.json"
# 写さないもの(実行に要らない。UnityEngine.CoreModule.xml だけで 3MB)
_SNAP_SKIP_SUFFIXES = (".xml", ".exe")


class BuildFailed(RuntimeError):
    """dotnet build が失敗した(環境は揃っている前提。Code/ のコンパイルエラーの可能性)。"""

    def __init__(self, message: str, output: str = ""):
        super().__init__(message)
        self.output = output


@dataclass
class BuildInfo:
    """ビルドした DLL とその来歴。"""
    dll: str                      # 使う DLL の絶対パス(写したなら写した先)
    dll_sha256: str
    source: str                   # "worktree" / "index" / "rev:<コミット>"
    sources: dict = field(default_factory=dict)   # リポジトリ相対パス -> blob の id(コンパイルした入力と csproj・props)
    samops_digest: str | None = None              # AI 提案の結果キャッシュの鍵(作れなければ None)
    samops_error: str = ""
    built_at: str = ""

    def save(self, path: Path) -> None:
        tmp = path.with_name(path.name + f".{os.getpid()}.tmp")
        tmp.write_text(json.dumps(asdict(self), indent=1, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp, path)

    @classmethod
    def load(cls, path: Path) -> "BuildInfo":
        doc = json.loads(Path(path).read_text(encoding="utf-8"))
        return cls(**{k: doc[k] for k in cls.__dataclass_fields__ if k in doc})


# ─────────────────────────── git ───────────────────────────
def git(args: list[str], cwd: Path = ROOT, input: bytes | None = None, env: dict | None = None) -> bytes:
    r = subprocess.run(["git", *args], cwd=str(cwd), input=input, capture_output=True, timeout=120,
                       env=None if env is None else {**os.environ, **env})
    if r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:3])} が失敗しました: {r.stderr.decode('utf-8', 'replace')}")
    return r.stdout


def blobs_worktree(rels: list[str], repo: Path = ROOT) -> dict[str, str | None]:
    """作業ツリーのファイルを git に足したときの blob の id(無いファイルは None)。autocrlf などの変換をかけて数える。"""
    present = [r for r in rels if (repo / r).is_file()]
    out: dict[str, str | None] = {r: None for r in rels}
    if present:
        ids = git(["hash-object", "--stdin-paths"], cwd=repo,
                  input="\n".join(present).encode("utf-8") + b"\n").decode().split()
        if len(ids) != len(present):
            raise RuntimeError("git hash-object の出力の数がファイルの数と合いません")
        out.update(zip(present, ids))
    return out


def blobs_index(pathspecs: list[str], repo: Path = ROOT) -> dict[str, str]:
    """index(GIT_INDEX_FILE が立っていればそれ)にある blob の id。pathspec は git の書き方(:(glob) も可)。"""
    raw = git(["ls-files", "-s", "-z", "--", *pathspecs], cwd=repo)
    out = {}
    for rec in raw.split(b"\0"):
        if not rec:
            continue
        meta, path = rec.split(b"\t", 1)
        out[path.decode("utf-8")] = meta.split()[1].decode()
    return out


def blobs_tree(rev: str, pathspecs: list[str], repo: Path = ROOT) -> dict[str, str]:
    """コミット rev にある blob の id。"""
    raw = git(["ls-tree", "-r", "-z", rev, "--", *pathspecs], cwd=repo)
    out = {}
    for rec in raw.split(b"\0"):
        if not rec:
            continue
        meta, path = rec.split(b"\t", 1)
        kind, oid = meta.split()[1:3]
        if kind == b"blob":
            out[path.decode("utf-8")] = oid.decode()
    return out


def rev_parse(rev: str, repo: Path = ROOT) -> str:
    return git(["rev-parse", "--verify", rev + "^{commit}"], cwd=repo).decode().strip()


def digest_of(sources: dict) -> str:
    """来歴(パス -> blob の id)を 1 つのダイジェストにする。"""
    h = hashlib.sha256()
    for k in sorted(sources):
        h.update(f"{k}\0{sources[k]}\n".encode("utf-8"))
    return h.hexdigest()


def file_sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ─────────────────────────── csproj の読み取り ───────────────────────────
def _expand(pattern: Path) -> list[Path]:
    parts = pattern.parts
    for i, seg in enumerate(parts):
        if any(c in seg for c in "*?["):
            base = Path(*parts[:i])
            return sorted(p for p in base.glob("/".join(parts[i:])) if p.is_file())
    return [pattern] if pattern.is_file() else []


def project_inputs(csproj: Path) -> tuple[list[Path], list[Path]]:
    """(コンパイルするファイル, csproj と Import した props)。MSBuild と同じく、Include の相対パスは csproj の
    フォルダから、$(MSBuildThisFileDirectory) はそれを書いたファイルのフォルダから読む。"""
    csproj = Path(csproj)
    projects, compiles = [csproj], []
    i = 0
    while i < len(projects):
        f = projects[i]
        i += 1
        text = f.read_text(encoding="utf-8-sig")
        for imp in re.findall(r'<Import\s+Project="([^"$]+)"', text):
            p = Path(os.path.normpath(f.parent / imp.replace("\\", "/")))
            if p.is_file() and p not in projects:
                projects.append(p)
        for inc in re.findall(r'<Compile\s+Include="([^"]+)"', text):
            norm = inc.replace("\\", "/")
            if norm.startswith("$(MSBuildThisFileDirectory)"):
                base, norm = f.parent, norm[len("$(MSBuildThisFileDirectory)"):]
            else:
                base = csproj.parent
            compiles.extend(_expand(Path(os.path.normpath(base / norm))))
    return sorted(set(compiles)), projects


def build_inputs(root: Path, with_samops: bool = True) -> list[str]:
    """root にあるハーネス(と鍵用の DLL)をビルドするときに読むファイル(root 相対、posix)。"""
    rels: set[str] = set()
    for csproj in [HARNESS_CSPROJ] + ([SAMOPS_CSPROJ] if with_samops else []):
        compiles, projects = project_inputs(root / csproj)
        rels.update(p.relative_to(root).as_posix() for p in compiles + projects)
    return sorted(rels)


# ─────────────────────────── ビルド ───────────────────────────
class _Lock:
    def __enter__(self):
        deadline = time.time() + LOCK_WAIT_S
        self.acquired = False
        while time.time() < deadline:
            try:
                fd = os.open(str(LOCK), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.close(fd)
                self.acquired = True
                break
            except FileExistsError:
                time.sleep(0.5)
        return self

    def __exit__(self, *exc):
        if self.acquired:
            try:
                LOCK.unlink()
            except OSError:
                pass


def build_lock() -> _Lock:
    """ハーネスのビルドのロック(fixtures・golden_lib・ここで同じものを使う)。"""
    return _Lock()


def _dotnet_build(csproj: Path, no_incremental: bool = False) -> subprocess.CompletedProcess:
    cmd = ["dotnet", "build", str(csproj), "-c", "Release", "-nologo", "-v", "quiet"]
    if no_incremental:
        cmd.append("--no-incremental")
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                          timeout=BUILD_TIMEOUT_S)


def _build_checked(root: Path, csproj_rel: str, dll_rel: str, sources: dict) -> subprocess.CompletedProcess:
    """1 つの csproj をビルドし、CoreCompile が飛んだ古い DLL なら作り直す(B-7②)。"""
    csproj, dll = root / csproj_rel, root / dll_rel
    compiles, _ = project_inputs(csproj)
    src_digest = digest_of({p.relative_to(root).as_posix(): sources.get(p.relative_to(root).as_posix())
                            for p in compiles})
    record_path = csproj.parent / "obj" / RECORD_NAME
    r = _dotnet_build(csproj)
    if r.returncode != 0 or not dll.is_file():
        return r
    sha = file_sha256(dll)
    try:
        prev = json.loads(record_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        prev = None
    if prev and prev.get("sources") != src_digest and prev.get("dll_sha256") == sha:
        # ソースが変わったのに DLL が同じ = MSBuild が mtime を見て CoreCompile を飛ばした
        r = _dotnet_build(csproj, no_incremental=True)
        if r.returncode != 0 or not dll.is_file():
            return r
        sha = file_sha256(dll)
    record_path.parent.mkdir(parents=True, exist_ok=True)
    record_path.write_text(json.dumps({"sources": src_digest, "dll_sha256": sha}), encoding="utf-8")
    return r


def _samops_digest(root: Path) -> str:
    # 形は以前の fixtures.samops_digest と同じ(作業ツリーのビルドなら、今あるキャッシュの鍵が変わらない)
    return "samops:" + file_sha256(root / SAMOPS_DLL) + ":" + file_sha256(root / HARNESS_CS)


def _build_in(root: Path, source: str, read_sources, with_samops: bool) -> BuildInfo:
    """root のハーネスをロックの中でビルドする。read_sources() は今のソースの来歴を返す(前後で 2 回呼ぶ)。"""
    last_out = ""
    with build_lock():
        for _attempt in range(3):
            before = read_sources()
            r = _build_checked(root, HARNESS_CSPROJ, HARNESS_DLL, before)
            last_out = (r.stdout or "") + (r.stderr or "")
            if r.returncode != 0 or not (root / HARNESS_DLL).is_file():
                raise BuildFailed("Harness のビルドが失敗しました", last_out)
            samops, samops_err = None, ""
            if with_samops:
                r2 = _build_checked(root, SAMOPS_CSPROJ, SAMOPS_DLL, before)
                if r2.returncode == 0 and (root / SAMOPS_DLL).is_file():
                    samops = _samops_digest(root)
                else:
                    samops_err = ((r2.stdout or "") + (r2.stderr or ""))[-1500:]
            dll_sha = file_sha256(root / HARNESS_DLL)
            after = read_sources()
            if after == before:
                return BuildInfo(dll=str(root / HARNESS_DLL), dll_sha256=dll_sha, source=source,
                                 sources=before, samops_digest=samops, samops_error=samops_err,
                                 built_at=datetime.now().isoformat(timespec="seconds"))
    raise BuildFailed("ビルドの間にソースが変わり続けました(3 回作り直しても前後が一致しない)", last_out)


def build_worktree(with_samops: bool = True) -> BuildInfo:
    """作業ツリーのハーネスを scripts/headless-run/bin/Release にビルドする(写さない)。"""
    def read():
        return blobs_worktree(build_inputs(ROOT, with_samops))
    return _build_in(ROOT, "worktree", read, with_samops)


def snapshot(info: BuildInfo, snap_root: Path) -> BuildInfo:
    """DLL のフォルダを snap_root/<sha16>/ へ写し、来歴を添えて、写した先を指す BuildInfo を返す。

    同じ DLL がもう写してあれば写し直さない(中身を確かめる)。使うたびにファイルの mtime を今にする
    (cache_gc.py は最後に使った時刻で消すので、使っている間は消えない)。
    """
    src_dir = Path(info.dll).parent
    dst_dir = Path(snap_root) / info.dll_sha256[:16]
    dst_dll = dst_dir / Path(info.dll).name
    names = [p.name for p in src_dir.iterdir() if p.is_file() and not p.name.endswith(_SNAP_SKIP_SUFFIXES)]
    complete = (dst_dll.is_file() and file_sha256(dst_dll) == info.dll_sha256
                and (dst_dir / PROVENANCE).is_file() and all((dst_dir / n).is_file() for n in names))
    if not complete:
        tmp = dst_dir.with_name(dst_dir.name + f".{os.getpid()}.tmp")
        shutil.rmtree(tmp, ignore_errors=True)
        tmp.mkdir(parents=True)
        for n in names:
            shutil.copyfile(src_dir / n, tmp / n)       # 日時は写さない(今の時刻になる)
        if file_sha256(tmp / dst_dll.name) != info.dll_sha256:
            shutil.rmtree(tmp, ignore_errors=True)
            raise BuildFailed(f"写している間に DLL が書き換わりました: {info.dll}")
        snap = BuildInfo(**{**asdict(info), "dll": str(dst_dll)})
        snap.save(tmp / PROVENANCE)
        shutil.rmtree(dst_dir, ignore_errors=True)
        try:
            tmp.replace(dst_dir)
        except OSError:            # 別のプロセスが先に置いた
            shutil.rmtree(tmp, ignore_errors=True)
    touch_snapshot(dst_dir)
    stored = BuildInfo.load(dst_dir / PROVENANCE)
    # 同じ DLL でも、来歴(どこから・いつ)は今回の分を返す
    return BuildInfo(**{**asdict(info), "dll": str(dst_dll), "samops_digest": info.samops_digest or stored.samops_digest})


def touch_snapshot(dst_dir: Path) -> None:
    for p in Path(dst_dir).iterdir():
        try:
            os.utime(p, None)
        except OSError:
            pass


def _export(root: Path, source: str) -> dict[str, str]:
    """index か コミットの EXPORT_DIRS を root へ書き出し、書き出したファイルの blob の id を返す。"""
    if source == "index":
        listing = blobs_index(list(EXPORT_DIRS))
        if listing:
            git(["checkout-index", "-f", "-z", "--stdin", f"--prefix={root.as_posix()}/"],
                input=b"\0".join(p.encode("utf-8") for p in listing) + b"\0")
        return listing
    rev = source[len("rev:"):]
    listing = blobs_tree(rev, list(EXPORT_DIRS))
    tar = root.with_name(root.name + ".tar")
    with open(tar, "wb") as f:
        f.write(git(["archive", "--format=tar", rev, "--", *EXPORT_DIRS]))
    try:
        with tarfile.open(tar) as t:
            t.extractall(root, filter="data")
    finally:
        tar.unlink(missing_ok=True)
    return listing


def build(source: str, snap_root: Path, work_root: Path | None = None, with_samops: bool = True) -> BuildInfo:
    """source("worktree" / "index" / "rev:<rev>")の中身でハーネスをビルドし、snap_root へ写した BuildInfo を返す。

    index とコミットは、中身(blob)が作業ツリーと同じなら作業ツリーでビルドする(同じ DLL になり、実行キャッシュに
    当たる)。違えば work_root へ書き出してビルドする(共有の bin/Release に触らない)。書き出したファイルは
    改行が CRLF になるので、作業ツリーで LF のまま編集したファイルがあると、同じ中身でも DLL のバイト列が違い
    (PDB が文書のチェックサムを持つ)、キャッシュに当たらない(出力は同じ)。
    """
    snap_root = Path(snap_root)
    if source == "worktree":
        return snapshot(build_worktree(with_samops), snap_root)
    if source.startswith("rev:"):
        source = "rev:" + rev_parse(source[len("rev:"):])
        listing = blobs_tree(source[4:], list(EXPORT_DIRS))
    elif source == "index":
        listing = blobs_index(list(EXPORT_DIRS))
    else:
        raise ValueError(f"知らないビルド元: {source}")
    inputs = build_inputs(ROOT, with_samops)
    compiled_in_listing = {r for r in listing if r.endswith((".cs", ".csproj", ".props"))}
    if compiled_in_listing == set(inputs) and blobs_worktree(inputs) == {r: listing[r] for r in inputs}:
        info = build_worktree(with_samops)
        if info.sources == {r: listing[r] for r in inputs}:
            return snapshot(BuildInfo(**{**asdict(info), "source": source}), snap_root)
    return build_export(source, listing, Path(work_root or snap_root.parent / "srcbuild"), snap_root, with_samops)


def build_export(source: str, listing: dict, work_root: Path, snap_root: Path, with_samops: bool = True) -> BuildInfo:
    """index かコミットの中身(listing はその blob の一覧)を書き出してビルドし、snap_root へ写した BuildInfo を返す。

    同じ中身(来歴のダイジェスト)を前に写してあれば、書き出しもビルドもしない。
    """
    index_file = snap_root / "by-source" / (digest_of(listing)[:16] + ".json")
    try:
        cached = json.loads(index_file.read_text(encoding="utf-8"))
        prov = snap_root / cached["sha16"] / PROVENANCE
        info = BuildInfo.load(prov)
        if Path(info.dll).is_file() and file_sha256(Path(info.dll)) == info.dll_sha256:
            touch_snapshot(prov.parent)
            os.utime(index_file, None)
            return BuildInfo(**{**asdict(info), "source": source})
    except (OSError, ValueError, KeyError, TypeError):
        pass
    root = Path(work_root) / f"{digest_of(listing)[:16]}.{os.getpid()}"
    shutil.rmtree(root, ignore_errors=True)
    root.mkdir(parents=True)
    try:
        exported = _export(root, source)
        if exported != listing:
            raise BuildFailed("書き出しの間に index が変わりました。やり直してください")
        rels = build_inputs(root, with_samops)
        missing = [r for r in rels if r not in listing]
        if missing:
            raise BuildFailed(f"ビルドに要るファイルが {source} にありません: {missing[:5]}")
        sources = {r: listing[r] for r in rels}
        info = _build_in(root, source, lambda: sources, with_samops)
        snap = snapshot(info, snap_root)
    finally:
        shutil.rmtree(root, ignore_errors=True)
    index_file.parent.mkdir(parents=True, exist_ok=True)
    index_file.write_text(json.dumps({"sha16": snap.dll_sha256[:16]}), encoding="utf-8")
    return snap


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="ハーネスをビルドして来歴を出す")
    ap.add_argument("--source", default="worktree", help="worktree / index / rev:<rev>")
    ap.add_argument("--snap-root", default=str(ROOT / "dev_safe" / "_harness_cache" / "dllsnap"))
    args = ap.parse_args()
    out = build(args.source, Path(args.snap_root))
    print(json.dumps({k: v for k, v in asdict(out).items() if k != "sources"}, indent=1, ensure_ascii=False))
    print(f"sources: {len(out.sources)} ファイル (digest {digest_of(out.sources)[:16]})")
