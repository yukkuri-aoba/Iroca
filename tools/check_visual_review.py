"""pre-commit フックから呼ばれる視覚レビュー承認チェック。

アルゴリズムファイル（dev_safe/vacc_python/ または Code/）がステージされているとき、
approved.json が全ステージファイルより新しくなければコミットをブロックする。

終了コード:
    0 — チェック通過（コミット許可）
    1 — チェック失敗（コミットブロック）
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from datetime import datetime
from pathlib import Path

# git リポジトリルートを取得
try:
    ROOT = Path(
        subprocess.check_output(
            ["git", "rev-parse", "--show-toplevel"],
            stderr=subprocess.DEVNULL,
            encoding="utf-8",
        ).strip()
    )
except (subprocess.CalledProcessError, UnicodeDecodeError):
    sys.exit(0)  # git リポジトリ外または取得失敗では無条件通過

APPROVED_JSON = ROOT / "dev_safe" / "Tests" / "visual_review" / "approved.json"

# 視覚レビュー要求の対象パス prefix（前方一致、スラッシュ区切り）
ALGORITHM_PREFIXES = [
    "dev_safe/vacc_python/",
    "Code/",
]


def get_staged_files() -> list[str]:
    result = subprocess.run(
        ["git", "diff", "--cached", "--name-only"],
        capture_output=True,
        encoding="utf-8",
    )
    return [f.strip() for f in result.stdout.splitlines() if f.strip()]


def is_algorithm_file(path: str) -> bool:
    normalized = path.replace("\\", "/")
    return any(normalized.startswith(prefix) for prefix in ALGORITHM_PREFIXES)


def get_file_mtime(rel_path: str) -> datetime | None:
    p = ROOT / rel_path.replace("/", "\\")
    if not p.exists():
        return None
    return datetime.fromtimestamp(p.stat().st_mtime)


def main() -> None:
    if os.environ.get("SKIP_VISUAL_REVIEW") == "1":
        print("[pre-commit] SKIP_VISUAL_REVIEW=1 のため視覚レビューチェックをスキップします")
        sys.exit(0)

    staged = get_staged_files()
    algo_staged = [f for f in staged if is_algorithm_file(f)]

    if not algo_staged:
        sys.exit(0)  # アルゴリズムファイルなし → チェック不要

    print(f"[pre-commit] アルゴリズムファイル変更を検出: {len(algo_staged)} ファイル")
    for f in algo_staged:
        print(f"  {f}")

    if not APPROVED_JSON.exists():
        _fail(
            "視覚レビューが未実施です。コミット前に以下を実行してください:\n"
            "\n"
            "  1. python tools/visual_review.py snapshot   # 変更前（すでに変更済みなら不要）\n"
            "  2. python tools/visual_review.py compare    # 比較パネル生成\n"
            "  3. 生成された PNG を Read ツールで 1 枚ずつ確認\n"
            "  4. python tools/visual_review.py approve    # 確認完了マーカー"
        )

    approved_data = json.loads(APPROVED_JSON.read_text(encoding="utf-8"))
    approved_at = datetime.fromisoformat(approved_data["approved_at"])

    # ステージされたアルゴリズムファイルの中で最新の mtime を取得
    newest_mtime: datetime | None = None
    newest_file: str = ""
    for f in algo_staged:
        mtime = get_file_mtime(f)
        if mtime is not None and (newest_mtime is None or mtime > newest_mtime):
            newest_mtime = mtime
            newest_file = f

    if newest_mtime is not None and approved_at < newest_mtime:
        _fail(
            f"視覚レビュー承認後にファイルが変更されました。再度 compare → approve を実行してください。\n"
            f"  最新変更ファイル : {newest_file}\n"
            f"  ファイル更新時刻 : {newest_mtime.strftime('%Y-%m-%d %H:%M:%S')}\n"
            f"  視覚レビュー承認 : {approved_at.strftime('%Y-%m-%d %H:%M:%S')}"
        )

    print(
        f"[pre-commit] 視覚レビュー承認確認済み "
        f"({approved_at.strftime('%Y-%m-%d %H:%M:%S')})"
    )
    sys.exit(0)


def _fail(message: str) -> None:
    print(f"\n[pre-commit] エラー: {message}\n", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
