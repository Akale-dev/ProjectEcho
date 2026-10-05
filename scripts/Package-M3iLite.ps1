[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkspaceRoot, [Parameter(Mandatory)][string]$BaselineDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
$baseline = [IO.Path]::GetFullPath($BaselineDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($path in @($source,$baseline,$output)) {
    if (-not $path.StartsWith($workspace + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'All inputs and outputs must remain inside the authorized workspace.' }
    for ($p=$path; $p -and $p.Length -ge $workspace.Length; $p=[IO.Path]::GetDirectoryName($p)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse paths refused.' }
    }
}
if (-not $output.StartsWith((Join-Path $workspace 'artifacts') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be a fresh workspace artifact.' }
foreach ($path in @($output,($output+'.zip'),($output+'.manifest.sha256'),($output+'.package.json'))) { if (Test-Path -LiteralPath $path) { throw ('Already exists: '+$path) } }
$baselineManifest = Join-Path $baseline 'release-manifest.json'
$declared = (Get-Content -LiteralPath ($baseline+'.manifest.sha256') -Raw).Split(' ',[StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash -LiteralPath $baselineManifest).Hash -ne $declared) { throw 'Baseline manifest mismatch.' }
$manifest = Get-Content -LiteralPath $baselineManifest -Raw | ConvertFrom-Json
$launch = Join-Path $workspace 'scripts\Dotnet.ps1'
$app = Join-Path $source 'src\ProjectEcho.App\ProjectEcho.App.csproj'
$hostProject = Join-Path $source 'src\ProjectEcho.CaptureHost\ProjectEcho.CaptureHost.csproj'
& $launch -Arguments @('restore',$app,'-r','win-x64','--source',(Join-Path $workspace '.local\nuget-packages'),'-p:NuGetAudit=false')
& $launch -Arguments @('publish',$app,'--no-restore','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$output)
& $launch -Arguments @('publish',$hostProject,'--no-restore','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',(Join-Path $output 'CaptureHost'))
$copied = @()
foreach ($entry in $manifest.files) {
    $relative = $entry.path.Replace('\','/')
    if ($relative -notmatch '^(LICENSES/|Tools/whisper-(cpu|vulkan)/|Models/ggml-base\.bin$|tests/audio_probe\.html$|[^/]+\.md$|THIRD_PARTY(\.asr)?\.json$|bundled-whisper\.json$|portable\.json$)') { continue }
    $from = [IO.Path]::GetFullPath((Join-Path $baseline $relative)); $to=[IO.Path]::GetFullPath((Join-Path $output $relative))
    if (-not $from.StartsWith($baseline.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $to.StartsWith($output.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe manifest path.' }
    if ((Get-Item -LiteralPath $from).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $from).Hash -ne $entry.sha256) { throw ('Baseline asset changed: '+$relative) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)) | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
    $copied += $relative
}
foreach ($required in @('ProjectEcho.exe','ProjectEcho.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll','e_sqlite3.dll','CaptureHost/ProjectEcho.CaptureHost.exe','CaptureHost/coreclr.dll','CaptureHost/hostfxr.dll','Tools/whisper-cpu/whisper-cli.exe','Tools/whisper-vulkan/whisper-cli.exe','Models/ggml-base.bin')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $required) -PathType Leaf)) { throw ('Incomplete bundle: '+$required) }
}
foreach ($config in @('ProjectEcho.runtimeconfig.json','CaptureHost/ProjectEcho.CaptureHost.runtimeconfig.json')) {
    $runtime=Get-Content -LiteralPath (Join-Path $output $config) -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.includedFrameworks -or @($runtime.runtimeOptions.includedFrameworks | Where-Object version -ne '10.0.12').Count) { throw 'Unexpected self-contained runtime.' }
}
$portablePath=Join-Path $output 'portable.json'
$portable=Get-Content -LiteralPath $portablePath -Raw | ConvertFrom-Json
$portable | Add-Member NoteProperty milestone 'M3I' -Force
$portable | Add-Member NoteProperty edition 'Lite' -Force
$portable | Add-Member NoteProperty releaseStatus 'FIXED_AND_LOCAL_TESTED_COMPANY_RETEST_PENDING' -Force
$portable | Add-Member NoteProperty baseline 'M3Hfix-Final; no Qwen or speaker components' -Force
$portable | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $portablePath -Encoding utf8
[IO.File]::WriteAllText((Join-Path $output 'M3I-Lite修复与验证.zh-CN.md'), [IO.File]::ReadAllText((Join-Path $source 'docs/82_M3I_LITE_CAPTURE_AND_ASR_FIX.md')).Replace('(83_M3I_TO_M5C_SYNC_CHECKLIST.md)', '(M3I-待同步M5C清单.zh-CN.md)'))
Copy-Item -LiteralPath (Join-Path $source 'docs/83_M3I_TO_M5C_SYNC_CHECKLIST.md') -Destination (Join-Path $output 'M3I-待同步M5C清单.zh-CN.md')
$readme=Join-Path $output 'README.zh-CN.md'
$text=[IO.File]::ReadAllText($readme)
[IO.File]::WriteAllText($readme,"# ProjectEcho M3I Lite`n`n本版基于 M3Hfix 修复大音频包和 Whisper 零时长字幕。请先阅读 [M3I 修复与验证](M3I-Lite修复与验证.zh-CN.md)。公司电脑复测尚待完成。以下保留基础使用说明及历史验收。`n`n"+$text)
& (Join-Path $workspace 'scripts/Update-PortableManifest.ps1') -PortableDirectory $output
[IO.Compression.ZipFile]::CreateFromDirectory($output,($output+'.zip'),[IO.Compression.CompressionLevel]::Optimal,$true,[Text.Encoding]::UTF8)
$hash=(Get-FileHash -LiteralPath ($output+'.zip')).Hash.ToLowerInvariant()
($hash+'  '+[IO.Path]::GetFileName($output)+'.zip') | Set-Content -LiteralPath ($output+'.zip.sha256') -Encoding utf8
$finalManifest=Get-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Raw | ConvertFrom-Json
[ordered]@{version='M3I';edition='Lite';baseline='M3Hfix-Final';archiveSha256=$hash;archiveBytes=(Get-Item -LiteralPath ($output+'.zip')).Length;files=$finalManifest.fileCount;manifestSha256=(Get-FileHash -LiteralPath (Join-Path $output 'release-manifest.json')).Hash.ToLowerInvariant();reusedVerifiedAssets=$copied;sourceBranch='codex/m3i-lite';sourceDirectory='lite';companyRetest='NOT_RUN';cleanWindows='NOT_RUN';captureStarted=$false;microphoneStarted=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($output+'.package.json') -Encoding utf8
Write-Host ('Packaged '+$output+'.zip')