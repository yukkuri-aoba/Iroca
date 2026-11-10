"""pre-commit フック(本体と dev_safe)から呼ばれる視覚レビュー承認チェック。

本体: scripts/hooks/pre-commit(有効化: git config core.hooksPath scripts/hooks)が引数なしで呼ぶ。
範囲のファイル（tools/review_state.py の in_main_scope: headless ハーネスがコンパイルする製品ソース・
scripts/headless-run・tools の測定器）がステージされているとき、次の順で検査する:
  1. 視覚レビューの承認が有効か（workflow_review.validate_approval: 固定設定とワンショット/追加操作後の両方の承認が、
     同じ比較の実行と同じ中身を指し、回帰・欠けたケース・変わったパネルや入力資産が無いこと）。
  2. ステージしたファイルごとに、コミットされる中身（index の blob。GIT_INDEX_FILE が立っていればその index）が
     「HEAD と同じ」か「承認した比較で測った中身と同じ」か（ファイル単位。判断 17 の (b)）。hunk を分けて stage した
     ファイルはどちらとも違うので落ちる。承認の時刻とファイルの mtime は見ない（2026-11-10、点検 B-1）。
  3. dev_safe の測定器と基準（別のリポジトリ）が、承認した比較のときと同じ中身か（作業ツリーで見る）。
  本体のフックは今のコードの品質を測らない（品質ゲートは改善サイクルで pytest を回して測る）。

dev_safe: dev_safe/hooks/pre-commit(有効化: dev_safe で git config core.hooksPath hooks)が --dev-safe で呼ぶ(点検 D-4)。
  1. 視覚レビューの state に入る測定器・基準(review_state.DEV_SAFE_FILES)がステージされていれば、承認が有効で、
     ステージした中身がファイルごとに「dev_safe の HEAD と同じ」か「承認で測った中身と同じ」で、本体の測るコードと
     測定器が承認のときのまま(承認の測り元: 作業ツリーか index)であること。ベースラインの凍結にも承認が要る。
  2. 品質ゲートのしきい値・ラベル・CSV(QUALITY_FILES)をステージしたら、quality_report.py --validate で good/bad の分離を
     確かめる(以前は本体のフックが毎回回していたが、今のコードと関係が無く「品質ゲート OK」と読み違えられた)。
     SKIP_QUALITY_GATE=1 でスキップ可。

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

# 本体のルート(dev_safe のフックから呼ばれても、本体のルートを指す)
ROOT = Path(__file__).resolve().parents[1]
DEV_SAFE = ROOT / "dev_safe"
# dev_safe の中の、品質ゲートのしきい値の較正(quality_report.py --validate)に効くファイル
QUALITY_FILES = (
    "Tests/regression/quality_thresholds.py", "Tests/regression/quality_labels.json",
    "Tests/regression/quality_report.py", "Tests/Baselines/quality_report.csv",
)

HOWTO = (
    "  1. python tools/visual_review.py snapshot            # 変更前(既定で HEAD のコードから。HEAD が変更前なら後でもよい)\n"
    "  2. python tools/visual_review.py compare [--index]   # 今の中身を測る(--index ならステージした中身のコード)\n"
    "  3. compare が一覧に出した(変化があった)PNG を Read ツールで 1 枚ずつ確認\n"
    "  4. python tools/visual_review.py approve --note \"<目視した範囲と根拠>\"\n"
    "  確かめるだけなら: python tools/visual_review.py check --index"
)


def _approval():
    import workflow_review
    try:
        return workflow_review.validate_approval(check_current=False)
    except (OSError, ValueError, KeyError, TypeError) as e:
        _fail(f"視覚レビューの承認が無いか、無効です: {e}\n{HOWTO}")


def main() -> None:
    if os.environ.get("SKIP_VISUAL_REVIEW") == "1":
        print("[pre-commit] SKIP_VISUAL_REVIEW=1 のため視覚レビューチェックをスキップします")
        sys.exit(0)
    if not (ROOT / ".git").exists():
        sys.exit(0)  # 本体のリポジトリの外(tools だけを写した所など)では無条件通過

    import review_state as rs
    scope = [f for f in rs.staged(ROOT) if rs.in_main_scope(f)]
    if not scope:
        sys.exit(0)  # 範囲のファイルなし → チェック不要

    print(f"[pre-commit] 視覚レビューの範囲のファイル(コード・ハーネス・測定器)の変更を検出: {len(scope)} ファイル")
    for f in scope:
        print(f"  {f}")

    doc = _approval()
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
    print("  ※ このフックは今のコードの品質を測りません。品質は改善サイクルで測る: pytest dev_safe/Tests/regression/ -q"
          "(IROCA_CSHARP_GATE_FULL=1・IROCA_AUTOTUNE_GATE_FULL=1)")
    sys.exit(0)


def main_dev_safe() -> None:
    """dev_safe のフック(--dev-safe)。"""
    if os.environ.get("SKIP_VISUAL_REVIEW") == "1":
        print("[pre-commit] SKIP_VISUAL_REVIEW=1 のため視覚レビューチェックをスキップします")
        sys.exit(0)
    import review_state as rs
    staged = rs.staged(DEV_SAFE)
    if any(f in QUALITY_FILES for f in staged):
        run_quality_gate_validate()
    scope = [f for f in staged if rs.in_dev_safe_scope(f)]
    if not scope:
        sys.exit(0)

    print(f"[pre-commit] 視覚レビューの state に入る dev_safe の測定器・基準の変更を検出: {len(scope)} ファイル")
    for f in scope:
        print(f"  {f}")
    doc = _approval()
    bad = rs.check_staged(scope, doc["state"], repo=DEV_SAFE, prefix="dev_safe/")
    if bad:
        _fail("コミットする中身が、承認した比較で測った中身と合いません(ファイルごとに「HEAD と同じ」か"
              "「測った中身と同じ」なら通します):\n  " + "\n  ".join(bad) + "\n" + HOWTO)
    main_now = rs.main_part(rs.state(doc["state_source"]))
    changed = rs.diff(main_now, rs.main_part(doc["state"]))
    if changed:
        _fail(f"本体の測るコード・測定器({doc['state_source']})が、承認した比較のときと違います:\n  "
              + "\n  ".join(changed[:20]) + "\n" + HOWTO)
    print(f"[pre-commit] 視覚レビュー承認確認済み(比較 {doc['run_id'][:8]}。ステージした {len(scope)} ファイルは "
          "dev_safe の HEAD か測った中身と同じ)")
    sys.exit(0)


def run_quality_gate_validate() -> None:
    """出力品質ゲートのしきい値較正が健全か(--validate)を高速検証する(dev_safe のフックが、しきい値・ラベル・CSV を
    ステージしたときだけ呼ぶ)。

    保存済みの CSV を読むだけで、今のコードの出力は測らない。SKIP_QUALITY_GATE=1 でスキップ。
    quality_report.py が無い環境では何もしない(通過)。
    """
    if os.environ.get("SKIP_QUALITY_GATE") == "1":
        print("[pre-commit] SKIP_QUALITY_GATE=1 のため品質ゲート検証をスキップします")
        return
    qr = DEV_SAFE / "Tests" / "regression" / "quality_report.py"
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


def _fail(message: str) -> None:
    print(f"\n[pre-commit] エラー: {message}\n", file=sys.stderr)
    sys.exit(1)


if __name__ == "__main__":
    if "--dev-safe" in sys.argv[1:]:
        main_dev_safe()
    else:
        main()
