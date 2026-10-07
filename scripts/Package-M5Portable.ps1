[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$SherpaBundleDirectory,
    [string]$BaselineDirectory='artifacts/ProjectEcho-win-x64-20261004-M4-AlignFix',
    [string]$PublishedDirectory='',
    [string]$SupplementReportPath='',
    [string]$SupplementFileName='M5尾端时间修复.zh-CN.md',
    [ValidateSet('M5','M5B','M5C','M5D','M5Dfix')][string]$ReleaseLabel='M5',
    [string]$DotnetPath=''
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Resolve-ProjectPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path,$projectRoot)
    if(-not $full.StartsWith($projectRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'All M5 package inputs and outputs must stay in ProjectEcho.'}
    for($p=$full;$p;$p=[IO.Path]::GetDirectoryName($p)){
        if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw ('Reparse path is prohibited: '+$p)}
    }
    return $full
}
function Hash-File([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
$output=Resolve-ProjectPath $OutputDirectory
$baseline=Resolve-ProjectPath $BaselineDirectory
$sherpa=Resolve-ProjectPath $SherpaBundleDirectory
if(-not $output.StartsWith((Join-Path $projectRoot 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be a fresh artifacts folder.'}
if(Test-Path -LiteralPath $output){throw 'Package destination exists; choose a fresh folder.'}
if(Test-Path -LiteralPath ($output+'.zip')){throw 'ZIP destination exists; choose a fresh name.'}
$baseManifest=Get-Content -LiteralPath (Join-Path $baseline 'release-manifest.json') -Raw | ConvertFrom-Json
if(-not (Test-Path -LiteralPath (Join-Path $sherpa 'bundled-sherpa.json'))){throw 'Missing reviewed sherpa bundle metadata.'}
if($PublishedDirectory){
    $published=Resolve-ProjectPath $PublishedDirectory
    if(-not (Test-Path -LiteralPath (Join-Path $published 'portable.json'))){throw 'Missing self-contained publish metadata.'}
    foreach($file in Get-ChildItem -LiteralPath $published -File -Recurse){
        $source=Resolve-ProjectPath $file.FullName
        $relative=[IO.Path]::GetRelativePath($published,$source)
        if($relative -match '(^|[\\/])(Data|\.local|\.tools|profiles?)([\\/]|$)' -or $file.Extension -in @('.wav','.mp3','.m4a','.srt','.vtt','.pdb','.dmp')){throw 'Private/generated test content in publish input.'}
        $destination=Resolve-ProjectPath (Join-Path $output $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
}else{
    & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -DotnetPath $DotnetPath -OutputDirectory $output
}
function Copy-Checked([string]$Source,[string]$Relative,[string]$ExpectedHash=''){
    $sourcePath=Resolve-ProjectPath $Source
    $destination=Resolve-ProjectPath (Join-Path $output $Relative)
    if(-not $destination.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Bundle relative path escapes output.'}
    if($Relative -match '(^|[\\/])(Data|\.local|\.tools|profiles?)([\\/]|$)' -or [IO.Path]::GetExtension($Relative) -in @('.wav','.mp3','.m4a','.flac','.srt','.vtt','.pdb','.dmp')){throw ('Forbidden package data: '+$Relative)}
    $actual=Hash-File $sourcePath
    if($ExpectedHash -and $actual -ne $ExpectedHash){throw ('Baseline hash mismatch: '+$Relative)}
    if(Test-Path -LiteralPath $destination){
        if((Hash-File $destination) -ne $actual){throw ('Unexpected package collision: '+$Relative)}
        return
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destination
    if((Hash-File $destination) -ne $actual){throw ('Copy verification failed: '+$Relative)}
}
# Only the previously inventoried engine/model/license assets are reused. Baseline Data,
# tests, old application assemblies and old validation claims are never recursively copied.
foreach($entry in $baseManifest.files){
    $relative=[string]$entry.path
    if($relative -match '^(Tools|Models|LICENSES)/' -or $relative -match '^M4.*\.md$' -or $relative -in @('bundled-whisper.json','bundled-qwen.json','THIRD_PARTY.asr.json','THIRD_PARTY.qwen.json')){
        Copy-Checked (Join-Path $baseline $relative) $relative $entry.sha256
    }
}
foreach($file in Get-ChildItem -LiteralPath $sherpa -Recurse -File){
    $relative=[IO.Path]::GetRelativePath($sherpa,$file.FullName).Replace('\','/')
    if($relative -notmatch '^(Tools/Sherpa/|Models/Sherpa/|LICENSES/Sherpa/)' -and $relative -notin @('bundled-sherpa.json','THIRD_PARTY.sherpa.json')){throw ('Unexpected sherpa bundle file: '+$relative)}
    Copy-Checked $file.FullName $relative
}
Copy-Checked (Join-Path $projectRoot 'docs/75_M5_MEETING_SPEAKERS.md') 'M5同场说话人与验证.zh-CN.md'
Copy-Checked (Join-Path $projectRoot 'docs/76_M5_USER_GUIDE.md') 'M5使用说明.zh-CN.md'
Copy-Checked (Join-Path $projectRoot 'docs/77_M5_DATA_CONTRACT.md') 'M5数据合同.zh-CN.md'
Copy-Checked (Join-Path $projectRoot 'docs/78_M5_NATIVE_EVIDENCE.md') 'M5原生模型验证.zh-CN.md'
if($ReleaseLabel -in @('M5C','M5D','M5Dfix')){Copy-Checked (Join-Path $projectRoot 'docs/80_M5B_REPLAY_EDITING_AND_VOICE_QUALITY.md') 'M5B更新与验证.zh-CN.md'}
if($ReleaseLabel -in @('M5D','M5Dfix')){Copy-Checked (Join-Path $projectRoot 'docs/81_M5C_GLOBAL_CLUSTERING_FIX.md') 'M5C聚类更新与验证.zh-CN.md'}
if($ReleaseLabel -eq 'M5Dfix'){Copy-Checked (Join-Path $projectRoot 'docs/85_M5D_LITE_FIXES_FORWARD_PORT.md') 'M5D同步修复与验证.zh-CN.md'}
foreach($document in @('M5同场说话人与验证.zh-CN.md','M5使用说明.zh-CN.md','M5数据合同.zh-CN.md','M5原生模型验证.zh-CN.md')){
    $documentPath=Join-Path $output $document
    $text=[IO.File]::ReadAllText($documentPath).Replace('(75_M5_MEETING_SPEAKERS.md)','(M5同场说话人与验证.zh-CN.md)').Replace('(76_M5_USER_GUIDE.md)','(M5使用说明.zh-CN.md)').Replace('(77_M5_DATA_CONTRACT.md)','(M5数据合同.zh-CN.md)').Replace('(78_M5_NATIVE_EVIDENCE.md)','(M5原生模型验证.zh-CN.md)')
    if($ReleaseLabel -in @('M5B','M5C','M5D','M5Dfix')){
        $clusterReport=if($ReleaseLabel -in @('M5D','M5Dfix')){'M5C聚类更新与验证.zh-CN.md'}else{$SupplementFileName}
        $text=$text.Replace('(80_M5B_REPLAY_EDITING_AND_VOICE_QUALITY.md)','(M5B更新与验证.zh-CN.md)').Replace('(81_M5C_GLOBAL_CLUSTERING_FIX.md)','('+$clusterReport+')')
    }
    [IO.File]::WriteAllText($documentPath,$text,[Text.UTF8Encoding]::new($false))
}
[IO.File]::WriteAllText((Join-Path $output '打开即用.zh-CN.md'), @'
# ProjectEcho M5

完整解压后启动 ProjectEcho.exe，保留 CaptureHost、Tools、Models、LICENSES。
录音/导出无需模型、SDK或预装.NET。说话人分析为会后用户主动触发的独立CPU任务。

本包包含 sherpa-onnx 及两个人物模型；在“转写与字幕 → 同场说话人”核对许可后运行。
自动人数可能误分，已知人数也不保证身份正确。短插话、重叠和自然会议质量限制见验证说明。
显示改名、单段改人、合并/拆出和人工切点均保存为新版本。没有跨会身份库、不推断姓名、不上传。

- [M5使用说明](M5使用说明.zh-CN.md)
- [M5实现与验证边界](M5同场说话人与验证.zh-CN.md)
- [M5原生模型证据](M5原生模型验证.zh-CN.md)

Whisper与Qwen功能保持；Qwen模型仍外置。旧版本和Data不自动迁移覆盖。
这是本机验证交付，不代表已通过干净Windows或既有M1/M2现场录音验收。
'@,[Text.UTF8Encoding]::new($false))
if($SupplementReportPath){
    if([IO.Path]::GetFileName($SupplementFileName) -ne $SupplementFileName -or [IO.Path]::GetExtension($SupplementFileName) -ne '.md'){throw 'Supplement must be a local Markdown filename.'}
    Copy-Checked (Resolve-ProjectPath $SupplementReportPath) $SupplementFileName
    $supplement=Join-Path $output $SupplementFileName
    [IO.File]::WriteAllText($supplement,[IO.File]::ReadAllText($supplement).Replace('(77_M5_DATA_CONTRACT.md)','(M5数据合同.zh-CN.md)'),[Text.UTF8Encoding]::new($false))
    [IO.File]::AppendAllText((Join-Path $output '打开即用.zh-CN.md'), "`n本包更新：[使用与验证说明]($SupplementFileName)。原音频和旧版本保持，具体功能和验证限制见说明。`n", [Text.UTF8Encoding]::new($false))
}
$startPath=Join-Path $output '打开即用.zh-CN.md'
[IO.File]::WriteAllText($startPath,[IO.File]::ReadAllText($startPath).Replace('# ProjectEcho M5','# ProjectEcho '+$ReleaseLabel),[Text.UTF8Encoding]::new($false))
$portablePath=Join-Path $output 'portable.json'
$portable=Get-Content -LiteralPath $portablePath -Raw | ConvertFrom-Json
$portable.milestone=$ReleaseLabel
if($ReleaseLabel -in @('M5D','M5Dfix')){$portable | Add-Member -Force -NotePropertyName edition -NotePropertyValue 'Pro'}
$portable | Add-Member -Force -NotePropertyName diarization -NotePropertyValue 'Optional user-triggered native CPU sherpa-onnx; meeting-local global clustering; immutable speaker IDs and revisions; display names and corrections; label export. See M5 report for tested scope.'
$portable | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $portablePath -Encoding utf8
& (Join-Path $PSScriptRoot 'Update-PortableManifest.ps1') -PortableDirectory $output
[IO.Compression.ZipFile]::CreateFromDirectory($output,$output+'.zip',[IO.Compression.CompressionLevel]::Optimal,$false)
$zipHash=Hash-File ($output+'.zip')
($zipHash+'  '+[IO.Path]::GetFileName($output)+'.zip') | Set-Content -LiteralPath ($output+'.zip.sha256') -Encoding utf8
[ordered]@{createdUtc=[DateTimeOffset]::UtcNow;milestone=$ReleaseLabel;directory=$output;zipBytes=(Get-Item -LiteralPath ($output+'.zip')).Length;zipSha256=$zipHash;baselineManifestSha256=Hash-File (Join-Path $baseline 'release-manifest.json');manifestSha256=Hash-File (Join-Path $output 'release-manifest.json');verification='PACKAGED; extracted hash/UI/native verification recorded separately';cleanWindows='NOT_RUN'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($output+'.package.json') -Encoding utf8
Write-Host ('M5 portable package: '+$output+'.zip')
