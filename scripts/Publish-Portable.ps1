[CmdletBinding()]
param([string]$DotnetPath = '', [string]$OutputDirectory = '')
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifacts = Join-Path $root 'artifacts'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifacts ('ProjectEcho-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($artifacts.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable output must be a fresh folder beneath this project artifacts directory.' }
if (Test-Path -LiteralPath $output) { throw 'Publish destination already exists. Choose a fresh folder; existing output is not overwritten or deleted.' }
for ($parent = [IO.Path]::GetDirectoryName($output); $parent -and $parent.Length -ge $root.Length; $parent = [IO.Path]::GetDirectoryName($parent)) {
    if ((Test-Path -LiteralPath $parent) -and (((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw 'Publish output must not pass through a linked/reparse directory.'
    }
}
$launcher = Join-Path $PSScriptRoot 'Dotnet.ps1'
$app = Join-Path $root 'src\ProjectEcho.App\ProjectEcho.App.csproj'
$hostProject = Join-Path $root 'src\ProjectEcho.CaptureHost\ProjectEcho.CaptureHost.csproj'
$cache = Join-Path $root '.local\nuget-packages'
# Publish is offline and uses the approved project cache. NuGet vulnerability auditing is explicitly NOT_RUN.
& $launcher -DotnetPath $DotnetPath -Arguments @('restore',$app,'-r','win-x64','--source',$cache,'-p:NuGetAudit=false')
& $launcher -DotnetPath $DotnetPath -Arguments @('publish',$app,'--no-restore','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$output)
$hostOutput = Join-Path $output 'CaptureHost'
& $launcher -DotnetPath $DotnetPath -Arguments @('publish',$hostProject,'--no-restore','-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$hostOutput)
foreach ($required in @('ProjectEcho.exe','ProjectEcho.dll','ProjectEcho.runtimeconfig.json','hostfxr.dll','coreclr.dll','PresentationFramework.dll','CaptureHost\ProjectEcho.CaptureHost.exe','CaptureHost\ProjectEcho.CaptureHost.runtimeconfig.json','CaptureHost\hostfxr.dll','CaptureHost\coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $required) -PathType Leaf)) { throw ('Incomplete portable publication: ' + $required) }
}
foreach ($runtimeConfig in @('ProjectEcho.runtimeconfig.json','CaptureHost\ProjectEcho.CaptureHost.runtimeconfig.json')) {
    $config = Get-Content -LiteralPath (Join-Path $output $runtimeConfig) -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw ('Not self-contained: ' + $runtimeConfig) }
    foreach ($framework in $config.runtimeOptions.includedFrameworks) {
        if ($framework.version -ne '10.0.12') { throw ('Unexpected runtime version: ' + $framework.version) }
    }
}
New-Item -ItemType Directory -Path (Join-Path $output 'LICENSES'), (Join-Path $output 'tests') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'tests\audio_probe.html') -Destination (Join-Path $output 'tests\audio_probe.html')
$readme = (Get-Content -LiteralPath (Join-Path $root 'docs\38_PORTABLE_GUIDE.md') -Raw).Replace('(49_M3B_USER_GUIDE_ZH.md)', '(使用说明.zh-CN.md)')
[IO.File]::WriteAllText((Join-Path $output 'README.zh-CN.md'), $readme)
$guide = (Get-Content -LiteralPath (Join-Path $root 'docs\49_M3B_USER_GUIDE_ZH.md') -Raw).Replace('(../STATUS.md)', '(README.zh-CN.md)').Replace('(38_PORTABLE_GUIDE.md)', '(README.zh-CN.md)')
[IO.File]::WriteAllText((Join-Path $output '使用说明.zh-CN.md'), $guide)
Get-ChildItem -LiteralPath (Join-Path $root 'tools\licenses') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output ('LICENSES\' + $_.Name))
}
@{
    schemaVersion = 1; application = 'ProjectEcho'; milestone = 'M3b'; releaseStatus = 'IMPLEMENTED_ACCEPTANCE_IN_PROGRESS'
    runtime = 'self-contained'; rid = 'win-x64'; defaultDataDirectory = 'Data'; compressedFormat = 'AAC in M4A; MPEG-1 Layer III in MP3'
    transcription = 'Local file import and optional user-configured whisper.cpp; no bundled executable/model or SQLite'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'portable.json') -Encoding utf8
$components = @(
    @{ id='NAudio.Core'; version='3.1.0'; repository='https://github.com/naudio/NAudio/tree/v3.1.0'; license='LICENSES/NAudio-MIT.txt' },
    @{ id='NAudio.Wasapi'; version='3.1.0'; repository='https://github.com/naudio/NAudio/tree/v3.1.0'; license='LICENSES/NAudio-MIT.txt' },
    @{ id='System.Numerics.Tensors'; version='9.0.0'; repository='https://github.com/dotnet/runtime/tree/v9.0.0'; license='LICENSES/System.Numerics.Tensors-LICENSE.txt' },
    @{ id='Microsoft.NETCore.App.Runtime.win-x64'; version='10.0.12'; repository='https://github.com/dotnet/dotnet'; license='LICENSES/dotnet-runtime-LICENSE.txt' },
    @{ id='Microsoft.WindowsDesktop.App.Runtime.win-x64'; version='10.0.12'; repository='https://github.com/dotnet/dotnet'; license='LICENSES/dotnet-windowsdesktop-LICENSE.txt' }
)
foreach ($component in $components) {
    $id = $component.id.ToLowerInvariant()
    $version = $component.version
    $packageDirectory = Join-Path $cache ($id + '\' + $version)
    $package = Join-Path $packageDirectory ($id + '.' + $version + '.nupkg')
    $metadata = Get-Content -LiteralPath (Join-Path $packageDirectory '.nupkg.metadata') -Raw | ConvertFrom-Json
    if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { throw ('Unexpected package source: ' + $id) }
    $sha512 = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($package)))
    # NuGet metadata contentHash excludes the repository signature; it is NOT the signed archive hash.
    $archiveHash = (Get-Content -LiteralPath ($package + '.sha512') -Raw).Trim()
    if ($sha512 -ne $archiveHash) { throw ('Signed package archive hash mismatch: ' + $id) }
    $matchedLockHash = $false
    foreach ($lockFile in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter packages.lock.json -File) {
        $lock = Get-Content -LiteralPath $lockFile.FullName -Raw | ConvertFrom-Json
        foreach ($target in $lock.dependencies.PSObject.Properties) {
            $property = $target.Value.PSObject.Properties[$component.id]
            if ($property -and $property.Value.PSObject.Properties['contentHash']) {
                if ($property.Value.contentHash -ne $metadata.contentHash) { throw ('NuGet content hash differs from locked dependency: ' + $id) }
                $matchedLockHash = $true
            }
        }
    }
    $component.source = 'https://api.nuget.org/v3-flatcontainer/' + $id + '/' + $version + '/' + $id + '.' + $version + '.nupkg'
    $component.packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $component.packageSha512Base64 = $sha512
    $component.nugetContentHash = $metadata.contentHash
    $component.nugetContentHashMatchedProjectLock = $matchedLockHash
    [xml]$nuspec = Get-Content -LiteralPath (Join-Path $packageDirectory ($id + '.nuspec')) -Raw
    $component.sourceCommit = $nuspec.package.metadata.repository.commit
    $component.licenseExpression = 'MIT'
}
$components += @{
    id='Windows Media Foundation AAC and MP3 encoder/decoder'; version='Supplied and serviced by target Windows'; bundled=$false
    source='https://learn.microsoft.com/en-us/windows/win32/medfound/aac-encoder'
    license='Windows system component; no Microsoft codec binary redistributed by ProjectEcho'
    mp3Source='https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder'
    missingCapabilityBehavior='M4A/MP3 fail visibly if required codec/timing checks fail; mixed WAV and originals remain available'
}
$inventory = @{
    schemaVersion=1; components=$components; applicationLicense='ProjectEcho has no public redistribution license declared by its owner in this repository.'
    build=@{sdk='10.0.401';runtime='10.0.12';rid='win-x64';configuration='Release';selfContained=$true;trimmed=$false;singleFile=$false;nugetAudit='NOT_RUN (offline cache restore)'}
    knownVulnerabilityCheck=@{date='2026-09-29';source='https://api.nuget.org/v3/index.json';scope='Six projects including transitive packages';result='No known vulnerabilities listed';qualification='Separate normal-user query; not a complete security review and not rerun by offline publish'}
    cleanWindowsValidation='NOT_RUN; development-machine execution does not establish clean-Windows acceptance.'
}
$inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'THIRD_PARTY.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Update-PortableManifest.ps1') -PortableDirectory $output
Write-Host ('Portable folder: ' + $output)
Write-Warning 'This is a complete self-contained folder, but clean Windows recording/playback/export validation remains NOT_RUN until actually performed.'
