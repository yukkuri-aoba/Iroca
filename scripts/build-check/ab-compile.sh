#!/usr/bin/env bash
# 基準のコミットの Code に、今の(または指定した)コミットの EditMode テストを当ててコンパイルできるかを確かめる。
# 手元の交互 A/B(Run-EditorTests.ps1 -AllowForeignCode で Code のジャンクションだけを基準の木へ向ける)は、テストの
# アセンブリ全体が基準の Code でコンパイルできないと回らない。Unity を開く前にクラウドや CI で確かめるための道具。
#
#   bash scripts/build-check/ab-compile.sh <基準の Code のコミット> [テストのコミット(既定: 作業ツリー)]
#
# 終了コードは 0 = コンパイルできる、1 = できない(エラーを出す)。作業ツリーと git の状態は変えない。
# Unity の DLL とパッケージは IrocaEditorTests.csproj と同じ解決(クラウドは scripts/cloud/env.sh を先に読み込む)。
set -euo pipefail

BASE="${1:?基準のコミットを指定してください}"
TESTS="${2:-}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/iroca-ab-compile.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

git -C "$ROOT" archive "$BASE" Code | tar -x -C "$WORK"
if [ -n "$TESTS" ]; then
    git -C "$ROOT" archive "$TESTS" scripts/editor-tests | tar -x -C "$WORK"
else
    mkdir -p "$WORK/scripts" && cp -r "$ROOT/scripts/editor-tests" "$WORK/scripts/"
fi
# コンパイルの仕組みは今のもの(基準のコミットには無いことがある)。
mkdir -p "$WORK/scripts/build-check/unity-test-framework"
cp "$ROOT"/scripts/build-check/*.csproj "$ROOT"/scripts/build-check/*.props "$WORK/scripts/build-check/"
cp "$ROOT"/scripts/build-check/unity-test-framework/*.csproj "$WORK/scripts/build-check/unity-test-framework/"

if dotnet build "$WORK/scripts/build-check/IrocaEditorTests.csproj" -c Release -nologo > "$WORK/build.log" 2>&1; then
    echo "[ab-compile] 基準 $BASE の Code で、テスト(${TESTS:-作業ツリー})はコンパイルできる"
else
    grep -E " error " "$WORK/build.log" | sed -E "s#^$WORK/##; s/ \[.*//" | sort -u
    echo "[ab-compile] 基準 $BASE の Code で、テスト(${TESTS:-作業ツリー})はコンパイルできない" >&2
    exit 1
fi
