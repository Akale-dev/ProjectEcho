param([Parameter(Mandatory)][string]$Bundle,[string]$ExtractDirectory='H:\AI\ProjectEcho\artifacts\M5F最终中文验包')
$ErrorActionPreference = 'Stop'
$root = 'H:\AI\ProjectEcho'
$evidence = Join-Path $root '.local/M5F'
$archivePath = $Bundle + '.zip'
$destination = [IO.Path]::GetFullPath($ExtractDirectory)
if (-not $destination.StartsWith((Join-Path $root 'artifacts')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Extraction must stay inside project artifacts.' }
if (Test-Path -LiteralPath $destination) { throw 'Extraction target must be fresh.' }
$manifestBytes = [IO.File]::ReadAllBytes((Join-Path $Bundle 'release-manifest.json'))
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
$metadata = Get-Content -LiteralPath ($Bundle + '.package.json') -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $metadata.archiveSha256) { throw 'ZIP hash mismatch.' }
$inventory = @{}
foreach ($entry in $manifest.files) { $inventory[$entry.path.Replace('\','/')] = $entry }
$prefix = [IO.Path]::GetFileName($Bundle) + '/'
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $entry.FullName.StartsWith($prefix, [StringComparison]::Ordinal)) { throw 'Unexpected archive root.' }
        $relative = $entry.FullName.Substring($prefix.Length)
        if (-not $seen.Add($relative) -or $relative -match '(^|/)(Data|profiles?|\.local|\.git)(/|$)' -or [IO.Path]::GetExtension($relative) -in @('.wav','.partial','.mp3','.m4a')) { throw 'Duplicate/private/audio archive entry.' }
        $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
        if (-not $target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive path escape.' }
        $source = $entry.Open()
        try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($source)) }
        finally { $source.Dispose() }
        if ($relative -eq 'release-manifest.json') {
            if ($hash -ne [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($manifestBytes))) { throw 'Archived manifest mismatch.' }
        } else {
            $expected = $inventory[$relative]
            if (-not $expected -or $entry.Length -ne $expected.bytes -or $hash -ne $expected.sha256) { throw ('Archive content mismatch: ' + $relative) }
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
    }
    if ($seen.Count -ne $manifest.fileCount + 1) { throw 'Missing inventory entries.' }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath ($Bundle + '.manifest.sha256') -Destination ($destination + '.manifest.sha256')
$old = Join-Path $root 'artifacts/ProjectEcho-win-x64-20261005-M5D-QwenFix-Final'
$oldManifest = Get-Content (Join-Path $old 'release-manifest.json') -Raw | ConvertFrom-Json
$assets = @($oldManifest.files.path | Where-Object { $_ -match '^(Tools|Models|LICENSES)/|^bundled-' })
foreach ($asset in $assets) {
    if ((Get-FileHash -LiteralPath (Join-Path $old $asset)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $destination $asset)).Hash) { throw ('Unexpected ASR asset change: ' + $asset) }
}
& (Join-Path $root 'scripts/Test-Portable.ps1') -PortableDirectory $destination
[ordered]@{
    status='PASS'; verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O'); archiveSha256=$metadata.archiveSha256
    archiveBytes=$metadata.archiveBytes; listedFiles=$manifest.fileCount; zipEntriesVerified=$seen.Count
    extractedTo=$destination; privateDataInZip=$false; originalAsrAssetsHashIdentical=$true
    actualExtractedWpfAndHostNoCapture='PASS'; captureStarted=$false; microphoneStarted=$false
    windows19043Capture='NOT_RUN'; jabraEndpointCapture='NOT_RUN'; cleanWindows='NOT_RUN'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'zip-verification-final.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $evidence 'zip-verification-final.json')
