[CmdletBinding()]
param([switch]$Download)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$local=Join-Path $root '.local/M5'
$lock=Get-Content (Join-Path $PSScriptRoot 'm5-sherpa-sources.json') -Raw|ConvertFrom-Json
function Hash($path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Local($relative){$full=[IO.Path]::GetFullPath((Join-Path $local $relative));if(!$full.StartsWith($local+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Dependency path outside project.'};for($p=$full;$p;$p=[IO.Path]::GetDirectoryName($p)){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse reparse dependency directory.'}};return $full}
foreach($item in $lock.Downloads){
 $path=Local $item.Path
 if(Test-Path -LiteralPath $path){if((Hash $path) -ne $item.Sha256){throw ('Existing dependency hash differs: '+$item.Path)};continue}
 if(!$Download){throw ('Missing '+$item.Path+'. Explicit -Download is required for developer setup. The product never downloads models.')}
 New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force|Out-Null
 $temp=$path+'.download-'+[Guid]::NewGuid().ToString('N')
 Invoke-WebRequest $item.Url -OutFile $temp
 if((Hash $temp) -ne $item.Sha256){throw ('Official download hash changed; retained '+$temp)}
 Move-Item -LiteralPath $temp -Destination $path
}
New-Item -ItemType Directory -Path (Local 'runtime') -Force|Out-Null
foreach($archive in @(@{File='downloads/runtime.tar.bz2';Prefix='sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts/'},@{File='downloads/segmentation.tar.bz2';Prefix='sherpa-onnx-pyannote-segmentation-3-0/'})){
 if(Test-Path -LiteralPath (Local ('runtime/'+$archive.Prefix.TrimEnd('/')))){continue}
 $entries=& tar -tf (Local $archive.File);if($LASTEXITCODE -ne 0){throw 'Cannot inspect archive.'}
 foreach($entry in $entries){if(!$entry.StartsWith($archive.Prefix,[StringComparison]::Ordinal) -or $entry.Contains('..') -or $entry.StartsWith('/') -or $entry.Contains(':')){throw 'Unsafe archive path.'}}
 & tar -xf (Local $archive.File) -C (Local 'runtime');if($LASTEXITCODE -ne 0){throw 'Dependency extraction failed.'}
}
foreach($item in $lock.Extracted){if(!(Test-Path -LiteralPath (Local $item.Path)) -or (Hash (Local $item.Path)) -ne $item.Sha256){throw ('Extracted file differs: '+$item.Path)}}
Write-Output 'Locked M5 native runtime, models and license source files verified inside ProjectEcho. Prepare-M5SherpaBundle.ps1 creates the portable overlay; no audio or microphone was used.'
