[CmdletBinding()]
param([string]$Destination='H:\AI\Qwen',[switch]$DownloadModel)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationRoot=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if($destinationRoot -ne 'H:\AI\Qwen'){throw 'This install is restricted to the explicitly authorized H:\AI\Qwen directory.'}
foreach($path in @($destinationRoot,(Join-Path $root '.tools\transcribe-source'),(Join-Path $root '.local\M4\runtime'))){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked install paths are not allowed'}
 }
}
$version='transcribe-077110e-ProjectEcho-20261004'
$runtime=Join-Path $destinationRoot $version
if(Test-Path -LiteralPath $runtime){throw 'Refuse to overwrite an existing runtime installation'}
$modelSha='f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8'
$modelRevision='28c66fba077f1359a42b52c55b618a24cdb6e0e7'
$model=Join-Path $destinationRoot 'models\Qwen3-ASR-0.6B-Q8_0.gguf'
$modelUrl='https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/'+$modelRevision+'/Qwen3-ASR-0.6B-Q8_0.gguf'
if(!(Test-Path -LiteralPath $model)){
 if(!$DownloadModel){throw 'Model absent. DownloadModel must be explicitly selected; software never auto-downloads models.'}
 New-Item -ItemType Directory -Path (Split-Path -Parent $model) -Force | Out-Null
 $partial=$model+'.partial'
 if(Test-Path -LiteralPath $partial){throw 'Existing partial download retained; choose a fresh reviewed download operation'}
 & curl.exe --fail --location --retry 3 --connect-timeout 30 --output $partial $modelUrl
 if($LASTEXITCODE -ne 0){throw 'Model download failed'}
 if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $modelSha){throw 'Model SHA-256 mismatch; partial retained'}
 Move-Item -LiteralPath $partial -Destination $model
}
if((Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant() -ne $modelSha){throw 'Existing model is not the locked Q8_0 revision'}
$evidence=Join-Path $root '.local\M4\runtime'
$source=Join-Path $root '.tools\transcribe-source'
$licenses=Join-Path $runtime 'LICENSES'
foreach($name in @('cpu','vulkan')){
 $built=Join-Path $root ('.tools\qwen-'+$name+'\bin\transcribe-cli.exe')
 $record=Get-Content -LiteralPath (Join-Path $evidence ('qwen-'+$name+'-build.json')) -Raw | ConvertFrom-Json
 if((Get-FileHash -LiteralPath $built -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.BinarySha256){throw 'Runtime differs from build evidence'}
}
foreach($required in @('Qwen-Apache-2.0.txt','model-card.md','model-api.json')){if(!(Test-Path -LiteralPath (Join-Path $evidence $required))){throw ('Required license/provenance missing: '+$required)}}
New-Item -ItemType Directory -Path $runtime,$licenses -Force | Out-Null
foreach($name in @('cpu','vulkan')){
 $dest=Join-Path $runtime $name;New-Item -ItemType Directory -Path $dest | Out-Null
 $built=Join-Path $root ('.tools\qwen-'+$name+'\bin\transcribe-cli.exe')
 Copy-Item -LiteralPath $built -Destination (Join-Path $dest 'transcribe-cli.exe')
 if((Get-FileHash -LiteralPath $built).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dest 'transcribe-cli.exe')).Hash){throw 'Runtime copy mismatch'}
 Copy-Item -LiteralPath (Join-Path $evidence ('qwen-'+$name+'-build.json')) -Destination (Join-Path $dest 'build-provenance.json')
}
$licenseSources=@{
 'transcribe-MIT.txt'=(Join-Path $source 'LICENSE');'transcribe-THIRD-PARTY-LICENSES.md'=(Join-Path $source 'THIRD-PARTY-LICENSES.md')
 'ggml-MIT.txt'=(Join-Path $source 'ggml\LICENSE');'miniz-MIT.txt'=(Join-Path $source 'src\third_party\miniz\LICENSE')
 'Qwen-Apache-2.0.txt'=(Join-Path $evidence 'Qwen-Apache-2.0.txt');'Qwen-model-card.md'=(Join-Path $evidence 'model-card.md')
}
foreach($name in @('COPYING.MinGW-w64-runtime.txt','GCC-GPL-3.0.txt','GCC-Runtime-Exception-3.1.txt','ggml-YaRN-MIT.txt','SPIRV-Headers-LICENSE.txt','Vulkan-Headers-Apache-2.0.txt','Vulkan-Headers-copyright.txt','Vulkan-Headers-LICENSE-summary.txt','Vulkan-Headers-MIT.txt','Vulkan-Loader-LICENSE.txt')){$licenseSources[$name]=Join-Path $root ('.local\M3D-Vulkan\licenses\'+$name)}
foreach($name in $licenseSources.Keys){Copy-Item -LiteralPath $licenseSources[$name] -Destination (Join-Path $licenses $name)}
$tinyBlasLicense=(Get-Content -LiteralPath (Join-Path $source 'ggml\src\ggml-cpu\llamafile\sgemm.cpp') -TotalCount 21) -replace '^// ?',''
[IO.File]::WriteAllLines((Join-Path $licenses 'tinyBLAS-Mozilla-MIT.txt'),$tinyBlasLicense,[Text.UTF8Encoding]::new($false))
@'
Optional ProjectEcho Qwen runtime. transcribe.cpp/ggml/miniz/tinyBLAS are MIT.
Qwen3-ASR weights are Apache-2.0 and remain outside the application distribution.
Compiler runtime notices and GCC runtime exception are included. No compiler,
SDK, Python, Node, model, driver or Vulkan loader is embedded in these runtimes.
CPU imports only Windows ADVAPI32/KERNEL32/msvcrt; Vulkan additionally requires
the existing graphics driver's system vulkan-1.dll. Actual clean Windows and
other GPU driver validation remain separate. Local changes are a UTF-8 process
manifest and actual-backend diagnostics; no ASR algorithm change.
'@ | Set-Content -LiteralPath (Join-Path $licenses 'README.txt') -Encoding utf8
$provenance=[ordered]@{Schema='projectecho-qwen-install-1';InstalledUtc=[DateTimeOffset]::UtcNow.ToString('O');Runtime=$runtime;SourceCommit='077110eb00ca6880e13bef4ddebf766b3b76896c';Model=@{Family='Qwen3-ASR';Variant='0.6B';Quantization='Q8_0';Path=$model;Bytes=(Get-Item -LiteralPath $model).Length;Sha256=$modelSha;Repository='handy-computer/Qwen3-ASR-0.6B-gguf';Revision=$modelRevision;DownloadUrl=$modelUrl;UpstreamRepository='Qwen/Qwen3-ASR-0.6B';UpstreamRevision='5eb144179a02acc5e5ba31e748d22b0cf3e303b0';License='Apache-2.0'};CpuExecutable=(Join-Path $runtime 'cpu\transcribe-cli.exe');VulkanExecutable=(Join-Path $runtime 'vulkan\transcribe-cli.exe');NoApplicationSettingsChanged=$true;ModelIncludedInApplication=$false}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'provenance.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $runtime 'provenance.json') -Destination (Join-Path $evidence 'installed-provenance.json')
Write-Output ('Installed separate Qwen runtime: '+$runtime)
Write-Output ('External model: '+$model)
