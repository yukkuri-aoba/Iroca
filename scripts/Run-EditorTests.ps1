<#
.SYNOPSIS
    Unity 実機（batchmode）で EditMode テスト（scripts/editor-tests）を実行する。

.DESCRIPTION
    headless ハーネスと golden は net8 で製品 C# を動かすが、Unity 固有の部分（AssetDatabase を使う
    保存・復元、PNG の読み書き、Mono 上での再着色の出力）はそこでは検証できない。このスクリプトは
    ホスト Unity プロジェクトを batchmode で起動し、Iroca.EditorTests アセンブリのテストだけを回す。

    前提:
      - ホストに本体とテストがリンク済み:
          .\scripts\Link-HostPackage.ps1 -HostProject <ホスト> -WithEditorTests
      - そのホストを Unity Editor で開いていない（batchmode は同じプロジェクトを同時に開けない）

    ゲーム中でも邪魔しないよう Unity は BelowNormal 優先度で走らせる。
    初回はホストの取り込み（Library 生成）で時間がかかる。

.PARAMETER HostProject
    ホスト Unity プロジェクトのルート。既定は Documents\Avatar_Projects\Iroca_Dev。

.PARAMETER UnityExe
    Unity.exe のパス。既定は UNITY_EDITOR_PATH（...\Editor\Data\Managed）から導出し、
    無ければ Unity Hub の既定インストール先（2022.3.22f1）。

.EXAMPLE
    .\scripts\Run-EditorTests.ps1
#>
[CmdletBinding()]
param(
    [string]$HostProject = (Join-Path $env:USERPROFILE 'Documents\Avatar_Projects\Iroca_Dev'),
    [string]$UnityExe
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $UnityExe) {
    if ($env:UNITY_EDITOR_PATH) {
        # ...\Editor\Data\Managed → ...\Editor\Unity.exe
        $UnityExe = Join-Path (Split-Path -Parent (Split-Path -Parent $env:UNITY_EDITOR_PATH)) 'Unity.exe'
    }
    if (-not $UnityExe -or -not (Test-Path $UnityExe)) {
        $UnityExe = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe'
    }
}
if (-not (Test-Path $UnityExe)) { throw "Unity.exe が見つかりません: $UnityExe（-UnityExe で指定）" }

$testsLink = Join-Path $HostProject 'Assets\IrocaEditorTests'
if (-not (Test-Path $testsLink)) {
    throw "テストがホストにリンクされていません: $testsLink`n  .\scripts\Link-HostPackage.ps1 -HostProject `"$HostProject`" -WithEditorTests を実行してください"
}

# Editor が同じプロジェクトを開いていると batchmode は即終了する。先に分かりやすく止める。
$lock = Join-Path $HostProject 'Temp\UnityLockfile'
if (Test-Path $lock) {
    try { [IO.File]::Open($lock, 'Open', 'ReadWrite', 'None').Dispose() }
    catch { throw "ホストプロジェクトを Unity Editor で開いているようです。閉じてから実行してください: $HostProject" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path $env:TEMP "iroca-editor-tests\$stamp"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$results = Join-Path $outDir 'results.xml'
$log = Join-Path $outDir 'unity.log'
$parity = Join-Path $outDir 'parity.tsv'

# テストが参照する（RuntimeParityTests は golden の入力と期待出力をリポジトリから読む）。
$env:IROCA_REPO = $RepoRoot
$env:IROCA_PARITY_REPORT = $parity

$unityArgs = @(
    '-batchmode', '-nographics',
    '-projectPath', "`"$HostProject`"",
    '-runTests', '-testPlatform', 'EditMode',
    '-assemblyNames', 'Iroca.EditorTests',
    '-testResults', "`"$results`"",
    '-logFile', "`"$log`""
)
Write-Host "Unity を batchmode で起動します（結果: $outDir）…"
$p = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru
try { $p.PriorityClass = 'BelowNormal' } catch { }
$p.WaitForExit()

if (-not (Test-Path $results)) {
    Write-Host (Get-Content $log -Tail 40 -ErrorAction SilentlyContinue | Out-String)
    throw "テスト結果が出力されませんでした（Unity の終了コード $($p.ExitCode)）。ログ: $log"
}

[xml]$xml = Get-Content $results -Encoding UTF8
$run = $xml.'test-run'
Write-Host ("合計 {0} / 成功 {1} / 失敗 {2} / スキップ {3}" -f $run.total, $run.passed, $run.failed, $run.skipped)
foreach ($tc in $xml.SelectNodes("//test-case[@result='Failed']")) {
    Write-Host "  FAIL $($tc.fullname)" -ForegroundColor Red
    $msg = $tc.failure.message.'#cdata-section'
    if ($msg) { Write-Host "       $($msg.Trim())" }
}
if (Test-Path $parity) {
    $lines = Get-Content $parity
    $exact = ($lines | Where-Object { $_ -match "`texact`t" }).Count
    Write-Host "Unity(Mono) とハーネス(net8) の完全一致: $exact / $($lines.Count) 件（詳細: $parity）"
}

if ([int]$run.total -eq 0) { throw "テストが 1 件も実行されませんでした（asmdef のコンパイルエラー？ ログ: $log）" }
exit ([int]$run.failed)
