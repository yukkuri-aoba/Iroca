<#
.SYNOPSIS
    Build the download zip for users (installer unitypackage + README + LICENSE).

.DESCRIPTION
    利用者がリリースページや BOOTH からダウンロードする zip を作る。中身は VPAI インストーラ
    （Iroca_Installer.unitypackage）・README.txt・LICENSE.txt の 3 つ。
    VPM zip（com.yukkuri-aoba.iroca-<VERSION>.zip）は VCC / ALCOM とインストーラが取得する
    パッケージ本体で、package.json を頂点に置く決まった形なので、こちらに混ぜない。

    インストーラは読み込んだ時点の最新版を入れ、README も版に依らない書き方にしているので、
    zip の中身は版を上げても変わらない。BOOTH の zip は、中身（README の原稿・LICENSE・
    インストーラの設定・応援メッセージ）を変えたときだけ作り直して差し替える。
    エントリの日時は実行時刻ではなく、中身の元になるファイルの最後のコミット日時にする。
    中身が同じなら zip もバイト単位で同じになり、SHA256 で差し替えが要るかどうかを見分けられる。

    インストーラは先に Build-Installer.ps1 で作っておく（Build-VpmPackage.ps1 から通しで呼ばれる）。
    この zip は listing の SHA256 と関係しないので、単独で作り直してよい。

.PARAMETER Version
    リリースページ用の zip の名前に入れるバージョン（例 0.2.0）。既定は package.json の値。

.PARAMETER InstallerPath
    同梱するインストーラ。既定はリポジトリ直下の Iroca_Installer.unitypackage。

.PARAMETER Booth
    リリースページ用の zip の代わりに、BOOTH の 3 つのバリエーション用の zip を booth/ に作る。
    無料版（Iroca.zip）はリリースページ用と同じ中身。応援パック・応援パック+ は、それに
    応援メッセージ（dev_safe/promo）を足したもの。

.PARAMETER SupporterMessage
    応援パックに入れるメッセージ。既定は dev_safe/promo/応援パックを購入してくださった方へ.txt。

.EXAMPLE
    .\scripts\Build-DownloadZip.ps1 -Version 0.2.0

.EXAMPLE
    .\scripts\Build-DownloadZip.ps1 -Booth
#>
param(
    [string]$Version = "",
    [string]$InstallerPath = "",
    [switch]$Booth,
    [string]$SupporterMessage = ""
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
$DevSafe = Join-Path $Root "dev_safe"
if ($SupporterMessage -eq "") {
    $SupporterMessage = Join-Path $DevSafe "promo\応援パックを購入してくださった方へ.txt"
}

# zip の中身の元になるファイル（インストーラは設定と Build-Installer.ps1 の creator の版で決まる）。
$Sources = @("scripts/download/README.txt", "LICENSE",
             "scripts/installer/vpai-config.json", "scripts/Build-Installer.ps1")

# 中身の元になるファイルの、最後のコミット日時。コミットしていない変更があると、
# 同じ日時で別の中身の zip ができてしまうので止める。
function Get-SourceTime([string]$Repo, [string[]]$Paths) {
    $dirty = & git -C $Repo status --porcelain -- @Paths
    if ($LASTEXITCODE -ne 0) { throw "git status に失敗しました: $Repo" }
    if ($dirty) { throw "コミットしていない変更があります（先にコミットしてください）:`n$($dirty -join "`n")" }
    $iso = & git -C $Repo log -1 --format=%cI -- @Paths
    if ($LASTEXITCODE -ne 0 -or -not $iso) { throw "git の履歴にありません: $($Paths -join ', ')（$Repo）" }
    return [System.DateTimeOffset]::Parse($iso.Trim()).ToUniversalTime()
}

# 利用者はメモ帳で開くので、テキストは BOM 付き UTF-8・CRLF にそろえる
# （BOM が無いと古いメモ帳や一部のエディタで文字化けする）。
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
function Get-WindowsTextBytes([string]$Text) {
    $crlf = $Text.TrimStart([char]0xFEFF).Replace("`r`n", "`n").Replace("`n", "`r`n")
    return ,($utf8Bom.GetPreamble() + $utf8Bom.GetBytes($crlf))
}
function Read-Utf8([string]$Path) { return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) }

$baseEntries = [ordered]@{
    "Iroca_Installer.unitypackage" = [System.IO.File]::ReadAllBytes($InstallerPath)
    "README.txt"                   = Get-WindowsTextBytes (Read-Utf8 (Join-Path $PSScriptRoot "download\README.txt"))
    "LICENSE.txt"                  = Get-WindowsTextBytes (Read-Utf8 (Join-Path $Root "LICENSE"))
}
$baseTime = Get-SourceTime $Root $Sources

Add-Type -AssemblyName System.IO.Compression
function New-DownloadZip([string]$ZipPath, $Entries, [System.DateTimeOffset]$EntryTime) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    # 日本語のエントリ名は UTF-8 で書かれ、UTF-8 のフラグが立つ（Windows のエクスプローラーで化けない）。
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Entries.Keys) {
            $bytes = [byte[]]$Entries[$name]
            $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $EntryTime
            $entryStream = $entry.Open()
            $entryStream.Write($bytes, 0, $bytes.Length)
            $entryStream.Dispose()
        }
    } finally {
        $zip.Dispose()
        $stream.Dispose()
    }
    $sha = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
    Write-Host "[OK] $ZipPath"
    Write-Host "     $($Entries.Keys -join ' / ')"
    Write-Host "     SHA256: $sha"
}

if (-not $Booth) {
    New-DownloadZip (Join-Path $Root "Iroca_v$Version.zip") $baseEntries $baseTime
    return
}

# --- BOOTH の 3 つのバリエーション（無料版・応援パック・応援パック+）---
if (-not (Test-Path $SupporterMessage)) { throw "応援メッセージが見つかりません: $SupporterMessage" }
$msgFull = (Resolve-Path $SupporterMessage).Path
$msgRepo = $Root
if ($msgFull.StartsWith($DevSafe + "\", [System.StringComparison]::OrdinalIgnoreCase)) { $msgRepo = $DevSafe }
$msgRel  = $msgFull.Substring($msgRepo.Length + 1).Replace("\", "/")
$msgTime = Get-SourceTime $msgRepo @($msgRel)
$supporterTime = if ($msgTime -gt $baseTime) { $msgTime } else { $baseTime }

$supporterEntries = [ordered]@{}
foreach ($name in $baseEntries.Keys) { $supporterEntries[$name] = $baseEntries[$name] }
$supporterEntries[[System.IO.Path]::GetFileName($msgFull)] = Get-WindowsTextBytes (Read-Utf8 $msgFull)

$outDir = Join-Path $Root "booth"
New-Item -ItemType Directory -Force $outDir | Out-Null
New-DownloadZip (Join-Path $outDir "Iroca.zip")              $baseEntries      $baseTime
New-DownloadZip (Join-Path $outDir "Iroca_応援パック.zip")   $supporterEntries $supporterTime
New-DownloadZip (Join-Path $outDir "Iroca_応援パック+.zip")  $supporterEntries $supporterTime
