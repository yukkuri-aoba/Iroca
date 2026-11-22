"""テスト用の別 PC（リモート）側で動く受け手。`tools/remote_test.py` が実行のたびに写して呼ぶ。

標準ライブラリだけで書く（リモートの素の Python で動かすため）。置き場所は `<ホーム>/iroca-remote/agent.py` で、
そのフォルダを土台にする:

    iroca-remote/
      agent.py            このファイルの写し
      Iroca/              本体の写し（git。毎回スナップショットのコミットへ checkout -f し、HEAD だけ親＝呼び手の HEAD へ戻す）
      Iroca/dev_safe/     dev_safe の写し（同上）
      UnityManaged/       ハーネスのビルドに要る Unity の DLL（UnityManaged に渡す）
      inbox/<run_id>/     1 回の実行で受け取ったバンドル・データ・依頼と、持ち帰る結果
      lock                同時に 2 つの実行が作業ツリーを書き換えないための排他（スロット 0 の分）
      Iroca_s<N>/ lock_s<N>   スロット N（1〜）の作業ツリーと排他。スロットごとに独立して 1 実行ずつ（2026-11-20〜）
      power.lock  power_users/   電源プランを上げ下げする数え（スロットをまたぐので）
      queue/  queue.lock  予約の列（札 1 つが 1 ファイル。名前の順が並んだ順）と、列を見る間の排他
      running_s<N>.json   スロット N で今動いている実行の名札（status で見せる）

コマンド:
    info [--manifest]       状態を JSON で出す（--manifest でデータの一覧も。slots に全スロットの空き）
    slot-init <dirs...>     スロット N（--slot N、1〜）の作業ツリーをスロット 0 の写しとして作る（データも写す）
    recv <run_id> <name>    標準入力を inbox/<run_id>/<name> へ書く
    queue <run_id>          予約の列に並び、空いたスロットを割り当ててから実行する（2026-11-22〜。呼び手の既定）。
                            標準入力の 1 行目が予約の札（JSON: slots＝使ってよいスロットを好む順に・label）、
                            その後は生存の合図。割り当てたら標準出力へ `@@IROCA_SLOT <n>@@` を 1 行出し、呼び手がその
                            スロットに合わせて送る inbox/<run_id>/request.json を待って、run と同じように実行する
    run <run_id>            inbox/<run_id>/request.json に従って同期して実行する（--slot のスロットの排他を待つ。従来の入口）
    cat <run_id> <name>     inbox/<run_id>/<name> を標準出力へ書く
    cleanup <run_id>        inbox/<run_id> を消す
  どのコマンドにも `--slot N` を付けられる（既定 0）。スロットは作業ツリーと排他が別なので、実行が同居できる。
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
MAX_SLOTS = 4                     # 相手が持てるスロットの上限。呼び手が IROCA_REMOTE_SLOTS（既定 2）で使う数を決める
SLOT = 0
MAIN = BASE / "Iroca"
DEV_SAFE = MAIN / "dev_safe"
INBOX = BASE / "inbox"
UNITY_DIR = BASE / "UnityManaged"
# EditMode テスト（tools/editor_tests.py）用。Unity 本体と、この作業ツリーへリンクしたホストの Unity プロジェクト
UNITY_EDITOR_EXE = Path(r"C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe")
EDITOR_HOST = BASE / "hosts" / "Iroca_U2022_ifx"
LOCK_PATH = BASE / "lock"
POWER_LOCK = BASE / "power.lock"
POWER_USERS = BASE / "power_users"
QUEUE_DIR = BASE / "queue"
QUEUE_LOCK = BASE / "queue.lock"
TICKET_STALE_S = 30            # 札の更新がこれより古ければ、並んだ人はもういない（落ちた）とみなして列から外す
QUEUE_POLL_S = 1.0
SLOT_LINE = "@@IROCA_SLOT {}@@"
REQUIREMENTS = Path("dev_safe") / "Tests" / "requirements.txt"
SNAP_REF = "refs/remote-test/snap"
LAST_REF = "refs/remote-test/last"
DLL_REF_PREFIX = "refs/remote-test/dll/"   # tools/remote_test.py と合わせる
DLL_TOKEN_RE = re.compile(r"@@IROCA_DLL:([0-9a-f]{40})@@")
SYNC_FAILED = 97  # tools/remote_test.py と合わせる
_NAME_RE = re.compile(r"^[A-Za-z0-9_.-]{1,80}$")


def slot_paths(n: int) -> tuple[Path, Path]:
    """スロット n の（本体の作業ツリー, 排他のファイル）。0 は従来の置き場のまま。"""
    if n == 0:
        return BASE / "Iroca", BASE / "lock"
    return BASE / f"Iroca_s{n}", BASE / f"lock_s{n}"


def set_slot(n: int) -> None:
    """このプロセスの作業先をスロット n にする（1 コマンド 1 プロセスなので、グローバルを差し替えるだけでよい）。"""
    global SLOT, MAIN, DEV_SAFE, LOCK_PATH
    if not 0 <= n < MAX_SLOTS:
        raise SystemExit(f"スロット番号が範囲外: {n}（0〜{MAX_SLOTS - 1}）")
    SLOT = n
    MAIN, LOCK_PATH = slot_paths(n)
    DEV_SAFE = MAIN / "dev_safe"


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
    return data_manifest_of(DEV_SAFE, dirs)


def data_manifest_of(dev_safe: Path, dirs: list[str]) -> dict[str, list[int]]:
    if not (dev_safe / ".git").exists() or not dirs:
        return {}
    r = git(dev_safe, "-c", "core.quotepath=false", "ls-files", "-oi", "--exclude-standard", "-z", "--", *dirs)
    out: dict[str, list[int]] = {}
    for rel in r.stdout.decode("utf-8", "replace").split("\0"):
        if not rel:
            continue
        try:
            st = (dev_safe / rel).stat()
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
        # EditMode のホストはスロット 0 の作業ツリーへリンクしてあるので、スロット 0 でだけ回せる
        "unity_editor": SLOT == 0 and UNITY_EDITOR_EXE.is_file()
        and (EDITOR_HOST / "Assets" / "IrocaEditorTests").exists(),
        "desktop_sessions": _desktop_sessions(),
        "models": bool(models and models.is_dir() and any(models.glob("*.onnx"))),
        "repos": {"main": _rev(MAIN, LAST_REF), "dev_safe": _rev(DEV_SAFE, LAST_REF)},
        "busy": _lock_busy(),
        "slot": SLOT,
        "slot_ready": (MAIN / ".git").exists(),
        "slots": [{"slot": i, "ready": (slot_paths(i)[0] / ".git").exists(), "busy": _lock_busy(slot_paths(i)[1]),
                   "running": _running_label(i) if _lock_busy(slot_paths(i)[1]) else None}
                  for i in range(MAX_SLOTS)],
        "queue": queue_snapshot(),
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


def _power_lock():
    """電源プランの上げ下げを 1 つずつにする短い排他（スロットをまたぐ）。取った fd を返す。"""
    import msvcrt
    fd = os.open(POWER_LOCK, os.O_RDWR | os.O_CREAT)
    while True:
        try:
            msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
            return fd
        except OSError:
            time.sleep(0.2)


def _power_unlock(fd: int) -> None:
    import msvcrt
    try:
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
    except OSError:
        pass
    os.close(fd)


def power_acquire() -> bool:
    """この実行のために電源プランを「高パフォーマンス」にする。スロットが同居するので、使っている実行の数を印のファイルで数える。"""
    fd = _power_lock()
    try:
        POWER_USERS.mkdir(parents=True, exist_ok=True)
        (POWER_USERS / f"slot{SLOT}").write_text(str(os.getpid()))
        return set_power_plan("performance")
    finally:
        _power_unlock(fd)


def power_release() -> bool:
    """この実行の印を外す。ほかの実行がまだ動いていれば（印があり、その排他が取られていれば）省電力へは戻さない。"""
    fd = _power_lock()
    try:
        (POWER_USERS / f"slot{SLOT}").unlink(missing_ok=True)
        for i in range(MAX_SLOTS):
            mark = POWER_USERS / f"slot{i}"
            if mark.exists() and _lock_busy(slot_paths(i)[1]):
                return True
            mark.unlink(missing_ok=True)   # 排他が空いているのに残った印（強制終了の名残）
        return set_power_plan("saver")
    finally:
        _power_unlock(fd)


# ---- 排他 ----

def _lock_busy(path: Path | None = None) -> bool:
    import msvcrt
    try:
        fd = os.open(path or LOCK_PATH, os.O_RDWR | os.O_CREAT)
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


def _try_lock(path: Path) -> int | None:
    """排他を待たずに取る。取れたら fd、取れなければ None。"""
    import msvcrt
    try:
        fd = os.open(path, os.O_RDWR | os.O_CREAT)
    except OSError:
        return None
    try:
        msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
        return fd
    except OSError:
        os.close(fd)
        return None


def _unlock(fd: int) -> None:
    import msvcrt
    try:
        msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
    except OSError:
        pass
    os.close(fd)


# ---- 予約の列（2026-11-22〜）----
# 呼び手が実行の前にスロットを決め打ちすると、決めたスロットが空くまで待ち、別のスロットが先に空いても使えなかった。
# また排他は 5 秒ごとに取り直すだけで順番が無く、後から来た実行が先に回ることがあった。
# そこで相手の中に列を持つ。札は来た順に並び、空いたスロットを前の札から順に配る。前の札が使えるスロット（例: スロット 0
# 固定の視覚レビュー）が空いていないときは、後ろの札が使える別のスロットへ先に回る（追い越しは空いた枠を埋めるときだけ。
# 前の札が使えるスロットを後ろの札が取ることは無い）。

class _Caller:
    """呼び手からの標準入力を読む。queue では 1 行目が予約の札、その後は生存の合図（1 行ずつ）。

    EOF は当てにしない: Windows の OpenSSH では、切れても子のプロセスが標準入力の書き口を持っていて EOF が届かなかった。
    sys.stdin.buffer.read() は使わない: 読んでいる間は BufferedReader のロックを持つので、終わりの後片付けで
    「could not acquire lock for <stdin>」になってプロセスが落ち、終了コードが壊れた(2026-11-11)。"""

    def __init__(self, want_first_line: bool):
        self.t = time.monotonic()
        self.eof = False
        self.first: str | None = None
        self._want_first = want_first_line
        self._first_ready = threading.Event()
        if not want_first_line:
            self._first_ready.set()
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self) -> None:
        buf = b""
        try:
            while True:
                chunk = os.read(0, 4096)
                if not chunk:
                    break
                self.t = time.monotonic()
                if not self._first_ready.is_set():
                    buf += chunk
                    if b"\n" in buf:
                        self.first = buf.split(b"\n", 1)[0].decode("utf-8", "replace")
                        self._first_ready.set()
        except OSError:
            pass
        self.eof = True
        self._first_ready.set()

    def wait_first(self, timeout: float) -> str | None:
        self._first_ready.wait(timeout)
        return self.first

    def alive(self, heartbeat: float) -> bool:
        return not self.eof and (heartbeat <= 0 or time.monotonic() - self.t <= heartbeat * 6)


class _FileLock:
    """短い間だけ持つ排他（列を読んで書く間）。"""

    def __init__(self, path: Path):
        self.path = path
        self.fd: int | None = None

    def __enter__(self):
        while True:
            fd = _try_lock(self.path)
            if fd is not None:
                self.fd = fd
                return self
            time.sleep(0.05)

    def __exit__(self, *exc):
        _unlock(self.fd)
        self.fd = None


def _slot_free(n: int) -> bool:
    main, lock = slot_paths(n)
    return (main / ".git").exists() and not _lock_busy(lock)


def _read_tickets(now: float, me: Path | None = None) -> list[tuple[Path, dict]]:
    """列の札を並んだ順に返す。古い札（並んだ人が落ちた）は消す。列の排他の中で呼ぶ。"""
    out = []
    if not QUEUE_DIR.is_dir():
        return out
    for p in sorted(QUEUE_DIR.glob("*.json")):
        try:
            if p != me and now - p.stat().st_mtime > TICKET_STALE_S:
                p.unlink(missing_ok=True)
                continue
            out.append((p, json.loads(p.read_text(encoding="utf-8"))))
        except (OSError, ValueError):
            continue
    return out


def _try_assign(me: Path) -> tuple[int | None, int | None, int]:
    """自分の札にスロットを配れるか見る。列の排他の中で呼ぶ。(スロット, 排他の fd, 前に並んでいる数) を返す。"""
    free = [i for i in range(MAX_SLOTS) if _slot_free(i)]
    ahead = 0
    for p, t in _read_tickets(time.time(), me):
        pick = next((s for s in t.get("slots", []) if s in free), None)
        if p == me:
            if pick is None:
                return None, None, ahead
            fd = _try_lock(slot_paths(pick)[1])
            if fd is None:
                return None, None, ahead
            return pick, fd, ahead
        if pick is not None:
            free.remove(pick)     # 前の札の分。前の札は次の見回り（QUEUE_POLL_S 以内）で取る
        ahead += 1
    return None, None, ahead


def _running_path(n: int) -> Path:
    return BASE / f"running_s{n}.json"


def queue_snapshot() -> list[dict]:
    """info 用: 並んでいる札の一覧（並んだ順）。"""
    now = time.time()
    out = []
    for p, t in _read_tickets(now):
        out.append({"label": t.get("label", ""), "slots": t.get("slots", []),
                    "waited_s": int(now - t.get("since", now))})
    return out


def _running_label(n: int) -> dict | None:
    try:
        r = json.loads(_running_path(n).read_text(encoding="utf-8"))
        r["running_s"] = int(time.time() - r.get("since", time.time()))
        return r
    except (OSError, ValueError):
        return None


def cmd_queue(argv: list[str]) -> int:
    run_id = _safe_name(argv[0])
    caller = _Caller(want_first_line=True)
    first = caller.wait_first(60)
    if not first:
        log("予約の札が届かない")
        return SYNC_FAILED
    try:
        ticket = json.loads(first)
        slots = [int(s) for s in ticket["slots"] if 0 <= int(s) < MAX_SLOTS]
    except (ValueError, KeyError, TypeError):
        log(f"予約の札が読めない: {first[:200]!r}")
        return SYNC_FAILED
    if not slots:
        log("使ってよいスロットが無い")
        return SYNC_FAILED
    heartbeat = float(ticket.get("heartbeat_s") or 0)
    label = str(ticket.get("label", ""))[:200]
    QUEUE_DIR.mkdir(parents=True, exist_ok=True)
    since = time.time()
    me = QUEUE_DIR / f"{time.time_ns():020d}-{run_id}.json"
    tmp = me.with_suffix(".tmp")
    tmp.write_text(json.dumps({"run_id": run_id, "slots": slots, "label": label, "since": since},
                              ensure_ascii=False), encoding="utf-8")
    os.replace(tmp, me)
    fd = None
    started = time.monotonic()
    last_note = None
    try:
        while True:
            if not caller.alive(heartbeat):
                log("呼び手との接続が切れたので、予約を取り消します")
                return SYNC_FAILED
            with _FileLock(QUEUE_LOCK):
                try:
                    os.utime(me)
                except OSError:
                    pass
                slot, fd, ahead = _try_assign(me)
                if slot is not None:
                    me.unlink(missing_ok=True)
            if slot is not None:
                break
            waited = time.monotonic() - started
            if last_note is None or waited - last_note >= 60:
                log(f"予約の列で待っています（{int(waited)} 秒・前に {ahead} 件・使えるスロット "
                    f"{'・'.join(map(str, slots))} が空いていない）")
                last_note = waited
            time.sleep(QUEUE_POLL_S)
    finally:
        me.unlink(missing_ok=True)
    set_slot(slot)
    waited = time.monotonic() - started
    if waited >= 1:
        log(f"スロット {slot} が空きました（{waited:.0f} 秒待ち）")
    try:
        _running_path(slot).write_text(json.dumps({"run_id": run_id, "label": label, "since": time.time()},
                                                  ensure_ascii=False), encoding="utf-8")
        sys.stdout.write(SLOT_LINE.format(slot) + "\n")
        sys.stdout.flush()
        # 呼び手はこのスロットの状態を見て送る物を決め、最後に request.json を置く（recv は .part から置き換えるので、
        # 見えたときには書き終わっている）
        req_path = _run_dir(run_id) / "request.json"
        while not req_path.is_file():
            if not caller.alive(heartbeat):
                log("呼び手との接続が切れたので、スロットを返します")
                return SYNC_FAILED
            time.sleep(0.2)
        return _execute(run_id, caller)
    finally:
        _running_path(slot).unlink(missing_ok=True)
        _unlock(fd)


# ---- 同期 ----

def _sync_repo(repo: Path, spec: dict, run_dir: Path, git_config: dict[str, str]) -> None:
    commit = spec["commit"]
    if not (repo / ".git").exists():
        repo.mkdir(parents=True, exist_ok=True)
        git(repo, "init", "-q")
    # dev_safe には 260 文字を超えるパスがある（手元の Git for Windows も longpaths で読んでいる）
    for key, value in {"core.longpaths": "true", **git_config}.items():
        git(repo, "config", key, value)
    # 前回の試作の DLL のコミットは捨てる（要る分は呼び手が毎回送る。送らないのはスナップショットの祖先だけ）
    refs = git(repo, "for-each-ref", "--format=%(refname)", DLL_REF_PREFIX).stdout.decode().split()
    for ref in refs:
        git(repo, "update-ref", "-d", ref)
    bundle = spec.get("bundle")
    if bundle:
        path = run_dir / _safe_name(bundle)
        heads = git(repo, "bundle", "list-heads", str(path)).stdout.decode("utf-8", "replace")
        specs = []
        # スナップショットが前回と同じで、試作の DLL のコミットだけを送ってきたときは SNAP_REF が入っていない
        if re.search(rf"\s{re.escape(SNAP_REF)}$", heads, re.M):
            specs.append(f"+{SNAP_REF}:{LAST_REF}")
        if DLL_REF_PREFIX in heads:
            specs.append(f"+{DLL_REF_PREFIX}*:{DLL_REF_PREFIX}*")
        if specs:
            git(repo, "fetch", "-q", "--no-tags", str(path), *specs)
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
    # -m を付ける: 付けないと索引の stat の記録（変わっていないファイルを読み直さずに済ませる印）が全部消え、次の
    # checkout が dev_safe の 9000 余りのファイルを全部読み直していた（同期が毎回 30〜60 秒。2026-11-22）。
    # 木が 1 つの -m は、中身が同じ項目の stat の記録を残す。
    if _rev(repo, commit + "^2") is not None:
        git(repo, "read-tree", "-m", commit + "^2")


def _link_shared_cache() -> None:
    """スロット 1〜の dev_safe/_harness_cache を、スロット 0 のものへのジャンクションにする（実行キャッシュは中身の
    ハッシュで引くので、同じ入力の結果を共有できる。別々に持つと同居の 2 本目が毎回コールドで始まる）。"""
    if SLOT == 0:
        return
    src = slot_paths(0)[0] / "dev_safe" / "_harness_cache"
    dst = DEV_SAFE / "_harness_cache"
    if dst.exists() or dst.is_symlink() or os.path.lexists(dst):
        return
    src.mkdir(parents=True, exist_ok=True)
    dst.parent.mkdir(parents=True, exist_ok=True)
    r = subprocess.run(["cmd", "/c", "mklink", "/J", str(dst), str(src)], capture_output=True)
    if r.returncode != 0:
        log("実行キャッシュのジャンクションを作れない（このスロットだけ別のキャッシュで回る）: "
            + r.stderr.decode("mbcs", "replace").strip())


def cmd_slot_init(argv: list[str]) -> int:
    """slot-init <データのフォルダ...>（--slot N、1〜）: スロット 0 の写しとしてスロット N の作業ツリーを作る。

    リポジトリはスロット 0 から `git clone --local --no-checkout`（オブジェクトはハードリンクで速い）。
    refs/remote-test/* も写すので、呼び手の差分のバンドルはスロット 0 と同じ基準で送れる。作業ツリーの中身は次の run の
    checkout で入る。git に入らないデータ（dev_safe の無視されたファイル）は、スロット 0 から写す（Wi-Fi 越しに 14GB を
    送り直さないため。ハードリンクにしないのは、tar の展開が元のファイルへ書き込んでしまうから）。"""
    if SLOT == 0:
        raise SystemExit("slot-init はスロット 1 以上に使う（--slot N）")
    dirs = argv
    src_main = slot_paths(0)[0]
    if not (src_main / ".git").exists():
        raise SystemExit("スロット 0 がまだ無い（先に setup）")
    fd = _acquire_lock()
    try:
        for src_repo, dst_repo in ((src_main, MAIN), (src_main / "dev_safe", DEV_SAFE)):
            if (dst_repo / ".git").exists():
                continue
            dst_repo.parent.mkdir(parents=True, exist_ok=True)
            git(dst_repo.parent, "clone", "-q", "--local", "--no-checkout", str(src_repo), str(dst_repo))
            git(dst_repo, "fetch", "-q", "--no-tags", str(src_repo), "+refs/remote-test/*:refs/remote-test/*")
            # 無視の規則（.gitignore）が効くよう、前回の中身を checkout しておく。しないと、呼び手が数える「git に無いデータの
            # 一覧」が空に見え、14GB を送り直す
            if _rev(dst_repo, LAST_REF):
                git(dst_repo, "checkout", "-q", "-f", "--detach", LAST_REF)
            log(f"スロット {SLOT}: {dst_repo.name} を作った")
        _link_shared_cache()
        t0 = time.monotonic()
        copied = 0
        if dirs and (src_main / "dev_safe" / ".git").exists():
            old_dev = DEV_SAFE
            for rel in data_manifest_of(src_main / "dev_safe", dirs):
                src, dst = src_main / "dev_safe" / rel, old_dev / rel
                try:
                    st = src.stat()
                    if dst.exists() and dst.stat().st_size == st.st_size and int(dst.stat().st_mtime) == int(st.st_mtime):
                        continue
                    dst.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(src, dst)
                    copied += 1
                except OSError as e:
                    log(f"写せない: {rel}: {e}")
        log(f"スロット {SLOT}: データ {copied} 個を写した（{time.monotonic() - t0:.0f} 秒）")
    finally:
        import msvcrt
        try:
            msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        os.close(fd)
    return 0


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


def _build_dlls(venv_py: Path, env: dict, dlls: dict[str, str]) -> dict[str, str] | None:
    """試作の DLL を、送られたソースのコミットから作り直す（2026-11-20）。{コミット: 作った DLL のパス}。失敗なら None。

    呼び手は手元の DLL の sha256 も送ってくる。同じソースでもコンパイラーや参照する DLL の版が違えばバイト列は変わりうるので、
    違っても止めずに知らせる（測るのは同じソースの製品 C#）。"""
    built: dict[str, str] = {}
    for sha, want in dlls.items():
        t0 = time.monotonic()
        r = subprocess.run([str(venv_py), "tools/harness_build.py", "--source", f"rev:{sha}"],
                           cwd=str(MAIN), env=env, capture_output=True, stdin=subprocess.DEVNULL)
        out = r.stdout.decode("utf-8", "replace")
        doc = None
        # 出力の頭に dotnet の出力が混ざっても、来歴の JSON（"dll" で始まる）から読む
        start = out.find('{\n "dll"')
        start = start if start >= 0 else out.find("{")
        if r.returncode == 0 and start >= 0:
            try:
                doc, _ = json.JSONDecoder().raw_decode(out[start:])
            except ValueError:
                doc = None
        if not doc or not Path(doc.get("dll", "")).is_file():
            log(f"試作の DLL（{sha[:10]}）を作れない: " + (r.stderr.decode("utf-8", "replace") or out).strip()[-800:])
            return None
        same = doc.get("dll_sha256") == want
        log(f"試作の DLL {sha[:10]} → {doc['dll_sha256'][:16]}（{time.monotonic() - t0:.1f} 秒・"
            + ("手元とバイト一致" if same else "手元とバイト列が違う。同じソースから作った") + "）")
        built[sha] = doc["dll"]
    return built


def cmd_run(argv: list[str]) -> int:
    """従来の入口: --slot のスロットの排他を待ってから実行する。"""
    run_id = argv[0]
    if SLOT > 0 and not (MAIN / ".git").exists():
        log(f"スロット {SLOT} がまだ作られていない（slot-init が先）")
        return SYNC_FAILED
    caller = _Caller(want_first_line=False)
    fd = _acquire_lock()
    try:
        return _execute(run_id, caller)
    finally:
        _unlock(fd)


def _execute(run_id: str, caller: _Caller) -> int:
    """スロットの排他を持った状態で、request.json に従って同期して実行する。"""
    run_dir = _run_dir(run_id)
    req = json.loads((run_dir / "request.json").read_text(encoding="utf-8"))
    # 排他の中にいる間は高パフォーマンス。終わりは finally で省電力へ戻す（使っている実行の数を数えて、最後の 1 本のとき）。
    # 呼び手が切れたときの見張りも child を止めて finally に来るので、戻し忘れない。
    powered = power_acquire()
    if not powered:
        log("電源プランを切り替えられない（今のまま回ります）")
    try:
        t0 = time.monotonic()
        try:
            git_config = req.get("git_config", {})
            steps = []
            ts = time.monotonic()

            def _mark(name: str) -> None:
                nonlocal ts
                now = time.monotonic()
                steps.append(f"{name} {now - ts:.1f}")
                ts = now

            _sync_repo(MAIN, req["repos"]["main"], run_dir, git_config.get("main", {}))
            _mark("本体")
            _sync_repo(DEV_SAFE, req["repos"]["dev_safe"], run_dir, git_config.get("dev_safe", {}))
            _mark("dev_safe")
            for b in ("main.bundle", "dev_safe.bundle"):
                (run_dir / b).unlink(missing_ok=True)
            _apply_data(run_dir, req)
            _link_shared_cache()
            _mark("データ")
            venv_py = _ensure_venv()
            _mark("venv")
        except (SystemExit, OSError, subprocess.CalledProcessError, tarfile.TarError) as e:
            # まだ何も実行していない。呼び手はこの終了コードを見て手元でやり直せる
            log(f"同期に失敗: {e}")
            return SYNC_FAILED
        env = os.environ.copy()
        env.update(req.get("env", {}))
        env.setdefault("PYTHONUTF8", "1")
        env.setdefault("PYTHONIOENCODING", "utf-8")
        env["IROCA_REMOTE_TEST"] = "1"
        if "UnityManaged" not in req.get("env", {}) and (UNITY_DIR / "UnityEngine").is_dir():
            env["UnityManaged"] = str(UNITY_DIR)
        argv = list(req["argv"])
        if req.get("dlls"):
            built = _build_dlls(venv_py, env, req["dlls"])
            if built is None:
                return SYNC_FAILED
            argv = [DLL_TOKEN_RE.sub(lambda m: built[m.group(1)], a) for a in argv]
            for k in req.get("env", {}):
                env[k] = DLL_TOKEN_RE.sub(lambda m: built[m.group(1)], env[k])
        log(f"同期 {time.monotonic() - t0:.1f} 秒（{'・'.join(steps)}）。実行: python {' '.join(argv)}")
        t1 = time.monotonic()
        child = subprocess.Popen([str(venv_py), *argv], cwd=str(MAIN), env=env, stdin=subprocess.DEVNULL)
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
            def _watch() -> None:
                while child.poll() is None:
                    if not caller.alive(heartbeat):
                        log("呼び手との接続が切れたので、実行を止めます")
                        subprocess.run(["taskkill", "/T", "/F", "/PID", str(child.pid)], capture_output=True)
                        return
                    time.sleep(2)

            threading.Thread(target=_watch, daemon=True).start()
        rc = child.wait()
        qos_note = f"・低 QoS を外したプロセス {qos.applied}（失敗 {qos.failed}）" if qos is not None else ""
        log(f"終了コード {rc}・{time.monotonic() - t1:.1f} 秒{qos_note}")
        if req.get("fetch"):
            _pack_fetch(run_dir, req["fetch"])
        return rc
    finally:
        if not power_release() and powered:
            log("電源プランを省電力へ戻せませんでした（手で戻してください）")


COMMANDS = {
    "info": cmd_info,
    "recv": cmd_recv,
    "queue": cmd_queue,
    "run": cmd_run,
    "cat": cmd_cat,
    "cleanup": cmd_cleanup,
    "extract-to": cmd_extract_to,
    "power": cmd_power,
    "slot-init": cmd_slot_init,
}


def main() -> int:
    if len(sys.argv) < 2 or sys.argv[1] not in COMMANDS:
        print(__doc__, file=sys.stderr)
        return 2
    BASE.mkdir(parents=True, exist_ok=True)
    # どのコマンドにも付けられる `--slot N`（既定 0）を先に取り除いて、作業先を決める
    args = sys.argv[2:]
    if "--slot" in args:
        i = args.index("--slot")
        try:
            n = int(args[i + 1])
        except (IndexError, ValueError):
            raise SystemExit("--slot の後ろに数字が要る")
        del args[i:i + 2]
        set_slot(n)
    sys.argv[2:] = args
    # 呼び手はバイトをそのまま流すので、こちらの文字は UTF-8 にそろえる（相手の既定は cp932）
    # （データのファイル名には cp932 に無い文字もある）
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    return COMMANDS[sys.argv[1]](sys.argv[2:])


if __name__ == "__main__":
    sys.exit(main())
