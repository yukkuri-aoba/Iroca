#!/usr/bin/env bash
# クラウドのセッション（Linux コンテナ）に、Unity を起動しない範囲の開発環境を用意する。
#
# 使い方:
#   環境の設定の「セットアップスクリプト」に次の 1 行を書く（スクリプトはリポジトリの 1 つ上で始まる）:
#     bash Iroca/scripts/cloud/setup.sh
#   セッションの中から手で回すときは、リポジトリのルートで bash scripts/cloud/setup.sh。
#   何度回してもよい（入っているものは飛ばす）。
#
# 入れるもの（/opt/iroca-env の下）:
#   - .NET SDK 8.0.423（ci.yml と同じ版）
#   - Unity 2022.3.22f1 の Editor/Data/Managed（ハーネス・build-check・unit-run のコンパイル用。Unity は起動しない）
#     Unity の DLL は Unity のライセンス物なのでリポジトリには入れず、ここで Unity の配布元から取ってくる。
#   - AI マスク提案の ONNX モデル（配布元と sha256 は製品コード MaskSuggestModelDownload.cs から読む）
# リポジトリに対して行うこと:
#   - dev_safe を本体の直下へ置く。セッションが本体の隣（iroca_dev_safe / Iroca_dev_safe）に clone したときは
#     本体の直下へ移し、元の場所にリンクを残す（dev_safe のスクリプトは自分の実体の場所から本体を探すので、
#     本体の直下にリンクを張るだけでは動かない）
#   - .venv を作り、dev_safe/Tests/requirements.txt を入れる（dev_safe が無ければ scripts/ci-requirements.txt）
#   - git config core.hooksPath scripts/hooks、通知のルールのコピー
#   - SAM の埋め込みを作る（freeze_sam_fixtures.py。--holdout も）
#
# できないこと: Unity 本体が要るもの（EditMode テスト・RuntimeParityTests・MemoryBench・PerfBench・実機の確認）。
# 使うコマンドの前で毎回 `. scripts/cloud/env.sh` を読み込む。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_DIR=/opt/iroca-env
DOTNET_VERSION=8.0.423
UNITY_VERSION=2022.3.22f1
UNITY_CHANGESET=887be4894c44

log() { printf '[iroca-setup] %s\n' "$*"; }

mkdir -p "$ENV_DIR"

# --- .NET SDK ---
if [ -x "$ENV_DIR/dotnet/dotnet" ] && "$ENV_DIR/dotnet/dotnet" --list-sdks | grep -q "^$DOTNET_VERSION "; then
    log ".NET SDK $DOTNET_VERSION: 入っている"
else
    log ".NET SDK $DOTNET_VERSION を入れる"
    curl -sSL -o "$ENV_DIR/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
    bash "$ENV_DIR/dotnet-install.sh" --version "$DOTNET_VERSION" --install-dir "$ENV_DIR/dotnet"
fi

# --- Unity の Managed DLL ---
MANAGED="$ENV_DIR/unity/Editor/Data/Managed"
if [ -f "$MANAGED/UnityEngine/UnityEngine.CoreModule.dll" ] && [ -f "$MANAGED/UnityEditor.dll" ]; then
    log "Unity $UNITY_VERSION の DLL: 入っている"
else
    log "Unity $UNITY_VERSION の DLL を取る（約 3.2GB の取得と展開。数分かかる）"
    mkdir -p "$ENV_DIR/dl" "$ENV_DIR/unity"
    TARBALL="$ENV_DIR/dl/Unity-$UNITY_VERSION.tar.xz"
    curl -sSL --retry 3 -o "$TARBALL" \
        "https://download.unity3d.com/download_unity/$UNITY_CHANGESET/LinuxEditorInstaller/Unity-$UNITY_VERSION.tar.xz"
    tar -xJf "$TARBALL" -C "$ENV_DIR/unity" --wildcards 'Editor/Data/Managed/*'
    rm -f "$TARBALL"
fi

