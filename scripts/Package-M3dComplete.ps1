[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$CpuRuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$VulkanRuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$BaseModelPath,
    [Parameter(Mandatory = $true)][string]$AsrProvenancePath,
    [Parameter(Mandatory = $true)][string]$AsrLicenseDirectory,
    [string]$PublishedDirectory = '',
    [string]$DotnetPath = ''
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $root 'artifacts'
$output = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory, $root))
$zip = $output + '.zip'
$zipHashPath = $zip + '.sha256'
$metadataPath = $output + '.package.json'

function Assert-NoReparse([string]$Path) {
    for ($ancestor = $Path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw ('Linked/reparse paths are not permitted: ' + $ancestor)
        }
    }
}
function Assert-Within([string]$Path, [string]$Parent) {
    if (-not $Path.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Path must stay within ' + $Parent + ': ' + $Path)
    }
    Assert-NoReparse $Path
}
function Read-WorkspacePath([string]$Path, [bool]$Directory = $false, [bool]$AllowWhisperModel = $false) {
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path, $root))
    $insideProject = $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    $authorizedModel = $AllowWhisperModel -and $full.StartsWith('H:\AI\Whisper\', [StringComparison]::OrdinalIgnoreCase)
    if (-not ($insideProject -or $authorizedModel)) { throw ('Input is outside the authorized project/model source: ' + $full) }
    Assert-NoReparse $full
    $kind = if ($Directory) { 'Container' } else { 'Leaf' }
    if (-not (Test-Path -LiteralPath $full -PathType $kind)) { throw ('Missing input: ' + $full) }
    return $full
}
function Assert-BundleRelative([string]$Relative) {
    $parts = $Relative.Replace('\', '/').Split('/')
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or
        @($parts | Where-Object { $_ -in @('', '.', '..') -or $_.TrimEnd(' ', '.') -ne $_ -or
            $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 }).Count -gt 0) { throw ('Unsafe bundle path: ' + $Relative) }
    if ($Relative.Replace('\', '/') -match '(^|/)(\.git|\.codex|\.agents|\.local|\.tools|Data|profiles?|node_modules)(/|$)' -or
        [IO.Path]::GetExtension($Relative) -in @('.pdb', '.wav', '.mp3', '.m4a', '.dmp')) {
        throw ('Development/private data is prohibited in the bundle: ' + $Relative)
    }
}
function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Copy-Verified([string]$Source, [string]$Relative) {
    Assert-BundleRelative $Relative
    Assert-NoReparse $Source
    $destination = [IO.Path]::GetFullPath((Join-Path $output $Relative))
    Assert-Within $destination $output
    $hash = Hash-File $Source
    if (Test-Path -LiteralPath $destination) {
        if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Hash-File $destination) -eq $hash) { return }
        throw ('A different file already exists in the new bundle: ' + $Relative)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination
    if ((Hash-File $destination) -ne $hash) { throw ('Copied file hash differs: ' + $Relative) }
}
function Runtime-Files([string]$Directory) {
    $executable = Join-Path $Directory 'whisper-cli.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw ('Runtime root must contain whisper-cli.exe: ' + $Directory) }
    $files = @(Get-Item -LiteralPath $executable) + @(Get-ChildItem -LiteralPath $Directory -File -Filter '*.dll')
    foreach ($file in $files) { Assert-NoReparse $file.FullName; if ($file.Length -eq 0) { throw ('Empty runtime file: ' + $file.FullName) } }
    return $files
}

