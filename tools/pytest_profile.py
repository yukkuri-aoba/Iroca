"""テストの時間がどこで掛かっているかを測る pytest プラグイン（テスト本体は変えない）。

    python -m pytest dev_safe/Tests/regression/ -q -p tools.pytest_profile
    python tools/pytest_profile.py report            # 結果をまとめて出す

各プロセス（xdist ならワーカーごと）を cProfile で測り、`.pytest_cache/iroca_profile/<worker>.prof` に書く。
あわせてテスト 1 件ごとの開始・終了時刻（準備・後片付け込み）を `<worker>.jsonl` に書く。ワーカーの偏り
（1 つだけ遅くまで動いて残りが待つ）を見るため。テスト用 PC で回すなら
`python tools/remote_test.py --fetch .pytest_cache/iroca_profile -- -m pytest … -p tools.pytest_profile`。
"""
from __future__ import annotations

import cProfile
import json
import os
import shutil
import sys
import time
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / ".pytest_cache" / "iroca_profile"

_prof: cProfile.Profile | None = None
_log = None
_worker = "main"


def pytest_configure(config):
    global _prof, _log, _worker
    winput = getattr(config, "workerinput", None)
    _worker = winput["workerid"] if winput else "main"
    if winput is None:
        shutil.rmtree(OUT, ignore_errors=True)
    OUT.mkdir(parents=True, exist_ok=True)
    _log = open(OUT / f"{_worker}.jsonl", "a", encoding="utf-8")
    _prof = cProfile.Profile()
    _prof.enable()


def pytest_runtest_logstart(nodeid, location):
    if _log is not None:
        _log.write(json.dumps({"ev": "start", "id": nodeid, "t": time.time()}) + "\n")


def pytest_runtest_logfinish(nodeid, location):
    if _log is not None:
        _log.write(json.dumps({"ev": "end", "id": nodeid, "t": time.time()}) + "\n")
        _log.flush()


def pytest_unconfigure(config):
    global _prof, _log
    if _prof is not None:
        _prof.disable()
        _prof.dump_stats(str(OUT / f"{_worker}.prof"))
        _prof = None
    if _log is not None:
        _log.close()
        _log = None


# ---- まとめ ----

def report(top: int = 40) -> None:
    import pstats
    from collections import defaultdict

    profs = sorted(OUT.glob("*.prof"))
    workers = [p for p in profs if p.stem != "main"]
    if not profs:
        raise SystemExit(f"{OUT} に結果が無い")
    # ワーカーごとの稼働
    spans = {}
    per_file = defaultdict(float)
    per_test = []
    t_min = None
    logs = sorted(OUT.glob("*.jsonl"))
    if any(p.stem != "main" for p in logs):
        # xdist の親もワーカーの報告を受けて同じ印を書くので、二重に数えない
        logs = [p for p in logs if p.stem != "main"]
    for log in logs:
        starts = {}
        busy = 0.0
        first = last = None
        for line in log.read_text(encoding="utf-8").splitlines():
            ev = json.loads(line)
            if ev["ev"] == "start":
                starts[ev["id"]] = ev["t"]
                first = ev["t"] if first is None else min(first, ev["t"])
            elif ev["id"] in starts:
                d = ev["t"] - starts.pop(ev["id"])
                busy += d
                last = ev["t"] if last is None else max(last, ev["t"])
                per_file[ev["id"].split("::")[0]] += d
                per_test.append((d, ev["id"], log.stem))
        if first is not None:
            spans[log.stem] = (first, last, busy)
            t_min = first if t_min is None else min(t_min, first)
    if spans:
        print("== ワーカーの稼働（最初のテストの開始からの秒）")
        for w, (a, b, busy) in sorted(spans.items()):
            print(f"  {w:>5}: {a - t_min:7.1f} 〜 {b - t_min:7.1f}  テスト中 {busy:7.1f} 秒")
        print("== ファイルごとの合計（秒）")
        for f, d in sorted(per_file.items(), key=lambda kv: -kv[1])[:25]:
            print(f"  {d:8.1f}  {f}")
        print("== 重いテスト")
        for d, nid, w in sorted(per_test, reverse=True)[:25]:
            print(f"  {d:8.1f}  {w:>4}  {nid}")
    st = pstats.Stats(*[str(p) for p in (workers or profs)])
    st.strip_dirs()
    print(f"== 関数ごと（{len(workers or profs)} プロセスの合計・自分の時間順）")
    st.sort_stats("tottime").print_stats(top)
    print("== 関数ごと（呼んだ先を含む時間順）")
    st.sort_stats("cumulative").print_stats(top)


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "report":
        report(int(sys.argv[2]) if len(sys.argv) > 2 else 40)
    else:
        print(__doc__)