# --- AI マスク提案のモデル ---
MODELS="$ENV_DIR/localappdata/Iroca/Models"
mkdir -p "$MODELS"
SRC="$ROOT/Code/MaskSuggest/MaskSuggestModelDownload.cs"
REPO=$(sed -nE 's/.*ModelRepo *= *"([^"]+)".*/\1/p' "$SRC" | head -1)
BRANCH=$(sed -nE 's/.*ModelBranch *= *"([^"]+)".*/\1/p' "$SRC" | head -1)
grep -oE '\( *"[^"]+\.onnx" *, *"[0-9a-fA-F]{64}" *, *[0-9]+L *\)' "$SRC" \
    | sed -E 's/\( *"([^"]+)" *, *"([0-9a-fA-F]{64})".*/\1 \2/' \
    | while read -r name sha; do
        if [ -f "$MODELS/$name" ] && [ "$(sha256sum "$MODELS/$name" | cut -d' ' -f1)" = "$sha" ]; then
            log "モデル $name: 入っている"
            continue
        fi
        log "モデル $name を取る"
        curl -sSL --retry 3 -o "$MODELS/$name.part" "https://raw.githubusercontent.com/$REPO/$BRANCH/$name"
        if [ "$(sha256sum "$MODELS/$name.part" | cut -d' ' -f1)" != "$sha" ]; then
            rm -f "$MODELS/$name.part"
            echo "モデル $name の sha256 が合わない" >&2
            exit 1
        fi
        mv "$MODELS/$name.part" "$MODELS/$name"
    done

# --- dev_safe を本体の直下へ ---
if [ ! -e "$ROOT/dev_safe" ]; then
    PARENT="$(dirname "$ROOT")"
    for name in iroca_dev_safe Iroca_dev_safe; do
        cand="$PARENT/$name"
        if [ -d "$cand" ] && [ ! -L "$cand" ] && [ -d "$cand/.git" ]; then
            log "dev_safe を $cand から $ROOT/dev_safe へ移す"
            mv "$cand" "$ROOT/dev_safe"
            ln -s "$ROOT/dev_safe" "$cand"
            break
        fi
    done
fi
if [ -d "$ROOT/dev_safe/.git" ]; then
    # 大きなファイルを 1 つずつ取りに行く自動の gc で止まらないように。
    git -C "$ROOT/dev_safe" config gc.auto 0
else
    log "dev_safe が無い（セッションのリポジトリに Iroca_dev_safe を選ぶと、回帰テストまで回せる）"
fi

# --- Python ---
if [ ! -x "$ROOT/.venv/bin/python" ]; then
    log ".venv を作る"
    python3 -m venv "$ROOT/.venv"
fi
REQ="$ROOT/scripts/ci-requirements.txt"
[ -f "$ROOT/dev_safe/Tests/requirements.txt" ] && REQ="$ROOT/dev_safe/Tests/requirements.txt"
log "Python の依存を入れる（$REQ）"
"$ROOT/.venv/bin/pip" install -q -r "$REQ"

# --- リポジトリの設定 ---
git -C "$ROOT" config core.hooksPath scripts/hooks
if [ -f "$ROOT/dev_safe/setup/claude-notify.md" ] && [ ! -f "$ROOT/.claude/instructions/notify.md" ]; then
    cp "$ROOT/dev_safe/setup/claude-notify.md" "$ROOT/.claude/instructions/notify.md"
fi

# --- SAM の埋め込み（無いと主経路の品質ゲートが静かに skip する） ---
if [ -d "$ROOT/dev_safe/Tests" ]; then
    # shellcheck source=/dev/null
    . "$ROOT/scripts/cloud/env.sh"
    log "SAM の埋め込みを作る"
    (cd "$ROOT" && .venv/bin/python dev_safe/scripts/freeze_sam_fixtures.py \
        && .venv/bin/python dev_safe/scripts/freeze_sam_fixtures.py --holdout)
    # 作業ディレクトリ（約 12GB。埋め込みを作るときの中間ファイルだけで、テストは読まない）を消す。
    rm -rf "$ROOT/dev_safe/_sam_freeze_work"
    # 追跡している凍結ロジット（*_logits.bin / *_scores.bin）は、Linux の onnxruntime で作り直すと
    # Windows で凍結したものと最下位ビットが違う。テストが Windows と同じ入力を使うよう、追跡している版へ戻す。
    git -C "$ROOT/dev_safe" checkout -- texture_sample/ground_truth/sam_fixtures
fi

log "完了。使う前に: . scripts/cloud/env.sh"
