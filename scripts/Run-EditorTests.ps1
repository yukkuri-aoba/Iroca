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

.PARAMETER PerfCases
    性能計測（PerfBenchTests、Category "Perf"）だけを回す。値は計測ケースの JSON
    （python dev_safe/scripts/perf_bench.py unity-cases が作る）。結果は出力フォルダの perf.jsonl。

.PARAMETER MemCases
    メモリ計測（MemoryBenchTests、Category "Memory"）だけを回す。値は計測ケースの JSON。
    結果は出力フォルダの mem.jsonl。ヒープの最大値は前のケースを引きずるので、1 回の起動に 1 ケースが基本。

.PARAMETER HiResCases
    大きなテクスチャ(8K・16K)の通し確認（HiResSmokeTests、Category "HiRes"）だけを回す。値はケースの JSON。
    結果は出力フォルダの hires.jsonl。16K はメモリを 20GB 前後使うので、1 回の起動に 1 ケースにする。

.PARAMETER Graphics
    -nographics を付けずに起動する（GPU を使う段も実機で通す。HiRes のシーンプレビューの段や AI 推論の GPU 経路）。

.PARAMETER KernelBench
    ループの書き方の速さ比べ（KernelBenchTests、Category "Kernel"）だけを回す。結果は出力フォルダの kernel.jsonl。

.PARAMETER UndoBench
    編集状態を Undo に記録する手間と自動保存の各段の時間（UndoBenchTests、Category "UndoBench"）だけを回す。
    結果は出力フォルダの undo.jsonl。

.PARAMETER DebugCodeOptimization
    Unity を Debug の最適化モード（-debugCodeOptimization）で起動する。Mono のインライン展開が止まる状態での速さを見るとき。

.PARAMETER AllowForeignCode
    ホストの Code が、このスクリプトのある作業ツリー以外を指していても止めない（交互 A/B で Code だけを
    基準の木へ向けるとき）。テストのリンクは常にこの作業ツリーを指している必要がある。

.EXAMPLE
    .\scripts\Run-EditorTests.ps1
.EXAMPLE
    .\scripts\Run-EditorTests.ps1 -HostProject <ホスト> -PerfCases $env:TEMP\iroca-perf\unity\unity_cases.json
