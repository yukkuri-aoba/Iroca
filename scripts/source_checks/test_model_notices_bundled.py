"""パッケージに同梱したモデルのライセンス表示が、ダウンロードするものと同じ中身であることを検査する。

AI マスク提案のモデル(MobileSAM、Apache License 2.0)のライセンスの全文と NOTICE は 2 つの道で利用者に届く:

  1. モデルをダウンロードするとき、モデルと同じフォルダへ一緒に取る
     (Code/MaskSuggest/MaskSuggestModelDownload.cs の NoticeFiles。コミットを固定した URL と sha256・バイト数)
  2. モデルがあるのに置き場に無いとき(前の版で入れたモデル・手で置いたモデル)、パッケージに同梱した写し
     (Code/MaskSuggest/Licenses/)を置く(PlaceBundledNoticesIfMissing。通信しない)

2 の写しが 1 と違う中身だと、同じ名前で違う文書が出回る。ここでは同梱の写しの sha256 とバイト数を、NoticeFiles の値と
突き合わせる。改行を変えると合わなくなるので、.gitattributes で `-text`(変換しない)にしてあることも見る。

C# ソースと同梱ファイルを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import hashlib
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DOWNLOAD_CS = REPO_ROOT / "Code" / "MaskSuggest" / "MaskSuggestModelDownload.cs"
BUNDLED_REL = "Code/MaskSuggest/Licenses"
BUNDLED = REPO_ROOT / BUNDLED_REL


def _source() -> str:
    return DOWNLOAD_CS.read_text(encoding="utf-8-sig")


def _notice_table() -> list[tuple[str, str, int]]:
    """NoticeFiles の (置くときの名前, sha256, バイト数)。"""
    m = re.search(r"NoticeFiles\s*=\s*\{(.*?)\};", _source(), re.S)
    assert m, "MaskSuggestModelDownload.cs に NoticeFiles の表が見つからない"
    rows = re.findall(r'\(\s*"[^"]+"\s*,\s*"([^"]+)"\s*,\s*"([0-9a-fA-F]{64})"\s*,\s*(\d+)L\s*\)', m.group(1))
    assert rows, "NoticeFiles の行を (\"remote\", \"file\", \"sha256\", sizeL) の形で読めない"
    return [(name, sha.lower(), int(size)) for name, sha, size in rows]


def test_bundled_copies_are_byte_identical_to_the_downloads():
    for name, sha, size in _notice_table():
        path = BUNDLED / name
        assert path.is_file(), f"同梱の写しが無い: {BUNDLED_REL}/{name}"
        data = path.read_bytes()
        assert len(data) == size, (
            f"{BUNDLED_REL}/{name} が {len(data)} バイト(NoticeFiles は {size})。改行が変換されていないか"
            "(.gitattributes の -text)・中身を書き換えていないかを確かめる")
        assert hashlib.sha256(data).hexdigest() == sha, (
            f"{BUNDLED_REL}/{name} の sha256 が NoticeFiles と違う。モデルリポジトリの固定したコミットのものと同じ中身にする")


def test_bundled_folder_holds_exactly_the_notice_files():
    names = sorted(p.name for p in BUNDLED.iterdir() if p.is_file() and p.suffix != ".meta")
    assert names == sorted(name for name, _, _ in _notice_table()), (
        f"{BUNDLED_REL} の中身が NoticeFiles の名前と違う(足した・消したら両方をそろえる)")


def test_bundled_copies_are_kept_as_they_are_by_git():
    lines = (REPO_ROOT / ".gitattributes").read_text(encoding="utf-8").splitlines()
    assert f"{BUNDLED_REL}/*.txt -text" in [line.strip() for line in lines], (
        ".gitattributes に同梱の写しの `-text` が無い。改行が変換されると sha256 が合わなくなる")


def test_code_copies_from_the_bundled_folder():
    assert f'BundledNoticesFolder = "{BUNDLED_REL}"' in _source(), (
        "PlaceBundledNoticesIfMissing が読むフォルダ(BundledNoticesFolder)が、同梱の置き場と違う")
