<#
.SYNOPSIS
  テスト用の別 PC を、tools/remote_test.py の相手として使えるようにする（相手の PC で 1 回、管理者で実行）。

.DESCRIPTION
  1. Git・Python 3.14・.NET SDK 8 を winget で入れる（入っていれば飛ばす）。
  2. OpenSSH サーバーを入れて自動起動にする。ファイアウォールは同じ LAN（LocalSubnet）からだけ通す。
  3. 開発機の公開鍵（-PublicKey）を、鍵で入れるように登録する。
  4. 開発機の ~/.ssh/config に書く値（ユーザー名・アドレス）と、この PC の性能を表示する。

  データ・リポジトリ・Unity の DLL は、ここでは入れない。開発機で `python tools/remote_test.py setup` を実行すると送られる。
  スリープに入るとテストは開発機の手元に戻る（電源の設定はこのスクリプトでは変えない）。

.PARAMETER PublicKey
  開発機の公開鍵（ssh-ed25519 で始まる 1 行）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Enable-RemoteTestHost.ps1 -PublicKey "ssh-ed25519 AAAA... iroca-remote-test"
#>
param(
    [Parameter(Mandatory = $true)][string]$PublicKey
)
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "管理者として実行してください（PowerShell を右クリック →「管理者として実行」）。"
}
if ($PublicKey -notmatch '^ssh-(ed25519|rsa) [A-Za-z0-9+/=]+') {
    throw "公開鍵の形が違います: $PublicKey"
}

function Install-WingetPackage([string]$Id, [string]$Probe) {
    if ($Probe -and (Get-Command $Probe -ErrorAction SilentlyContinue)) {
        Write-Host "[済] $Id"
        return
    }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Write-Warning "winget が無いので $Id を手で入れてください。"
        return
    }
    Write-Host "[入れる] $Id"
    winget install --id $Id -e --silent --scope machine --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        winget install --id $Id -e --silent --accept-package-agreements --accept-source-agreements
    }
}

# 1. 道具
Install-WingetPackage 'Git.Git' 'git'
Install-WingetPackage 'Python.Python.3.14' $null
Install-WingetPackage 'Microsoft.DotNet.SDK.8' $null

# 2. OpenSSH サーバー
$cap = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*'
if ($cap -and $cap.State -ne 'Installed') {
    Write-Host "[入れる] OpenSSH サーバー"
    Add-WindowsCapability -Online -Name $cap.Name | Out-Null
}
Set-Service -Name sshd -StartupType Automatic
# 道具を入れた後の PATH を ssh のセッションに渡すため、起動し直す
Restart-Service -Name sshd -Force -ErrorAction SilentlyContinue
if ((Get-Service sshd).Status -ne 'Running') {
    Start-Service sshd
}

# ファイアウォール: 同じ LAN からだけ
$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue
if ($rule) {
    # 回線が「パブリック」扱いでも通るよう、プロファイルは全部にする（相手は LocalSubnet に絞る）
    Set-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -RemoteAddress LocalSubnet -Profile Any -Enabled True
} else {
    New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Direction Inbound `
        -Protocol TCP -LocalPort 22 -Action Allow -RemoteAddress LocalSubnet -Profile Any | Out-Null
}

# 3. 鍵の登録（管理者のアカウントは ProgramData 側のファイルしか見ない）
$isAdminUser = (Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq "$env:COMPUTERNAME\$env:USERNAME" -or $_.Name -like "*\$env:USERNAME" })
if ($isAdminUser) {
    $keyFile = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
} else {
    $keyFile = Join-Path $env:USERPROFILE '.ssh\authorized_keys'
}
New-Item -ItemType Directory -Force (Split-Path $keyFile) | Out-Null
$existing = if (Test-Path $keyFile) { Get-Content $keyFile } else { @() }
if ($existing -notcontains $PublicKey.Trim()) {
    Add-Content -Path $keyFile -Value $PublicKey.Trim() -Encoding ascii
}
if ($isAdminUser) {
    icacls.exe $keyFile /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
}
Write-Host "[済] 鍵を $keyFile に登録"

# ssh の既定シェルは cmd のまま（tools/remote_test.py はどちらでも動く）

# 4. 開発機に伝える値
$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$disk = Get-Volume -DriveLetter C
Write-Host ""
Write-Host "==== 開発機の ~/.ssh/config に書く値 ===="
Write-Host "User     : $env:USERNAME"
Write-Host "HostName : $($ips -join ', ')"
Write-Host "==== この PC ===="
Write-Host "名前     : $env:COMPUTERNAME"
Write-Host "CPU      : $cpu（論理 $($cs.NumberOfLogicalProcessors)）"
Write-Host ("メモリ   : {0:N0} GB" -f ($cs.TotalPhysicalMemory / 1GB))
Write-Host ("C: の空き: {0:N0} GB（データと作業で 60GB 以上を見込む）" -f ($disk.SizeRemaining / 1GB))
