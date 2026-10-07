[CmdletBinding()]
param([string]$OutputDirectory='.local/M5C/sherpa-bundle',[string]$ModelsDirectory='.local/M5B/sherpa-bundle-02',[string]$NativeDirectory='.local/M5C/native-worker-02')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Safe([string]$p){
 $full=[IO.Path]::GetFullPath($p,$root)
 if(-not $full.StartsWith((Join-Path $root '.local')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only project-local dependency/evidence folders.'}
 for($a=$full;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse linked path.'}}
 return $full
}
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()}
$out=Safe $OutputDirectory;$models=Safe $ModelsDirectory;$native=Safe $NativeDirectory
if(Test-Path -LiteralPath $out){throw 'Use a new overlay directory.'}
$meta=Get-Content -LiteralPath (Join-Path $native 'native-worker.json') -Raw|ConvertFrom-Json
foreach($entry in $meta.Files){
 $file=Safe (Join-Path $native $entry.Path)
 if((Hash $file) -ne $entry.Sha256 -or (Get-Item -LiteralPath $file).Length -ne $entry.Bytes){throw ('Native dependency changed: '+$entry.Path)}
}
Copy-Item -LiteralPath $models -Destination $out -Recurse
foreach($folder in @('Tools/Sherpa','LICENSES/Sherpa')){
 foreach($file in Get-ChildItem -LiteralPath (Join-Path $native $folder) -File -Recurse){
  $source=Safe $file.FullName;$relative=[IO.Path]::GetRelativePath($native,$source);$dest=Safe (Join-Path $out $relative)
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))|Out-Null
  Copy-Item -LiteralPath $source -Destination $dest -Force
  if((Hash $dest) -ne (Hash $source)){throw 'Native overlay copy changed bytes.'}
 }
}
# Retain a complete upstream comparison image; it is never the normal processing path.
foreach($dll in @('onnxruntime.dll','onnxruntime_providers_shared.dll')){Copy-Item -LiteralPath (Join-Path $out ('Tools/Sherpa/'+$dll)) -Destination (Join-Path $out ('Tools/Sherpa/Legacy/'+$dll))}
Copy-Item -LiteralPath (Join-Path $native 'native-worker.json') -Destination (Join-Path $out 'LICENSES/Sherpa/ProjectEcho-native-worker.json')
$settingsPath=Join-Path $out 'bundled-sherpa.json'
$settings=Get-Content -LiteralPath $settingsPath -Raw|ConvertFrom-Json
$settings.Schema='m5c-bundled-sherpa-3';$settings.ExecutableSha256=$meta.ExecutableSha256
$settings.Modification='ProjectEcho native worker: same verified segmentation and embeddings; known speaker count uses global spherical k-means, automatic count explicitly retains upstream complete-link. Original reference retained.'
$settings.ResourceEvidence='Known-count spherical algorithm: actual M5C three-hour repeated public-voice run; final fault preflight is separately checked. Automatic-count and dense natural meetings are not certified by that run. See M5C report for exact binary hashes and scope.'
$settings|Add-Member -NotePropertyName KnownCountAlgorithm -NotePropertyValue $meta.KnownCountAlgorithm
$settings|Add-Member -NotePropertyName AutomaticAlgorithm -NotePropertyValue $meta.AutomaticAlgorithm
$settings|Add-Member -NotePropertyName WorkerVersion -NotePropertyValue $meta.WorkerVersion
$settings|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $settingsPath -Encoding utf8
$thirdPath=Join-Path $out 'THIRD_PARTY.sherpa.json'
$third=Get-Content -LiteralPath $thirdPath -Raw|ConvertFrom-Json
$third|Add-Member -NotePropertyName ProjectEchoWorker -NotePropertyValue ([ordered]@{Version=$meta.WorkerVersion;ExecutableSha256=$meta.ExecutableSha256;Source='LICENSES/Sherpa/ProjectEcho-Diarization-Native-source';Build='scripts/Build-M5CSpeakerWorker.ps1';NativeDependencies='Original sherpa C API and ONNX Runtime; no Python';OriginalReference='Tools/Sherpa/Legacy/sherpa-onnx-offline-speaker-diarization.exe';OriginalReferenceQualification='Upstream original keeps its original path-encoding limitations; normal runs use the new UTF-8 worker.'})
$third|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $thirdPath -Encoding utf8
$files=@(Get-ChildItem -LiteralPath $out -File -Recurse|ForEach-Object{[ordered]@{path=[IO.Path]::GetRelativePath($out,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=Hash $_.FullName}})
[ordered]@{status='PASS';directory=$out;workerSha256=$meta.ExecutableSha256;fileCount=$files.Count;privateDataCopied=$false;files=$files}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath ($out+'.verification.json') -Encoding utf8
Write-Host ('Prepared reviewed M5C overlay: '+$out)
