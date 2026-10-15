<#
.SYNOPSIS
    Build the VPAI installer unitypackage (VPMPackageAutoInstaller).

.DESCRIPTION
    インポートすると VPM listing (docs/index.json の公開先) から Iroca の最新版を
    Packages/ へ導入し、自身は削除される unitypackage を作る。中身は設定 JSON と
    VPAI 本体 DLL だけで Iroca のコードは含まないため、listing の範囲指定
    (scripts/installer/vpai-config.json) を変えない限りリリースごとに作り直す必要はない。

    creator はバージョンと SHA256 を固定して取得する。同じ設定 + 同じ creator なら
    出力はバイト単位で同一になる（2026-09-23 実測）。

.PARAMETER ConfigPath
    VPAI 設定 JSON。既定は scripts/installer/vpai-config.json（コメント不可）。

.PARAMETER OutputPath
    出力先。既定はリポジトリ直下の Iroca_Installer.unitypackage。

.EXAMPLE
    .\scripts\Build-Installer.ps1
#>
param(
    [string]$ConfigPath = "",
    [string]$OutputPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# creator を上げるときは両方を更新する（release の creator.mjs の SHA256）。
$CreatorVersion = "1.1.6"
$CreatorSha256  = "416709b3a70dd7b3023e84b04d077f6b38873b15fb7f1042c6900e72f00285af"
$CreatorUrl     = "https://github.com/anatawa12/VPMPackageAutoInstaller/releases/download/v$CreatorVersion/creator.mjs"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($ConfigPath -eq "") { $ConfigPath = Join-Path $PSScriptRoot "installer\vpai-config.json" }
if ($OutputPath -eq "") { $OutputPath = Join-Path $Root "Iroca_Installer.unitypackage" }
if (-not (Test-Path $ConfigPath)) { throw "設定が見つかりません: $ConfigPath" }

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "node が見つかりません（creator.mjs の実行に Node.js が必要です）"
}

# 取得した creator は %LOCALAPPDATA% にキャッシュし、毎回 SHA256 を照合する。
$cacheDir = Join-Path $env:LOCALAPPDATA "Iroca\vpai-creator\$CreatorVersion"
$creator  = Join-Path $cacheDir "creator.mjs"
New-Item -ItemType Directory -Force $cacheDir | Out-Null
if (-not (Test-Path $creator)) {
    Write-Host "Downloading creator.mjs v$CreatorVersion"
    Invoke-WebRequest -Uri $CreatorUrl -OutFile $creator -UseBasicParsing
}
$actual = (Get-FileHash $creator -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $CreatorSha256) {
    Remove-Item $creator -Force
    throw "creator.mjs の SHA256 が一致しません（expected $CreatorSha256, actual $actual）"
}

& node $creator $ConfigPath $OutputPath
if ($LASTEXITCODE -ne 0) { throw "creator.mjs が失敗しました (exit $LASTEXITCODE)" }

$sha = (Get-FileHash $OutputPath -Algorithm SHA256).Hash.ToLower()
Write-Host "[OK] installer: $OutputPath"
Write-Host "[OK] SHA256   : $sha"
