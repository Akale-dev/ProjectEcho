[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PortableDirectory,[Parameter(Mandatory=$true)][string]$ExtractDirectory,[Parameter(Mandatory=$true)][string]$EvidencePath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Local([string]$Path,[string]$Prefix){
    $p=[IO.Path]::GetFullPath($Path,$projectRoot)
    if(-not $p.StartsWith((Join-Path $projectRoot $Prefix)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path outside generated project destination.'}
    for($ancestor=$p;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse path rejected.'}
    }
    return $p
}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
$bundle=Local $PortableDirectory 'artifacts';$extract=Local $ExtractDirectory 'artifacts';$evidence=Local $EvidencePath '.local/M5'
if(Test-Path -LiteralPath $extract){throw 'Choose a new extraction directory.'}
$zip=$bundle+'.zip';$manifestPath=Join-Path $bundle 'release-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if(@($manifest.files|Group-Object path|Where-Object Count -gt 1).Count){throw 'Duplicate manifest path.'}
$expected=@{};foreach($row in $manifest.files){$expected[$row.path]=$row}
$expected['release-manifest.json']=@{sha256=(Hash $manifestPath);bytes=(Get-Item -LiteralPath $manifestPath).Length}
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    if($archive.Entries.Count -ne $expected.Count){throw 'ZIP entry count differs from complete manifest.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $archive.Entries){
        if(-not $expected.ContainsKey($entry.FullName) -or -not $seen.Add($entry.FullName) -or $entry.FullName -match '(^|/)(\.\.?|Data|profiles?|\.local)(/|$)' -or [IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':')){throw ('Unexpected/unsafe ZIP entry: '+$entry.FullName)}
    }
}finally{$archive.Dispose()}
[IO.Compression.ZipFile]::ExtractToDirectory($zip,$extract)
$verified=@()
foreach($path in $expected.Keys){
    $file=Join-Path $extract $path;$row=$expected[$path]
    if((Get-Item -LiteralPath $file).Length -ne $row.bytes -or (Hash $file) -ne $row.sha256){throw ('Extracted byte/hash mismatch: '+$path)}
    $verified += $path
}
$manifestHash=Hash (Join-Path $extract 'release-manifest.json')
($manifestHash+'  '+[IO.Path]::GetFileName($extract)+'/release-manifest.json')|Set-Content -LiteralPath ($extract+'.manifest.sha256') -Encoding utf8
$core=Join-Path $projectRoot 'src/ProjectEcho.Core/bin/Release/net10.0/ProjectEcho.Core.dll'
$coreHash=Hash $core
foreach($path in @('ProjectEcho.Core.dll','CaptureHost/ProjectEcho.Core.dll')){
    if((Hash (Join-Path $extract $path)) -ne $coreHash){throw 'Bundled Core differs from final tested assembly.'}
}
foreach($path in @('ProjectEcho.runtimeconfig.json','CaptureHost/ProjectEcho.CaptureHost.runtimeconfig.json')){
    $config=Get-Content -LiteralPath (Join-Path $extract $path) -Raw|ConvertFrom-Json
    if(-not $config.runtimeOptions.PSObject.Properties['includedFrameworks'] -or @($config.runtimeOptions.includedFrameworks|Where-Object version -ne '10.0.12').Count){throw 'Expected self-contained 10.0.12 runtime.'}
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($evidence))|Out-Null
[ordered]@{status='PASS';zipSha256=(Hash $zip);zipBytes=(Get-Item -LiteralPath $zip).Length;manifestSha256=$manifestHash;manifestFiles=$manifest.fileCount;zipEntries=$verified.Count;extractedDirectory=$extract;testedCoreSha256=$coreHash;sourceMode='ZIP integrity and self-contained configuration; no capture';cleanWindows='NOT_RUN';verifiedFiles=$verified}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $evidence -Encoding utf8
Write-Host ('PASS '+$verified.Count+' extracted files; '+$zip)