Assert-Within $output $artifacts
foreach ($destination in @($output, $zip, $zipHashPath, $metadataPath, ($output + '.manifest.sha256'))) {
    Assert-NoReparse $destination
    if (Test-Path -LiteralPath $destination) { throw ('Choose a fresh output name; existing files are never overwritten or removed: ' + $destination) }
}
$cpu = Read-WorkspacePath $CpuRuntimeDirectory $true
$vulkan = Read-WorkspacePath $VulkanRuntimeDirectory $true
$model = Read-WorkspacePath $BaseModelPath $false $true
if ([IO.Path]::GetExtension($model) -ne '.bin' -or (Get-Item -LiteralPath $model).Length -eq 0) { throw 'A nonempty GGML base .bin model is required.' }
$provenance = Read-WorkspacePath $AsrProvenancePath
if ((Get-Item -LiteralPath $provenance).Length -gt 2000000) { throw 'ASR provenance JSON exceeds its size limit.' }
$null = Get-Content -LiteralPath $provenance -Raw | ConvertFrom-Json
$licenses = Read-WorkspacePath $AsrLicenseDirectory $true
$cpuFiles = @(Runtime-Files $cpu)
$vulkanFiles = @(Runtime-Files $vulkan)
$licenseFiles = @(Get-ChildItem -LiteralPath $licenses -Recurse -File -Force)
if ($licenseFiles.Count -eq 0) { throw 'The prepared ASR licenses directory is empty.' }
foreach ($file in $licenseFiles) {
    Assert-NoReparse $file.FullName
    $relative = [IO.Path]::GetRelativePath($licenses, $file.FullName)
    Assert-BundleRelative ('LICENSES/' + $relative.Replace('\', '/'))
    if ($file.Extension -notin @('', '.txt', '.md', '.html', '.json', '.rtf', '.license')) { throw ('Unexpected license-directory content: ' + $relative) }
}

if ($PublishedDirectory) {
    $published = Read-WorkspacePath $PublishedDirectory $true
    Assert-Within $published $artifacts
    $sourceManifest = Join-Path $published 'release-manifest.json'
    $sourceManifestHash = $published + '.manifest.sha256'
    Assert-NoReparse $sourceManifest; Assert-NoReparse $sourceManifestHash
    $declaredHash = (Get-Content -LiteralPath $sourceManifestHash -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
    if ((Hash-File $sourceManifest) -ne $declaredHash) { throw 'Published source manifest hash mismatch.' }
    $manifest = Get-Content -LiteralPath $sourceManifest -Raw | ConvertFrom-Json
    if ($manifest.files.Count -ne $manifest.fileCount -or @($manifest.files | Group-Object path | Where-Object Count -gt 1).Count -gt 0) {
        throw 'Published source has an invalid or duplicate file inventory.'
    }
    foreach ($entry in $manifest.files) {
        Assert-BundleRelative $entry.path
        $source = [IO.Path]::GetFullPath((Join-Path $published $entry.path))
        Assert-Within $source $published
        if ((Get-Item -LiteralPath $source).Length -ne $entry.bytes -or (Hash-File $source) -ne $entry.sha256) {
            throw ('Published source is no longer identical to its release manifest: ' + $entry.path)
        }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    # Copy only verified immutable inventory entries. Existing user Data or extra files in the source stay outside this package.
    foreach ($entry in $manifest.files) { Copy-Verified (Join-Path $published $entry.path) $entry.path }
} else {
    & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -DotnetPath $DotnetPath -OutputDirectory $output
}
foreach ($required in @('ProjectEcho.exe', 'ProjectEcho.dll', 'portable.json', 'THIRD_PARTY.json', 'CaptureHost/ProjectEcho.CaptureHost.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $required) -PathType Leaf)) { throw ('Incomplete application bundle: ' + $required) }
}
foreach ($file in $cpuFiles) { Copy-Verified $file.FullName ('Tools/whisper-cpu/' + $file.Name) }
foreach ($file in $vulkanFiles) { Copy-Verified $file.FullName ('Tools/whisper-vulkan/' + $file.Name) }
Copy-Verified $model 'Models/ggml-base.bin'
Copy-Verified $provenance 'THIRD_PARTY.asr.json'
foreach ($file in $licenseFiles) { Copy-Verified $file.FullName ('LICENSES/' + [IO.Path]::GetRelativePath($licenses, $file.FullName).Replace('\', '/')) }

$configurationPath = Join-Path $output 'bundled-whisper.json'
if (Test-Path -LiteralPath $configurationPath) { throw 'Published source already includes bundled-whisper.json; supply a fresh base application publication.' }
[ordered]@{
    ExecutablePath = 'Tools/whisper-cpu/whisper-cli.exe'; ModelPath = 'Models/ggml-base.bin'
    Language = 'auto'; Threads = 2; ModelName = 'base (multilingual)'; ExpectedModelSha256 = $null
    ModelLicenseAcknowledged = $true; Hardware = 0; GpuDeviceIndex = 0; InitialPrompt = $null
} | ConvertTo-Json | Set-Content -LiteralPath $configurationPath -Encoding utf8

$portablePath = Join-Path $output 'portable.json'
$portable = Get-Content -LiteralPath $portablePath -Raw | ConvertFrom-Json
$portable | Add-Member -MemberType NoteProperty -Name transcription -Value 'Bundled offline whisper.cpp CPU and Vulkan runtimes with multilingual base GGML model; portable relative defaults use CPU; GPU requires explicit selection and a compatible vendor driver. M3H saves prompts per meeting, never in defaults.' -Force
$portable | Add-Member -MemberType NoteProperty -Name bundledAsr -Value ([ordered]@{
    configuration = 'bundled-whisper.json'; defaultHardware = 'Cpu'; model = 'Models/ggml-base.bin'
    engines = @(@{ id = 'cpu'; executable = 'Tools/whisper-cpu/whisper-cli.exe' }, @{ id = 'vulkan'; executable = 'Tools/whisper-vulkan/whisper-cli.exe' })
    provenanceAndBuildDetails = 'THIRD_PARTY.asr.json'; configurationPolicy = 'Existing user Data/settings/whisper.json wins; bundled defaults are used only when it is absent.'
}) -Force
$portable | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $portablePath -Encoding utf8

$quickStart = @'
# 打开即用：完整录音与本地识别包

1. 将整个 ZIP 解压到一个可写的本地文件夹，再打开其中的 ProjectEcho.exe。保留 CaptureHost、Tools、Models、LICENSES 等文件夹，不要只复制 EXE。
2. 本包已包含独立 CPU 识别程序与多语言 base 模型；首次启动默认 CPU、自动识别语言、2 个线程。不需要另找原电脑的 H 盘路径。录音、导出和本地识别均在本机完成；程序不自动上传音频或文字。
3. 录音并保存，或导入本地音频后，在会议库选择会议，进入“本地识别”。base 适合先跑通流程；金融名称、ticker、人名和行业词可填到 initial prompt。识别文字与近似字幕时间仍需人工核对。每次识别保存独立版本。
4. 使用 AMD 等支持 Vulkan 的显卡：本地识别硬件选为 Vulkan。本包的 CPU/Vulkan 引擎会随硬件选择切换到对应程序，界面显示实际路径。选择显卡编号（通常先试 0），需要供新会议沿用时点“保存为默认配置”，再主动开始识别。包外或自定义引擎不会自动替换；请自行选择包含对应后端的 whisper-cli.exe。实际未使用所选 GPU 时仍明确失败。
5. Vulkan 需要显卡厂商提供的兼容驱动；本包不安装驱动或修改系统设置。没有兼容 GPU 时使用默认 CPU 引擎。具体引擎版本、来源、构建详情、运行库与许可证见 THIRD_PARTY.asr.json 和 LICENSES。
6. 要换回 CPU，将硬件改回 CPU；若当前使用本包引擎，程序会切回 Tools/whisper-cpu/whisper-cli.exe。要换模型，选择本地兼容的 ggml-*.bin 文件；中文或中英混合不要选名称带 .en 的英文专用模型。

整个文件夹搬到其他本地路径后，随包的相对配置仍可定位程序和模型。后续保存的用户配置在 Data/settings/whisper.json；它优先于随包默认配置。自行选择包外程序或模型会保留绝对路径，移动到另一台电脑后需重新选择这些包外文件。损坏或失效的用户配置不会被静默替换。

Data 为使用时产生的私有数据，本 ZIP 不含旧会议、录音、字幕、个人提示词或浏览器资料。更详细用法见“使用说明.zh-CN.md”；当前测试范围和未完成验证见“验证说明.zh-CN.md”。
'@
[IO.File]::WriteAllText((Join-Path $output '打开即用.zh-CN.md'), $quickStart, [Text.UTF8Encoding]::new($false))

# Inventory is regenerated only after all immutable runtime/model/license/configuration files are present.
& (Join-Path $PSScriptRoot 'Update-PortableManifest.ps1') -PortableDirectory $output
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output, $zip, [IO.Compression.CompressionLevel]::Optimal, $true, [Text.Encoding]::UTF8)
$zipHash = Hash-File $zip
($zipHash + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath $zipHashPath -Encoding utf8
$finalManifest = Get-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Raw | ConvertFrom-Json
[ordered]@{
    schemaVersion = 1; generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    archive = [IO.Path]::GetFileName($zip); archiveBytes = (Get-Item -LiteralPath $zip).Length; archiveSha256 = $zipHash
    topLevelDirectory = [IO.Path]::GetFileName($output); bundledFiles = $finalManifest.fileCount; uncompressedBytes = $finalManifest.totalBytes
    releaseManifestSha256 = Hash-File (Join-Path $output 'release-manifest.json')
    cpuExecutableSha256 = Hash-File (Join-Path $output 'Tools/whisper-cpu/whisper-cli.exe')
    vulkanExecutableSha256 = Hash-File (Join-Path $output 'Tools/whisper-vulkan/whisper-cli.exe')
    baseModelSha256 = Hash-File (Join-Path $output 'Models/ggml-base.bin')
    asrProvenanceSha256 = Hash-File (Join-Path $output 'THIRD_PARTY.asr.json')
    sourceMode = 'Packaging only; no recording, microphone, inference or network'
    relocationValidation = 'NOT_RUN by packaging; validate the freshly extracted ZIP separately'
    cleanWindowsValidation = 'NOT_RUN by packaging'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath -Encoding utf8
Write-Host ('Complete portable folder: ' + $output)
Write-Host ('ZIP: ' + $zip)
Write-Host ('ZIP SHA256: ' + $zipHash)
