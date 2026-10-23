[CmdletBinding()]
param([string]$ReleaseName = 'ProjectEcho-win-x64-20261023-M5F-Pro-Final')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$lite = $root
$artifacts = Join-Path $root 'artifacts'
if ($ReleaseName -notmatch '^ProjectEcho-win-x64-[a-zA-Z0-9-]+$') { throw 'Use a simple release folder name.' }
$output = Join-Path $artifacts $ReleaseName
$baseline = Join-Path $artifacts 'ProjectEcho-win-x64-20261006-M5E-Pro-Final'
foreach ($path in @($output, "$output.zip", "$output.package.json", "$output.manifest.sha256")) {
    if (Test-Path -LiteralPath $path) { throw "Release output already exists: $path" }
}
function Assert-NoLink([string]$path) {
    for ($p = $path; $p; $p = [IO.Path]::GetDirectoryName($p)) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked path: $p" }
    }
}
Assert-NoLink $output
Assert-NoLink $baseline
# Reuse only the previous verified release inventory; never copy mutable Data.
$manifestPath = Join-Path $baseline 'release-manifest.json'
$expected = (Get-Content -LiteralPath "$baseline.manifest.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $expected) { throw 'Baseline manifest changed.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    $relative = $entry.path.Replace('\','/')
    if ($relative -match '(^|/)(\.\.?|Data|DEBUG|\.local|profiles?)(/|$)' -or [IO.Path]::IsPathRooted($relative)) { throw 'Unsafe baseline entry.' }
    $source = [IO.Path]::GetFullPath((Join-Path $baseline $relative))
    $target = [IO.Path]::GetFullPath((Join-Path $output $relative))
    if (-not $target.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $source.StartsWith($baseline + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escape.' }
    Assert-NoLink $source
    if ((Get-FileHash -LiteralPath $source).Hash -ne $entry.sha256) { throw "Baseline changed: $relative" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$launcher = Join-Path $root 'scripts/Dotnet.ps1'
$app = Join-Path $lite 'src/ProjectEcho.App/ProjectEcho.App.csproj'
$captureHost = Join-Path $lite 'src/ProjectEcho.CaptureHost/ProjectEcho.CaptureHost.csproj'
& $launcher -Arguments @('restore',$app,'-m:1','--disable-parallel','-r','win-x64','--source',(Join-Path $root '.local/nuget-packages'),'-p:NuGetAudit=false')
foreach ($pair in @(@($app,$output),@($captureHost,(Join-Path $output 'CaptureHost')))) {
    & $launcher -Arguments @('publish',$pair[0],'--no-restore','-c','Release','-m:1','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:DebugType=None','-p:DebugSymbols=false','-p:UseSharedCompilation=false','-o',$pair[1])
}
Copy-Item -LiteralPath (Join-Path $root 'docs/96_M5F_M3P_TIMING_SYNC.md') -Destination (Join-Path $output 'M5F字幕时间戳检查与验证.zh-CN.md')
$readmePath = Join-Path $output '打开即用.zh-CN.md'
$intro = "# M5F Pro（2026-10-23）`r`n`r`n同步 M3P 字幕时间戳浅红高亮、寻找错误及导出前纠错定位，适用于普通与人物版字幕；保留 Qwen 全文保全和人物修订。详见 [M5F字幕时间戳检查与验证](M5F字幕时间戳检查与验证.zh-CN.md)。完整解压并运行 ProjectEcho.exe，在新目录选择原数据目录；保留原数据及 settings/data-root.json。`r`n`r`n"
[IO.File]::WriteAllText($readmePath,$intro+(Get-Content -LiteralPath $readmePath -Raw))
$portable = Get-Content -LiteralPath (Join-Path $output 'portable.json') -Raw | ConvertFrom-Json
$portable.milestone = 'M5F-Pro-20261023'
$portable | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'portable.json') -Encoding utf8
& (Join-Path $root 'scripts/Update-PortableManifest.ps1') -PortableDirectory $output
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::Open("$output.zip", [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $output -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,($ReleaseName + '/' + $relative),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath "$output.zip").Hash.ToLowerInvariant()
"$hash  $ReleaseName.zip" | Set-Content -LiteralPath "$output.zip.sha256" -Encoding utf8
[ordered]@{milestone='M5F-Pro-20261023';archiveSha256=$hash;archiveBytes=(Get-Item -LiteralPath "$output.zip").Length;baseline=$baseline;capture='NOT_RUN';microphone='NOT_RUN';cleanWindows='NOT_RUN'} | ConvertTo-Json | Set-Content -LiteralPath "$output.package.json" -Encoding utf8
Write-Host "Created $output.zip"
