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
SNAP_REF = "refs/remote-test/snap"
SENT_REF = "refs/remote-test/sent"
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


def remote_info(t: Transport, manifest: bool = False) -> dict:
    args = ["info"] + (["--manifest", *DATA_DIRS] if manifest else [])
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


def check_ready(info: dict) -> list[str]:
    missing = []
    if not info.get("git"):
        missing.append("git")
    if not info.get("dotnet"):
        missing.append(".NET SDK")
    if not info.get("unity"):
        missing.append("Unity の DLL（setup で送る）")
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
    git(repo, "add", "-A", env=env)
    tree = git(repo, "write-tree", env=env)
    head = git(repo, "rev-parse", "-q", "--verify", "HEAD^{commit}", check=False)
    parent = ["-p", head] if head else []
    commit = git(repo, "commit-tree", tree, *parent, "-m", "remote-test snapshot", env=env)
    git(repo, "update-ref", SNAP_REF, commit)
    return commit


def make_bundle(repo: Path, commit: str, remote_has: str | None, out: Path) -> Path | None:
    if remote_has == commit:
        return None
    excl = []
    if remote_has and subprocess.run(["git", "-C", str(repo), "cat-file", "-e", remote_has + "^{commit}"],
                                     capture_output=True).returncode == 0:
        excl = ["^" + remote_has]
    git(repo, "bundle", "create", "-q", str(out), SNAP_REF, *excl)
    return out


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
        if _ABS_PATH_RE.match(v):
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
    for key in ("core.autocrlf", "core.eol", "core.symlinks"):
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


def run_remote(t: Transport, argv: list[str], fetch: list[str], env_extra: list[str], info: dict) -> int:
    run_id = time.strftime("%Y%m%d-%H%M%S-") + uuid.uuid4().hex[:6]
    tmpdir = Path(tempfile.mkdtemp(prefix="iroca-remote-test-"))
    try:
        try:
            _prepare(t, run_id, argv, fetch, env_extra, info, tmpdir)
        except (RuntimeError, OSError, subprocess.SubprocessError) as e:
            # まだ何も実行していないので、auto なら手元でやり直してよい
            raise Unavailable(f"送る途中で失敗: {e}") from e
        p = t.popen_agent("run", run_id)
        try:
            rc = p.wait()
        except KeyboardInterrupt:
            p.terminate()
            raise
        if rc == SYNC_FAILED:
            raise Unavailable("相手で作業ツリーをそろえられなかった（テストは走っていない）")
        if rc == 255 and not isinstance(t, LoopbackTransport):
            log("ssh の接続が切れた可能性があります（終了コード 255）。結果は信用せず回し直してください")
        if fetch:
            pf = t.popen_agent("cat", run_id, "fetch.tar", stdout=subprocess.PIPE)
            n = 0
            try:
                with tarfile.open(fileobj=pf.stdout, mode="r|") as tf:
                    for member in tf:
                        tf.extract(member, REPO, filter="data")
                        n += 1
            except tarfile.ReadError:
                pass
            pf.wait()
            log(f"持ち帰り {n} 個")
        return rc
    finally:
        try:
            t.run_agent("cleanup", run_id, timeout=120)
        except Exception:
            pass
        shutil.rmtree(tmpdir, ignore_errors=True)


def _prepare(t: Transport, run_id: str, argv: list[str], fetch: list[str], env_extra: list[str],
             info: dict, tmpdir: Path) -> None:
    t0 = time.monotonic()
    repos = {}
    for key, repo in (("main", REPO), ("dev_safe", DEV_SAFE)):
        commit = snapshot(repo, tmpdir)
        bundle = make_bundle(repo, commit, info["repos"].get(key), tmpdir / f"{key}.bundle")
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
        "env": forwarded_env(env_extra),
        "argv": argv,
        "fetch": fetch,
        "git_config": git_config_to_mirror(REPO),
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


def resolve(t: Transport, mode: str, manifest: bool) -> dict | None:
    """使えるならリモートの情報を返す。auto で使えなければ None（理由は log に出す）。"""
    if mode == "local":
        return None
    try:
        if not reachable(t):
            raise Unavailable(f"{t.host} につながらない")
        info = remote_info(t, manifest=manifest)
        missing = check_ready(info)
        if missing:
            raise Unavailable("相手の準備が足りない: " + "・".join(missing))
        if info.get("busy"):
            log("相手は別の実行中です。終わるのを待ってから回ります")
        return info
    except Unavailable as e:
        if mode == "remote":
            raise SystemExit(f"[remote-test] リモートを使えない: {e}")
        log(f"{e} → 手元で実行します")
        return None


def cmd_run(ns) -> int:
    argv = ns.argv[1:] if ns.argv and ns.argv[0] == "--" else ns.argv
    if not argv:
        raise SystemExit("`--` の後ろに Python へ渡す引数を書く（例: -- -m pytest scripts/source_checks -q）")
    t = make_transport(ns.host)
    mode = "local" if ns.local else ("remote" if ns.remote else "auto")
    info = resolve(t, mode, manifest=True)
    if info is None:
        return run_local(argv)
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
    sp_status = sub.add_parser("status", help="相手につながるか・準備が済んでいるかを見る")
    sp_setup = sub.add_parser("setup", help="相手へ Unity の DLL・モデル・リポジトリ・データを送って .venv を作る")
    rp = sub.add_parser("run", help="（既定）Python を相手か手元で動かす。`--` の後ろが Python の引数")
    for p in (sp_status, sp_setup, rp):
        p.add_argument("--host", help=f"ssh の Host 名（既定 {DEFAULT_HOST}・IROCA_REMOTE_HOST）")
    g = rp.add_mutually_exclusive_group()
    g.add_argument("--remote", action="store_true", help="リモート必須（使えなければ失敗）")
    g.add_argument("--local", action="store_true", help="常に手元で回す")
    rp.add_argument("--fetch", action="append", default=[], metavar="PATH",
                    help="実行後に持ち帰る、本体からの相対パス（複数可）")
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
