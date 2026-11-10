"""仕上がりの版（`Code/Infra/RecipeOutputVersion.cs` の `Current`）の上げ忘れを防ぐ（点検 D-1 段階 1）。

いろかの計算が変わると、同じレシピでも仕上がりが変わる（登録済みのアバターの色が次の再生・アップロードから黙って
変わる）。レシピは「どの版の仕上がりで確かめたか」を持ち、`Current` より古ければ知らせる。`Current` を上げ忘れると
知らせられないので、golden（C# の出力ハッシュ）に「どの版の仕上がりか」を `outputVersion` として持たせ、次を見る:

  - golden_hashes.json の outputVersion が C# の Current と同じ（片方だけ上げたら落ちる）。
  - 版ごとの最初のリリースの表（FirstRelease）が 0〜Current の全部の版を持つ（Current だけ上げて表に足し忘れると
    「0.4.0 から」のような表示が範囲の外を引く）。
  - golden を作り直す（golden_lib.py --force）ときに outputVersion を保つ（ハッシュが変わっても勝手に上げない・消さない）。

リリースの前に「前のタグから golden のハッシュが変わったのに outputVersion が同じ」なら、Current と outputVersion を
一緒に上げる（docs/RELEASING.md の手順 1）。C# ソースと JSON を読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import inspect
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_DIR = REPO_ROOT / "scripts" / "golden"
VERSION_CS = REPO_ROOT / "Code" / "Infra" / "RecipeOutputVersion.cs"
GOLDEN_JSON = GOLDEN_DIR / "golden_hashes.json"

if str(GOLDEN_DIR) not in sys.path:
    sys.path.insert(0, str(GOLDEN_DIR))
import golden_lib as G  # noqa: E402

_FIRST_RELEASE = re.compile(r"\bFirstRelease\s*=\s*(?:new\s+string\s*\[\s*\]\s*)?\{([^}]*)\}")


def _current() -> int:
    return G.csharp_output_version()


def test_golden_output_version_matches_current():
    doc = json.loads(GOLDEN_JSON.read_text(encoding="utf-8"))
    assert G.OUTPUT_VERSION_KEY in doc, (
        f"{GOLDEN_JSON.name} に {G.OUTPUT_VERSION_KEY} が無い（golden のハッシュがどの仕上がりの版かを持たせる）")
    golden = doc[G.OUTPUT_VERSION_KEY]
    current = _current()
    assert golden == current, (
        f"golden_hashes.json の {G.OUTPUT_VERSION_KEY}={golden} と RecipeOutputVersion.Current={current} が違う。"
        "仕上がりの版を上げるときは両方を同じ数にする（docs/RELEASING.md の手順 1）")


def test_first_release_table_covers_every_version():
    src = VERSION_CS.read_text(encoding="utf-8")
    m = _FIRST_RELEASE.search(src)
    assert m, f"{VERSION_CS.name} に FirstRelease の表が見つからない"
    entries = [e.strip() for e in m.group(1).split(",") if e.strip()]
    current = _current()
    assert entries and entries[0] == "null", f"FirstRelease の先頭（版 0 = 印の無い古いレシピ）は null: {entries}"
    assert len(entries) == current + 1, (
        f"FirstRelease の表は版 0〜{current} の {current + 1} 個が要るが {len(entries)} 個: {entries}。"
        "Current を上げたら、その版になった最初のリリースを表に足す")
    assert all(re.fullmatch(r'"\d+\.\d+\.\d+"', e) for e in entries[1:]), f"版の最初のリリースは \"x.y.z\": {entries}"


def test_regenerate_keeps_output_version(tmp_path):
    cases = {"b": {"sha256": "2", "shape": [1, 1, 4]}, "a": {"sha256": "1", "shape": [1, 1, 4]}}
    prev = tmp_path / "golden_hashes.json"
    prev.write_text(json.dumps({"_meta": {}, G.OUTPUT_VERSION_KEY: 7, "cases": {}}), encoding="utf-8")
    doc = G.golden_doc(cases, prev)
    assert doc[G.OUTPUT_VERSION_KEY] == 7, "作り直しは今の outputVersion を保つ（ハッシュが変わっても勝手に変えない）"
    assert list(doc["cases"]) == ["a", "b"]
    # 欄の無い古いファイル・ファイルが無いときは、C# の Current から始める
    prev.write_text(json.dumps({"_meta": {}, "cases": {}}), encoding="utf-8")
    assert G.golden_doc(cases, prev)[G.OUTPUT_VERSION_KEY] == _current()
    assert G.golden_doc(cases, tmp_path / "none.json")[G.OUTPUT_VERSION_KEY] == _current()
    # 作り直し（regenerate）が書く中身は golden_doc から作る（手書きの辞書へ戻すと outputVersion が落ちる）
    assert "golden_doc(" in inspect.getsource(G.regenerate), "regenerate が golden_doc を通していない"
