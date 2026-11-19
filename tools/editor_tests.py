"""Unity 実機の EditMode テスト（scripts/Run-EditorTests.ps1）を Python から呼ぶ入口。

    python tools/remote_test.py -- tools/editor_tests.py                      # テスト用 PC があればそちらで
    python tools/remote_test.py -- tools/editor_tests.py -TestFilter Parity   # 後ろは Run-EditorTests.ps1 の引数
    python tools/remote_test.py --fetch dev_safe/_editor_tests -- tools/editor_tests.py --save dev_safe/_editor_tests

`tools/remote_test.py` は `--` の後ろを python に渡すので、この入口を挟むと EditMode テストもテスト用 PC で回る。
ホストの Unity プロジェクトは、`--host` → 環境変数 `IROCA_EDITOR_HOST` → 次の順で最初にあるもの:
  1. `<この作業ツリーの親>\\hosts\\Iroca_U2022_ifx`（テスト用 PC の置き場 `iroca-remote\\hosts\\`）
  2. `%USERPROFILE%\\Documents\\Avatar_Projects\\Iroca_U2022_ifx`（開発機。ユーザーが開いていないホスト）
ホストは `scripts/Link-HostPackage.ps1 -WithEditorTests` でこの作業ツリーへリンク済みであること
（Run-EditorTests.ps1 が確かめる）。

`--save <本体からの相対パス>` は、結果のフォルダ（results.xml・unity.log・parity.tsv など）をそこへ写す。
リモートでは `--fetch` で持ち帰るために使う。終了コードは Run-EditorTests.ps1 のまま（失敗の数）。
"""
from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SCRIPT = REPO / "scripts" / "Run-EditorTests.ps1"
HOST_NAME = "Iroca_U2022_ifx"


def default_host() -> Path | None:
    cands = [
        REPO.parent / "hosts" / HOST_NAME,
        Path(os.environ.get("USERPROFILE", str(Path.home()))) / "Documents" / "Avatar_Projects" / HOST_NAME,
    ]
    for c in cands:
        if (c / "Assets" / "IrocaEditorTests").exists():
            return c
    return None


def _ps_quote(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


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
    host_path = Path(host) if host else default_host()
    if host_path is None:
        print("[editor-tests] ホストの Unity プロジェクトが見つからない（--host か IROCA_EDITOR_HOST で指定）",
              file=sys.stderr)
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
