<#
.SYNOPSIS
    ホスト Unity プロジェクトに Iroca 本体を「埋め込みパッケージ」としてリンクする。

.DESCRIPTION
    ホスト側 `Packages/manifest.json` に `file:<リポジトリルート>` を書く方式は使わない。
    リポジトリルートには dev_safe（PSD/テクスチャ素材が数 GB）や dotnet のビルド成果物
    （scripts/*/bin, obj）が同居しており、Unity がそれらを全部アセットとして取り込むため:

      - dev_safe のテクスチャ/PSD を延々インポートし続けて実用にならない
      - scripts/build-check/bin の `com.yukkuri-aoba.iroca.Editor.dll` が
        「同名アセンブリのプラグイン」として読み込まれ、ソースからのコンパイルと
        型が衝突する（CS0436）

    そこで、ホストの `Packages/com.yukkuri-aoba.iroca/` を実体のあるフォルダとして作り、
    配布パッケージに含まれるものだけを置く:

      Packages/com.yukkuri-aoba.iroca/
      ├─ package.json     （リポジトリからコピー。バージョン更新後は再実行して同期する）
      ├─ README.md / CHANGELOG.md / LICENSE （同上・任意）
      └─ Code/            （リポジトリの Code へのディレクトリジャンクション）

    Code をジャンクションにするので、ソース編集はリポジトリ側に直接反映され、Unity が生成する
    `Code/**/*.meta` もリポジトリ側に書かれる（`.gitignore` の `*.meta` 前提どおり）。
    `Packages/<name>/` に置いたフォルダは Unity が自動で埋め込みパッケージとして認識するため、
    manifest.json への追記は不要。`PackageInfo.FindForAssembly` も配布時と同じ形で解決される。

.PARAMETER HostProject
    ホスト Unity プロジェクトのルート（`Assets` と `Packages` があるフォルダ）。

.PARAMETER Unlink
    リンクを解除する。ジャンクションはリパースポイントだけを外すので、リポジトリ側の
    Code は削除されない。

.EXAMPLE
    pwsh scripts/Link-HostPackage.ps1 -HostProject "C:\Users\me\Documents\Avatar_Projects\Iroca_Dev"

.EXAMPLE
    pwsh scripts/Link-HostPackage.ps1 -HostProject "..." -Unlink
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HostProject,

    [switch]$Unlink
)

$ErrorActionPreference = 'Stop'

$PackageName = 'com.yukkuri-aoba.iroca'
# パッケージ直下にコピーする配布物（Code はジャンクションなので別扱い）
$CopyItems = @('package.json', 'README.md', 'CHANGELOG.md', 'LICENSE')

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RepoRoot 'package.json'))) {
    throw "リポジトリルートを特定できません: $RepoRoot（package.json が無い）"
}

$PackagesDir = Join-Path $HostProject 'Packages'
if (-not (Test-Path (Join-Path $HostProject 'Assets')) -or -not (Test-Path $PackagesDir)) {
    throw "Unity プロジェクトに見えません: $HostProject（Assets / Packages が必要）"
}

$Target = Join-Path $PackagesDir $PackageName
$CodeLink = Join-Path $Target 'Code'

function Test-IsJunction([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    $item = Get-Item $Path -Force
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Remove-JunctionOnly([string]$Path) {
    # Remove-Item -Recurse は環境によってジャンクションの「中身」を消しにいくため使わない。
    # Directory.Delete(path, recursive:$false) はリパースポイントだけを外す。
    [System.IO.Directory]::Delete($Path, $false)
}

if ($Unlink) {
    if (-not (Test-Path $Target)) {
        Write-Host "リンクはありません: $Target"
        return
    }
    if (Test-IsJunction $CodeLink) { Remove-JunctionOnly $CodeLink }
    elseif (Test-Path $CodeLink) {
        throw "Code がジャンクションではありません。手動で確認してください: $CodeLink"
    }
    Remove-Item $Target -Recurse -Force
    Write-Host "リンクを解除しました: $Target"
    return
}

# 既存のリンクを作り直す（Code のジャンクションは必ず「外すだけ」で消す）
if (Test-Path $Target) {
    if (Test-IsJunction $Target) {
        throw "パッケージフォルダ自体がジャンクションになっています（リポジトリルート直リンクの疑い）。手動で外してください: $Target"
    }
    if (Test-IsJunction $CodeLink) { Remove-JunctionOnly $CodeLink }
    elseif (Test-Path $CodeLink) {
        throw "Code がジャンクションではありません。手動で確認してください: $CodeLink"
    }
}
else {
    New-Item -ItemType Directory -Path $Target | Out-Null
}

New-Item -ItemType Junction -Path $CodeLink -Target (Join-Path $RepoRoot 'Code') | Out-Null

foreach ($name in $CopyItems) {
    $src = Join-Path $RepoRoot $name
    if (Test-Path $src) { Copy-Item $src (Join-Path $Target $name) -Force }
}

Write-Host "リンクしました: $Target"
Write-Host "  Code -> $(Join-Path $RepoRoot 'Code')  (junction)"
Write-Host "  コピー: $((Get-ChildItem $Target -File | Select-Object -ExpandProperty Name) -join ', ')"
Write-Host "※ package.json のバージョンを上げたら、このスクリプトを再実行してコピーを同期すること。"
