[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$manifestPath = Join-Path $root 'handoff-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$bad = @()
foreach ($entry in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
    if (-not $path.StartsWith(($root.TrimEnd('\') + '\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe path in manifest.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $bad += ($entry.path + ': missing'); continue }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.sha256) { $bad += ($entry.path + ': changed') }
}
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host $_ }; throw 'Initial handoff verification failed. Intentional development edits naturally change hashes; this is not a runtime test.' }
Write-Host ('Initial handoff files verified: {0}. This does NOT test the Windows app, which is not implemented in this handoff.' -f $manifest.files.Count)
