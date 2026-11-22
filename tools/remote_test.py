"""テストを LAN の別 PC（テスト用の PC）で回す。つながらないときは手元で回す。

    python tools/remote_test.py -- -m pytest dev_safe/Tests/regression/ -q
    python tools/remote_test.py status
    python tools/remote_test.py setup

`--` の後ろはそのまま Python に渡す引数（手元なら `.venv` の python、リモートなら向こうの `.venv` の python）。
リモートは作業ツリーの今の中身（未コミットの変更・追跡していない新しいファイルを含む）を測る。
手元の git の索引・ブランチには触らない（索引の写しでスナップショットのコミットを作り、`refs/remote-test/*` にだけ書く）。

相手の決め方:
- ssh の Host 名 `iroca-remote`（`IROCA_REMOTE_HOST` か `--host` で変更）。IP・ユーザー名・鍵は `~/.ssh/config` に書く。
  公開リポにテスト用の PC の名前やアドレスを書かないため。
- 既定（auto）は、ssh のポートに 1.5 秒以内につながり、鍵で入れて、相手の準備が済んでいればリモート。
  どれかが欠ければ理由を 1 行出して手元で回す。`--remote` はリモート必須（だめなら失敗）、`--local` は常に手元。

送るもの:
- 本体と dev_safe: スナップショットのコミットを git のバンドルで（前回から増えた分だけ）。
- dev_safe の無視されたデータ（DATA_DIRS。テクスチャ・PSD・SAM の埋め込みなど git に入らない入力）:
  大きさと更新時刻が違うものだけ tar で。
- 環境変数: `IROCA_*`・`VACC_*`（手元の絶対パスを指すものと `IROCA_REMOTE_*` は除く）と `--env K=V`。
- 引数・環境変数の中の手元の `.dll`（試作の --dll など）: 来歴のソースのコミットを送り、相手で作り直したパスに置き換える
  （下の rewrite_dlls）。

持ち帰るもの: `--fetch <本体からの相対パス>`（複数可）で、実行後のファイル・フォルダを手元の同じ場所へ上書きする。

リモートの準備は `scripts/remote-test/Enable-RemoteTestHost.ps1`（相手の PC で 1 回、管理者で）と、
こちらの `python tools/remote_test.py setup`（Unity の DLL・SAM のモデルを送って .venv を作る）。
詳しくは docs/testing-architecture.md の「テスト用の別 PC で回す」。
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tarfile
import tempfile
import threading
import time
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
DEV_SAFE = REPO / "dev_safe"
AGENT_SRC = Path(__file__).resolve().parent / "remote_test_agent.py"
AGENT_REMOTE = "iroca-remote/agent.py"
DEFAULT_HOST = "iroca-remote"
# git では届かない（無視されている）が、テストが入力として読むデータの置き場（dev_safe からの相対）
DATA_DIRS = ("texture_sample", "Tests/Baselines")
SYNC_FAILED = 97  # remote_test_agent.py と合わせる（相手で同期に失敗し、何も実行していない）
HEARTBEAT_S = 10  # 実行中に相手へ送る生存の合図の間隔。6 回分途絶えると相手は実行を止める
SNAP_REF = "refs/remote-test/snap"
SENT_REF = "refs/remote-test/sent"
# 試作の DLL（--dll・IROCA_HARNESS_DLL）を相手で作り直すためのコミット（remote_test_agent.py と合わせる）
DLL_REF_PREFIX = "refs/remote-test/dll/"
DLL_TOKEN = "@@IROCA_DLL:{}@@"
_ABS_PATH_RE = re.compile(r"^[A-Za-z]:[\\/]|^\\\\")
_FIXED_ID = {
    "GIT_AUTHOR_NAME": "remote-test", "GIT_AUTHOR_EMAIL": "remote-test@localhost",
    "GIT_COMMITTER_NAME": "remote-test", "GIT_COMMITTER_EMAIL": "remote-test@localhost",
    # 同じ中身なら同じコミットになるよう日時を固定する（変わっていなければ送らずに済む）
    "GIT_AUTHOR_DATE": "2000-01-01T00:00:00Z", "GIT_COMMITTER_DATE": "2000-01-01T00:00:00Z",
}


def log(msg: str) -> None:
    print(f"[remote-test] {msg}", file=sys.stderr, flush=True)


class Unavailable(Exception):
    """リモートを使えない理由（auto なら手元へ切り替える）。"""


# ---- 経路 ----

class Transport:
    """ssh で相手の agent.py を呼ぶ。"""

    def __init__(self, host: str, python: str):
        self.host = host
        self.python = python
        self.ssh = ["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
                    "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=6", host]

    def endpoint(self) -> tuple[str, int]:
        # ssh -G は UTF-8 で出す（日本語のユーザー名を cp932 で読むと落ちる）
        r = subprocess.run(["ssh", "-G", self.host], capture_output=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            # 例: ~/.ssh/config に余計なアカウントの権限があると ssh は設定ごと読まない
            lines = (r.stderr or "").strip().splitlines()
            raise Unavailable("ssh の設定を読めない: " + (lines[-1] if lines else f"終了コード {r.returncode}"))
        hostname, port = self.host, 22
        for line in r.stdout.splitlines():
            key, _, value = line.partition(" ")
            if key == "hostname":
                hostname = value.strip()
            elif key == "port":
                port = int(value.strip())
        return hostname, port

    def agent_cmd(self, *args: str) -> list[str]:
        # 引数は英数字と決まった記号だけにする（相手の既定シェルが cmd でも PowerShell でも同じに解釈される）
        for a in args:
            if not re.match(r"^[A-Za-z0-9_./=-]+$", a):
                raise ValueError(f"リモートへ渡せない引数: {a!r}")
        return [*self.ssh, *self.python.split(), AGENT_REMOTE.replace("/", "\\"), *args]

    def run_agent(self, *args: str, stdin=None, capture: bool = True, timeout: float | None = None):
        return subprocess.run(self.agent_cmd(*args), stdin=stdin,
                              capture_output=capture, timeout=timeout)

    def popen_agent(self, *args: str, **kw) -> subprocess.Popen:
        return subprocess.Popen(self.agent_cmd(*args), **kw)

    def upload_agent(self) -> None:
        subprocess.run([*self.ssh, "mkdir", "iroca-remote"], capture_output=True)
        r = subprocess.run(["scp", "-q", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
                            str(AGENT_SRC), f"{self.host}:{AGENT_REMOTE}"], capture_output=True)
        if r.returncode != 0:
            raise Unavailable("agent.py を送れない: " + r.stderr.decode(errors="replace").strip())


class LoopbackTransport(Transport):
    """検証用: ssh を使わず、手元の別フォルダを「リモート」に見立てる（IROCA_REMOTE_LOOPBACK=<フォルダ>）。"""

    def __init__(self, home: Path):
        super().__init__("loopback", sys.executable)
        self.home = home

    def endpoint(self) -> tuple[str, int]:
        return "", 0

    def agent_cmd(self, *args: str) -> list[str]:
        return [self.python, str(self.home / AGENT_REMOTE), *args]

    def upload_agent(self) -> None:
        dst = self.home / AGENT_REMOTE
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(AGENT_SRC, dst)


def make_transport(host: str | None) -> Transport:
    loop = os.environ.get("IROCA_REMOTE_LOOPBACK")
    if loop:
        return LoopbackTransport(Path(loop))
    return Transport(host or os.environ.get("IROCA_REMOTE_HOST") or DEFAULT_HOST,
                     os.environ.get("IROCA_REMOTE_PYTHON", "py -3.14"))


def reachable(t: Transport, timeout: float = 1.5) -> bool:
    hostname, port = t.endpoint()
    if not hostname:
        return True
    try:
        with socket.create_connection((hostname, port), timeout=timeout):
            return True
    except OSError:
        return False


def remote_info(t: Transport, manifest: bool = False, slot: int = 0) -> dict:
    args = ["info"] + (["--manifest", *DATA_DIRS] if manifest else []) + (["--slot", str(slot)] if slot else [])
    want = hashlib.sha256(AGENT_SRC.read_bytes()).hexdigest()
    for attempt in (0, 1):
        try:
            r = t.run_agent(*args, timeout=600)
        except subprocess.TimeoutExpired:
            raise Unavailable("相手の応答が 10 分無い")
        info = None
        if r.returncode == 0:
            try:
                info = json.loads(r.stdout.decode("utf-8"))
            except ValueError:
                info = None
        if info is not None and info.get("agent") == want:
            return info
        if attempt == 0:
            err = r.stderr.decode(errors="replace").strip()
            if r.returncode == 255:
                raise Unavailable("ssh で入れない: " + (err.splitlines()[-1] if err else "不明"))
            t.upload_agent()
    raise Unavailable("相手の agent.py が動かない: " + r.stderr.decode(errors="replace").strip()[-400:])


def check_ready(info: dict, need_editor: bool = False, need_desktop: bool = False) -> list[str]:
    missing = []
    if need_editor and not info.get("unity_editor"):
        missing.append("Unity 本体か EditMode のホスト（docs/testing-architecture.md の「EditMode もテスト用 PC で」）")
    if need_desktop and not info.get("desktop_sessions"):
        missing.append("サインインしたままのデスクトップ（画面の要る Unity の台本を動かす所）")
    if not info.get("git"):
        missing.append("git")
    if not info.get("dotnet"):
        missing.append(".NET SDK")
    if not info.get("unity"):
        missing.append("Unity の DLL（setup で送る）")
    if info.get("smart_app_control") == 1:
        missing.append("Smart App Control が有効で、ビルドしたハーネスの DLL を読めない（相手の PC で切る）")
    return missing


# ---- 手元のスナップショット ----

def git(repo: Path, *args: str, env: dict | None = None, check: bool = True) -> str:
    r = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, env=env)
    if check and r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout.decode("utf-8", "replace").strip()


def snapshot(repo: Path, tmpdir: Path) -> str:
    """作業ツリーの今の中身（無視されたファイルを除く）を、HEAD を親にしたコミットにする。"""
    index = Path(git(repo, "rev-parse", "--path-format=absolute", "--git-path", "index"))
    tmp_index = tmpdir / f"index-{repo.name}"
    if index.is_file():
        shutil.copyfile(index, tmp_index)
    env = {**os.environ, "GIT_INDEX_FILE": str(tmp_index), **_FIXED_ID}
    head = git(repo, "rev-parse", "-q", "--verify", "HEAD^{commit}", check=False)
    parent = ["-p", head] if head else []
    # 索引の中身（ステージした分）も送り、相手の索引をこれにそろえる（2026-11-19。visual_review.py compare --index を
    # 相手で回して手元で approve するため）。2 番目の親にして、同じバンドルで届くようにする。
    index_tree = git(repo, "write-tree", env=env) if index.is_file() else None
    index_commit = (git(repo, "commit-tree", index_tree, *parent, "-m", "remote-test index", env=env)
                    if index_tree else None)
    git(repo, "add", "-A", env=env)
    tree = git(repo, "write-tree", env=env)
    parents = parent + (["-p", index_commit] if index_commit and head else [])
    commit = git(repo, "commit-tree", tree, *parents, "-m", "remote-test snapshot", env=env)
    git(repo, "update-ref", SNAP_REF, commit)
    return commit


def make_bundle(repo: Path, commit: str, remote_has: str | None, out: Path,
                extra_refs: list[str] | tuple = ()) -> Path | None:
    if remote_has == commit and not extra_refs:
        return None
    excl = []
    if remote_has and subprocess.run(["git", "-C", str(repo), "cat-file", "-e", remote_has + "^{commit}"],
                                     capture_output=True).returncode == 0:
        excl = ["^" + remote_has]
    try:
        git(repo, "bundle", "create", "-q", str(out), SNAP_REF, *extra_refs, *excl)
    except RuntimeError as e:
        # 足したコミットも相手にもうある（送るものが無い）
        if "empty bundle" in str(e):
            return None
        raise
    return out


# ---- 試作の DLL を相手で作り直す（2026-11-20）----
# 試作は変種のコミットから作った DLL を --dll <パス>（や name=<パス>・IROCA_HARNESS_DLL）で渡す。手元のパスは相手に無いので、
# DLL の来歴（同じフォルダの provenance.json。tools/harness_build.py が書く）から元のソースのコミットを決めて一緒に送り、
# 引数のパスを印 @@IROCA_DLL:<コミット>@@ に置き換える。相手は harness_build.py --source rev:<コミット> で作り直して
# 印を自分のパスへ戻す。作業ツリーや索引から作った DLL は、来歴の blob がそろっていれば HEAD の木に重ねたコミットにする。

def _local_dll(s: str) -> Path | None:
    if not s.lower().endswith(".dll"):
        return None
    p = Path(s)
    for c in ([p] if p.is_absolute() else [Path.cwd() / p, REPO / p]):
        if c.is_file():
            return c.resolve()
    return None


def _dll_commit(dll: Path) -> str:
    try:
        prov = json.loads((dll.parent / "provenance.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        raise Unavailable(f"{dll} の来歴（provenance.json）が無く、相手で同じ DLL を作れない")
    source = str(prov.get("source") or "")
    if source.startswith("rev:"):
        sha = git(REPO, "rev-parse", "-q", "--verify", source[4:] + "^{commit}", check=False)
        if sha:
            return sha
    sources = prov.get("sources") or {}
    if not sources:
        raise Unavailable(f"{dll} の来歴にソースの一覧が無く、相手で作れない")
    r = subprocess.run(["git", "-C", str(REPO), "cat-file", "--batch-check"],
                       input="\n".join(sources.values()).encode(), capture_output=True)
    if r.returncode != 0 or b"missing" in r.stdout:
        raise Unavailable(f"{dll} のソースの一部が git に無く（作業ツリーから作った DLL）、相手で作れない")
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    try:
        from harness_build import EXPORT_DIRS
    finally:
        sys.path.pop(0)
    with tempfile.TemporaryDirectory(prefix="iroca-remote-dll-") as td:
        env = {**os.environ, "GIT_INDEX_FILE": str(Path(td) / "index"), **_FIXED_ID}
        git(REPO, "read-tree", "HEAD", env=env)
        git(REPO, "rm", "-r", "-q", "--cached", "--ignore-unmatch", "--", *EXPORT_DIRS, env=env)
        listing = "".join(f"100644 {blob}\t{rel}\n" for rel, blob in sources.items())
        r = subprocess.run(["git", "-C", str(REPO), "update-index", "--index-info"],
                           input=listing.encode("utf-8"), env=env, capture_output=True)
        if r.returncode != 0:
            raise Unavailable(f"{dll} のソースを木にできない: {r.stderr.decode(errors='replace').strip()}")
        tree = git(REPO, "write-tree", env=env)
        return git(REPO, "commit-tree", tree, "-m", f"remote-test dll {source}", env=env)


def rewrite_dlls(argv: list[str], env: dict[str, str]) -> tuple[list[str], dict[str, str], dict[str, str]]:
    """引数と環境変数の中の手元の DLL のパスを印に置き換える。戻り値の 3 つ目は {コミット: 手元の DLL の sha256}。"""
    commits: dict[str, str] = {}

    def sub(tok: str) -> str:
        cuts = [0] + ([tok.index("=") + 1] if "=" in tok else [])
        for cut in cuts:
            dll = _local_dll(tok[cut:])
            if dll is not None:
                sha = _dll_commit(dll)
                commits[sha] = hashlib.sha256(dll.read_bytes()).hexdigest()
                return tok[:cut] + DLL_TOKEN.format(sha)
        return tok

    new_argv = [sub(a) for a in argv]
    new_env = {k: sub(v) for k, v in env.items()}
    return new_argv, new_env, commits


def local_data_manifest() -> dict[str, tuple[int, int]]:
    out = subprocess.run(["git", "-C", str(DEV_SAFE), "-c", "core.quotepath=false", "ls-files", "-oi",
                          "--exclude-standard", "-z", "--", *DATA_DIRS], capture_output=True).stdout
    result = {}
    for rel in out.decode("utf-8", "replace").split("\0"):
        if not rel:
            continue
        try:
            st = (DEV_SAFE / rel).stat()
        except OSError:
            continue
        result[rel] = (st.st_size, int(st.st_mtime))
    return result


def forwarded_env(extra: list[str]) -> dict[str, str]:
    env = {}
    for k, v in os.environ.items():
        ku = k.upper()
        if not (ku.startswith("IROCA_") or ku.startswith("VACC_")) or ku.startswith("IROCA_REMOTE"):
            continue
        if _ABS_PATH_RE.match(v) and _local_dll(v) is None:   # DLL は rewrite_dlls が相手で作り直す
            continue
        env[k] = v
    for kv in extra:
        k, sep, v = kv.partition("=")
        if not sep:
            raise SystemExit(f"--env は K=V の形で: {kv}")
        env[k] = v
    return env


def git_config_to_mirror(repo: Path) -> dict[str, str]:
    cfg = {}
    # core.hooksPath はテスト（test_hooks_configured）が見る。相手でコミットはしないのでフックは動かない
    for key in ("core.autocrlf", "core.eol", "core.symlinks", "core.hooksPath"):
        v = git(repo, "config", "--get", key, check=False)
        if v:
            cfg[key] = v
    return cfg


# ---- 送る ----

def _upload(t: Transport, run_id: str, name: str, src: Path | bytes) -> None:
    if isinstance(src, bytes):
        p = t.popen_agent("recv", run_id, name, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
        _, err = p.communicate(src)
    else:
        with open(src, "rb") as f:
            p = t.popen_agent("recv", run_id, name, stdin=f, stderr=subprocess.PIPE)
            _, err = p.communicate()
    if p.returncode != 0:
        raise RuntimeError(f"{name} を送れない: {err.decode(errors='replace').strip()}")


def _upload_tar(t: Transport, run_id: str, name: str, root: Path, rels: list[str]) -> None:
    p = t.popen_agent("recv", run_id, name, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    sent = 0
    next_note = 1 << 30
    started = time.monotonic()
    try:
        with tarfile.open(fileobj=p.stdin, mode="w|") as tf:
            for rel in rels:
                path = root / rel
                tf.add(path, arcname=rel, recursive=False)
                sent += path.stat().st_size
                if sent >= next_note:
                    rate = sent / max(time.monotonic() - started, 1e-3) / 1e6
                    log(f"  データ {sent / 1e9:.1f} GB 送信（{rate:.0f} MB/s）")
                    next_note += 1 << 30
        p.stdin.close()
    except BrokenPipeError:
        pass
    err = p.stderr.read()
    p.wait()
    if p.returncode != 0:
        raise RuntimeError(f"{name} を送れない: {err.decode(errors='replace').strip()}")


def _upload_dir_tar(t: Transport, which: str, src: Path) -> None:
    p = t.popen_agent("extract-to", which, stdin=subprocess.PIPE, stderr=subprocess.PIPE)
    with tarfile.open(fileobj=p.stdin, mode="w|") as tf:
        for f in sorted(src.rglob("*")):
            if f.is_file():
                tf.add(f, arcname=f.relative_to(src.parent if which == "unity" else src).as_posix())
    p.stdin.close()
    err = p.stderr.read()
    p.wait()
    sys.stderr.write(err.decode("utf-8", "replace"))
    if p.returncode != 0:
        raise RuntimeError(f"{which} を送れない")


def _label(argv: list[str]) -> str:
    """status に出す実行の名札（引数を短く）。"""
    s = " ".join(Path(a).name if i == 0 and not a.startswith("-") else a for i, a in enumerate(argv))
    return s if len(s) <= 120 else s[:117] + "..."


def run_remote(t: Transport, argv: list[str], fetch: list[str], env_extra: list[str], info: dict) -> int:
    """相手の予約の列に並び、割り当てられたスロットに合わせて送ってから実行する（2026-11-22〜）。

    使ってよいスロットは info["candidates"]（resolve が決める。好む順）。並んでいる間も、送る間も、実行中も
    HEARTBEAT_S 秒ごとに 1 行送る。こちらが落ちる・止められると途絶え、相手は予約を取り消すか実行を止める。"""
    run_id = time.strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:6]
    tmpdir = Path(tempfile.mkdtemp(prefix="iroca-remote-test-"))
    p = None
    try:
        slots = info.get("candidates") or [info.get("slot") or 0]
        ticket = {"slots": slots, "label": _label(argv), "heartbeat_s": HEARTBEAT_S}
        p = t.popen_agent("queue", run_id, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
        try:
            p.stdin.write(json.dumps(ticket, ensure_ascii=False).encode("utf-8") + b"\n")
            p.stdin.flush()
        except OSError as e:
            raise Unavailable(f"予約を送れない: {e}") from e

        def _beat() -> None:
            while p.poll() is None:
                try:
                    p.stdin.write(b".\n")
                    p.stdin.flush()
                except OSError:
                    return
                time.sleep(HEARTBEAT_S)

        threading.Thread(target=_beat, daemon=True).start()
        busy = {int(s["slot"]) for s in info.get("slots") or [] if s.get("busy")}
        if all(n in busy for n in slots):
            log(f"使えるスロット {'・'.join(map(str, slots))} が使用中なので、予約の列に並びます（空いた所から順に回ります）")
        slot = None
        try:
            for raw in iter(p.stdout.readline, b""):
                m = re.match(rb"@@IROCA_SLOT (\d+)@@", raw.strip())
                if m:
                    slot = int(m.group(1))
                    break
                sys.stdout.buffer.write(raw)
                sys.stdout.buffer.flush()
        except KeyboardInterrupt:
            p.terminate()
            raise
        if slot is None:
            rc = p.wait()
            raise Unavailable(f"相手で予約できなかった（終了コード {rc}）")
        try:
            info = remote_info(t, manifest=True, slot=slot)
            info["slot"] = slot
            _prepare(t, run_id, argv, fetch, env_extra, info, tmpdir)
        except (RuntimeError, OSError, subprocess.SubprocessError, Unavailable) as e:
            # まだ何も実行していないので、auto なら手元でやり直してよい（相手は合図が途絶えてスロットを返す）
            p.terminate()
            raise Unavailable(f"送る途中で失敗: {e}") from e
        # 出力に出る相手の本体の絶対パスは、手元の本体のパスに読み替えて出す（2026-11-19。visual_review.py compare が
        # 出す「確認するパネル」の一覧を、持ち帰った手元のファイルとしてそのまま Read できるように）
        remote_main = info.get("main")
        rewrite = [(remote_main, str(REPO)), (remote_main.replace("\\", "/"), REPO.as_posix())] if remote_main else []

        def _pump() -> None:
            out = sys.stdout.buffer
            for raw in iter(p.stdout.readline, b""):
                line = raw.decode("utf-8", errors="replace")
                for a, b in rewrite:
                    line = line.replace(a, b)
                out.write(line.encode("utf-8"))
                out.flush()

        pump = threading.Thread(target=_pump, daemon=True)
        pump.start()
        try:
            rc = p.wait()
            pump.join(timeout=30)
        except KeyboardInterrupt:
            p.terminate()
            raise
        if rc == SYNC_FAILED:
            raise Unavailable("相手で作業ツリーをそろえられなかった（テストは走っていない）")
        if rc == 255 and not isinstance(t, LoopbackTransport):
            log("ssh の接続が切れた可能性があります（終了コード 255）。結果は信用せず回し直してください")
        if fetch:
            # いったん一時フォルダへ展開し、持ち帰ったパスごとに手元を丸ごと入れ替える(前の実行の残りが混ざらないように。
            # 2026-11-11 に、ワーカー数が違う実行のプロファイルへ前の実行の gw3 が残って混ざった)
            pf = t.popen_agent("cat", run_id, "fetch.tar", stdout=subprocess.PIPE)
            got = tmpdir / "fetched"
            n = 0
            try:
                with tarfile.open(fileobj=pf.stdout, mode="r|") as tf:
                    for member in tf:
                        tf.extract(member, got, filter="data")
                        n += 1
            except tarfile.ReadError:
                pass
            pf.wait()
            for rel in fetch:
                src = got / rel
                dst = REPO / rel
                if not src.exists():
                    continue
                if dst.is_dir():
                    shutil.rmtree(dst)
                elif dst.exists():
                    dst.unlink()
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(src), str(dst))
            log(f"持ち帰り {n} 個")
        return rc
    finally:
        if p is not None and p.poll() is None:
            p.terminate()
        try:
            t.run_agent("cleanup", run_id, timeout=120)
        except Exception:
            pass
        shutil.rmtree(tmpdir, ignore_errors=True)


def _prepare(t: Transport, run_id: str, argv: list[str], fetch: list[str], env_extra: list[str],
             info: dict, tmpdir: Path) -> None:
    t0 = time.monotonic()
    argv, env, dlls = rewrite_dlls(argv, forwarded_env(env_extra))
    for ref in git(REPO, "for-each-ref", "--format=%(refname)", DLL_REF_PREFIX).splitlines():
        git(REPO, "update-ref", "-d", ref)
    dll_refs = []
    for sha in dlls:
        git(REPO, "update-ref", DLL_REF_PREFIX + sha, sha)
        dll_refs.append(DLL_REF_PREFIX + sha)
    if dlls:
        log(f"試作の DLL {len(dlls)} 個は、元のソースのコミットを送って相手で作り直します")
    repos = {}
    for key, repo in (("main", REPO), ("dev_safe", DEV_SAFE)):
        commit = snapshot(repo, tmpdir)
        bundle = make_bundle(repo, commit, info["repos"].get(key), tmpdir / f"{key}.bundle",
                             dll_refs if key == "main" else ())
        if bundle is not None:
            size = bundle.stat().st_size
            log(f"{key}: {commit[:10]} を送ります（{size / 1e6:.1f} MB）")
            _upload(t, run_id, bundle.name, bundle)
            bundle.unlink()
        repos[key] = {"commit": commit, "bundle": bundle.name if bundle else None}

    local = local_data_manifest()
    remote = {k: tuple(v) for k, v in info.get("manifest", {}).items()}
    send = sorted(rel for rel, sig in local.items() if remote.get(rel) != sig)
    delete = sorted(rel for rel in remote if rel not in local)
    if send:
        total = sum(local[r][0] for r in send)
        log(f"データ {len(send)} 個（{total / 1e9:.2f} GB）を送ります")
        _upload_tar(t, run_id, "data.tar", DEV_SAFE, send)
    request = {
        "repos": repos,
        "data_tar": "data.tar" if send else None,
        "data_delete": delete,
        "env": env,
        "argv": argv,
        "dlls": dlls,
        "fetch": fetch,
        "git_config": {"main": git_config_to_mirror(REPO), "dev_safe": git_config_to_mirror(DEV_SAFE)},
        "heartbeat_s": HEARTBEAT_S,
    }
    _upload(t, run_id, "request.json", json.dumps(request, ensure_ascii=False).encode("utf-8"))
    log(f"準備 {time.monotonic() - t0:.1f} 秒。{info.get('computer') or 'リモート'} で実行します")
    # 成功したら「相手に届いた」印を進める（次回はここからの差分だけ送る）
    for key, repo in (("main", REPO), ("dev_safe", DEV_SAFE)):
        git(repo, "update-ref", SENT_REF, repos[key]["commit"])


def run_local(argv: list[str]) -> int:
    py = REPO / ".venv" / "Scripts" / "python.exe"
    exe = str(py) if py.is_file() else sys.executable
    return subprocess.run([exe, *argv], cwd=str(REPO)).returncode


def needs_editor(argv: list[str]) -> bool:
    """Unity 本体が要る実行か（EditMode テストの入口 tools/editor_tests.py）。"""
    return bool(argv) and Path(argv[0]).name == "editor_tests.py"


def needs_desktop(argv: list[str]) -> bool:
    """サインインしたデスクトップが要る実行か（画面ありの Unity を起動する台本。editor_tests.GUI_SWITCHES と同じ）。"""
    return needs_editor(argv) and any(a.lower() in ("-undoreload", "-sdkbuild", "-sdkupload") for a in argv[1:])


# ---- スロット（同居。2026-11-20〜）----
# 相手の作業ツリーは 1 つだと実行が 1 本ずつになる。相手にスロット（作業ツリーと排他の組）を持たせ、別のスロットなら
# 同時に回せる。スロット 0 は従来の置き場。1〜 は 0 の写しで、実行キャッシュ（_harness_cache）だけ 0 と共有する。
# 作業ツリーの中に前の実行の結果を残して次の実行が読む道具（visual_review の snapshot→compare・holdout_eval の記録・
# EditMode のホスト）はスロット 0 に固定する。そうでない実行（pytest・abcheck・結果を --fetch で持ち帰る計測）は、
# どのスロットでもよい（1〜 を先に好む）。どれを使うかは相手の予約の列が決める（2026-11-22〜。来た順に、空いた
# スロットを配る。前の実行が使えるスロットが空いていなければ、後ろの実行が使える別のスロットへ先に回る）。
DEFAULT_SLOTS = 2
_STATELESS_SCRIPTS = ("abcheck.py", "generalization_report.py", "highlight_recall.py", "color_report.py", "click_bridge.py")
_STATELESS_PREFIXES = ("probe_", "measure_", "sweep_")


def slot_count() -> int:
    try:
        return max(1, int(os.environ.get("IROCA_REMOTE_SLOTS", DEFAULT_SLOTS)))
    except ValueError:
        return DEFAULT_SLOTS


def is_stateless(argv: list[str]) -> bool:
    """作業ツリーの中の前の実行の結果に頼らない実行か（空いている別スロットへ回してよい実行）。"""
    if not argv:
        return False
    if argv[0] == "-m":
        return len(argv) > 1 and argv[1] == "pytest"
    name = Path(argv[0]).name
    return name in _STATELESS_SCRIPTS or name.startswith(_STATELESS_PREFIXES)


def candidate_slots(info: dict, argv: list[str] | None, requested: int | None, need_editor: bool) -> list[int]:
    """使ってよいスロットを好む順に返す（どれを使うかは相手の予約の列が、空いた順に決める。2026-11-22〜）。

    指定があればそれだけ。スロット 0 に固定する実行は 0 だけ。別スロットへ回してよい実行は 1〜 を先に、0 を最後に
    （0 でしか回せない視覚レビュー・EditMode のために 0 を空けておく）。まだ作っていないスロットは、ほかが全部
    使用中のときだけ作って入れる（作るのに数分かかるので、空いていればそちらで回る方が早い）。"""
    if requested is not None:
        return [requested]
    if need_editor or argv is None or not is_stateless(argv):
        return [0]
    slots = {int(s["slot"]): s for s in info.get("slots") or []}
    others = [n for n in range(1, slot_count()) if n in slots]
    ready = [n for n in others if slots[n].get("ready")]
    cands = ready + [0]
    if all(slots.get(n, {}).get("busy") for n in cands):
        cands = [n for n in others if not slots[n].get("ready")][:1] + cands
    return cands


def resolve(t: Transport, mode: str, manifest: bool, need_editor: bool = False,
            need_desktop: bool = False, argv: list[str] | None = None, slot: int | None = None) -> dict | None:
    """使えるならリモートの情報を返す。auto で使えなければ None（理由は log に出す）。

    返す info の "candidates" が使ってよいスロット（好む順）。実際のスロットは run_remote が予約して決まる。
    argv を渡さない呼び手（visual_review など）は常にスロット 0。manifest は互換のために受けるだけ（データの一覧は
    スロットが決まってから、そのスロットの分を取る）。"""
    if mode == "local":
        return None
    try:
        if not reachable(t):
            raise Unavailable(f"{t.host} につながらない")
        info = remote_info(t)
        missing = check_ready(info, need_editor, need_desktop)
        if missing:
            raise Unavailable("相手の準備が足りない: " + "・".join(missing))
        cands = candidate_slots(info, argv, slot, need_editor)
        slots = info.get("slots") or []
        for n in cands:
            if n >= len(slots):
                raise Unavailable(f"相手にスロット {n} は無い")
            if n > 0 and not slots[n].get("ready"):
                log(f"スロット {n} を作ります（スロット 0 の写し。データを相手の中で写すので数分かかる）")
                r = t.run_agent("slot-init", "--slot", str(n), *DATA_DIRS, capture=False)
                if r.returncode != 0:
                    raise Unavailable(f"スロット {n} を作れなかった")
        info["candidates"] = cands
        return info
    except Unavailable as e:
        if mode == "remote":
            raise SystemExit(f"[remote-test] リモートを使えない: {e}")
        log(f"{e} → 手元で実行します")
        return None


def sweep_local_caches() -> None:
    """手元のキャッシュを、pytest の開始時と同じ規則で掃除する(dev_safe の cache_gc.maybe_sweep。2026-11-14)。

    pytest の開始時の掃除は、回す機械(相手)の dev_safe しか見ない。テストを相手で回すようになってから手元では
    pytest が始まらず、手元で使った DLL(visual_review・golden など)の分も、日数の切れた作業ファイルも残っていた。
    使われなくなった DLL の実行キャッシュ(印が IROCA_CACHE_GC_DLL_DAYS、手元の 2TB では 1 日動いていない分)は毎回、
    日数での掃除は 6 時間に 1 回。ふだんは一瞬で終わる。失敗してもテストは止めない。"""
    if os.environ.get("IROCA_CACHE_GC", "1") == "0":
        return
    tests = REPO / "dev_safe" / "Tests"
    if not (tests / "regression" / "cache_gc.py").is_file():
        return
    try:
        sys.path.insert(0, str(tests))
        try:
            from regression import cache_gc
        finally:
            sys.path.remove(str(tests))
        note = cache_gc.maybe_sweep()
    except Exception as e:      # 掃除の失敗でテストを止めない
        log(f"手元のキャッシュの掃除に失敗しました: {e!r}")
        return
    for line in (note or "").splitlines():
        log("手元: " + line.removeprefix("[iroca] "))


def cmd_run(ns) -> int:
    argv = ns.argv[1:] if ns.argv and ns.argv[0] == "--" else ns.argv
    if not argv:
        raise SystemExit("`--` の後ろに Python へ渡す引数を書く（例: -- -m pytest scripts/source_checks -q）")
    t = make_transport(ns.host)
    mode = "local" if ns.local else ("remote" if ns.remote else "auto")
    info = resolve(t, mode, manifest=True, need_editor=needs_editor(argv), need_desktop=needs_desktop(argv),
                   argv=argv, slot=ns.slot)
    if info is None:
        return run_local(argv)
    sweep_local_caches()
    try:
        return run_remote(t, argv, ns.fetch, ns.env, info)
    except Unavailable as e:
        if mode == "remote":
            raise SystemExit(f"[remote-test] リモートを使えない: {e}")
        log(f"{e} → 手元で実行します")
        return run_local(argv)


def cmd_status(ns) -> int:
    t = make_transport(ns.host)
    try:
        hostname, port = t.endpoint()
        print(f"相手: {t.host}（{hostname}:{port}）")
        if not reachable(t):
            print("つながらない（テストは手元で回る）")
            return 1
        info = remote_info(t)
    except Unavailable as e:
        print(f"使えない: {e}")
        return 1
    if not ns.json:
        info_brief = {k: v for k, v in info.items() if k not in ("slots", "queue")}
        print(json.dumps(info_brief, ensure_ascii=False, indent=2))
        n = slot_count()
        for s in (info.get("slots") or [])[:n]:
            run = s.get("running")
            if not s.get("ready"):
                state = "未作成"
            elif s.get("busy"):
                state = (f"実行中 {run['label']}（{run['running_s'] // 60} 分）" if run else "実行中（従来の入口）")
            else:
                state = "空き"
            print(f"スロット {s['slot']}: {state}")
        queue = info.get("queue") or []
        print(f"予約の列: {len(queue)} 件" if queue else "予約の列: なし")
        for i, q in enumerate(queue, 1):
            print(f"  {i}. {q['label']}（スロット {'・'.join(map(str, q['slots']))}・{q['waited_s'] // 60} 分待ち）")
    else:
        print(json.dumps(info, ensure_ascii=False, indent=2))
    missing = check_ready(info)
    print("準備: " + ("済み" if not missing else "足りない " + "・".join(missing)))
    return 0 if not missing else 1


def cmd_setup(ns) -> int:
    t = make_transport(ns.host)
    if not reachable(t):
        raise SystemExit(f"{t.host} につながらない")
    info = remote_info(t)
    if not info.get("git") or not info.get("dotnet"):
        raise SystemExit("相手に Git と .NET SDK 8 が要る（Enable-RemoteTestHost.ps1 を相手の PC で管理者で実行）")
    managed = os.environ.get("UnityManaged") or os.environ.get("UNITY_EDITOR_PATH")
    if not managed or not (Path(managed) / "UnityEngine" / "UnityEngine.CoreModule.dll").is_file():
        raise SystemExit("手元の Unity の DLL が見つからない（UnityManaged を設定する）")
    log("Unity の DLL を送ります")
    _upload_dir_tar(t, "unity", Path(managed) / "UnityEngine")
    models = Path(os.environ.get("LOCALAPPDATA", "")) / "Iroca" / "Models"
    if models.is_dir() and any(models.glob("*.onnx")):
        log("SAM のモデルを送ります")
        _upload_dir_tar(t, "models", models)
    else:
        log("手元に SAM のモデルが無いので送らない（AI 提案のテストは skip になる）")
    info = remote_info(t, manifest=True)
    log("本体・dev_safe・データを送って .venv を作ります（初回はデータが十数 GB あるので時間がかかる）")
    return run_remote(t, ["-m", "pytest", "--version"], [], [], info)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd")
    sp_status = sub.add_parser("status", help="相手につながるか・準備が済んでいるか・スロットと予約の列を見る")
    sp_status.add_argument("--json", action="store_true", help="相手の状態を JSON のまま全部出す")
    sp_setup = sub.add_parser("setup", help="相手へ Unity の DLL・モデル・リポジトリ・データを送って .venv を作る")
    rp = sub.add_parser("run", help="（既定）Python を相手か手元で動かす。`--` の後ろが Python の引数")
    for p in (sp_status, sp_setup, rp):
        p.add_argument("--host", help=f"ssh の Host 名（既定 {DEFAULT_HOST}・IROCA_REMOTE_HOST）")
    g = rp.add_mutually_exclusive_group()
    g.add_argument("--remote", action="store_true", help="リモート必須（使えなければ失敗）")
    g.add_argument("--local", action="store_true", help="常に手元で回す")
    rp.add_argument("--fetch", action="append", default=[], metavar="PATH",
                    help="実行後に持ち帰る、本体からの相対パス（複数可）")
    rp.add_argument("--slot", type=int, default=None, metavar="N",
                    help="相手のスロットを指定する（既定: 0 が使用中で、前の実行の結果に頼らない実行なら空いている次のスロット）")
    rp.add_argument("--env", action="append", default=[], metavar="K=V", help="相手に渡す環境変数を足す")
    rp.add_argument("argv", nargs=argparse.REMAINDER)
    args = sys.argv[1:]
    if not args or args[0] not in ("status", "setup", "run", "-h", "--help"):
        args = ["run", *args]
    ns = ap.parse_args(args)
    if ns.cmd == "status":
        return cmd_status(ns)
    if ns.cmd == "setup":
        return cmd_setup(ns)
    return cmd_run(ns)


if __name__ == "__main__":
    sys.exit(main())
