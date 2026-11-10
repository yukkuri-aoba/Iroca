"""ハーネスのビルドの口(tools/harness_build.py)の規則を、dotnet を使わずに確かめる。

- ソースの中身が前回と違うのに DLL が同じなら、CoreCompile が飛んだとみなして --no-incremental で作り直す(点検 B-7②)。
  mtime を保ったままのコピー(Copy-Item・unzip)では MSBuild が入力を古いと判断せず、古い DLL のまま「全ケース一致」になった。
- ビルドの前後でソースの中身が食い違ったら作り直し、返す来歴はビルドに使った中身にする(点検 B-7①)。
- csproj と Import した props の Compile Include を MSBuild と同じ基準のフォルダから読む。
"""
from __future__ import annotations

import hashlib
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import harness_build as hb  # noqa: E402


def _fake_tree(root: Path) -> None:
    head = root / "scripts" / "headless-run"
    (head / "sub").mkdir(parents=True)
    (root / "Code" / "Core" / "Deep").mkdir(parents=True)
    (head / "Harness.csproj").write_text(
        '<Project><Import Project="Srcs.props" /><ItemGroup><Compile Include="Harness.cs" /></ItemGroup></Project>',
        encoding="utf-8")
    (head / "Srcs.props").write_text(
        '<Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)..\\..\\Code\\Core\\**\\*.cs" />'
        '</ItemGroup></Project>', encoding="utf-8")
    (head / "Harness.cs").write_text("class H {}", encoding="utf-8")
    (root / "Code" / "Core" / "A.cs").write_text("class A {}", encoding="utf-8")
    (root / "Code" / "Core" / "Deep" / "B.cs").write_text("class B {}", encoding="utf-8")


def test_project_inputs_follow_imports_and_globs(tmp_path):
    _fake_tree(tmp_path)
    compiles, projects = hb.project_inputs(tmp_path / hb.HARNESS_CSPROJ)
    rel = sorted(p.relative_to(tmp_path).as_posix() for p in compiles)
    assert rel == ["Code/Core/A.cs", "Code/Core/Deep/B.cs", "scripts/headless-run/Harness.cs"]
    assert sorted(p.name for p in projects) == ["Harness.csproj", "Srcs.props"]


class _FakeDotnet:
    """CoreCompile を飛ばす MSBuild の真似: --no-incremental でないときは、DLL があれば作り直さない。"""

    def __init__(self, root: Path):
        self.root = root
        self.calls: list[bool] = []

    def __call__(self, csproj: Path, no_incremental: bool = False):
        self.calls.append(no_incremental)
        dll = self.root / hb.HARNESS_DLL
        if no_incremental or not dll.exists():
            compiles, _ = hb.project_inputs(csproj)
            h = hashlib.sha256()
            for p in compiles:
                h.update(p.read_bytes())
            dll.parent.mkdir(parents=True, exist_ok=True)
            dll.write_bytes(h.digest())
        return subprocess.CompletedProcess([], 0, stdout="", stderr="")


def _sources(root: Path) -> dict:
    compiles, _ = hb.project_inputs(root / hb.HARNESS_CSPROJ)
    return {p.relative_to(root).as_posix(): hashlib.sha1(p.read_bytes()).hexdigest() for p in compiles}


def test_stale_dll_after_mtime_preserving_copy_is_rebuilt(tmp_path, monkeypatch):
    _fake_tree(tmp_path)
    fake = _FakeDotnet(tmp_path)
    monkeypatch.setattr(hb, "_dotnet_build", fake)
    hb._build_checked(tmp_path, hb.HARNESS_CSPROJ, hb.HARNESS_DLL, _sources(tmp_path))
    first = (tmp_path / hb.HARNESS_DLL).read_bytes()
    assert fake.calls == [False]

    # 中身を変える(mtime を保ったコピーの真似: 偽の dotnet は増分ビルドでは作り直さない)
    (tmp_path / "Code" / "Core" / "A.cs").write_text("class A { int x; }", encoding="utf-8")
    hb._build_checked(tmp_path, hb.HARNESS_CSPROJ, hb.HARNESS_DLL, _sources(tmp_path))
    assert fake.calls == [False, False, True], "ソースが変わったのに DLL が同じなら --no-incremental で作り直す"
    assert (tmp_path / hb.HARNESS_DLL).read_bytes() != first

    # 変えていなければ作り直さない
    hb._build_checked(tmp_path, hb.HARNESS_CSPROJ, hb.HARNESS_DLL, _sources(tmp_path))
    assert fake.calls == [False, False, True, False]


def test_sources_edited_during_build_cause_rebuild(tmp_path, monkeypatch):
    _fake_tree(tmp_path)
    fake = _FakeDotnet(tmp_path)
    monkeypatch.setattr(hb, "_dotnet_build", fake)
    monkeypatch.setattr(hb, "LOCK", tmp_path / "build.lock")
    reads = iter([{"a": "1"}, {"a": "2"}, {"a": "2"}, {"a": "2"}])
    info = hb._build_in(tmp_path, "worktree", lambda: next(reads), with_samops=False)
    assert info.sources == {"a": "2"}, "前後が一致したビルドの来歴を返す"
    assert len(fake.calls) == 2

    always_changing = iter(range(100))
    with pytest.raises(hb.BuildFailed):
        hb._build_in(tmp_path, "worktree", lambda: {"a": next(always_changing)}, with_samops=False)


def test_snapshot_is_content_addressed_and_keeps_provenance(tmp_path):
    out = tmp_path / "bin"
    out.mkdir()
    (out / "IrocaHeadless.dll").write_bytes(b"dll v1")
    (out / "UnityEngine.CoreModule.dll").write_bytes(b"unity")
    (out / "UnityEngine.CoreModule.xml").write_bytes(b"docs")
    info = hb.BuildInfo(dll=str(out / "IrocaHeadless.dll"), dll_sha256=hb.file_sha256(out / "IrocaHeadless.dll"),
                        source="worktree", sources={"x.cs": "abc"}, samops_digest="samops:1:2")
    snap = hb.snapshot(info, tmp_path / "snap")
    dst = Path(snap.dll)
    assert dst.parent.name == info.dll_sha256[:16]
    assert (dst.parent / "UnityEngine.CoreModule.dll").is_file()
    assert not (dst.parent / "UnityEngine.CoreModule.xml").exists(), "実行に要らない xml は写さない"
    assert hb.BuildInfo.load(dst.parent / hb.PROVENANCE).sources == {"x.cs": "abc"}
    # 元の DLL を作り直しても、写した方は変わらない
    (out / "IrocaHeadless.dll").write_bytes(b"dll v2")
    assert dst.read_bytes() == b"dll v1"
