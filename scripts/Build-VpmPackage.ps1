<#
.SYNOPSIS
    Build a VPM zip package for release.

.PARAMETER Version
    Version number (e.g. 0.2.0). Defaults to the value in package.json.

.PARAMETER UnityPackagePath
    Path to the .unitypackage file to include in the zip.
    If omitted, a zip without unitypackage is created (SHA256 will differ from final zip).

.EXAMPLE
    .\scripts\Build-VpmPackage.ps1 -UnityPackagePath "C:\path\to\VACC_Ver0.2.0.unitypackage"
#>
param(
    [string]$Version = "",
    [string]$UnityPackagePath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root        = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PackageId   = "com.yukkuri-aoba.vrc-avatar-color-changer"
$RepoUrl     = "https://github.com/yukkuri-aoba/VRC_AvatarColorChanger"
$PkgJsonPath = Join-Path $Root "package.json"
$IndexPath   = Join-Path $Root "docs\index.json"

Push-Location $Root
try {
    # --- Read current package.json ---
    $pkg = Get-Content $PkgJsonPath -Encoding UTF8 | ConvertFrom-Json
    if ($Version -eq "") { $Version = $pkg.version }

    $ZipName    = "$PackageId-$Version.zip"
    $ZipPath    = Join-Path $Root $ZipName
    $ReleaseUrl = "$RepoUrl/releases/download/v$Version/$ZipName"

    Write-Host "Version : $Version"
    Write-Host "Output  : $ZipName"

    # Validate unitypackage path if specified
    if ($UnityPackagePath -ne "" -and -not (Test-Path $UnityPackagePath)) {
        throw "unitypackage not found: $UnityPackagePath"
    }

    # --- Update package.json FIRST (zip must include the updated version/url) ---
    $pkg.version = $Version
    $pkg.url     = $ReleaseUrl
    if ($pkg.PSObject.Properties["zipSHA256"]) {
        $pkg.PSObject.Properties.Remove("zipSHA256")
    }
    $pkg | ConvertTo-Json -Depth 10 | Set-Content $PkgJsonPath -Encoding UTF8 -NoNewline
    Write-Host "[OK] package.json updated"

    # --- Remove old zip ---
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    # --- Create zip ---
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    $zip    = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)

    function Add-ZipEntry([string]$AbsPath, [string]$EntryName) {
        $entry       = $zip.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        $fileStream  = [System.IO.File]::OpenRead($AbsPath)
        $fileStream.CopyTo($entryStream)
        $fileStream.Dispose()
        $entryStream.Dispose()
        Write-Host "  + $EntryName"
    }

    # Top-level files
    foreach ($f in @("package.json","README.md","MANUAL.md","CHANGELOG.md","LICENSE","$PackageId.Editor.asmdef")) {
        $abs = Join-Path $Root $f
        if (Test-Path $abs) { Add-ZipEntry $abs $f }
    }

    # Code/ directory (recursive)
    $codeDir = Join-Path $Root "Code"
    if (Test-Path $codeDir) {
        Get-ChildItem -Path $codeDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
            Add-ZipEntry $_.FullName $rel
        }
    }

    # unitypackage
    if ($UnityPackagePath -ne "") {
        $upkgAbs  = (Resolve-Path $UnityPackagePath).Path
        $upkgName = Split-Path $upkgAbs -Leaf
        Add-ZipEntry $upkgAbs $upkgName
    } else {
        Write-Host "  (no .unitypackage specified; re-run with -UnityPackagePath to include it)"
        Write-Host "  NOTE: SHA256 in docs/index.json will be updated again when you add the unitypackage."
    }

    $zip.Dispose()
    $stream.Dispose()
    Write-Host "[OK] zip created: $ZipName"

    # --- SHA256 ---
    $sha256 = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
    Write-Host "[OK] SHA256: $sha256"
    Write-Host "[OK] URL   : $ReleaseUrl"

    # --- Update docs/index.json ---
    $index        = Get-Content $IndexPath -Encoding UTF8 | ConvertFrom-Json
    $listingEntry = $pkg | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $listingEntry | Add-Member -MemberType NoteProperty -Name "zipSHA256" -Value $sha256 -Force

    if ($null -eq $index.packages.PSObject.Properties[$PackageId]) {
        $index.packages | Add-Member -MemberType NoteProperty -Name $PackageId `
            -Value ([PSCustomObject]@{ versions = [PSCustomObject]@{} })
    }
    $verObj = $index.packages.$PackageId.versions
    if ($null -eq $verObj.PSObject.Properties[$Version]) {
        $verObj | Add-Member -MemberType NoteProperty -Name $Version -Value $listingEntry
    } else {
        $verObj.$Version = $listingEntry
    }
    $index | ConvertTo-Json -Depth 20 | Set-Content $IndexPath -Encoding UTF8 -NoNewline
    Write-Host "[OK] docs/index.json updated"

    # --- Next steps ---
    Write-Host ""
    Write-Host "=== Next Steps ==="
    Write-Host "1. (If not done) Re-run with -UnityPackagePath to finalize the zip + SHA256"
    Write-Host "2. git add package.json docs/index.json CHANGELOG.md"
    Write-Host "3. git commit"
    Write-Host "4. Merge feature/refactor-all -> main (PR or local merge)"
    Write-Host "5. git push origin main"
    Write-Host "6. git tag v$Version && git push origin v$Version"
    Write-Host "   -> CI will create a DRAFT release on GitHub"
    Write-Host "7. Upload $ZipName to the draft release, then publish"
    Write-Host "   $RepoUrl/releases"
    Write-Host ""
    Write-Host "zip location:"
    Write-Host "  $ZipPath"

} finally {
    Pop-Location
}