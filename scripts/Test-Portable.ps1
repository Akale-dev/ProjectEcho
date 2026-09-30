[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PortableDirectory)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$bundle = [IO.Path]::GetFullPath($PortableDirectory)
if (-not $bundle.StartsWith((Join-Path $root 'artifacts').TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test only a generated portable folder in this project.' }
for ($ancestor = $bundle; $ancestor -and $ancestor.Length -ge $root.Length; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Portable test folder must not pass through a link.' }
}
Get-ChildItem -LiteralPath $bundle -Recurse | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Portable test refuses linked content.' }
}
$evidence = Join-Path $root ('.local\diagnostics\portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $bundle 'release-manifest.json') -Raw | ConvertFrom-Json
$declaredManifestHash = (Get-Content -LiteralPath ($bundle + '.manifest.sha256') -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
if ((Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json') -Algorithm SHA256).Hash -ne $declaredManifestHash) { throw 'Release manifest hash mismatch.' }
$actualCount = @(Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $bundle 'release-manifest.json') }).Count
if ($actualCount -ne $manifest.fileCount -or $manifest.files.Count -ne $manifest.fileCount) { throw 'Unlisted/missing files found in portable bundle.' }
if (@($manifest.files | Group-Object path | Where-Object Count -gt 1).Count -gt 0) { throw 'Duplicate manifest paths found.' }
foreach ($entry in $manifest.files) {
    $file = [IO.Path]::GetFullPath((Join-Path $bundle $entry.path))
    if (-not $file.StartsWith($bundle.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes bundle.' }
    if ((Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw ('Bundle hash/length mismatch: ' + $entry.path) }
}
function New-BundledProcess([string]$Executable) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $root
    # Invalid per-process runtime roots exercise the sibling self-contained runtime, without changing system settings.
    $info.Environment['DOTNET_ROOT'] = Join-Path $root '.local\no-runtime-for-portable-test'
    $info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    return $info
}
$appInfo = New-BundledProcess (Join-Path $bundle 'ProjectEcho.exe')
$appInfo.ArgumentList.Add('--smoke-test')
$appProcess = [Diagnostics.Process]::Start($appInfo)
try {
    if (-not $appProcess.WaitForExit(30000)) { $appProcess.Kill(); $null = $appProcess.WaitForExit(5000); throw 'Portable WPF smoke did not exit.' }
    if ($appProcess.ExitCode -ne 0) { throw ('Portable WPF smoke failed: ' + $appProcess.ExitCode) }
} finally { $appProcess.Dispose() }
Copy-Item -LiteralPath (Join-Path $root '.local\diagnostics\m0-wpf-smoke.json') -Destination (Join-Path $evidence 'app-smoke.json')
$session = [Guid]::NewGuid().ToString('N')
$pipeName = 'ProjectEcho-' + $session
$pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
$pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,$pipeOptions)
$process = $null; $reader = $null; $writer = $null
try {
    $hostInfo = New-BundledProcess (Join-Path $bundle 'CaptureHost\ProjectEcho.CaptureHost.exe')
    $hostInfo.ArgumentList.Add($pipeName); $hostInfo.ArgumentList.Add($session)
    $process = [Diagnostics.Process]::Start($hostInfo)
    $pipe.WaitForConnectionAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    $reader = [IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
    $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
    $writer.AutoFlush = $true
    $ready = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | ConvertFrom-Json
    if ($ready.Kind -ne 'ready' -or $ready.SessionId -ne $session) { throw 'Portable Host handshake failed.' }
    $runtimeModules = @($process.Modules | Where-Object { $_.ModuleName -in @('coreclr.dll','hostfxr.dll') })
    if ($runtimeModules.Count -ne 2) { throw 'Cannot verify both bundled runtime modules.' }
    foreach ($module in $runtimeModules) {
        if (-not $module.FileName.StartsWith((Join-Path $bundle 'CaptureHost').TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Host used a runtime outside its self-contained folder.' }
    }
    # Never send start, record or mic_on. This checks runtime/IPC only.
    $writer.WriteLine((@{ProtocolVersion=1;SessionId=$session;RequestId='portable-no-capture';Command='stop'} | ConvertTo-Json -Compress))
    if (-not $process.WaitForExit(10000)) { $process.Kill(); $null = $process.WaitForExit(5000); throw 'Portable Host idle-stop failed.' }
    if ($process.ExitCode -ne 0) { throw ('Portable Host failed: ' + $process.ExitCode) }
    @{
        checkedUtc=[DateTimeOffset]::UtcNow.ToString('O');os=[Environment]::OSVersion.Version.ToString();architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        bundle=[IO.Path]::GetRelativePath($root,$bundle);manifestFilesVerified=$manifest.files.Count;appSmoke='PASS';hostReadyIdleStop='PASS'
        runtimeResolution='PASS: coreclr.dll and hostfxr.dll loaded from bundled CaptureHost with invalid DOTNET_ROOT';captureStarted=$false;microphoneStarted=$false
        cleanWindows='NOT_RUN: this is the development machine';realPortableRecordingPlaybackExport='NOT_RUN'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'portable-smoke.json') -Encoding utf8
} finally {
    if ($writer) { $writer.Dispose() }; if ($reader) { $reader.Dispose() }; $pipe.Dispose()
    if ($process) { if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }; $process.Dispose() }
}
Write-Host ('PASS portable integrity, WPF startup and bundled Host IPC (no capture): ' + $evidence)
