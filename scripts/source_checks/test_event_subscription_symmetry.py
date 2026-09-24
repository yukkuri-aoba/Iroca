"""ドメイン寿命イベントへの購読に、対になる解除があることを機械検査する。

`EditorApplication.update` / `AssemblyReloadEvents.*` / `Undo.undoRedoPerformed` や
AI マスク提案サービスの `StateChanged` は、**発行側がウィンドウより長生きする**
(static またはエディタ本体が保持する)。購読したまま購読者を捨てると、

  1. 捨てたはずのオブジェクトがイベント経由で生き続ける(GC されない)
  2. 破棄済み EditorWindow を触って MissingReferenceException を出す
     (Unity の fake-null は `?.` をすり抜けるので null 条件演算子では防げない)
  3. **再オープン後に新旧 2 購読者が並び、先に登録された旧側が先に処理してしまう**

の 3 つが起きる。実際 `MaskSuggestController` は 3 に該当し、旧コントローラが
`TryTakeProposal` で提案を先取りして捨てるため「クリックしても時々何も起きない」
という再現しにくい不具合になっていた(レビュー 2026-08-06 の N-4)。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"

# 発行側がウィンドウ/購読者より長生きするイベント名。ここに載ったものだけを検査する
# (`+=` は数値加算にも使われるため、全 `+=` を機械的に見ると偽陽性だらけになる)。
LONG_LIVED_EVENTS = {
    "update",                  # EditorApplication.update
    "undoRedoPerformed",       # Undo.undoRedoPerformed
    "beforeAssemblyReload",    # AssemblyReloadEvents
    "afterAssemblyReload",     # AssemblyReloadEvents
    "playModeStateChanged",    # EditorApplication
    "projectChanged",          # EditorApplication
    "hierarchyChanged",        # EditorApplication
    "selectionChanged",        # Selection / EditorApplication
    "StateChanged",            # IMaskSuggestService(ドメイン寿命の静的保持)
}

# 解除が不要と判断した購読。**理由を必ず書くこと**(黙って足すと本テストの意味が消える)。
ALLOWED_WITHOUT_UNSUBSCRIBE = {
    # 購読者自身がドメイン寿命の singleton で、ハンドラの仕事が「ドメイン破棄の直前に
    # ネイティブ資源を解放する」こと。ドメインと同時に消えるので解除の意味がない。
    ("Code/SentisIntegration/SentisMaskSuggestService.cs",
     "beforeAssemblyReload", "DisposeAll"),
}

# 行コメント / ブロックコメント / 文字列リテラルを落とす(コメント中の += を拾わない)。
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/|\"(?:\\.|[^\"\\])*\"", re.DOTALL)
# <なにか>.<イベント名> += <ハンドラ名>;  /  -= <ハンドラ名>;
_SUB = re.compile(r"\.(\w+)\s*(\+=|-=)\s*([\w.]+)\s*;")


def _iter_subscriptions():
    """(rel_path, event, op, handler) を Code/ 配下から列挙する。"""
    for path in sorted(CODE_DIR.rglob("*.cs")):
        src = _COMMENT.sub(" ", path.read_text(encoding="utf-8"))
        rel = path.relative_to(REPO_ROOT).as_posix()
        for event, op, handler in _SUB.findall(src):
            if event in LONG_LIVED_EVENTS:
                yield rel, event, op, handler


@pytest.fixture(scope="module")
def subscriptions():
    subs = list(_iter_subscriptions())
    # 回帰の前提。正規表現が壊れて 0 件になると空一致で全 assert が通ってしまう。
    assert len(subs) >= 10, f"購読の抽出が異常です({len(subs)} 件): {subs}"
    return subs


def test_every_long_lived_subscription_has_an_unsubscribe(subscriptions):
    """`+=` したハンドラは同じファイル内で `-=` もされていること。"""
    removed = {(f, e, h) for f, e, op, h in subscriptions if op == "-="}
    offenders = sorted({
        (f, e, h)
        for f, e, op, h in subscriptions
        if op == "+=" and (f, e, h) not in removed
        and (f, e, h) not in ALLOWED_WITHOUT_UNSUBSCRIBE
    })
    assert not offenders, (
        "解除されない購読があります(ファイル, イベント, ハンドラ):\n"
        + "\n".join(f"  {f}: {e} += {h}" for f, e, h in offenders)
        + "\n  発行側は購読者より長生きするため、解除しないと購読者が生き残り、"
        "\n  再オープン後に新旧 2 購読者が並んで旧側がイベントを先取りする。"
        "\n  解除が本当に不要なら ALLOWED_WITHOUT_UNSUBSCRIBE へ理由付きで載せること。")


def test_allowlist_entries_still_exist(subscriptions):
    """許可リストの陳腐化を検出する(該当コードが消えたら外す)。"""
    present = {(f, e, h) for f, e, op, h in subscriptions if op == "+="}
    stale = sorted(ALLOWED_WITHOUT_UNSUBSCRIBE - present)
    assert not stale, (
        f"ALLOWED_WITHOUT_UNSUBSCRIBE に実在しない項目があります: {stale}\n"
        "  該当の購読が消えた/変わったので、リストからも外すこと。")
