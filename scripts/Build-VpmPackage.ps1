<#
.SYNOPSIS
    Build a VPM zip package for release.

.PARAMETER Version
    Version number (e.g. 0.2.0). Defaults to the value in package.json.

.PARAMETER UnityPackagePath
    Path to the .unitypackage file to include in the zip.
    REQUIRED unless -AllowNoUnityPackage is given: omitting it produces a zip whose
    SHA256 differs from the final release asset (past incident source).

.PARAMETER AllowNoUnityPackage
    Explicitly build a zip WITHOUT the unitypackage (SHA256 will not match the
    final release asset — docs/index.json must be regenerated before publishing).

.PARAMETER AllowDirtyTree
    Build even when Code/ has uncommitted changes. The zip then no longer
    corresponds to the HEAD commit and is not reproducible from a clean clone.

.EXAMPLE
    .\scripts\Build-VpmPackage.ps1 -UnityPackagePath "C:\path\to\Iroca_Ver0.2.0.unitypackage"
#>
param(
    [string]$Version = "",
    [string]$UnityPackagePath = "",
    [switch]$AllowNoUnityPackage,
    [switch]$AllowDirtyTree
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root        = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$PackageId   = "com.yukkuri-aoba.iroca"
$RepoUrl     = "https://github.com/yukkuri-aoba/Iroca"
$PkgJsonPath = Join-Path $Root "package.json"
$IndexPath   = Join-Path $Root "docs\index.json"

Push-Location $Root
try {
    # --- Deterministic JSON writer -------------------------------------------
    # ConvertTo-Json の整形は PowerShell の版で変わる（5.1 = 4 スペース + コロン後 2 スペース +
    # 空 {} / [] を複数行へ展開、7 = 2 スペース）。package.json は zip に入るので、これは
    # 「どのホストで実行したか」が配布物の SHA256 に漏れることを意味する（実際に repo 内の
    # package.json は 5.1 形式、docs/index.json は 7 形式で保存されていた）。
    # 版に依存しない整形（2 スペース・LF・末尾改行）で固定する。
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    function Format-JsonString([string]$Text) {
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        foreach ($ch in $Text.ToCharArray()) {
            switch ($ch) {
                '"'  { [void]$sb.Append('\"'); break }
                '\'  { [void]$sb.Append('\\'); break }
                "`b" { [void]$sb.Append('\b'); break }
                "`f" { [void]$sb.Append('\f'); break }
                "`n" { [void]$sb.Append('\n'); break }
                "`r" { [void]$sb.Append('\r'); break }
                "`t" { [void]$sb.Append('\t'); break }
                default {
                    if ([int][char]$ch -lt 0x20) { [void]$sb.AppendFormat('\u{0:x4}', [int][char]$ch) }
                    else { [void]$sb.Append($ch) }
                }
            }
        }
        [void]$sb.Append('"')
        return $sb.ToString()
    }

    function ConvertTo-StableJson($Value, [int]$IndentLevel = 0) {
        $pad     = '  ' * $IndentLevel
        $padItem = '  ' * ($IndentLevel + 1)
        if ($null -eq $Value)    { return 'null' }
        if ($Value -is [bool])   { if ($Value) { return 'true' } else { return 'false' } }
        if ($Value -is [string]) { return (Format-JsonString $Value) }
        if ($Value -is [ValueType]) {
            return [System.Convert]::ToString($Value, [System.Globalization.CultureInfo]::InvariantCulture)
        }
        if ($Value -is [System.Collections.IEnumerable]) {
            $items = @($Value)
            if ($items.Count -eq 0) { return '[]' }
            $lines = @($items | ForEach-Object { "$padItem$(ConvertTo-StableJson $_ ($IndentLevel + 1))" })
            return "[`n" + ($lines -join ",`n") + "`n$pad]"
        }
        # それ以外は JSON オブジェクトとして扱う（ConvertFrom-Json の PSCustomObject）。
        # プロパティ順は入力の出現順のまま＝並びも版に依存させない。
        $props = @($Value.PSObject.Properties)
        if ($props.Count -eq 0) { return '{}' }
        $lines = @($props | ForEach-Object {
            "$padItem$(Format-JsonString $_.Name): $(ConvertTo-StableJson $_.Value ($IndentLevel + 1))"
        })
        return "{`n" + ($lines -join ",`n") + "`n$pad}"
    }

    function Write-StableJsonFile([string]$Path, $Value) {
        [System.IO.File]::WriteAllText($Path, ((ConvertTo-StableJson $Value 0) + "`n"), $utf8NoBom)
    }

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

    # -UnityPackagePath 省略はドキュメント警告だけでは防げなかった既知の事故経路
    # （非同梱 zip の SHA256 で docs/index.json を上書き→listing 不一致）。明示スイッチを要求する。
    if ($UnityPackagePath -eq "" -and -not $AllowNoUnityPackage) {
        throw ("-UnityPackagePath が指定されていません。最終 zip と SHA256 が一致しなくなります。`n" +
               "  unitypackage を同梱する:   -UnityPackagePath <path>`n" +
               "  意図的に省略する(検証用):  -AllowNoUnityPackage")
    }

    # --- Collect packaged files from git, not from the working tree ---
    # 作業ツリーを走査すると、ホスト Unity が生成した追跡外の .meta（現在 94 件）や
    # 手元の一時ファイルがそのまま配布物に入る。zip の内容がこの 1 台の作業ツリーに
    # 依存し、クリーン clone から同一 zip を再生成できない（＝listing の SHA256 を
    # 誰も再検証できない）。追跡ファイルだけを対象にして commit と 1:1 にする。
    # .meta を同梱しないのは .gitignore の方針どおり。asmdef の参照は全て名前ベースで
    # パッケージ内に GUID 参照される資産も無いため、Unity 側の再生成で問題ない。
    function Get-TrackedFiles([string[]]$Paths) {
        $out = & git -c core.quotepath=false ls-files -- $Paths
        if ($LASTEXITCODE -ne 0) { throw "git ls-files に失敗しました: $($Paths -join ', ')" }
        return @($out | Where-Object { $_ -ne "" })
    }

    # 作業ツリーが汚れていれば zip は HEAD の内容ではない。Code/ は リリース手順の中で
    # 編集しない場所なので、汚れていたら止める（package.json / CHANGELOG.md / docs/index.json は
    # この手順自体が書き換えるため対象外）。
    $dirty = @((& git status --porcelain -- Code) | Where-Object { $_ -ne "" })
    if ($LASTEXITCODE -ne 0) { throw "git status に失敗しました" }
    if ($dirty.Count -gt 0) {
        $listing = ($dirty -join "`n  ")
        if (-not $AllowDirtyTree) {
            throw ("Code/ に未コミットの変更があります。この zip は HEAD の内容と一致せず、" +
                   "クリーン clone から再生成できません。`n  $listing`n" +
                   "  コミットしてから再実行するか、意図的なら -AllowDirtyTree を付けてください。")
        }
        Write-Host "[WARN] -AllowDirtyTree: Code/ が未コミットのまま梱包します（再現不能）"
        Write-Host "  $listing"
    }

    $headCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git rev-parse に失敗しました" }
    # zip のタイムスタンプを実行時刻にすると、同じ内容でも実行のたびに SHA256 が変わる。
    # HEAD のコミット日時（UTC 固定）を全エントリに焼くことで、同一 commit → 同一 zip にする。
    $entryTime = [System.DateTimeOffset]::Parse(
        (& git log -1 --format=%cI HEAD).Trim()).ToUniversalTime()

    $packedFiles = @()
    $packedFiles += Get-TrackedFiles @("package.json","README.md","MANUAL.md","CHANGELOG.md","LICENSE")
    # Debug 衛星(Code/Debug/)は配布対象外。IrocaEditor.Debug.asmdef は defineConstraints 無し
    # ・autoReferenced=true で、同梱すると全ユーザーで常時コンパイルされ Debug ウィンドウが見えてしまう。
    # AssemblyInfo.cs の「Code/Debug が無ければ Debug ターゲットは単に存在しない」という非同梱運用想定と一致させる。
    $packedFiles += Get-TrackedFiles @("Code") | Where-Object { $_ -notlike "Code/Debug/*" }
    $packedFiles = @($packedFiles)
    if ($packedFiles.Count -eq 0) { throw "梱包対象の追跡ファイルが 0 件です（git 追跡状態を確認してください）" }
    # 収集順が環境で揺れないよう序数ソートで固定する（エントリ順も SHA256 に影響する）。
    [Array]::Sort($packedFiles, [System.StringComparer]::Ordinal)

    Write-Host "Source  : $headCommit ($($packedFiles.Count) tracked files)"

    # --- Update package.json FIRST (zip must include the updated version/url) ---
    $pkg.version = $Version
    $pkg.url     = $ReleaseUrl
    if ($pkg.PSObject.Properties["zipSHA256"]) {
        $pkg.PSObject.Properties.Remove("zipSHA256")
    }
    # BOM 付きだと release.yml の jq が parse に失敗するため BOM なし UTF-8 固定。
    Write-StableJsonFile $PkgJsonPath $pkg
    Write-Host "[OK] package.json updated"

    # --- Remove old zip ---
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    # --- Create zip ---
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::Create)
    $zip    = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)

    # 作業ツリーの改行は core.autocrlf 次第（Windows では CRLF、Linux では LF）。そのまま
    # 詰めると同じ commit でもチェックアウト環境で zip が変わる。git の blob と同じ LF に
    # 正規化して、zip の中身を「commit の中身」と一致させる。
    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    function ConvertTo-LfBytes([byte[]]$Bytes) {
        try   { $text = $utf8Strict.GetString($Bytes) }
        catch { return ,$Bytes }   # UTF-8 として読めないものは触らない
        if ($text.IndexOf("`r`n") -lt 0) { return ,$Bytes }
        return ,($utf8Strict.GetBytes($text.Replace("`r`n", "`n")))
    }

    function Add-ZipEntry([string]$AbsPath, [string]$EntryName, [switch]$Binary) {
        $entry = $zip.CreateEntry($EntryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $entryTime   # 実行時刻を焼かない（同一 commit → 同一 SHA256）
        $bytes = [System.IO.File]::ReadAllBytes($AbsPath)
        if (-not $Binary) { $bytes = ConvertTo-LfBytes $bytes }
        $entryStream = $entry.Open()
        $entryStream.Write($bytes, 0, $bytes.Length)
        $entryStream.Dispose()
        Write-Host "  + $EntryName"
    }

    # 追跡ファイル（package.json / README / MANUAL / CHANGELOG / LICENSE / Code、Debug 除く）
    foreach ($rel in $packedFiles) {
        $abs = Join-Path $Root ($rel -replace '/', '\')
        if (-not (Test-Path $abs)) { throw "追跡されているのに実体がありません: $rel" }
        Add-ZipEntry $abs $rel
    }

    # unitypackage（Unity のエクスポート出力なので、これを同梱した zip はバイト単位では再現しない）
    if ($UnityPackagePath -ne "") {
        $upkgAbs  = (Resolve-Path $UnityPackagePath).Path
        $upkgName = Split-Path $upkgAbs -Leaf
        Add-ZipEntry $upkgAbs $upkgName -Binary
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
    Write-StableJsonFile $IndexPath $index
    Write-Host "[OK] docs/index.json updated"

    # --- Next steps ---
    Write-Host ""
    Write-Host "=== Next Steps ==="
    Write-Host "1. (If not done) Re-run with -UnityPackagePath to finalize the zip + SHA256"
    Write-Host "2. git add package.json docs/index.json CHANGELOG.md"
    Write-Host "3. git commit"
    Write-Host "4. Merge develop -> main (PR or local merge)"
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