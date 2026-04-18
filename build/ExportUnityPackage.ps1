# ExportUnityPackage.ps1
# VRC AvatarColorChanger を unitypackage としてエクスポートするスクリプト。
#
# 使い方:
#   .\build\ExportUnityPackage.ps1 [-UnityExe "C:\...\Unity.exe"] [-OutputDir ".\build"]
#
# 前提:
#   - このリポジトリが Unity プロジェクトの Assets/VACC/ に配置されていること
#   - (例) Assets/VACC/Editor/*.cs , Assets/VACC/package.json
#
# エクスポート対象フォルダ: Assets/VACC

param(
    [string]$UnityExe   = "",          # Unity.exe の絶対パス（省略時は自動検索）
    [string]$OutputDir  = "$PSScriptRoot",  # 出力先ディレクトリ
    [string]$ProjectDir = ""           # Unity プロジェクトルート（省略時は自動検索）
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Unity.exe の自動検索 ────────────────────────────────────────────────────
if (-not $UnityExe) {
    $candidates = @(
        "C:\Program Files\Unity\Hub\Editor",
        "C:\Program Files (x86)\Unity\Hub\Editor"
    )
    foreach ($base in $candidates) {
        if (Test-Path $base) {
            $found = Get-ChildItem -Path $base -Filter "Unity.exe" -Recurse -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($found) { $UnityExe = $found.FullName; break }
        }
    }
    if (-not $UnityExe) {
        Write-Error "Unity.exe が見つかりませんでした。-UnityExe パラメータで指定してください。"
        exit 1
    }
}
Write-Host "Unity: $UnityExe"

# ── Unity プロジェクトルートの自動検索 ─────────────────────────────────────
# このスクリプトの場所から親方向に ProjectSettings/ を探す
if (-not $ProjectDir) {
    $dir = Split-Path $PSScriptRoot -Parent
    while ($dir) {
        if (Test-Path (Join-Path $dir "ProjectSettings")) {
            $ProjectDir = $dir; break
        }
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    if (-not $ProjectDir) {
        Write-Error "Unity プロジェクトが見つかりませんでした。-ProjectDir パラメータで指定してください。"
        exit 1
    }
}
Write-Host "Project: $ProjectDir"

# ── バージョン取得 ──────────────────────────────────────────────────────────
$pkgJson = Join-Path $PSScriptRoot "..\package.json"
$version  = "0.0.0"
if (Test-Path $pkgJson) {
    $pkg     = Get-Content $pkgJson -Raw | ConvertFrom-Json
    $version = $pkg.version
}
Write-Host "Version: $version"

# ── 出力パス ────────────────────────────────────────────────────────────────
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
$outFile = Join-Path (Resolve-Path $OutputDir) "com.yukkuri-aoba.vrc-avatar-color-changer-$version.unitypackage"

# ── エクスポート用 C# メソッドをバッチモードで呼び出す ─────────────────────
$method = "VRCAvatarColorChanger.BuildHelper.Export"
$logFile = Join-Path $OutputDir "unity_export.log"

$args = @(
    "-quit",
    "-batchmode",
    "-nographics",
    "-projectPath", $ProjectDir,
    "-executeMethod", $method,
    "-outputPath", $outFile,
    "-logFile", $logFile
)

Write-Host "エクスポート開始..."
$proc = Start-Process -FilePath $UnityExe -ArgumentList $args -Wait -PassThru -NoNewWindow
if ($proc.ExitCode -ne 0) {
    Write-Host "Unity ログ:"
    Get-Content $logFile -ErrorAction SilentlyContinue | Select-Object -Last 40
    Write-Error "Unity がエラーコード $($proc.ExitCode) で終了しました。"
    exit $proc.ExitCode
}

Write-Host "エクスポート完了: $outFile"
