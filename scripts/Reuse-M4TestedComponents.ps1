[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PortableDirectory,
    [Parameter(Mandatory=$true)][string]$ComponentEvidencePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$bundle=[IO.Path]::GetFullPath($PortableDirectory,$root)
$evidencePath=[IO.Path]::GetFullPath($ComponentEvidencePath,$root)
function Within([string]$Path,[string]$Parent){
    if(-not $Path.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Generated path escaped authorized root.'}
    for($part=$Path;$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked paths are prohibited.'}
    }
}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
Within $bundle (Join-Path $root 'artifacts');Within $evidencePath (Join-Path $root '.local/M4')
if(Test-Path -LiteralPath (Join-Path $bundle 'Data')){throw 'Reuse is only for a fresh generated publication without user Data.'}
$manifestPath=Join-Path $bundle 'release-manifest.json'
$declared=(Get-Content -LiteralPath ($bundle+'.manifest.sha256') -Raw).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0].Trim([char]0xFEFF)
if((Hash $manifestPath) -ne $declared){throw 'Publication manifest hash changed before component reuse.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
foreach($entry in $manifest.files){
    $file=[IO.Path]::GetFullPath((Join-Path $bundle $entry.path));Within $file $bundle
    if((Hash $file) -ne $entry.sha256 -or (Get-Item -LiteralPath $file).Length -ne $entry.bytes){throw ('Published file changed: '+$entry.path)}
}
$evidence=Get-Content -LiteralPath $evidencePath -Raw|ConvertFrom-Json
$source=[IO.Path]::GetFullPath($evidence.Source);Within $source (Join-Path $root '.local/M4')
$allowed=@('ProjectEcho.Core.dll','ProjectEcho.Capture.Windows.dll','ProjectEcho.Export.dll',
    'CaptureHost/ProjectEcho.Core.dll','CaptureHost/ProjectEcho.Capture.Windows.dll','CaptureHost/ProjectEcho.Export.dll','CaptureHost/ProjectEcho.CaptureHost.dll')
$entries=@($evidence.Components)
if($entries.Count -ne $allowed.Count){throw 'Unexpected tested component count.'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$results=@()
foreach($entry in $entries){
    $relative=$entry.RelativePath.Replace('\','/')
    if($relative -notin $allowed -or -not $seen.Add($relative)){throw 'Unexpected or duplicate tested component.'}
    $from=[IO.Path]::GetFullPath((Join-Path $source $relative));$to=[IO.Path]::GetFullPath((Join-Path $bundle $relative))
    Within $from $source;Within $to $bundle
    if((Hash $from) -ne $entry.Sha256 -or (Get-Item -LiteralPath $from).Length -ne $entry.Bytes){throw 'Frozen tested component changed.'}
    $fromName=[Reflection.AssemblyName]::GetAssemblyName($from);$toName=[Reflection.AssemblyName]::GetAssemblyName($to)
    if($fromName.FullName -ne $toName.FullName){throw 'Tested and published assembly identities differ.'}
    $before=Hash $to
    Copy-Item -LiteralPath $from -Destination $to -Force
    if((Hash $to) -ne $entry.Sha256){throw 'Component copy hash mismatch.'}
    $results += [ordered]@{path=$relative;publishedBeforeSha256=$before;testedAndPublishedSha256=$entry.Sha256;bytes=$entry.Bytes}
}
$record=[ordered]@{schemaVersion=1;purpose='Reuse exact tested production DLLs; publish-generated debug metadata must not substitute different bytes for the long-run binaries';
    sourceEvidenceSha256=Hash $evidencePath;components=$results;longRunResult='Consult external M4 validation report; copying alone is not test PASS';privateDataCopied=$false}
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundle 'tested-components.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Update-PortableManifest.ps1') -PortableDirectory $bundle
Write-Host ('Reused and hash-verified '+$results.Count+' exact tested DLLs in '+$bundle)
