"""テスト・ハーネスだけが使う口を、製品のコードが呼んでいないことを機械検査する。

製品のアセンブリには、検証のために残してある口がある（bool[] のマスクの見本・配列を受ける版など）。製品はヒープの外の版・
1 画素 1 ビットの版を使い、これらを呼ばない。黙って呼ばれ始めると、

  1. 画素数の大きさの管理配列が Mono のヒープに積まれる（ヒープは縮まない）
  2. 見本と製品の 2 つの道の片方だけが直されて、結果が食い違う（見本は保存形式の版も、塗った範囲の記録も書かない）

ので、一覧の口が `Code/` から参照されたら落とす。口を足す・消すときは、ここと、その口の説明（「テスト・ハーネス用」）を
一緒に直す。名前で見分けられない多重定義（`MaskSnapshot.Pack(bool[])`・`MaskPaintView.ApplyRegion(bool[])`・
`SelectionCache.Store` / `TryGet`）は、ここでは見ない（説明に書いてあるだけ）。
AI 提案の純計算（`Code/MaskSuggest/Ops`）の配列を受ける版は、製品のアセンブリに置かない（ハーネスの
`scripts/headless-run/SamOps.Arrays.cs` が partial で足す）。製品から呼べばコンパイルが通らないので、ここでは見ない。

C# ソースを直接読むだけなのでハーネス不要・常時実行。
"""
from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODE_DIR = REPO_ROOT / "Code"

# 呼び出しの形（正規表現） → その形が出てよいファイル（定義そのものと、同じくテスト・ハーネス用の口の中身）。
TEST_ONLY_CALLS: dict[str, set[str]] = {
    # 保存形式 ⇔ bool[]（製品は MaskWorkspace.EncodeTo / DecodeFrom）
    r"\bMaskStateCodec\.Encode\(": set(),
    r"\bMaskStateCodec\.Decode\(": set(),
    # bool[] 一式 → スナップショット（製品は MaskWorkspace.ToSnapshot / MaskStateCodec.ToSnapshot）
    r"\bMaskSnapshot\.FromBuffers\(": set(),
    # bool[] の RLE（製品は EncodeBits / DecodeBits の系統）。MaskStateCodec の bool[] の口が中で使う
    r"\bMaskRle\.(?:Encode|Decode|EncodeIfAny)\(": {"Code/Core/MaskStateCodec.cs"},
    # bool[] → ヒープの外のマスク
    r"\bNativeBitMask\.FromBools\(": set(),
    r"\bNativeBools\.CopyOf\(": set(),
}

# 定義の形（その口がまだあることを確かめる。消したのに一覧が残ると、一覧が嘘になる）。
DEFINITIONS: dict[str, tuple[str, str]] = {
    r"\bMaskStateCodec\.Encode\(": ("Code/Core/MaskStateCodec.cs", r"public static void Encode\("),
    r"\bMaskStateCodec\.Decode\(": ("Code/Core/MaskStateCodec.cs", r"public static bool Decode\("),
    r"\bMaskSnapshot\.FromBuffers\(": ("Code/Core/MaskSnapshot.cs", r"public static MaskSnapshot FromBuffers\("),
    r"\bMaskRle\.(?:Encode|Decode|EncodeIfAny)\(": ("Code/Core/MaskRle.cs", r"public static string Encode\(bool\[\]"),
    r"\bNativeBitMask\.FromBools\(": ("Code/Core/NativeBitMask.cs", r"public static NativeBitMask FromBools\("),
    r"\bNativeBools\.CopyOf\(": ("Code/Core/NativeBools.cs", r"public static NativeBools CopyOf\(bool\[\]"),
}


def _code_lines(path: Path):
    """コメントを除いた行（行番号つき）。/// と // の行、行末の // 以降を落とす。文字列の中の // は気にしない
    （呼び出しの形が文字列に出ることは無い）。"""
    for no, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
        code = line.split("//", 1)[0]
        if code.strip():
            yield no, code


def test_test_only_api_is_not_called_from_product_code():
    hits = []
    for path in sorted(CODE_DIR.rglob("*.cs")):
        rel = path.relative_to(REPO_ROOT).as_posix()
        for no, code in _code_lines(path):
            for pattern, allowed in TEST_ONLY_CALLS.items():
                if rel not in allowed and re.search(pattern, code):
                    hits.append(f"{rel}:{no}: {code.strip()}")
    assert not hits, (
        "テスト・ハーネス用の口を、製品のコードが呼んでいます:\n  " + "\n  ".join(hits) + "\n"
        "→ 製品はヒープの外の版・1 画素 1 ビットの版を使ってください（各口の説明に、製品が使う版が書いてあります）。")


def test_listed_test_only_api_still_exists():
    missing = []
    for pattern, (rel, definition) in DEFINITIONS.items():
        src = (REPO_ROOT / rel).read_text(encoding="utf-8-sig")
        if not re.search(definition, src):
            missing.append(f"{rel}: {definition}")
    assert not missing, "一覧にあるテスト・ハーネス用の口の定義が見つかりません（消した・改名したなら一覧を直す）:\n  " + "\n  ".join(missing)
    assert set(TEST_ONLY_CALLS) == set(DEFINITIONS), "TEST_ONLY_CALLS と DEFINITIONS の鍵が食い違っています"
