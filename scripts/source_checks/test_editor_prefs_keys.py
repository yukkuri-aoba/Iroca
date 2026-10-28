"""EditorPrefs / SessionState のキーを、今の一覧から黙って変えないことを機械検査する。

0.4.0 の計画（docs/plan-0.4.0.md）の決まり 3「互換を壊さない」と 0-2。EditorPrefs は PC ごとの設定（言語・表示の
設定・閉じた案内など）を、SessionState はエディタを閉じるまでの状態を、キーの文字列で持つ。キーを変えると、利用者の
設定が黙って既定に戻り、閉じた案内がまた出る。0.4.0 の 4-1 でキーを `IrocaPrefs` にまとめるときも、文字列は変えない。

キーは `Iroca.` / `Iroca_` で始まる文字列リテラルとして書いてある（前置きにパスや GUID を足すものは、その前置き）。
EditorPrefs か SessionState を呼ぶファイルにある、そういうリテラルを集めて PREFS_KEYS と比べる。
  - 一覧にあるのに無くなったら落ちる（キーが消えた・変わった。変えるなら古いキーから読んで移す処理を書く）。
  - 一覧に無いキーが現れたら落ちる（新しいキーは一覧に足す）。
EditorPrefs / SessionState の呼び出しのキーに、`Iroca` で始まらないリテラルを直接書いても落ちる（集められないため）。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"

# 2026-10-27 のキー（前置きのものは末尾の区切りまで）。**消す・変えるときは移行を書く。**
PREFS_KEYS = {
    # EditorPrefs（PC ごと）
    "Iroca.Language",
    "Iroca.EnglishTranslationNoticeShown",
    "Iroca.ContextMenuUsed",
    "Iroca.SceneClickUsed",
    "Iroca.ShowDeveloperSettings",
    "Iroca.ShowMeshIslands",
    "Iroca.PreviewSmoothMagnify",
    "Iroca.PreviewDetailFullResolution",
    "Iroca.LastTextureGuid.",            # + プロジェクトのパスのハッシュ
    "Iroca.MaskBrushWindowPlaced:",      # + プロジェクトのパス
    "Iroca.AiSuggestPerfLog",
    "Iroca.UpdateCheck.Enabled",
    "Iroca.UpdateCheck.Dismissed",
    "Iroca.UpdateCheck.Latest:",         # + Unity の版（+ ":pre"）
    "Iroca.UpdateCheck.CheckedAtUtc:",   # + Unity の版（+ ":pre"）
    "Iroca.Debug.EnableCapture",
    "Iroca.Debug.MasterEnabled",
    "Iroca.Debug.Mode",
    "Iroca.Debug.SelectedStage",
    "Iroca.Debug.SelectedZone",
    "Iroca.Perf.ThreadOverride",
    "Iroca.ReproDump.LastDir",
    # SessionState（エディタを閉じるまで）
    "Iroca.UpdateCheck.Tried",
    "Iroca.WindowEnabled.",              # + ウィンドウの InstanceID
    "Iroca.RecipeSynced.",               # + レシピの GUID
    "Iroca.MaterialColor.Dismissed:",    # + テクスチャのパス
    "Iroca.LargeSource.Dismissed:",      # + テクスチャのパス
    "Iroca.AiSetup.BannerDismissed",
    "Iroca.Burst.CompileFailed",
    "Iroca.Sentis.RestartRecommended",
    "Iroca_MaskIndex_",                  # + テクスチャのパス（古い版のマスクの置き場。読んで移したら消す）
    "Iroca_Mask_",                       # + テクスチャのパス（同上）
}

_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/", re.DOTALL)
_STRING = re.compile(r"\"((?:\\.|[^\"\\\n])*)\"")
_PREFS_CALL = re.compile(r"\b(?:EditorPrefs|SessionState)\.(?:Get|Set|Has|Delete|Erase)\w*\s*\(\s*")


# キーの一覧(0.4.0 の 4-1 で各ファイルの定数をここへまとめた)。EditorPrefs / SessionState を呼ばないファイルだが、
# キーの文字列はここにあるので、呼び出しの有無によらず読む。
KEY_LIST = CODE_DIR / "Infra" / "IrocaPrefs.cs"


def keys_in(src: str, key_list: bool = False) -> tuple[set[str], list[str]]:
    """(キーに見えるリテラル, Iroca で始まらないリテラルを直接キーにした呼び出し)。
    key_list = True ならキーの一覧のファイルとして、呼び出しが無くてもリテラルを集める。"""
    code = _COMMENT.sub(" ", src)
    if not key_list and not _PREFS_CALL.search(code):
        return set(), []
    keys = {m.group(1) for m in _STRING.finditer(code) if re.match(r"Iroca[._]", m.group(1))}
    raw = [m.group(0) + code[m.end():m.end() + 40].split(")")[0]
           for m in _PREFS_CALL.finditer(code)
           if code[m.end()] == '"' and not re.match(r"\"Iroca[._]", code[m.end():])]
    return keys, raw


def _scan() -> tuple[set[str], list[str]]:
    keys: set[str] = set()
    raw: list[str] = []
    assert KEY_LIST.is_file(), f"キーの一覧がありません(移したならここも直す): {KEY_LIST}"
    for path in sorted(CODE_DIR.rglob("*.cs")):
        k, r = keys_in(path.read_text(encoding="utf-8"), key_list=path == KEY_LIST)
        keys |= k
        raw += [f"{path.relative_to(REPO_ROOT).as_posix()}: {x}" for x in r]
    return keys, raw


def test_detector():
    """見分け方が壊れて空一致で通ることがないよう、既知の入力で確かめる。"""
    src = (
        'const string A = "Iroca.A";\n'
        'string B(string p) => "Iroca_B_" + p;\n'
        '// const string C = "Iroca.Comment";\n'
        'void F() { EditorPrefs.GetBool(A, false); SessionState.SetString("other", ""); }\n'
    )
    keys, raw = keys_in(src)
    assert keys == {"Iroca.A", "Iroca_B_"}
    assert len(raw) == 1 and '"other"' in raw[0]
    assert keys_in('const string A = "Iroca.A";') == (set(), [])   # 呼び出しの無いファイルは見ない


def test_prefs_keys_unchanged():
    keys, _ = _scan()
    # 回帰の前提。読み方が壊れて 0 件になると、消えた側の一覧が全部出て原因が分かりにくい。
    assert len(keys) >= 20, f"キーの収集が異常です({len(keys)} 件)"
    lost, new = sorted(PREFS_KEYS - keys), sorted(keys - PREFS_KEYS)
    assert not lost, (
        f"EditorPrefs / SessionState のキーが消えた・変わりました: {lost}\n"
        "  利用者の設定が黙って既定に戻る。変えるなら古いキーから読んで移す処理を書き、一覧も直す。")
    assert not new, f"一覧に無いキーがあります。PREFS_KEYS に足すこと: {new}"


def test_prefs_calls_use_named_keys():
    _, raw = _scan()
    assert not raw, (
        "EditorPrefs / SessionState のキーに、Iroca で始まらないリテラルを直接書いています:\n  " + "\n  ".join(raw)
        + "\n  キーは `Iroca.` で始め、定数にする(この検査が集められるように)。")
