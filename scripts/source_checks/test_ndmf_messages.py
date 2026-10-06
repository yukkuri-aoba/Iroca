"""NDMF のエラー画面に出す文言（NdmfMessages）が、日英そろって全キーぶんあることを検査する。

Code/NdmfIntegration/NdmfMessages.cs は、Classify が返すキーごとに、NDMF の規約どおり
タイトル = key、説明 = key:description、対処 = key:hint の 3 項目を Ja / En の辞書に持つ。
NDMF は訳が無いと生のキーをそのまま表示する。ビルド時にしか出ない画面なので、問題の種類を
増やして片側の辞書だけに足したり、:hint を書き忘れたりしても目視では気づきにくい。ここで次を機械検査する:

  1. Ja と En のキー集合が、Classify の各キー × {本文, :description, :hint} とちょうど一致する
  2. 各項目の {0} {1} などのプレースホルダの集合が日英で一致する

NDMF のアセンブリは不要で、C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MESSAGES_CS = REPO_ROOT / "Code" / "NdmfIntegration" / "NdmfMessages.cs"

SUFFIXES = ("", ":description", ":hint")

_NDMF_NOTE = "NDMF は訳が無いと生のキーを表示する。ビルド時にしか出ない画面なので目視で気づきにくい。"

# 文字列リテラル（逐語的・通常・文字）とコメント。文字列は中身を使うので残し、コメントだけ落とす。
_STRING = r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])+\''
_TOKEN = re.compile(rf"{_STRING}|//[^\n]*|/\*.*?\*/", re.DOTALL)
_ITEM = re.compile(r'\["([^"]+)"\]\s*=\s*"((?:[^"\\]|\\.)*)"')


def _src() -> str:
    text = MESSAGES_CS.read_text(encoding="utf-8")
    return _TOKEN.sub(lambda m: m.group(0) if m.group(0)[0] in "@\"'" else "", text)


def _block_after(src: str, pattern: str, what: str) -> str:
    """pattern（末尾が {）の直後から対応する } までを返す。文字列中の波括弧（{0} など）は数えない。"""
    m = re.search(pattern, src)
    assert m, f"{what} が見つかりません: {pattern}"
    masked = re.sub(_STRING, lambda s: " " * len(s.group(0)), src)
    start = m.end() - 1
    assert masked[start] == "{", f"{what} の開き波括弧を特定できません"
    depth, i = 1, start + 1
    while depth:
        depth += (masked[i] == "{") - (masked[i] == "}")
        i += 1
    return src[start + 1:i - 1]


def _dictionary(name: str) -> list[tuple[str, str]]:
    body = _block_after(
        _src(),
        rf"Dictionary<string,\s*string>\s+{name}\s*=\s*new\s+Dictionary<string,\s*string>\s*(?:\(\s*\))?\s*\{{",
        f"辞書 {name}")
    return _ITEM.findall(body)


def _classify_keys() -> set[str]:
    # 宣言だけに当てる（Report 内の呼び出し `Classify(problem, failure);` は直後が { でない）。
    body = _block_after(_src(), r"\bClassify\s*\([^)]*\)\s*\{", "Classify")
    return set(re.findall(r'"(iroca\.[\w.]+)"', body))


def _placeholders(text: str) -> set[str]:
    return set(re.findall(r"\{\d+\}", text))


def test_extraction_is_not_empty():
    """抽出が空になると以降の一致検査が空集合どうしで通ってしまうので、件数の下限を先に見る。"""
    keys = _classify_keys()
    assert len(keys) >= 5, f"Classify が返すキーを読めていません: {sorted(keys)}"
    for name in ("Ja", "En"):
        items = _dictionary(name)
        assert len(items) >= 15, f"辞書 {name} の項目を読めていません（{len(items)} 件）"


def test_no_duplicate_entries():
    """辞書の初期化子は [key] = 値 の代入なので、同じキーを 2 回書くと後勝ちで前の文言が黙って消える。"""
    for name in ("Ja", "En"):
        keys = [k for k, _ in _dictionary(name)]
        dup = sorted({k for k in keys if keys.count(k) > 1})
        assert not dup, f"辞書 {name} に同じキーが複数あります: {dup}"


def test_every_key_has_all_entries_in_both_languages():
    need = {k + s for k in _classify_keys() for s in SUFFIXES}
    for name in ("Ja", "En"):
        have = {k for k, _ in _dictionary(name)}
        assert have == need, (
            f"辞書 {name} のキーが Classify の各キー × {{本文, :description, :hint}} と一致しません。\n"
            f"  {name} に無い: {sorted(need - have)}\n"
            f"  Classify から届かない余分な項目: {sorted(have - need)}\n  " + _NDMF_NOTE)


def test_placeholders_match_between_languages():
    ja = dict(_dictionary("Ja"))
    en = dict(_dictionary("En"))
    bad = {k: (sorted(_placeholders(ja[k])), sorted(_placeholders(en[k])))
           for k in ja.keys() & en.keys() if _placeholders(ja[k]) != _placeholders(en[k])}
    assert not bad, (
        f"日英でプレースホルダ（{{0}} {{1}} など）がそろっていない項目（日, 英）: {bad}\n"
        "  片方だけコンポーネント名やレシピ名が抜ける。")
