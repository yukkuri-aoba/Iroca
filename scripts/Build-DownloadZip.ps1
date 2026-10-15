<#
.SYNOPSIS
    Build the download zip for users (installer unitypackage + README + LICENSE).

.DESCRIPTION
    利用者がリリースページや BOOTH からダウンロードする zip を作る。中身は VPAI インストーラ
    （Iroca_Installer.unitypackage）・README.txt・LICENSE.txt の 3 つ。
    VPM zip（com.yukkuri-aoba.iroca-<VERSION>.zip）は VCC / ALCOM とインストーラが取得する
    パッケージ本体で、package.json を頂点に置く決まった形なので、こちらに混ぜない。

    インストーラは先に Build-Installer.ps1 で作っておく（Build-VpmPackage.ps1 から通しで呼ばれる）。
    この zip は listing の SHA256 と関係しないので、単独で作り直してよい。

.PARAMETER Version
    Version number (e.g. 0.2.0). Defaults to the value in package.json.

.PARAMETER InstallerPath
    同梱するインストーラ。既定はリポジトリ直下の Iroca_Installer.unitypackage。

.EXAMPLE
    .\scripts\Build-DownloadZip.ps1 -Version 0.2.0
#>
param(
    [string]$Version = "",
    [string]$InstallerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ($Version -eq "") {
    $Version = ((Get-Content (Join-Path $Root "package.json") -Encoding UTF8) | ConvertFrom-Json).version
}
if ($InstallerPath -eq "") { $InstallerPath = Join-Path $Root "Iroca_Installer.unitypackage" }
if (-not (Test-Path $InstallerPath)) {
    throw "インストーラが見つかりません: $InstallerPath（先に scripts/Build-Installer.ps1 を実行してください）"
}

$ZipName  = "Iroca_v$Version.zip"
$ZipPath  = Join-Path $Root $ZipName
$Template = Join-Path $PSScriptRoot "download\README.txt"

# 利用者はメモ帳で開くので、README と LICENSE は BOM 付き UTF-8・CRLF にそろえる
# （BOM が無いと古いメモ帳や一部のエディタで文字化けする）。
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
function Get-WindowsTextBytes([string]$Text) {
    $crlf = $Text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    return ,($utf8Bom.GetPreamble() + $utf8Bom.GetBytes($crlf))
}

$readme  = [System.IO.File]::ReadAllText($Template, [System.Text.Encoding]::UTF8).Replace("{VERSION}", $Version)
$license = [System.IO.File]::ReadAllText((Join-Path $Root "LICENSE"), [System.Text.Encoding]::UTF8)

# Build-VpmPackage.ps1 と同じく、実行時刻ではなく HEAD のコミット日時を焼く（同じ commit → 同じ zip）。
$entryTime = [System.DateTimeOffset]::Parse(
    (& git -C $Root log -1 --format=%cI HEAD).Trim()).ToUniversalTime()
if ($LASTEXITCODE -ne 0) { throw "git log に失敗しました" }

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
$zip    = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    function Add-ZipBytes([string]$EntryName, [byte[]]$Bytes) {
        $entry = $zip.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $entryTime
        $entryStream = $entry.Open()
        $entryStream.Write($Bytes, 0, $Bytes.Length)
        $entryStream.Dispose()
        Write-Host "  + $EntryName"
    }
    Add-ZipBytes "Iroca_Installer.unitypackage" ([System.IO.File]::ReadAllBytes($InstallerPath))
    Add-ZipBytes "README.txt"  (Get-WindowsTextBytes $readme)
    Add-ZipBytes "LICENSE.txt" (Get-WindowsTextBytes $license)
} finally {
    $zip.Dispose()
    $stream.Dispose()
}

$sha = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
Write-Host "[OK] download zip: $ZipPath"
Write-Host "[OK] SHA256      : $sha"
