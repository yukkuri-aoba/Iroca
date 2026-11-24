"""tools/remote_test_agent.py の、スロット間で共有するデータ（ハードリンク）の扱いを、一時フォルダの偽のスロットで確かめる。

共有したファイルへ別のスロットがデータを展開・削除しても、相手のスロットの中身が変わらないこと（2026-11-24）。
"""
from __future__ import annotations

import io
import os
import sys
import tarfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import remote_test_agent as agent  # noqa: E402


def _tar(files: dict[str, bytes], mtime: int = 1_700_000_000) -> io.BytesIO:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w") as tf:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mtime = mtime
            tf.addfile(info, io.BytesIO(data))
    buf.seek(0)
    return buf


def test_extract_into_a_shared_file_does_not_change_the_other_slot(tmp_path):
    """tar の展開は既存のファイルへその場で書く。共有しているファイルなら、先に名前を外して相手を守る。"""
    a, b = tmp_path / "s0", tmp_path / "s1"
    (a / "d").mkdir(parents=True)
    (b / "d").mkdir(parents=True)
    (a / "d" / "x.png").write_bytes(b"old")
    os.link(a / "d" / "x.png", b / "d" / "x.png")
    plain = b / "d" / "y.png"
    plain.write_bytes(b"plain-old")

    assert agent._extract_stream(_tar({"d/x.png": b"new-content", "d/y.png": b"plain-new"}), b) == 2

    assert (b / "d" / "x.png").read_bytes() == b"new-content"
    assert (a / "d" / "x.png").read_bytes() == b"old", "相手のスロットのファイルは変わらない"
    assert os.stat(a / "d" / "x.png").st_nlink == 1
    assert plain.read_bytes() == b"plain-new", "共有していないファイルは今までどおり書く"


def test_extract_rejects_names_outside_the_destination_before_touching_files(tmp_path):
    dest = tmp_path / "dest"
    outside = tmp_path / "outside.png"
    outside.write_bytes(b"keep")
    os.link(outside, tmp_path / "other_link.png")      # 外のファイルが共有されていても外さない
    with pytest.raises(tarfile.TarError):
        agent._extract_stream(_tar({"../outside.png": b"evil"}), dest)
    assert outside.read_bytes() == b"keep" and os.stat(outside).st_nlink == 2


def _fake_slots(tmp_path, monkeypatch, manifest: list[str]):
    base0, base1 = tmp_path / "Iroca", tmp_path / "Iroca_s1"
    monkeypatch.setattr(agent, "slot_paths", lambda n: (base0 if n == 0 else base1, tmp_path / f"lock{n}"))
    monkeypatch.setattr(agent, "SLOT", 1)
    monkeypatch.setattr(agent, "DEV_SAFE", base1 / "dev_safe")
    monkeypatch.setattr(agent, "data_manifest_of", lambda dev_safe, dirs: {rel: [0, 0] for rel in manifest})
    return base0 / "dev_safe", base1 / "dev_safe"


def _put(path: Path, data: bytes, mtime: int = 1_700_000_000) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    os.utime(path, (mtime, mtime))
    return path


def test_link_shared_data_links_only_identical_copies(tmp_path, monkeypatch):
    same, diff, newer, only1 = ("texture_sample/a/same.png", "texture_sample/a/diff.png",
                                "texture_sample/a/newer.png", "texture_sample/a/only1.png")
    d0, d1 = _fake_slots(tmp_path, monkeypatch, [same, diff, newer, only1])
    _put(d0 / same, b"same-bytes")
    _put(d1 / same, b"same-bytes")
    _put(d0 / diff, b"aaaa")
    _put(d1 / diff, b"bbbb")                     # 大きさ・日時が同じでも中身が違えばリンクしない
    _put(d0 / newer, b"v2", mtime=1_700_000_100)
    _put(d1 / newer, b"v1")
    _put(d1 / only1, b"x")

    n, size = agent._link_shared_data()
    assert (n, size) == (1, len(b"same-bytes"))
    assert os.path.samefile(d0 / same, d1 / same)
    for rel, want in ((diff, b"bbbb"), (newer, b"v1"), (only1, b"x")):
        assert (d1 / rel).read_bytes() == want
        assert os.stat(d1 / rel).st_nlink == 1
    assert agent._link_shared_data() == (0, 0), "リンク済みは何もしない"


def test_link_shared_data_does_nothing_in_slot_zero(monkeypatch):
    monkeypatch.setattr(agent, "SLOT", 0)
    assert agent._link_shared_data() == (0, 0)


def test_baselines_are_not_shared():
    assert "Tests/Baselines" not in agent.SHARED_DATA_DIRS
