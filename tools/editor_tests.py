"""Unity 実機の EditMode テスト（scripts/Run-EditorTests.ps1）を Python から呼ぶ入口。

    python tools/remote_test.py -- tools/editor_tests.py                      # テスト用 PC があればそちらで
    python tools/remote_test.py -- tools/editor_tests.py -TestFilter Parity   # 後ろは Run-EditorTests.ps1 の引数
    python tools/remote_test.py --fetch dev_safe/_editor_tests -- tools/editor_tests.py --save dev_safe/_editor_tests

`tools/remote_test.py` は `--` の後ろを python に渡すので、この入口を挟むと EditMode テストもテスト用 PC で回る。
ホストの Unity プロジェクトは、`--host` → 環境変数 `IROCA_EDITOR_HOST` → 次の順で最初にあるもの:
  1. `<この作業ツリーの親>\\hosts\\Iroca_U2022_ifx`（テスト用 PC の置き場 `iroca-remote\\hosts\\`）
  2. `%USERPROFILE%\\Documents\\Avatar_Projects\\Iroca_U2022_ifx`（開発機。ユーザーが開いていないホスト）
`--host` に名前だけを書くと、同じ 2 か所からその名前のホストを探す（`--host Iroca_U2022` は Sentis 入りのホスト。
本物の AI 推論のテストを回す。2026-11-22〜）。パスを書けばそのまま使う。
ホストは `scripts/Link-HostPackage.ps1 -WithEditorTests` でこの作業ツリーへリンク済みであること
（Run-EditorTests.ps1 が確かめる）。

`--save <本体からの相対パス>` は、結果のフォルダ（results.xml・unity.log・parity.tsv など）をそこへ写す。
リモートでは `--fetch` で持ち帰るために使う。終了コードは Run-EditorTests.ps1 のまま（失敗の数）。

画面の要る台本（`-UndoReload`・`-SdkBuild`・`-SdkUpload`）は、ssh の先のようにデスクトップの無いセッション
（session 0）から呼ばれたら、同じユーザーがサインインしているデスクトップでタスク スケジューラーから動かし、
出力を写して待つ（2026-11-20〜）。サインインしたデスクトップが無ければ失敗する（remote_test.py は先に確かめて手元へ回す）。
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SCRIPT = REPO / "scripts" / "Run-EditorTests.ps1"
HOST_NAME = "Iroca_U2022_ifx"
# 画面ありの Unity を起動する台本（Run-EditorTests.ps1 で batchmode を使わない分岐）
GUI_SWITCHES = ("-undoreload", "-sdkbuild", "-sdkupload")
# デスクトップで動かすときの全体の制限時間（-SdkBuild は Run-EditorTests.ps1 の -SdkTimeoutMinutes に足す余裕）
GUI_TIMEOUT_MIN = 40


def needs_desktop(args: list[str]) -> bool:
    return any(a.lower() in GUI_SWITCHES for a in args)


def session_id() -> int | None:
    """このプロセスの Windows のセッション番号（0 はサービスと ssh の、デスクトップの無いセッション）。"""
    if os.name != "nt":
        return None
    import ctypes
    from ctypes import wintypes
    sid = wintypes.DWORD()
    if not ctypes.windll.kernel32.ProcessIdToSessionId(os.getpid(), ctypes.byref(sid)):
        return None
    return int(sid.value)


def desktop_sessions() -> list[int]:
    """このユーザーがサインインしていて、デスクトップ（explorer.exe）が動いているセッションの番号。"""
    # ssh の先では USERDOMAIN が WORKGROUP になり、domain\user で絞ると何も出ない。ユーザー名だけで絞る
    user = os.environ.get("USERNAME", "")
    try:
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FI", f"USERNAME eq {user}",
                            "/FO", "CSV", "/NH"], capture_output=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired):
        return []
    out = []
    for line in r.stdout.decode("mbcs" if os.name == "nt" else "utf-8", "replace").splitlines():
        cols = [c.strip('"') for c in line.split('","')]
        if len(cols) >= 4 and cols[0].lower().endswith("explorer.exe") and cols[3].isdigit() and int(cols[3]) > 0:
            out.append(int(cols[3]))
    return out


def find_host(name: str) -> Path | None:
    """名前だけのホストを、テスト用 PC の置き場 → 開発機の置き場の順に探す（テストがリンクしてあるもの）。"""
    cands = [
        REPO.parent / "hosts" / name,
        Path(os.environ.get("USERPROFILE", str(Path.home()))) / "Documents" / "Avatar_Projects" / name,
    ]
    for c in cands:
        if (c / "Assets" / "IrocaEditorTests").exists():
            return c
    return None


def default_host() -> Path | None:
    return find_host(HOST_NAME)


def resolve_host(arg: str) -> Path | None:
    """`--host` の値。パスならそのまま、名前だけ（区切りの無い値）なら find_host で探す。
    名前で渡せば、テスト用 PC でも開発機でも同じ書き方で同じホストを選べる（例: --host Iroca_U2022 で Sentis 入りのホスト）。"""
    p = Path(arg)
    if p.is_absolute() or len(p.parts) > 1:
        return p
    return find_host(arg)


def _ps_quote(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def _ps(script: str, timeout: float = 120) -> subprocess.CompletedProcess:
    return subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                           "$ProgressPreference = 'SilentlyContinue'; " + script],
                          capture_output=True, timeout=timeout)


def run_on_desktop(call: str, sessions: list[int], host_path: Path) -> int:
    """`call`（PowerShell の文）を、サインイン中のデスクトップでタスク スケジューラーから動かし、出力を写して待つ。

    ssh の先（session 0）から画面ありの Unity を起動しても、ウィンドウを持てずに動かない。ログオンの種類を
    Interactive にしたタスクは、そのユーザーがサインインしているセッションで動く（パスワードを預けずに済む）。
    出力はファイルに書かせて、こちらで少しずつ読んで出す。終わりの印は終了コードのファイル。
    """
    stamp = time.strftime("%Y%m%d-%H%M%S-") + str(os.getpid())
    work = Path(os.environ.get("TEMP", str(REPO))) / "iroca-desktop-run" / stamp
    work.mkdir(parents=True, exist_ok=True)
    out, rc_file, launcher = work / "out.log", work / "rc.txt", work / "run.ps1"
    env_lines = [f"$env:{k} = {_ps_quote(v)}" for k, v in os.environ.items()
                 if k.upper().startswith(("IROCA_", "VACC_")) and not k.upper().startswith("IROCA_REMOTE")]
    body = "\n".join([
        "$enc = New-Object Text.UTF8Encoding $false",
        "[Console]::OutputEncoding = $enc",
        f"$out = {_ps_quote(str(out))}",
        f"Set-Location -LiteralPath {_ps_quote(str(REPO))}",
        *env_lines,
        "$rc = 1",
        "try {",
        f"    {call} *>&1 | ForEach-Object {{ [IO.File]::AppendAllText($out, \"$_`r`n\", $enc) }}",
        "    $rc = $LASTEXITCODE",
        "} catch {",
        "    [IO.File]::AppendAllText($out, \"$_`r`n\", $enc)",
        "}",
        f"Set-Content -LiteralPath {_ps_quote(str(rc_file) + '.tmp')} -Value $rc -Encoding ascii",
        f"Move-Item -LiteralPath {_ps_quote(str(rc_file) + '.tmp')} -Destination {_ps_quote(str(rc_file))} -Force",
    ])
    # PowerShell 5.1 は BOM の無い .ps1 を ANSI で読む（日本語のパス・文言が化ける）
    launcher.write_text(body, encoding="utf-8-sig")
    out.write_bytes(b"")
    task = f"iroca-desktop-{stamp}"
    q_task = f"-TaskName {_ps_quote(task)} -TaskPath '\\Iroca\\'"
    arg = f'-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{launcher}"'
    reg = _ps("; ".join([
        f"$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument {_ps_quote(arg)}",
        "$p = New-ScheduledTaskPrincipal -UserId \"$env:COMPUTERNAME\\$env:USERNAME\" -LogonType Interactive -RunLevel Limited",
        "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries "
        f"-ExecutionTimeLimit (New-TimeSpan -Minutes {GUI_TIMEOUT_MIN + 10}) -MultipleInstances IgnoreNew",
        f"Register-ScheduledTask {q_task} -Action $a -Principal $p -Settings $s -Force | Out-Null",
        f"Start-ScheduledTask {q_task}",
    ]))
    if reg.returncode != 0:
        print("[editor-tests] デスクトップで動かすタスクを作れない: "
              + reg.stderr.decode("mbcs", "replace").strip()[-600:], file=sys.stderr)
        shutil.rmtree(work, ignore_errors=True)
        return 1
    print(f"[editor-tests] サインイン中のデスクトップ（セッション {', '.join(map(str, sessions))}）で動かします", flush=True)
    started = time.monotonic()
    pos = 0
    last_state_check = started
    rc: int | None = None
    try:
        while True:
            data = out.read_bytes()[pos:] if out.is_file() else b""
            if data:
                cut = data.rfind(b"\n") + 1
                if cut:
                    sys.stdout.write(data[:cut].decode("utf-8", "replace"))
                    sys.stdout.flush()
                    pos += cut
            if rc_file.is_file():
                rest = out.read_bytes()[pos:]
                if rest:
                    sys.stdout.write(rest.decode("utf-8", "replace") + "\n")
                txt = rc_file.read_text(encoding="ascii", errors="replace").strip()
                rc = int(txt) if txt.lstrip("-").isdigit() else 1
                return rc
            now = time.monotonic()
            if now - started > GUI_TIMEOUT_MIN * 60:
                print(f"[editor-tests] {GUI_TIMEOUT_MIN} 分で終わらないので止めます", file=sys.stderr)
                return 1
            if now - last_state_check > 30:
                last_state_check = now
                st = _ps(f"$t = Get-ScheduledTask {q_task}; $i = $t | Get-ScheduledTaskInfo; "
                         "\"$($t.State) $($i.LastTaskResult)\"")
                state = st.stdout.decode("mbcs", "replace").split()
                # 走っていないのに終わりの印が無い（起動できなかった・途中で落ちた）。印の書き込みを少し待ってから見る
                if state and state[0] != "Running" and not rc_file.is_file():
                    time.sleep(3)
                    if not rc_file.is_file():
                        print(f"[editor-tests] デスクトップのタスクが終わりの印なしで止まった（状態 {' '.join(state)}）",
                              file=sys.stderr)
                        return 1
            time.sleep(1)
    finally:
        if rc is None:
            # 止めたときは、台本が起動した Unity（このホストを開いたもの）も止める
            _ps(f"Stop-ScheduledTask {q_task} -ErrorAction SilentlyContinue; "
                "Get-CimInstance Win32_Process -Filter \"Name='Unity.exe'\" | "
                f"Where-Object {{ $_.CommandLine -like {_ps_quote('*' + str(host_path) + '*')} }} | "
                "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }")
        _ps(f"Unregister-ScheduledTask {q_task} -Confirm:$false -ErrorAction SilentlyContinue")
        shutil.rmtree(work, ignore_errors=True)



def main() -> int:
    # パイプ越しでも PowerShell 側（UTF-8）と同じ文字コードで出す
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    args = sys.argv[1:]
    host: str | None = os.environ.get("IROCA_EDITOR_HOST")
    save: str | None = None
    rest: list[str] = []
    i = 0
    while i < len(args):
        a = args[i]
        if a in ("--host", "--save") and i + 1 < len(args):
            if a == "--host":
                host = args[i + 1]
            else:
                save = args[i + 1]
            i += 2
            continue
        if a in ("-h", "--help"):
            print(__doc__)
            return 0
        rest.append(a)
        i += 1
    host_path = resolve_host(host) if host else default_host()
    if host_path is None:
        print(f"[editor-tests] ホストの Unity プロジェクトが見つからない（{host or HOST_NAME}。"
              "--host か IROCA_EDITOR_HOST で、パスか置き場の中の名前を指定）", file=sys.stderr)
        return 2

    tmp_root = Path(os.environ.get("TEMP", "")) / "iroca-editor-tests"
    before = set(tmp_root.iterdir()) if tmp_root.is_dir() else set()

    # $PSScriptRoot が空になるので -File でなく呼び出し演算子で呼ぶ。
    # 引数はそのまま渡す（-Name 値 / スイッチ）。値は文字列として引用する。
    parts = []
    for a in rest:
        parts.append(a if a.startswith("-") else _ps_quote(a))
    # スクリプトの exit の値を powershell.exe の終了コードへ渡す（-Command は既定で 0/1 にしてしまう）。
    call = f"& {_ps_quote(str(SCRIPT))} -HostProject {_ps_quote(str(host_path))} {' '.join(parts)}"
    # 出力は UTF-8 にする（テスト用 PC の受け手は UTF-8 で読む。既定の cp932 だと日本語が化ける）。
    cmd = f"[Console]::OutputEncoding = [Text.Encoding]::UTF8; try {{ {call}; exit $LASTEXITCODE }} catch {{ Write-Host $_ -ForegroundColor Red; exit 1 }}"
    print(f"[editor-tests] ホスト: {host_path}", flush=True)
    if needs_desktop(rest) and session_id() == 0:
        sessions = desktop_sessions()
        if not sessions:
            print("[editor-tests] 画面の要る台本ですが、このユーザーがサインインしたデスクトップがありません"
                  "（テスト用 PC なら、サインインしたままにしておく）", file=sys.stderr)
            return 2
        rc = run_on_desktop(call, sessions, host_path)
    else:
        rc = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", cmd],
                            cwd=str(REPO)).returncode

    if save:
        after = set(tmp_root.iterdir()) if tmp_root.is_dir() else set()
        new = sorted(after - before)
        dest = REPO / save
        if new:
            if dest.exists():
                shutil.rmtree(dest)
            shutil.copytree(new[-1], dest)
            print(f"[editor-tests] 結果を写しました: {save}", flush=True)
        else:
            print("[editor-tests] 結果のフォルダが見つからず、写していない", file=sys.stderr)
    return rc


if __name__ == "__main__":
    sys.exit(main())
