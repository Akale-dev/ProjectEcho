[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PortableDirectory)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [IO.Path]::GetFullPath($PortableDirectory)
$allowed = (Join-Path $root 'artifacts').TrimEnd('\') + '\'
if (-not $output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest folder must be under this project artifacts.' }
if (-not (Test-Path -LiteralPath (Join-Path $output 'portable.json'))) { throw 'Missing portable marker.' }
for ($ancestor = $output; $ancestor -and $ancestor.Length -ge $root.Length; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Manifest folder must not pass through a link.' }
}
Get-ChildItem -LiteralPath $output -Recurse -Directory | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A portable release must not contain linked directories.' }
    if ($_.Name -match '^(\.local|\.tools|Data|profiles?|node_modules)$') { throw ('Unexpected development/private directory in portable folder: ' + $_.Name) }
}
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $output 'release-manifest.json') } | Sort-Object FullName | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A portable release must not contain linked files.' }
    $relative = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/')
    if ($relative -match '(^|/)(\.local|\.tools|Data|profiles?|node_modules)(/|$)' -or $_.Extension -in @('.pdb','.wav','.mp3','.m4a','.dmp')) { throw ('Unexpected development/private data in portable folder: ' + $relative) }
    @{path=$relative;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = @{schemaVersion=1;generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');scope='All bundled files except this self-referential manifest';fileCount=$files.Count;totalBytes=($files | Measure-Object -Property bytes -Sum).Sum;files=$files}
$manifestPath = Join-Path $output 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
($manifestHash + '  ' + [IO.Path]::GetFileName($output) + '/release-manifest.json') | Set-Content -LiteralPath ($output + '.manifest.sha256') -Encoding utf8
Write-Host ('Release inventory: ' + $files.Count + ' files; manifest SHA256 ' + $manifestHash)
