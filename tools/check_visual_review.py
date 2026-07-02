"""pre-commit フックから呼ばれる視覚レビュー承認チェック + 出力品質ゲート検証。

フックの実体は scripts/hooks/pre-commit（有効化: git config core.hooksPath scripts/hooks）。

アルゴリズムファイル（Code/、ただし Debug/Tests 除く）がステージされているとき:
  1. approved.json が全ステージファイルより新しいか（視覚レビュー実施の確認）。
  2. 出力品質ゲートのしきい値較正が健全か（quality_report.py --validate が
     good/bad ラベルを分離できているか）。SKIP_QUALITY_GATE=1 でスキップ可。

【設計メモ】品質ゲート本体(test_recolor_quality_gate.py)はフル計測に数分かかるため
pre-commit では走らせない(コミットを重くしない)。代わりに既存 CSV を使った高速な
--validate(しきい値が現状の good/bad を分離し続けるか=baseline 罠/しきい値緩めの検知)を
回す。フルな品質ゲート(pytest test_recolor_quality_gate.py / test_csharp_quality_gate.py)は
改善サイクル内で実行する。テスト資産は dev_safe(gitignore)にありローカルでのみ動く。

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
    "Code/",
]

# 視覚レビュー不要の除外パス（ALGORITHM_PREFIXES より優先）
# デバッグツール・UI・テストなどアルゴリズム出力に影響しないファイル
ALGORITHM_EXCLUSION_PREFIXES = [
    "Code/Debug/",
    "Code/Tests/",
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
    if any(normalized.startswith(ex) for ex in ALGORITHM_EXCLUSION_PREFIXES):
        return False
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

    run_quality_gate_validate()
    sys.exit(0)


def run_quality_gate_validate() -> None:
    """出力品質ゲートのしきい値較正が健全か(--validate)を高速検証する。

    SKIP_QUALITY_GATE=1 でスキップ。quality_report.py / CSV が無い環境では警告のみ(通過)。
    """
    if os.environ.get("SKIP_QUALITY_GATE") == "1":
        print("[pre-commit] SKIP_QUALITY_GATE=1 のため品質ゲート検証をスキップします")
        return
    qr = ROOT / "dev_safe" / "Tests" / "regression" / "quality_report.py"
    if not qr.exists():
        print("[pre-commit] (品質ゲート未配置のためスキップ)")
        return
    res = subprocess.run(
        [sys.executable, str(qr), "--validate"],
        capture_output=True, encoding="utf-8", errors="replace",
    )
    if res.returncode != 0:
        _fail(
            "出力品質ゲートのしきい値較正が good/bad を分離できていません(--validate 失敗)。\n"
            "しきい値を緩めて破綻を通していないか quality_thresholds.py を見直してください。\n"
            "（このチェックは SKIP_QUALITY_GATE=1 で一時的に回避できます）\n\n"
            + (res.stdout or "") + (res.stderr or "")
        )
    print("[pre-commit] 品質ゲートしきい値較正 OK (good/bad を分離)")
    print("  ※ フルな品質ゲートは改善サイクルで: "
          "pytest dev_safe/Tests/regression/test_recolor_quality_gate.py")


def _fail(message: str) -> None:
    print(f"\n[pre-commit] エラー: {message}\n", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
