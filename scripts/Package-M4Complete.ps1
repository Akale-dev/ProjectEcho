[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$WhisperCpuRuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$WhisperVulkanRuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$WhisperBaseModelPath,
    [Parameter(Mandatory=$true)][string]$WhisperProvenancePath,
    [Parameter(Mandatory=$true)][string]$WhisperLicenseDirectory,
    [Parameter(Mandatory=$true)][string]$QwenRuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$AlignerRuntimeDirectory,
    [Parameter(Mandatory=$true)][string]$ImplementationReportPath,
    [string]$AlignerAvx2RuntimeDirectory='',
    [string]$AlignerVulkanRuntimeDirectory='',
    [string]$PublishedDirectory='',
    [string]$DotnetPath=''
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts=Join-Path $root 'artifacts'
$output=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory,$root))
$zip=$output+'.zip';$zipHashPath=$zip+'.sha256';$metadataPath=$output+'.package.json'
$utf8=[Text.UTF8Encoding]::new($false,$true)

function Assert-NoReparse([string]$Path){
    for($ancestor=$Path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
        if((Test-Path -LiteralPath $ancestor) -and (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)){
            throw ('Linked/reparse input or output is prohibited: '+$ancestor)
        }
    }
}
function Assert-Within([string]$Path,[string]$Parent){
    if(-not $Path.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw ('Path outside '+$Parent+': '+$Path)}
    Assert-NoReparse $Path
}
function Read-Input([string]$Path,[bool]$Directory=$false,[ValidateSet('Project','Qwen','Whisper')][string]$Source='Project'){
    $full=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path,$root))
    $inside=$full.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)
    $authorized=($Source -eq 'Qwen' -and $full.StartsWith('H:\AI\Qwen\',[StringComparison]::OrdinalIgnoreCase)) -or
        ($Source -eq 'Whisper' -and $full.StartsWith('H:\AI\Whisper\',[StringComparison]::OrdinalIgnoreCase))
    if(-not ($inside -or $authorized)){throw ('Input is outside the authorized project/model source: '+$full)}
    if($Source -eq 'Qwen' -and $inside -and -not $full.StartsWith((Join-Path $root '.tools').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){
        throw ('Qwen runtime input must be project .tools or the authorized external Qwen folder: '+$full)
    }
    Assert-NoReparse $full
    $kind=if($Directory){'Container'}else{'Leaf'}
    if(-not (Test-Path -LiteralPath $full -PathType $kind)){throw ('Missing input: '+$full)}
    return $full
}
function Assert-Relative([string]$Relative){
    $name=$Relative.Replace('\','/');$parts=$name.Split('/')
    if([string]::IsNullOrWhiteSpace($name) -or [IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
        @($parts|Where-Object{$_ -in @('','.','..') -or $_.TrimEnd(' ','.') -ne $_ -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0}).Count -gt 0){throw ('Unsafe bundle path: '+$name)}
    if($name -match '(^|/)(\.git|\.codex|\.agents|\.local|\.tools|Data|profiles?|node_modules)(/|$)' -or
        [IO.Path]::GetExtension($name) -in @('.pdb','.wav','.mp3','.m4a','.flac','.ogg','.srt','.vtt','.dmp','.gguf','.safetensors','.onnx','.pt','.pth','.npy','.npz')){
        throw ('Private/test data or an external Qwen model is prohibited in the bundle: '+$name)
    }
    if([IO.Path]::GetExtension($name) -eq '.bin' -and $name -ne 'Models/ggml-base.bin'){throw ('Only the existing Whisper base model is bundled: '+$name)}
}
function Hash-File([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Write-NewText([string]$Path,[string]$Value){
    Assert-NoReparse $Path
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=$utf8.GetBytes($Value);$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
}
function Copy-Verified([string]$Source,[string]$Relative){
    Assert-Relative $Relative;Assert-NoReparse $Source
    $destination=[IO.Path]::GetFullPath((Join-Path $output $Relative));Assert-Within $destination $output
    $hash=Hash-File $Source
    if(Test-Path -LiteralPath $destination){
        if((Test-Path -LiteralPath $destination -PathType Leaf) -and (Hash-File $destination) -eq $hash){return}
        throw ('Different file already exists in new package: '+$Relative)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination
    if((Hash-File $destination) -ne $hash){throw ('Copy verification failed: '+$Relative)}
}
function Read-Provenance([string]$Path){
    Assert-NoReparse $Path
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 2000000){throw ('Missing or oversized provenance: '+$Path)}
    return Get-Content -LiteralPath $Path -Raw -Encoding utf8|ConvertFrom-Json
}
function Write-PortableProvenance([string]$Source,[string]$Relative){
    $value=Read-Provenance $Source
    if($value.PSObject.Properties['Version'] -and $value.Version -like 'audio.cpp*'){
        $value|Add-Member -NotePropertyName SourceRepository -NotePropertyValue 'https://github.com/0xShug0/audio.cpp/tree/v0.9.0' -Force
    }
    $value|Add-Member -NotePropertyName SourceProvenanceSha256 -NotePropertyValue (Hash-File $Source) -Force
    $value|Add-Member -NotePropertyName PathNotation -NotePropertyValue 'Local build/install roots replaced with project://, external-qwen:// or external-whisper:// labels. They are provenance references, never application defaults or executable configuration.' -Force
    $json=$value|ConvertTo-Json -Depth 20
    # Change only known authorized roots in JSON strings; retain original source hash and public URLs.
    $json=$json.Replace(($root+'\').Replace('\','\\'),'project://')
    $json=$json.Replace('H:\AI\Qwen\'.Replace('\','\\'),'external-qwen://')
    $json=$json.Replace('H:\AI\Whisper\'.Replace('\','\\'),'external-whisper://')
    Assert-Relative $Relative
    $destination=[IO.Path]::GetFullPath((Join-Path $output $Relative));Assert-Within $destination $output
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
    Write-NewText $destination $json
}
function Require-StaticNative([string]$Executable,[string]$ProvenancePath,[bool]$Vulkan=$false){
    Assert-NoReparse $Executable
    $provenance=Read-Provenance $ProvenancePath
    if((Hash-File $Executable) -ne $provenance.BinarySha256){throw ('Native executable differs from its build provenance: '+$Executable)}
    $allowed=@('ADVAPI32.dll','KERNEL32.dll','msvcrt.dll')
    if($Vulkan){$allowed+=@('vulkan-1.dll')}
    if(@($provenance.ImportedDlls|Where-Object{$_ -notin $allowed}).Count -gt 0){throw ('Unexpected native dependency; update and validate the explicit closure first: '+$Executable)}
    if($Vulkan -and 'vulkan-1.dll' -notin $provenance.ImportedDlls){throw 'The selected Vulkan runtime does not declare the Vulkan loader dependency.'}
    return $provenance
}
function Copy-Licenses([string]$Directory,[string]$RelativeRoot){
    $files=@(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force)
    if($files.Count -eq 0){throw ('License directory is empty: '+$Directory)}
    foreach($file in $files){
        if($file.Extension -notin @('','.txt','.md','.html','.json','.rtf','.license') -or $file.Length -gt 2000000){throw ('Unexpected license asset: '+$file.FullName)}
        Copy-Verified $file.FullName ($RelativeRoot+'/'+[IO.Path]::GetRelativePath($Directory,$file.FullName).Replace('\','/'))
    }
}
function Copy-Whisper([string]$Directory,[string]$RelativeRoot){
    $files=@(Get-Item -LiteralPath (Join-Path $Directory 'whisper-cli.exe'))+@(Get-ChildItem -LiteralPath $Directory -File -Filter '*.dll')
    foreach($file in $files){if($file.Length -eq 0){throw 'Empty Whisper runtime file.'};Copy-Verified $file.FullName ($RelativeRoot+'/'+$file.Name)}
}
function Read-OptionalAligner([string]$Directory,[string]$Name,[bool]$Vulkan=$false){
    if(-not $Directory){return $null}
    $runtime=Read-Input $Directory $true 'Qwen'
    $executable=Join-Path $runtime 'audiocpp_cli.exe';$provenancePath=Join-Path $runtime 'provenance.json'
    $provenance=Require-StaticNative $executable $provenancePath $Vulkan
    if(-not $provenance.PSObject.Properties['Version'] -or $provenance.Version -notlike '*ProjectEcho-raw-v1*' -or
        -not $provenance.PSObject.Properties['Protocol'] -or $provenance.Protocol -ne 'batch-v2'){
        throw ('Optimized aligner requires verified raw-v1 and batch-v2 provenance: '+$runtime)
    }
    $licenses=Join-Path $runtime 'licenses';Assert-NoReparse $licenses
    if(-not (Test-Path -LiteralPath $licenses -PathType Container)){throw ('Missing optimized aligner licenses: '+$licenses)}
    return [pscustomobject]@{Name=$Name;Directory=$runtime;Executable=$executable;Provenance=$provenancePath;Licenses=$licenses;Vulkan=$Vulkan}
}

Assert-Within $output $artifacts
foreach($destination in @($output,$zip,$zipHashPath,$metadataPath,$output+'.manifest.sha256')){
    Assert-NoReparse $destination;if(Test-Path -LiteralPath $destination){throw ('Choose a new package name; nothing is overwritten or deleted: '+$destination)}
}
$whisperCpu=Read-Input $WhisperCpuRuntimeDirectory $true 'Whisper'
$whisperVulkan=Read-Input $WhisperVulkanRuntimeDirectory $true 'Whisper'
$whisperModel=Read-Input $WhisperBaseModelPath $false 'Whisper'
if([IO.Path]::GetFileName($whisperModel) -ne 'ggml-base.bin'){throw 'The existing multilingual Whisper base model is required.'}
$whisperProvenance=Read-Input $WhisperProvenancePath
$null=Read-Provenance $whisperProvenance
$whisperLicenses=Read-Input $WhisperLicenseDirectory $true
$qwen=Read-Input $QwenRuntimeDirectory $true 'Qwen'
$aligner=Read-Input $AlignerRuntimeDirectory $true 'Qwen'
$implementation=Read-Input $ImplementationReportPath
$quality=Read-Input 'docs/72_M4_QUALITY_AND_WINDOW_EVIDENCE.md'
$userGuide=Read-Input 'docs/73_M4_QWEN_USER_GUIDE.md'
$qwenCpu=Join-Path $qwen 'cpu/transcribe-cli.exe';$qwenVulkan=Join-Path $qwen 'vulkan/transcribe-cli.exe'
$qwenCpuBuild=Join-Path $qwen 'cpu/build-provenance.json';$qwenVulkanBuild=Join-Path $qwen 'vulkan/build-provenance.json'
$null=Require-StaticNative $qwenCpu $qwenCpuBuild
$null=Require-StaticNative $qwenVulkan $qwenVulkanBuild $true
$alignerExe=Join-Path $aligner 'audiocpp_cli.exe';$alignerBuild=Join-Path $aligner 'provenance.json'
$alignerProvenance=Require-StaticNative $alignerExe $alignerBuild
if($alignerProvenance.Version -notlike '*ProjectEcho-raw-v1*'){throw 'Only the verified raw-boundary aligner may be included; upstream timestamp interpolation is prohibited.'}
$alignerAvx2=Read-OptionalAligner $AlignerAvx2RuntimeDirectory 'qwen-aligner-avx2'
$alignerVulkan=Read-OptionalAligner $AlignerVulkanRuntimeDirectory 'qwen-aligner-vulkan' $true
$performanceReport=if($alignerAvx2 -or $alignerVulkan){Read-Input 'docs/74_M4_ALIGNMENT_PERFORMANCE.md'}else{$null}
$officialPythonBridge=if($alignerAvx2 -or $alignerVulkan){Read-Input 'scripts/qwen_aligner_bridge.py'}else{$null}
$null=Read-Provenance (Join-Path $qwen 'provenance.json')
foreach($directory in @((Join-Path $qwen 'LICENSES'),(Join-Path $aligner 'licenses'))){Assert-NoReparse $directory;if(-not (Test-Path -LiteralPath $directory -PathType Container)){throw ('Missing native license directory: '+$directory)}}

if($PublishedDirectory){
    $published=Read-Input $PublishedDirectory $true;Assert-Within $published $artifacts
    $sourceManifest=Join-Path $published 'release-manifest.json';$sourceHash=$published+'.manifest.sha256'
    Assert-NoReparse $sourceManifest;Assert-NoReparse $sourceHash
    $expected=(Get-Content -LiteralPath $sourceHash -Raw).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0].Trim([char]0xFEFF)
    if((Hash-File $sourceManifest) -ne $expected){throw 'Published source manifest hash mismatch.'}
    $manifest=Get-Content -LiteralPath $sourceManifest -Raw|ConvertFrom-Json
    if($manifest.files.Count -ne $manifest.fileCount -or @($manifest.files|Group-Object path|Where-Object Count -gt 1).Count -gt 0){throw 'Invalid published source inventory.'}
    foreach($entry in $manifest.files){
        Assert-Relative $entry.path;$source=[IO.Path]::GetFullPath((Join-Path $published $entry.path));Assert-Within $source $published
        if((Get-Item -LiteralPath $source).Length -ne $entry.bytes -or (Hash-File $source) -ne $entry.sha256){throw ('Published source changed: '+$entry.path)}
    }
    [IO.Directory]::CreateDirectory($output)|Out-Null
    foreach($entry in $manifest.files){Copy-Verified (Join-Path $published $entry.path) $entry.path}
}else{
    & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -DotnetPath $DotnetPath -OutputDirectory $output
}
foreach($relative in @('ProjectEcho.exe','ProjectEcho.dll','hostfxr.dll','coreclr.dll','portable.json','THIRD_PARTY.json',
    'CaptureHost/ProjectEcho.CaptureHost.exe','CaptureHost/hostfxr.dll','CaptureHost/coreclr.dll')){
    if(-not (Test-Path -LiteralPath (Join-Path $output $relative) -PathType Leaf)){throw ('Incomplete self-contained application: '+$relative)}
}
foreach($relative in @('ProjectEcho.runtimeconfig.json','CaptureHost/ProjectEcho.CaptureHost.runtimeconfig.json')){
    $configuration=Get-Content -LiteralPath (Join-Path $output $relative) -Raw|ConvertFrom-Json
    if(-not $configuration.runtimeOptions.PSObject.Properties['includedFrameworks']){throw ('Not self-contained: '+$relative)}
}

Copy-Whisper $whisperCpu 'Tools/whisper-cpu';Copy-Whisper $whisperVulkan 'Tools/whisper-vulkan'
Copy-Verified $whisperModel 'Models/ggml-base.bin';Write-PortableProvenance $whisperProvenance 'THIRD_PARTY.asr.json'
Copy-Licenses $whisperLicenses 'LICENSES'
Copy-Verified $qwenCpu 'Tools/qwen-cpu/transcribe-cli.exe';Copy-Verified $qwenVulkan 'Tools/qwen-vulkan/transcribe-cli.exe'
Copy-Verified $alignerExe 'Tools/qwen-aligner/audiocpp_cli.exe'
Write-PortableProvenance $qwenCpuBuild 'Tools/qwen-cpu/build-provenance.json';Write-PortableProvenance $qwenVulkanBuild 'Tools/qwen-vulkan/build-provenance.json'
Write-PortableProvenance $alignerBuild 'Tools/qwen-aligner/build-provenance.json'
Write-PortableProvenance (Join-Path $qwen 'provenance.json') 'THIRD_PARTY.qwen.json'
Copy-Licenses (Join-Path $qwen 'LICENSES') 'LICENSES/Qwen'
Copy-Licenses (Join-Path $aligner 'licenses') 'LICENSES/QwenAligner'
foreach($optionalAligner in @($alignerAvx2,$alignerVulkan)){
    if($null -eq $optionalAligner){continue}
    Copy-Verified $optionalAligner.Executable ('Tools/'+$optionalAligner.Name+'/audiocpp_cli.exe')
    Write-PortableProvenance $optionalAligner.Provenance ('Tools/'+$optionalAligner.Name+'/build-provenance.json')
    Copy-Licenses $optionalAligner.Licenses ('LICENSES/'+$optionalAligner.Name)
}
if($officialPythonBridge){Copy-Verified $officialPythonBridge 'Tools/qwen-python/qwen_aligner_bridge.py'}
Copy-Verified $implementation 'M4实现与验证.zh-CN.md';Copy-Verified $quality 'M4质量与分窗验证.zh-CN.md'
$guideText=([IO.File]::ReadAllText($userGuide)).Replace('(71_M4_QWEN_CHINESE_ASR.md)','(M4实现与验证.zh-CN.md)').Replace('(72_M4_QUALITY_AND_WINDOW_EVIDENCE.md)','(M4质量与分窗验证.zh-CN.md)')
if($performanceReport){
    Copy-Verified $performanceReport 'M4句子定位性能.zh-CN.md'
    $guideText=$guideText.Replace('(74_M4_ALIGNMENT_PERFORMANCE.md)','(M4句子定位性能.zh-CN.md)')
}
Write-NewText (Join-Path $output 'M4使用说明.zh-CN.md') $guideText

Write-NewText (Join-Path $output 'bundled-whisper.json') ([ordered]@{
    ExecutablePath='Tools/whisper-cpu/whisper-cli.exe';ModelPath='Models/ggml-base.bin';Language='auto';Threads=2;
    ModelName='base (multilingual)';ExpectedModelSha256=$null;ModelLicenseAcknowledged=$true;Hardware=0;GpuDeviceIndex=0;InitialPrompt=$null
}|ConvertTo-Json)
$bundledQwen=[ordered]@{
    schemaVersion=1;backendId='qwen3-asr';cpuExecutable='Tools/qwen-cpu/transcribe-cli.exe';vulkanExecutable='Tools/qwen-vulkan/transcribe-cli.exe';
    alignerExecutable='Tools/qwen-aligner/audiocpp_cli.exe';modelsBundled=$false;defaultBackendChanged=$false;
    modelPolicy='Choose externally downloaded compatible Qwen3-ASR 0.6B or 1.7B GGUF and Qwen3-ForcedAligner GGUF; neither is bundled or downloaded by this application.'
}
if($alignerAvx2){$bundledQwen['alignerAvx2Executable']='Tools/qwen-aligner-avx2/audiocpp_cli.exe'}
if($alignerVulkan){$bundledQwen['alignerVulkanExecutable']='Tools/qwen-aligner-vulkan/audiocpp_cli.exe'}
if($officialPythonBridge){
    $bundledQwen['officialPythonBridge']='Tools/qwen-python/qwen_aligner_bridge.py'
    $bundledQwen['officialPythonBridgeSha256']=Hash-File $officialPythonBridge
    $bundledQwen['officialPythonEnvironmentBundled']=$false
    $bundledQwen['officialPythonPolicy']='Explicit optional CPU route only; choose an external compatible Python runtime and official safetensors model. No environment or model is bundled or automatically downloaded; generic native compatibility mode remains available.'
}
Write-NewText (Join-Path $output 'bundled-qwen.json') ($bundledQwen|ConvertTo-Json)
$portablePath=Join-Path $output 'portable.json';$portable=Get-Content -LiteralPath $portablePath -Raw|ConvertFrom-Json
$portable|Add-Member -NotePropertyName milestone -NotePropertyValue 'M4' -Force
$portable|Add-Member -NotePropertyName transcription -NotePropertyValue 'Existing Whisper CPU/Vulkan/base remains available. Optional Qwen3-ASR CPU/Vulkan and separate raw-boundary CPU forced alignment use user-selected external GGUF models. No Qwen ASR, forced-aligner or VAD model is bundled; no automatic download/upload.' -Force
$portable|Add-Member -NotePropertyName bundledAsr -NotePropertyValue ([ordered]@{
    whisperConfiguration='bundled-whisper.json';qwenRuntimeDiscovery='bundled-qwen.json';whisperModel='Models/ggml-base.bin';qwenModelsIncluded=$false;
    qwenEngines=@('Tools/qwen-cpu/transcribe-cli.exe','Tools/qwen-vulkan/transcribe-cli.exe');qwenAligner='Tools/qwen-aligner/audiocpp_cli.exe';
    configurationPolicy='Existing user settings take precedence; Qwen is not automatically selected; external models must be chosen by the user.'
}) -Force
if($alignerAvx2){$portable.bundledAsr['qwenAlignerAvx2']='Tools/qwen-aligner-avx2/audiocpp_cli.exe'}
if($alignerVulkan){$portable.bundledAsr['qwenAlignerVulkan']='Tools/qwen-aligner-vulkan/audiocpp_cli.exe'}
if($officialPythonBridge){
    $portable.bundledAsr['qwenOfficialPythonBridge']='Tools/qwen-python/qwen_aligner_bridge.py'
    $portable.bundledAsr['qwenOfficialPythonBridgeSha256']=Hash-File $officialPythonBridge
    $portable.bundledAsr['qwenOfficialPythonEnvironmentIncluded']=$false
}
if($alignerAvx2 -or $alignerVulkan){
    $portable.transcription='Existing Whisper CPU/Vulkan/base remains available. Optional Qwen ASR and separately selected raw-boundary CPU/AVX2/Vulkan alignment use external GGUF models. Generic CPU alignment is retained. Optimized paths require supported hardware and batch-v2 verification; no model is bundled or automatically downloaded/uploaded.'
}
[IO.File]::WriteAllText($portablePath,($portable|ConvertTo-Json -Depth 12),$utf8)
$quickStart=@'
# M4 完整便携包

完整解压后启动 ProjectEcho.exe，保留 CaptureHost、Tools、Models 和 LICENSES。录音/导出无需模型，不需要安装 Python、Node、.NET 或 SDK。

每场选择实际声音来源，并主动决定是否开启独立麦克风。录音、暂停和收尾期间禁用本程序回放；新录音排除暂停及恢复准备时间，旧录音保持原时间轴。录音与转写原件保留，外部上传由用户自行操作。

本包保留可用的 Whisper CPU/Vulkan 程序与多语言 base 模型，不自动改掉已有识别引擎或设置。Vulkan 需要目标机兼容显卡驱动；没有适用驱动可选择 CPU，程序不安装驱动。

Qwen 为可选的第二本地引擎。本包带 CPU/Vulkan 程序，以及提供原始声学边界的独立 CPU 对齐程序；**不含 Qwen ASR、ForcedAligner 或 VAD 模型**。请自行下载并在设置中选取外部兼容 GGUF：Qwen3-ASR 可选择 0.6B 或 1.7B，句子回听还需要 Qwen3-ForcedAligner。当前实际验证候选以“M4实现与验证.zh-CN.md”为准，能选路径不等于所有型号/量化都通过本机验收。

参考模型来源：Qwen3-ASR 官方仓库 https://github.com/QwenLM/Qwen3-ASR ，transcribe.cpp 候选端口 https://github.com/handy-computer/transcribe.cpp 。模型由用户显式获取，本程序不在录音时或识别时自动下载，也不上传音频或文字。

随包程序分别在 Tools/qwen-cpu/transcribe-cli.exe、Tools/qwen-vulkan/transcribe-cli.exe、Tools/qwen-aligner/audiocpp_cli.exe。模型建议放在软件目录以外，移动电脑后重新选外部模型。0.6B 更小不等于永远更快/更准，1.7B 的实际质量与资源应另验。

每次识别保存独立原稿；取消、切会或重试不覆盖原音频/人工稿。句子时间为近似音文对齐，可点击核对。VAD/音频窗口不能替代句子定位。本 ZIP 不含私人 Data、会议音频、字幕、提示词、浏览器资料或测试语音。

来源/依赖/许可见 THIRD_PARTY*.json、Tools 下 build-provenance.json 和 LICENSES。测试证据/限制见“M4实现与验证.zh-CN.md”“M4质量与分窗验证.zh-CN.md”；打包校验通过不能替代真正干净 Windows 的录音与回放验收。

Qwen配置与句子回听见 [M4使用说明](M4使用说明.zh-CN.md)，原有录音、导出、Whisper操作见 [使用说明](使用说明.zh-CN.md)。
'@
if($alignerAvx2 -or $alignerVulkan){
    $quickStart+="`n`n本包另带经过本轮验证的定位加速候选，同时保留原通用CPU定位程序。识别硬件与句子定位硬件分别选择，显式Vulkan失败不静默回退CPU。具体速度与支持范围以本包验证报告为准。`n"
    if($alignerAvx2){$quickStart+="优化CPU定位：Tools/qwen-aligner-avx2/audiocpp_cli.exe，需要适用的AVX2/FMA/F16C等CPU能力。`n"}
    if($alignerVulkan){$quickStart+="Vulkan定位：Tools/qwen-aligner-vulkan/audiocpp_cli.exe，需要兼容的系统显卡驱动；权重仍位于软件目录以外。`n"}
    $quickStart+="官方Python CPU定位仅为明确选择的可选项，脚本位于Tools/qwen-python/qwen_aligner_bridge.py；解释器、计算库和官方模型均由用户另行配置，不随本ZIP，也不改变默认引擎。普通录音、导出和原生定位无需Python。旧CPU兼容模式使用通用原生程序。`n"
}
Write-NewText (Join-Path $output '打开即用.zh-CN.md') $quickStart
# This is a fresh generated folder; replace the base publisher's historical cover with this M4 cover.
[IO.File]::WriteAllText((Join-Path $output 'README.zh-CN.md'),$quickStart,$utf8)

# Explicit final scan excludes every Qwen/aligner/VAD weight and all audio, even if a future publisher adds one.
foreach($file in Get-ChildItem -LiteralPath $output -Recurse -File -Force){
    Assert-NoReparse $file.FullName;Assert-Relative ([IO.Path]::GetRelativePath($output,$file.FullName))
}
& (Join-Path $PSScriptRoot 'Update-PortableManifest.ps1') -PortableDirectory $output
$finalManifestPath=Join-Path $output 'release-manifest.json';$manifestHash=Hash-File $finalManifestPath
$finalManifest=Get-Content -LiteralPath $finalManifestPath -Raw|ConvertFrom-Json
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output,$zip,[IO.Compression.CompressionLevel]::Optimal,$true,[Text.Encoding]::UTF8)
$zipHash=Hash-File $zip
Write-NewText $zipHashPath ($zipHash+'  '+[IO.Path]::GetFileName($zip))

# Read every compressed entry back, compare bytes and hashes against the on-disk immutable inventory.
$expectedEntries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($entry in $finalManifest.files){$expectedEntries.Add($entry.path,$entry)}
$expectedEntries.Add('release-manifest.json',[pscustomobject]@{bytes=(Get-Item -LiteralPath $finalManifestPath).Length;sha256=$manifestHash})
$archive=[IO.Compression.ZipFile]::OpenRead($zip);$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$prefix=[IO.Path]::GetFileName($output)+'/'
try{
    foreach($entry in $archive.Entries){
        if(-not $entry.FullName.StartsWith($prefix,[StringComparison]::Ordinal)){throw 'ZIP contains an unexpected root entry.'}
        $relative=$entry.FullName.Substring($prefix.Length)
        if($relative.EndsWith('/')){continue}
        Assert-Relative $relative
        if(-not $seen.Add($relative) -or -not $expectedEntries.ContainsKey($relative)){throw ('ZIP duplicate/unlisted entry: '+$relative)}
        $expected=$expectedEntries[$relative];$stream=$entry.Open()
        try{$hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($entry.Length -ne $expected.bytes -or $hash -ne $expected.sha256){throw ('ZIP entry differs from folder inventory: '+$relative)}
    }
    if($seen.Count -ne $expectedEntries.Count){throw 'ZIP omitted one or more inventory files.'}
}finally{$archive.Dispose()}
$packageMetadata=[ordered]@{
    schemaVersion=1;generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');archive=[IO.Path]::GetFileName($zip);
    archiveBytes=(Get-Item -LiteralPath $zip).Length;archiveSha256=$zipHash;releaseManifestSha256=$manifestHash;
    bundledFiles=$finalManifest.fileCount;uncompressedBytes=$finalManifest.totalBytes;zipEntriesVerified=$seen.Count;
    zipVsFolderHashVerification='PASS';qwenModelsIncluded=$false;whisperBaseIncluded=$true;
    qwenCpuSha256=Hash-File (Join-Path $output 'Tools/qwen-cpu/transcribe-cli.exe');
    qwenVulkanSha256=Hash-File (Join-Path $output 'Tools/qwen-vulkan/transcribe-cli.exe');
    rawAlignerSha256=Hash-File (Join-Path $output 'Tools/qwen-aligner/audiocpp_cli.exe');
    sourceMode='Packaging only; no recording, microphone, inference, playback or network';
    relocationValidation='NOT_RUN by packaging; test freshly extracted files separately';cleanWindowsValidation='NOT_RUN by packaging'
}
if($alignerAvx2){$packageMetadata['rawAlignerAvx2Sha256']=Hash-File (Join-Path $output 'Tools/qwen-aligner-avx2/audiocpp_cli.exe')}
if($alignerVulkan){$packageMetadata['rawAlignerVulkanSha256']=Hash-File (Join-Path $output 'Tools/qwen-aligner-vulkan/audiocpp_cli.exe')}
if($officialPythonBridge){
    $packageMetadata['officialPythonBridgeSha256']=Hash-File (Join-Path $output 'Tools/qwen-python/qwen_aligner_bridge.py')
    $packageMetadata['officialPythonEnvironmentIncluded']=$false
}
Write-NewText $metadataPath ($packageMetadata|ConvertTo-Json -Depth 8)
Write-Host ('M4 complete portable folder: '+$output)
Write-Host ('ZIP verified against every folder file: '+$zip)
Write-Host ('ZIP SHA256: '+$zipHash)
