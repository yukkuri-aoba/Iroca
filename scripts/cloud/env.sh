# クラウドのセッション（Linux コンテナ）で、scripts/cloud/setup.sh が入れた道具を使うための環境変数。
# コマンドごとにシェルが新しくなるので、使うコマンドの前で毎回読み込む:
#   . scripts/cloud/env.sh && .venv/bin/python -m pytest scripts/golden -q
IROCA_ENV=/opt/iroca-env
export PATH="$IROCA_ENV/dotnet:$PATH"
export DOTNET_ROOT="$IROCA_ENV/dotnet"
# ICU の無いコンテナで dotnet を動かす（ci.yml と同じ）。
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# ハーネス（UnityManaged）と build-check（UNITY_EDITOR_PATH）が参照する Unity の DLL。
export UnityManaged="$IROCA_ENV/unity/Editor/Data/Managed"
export UNITY_EDITOR_PATH="$IROCA_ENV/unity/Editor/Data/Managed"
# EditMode テストのコンパイル確認（build-check/IrocaEditorTests.csproj）が使う Unity のパッケージの置き場
# （Library/PackageCache と同じ形。setup.sh が fetch-unity-test-framework.sh で入れる）。
export IROCA_UNITY_PACKAGES="$IROCA_ENV/upm"
# AI マスク提案のモデルの置き場（$LOCALAPPDATA/Iroca/Models。製品・Fetch-Models.ps1 と同じ形）。
export LOCALAPPDATA="$IROCA_ENV/localappdata"
# golden はハッシュで厳密に比べる（IROCA_GOLDEN_TOLERANT は立てない）。2026-10-26 にこの環境で 53 件すべてが
# Windows で固定したハッシュと一致した。CI（ci.yml）は別の Linux イメージなので画素比較にしている。
# セッションのディスクは約 30GB しか無い。dev_safe（約 6GB）と道具を置いた残りに収まるよう、ハーネスの実行キャッシュの
# 上限を既定の 24GB から下げる（足りない分は古い順に捨てて作り直すので、遅くなるだけで結果は変わらない）。
export IROCA_HARNESS_CACHE_GB=8
