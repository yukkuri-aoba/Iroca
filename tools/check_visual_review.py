"""pre-commit フックから呼ばれる視覚レビュー承認チェック + 出力品質ゲート検証。

フックの実体は scripts/hooks/pre-commit（有効化: git config core.hooksPath scripts/hooks）。

範囲のファイル（tools/review_state.py の in_main_scope: headless ハーネスがコンパイルする製品ソース・
scripts/headless-run・tools の測定器）がステージされているとき、次の順で検査する:
  1. 視覚レビューの承認が有効か（workflow_review.validate_approval: 固定設定とワンショット/追加操作後の両方の承認が、
     同じ比較の実行と同じ中身を指し、回帰・欠けたケース・変わったパネルや入力資産が無いこと）。
  2. ステージしたファイルごとに、コミットされる中身（index の blob。GIT_INDEX_FILE が立っていればその index）が
     「HEAD と同じ」か「承認した比較で測った中身と同じ」か（ファイル単位。判断 17 の (b)）。hunk を分けて stage した
     ファイルはどちらとも違うので落ちる。承認の時刻とファイルの mtime は見ない（2026-11-10、点検 B-1）。
  3. dev_safe の測定器と基準（別のリポジトリ）が、承認した比較のときと同じ中身か（作業ツリーで見る）。
  4. 出力品質ゲートのしきい値較正が健全か（quality_report.py --validate が
     good/bad ラベルを分離できているか）。SKIP_QUALITY_GATE=1 でスキップ可。
     **保存済みの CSV を読むだけで、今のコードの出力は測らない**（通っても「品質ゲート OK」ではない）。

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

import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

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

HOWTO = (
    "  1. python tools/visual_review.py snapshot            # 変更前(既定で HEAD のコードから。HEAD が変更前なら後でもよい)\n"
    "  2. python tools/visual_review.py compare [--index]   # 今の中身を測る(--index ならステージした中身のコード)\n"
    "  3. compare が一覧に出した(変化があった)PNG を Read ツールで 1 枚ずつ確認\n"
    "  4. python tools/visual_review.py approve --note \"<目視した範囲と根拠>\"\n"
    "  確かめるだけなら: python tools/visual_review.py check --index"
)


def main() -> None:
    if os.environ.get("SKIP_VISUAL_REVIEW") == "1":
        print("[pre-commit] SKIP_VISUAL_REVIEW=1 のため視覚レビューチェックをスキップします")
        sys.exit(0)

    import review_state as rs
    scope = [f for f in rs.staged(ROOT) if rs.in_main_scope(f)]
    if not scope:
        sys.exit(0)  # 範囲のファイルなし → チェック不要

    print(f"[pre-commit] 視覚レビューの範囲のファイル(コード・ハーネス・測定器)の変更を検出: {len(scope)} ファイル")
    for f in scope:
        print(f"  {f}")

    import workflow_review
    try:
        doc = workflow_review.validate_approval(check_current=False)
    except (OSError, ValueError, KeyError, TypeError) as e:
        _fail(f"視覚レビューの承認が無いか、無効です: {e}\n{HOWTO}")

    bad = rs.check_staged(scope, doc["state"])
    if bad:
        _fail("コミットする中身が、承認した比較で測った中身と合いません(ファイルごとに「HEAD と同じ」か"
              "「測った中身と同じ」なら通します):\n  " + "\n  ".join(bad) + "\n"
              "ステージした中身を測り直すなら compare --index。\n" + HOWTO)
    dev = rs.diff(rs.dev_safe_state(), rs.dev_safe_part(doc["state"]))
    if dev:
        _fail("dev_safe の測定器・基準が、承認した比較のときと違います(測り方が変わったので測り直す):\n  "
              + "\n  ".join(dev) + "\n" + HOWTO)

    print(f"[pre-commit] 視覚レビュー承認確認済み(比較 {doc['run_id'][:8]}・測り元 {doc['state_source']}・"
          f"{doc['generated_at'][:16]}。ステージした {len(scope)} ファイルは HEAD か測った中身と同じ)")

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
    print("[pre-commit] 品質ゲートのしきい値の較正 OK（保存済みの CSV で good/bad を分離。"
          "今のコードの出力は測っていない）")
    print("  ※ 今のコードの品質は改善サイクルで測る: pytest dev_safe/Tests/regression/ -q"
          "（IROCA_CSHARP_GATE_FULL=1・IROCA_AUTOTUNE_GATE_FULL=1）")


def _fail(message: str) -> None:
    print(f"\n[pre-commit] エラー: {message}\n", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    main()
