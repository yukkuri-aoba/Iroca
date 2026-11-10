"""テスト用の別 PC（リモート）側で動く受け手。`tools/remote_test.py` が実行のたびに写して呼ぶ。

標準ライブラリだけで書く（リモートの素の Python で動かすため）。置き場所は `<ホーム>/iroca-remote/agent.py` で、
そのフォルダを土台にする:

    iroca-remote/
      agent.py            このファイルの写し
      Iroca/              本体の写し（git。毎回スナップショットのコミットへ checkout -f する）
      Iroca/dev_safe/     dev_safe の写し（同上）
      UnityManaged/       ハーネスのビルドに要る Unity の DLL（UnityManaged に渡す）
      inbox/<run_id>/     1 回の実行で受け取ったバンドル・データ・依頼と、持ち帰る結果
      lock                同時に 2 つの実行が作業ツリーを書き換えないための排他

コマンド:
    info [--manifest]       状態を JSON で出す（--manifest でデータの一覧も）
    recv <run_id> <name>    標準入力を inbox/<run_id>/<name> へ書く
    run <run_id>            inbox/<run_id>/request.json に従って同期して実行する（排他つき）
    cat <run_id> <name>     inbox/<run_id>/<name> を標準出力へ書く
    cleanup <run_id>        inbox/<run_id> を消す
    extract-to <unity|models>   標準入力の tar を決まった置き場へ展開する（準備用）
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import time
from pathlib import Path

BASE = Path(__file__).resolve().parent
MAIN = BASE / "Iroca"
DEV_SAFE = MAIN / "dev_safe"
INBOX = BASE / "inbox"
UNITY_DIR = BASE / "UnityManaged"
LOCK_PATH = BASE / "lock"
REQUIREMENTS = Path("dev_safe") / "Tests" / "requirements.txt"
SNAP_REF = "refs/remote-test/snap"
LAST_REF = "refs/remote-test/last"
SYNC_FAILED = 97  # tools/remote_test.py と合わせる
_NAME_RE = re.compile(r"^[A-Za-z0-9_.-]{1,80}$")


def log(msg: str) -> None:
    print(f"[remote-test@{os.environ.get('COMPUTERNAME', 'remote')}] {msg}", file=sys.stderr, flush=True)


def _safe_name(name: str) -> str:
    if not _NAME_RE.match(name) or name in (".", ".."):
        raise SystemExit(f"不正な名前: {name!r}")
    return name


def _run_dir(run_id: str) -> Path:
    return INBOX / _safe_name(run_id)


def _git_exe() -> str | None:
    found = shutil.which("git")
    if found:
        return found
    for cand in (r"C:\Program Files\Git\cmd\git.exe", r"C:\Program Files (x86)\Git\cmd\git.exe"):
        if Path(cand).is_file():
            return cand
    return None


def git(repo: Path, *args: str, check: bool = True, capture: bool = True) -> subprocess.CompletedProcess:
    exe = _git_exe()
    if exe is None:
        raise SystemExit("git が見つからない（リモートに Git for Windows を入れる）")
    r = subprocess.run([exe, "-C", str(repo), *args], capture_output=capture)
    if check and r.returncode != 0:
        err = r.stderr.decode("utf-8", "replace") if capture else ""
        raise SystemExit(f"git {' '.join(args)} が失敗 ({r.returncode}): {err.strip()}")
    return r


def _rev(repo: Path, ref: str) -> str | None:
    if not (repo / ".git").exists():
        return None
    r = git(repo, "rev-parse", "--verify", "-q", ref + "^{commit}", check=False)
    return r.stdout.decode().strip() or None if r.returncode == 0 else None


def _dotnet_version() -> str | None:
    exe = shutil.which("dotnet")
    if exe is None:
        return None
    try:
        r = subprocess.run([exe, "--version"], capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return r.stdout.decode(errors="replace").strip() if r.returncode == 0 else None


def _models_dir() -> Path | None:
    root = os.environ.get("LOCALAPPDATA")
    return Path(root) / "Iroca" / "Models" if root else None


def data_manifest(dirs: list[str]) -> dict[str, list[int]]:
    """dev_safe の中で git が無視している（= git では届かない）データファイルの一覧。"""
    if not (DEV_SAFE / ".git").exists() or not dirs:
        return {}
    r = git(DEV_SAFE, "-c", "core.quotepath=false", "ls-files", "-oi", "--exclude-standard", "-z", "--", *dirs)
    out: dict[str, list[int]] = {}
    for rel in r.stdout.decode("utf-8", "replace").split("\0"):
        if not rel:
            continue
        try:
            st = (DEV_SAFE / rel).stat()
        except OSError:
            continue
        out[rel] = [st.st_size, int(st.st_mtime)]
    return out


def cmd_info(argv: list[str]) -> int:
    manifest_dirs: list[str] = []
    if argv and argv[0] == "--manifest":
        manifest_dirs = argv[1:]
    me = Path(__file__).read_bytes()
    models = _models_dir()
    info = {
        "agent": hashlib.sha256(me).hexdigest(),
        "computer": os.environ.get("COMPUTERNAME"),
        "python": sys.version.split()[0],
        "python_exe": sys.executable,
        "git": _git_exe() is not None,
        "dotnet": _dotnet_version(),
        "unity": (UNITY_DIR / "UnityEngine" / "UnityEngine.CoreModule.dll").is_file(),
        "models": bool(models and models.is_dir() and any(models.glob("*.onnx"))),
        "repos": {"main": _rev(MAIN, LAST_REF), "dev_safe": _rev(DEV_SAFE, LAST_REF)},
        "busy": _lock_busy(),
        "cpu_count": os.cpu_count(),
        "disk_free_gb": round(shutil.disk_usage(BASE).free / 1e9, 1),
    }
    if manifest_dirs:
        info["manifest"] = data_manifest(manifest_dirs)
    sys.stdout.write(json.dumps(info, ensure_ascii=False))
    sys.stdout.flush()
    return 0


def cmd_recv(argv: list[str]) -> int:
    run_id, name = argv
    d = _run_dir(run_id)
    d.mkdir(parents=True, exist_ok=True)
    dst = d / _safe_name(name)
    tmp = dst.with_name(dst.name + ".part")
    n = 0
    with open(tmp, "wb") as f:
        while True:
            chunk = sys.stdin.buffer.read(1 << 20)
            if not chunk:
                break
            f.write(chunk)
            n += len(chunk)
    os.replace(tmp, dst)
    log(f"受信 {name} {n / 1e6:.1f} MB")
    return 0


def cmd_cat(argv: list[str]) -> int:
    run_id, name = argv
    p = _run_dir(run_id) / _safe_name(name)
    if not p.is_file():
        return 3
    with open(p, "rb") as f:
        shutil.copyfileobj(f, sys.stdout.buffer, 1 << 20)
    sys.stdout.buffer.flush()
    return 0


def cmd_cleanup(argv: list[str]) -> int:
    d = _run_dir(argv[0])
    shutil.rmtree(d, ignore_errors=True)
    # 中断で残った古い受け取り（1 日以上）も片付ける
    if INBOX.is_dir():
        cutoff = time.time() - 86400
        for old in INBOX.iterdir():
            try:
                if old.stat().st_mtime < cutoff:
                    shutil.rmtree(old, ignore_errors=True)
            except OSError:
                pass
    return 0


def _extract_stream(fileobj, dest: Path) -> int:
    dest.mkdir(parents=True, exist_ok=True)
    n = 0
    with tarfile.open(fileobj=fileobj, mode="r|") as tf:
        for member in tf:
            tf.extract(member, dest, filter="data")
            n += 1
    return n


def cmd_extract_to(argv: list[str]) -> int:
    which = argv[0]
    if which == "unity":
        dest = UNITY_DIR
    elif which == "models":
        dest = _models_dir()
        if dest is None:
            raise SystemExit("LOCALAPPDATA が無い")
    else:
        raise SystemExit(f"置き場の名前が不正: {which}")
    n = _extract_stream(sys.stdin.buffer, dest)
    log(f"{which}: {n} 個を {dest} へ展開")
    return 0


# ---- 排他 ----

def _lock_busy() -> bool:
    import msvcrt
    try:
        fd = os.open(LOCK_PATH, os.O_RDWR | os.O_CREAT)
    except OSError:
        return False
    try:
        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
    except OSError:
        os.close(fd)
        return True
    msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
    os.close(fd)
    return False


def _acquire_lock() -> int:
    import msvcrt
    fd = os.open(LOCK_PATH, os.O_RDWR | os.O_CREAT)
    started = time.monotonic()
    last_note = 0.0
    while True:
        try:
            msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
            return fd
        except OSError:
            waited = time.monotonic() - started
            if waited - last_note >= 60 or last_note == 0.0:
                log(f"別の実行が終わるのを待っています（{int(waited)} 秒）")
                last_note = waited or 1e-9
            time.sleep(5)


# ---- 同期 ----

def _sync_repo(repo: Path, spec: dict, run_dir: Path, git_config: dict[str, str]) -> None:
    commit = spec["commit"]
    if not (repo / ".git").exists():
        repo.mkdir(parents=True, exist_ok=True)
        git(repo, "init", "-q")
    # dev_safe には 260 文字を超えるパスがある（手元の Git for Windows も longpaths で読んでいる）
    for key, value in {"core.longpaths": "true", **git_config}.items():
        git(repo, "config", key, value)
    bundle = spec.get("bundle")
    if bundle:
        path = run_dir / _safe_name(bundle)
        git(repo, "fetch", "-q", "--no-tags", str(path), f"+{SNAP_REF}:{LAST_REF}")
    have = _rev(repo, LAST_REF)
    if have != commit:
        raise SystemExit(f"{repo.name}: 受け取ったコミットが合わない（期待 {commit}・手元 {have}）")
    git(repo, "checkout", "-q", "-f", "--detach", commit)
    # テストが書き換えた追跡ファイルや、消えたファイルを戻す（無視されたキャッシュや .venv は残す）
    git(repo, "clean", "-q", "-f", "-d")


def _apply_data(run_dir: Path, req: dict) -> None:
    root = DEV_SAFE.resolve()
    for rel in req.get("data_delete", []):
        p = (DEV_SAFE / rel).resolve()
        if root not in p.parents:
            raise SystemExit(f"消す対象が dev_safe の外: {rel}")
        try:
            p.unlink()
        except FileNotFoundError:
            pass
    tar_name = req.get("data_tar")
    if tar_name:
        with open(run_dir / _safe_name(tar_name), "rb") as f:
            n = _extract_stream(f, DEV_SAFE)
        log(f"データ {n} 個を更新")
        (run_dir / tar_name).unlink(missing_ok=True)


def _ensure_venv() -> Path:
    venv_py = MAIN / ".venv" / "Scripts" / "python.exe"
    if not venv_py.is_file():
        log(".venv を作ります")
        subprocess.run([sys.executable, "-m", "venv", str(MAIN / ".venv")], check=True)
    req = MAIN / REQUIREMENTS
    if req.is_file():
        digest = hashlib.sha256(req.read_bytes()).hexdigest()
        stamp = MAIN / ".venv" / "iroca_requirements.sha256"
        if not stamp.is_file() or stamp.read_text().strip() != digest:
            log("依存を入れます（requirements.txt が変わった）")
            subprocess.run([str(venv_py), "-m", "pip", "install", "-q", "--disable-pip-version-check",
                            "-r", str(req)], check=True, stdout=sys.stderr)
            stamp.write_text(digest)
    return venv_py


def _pack_fetch(run_dir: Path, paths: list[str]) -> None:
    root = MAIN.resolve()
    out = run_dir / "fetch.tar"
    with tarfile.open(out, "w") as tf:
        for rel in paths:
            p = (MAIN / rel).resolve()
            if p != root and root not in p.parents:
                log(f"持ち帰りの対象が本体の外なので飛ばす: {rel}")
                continue
            if not p.exists():
                log(f"持ち帰りの対象が無い: {rel}")
                continue
            tf.add(p, arcname=p.relative_to(root).as_posix())


def cmd_run(argv: list[str]) -> int:
    run_id = argv[0]
    run_dir = _run_dir(run_id)
    req = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
    fd = _acquire_lock()
    try:
        t0 = time.monotonic()
        try:
            git_config = req.get("git_config", {})
            _sync_repo(MAIN, req["repos"]["main"], run_dir, git_config.get("main", {}))
            _sync_repo(DEV_SAFE, req["repos"]["dev_safe"], run_dir, git_config.get("dev_safe", {}))
            for b in ("main.bundle", "dev_safe.bundle"):
                (run_dir / b).unlink(missing_ok=True)
            _apply_data(run_dir, req)
            venv_py = _ensure_venv()
        except (SystemExit, OSError, subprocess.CalledProcessError, tarfile.TarError) as e:
            # まだ何も実行していない。呼び手はこの終了コードを見て手元でやり直せる
            log(f"同期に失敗: {e}")
            return SYNC_FAILED
        log(f"同期 {time.monotonic() - t0:.1f} 秒。実行: python {' '.join(req['argv'])}")
        env = os.environ.copy()
        env.update(req.get("env", {}))
        env.setdefault("PYTHONUTF8", "1")
        env.setdefault("PYTHONIOENCODING", "utf-8")
        env["IROCA_REMOTE_TEST"] = "1"
        if "UnityManaged" not in req.get("env", {}) and (UNITY_DIR / "UnityEngine").is_dir():
            env["UnityManaged"] = str(UNITY_DIR)
        t1 = time.monotonic()
        rc = subprocess.run([str(venv_py), *req["argv"]], cwd=str(MAIN), env=env).returncode
        log(f"終了コード {rc}・{time.monotonic() - t1:.1f} 秒")
        if req.get("fetch"):
            _pack_fetch(run_dir, req["fetch"])
        return rc
    finally:
        import msvcrt
        try:
            msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        os.close(fd)


COMMANDS = {
    "info": cmd_info,
    "recv": cmd_recv,
    "run": cmd_run,
    "cat": cmd_cat,
    "cleanup": cmd_cleanup,
    "extract-to": cmd_extract_to,
}


def main() -> int:
    if len(sys.argv) < 2 or sys.argv[1] not in COMMANDS:
        print(__doc__, file=sys.stderr)
        return 2
    BASE.mkdir(parents=True, exist_ok=True)
    # 呼び手はバイトをそのまま流すので、こちらの文字は UTF-8 にそろえる（相手の既定は cp932）
    # （データのファイル名には cp932 に無い文字もある）
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    return COMMANDS[sys.argv[1]](sys.argv[2:])


if __name__ == "__main__":
    sys.exit(main())
