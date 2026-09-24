"""scripts/unit-run（Unity 不要の部品のユニットテスト）を pytest から回す。

C# 側のランナーが 1 行 1 件で PASS / FAIL を出すので、件ごとの pytest テストに展開する。
ビルドに失敗したら fail（Code/Infra のコンパイルエラー＝製品退行）、dotnet / Unity DLL が
無いだけなら skip（CI は IROCA_REQUIRE_HARNESS=1 で fail にする。golden と同じ規則）。
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
sys.path.insert(0, str(REPO / "scripts" / "golden"))
import golden_lib as G  # noqa: E402  (dotnet / Unity DLL の検出と skip/fail 規則を共有する)

CSPROJ = HERE / "UnitRun.csproj"
DLL = HERE / "bin" / "Release" / "IrocaUnitRun.dll"


@pytest.fixture(scope="module")
def results() -> dict[str, str]:
    if not G.dotnet_available():
        G.env_missing("dotnet が利用できません")
    missing = G.unity_dll_missing()
    if missing:
        G.env_missing(f"Unity CoreModule DLL がありません: {missing}")
    b = subprocess.run(["dotnet", "build", str(CSPROJ), "-c", "Release", "-nologo", "-v", "quiet"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace",
                       timeout=G.BUILD_TIMEOUT_S)
    if b.returncode != 0 or not DLL.exists():
        pytest.fail("UnitRun のビルドに失敗（Code/Infra のコンパイルエラーの可能性）:\n"
                    + ((b.stdout or "") + (b.stderr or ""))[-1500:])
    r = subprocess.run(["dotnet", str(DLL)], capture_output=True, text=True, encoding="utf-8",
                       errors="replace", timeout=G.RUN_TIMEOUT_S)
    out: dict[str, str] = {}
    for line in r.stdout.splitlines():
        status, _, rest = line.partition(" ")
        name, _, reason = rest.partition(": ")
        if status in ("PASS", "FAIL"):
            out[name] = "" if status == "PASS" else (reason or "(理由なし)")
    assert out, f"UnitRun が結果を出しませんでした:\n{r.stdout}\n{r.stderr}"
    return out


def _names() -> list[str]:
    # 収集時に C# を走らせないよう、テスト名はソースから読む（1 メソッド = 1 件）。
    import re
    src = (HERE / "UnitRun.cs").read_text(encoding="utf-8")
    body = src[src.index("public static class Tests"):]
    return re.findall(r"public\s+static\s+void\s+(\w+)\s*\(\s*\)", body)


@pytest.mark.parametrize("name", _names())
def test_unit(results, name):
    assert name in results, f"{name} が実行されませんでした（ソースの読み取りとランナーの食い違い）"
    assert results[name] == "", f"{name}: {results[name]}"


def test_all_results_are_known(results):
    assert set(results) == set(_names()), (
        f"ランナーとソースの件数が一致しません: ランナーのみ {sorted(set(results) - set(_names()))}")
