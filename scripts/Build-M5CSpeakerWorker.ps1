[CmdletBinding()]
param([string]$OutputDirectory='.local/M5C/native-worker-01')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
function Local([string]$path){
 $full=if([IO.Path]::IsPathRooted($path)){[IO.Path]::GetFullPath($path)}else{[IO.Path]::GetFullPath((Join-Path $root $path))}
 if(!$full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Build paths must remain inside ProjectEcho.'}
 for($p=$full;$p;$p=[IO.Path]::GetDirectoryName($p)){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse reparse build path.'}}
 return $full
}
$output=Local $output
if(Test-Path -LiteralPath $output){throw 'Use a new native worker output directory.'}
function Hash($path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
$source=Local 'src/ProjectEcho.Diarization.Native'
$lock=Get-Content -LiteralPath (Join-Path $source 'deps.lock.json') -Raw|ConvertFrom-Json
foreach($item in $lock.Files){$path=Local $item.Path;if(!(Test-Path -LiteralPath $path) -or (Hash $path) -ne $item.Sha256){throw ('Missing or changed pinned dependency: '+$item.Path)}}
$toolchain=Local '.tools/vulkan-build/w64devkit/bin'
$runtime=Local '.local/M5/runtime/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts'
$toolsDir=Join-Path $output 'Tools/Sherpa';$licenses=Join-Path $output 'LICENSES/Sherpa';$build=Join-Path $output 'build';$legacy=Join-Path $toolsDir 'Legacy'
New-Item -ItemType Directory -Force $toolsDir,$licenses,$build,$legacy|Out-Null
$oldPath=$env:PATH
try{
 # Child-process search only; never modify user/machine PATH.
 $env:PATH=$toolchain+';'+$oldPath
 & (Join-Path $toolchain 'windres.exe') -I $source (Join-Path $source 'app.rc') -o (Join-Path $build 'app.res.o') 2> (Join-Path $build 'resource.log')
 if($LASTEXITCODE -ne 0){throw 'Native manifest compilation failed.'}
 $exe=Join-Path $toolsDir 'sherpa-onnx-offline-speaker-diarization.exe'
 $flags=@('-std=c++17','-D_stdcall=__stdcall','-O2','-Wall','-Wextra','-static','-municode','-Wl,--no-insert-timestamp','-I',(Join-Path $source 'include'),'-I',(Join-Path $runtime 'include'),(Join-Path $source 'main.cpp'),(Join-Path $build 'app.res.o'),(Join-Path $runtime 'lib/onnxruntime.dll'),(Join-Path $runtime 'lib/sherpa-onnx-c-api.dll'),'-o',$exe)
 & (Join-Path $toolchain 'g++.exe') @flags 2> (Join-Path $build 'compile.log')
 if($LASTEXITCODE -ne 0){throw ('Native compilation failed. See '+(Join-Path $build 'compile.log'))}
 $testExe=Join-Path $build 'native-tests.exe'
 $testFlags=@('-std=c++17','-D_stdcall=__stdcall','-O2','-Wall','-Wextra','-Wno-unused-function','-static','-Wl,--no-insert-timestamp','-I',(Join-Path $source 'include'),'-I',(Join-Path $runtime 'include'),(Join-Path $source 'tests.cpp'),(Join-Path $runtime 'lib/onnxruntime.dll'),(Join-Path $runtime 'lib/sherpa-onnx-c-api.dll'),'-o',$testExe)
 & (Join-Path $toolchain 'g++.exe') @testFlags 2> (Join-Path $build 'tests-build.log')
 if($LASTEXITCODE -ne 0){throw 'Native tests compilation failed.'}
 foreach($name in @('sherpa-onnx-c-api.dll','onnxruntime.dll','onnxruntime_providers_shared.dll')){Copy-Item -LiteralPath (Join-Path $runtime ('lib/'+$name)) -Destination $build}
 & $testExe > (Join-Path $build 'tests.stdout.txt') 2> (Join-Path $build 'tests.stderr.txt')
 if($LASTEXITCODE -ne 0){throw 'Native numerical regression tests failed.'}
 & (Join-Path $toolchain 'objdump.exe') -p $exe > (Join-Path $build 'pe-imports.txt')
 if($LASTEXITCODE -ne 0){throw 'PE dependency inspection failed.'}
}finally{$env:PATH=$oldPath}
foreach($name in @('sherpa-onnx-c-api.dll','onnxruntime.dll','onnxruntime_providers_shared.dll')){Copy-Item -LiteralPath (Join-Path $runtime ('lib/'+$name)) -Destination $toolsDir}
Copy-Item -LiteralPath (Join-Path $runtime 'bin/sherpa-onnx-offline-speaker-diarization.exe') -Destination $legacy
foreach($name in @('3dspeaker-LICENSE.txt','eres2net-model-card.md','onnxruntime-LICENSE.txt','onnxruntime-ThirdPartyNotices.txt','hclust-cpp-LICENSE.txt','kaldi-native-fbank-LICENSE.txt','eigen-COPYING.MPL2.txt','json-v3.12.0-LICENSE.txt','kissfft-COPYING.txt','kissfft-BSD-3-Clause.txt')){Copy-Item -LiteralPath (Local ('.local/M5/provenance/'+$name)) -Destination $licenses}
Copy-Item -LiteralPath (Local '.local/M5/provenance/LICENSE') -Destination (Join-Path $licenses 'sherpa-onnx-LICENSE.txt')
Copy-Item -LiteralPath (Local '.tools/vulkan-build/w64devkit/COPYING.MinGW-w64-runtime.txt') -Destination (Join-Path $licenses 'MinGW-w64-runtime-LICENSE.txt')
foreach($name in @('GCC-GPL-3.0.txt','GCC-Runtime-Exception-3.1.txt')){Copy-Item -LiteralPath (Local ('.tools/qwen-aligner-avx2-runtime/licenses/'+$name)) -Destination $licenses}
Copy-Item -LiteralPath (Join-Path $source 'THIRD_PARTY.md') -Destination (Join-Path $licenses 'ProjectEcho-Diarization-Native-NOTICE.md')
$sourceCopy=Join-Path $licenses 'ProjectEcho-Diarization-Native-source'
Copy-Item -LiteralPath $source -Destination $sourceCopy -Recurse
$metadata=@{Schema='projectecho-speaker-native-1';WorkerVersion='M5C-spherical-v1-faultfix1';KnownCountAlgorithm='spherical-kmeans-global-v1';AutomaticAlgorithm='sherpa-complete-link-global-v1';Restarts=16;MaxIterations=80;Seed='SplitMix64 restart+1, k-means++ cosine-distance-squared';Objective='Unweighted sum of cosine to normalized cluster centroid';Quantities='All finite per-window embedding vectors; no transcript input';SherpaVersion=$lock.Sherpa;OnnxRuntimeVersion=$lock.OrtRuntime;Compiler=$lock.Compiler;Executable='Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe';ExecutableSha256=(Hash $exe);OriginalExecutable='Tools/Sherpa/Legacy/sherpa-onnx-offline-speaker-diarization.exe';OriginalExecutableSha256=(Hash (Join-Path $legacy 'sherpa-onnx-offline-speaker-diarization.exe'));SourceSha256=(Hash (Join-Path $source 'main.cpp'));DependencyLockSha256=(Hash (Join-Path $source 'deps.lock.json'));CpuBaseline='x86-64 compiler default; no AVX-specific build flags';PythonRequired=$false;ModelFilesBundled=$false;PrivateAudioOrFeaturesBundled=$false;Files=@(Get-ChildItem -LiteralPath $toolsDir,$licenses -Recurse -File|ForEach-Object{@{Path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');Bytes=$_.Length;Sha256=(Hash $_.FullName)}})}
$metadata|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'native-worker.json') -Encoding utf8
Write-Output $output