#>
[CmdletBinding()]
param(
    [string]$HostProject = (Join-Path $env:USERPROFILE 'Documents\Avatar_Projects\Iroca_Dev'),
    [string]$UnityExe,
    [string]$PerfCases,
    [string]$MemCases,
    [string]$HiResCases,
    [switch]$Graphics,
    [switch]$AllowForeignCode,
    [switch]$KernelBench,
    [switch]$UndoBench,
    [switch]$DebugCodeOptimization
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

# ホストの Code とテストは、このスクリプトのある作業ツリーへのジャンクションのはず。別の作業ツリー
# （worktree や本体）を指したまま回すと、黙ってそちらのコードを測る。交互 A/B で Code だけを基準の木へ
# 向けるときは -AllowForeignCode を付ける（指している先は表示する）。
function Get-LinkTarget([string]$path) {
    $item = Get-Item -LiteralPath $path -Force
    if ($item.LinkType -ne 'Junction' -and $item.LinkType -ne 'SymbolicLink') { return $null }
    return [IO.Path]::GetFullPath(@($item.Target)[0]).TrimEnd('\')
}
$codeLink = Join-Path $HostProject 'Packages\com.yukkuri-aoba.iroca\Code'
foreach ($pair in @(@($codeLink, (Join-Path $RepoRoot 'Code')), @($testsLink, (Join-Path $RepoRoot 'scripts\editor-tests')))) {
    $target = if (Test-Path $pair[0]) { Get-LinkTarget $pair[0] } else { $null }
    $want = [IO.Path]::GetFullPath($pair[1]).TrimEnd('\')
    if ($target -ieq $want) { continue }
    $msg = "$($pair[0]) は $(if ($target) { $target } else { 'ジャンクションではありません' }) を指しています（この作業ツリーは $want）"
    if ($AllowForeignCode -and $pair[0] -eq $codeLink -and $target) { Write-Host "注意: $msg" -ForegroundColor Yellow; continue }
    throw "$msg`n  この作業ツリーの .\scripts\Link-HostPackage.ps1 -HostProject `"$HostProject`" -WithEditorTests で向け直してください"
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
# 呼び出したシェルにそのまま残すと、同じシェルで回した別の作業ツリーの abcheck がこれを読んで
# こちらの木を測ってしまうので、終わったら元に戻す（下の finally）。
$prevIrocaRepo = $env:IROCA_REPO
$env:IROCA_REPO = $RepoRoot
$env:IROCA_PARITY_REPORT = $parity

$unityArgs = @('-batchmode')
if (-not $Graphics) { $unityArgs += '-nographics' }
$unityArgs += @(
    '-projectPath', "`"$HostProject`"",
    '-runTests', '-testPlatform', 'EditMode',
    '-assemblyNames', 'Iroca.EditorTests',
    '-testResults', "`"$results`"",
    '-logFile', "`"$log`""
)
# 性能計測は Perf カテゴリだけを回す（普段のテストでは IROCA_PERF_CASES が無いので Ignore になる）。
$perfReport = Join-Path $outDir 'perf.jsonl'
if ($PerfCases) {
    if (-not (Test-Path $PerfCases)) { throw "計測ケースの JSON がありません: $PerfCases" }
    $env:IROCA_PERF_CASES = (Resolve-Path $PerfCases).Path
    $env:IROCA_PERF_REPORT = $perfReport
    $unityArgs += @('-testCategory', 'Perf')
} else {
    Remove-Item Env:IROCA_PERF_CASES -ErrorAction SilentlyContinue
}
# メモリ計測は Memory カテゴリだけを回す（普段のテストでは IROCA_MEM_CASES が無いので Ignore になる）。
$memReport = Join-Path $outDir 'mem.jsonl'
if ($MemCases) {
    if ($PerfCases) { throw "-PerfCases と -MemCases は同時に指定できません（Unity を別々に起動して測る）" }
    if (-not (Test-Path $MemCases)) { throw "計測ケースの JSON がありません: $MemCases" }
    $env:IROCA_MEM_CASES = (Resolve-Path $MemCases).Path
    $env:IROCA_MEM_REPORT = $memReport
    $unityArgs += @('-testCategory', 'Memory')
} else {
    Remove-Item Env:IROCA_MEM_CASES -ErrorAction SilentlyContinue
}
# 大きなテクスチャの通し確認は HiRes カテゴリだけを回す（普段のテストでは IROCA_HIRES_CASES が無いので Ignore になる）。
$hiresReport = Join-Path $outDir 'hires.jsonl'
if ($HiResCases) {
    if ($PerfCases -or $MemCases) { throw "-HiResCases は -PerfCases / -MemCases と同時に指定できません（Unity を別々に起動して測る）" }
    if (-not (Test-Path $HiResCases)) { throw "ケースの JSON がありません: $HiResCases" }
    $env:IROCA_HIRES_CASES = (Resolve-Path $HiResCases).Path
    $env:IROCA_HIRES_REPORT = $hiresReport
    $unityArgs += @('-testCategory', 'HiRes')
} else {
    Remove-Item Env:IROCA_HIRES_CASES -ErrorAction SilentlyContinue
}
# ループの書き方の速さ比べは Kernel カテゴリだけを回す（普段のテストでは IROCA_KERNEL_REPORT が無いので Ignore になる）。
$kernelReport = Join-Path $outDir 'kernel.jsonl'
if ($KernelBench) {
    if ($PerfCases -or $MemCases -or $HiResCases) { throw "-KernelBench は -PerfCases / -MemCases / -HiResCases と同時に指定できません" }
    $env:IROCA_KERNEL_REPORT = $kernelReport
    $unityArgs += @('-testCategory', 'Kernel')
} else {
    Remove-Item Env:IROCA_KERNEL_REPORT -ErrorAction SilentlyContinue
}
# Undo の記録と自動保存の時間は UndoBench カテゴリだけを回す（普段のテストでは IROCA_UNDO_BENCH_REPORT が無いので Ignore になる）。
$undoReport = Join-Path $outDir 'undo.jsonl'
if ($UndoBench) {
    if ($PerfCases -or $MemCases -or $HiResCases -or $KernelBench) { throw "-UndoBench は -PerfCases / -MemCases / -HiResCases / -KernelBench と同時に指定できません" }
    $env:IROCA_UNDO_BENCH_REPORT = $undoReport
    $unityArgs += @('-testCategory', 'UndoBench')
} else {
    Remove-Item Env:IROCA_UNDO_BENCH_REPORT -ErrorAction SilentlyContinue
}
if ($DebugCodeOptimization) { $unityArgs += '-debugCodeOptimization' }
Write-Host "Unity を batchmode で起動します（結果: $outDir）…"
try {
    $p = Start-Process -FilePath $UnityExe -ArgumentList $unityArgs -PassThru
    try { $p.PriorityClass = 'BelowNormal' } catch { }
    $p.WaitForExit()
} finally {
    if ($null -eq $prevIrocaRepo) { Remove-Item Env:IROCA_REPO -ErrorAction SilentlyContinue }
    else { $env:IROCA_REPO = $prevIrocaRepo }
}

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
if ($PerfCases -and (Test-Path $perfReport)) {
    Write-Host "性能計測の結果: $perfReport（python dev_safe/scripts/perf_bench.py unity-report で集計）"
}
if ($MemCases -and (Test-Path $memReport)) {
    Write-Host "メモリ計測の結果: $memReport"
}
if ($KernelBench -and (Test-Path $kernelReport)) {
    Write-Host "ループの書き方の速さ比べの結果: $kernelReport"
}
if ($UndoBench -and (Test-Path $undoReport)) {
    Write-Host "Undo の記録と自動保存の時間: $undoReport"
}
if ($HiResCases -and (Test-Path $hiresReport)) {
    Write-Host "大きなテクスチャの通し確認の結果: $hiresReport"
}
if (Test-Path $parity) {
    $lines = Get-Content $parity
    $exact = ($lines | Where-Object { $_ -match "`texact`t" }).Count
    Write-Host "Unity(Mono) とハーネス(net8) の完全一致: $exact / $($lines.Count) 件（詳細: $parity）"
}

if ([int]$run.total -eq 0) { throw "テストが 1 件も実行されませんでした（asmdef のコンパイルエラー？ ログ: $log）" }
exit ([int]$run.failed)
