[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeDirectory,[Parameter(Mandatory)][string]$LicenseDirectory,[Parameter(Mandatory)][string]$ProvenancePath,
 [string]$Destination='H:\AI\Whisper\whisper-b5130-vulkan-ProjectEcho-20261002')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationRoot=[IO.Path]::GetFullPath($Destination)
if($destinationRoot -ne 'H:\AI\Whisper\whisper-b5130-vulkan-ProjectEcho-20261002'){throw 'This user-authorized install is scoped to its fresh Vulkan folder.'}
foreach($inputPath in @($RuntimeDirectory,$LicenseDirectory,$ProvenancePath)){
 $absolute=[IO.Path]::GetFullPath($inputPath,$root)
 if(!$absolute.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only reviewed project runtime inputs are accepted.'}
}
foreach($path in @($destinationRoot,[IO.Path]::GetFullPath($RuntimeDirectory,$root),[IO.Path]::GetFullPath($LicenseDirectory,$root))){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked install paths are not allowed.'}
 }
}
if(Test-Path -LiteralPath $destinationRoot){throw 'Refuse to overwrite an existing engine installation.'}
$runtime=[IO.Path]::GetFullPath($RuntimeDirectory,$root); $licenses=[IO.Path]::GetFullPath($LicenseDirectory,$root)
$exe=Join-Path $runtime 'whisper-cli.exe'; if(!(Test-Path -LiteralPath $exe)){throw 'Missing engine'}
New-Item -ItemType Directory -Path $destinationRoot,(Join-Path $destinationRoot 'LICENSES') -Force | Out-Null
$files=@(Get-Item $exe)+@(Get-ChildItem -LiteralPath $runtime -File -Filter '*.dll')
foreach($file in $files){
 $out=Join-Path $destinationRoot $file.Name; Copy-Item -LiteralPath $file.FullName -Destination $out
 if((Get-FileHash $out -Algorithm SHA256).Hash -ne (Get-FileHash $file.FullName -Algorithm SHA256).Hash){throw 'Install copy hash mismatch'}
}
foreach($file in Get-ChildItem -LiteralPath $licenses -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destinationRoot ('LICENSES\'+$file.Name))}
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($ProvenancePath,$root)) -Destination (Join-Path $destinationRoot 'provenance.json')
[ordered]@{ExecutablePath=(Join-Path $destinationRoot 'whisper-cli.exe');ModelPath='H:\AI\Whisper\models\ggml-large-v3.bin';Language='auto';Threads=2;ModelName='ggml-large-v3';ExpectedModelSha256=$null;ModelLicenseAcknowledged=$true;Hardware='Vulkan';GpuDeviceIndex=0;InitialPrompt=$null} |
 ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationRoot 'projectecho-whisper-vulkan.json') -Encoding utf8
Write-Output ('Installed separate Vulkan runtime: '+$destinationRoot)
Write-Output 'Existing CPU engine, models, projectecho-whisper.json and system settings were not changed.'
