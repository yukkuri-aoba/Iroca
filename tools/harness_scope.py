"""視覚レビューゲートの対象範囲（＝ headless ハーネスがコンパイルする製品ソース）を返す。

なぜ範囲を絞るか（レビュー 2026-08-06 §6-14）:
  以前のゲートは `Code/` ほぼ全部（UI/Infra 含む）を対象にしており、UI 文言 1 行の修正でも
  「csharp compare を 30 分以上回す」か「SKIP_VISUAL_REVIEW=1」の二択になっていた。
  これはバイパスを日常動作として学習させ、ゲート自身を弱める。
  そもそも視覚レビューはハーネスの出力を見るものなので、**ハーネスがコンパイルしない
  ファイルの変更は、compare を何回回しても検出できない**（検証にならない）。

なぜ csproj から読むか:
  対象リストをここに書き写すと、csproj 側に Core ファイルが増えたときミラーが陳腐化し、
  出力に効く変更がゲートをすり抜ける。Harness.csproj を唯一の正として読む。

対象外のファイル（UI / Infra / Automation など）が無検査になるわけではない:
  ci.yml の build-check が dotnet 型チェックを回しており、そちらが受け持つ。
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HARNESS_CSPROJ = ROOT / "scripts" / "headless-run" / "Harness.csproj"


def harness_source_globs() -> list[str]:
    """Harness.csproj の <Compile Include> のうち Code/ 配下のものを glob 文字列で返す。

    例: '..\\..\\Code\\Core\\PixelProcessor*.cs' -> 'Code/Core/PixelProcessor*.cs'
    """
    text = HARNESS_CSPROJ.read_text(encoding="utf-8")
    globs: list[str] = []
    for raw in re.findall(r'<Compile\s+Include="([^"]+)"', text):
        norm = raw.replace("\\", "/")
        idx = norm.find("Code/")
        if idx < 0:
            continue  # Harness.cs 自体など、製品ソースでないもの
        globs.append(norm[idx:])
    if not globs:
        raise RuntimeError(
            f"Harness.csproj から Code/ の Compile Include を 1 件も読めません: {HARNESS_CSPROJ}")
    return sorted(set(globs))


def _glob_to_regex(glob: str) -> re.Pattern:
    # '*' はディレクトリ境界を越えない（csproj の Include も同じ意味）
    parts = [re.escape(seg).replace(r"\*", "[^/]*") for seg in glob.split("/")]
    return re.compile("^" + "/".join(parts) + "$")


def is_harness_source(rel_path: str) -> bool:
    """リポジトリ相対パスがハーネスのコンパイル対象なら True。"""
    norm = rel_path.replace("\\", "/")
    return any(_glob_to_regex(g).match(norm) for g in harness_source_globs())


def harness_source_files() -> list[Path]:
    """ハーネスがコンパイルする製品ソースの実ファイル一覧。"""
    out: list[Path] = []
    for g in harness_source_globs():
        out.extend(sorted(ROOT.glob(g)))
    return out


if __name__ == "__main__":
    files = harness_source_files()
    print(f"視覚レビューゲートの対象: {len(files)} ファイル")
    for p in files:
        print("  " + p.relative_to(ROOT).as_posix())
