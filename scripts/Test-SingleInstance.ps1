[CmdletBinding()]
param(
    [string]$Executable = '',
    [string]$OutputDirectory = ''
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $Executable) { $Executable = Join-Path $root 'src\ProjectEcho.App\bin\Release\net10.0-windows\ProjectEcho.exe' }
$exe = [IO.Path]::GetFullPath($Executable)
if (-not $exe.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Use an already-built ProjectEcho.exe inside this project. This probe never builds or downloads dependencies.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('.local\M3F\single-instance-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
$evidence = [IO.Path]::GetFullPath($OutputDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $root '.local\M3F')).TrimEnd('\') + '\'
if (-not $evidence.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must remain inside .local/M3F.' }
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$appAssembly = Join-Path ([IO.Path]::GetDirectoryName($exe)) 'ProjectEcho.dll'
$appAssemblyHash = (Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash
$windowsVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$results = [Collections.Generic.List[object]]::new()

function Start-Probe([string]$Scope, [int]$StartupDelay = 0, [bool]$Minimized = $false) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory = $root
    foreach ($argument in @('--single-instance-probe', '--instance-scope', $Scope, '--probe-output', $evidence,
            '--probe-startup-delay-ms', $StartupDelay.ToString(), '--probe-hold-ms', '30000')) { $info.ArgumentList.Add($argument) }
    if ($Minimized) { $info.ArgumentList.Add('--probe-minimized') }
    $runtime = Join-Path $root '.tools\dotnet'
    if (Test-Path -LiteralPath (Join-Path $runtime 'dotnet.exe')) {
        $info.Environment['DOTNET_ROOT'] = $runtime
        $info.Environment['DOTNET_ROOT_X64'] = $runtime
    }
    $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    return $process
}

function Read-Report([Diagnostics.Process]$Process) {
    $path = Join-Path $evidence ($Process.Id.ToString() + '.json')
    if (Test-Path -LiteralPath $path) { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    return $null
}

function Wait-Phase([Diagnostics.Process]$Process, [string]$Phase) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while ($deadline.Elapsed.TotalSeconds -lt 20) {
        $report = Read-Report $Process
        if ($report -and $report.Phase -eq $Phase) { return $report }
        if ($report -and $report.Phase -eq 'failed') { throw ('Probe failed: ' + ($report | ConvertTo-Json -Depth 8)) }
        if ($Process.HasExited) { throw ('Probe exited before phase ' + $Phase + ': ' + $Process.ExitCode) }
        Start-Sleep -Milliseconds 50
    }
    throw ('Timed out waiting for phase ' + $Phase)
}

function Wait-Exit([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForExit(10000)) { throw ('Probe did not exit: ' + $Process.Id) }
    if ($Process.ExitCode -ne 0) { throw ('Probe exit code: ' + $Process.ExitCode) }
}

function Stop-Probe([Diagnostics.Process]$Process) {
    Set-Content -LiteralPath (Join-Path $evidence ('stop-' + $Process.Id)) -Value 'stop only this synthetic probe' -Encoding utf8
    Wait-Exit $Process
}

function Assert-Probe([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

try {
    # A second launch restores the same minimized WPF window and exits.
    $restoreScope = [Guid]::NewGuid().ToString('N')
    $first = Start-Probe $restoreScope 0 $true
    $ready = Wait-Phase $first 'window-ready'
    Assert-Probe ($ready.Role -eq 'primary' -and $ready.WindowState -eq 'Minimized') 'First probe was not minimized primary.'
    $second = Start-Probe $restoreScope
    Wait-Exit $second
    $forwarded = Read-Report $second
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do { $restored = Read-Report $first; if ($restored.ActivationCount -gt 0) { break }; Start-Sleep -Milliseconds 50 } while ($deadline.Elapsed.TotalSeconds -lt 5)
    Assert-Probe ($forwarded.Role -eq 'secondary' -and $forwarded.Phase -eq 'forwarded') 'Second probe created another primary.'
    Assert-Probe ($restored.ActivationCount -eq 1 -and $restored.WindowState -eq 'Normal' -and $restored.WindowVisible) 'Minimized primary was not restored.'
    $results.Add(@{test='existing-minimized-window';status='PASS';primary=$first.Id;secondary=$second.Id;foregroundGranted=$restored.Events[-1].ForegroundGranted})

    # An unrelated scope must have its own primary; production uses neither test scope.
    $other = Start-Probe ([Guid]::NewGuid().ToString('N'))
    $otherReady = Wait-Phase $other 'window-ready'
    Assert-Probe ($otherReady.Role -eq 'primary' -and -not $first.HasExited) 'Probe namespaces were not isolated.'
    $results.Add(@{test='isolated-probe-namespaces';status='PASS'})
    Stop-Probe $other

    # Reuse after a clean exit.
    Stop-Probe $first
    $restart = Start-Probe $restoreScope
    $restartReady = Wait-Phase $restart 'window-ready'
    Assert-Probe ($restartReady.Role -eq 'primary') 'Cleanly closed primary retained its instance lock.'
    $results.Add(@{test='restart-after-close';status='PASS'})

    # Abandoned mutex recovery after terminating only a process created by this script.
    $restart.Kill()
    $null = $restart.WaitForExit(5000)
    $afterCrash = Start-Probe $restoreScope
    $crashReady = Wait-Phase $afterCrash 'window-ready'
    Assert-Probe ($crashReady.Role -eq 'primary') 'Crashed primary retained its instance lock.'
    $results.Add(@{test='restart-after-crash';status='PASS'})
    Stop-Probe $afterCrash

    # Concurrent starts plus requests before the first window exists.
    $raceScope = [Guid]::NewGuid().ToString('N')
    $racing = @()
    for ($index = 0; $index -lt 6; $index++) { $racing += Start-Probe $raceScope 1800 $true }
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $reports = @($racing | ForEach-Object { Read-Report $_ } | Where-Object { $null -ne $_ })
        $primaries = @($reports | Where-Object { $_.Role -eq 'primary' -and $_.Phase -eq 'window-ready' })
        $secondaries = @($reports | Where-Object { $_.Role -eq 'secondary' -and $_.Phase -eq 'forwarded' })
        if ($primaries.Count -eq 1 -and $secondaries.Count -eq 5) { break }
        if (@($reports | Where-Object { $_.Phase -eq 'failed' }).Count -gt 0) { throw 'Concurrent probe failed.' }
        Start-Sleep -Milliseconds 50
    } while ($deadline.Elapsed.TotalSeconds -lt 20)
    Assert-Probe ($primaries.Count -eq 1 -and $secondaries.Count -eq 5) 'Concurrent launches did not elect exactly one primary.'
    $raceReport = $primaries[0]
    Assert-Probe ($raceReport.ActivationCount -eq 5 -and $raceReport.WindowState -eq 'Normal') 'Concurrent activation requests were lost.'
    Assert-Probe (@($raceReport.Events | Where-Object { $_.Event -eq 'activation' -and $_.BeforeWindowReady }).Count -gt 0) 'Startup-window queue path was not exercised.'
    Assert-Probe (@($raceReport.Events | Where-Object { $_.Event -eq 'pending-activation-applied' }).Count -eq 1) 'Pending activation was not applied after window creation.'
    foreach ($process in $racing) { if ($process.Id -eq $raceReport.ProcessId) { Stop-Probe $process } else { Wait-Exit $process } }
    $results.Add(@{test='six-concurrent-launches';status='PASS';primary=$raceReport.ProcessId;forwarded=5})
    $results.Add(@{test='activation-before-window-ready';status='PASS'})
    Assert-Probe ((Get-FileHash -LiteralPath $appAssembly -Algorithm SHA256).Hash -eq $appAssemblyHash) 'Application assembly changed during this probe.'

    @{
        status='PASS';checkedUtc=[DateTimeOffset]::UtcNow.ToString('O');os=[Environment]::OSVersion.Version.ToString()
        osBuild=($windowsVersion.CurrentBuildNumber + '.' + $windowsVersion.UBR);applicationAssemblySha256=$appAssemblyHash
        executable=[IO.Path]::GetRelativePath($root,$exe);executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        tests=$results.ToArray();captureStarted=$false;microphoneStarted=$false;productionInstanceContacted=$false
        scope='Real independent Windows processes and WPF windows; isolated random scopes; no MainWindow/library/audio access.'
        cleanWindows='NOT_RUN';differentWindowsUser='NOT_RUN (CurrentUserOnly is enforced by named-pipe API)'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'report.json') -Encoding utf8
    Write-Host ('PASS single instance: ' + $evidence)
} catch {
    @{status='FAIL';error=$_.ToString();tests=$results.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'report.json') -Encoding utf8
    throw
} finally {
    foreach ($process in $owned) {
        if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
}
