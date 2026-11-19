<#
.SYNOPSIS
  テスト用の PC で、画面の要る Unity の台本（-UndoReload・-SdkBuild）を動かせるよう、サインインしたデスクトップを保つ
  （相手の PC で 1 回、管理者で。ssh で入った開発機からでもよい）。

.DESCRIPTION
  ssh の先はデスクトップの無いセッション（session 0）なので、画面ありの Unity は動かない。tools/editor_tests.py は、
  同じユーザーがサインインしているデスクトップでタスク スケジューラーから Unity を動かす（2026-11-20〜）。そのため
  テスト用の PC はサインインしたままにしておく必要がある。

  1. リモート デスクトップ（RDP）を切断したら、そのセッションを本体の画面（コンソール）へ移すタスクを入れる。
     切断したままの RDP のセッションには画面が無く、Unity の描画が当てにならない。移した後は本体の画面に
     サインインしたデスクトップが出たままになる（ロックはしない。Windows の設定で画面ロックが掛かっても、動いている
     Unity は止まらない）。
  2. 自動サインイン（再起動の後もサインインした状態に戻す）の状態を表示する。パスワードはこのスクリプトでは扱わない。
     有効にするには、本体の画面か RDP で、Sysinternals の Autologon（winget install Microsoft.Sysinternals.Autologon）を
     起動して、ユーザー名・パスワードを入れて Enable を押す（パスワードは暗号化して保存される）。

.PARAMETER User
  サインインしたままにするユーザー名。既定は実行したユーザー。

.PARAMETER Remove
  1 のタスクを消す。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Enable-DesktopSession.ps1
#>
param(
    [string]$User = $env:USERNAME,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "管理者として実行してください。"
}

$taskPath = '\Iroca\'
$taskName = 'keep-desktop-session'
$dir = Join-Path $env:ProgramData 'Iroca'
$script = Join-Path $dir 'keep-desktop-session.ps1'

if ($Remove) {
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script -ErrorAction SilentlyContinue
    Write-Host "消しました: $taskPath$taskName"
    return
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
# 切断されたセッションは qwinsta で「ユーザー名 ID Disc」（セッション名が空）と出る。日本語の表示なら「切断」。
$body = @"
`$user = '$($User.Replace("'", "''"))'
foreach (`$line in (qwinsta 2>`$null)) {
    `$t = (`$line.TrimStart('>')).Trim() -split '\s+'
    if (`$t.Count -ge 3 -and `$t[0] -ieq `$user -and `$t[1] -match '^\d+$' -and [int]`$t[1] -gt 0 -and `$t[2] -match '^(Disc|切断)') {
        tscon.exe `$t[1] /dest:console
    }
}
"@
# PowerShell 5.1 は BOM の無い .ps1 を ANSI で読む
[IO.File]::WriteAllText($script, $body, (New-Object Text.UTF8Encoding $true))

# 切断（LocalSessionManager のイベント 24）で、SYSTEM として動かす（tscon は SYSTEM ならパスワードなしで移せる）
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>RDP を切断したセッションを本体の画面へ移し、サインインしたデスクトップを保つ（Iroca のテスト用。scripts/remote-test/Enable-DesktopSession.ps1）</Description></RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Microsoft-Windows-TerminalServices-LocalSessionManager/Operational"&gt;&lt;Select Path="Microsoft-Windows-TerminalServices-LocalSessionManager/Operational"&gt;*[System[(EventID=24)]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <Delay>PT5S</Delay>
    </EventTrigger>
  </Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "$script"</Arguments>
    </Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Xml $xml -Force | Out-Null
Write-Host "[済] RDP を切断したら本体の画面へ移すタスク: $taskPath$taskName（ユーザー $User）"

$wl = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
if ($wl.AutoAdminLogon -eq '1') {
    Write-Host "[済] 自動サインイン: $($wl.DefaultUserName)"
} else {
    Write-Host "[未] 自動サインイン。再起動（Windows Update など）の後はサインインし直すまで、画面の要る台本は開発機で回ります。" -ForegroundColor Yellow
    Write-Host "      有効にするには、この PC の画面か RDP で Sysinternals の Autologon を起動してパスワードを入れる。"
}
$sessions = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { $_.SessionId } | Sort-Object -Unique)
Write-Host "今デスクトップのあるセッション: $(if ($sessions) { $sessions -join ', ' } else { 'なし' })"
