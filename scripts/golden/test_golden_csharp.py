"""C# 自己ゴールデン回帰テスト。

製品の唯一の正である C# 実装（PixelProcessor）の出力が、合成入力に対して黙って変化していないかを
検出する。Python 参照実装（dev_safe, 使い捨て）には依存しない。必要なのは dotnet + Unity
CoreModule DLL のみで、無ければ skip する。

  実行:        pytest scripts/golden/test_golden_csharp.py -q
  ゴールデン更新: python scripts/golden/golden_lib.py --force   （意図的変更・toolchain 更新時のみ）

ゴールデンは toolchain（Unity 2022.3.22f1 / dotnet ランタイム）に紐づくため、別環境ではハッシュが
変わりうる。主用途はローカル/開発者環境でのリグレッション検出（C# が改善サイクルの reset 等で
段を黙って失うのを防ぐ）。
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

# scripts/golden を import パスに（golden_lib / synth_textures は同ディレクトリ）。
sys.path.insert(0, str(Path(__file__).resolve().parent))

import golden_lib as G  # noqa: E402

_GOLDEN = G.load_golden()
_CASES = G.build_cases()


@pytest.fixture(scope="module")
def harness():
    # skip は「環境不備」だけに限定する。環境が揃っているのにビルドが失敗するのは
    # Code/ のコンパイルエラー（＝製品退行そのもの）なので fail で顕在化させる。
    if not G.dotnet_available():
        pytest.skip("dotnet が利用できません")
    missing = G.unity_dll_missing()
    if missing:
        pytest.skip(f"Unity CoreModule DLL がありません: {missing}")
    ok, r = G.build_harness()
    if not ok:
        pytest.fail(
            "Harness ビルド失敗。dotnet と Unity DLL は存在するため、Code/ の"
            "コンパイルエラーの可能性が高い（サイレント skip にしない）:\n"
            f"{((r.stdout or '') + (r.stderr or ''))[-1500:]}")
    if not _GOLDEN:
        pytest.skip("golden_hashes.json が未生成です。`python scripts/golden/golden_lib.py` で生成してください")
    return True


@pytest.mark.parametrize("label,rgba,zone,settings",
                         [pytest.param(*c, id=c[0]) for c in _CASES])
def test_golden_matches(label, rgba, zone, settings, harness, tmp_path):
    out = G.run_csharp(rgba, zone, settings, tmp_path)
    cur = G.output_hash(out)
    assert label in _GOLDEN, (
        f"{label}: golden 未登録。新規ケースなら `python scripts/golden/golden_lib.py` で再生成してください")
    g = _GOLDEN[label]
    assert list(out.shape) == g["shape"], (
        f"{label}: 出力 shape が変化 golden={g['shape']} current={list(out.shape)}")
    assert cur == g["sha256"], (
        f"{label}: C# 出力が golden と不一致（C# の挙動が変化しています）。\n"
        f"  golden ={g['sha256'][:16]}…\n  current={cur[:16]}… shape={list(out.shape)}\n"
        f"  → 意図的なアルゴリズム変更なら `python scripts/golden/golden_lib.py --force` で再生成、\n"
        f"     想定外なら C# のリグレッション（段の欠落・定数ずれ等）を調査してください。")


def test_output_is_deterministic(harness, tmp_path):
    """C# 出力が同一入力で 2 回連続実行してバイト一致する（ゴールデン照合の前提）。"""
    label, rgba, zone, settings = _CASES[0]
    a = G.run_csharp(rgba, zone, settings, tmp_path / "a")
    b = G.run_csharp(rgba, zone, settings, tmp_path / "b")
    assert G.output_hash(a) == G.output_hash(b), f"{label}: C# 出力が非決定的（並列処理の順序依存?）"
