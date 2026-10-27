#!/usr/bin/env bash
# EditMode テストのコンパイル確認（IrocaEditorTests.csproj）に要る Unity のパッケージを、Unity のパッケージの配布元から取る。
# Unity を開いた手元のホストなら Library/PackageCache にあるので要らない（クラウドのセッションと CI 用）。
#
#   bash scripts/build-check/fetch-unity-test-framework.sh <置き場>
#   dotnet build scripts/build-check/IrocaEditorTests.csproj -p:UnityPackagesPath=<置き場>
#
# 置き場には Library/PackageCache と同じ形（<名前>@<版>/）で展開する。版は Unity 2022.3.22f1 の既定
# （test-framework 1.1.33 と、それが使う ext.nunit 1.0.6）。リポジトリには入れない（Unity Companion License の物）。
# 何度回してもよい（入っているものは飛ばす）。
set -euo pipefail

DEST="${1:?置き場を指定してください}"
mkdir -p "$DEST"

# 名前 版 sha256（配布元の tgz）
PACKAGES="
com.unity.test-framework 1.1.33 037d9797581d8db3935177c2605de1cc45acca95b69d3e26af84e268656468cb
com.unity.ext.nunit 1.0.6 2314edd22ed0e5ebd39d8d51f981d93059d5361954327984c8367bc43650e2bb
"

echo "$PACKAGES" | while read -r name version sha; do
    [ -n "$name" ] || continue
    dir="$DEST/$name@$version"
    if [ -f "$dir/package.json" ]; then
        echo "[unity-packages] $name@$version: 入っている"
        continue
    fi
    echo "[unity-packages] $name@$version を取る"
    tgz="$DEST/$name-$version.tgz"
    curl -sSL --retry 3 -o "$tgz" "https://download.packages.unity.com/$name/-/$name-$version.tgz"
    if [ "$(sha256sum "$tgz" | cut -d' ' -f1)" != "$sha" ]; then
        rm -f "$tgz"
        echo "$name@$version の sha256 が合わない" >&2
        exit 1
    fi
    rm -rf "$dir.part" && mkdir -p "$dir.part"
    tar -xzf "$tgz" -C "$dir.part" --strip-components=1
    rm -f "$tgz"
    mv "$dir.part" "$dir"
done
