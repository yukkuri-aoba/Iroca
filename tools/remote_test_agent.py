"""テスト用の別 PC（リモート）側で動く受け手。`tools/remote_test.py` が実行のたびに写して呼ぶ。

標準ライブラリだけで書く（リモートの素の Python で動かすため）。置き場所は `<ホーム>/iroca-remote/agent.py` で、
そのフォルダを土台にする:

    iroca-remote/
      agent.py            このファイルの写し
      Iroca/              本体の写し（git。毎回スナップショットのコミットへ checkout -f し、HEAD だけ親＝呼び手の HEAD へ戻す）
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
    power [get | set <saver|balanced|performance>]   電源プランを見る・切り替える（確認用）

電源プラン: run は排他を取っている間だけ「高パフォーマンス」にし、終わったら「省電力」へ戻す
（普段は省電力で置くため。2026-11-14 のユーザー依頼）。
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
import threading
import time
from pathlib import Path

BASE = Path(__file__).resolve().parent
MAIN = BASE / "Iroca"
DEV_SAFE = MAIN / "dev_safe"
INBOX = BASE / "inbox"
UNITY_DIR = BASE / "UnityManaged"
# EditMode テスト（tools/editor_tests.py）用。Unity 本体と、この作業ツリーへリンクしたホストの Unity プロジェクト
UNITY_EDITOR_EXE = Path(r"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe")
EDITOR_HOST = BASE / "hosts" / "Iroca_U2022_ifx"
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


def _smart_app_control() -> int | None:
    """Windows の Smart App Control の状態(0=切・1=有効・2=評価中)。有効だと、自前でビルドした署名の無いハーネスの DLL を
    読めない(0x800711C7。2026-11-11 にテスト用 PC で評価中から有効へ自動で切り替わり、ハーネスの全件が落ちた)。"""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Control\CI\Policy") as k:
            return int(winreg.QueryValueEx(k, "VerifiedAndReputablePolicyState")[0])
    except OSError:
        return None


def _desktop_sessions() -> list[int]:
    """このユーザーがサインインしていてデスクトップ（explorer.exe）が動いているセッション。画面の要る Unity の台本
    （tools/editor_tests.py の -UndoReload・-SdkBuild）は、ssh の先（session 0）ではなくそこで動かす（2026-11-20）。"""
    if os.name != "nt":
        return []
    # ssh の先では USERDOMAIN が WORKGROUP になり、domain\user で絞ると何も出ない。ユーザー名だけで絞る
    user = os.environ.get("USERNAME", "")
    try:
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FI", f"USERNAME eq {user}",
                            "/FO", "CSV", "/NH"], capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return []
    out = []
    for line in r.stdout.decode("mbcs", "replace").splitlines():
        cols = [c.strip('"') for c in line.split('","')]
        if len(cols) >= 4 and cols[0].lower().endswith("explorer.exe") and cols[3].isdigit() and int(cols[3]) > 0:
            out.append(int(cols[3]))
    return out


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
        "main": str(MAIN),
        "python": sys.version.split()[0],
        "python_exe": sys.executable,
        "git": _git_exe() is not None,
        "dotnet": _dotnet_version(),
        "unity": (UNITY_DIR / "UnityEngine" / "UnityEngine.CoreModule.dll").is_file(),
        "unity_editor": UNITY_EDITOR_EXE.is_file() and (EDITOR_HOST / "Assets" / "IrocaEditorTests").exists(),
        "desktop_sessions": _desktop_sessions(),
        "models": bool(models and models.is_dir() and any(models.glob("*.onnx"))),
        "repos": {"main": _rev(MAIN, LAST_REF), "dev_safe": _rev(DEV_SAFE, LAST_REF)},
        "busy": _lock_busy(),
        "smart_app_control": _smart_app_control(),
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


# ---- 電源プラン ----
# 実行の間だけ「高パフォーマンス」、終わったら「省電力」へ戻す(つけっぱなしの消費電力のため。2026-11-14 のユーザー依頼)。
# `powercfg /setactive` で切り替える(管理者権限は不要)。この 2 つは Windows の標準の GUID で、`powercfg /list` に出ない
# 環境(モダンスタンバイの Win11 など)でも /setactive は通る(G83 で確認)。
# Win11 の「電源モード」(設定 > システム > 電源)はこれとは別物(オーバーレイ)で、ここでは触らない。
POWER_PLANS = {
    "saver": "a1841308-3541-4fab-bc81-f71556f20b4a",        # 省電力
    "balanced": "381b4222-f694-41f0-9685-ff5bb260df2e",     # バランス
    "performance": "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",  # 高パフォーマンス
}


def set_power_plan(name: str) -> bool:
    """電源プランを切り替える。切り替えられたら True。"""
    if os.name != "nt" or name not in POWER_PLANS:
        return False
    try:
        r = subprocess.run(["powercfg", "/setactive", POWER_PLANS[name]], capture_output=True, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        return False
    return r.returncode == 0


def get_power_plan() -> str | None:
    if os.name != "nt":
        return None
    try:
        r = subprocess.run(["powercfg", "/getactivescheme"], capture_output=True, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        return None
    m = re.search(rb"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", r.stdout)
    if r.returncode != 0 or not m:
        return None
    cur = m.group(0).decode().lower()
    for k, v in POWER_PLANS.items():
        if v == cur:
            return k
    return cur


def cmd_power(argv: list[str]) -> int:
    """power [get | set <saver|balanced|performance>] — 切り替えの確認用。"""
    if argv and argv[0] == "set" and len(argv) > 1:
        ok = set_power_plan(argv[1])
        print(f"set {argv[1]}: {'ok' if ok else 'failed'} -> {get_power_plan()}")
        return 0 if ok else 1
    print(get_power_plan())
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
    # HEAD を呼び手の HEAD（スナップショットの親）へ戻す。作業ツリーと索引はスナップショットのまま。
    # 手元と同じく「HEAD = 最後のコミット・作業ツリー = 未コミットの変更込み」にそろえるため（abcheck の
    # 既定の基準 --base-rev HEAD が、スナップショット自身＝作業ツリーと同じ中身を指して比べられなくなっていた）
    if _rev(repo, commit + "^") is not None:
        git(repo, "reset", "-q", "--soft", commit + "^")
    # 2 番目の親は呼び手の索引（ステージした中身。2026-11-19〜）。索引もそれにそろえる（作業ツリーは触らない）。
    # これで「HEAD・索引・作業ツリー」の 3 つとも手元と同じになり、compare --index も相手で測れる。
    if _rev(repo, commit + "^2") is not None:
        git(repo, "read-tree", commit + "^2")


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


class _HighQosSweeper:
    """実行したプロセスとその子孫を、Windows の低 QoS 扱い（E コアへ寄せる）から外し続ける（2026-11-12）。

    ssh の先で起動したプロセスはウィンドウの無いセッションで動くので、Windows 11 は裏の処理とみなして QoS を下げ、
    ハイブリッド CPU（P コアと E コア）では E コアへ寄せる。i5-1340P で、空いている P コアをほとんど使わずに
    E コアだけが張り付いていた。プロセスごとに SetProcessInformation(ProcessPowerThrottling) で
    「速さを絞らない」と指定すると P コアに乗る（忙しいスレッド 4 本で P コアの割合 50%→99%）。
    この指定は子プロセスへ引き継がれないので、pytest のワーカー・ハーネス（dotnet）・ビルドなど子孫を
    一定間隔で見つけて 1 回ずつ指定する。優先度（PriorityClass）は変えない。出力は変わらない。
    """

    INTERVAL_S = 0.25

    def __init__(self, root_pid: int):
        import ctypes
        from ctypes import wintypes

        class PROCESSENTRY32W(ctypes.Structure):
            _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                        ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                        ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                        ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
                        ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260)]

        class POWER_THROTTLING_STATE(ctypes.Structure):
            _fields_ = [("Version", wintypes.ULONG), ("ControlMask", wintypes.ULONG),
                        ("StateMask", wintypes.ULONG)]

        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        k32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
        k32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
        k32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
        k32.OpenProcess.restype = wintypes.HANDLE
        k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k32.SetProcessInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        k32.CloseHandle.argtypes = [wintypes.HANDLE]
        self._ctypes = ctypes
        self._k32 = k32
        self._entry_t = PROCESSENTRY32W
        # Version 1・ControlMask=EXECUTION_SPEED・StateMask=0: 速さを絞らない（常に通常の QoS）
        self._state = POWER_THROTTLING_STATE(1, 1, 0)
        self._known: set[int] = set()  # 自分の子孫として見つけた PID（終わったものも親の手がかりに残す）
        self._pending = {root_pid}
        self.applied = 0
        self.failed = 0

    def _snapshot(self) -> list[tuple[int, int]]:
        k32 = self._k32
        snap = k32.CreateToolhelp32Snapshot(0x00000002, 0)  # TH32CS_SNAPPROCESS
        if not snap or snap == self._invalid_handle:
            return []
        try:
            e = self._entry_t()
            e.dwSize = self._ctypes.sizeof(e)
            out = []
            ok = k32.Process32FirstW(snap, self._ctypes.byref(e))
            while ok:
                out.append((e.th32ProcessID, e.th32ParentProcessID))
                ok = k32.Process32NextW(snap, self._ctypes.byref(e))
            return out
        finally:
            k32.CloseHandle(snap)

    def _apply(self, pid: int) -> None:
        h = self._k32.OpenProcess(0x0200, False, pid)  # PROCESS_SET_INFORMATION
        if not h:
            self.failed += 1
            return
        try:
            if self._k32.SetProcessInformation(h, 4, self._ctypes.byref(self._state),  # ProcessPowerThrottling
                                               self._ctypes.sizeof(self._state)):
                self.applied += 1
            else:
                self.failed += 1
        finally:
            self._k32.CloseHandle(h)

    @property
    def _invalid_handle(self) -> int:
        return self._ctypes.c_void_p(-1).value  # INVALID_HANDLE_VALUE

    def sweep(self) -> None:
        procs = self._snapshot()
        alive = {pid for pid, _ in procs}
        for pid in list(self._pending):
            if pid in alive:
                self._known.add(pid)
                self._apply(pid)
            self._pending.discard(pid)
        # 親が既知なら自分の子孫。1 回の走査で孫まで拾えるよう、増えなくなるまで回す
        grew = True
        while grew:
            grew = False
            for pid, ppid in procs:
                if ppid in self._known and pid not in self._known and pid != ppid:
                    self._known.add(pid)
                    self._apply(pid)
                    grew = True

    def run_until(self, proc: subprocess.Popen) -> None:
        while proc.poll() is None:
            try:
                self.sweep()
            except OSError:
                pass
            time.sleep(self.INTERVAL_S)


def cmd_run(argv: list[str]) -> int:
    run_id = argv[0]
    run_dir = _run_dir(run_id)
    req = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
    fd = _acquire_lock()
    # 排他の中にいる間(=実行は 1 本だけ)は高パフォーマンス。終わりは finally で省電力へ戻す。
    # 呼び手が切れたときの見張りも child を止めて finally に来るので、戻し忘れない。
    powered = set_power_plan("performance")
    if not powered:
        log("電源プランを切り替えられない（今のまま回ります）")
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
        child = subprocess.Popen([str(venv_py), *req["argv"]], cwd=str(MAIN), env=env, stdin=subprocess.DEVNULL)
        qos = None
        # IROCA_HIGH_QOS=0 で外さない（比べるとき用）
        if os.name == "nt" and env.get("IROCA_HIGH_QOS", "1") != "0":
            try:
                qos = _HighQosSweeper(child.pid)
                threading.Thread(target=qos.run_until, args=(child,), daemon=True).start()
            except (OSError, AttributeError) as e:
                log(f"低 QoS を外せない（E コアに寄ったまま回ります）: {e}")
                qos = None
        heartbeat = float(req.get("heartbeat_s") or 0)
        if heartbeat > 0:
            # 呼び手は標準入力へ heartbeat 秒ごとに 1 行送る。途絶えたら呼び手(ssh)が切れたとみなし、置き去りの実行
            # (排他を持ったまま 20 分走り続ける)を止める。止めるのはここで起動したプロセスとその子だけ。
            # EOF は当てにしない: Windows の OpenSSH では、切れても子のプロセスが標準入力の書き口を持っていて EOF が届かなかった。
            seen = {"t": time.monotonic(), "eof": False}

            def _read() -> None:
                # sys.stdin.buffer.read() は使わない: 読んでいる間は BufferedReader のロックを持つので、終わりの後片付けで
                # 「could not acquire lock for <stdin>」になってプロセスが落ち、終了コードが壊れた(2026-11-11)
                try:
                    while os.read(0, 4096):
                        seen["t"] = time.monotonic()
                except OSError:
                    pass
                seen["eof"] = True

            def _watch() -> None:
                while child.poll() is None:
                    if seen["eof"] or time.monotonic() - seen["t"] > heartbeat * 6:
                        log("呼び手との接続が切れたので、実行を止めます")
                        subprocess.run(["taskkill", "/T", "/F", "/PID", str(child.pid)], capture_output=True)
                        return
                    time.sleep(2)

            threading.Thread(target=_read, daemon=True).start()
            threading.Thread(target=_watch, daemon=True).start()
        rc = child.wait()
        qos_note = f"・低 QoS を外したプロセス {qos.applied}（失敗 {qos.failed}）" if qos is not None else ""
        log(f"終了コード {rc}・{time.monotonic() - t1:.1f} 秒{qos_note}")
        if req.get("fetch"):
            _pack_fetch(run_dir, req["fetch"])
        return rc
    finally:
        if powered and not set_power_plan("saver"):
            log("電源プランを省電力へ戻せませんでした（手で戻してください）")
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
    "power": cmd_power,
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
