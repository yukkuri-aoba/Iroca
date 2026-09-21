<#
.SYNOPSIS
    AI マスク提案の ONNX モデル（encoder/decoder）を開発用に取得する。

.DESCRIPTION
    製品では Iroca ウィンドウのボタンからダウンロードするが、pytest の主経路ゲート
    （test_autotune_evidence / test_assisted_include / test_sam_e2e）も同じモデルを要求する。
    モデルが無いとそれらは **fail ではなく skip** になり、「走っていないのに緑」になるため、
    clone 直後のセットアップでこのスクリプトを 1 回実行する。

    配布元・sha256・バイト数は製品コード Code/MaskSuggest/MaskSuggestModelDownload.cs
    から読み取る（唯一の正を二重化しないため。ハッシュを書き写さない）。

.PARAMETER Destination
    保存先。既定は製品と同じ %LOCALAPPDATA%\Iroca\Models
    （dev_safe 側のスクリプトは環境変数 IROCA_MODELS_DIR でも上書きできる）。

.PARAMETER Force
    既にあるファイルもダウンロードし直す。既定は sha256 が一致するものはスキップ。

.EXAMPLE
    pwsh scripts/Fetch-Models.ps1
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Iroca\Models'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$srcPath = Join-Path $PSScriptRoot '..\Code\MaskSuggest\MaskSuggestModelDownload.cs'
if (-not (Test-Path $srcPath)) {
    throw "製品コードが見つかりません: $srcPath"
}
$src = Get-Content -Raw -LiteralPath $srcPath

$repoMatch = [regex]::Match($src, 'ModelRepo\s*=\s*"([^"]+)"')
$branchMatch = [regex]::Match($src, 'ModelBranch\s*=\s*"([^"]+)"')
if (-not ($repoMatch.Success -and $branchMatch.Success)) {
    throw "MaskSuggestModelDownload.cs から配布元(ModelRepo/ModelBranch)を読み取れませんでした。"
}
$baseUrl = "https://raw.githubusercontent.com/$($repoMatch.Groups[1].Value)/$($branchMatch.Groups[1].Value)/"

$files = [regex]::Matches($src, '\(\s*"([^"]+\.onnx)"\s*,\s*"([0-9a-fA-F]{64})"\s*,\s*(\d+)L\s*\)')
if ($files.Count -eq 0) {
    throw "MaskSuggestModelDownload.cs からモデル一覧を読み取れませんでした。"
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

Write-Host "配布元: $baseUrl"
Write-Host "保存先: $Destination"

foreach ($m in $files) {
    $name = $m.Groups[1].Value
    $sha = $m.Groups[2].Value.ToLowerInvariant()
    $size = [int64]$m.Groups[3].Value
    $dest = Join-Path $Destination $name

    if ((-not $Force) -and (Test-Path $dest)) {
        $have = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($have -eq $sha) {
            Write-Host "  [skip] $name (sha256 一致)"
            continue
        }
        Write-Host "  [redo] $name (sha256 不一致: $have)"
    }

    $tmp = "$dest.part"
    Write-Host "  [get ] $name ($([math]::Round($size / 1MB, 1)) MB)"
    # 進捗バーはリダイレクト環境で極端に遅くなるので切る
    $oldPref = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri ($baseUrl + $name) -OutFile $tmp -UseBasicParsing
    } finally {
        $ProgressPreference = $oldPref
    }

    $actualSize = (Get-Item -LiteralPath $tmp).Length
    $actualSha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSize -ne $size -or $actualSha -ne $sha) {
        Remove-Item -LiteralPath $tmp -Force
        throw "$name の検証に失敗しました (size $actualSize/$size, sha256 $actualSha/$sha)。部分ファイルは削除しました。"
    }
    Move-Item -LiteralPath $tmp -Destination $dest -Force
    Write-Host "  [ ok ] $name"
}

Write-Host "完了。埋め込みキャッシュは初回のテスト実行時に再生成される。"
