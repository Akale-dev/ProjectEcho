[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$hostExe = Join-Path $projectRoot 'src\ProjectEcho.App\bin\Debug\net10.0-windows\CaptureHost\ProjectEcho.CaptureHost.exe'
if (-not (Test-Path -LiteralPath $hostExe)) { throw 'Build the solution before this non-capture IPC test.' }
$results = @()
foreach ($testCase in @('idle-stop', 'idle-heartbeat-timeout')) {
    $session = [Guid]::NewGuid().ToString('N')
    $pipeName = 'ProjectEcho-' + $session
    $pipeOptions = [IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Byte, $pipeOptions)
    $process = $null
    $reader = $null
    $writer = $null
    try {
        $info = [Diagnostics.ProcessStartInfo]::new($hostExe)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $info.WorkingDirectory = $projectRoot
        $info.Environment['DOTNET_ROOT'] = Join-Path $projectRoot '.tools\dotnet'
        $info.ArgumentList.Add($pipeName)
        $info.ArgumentList.Add($session)
        $process = [Diagnostics.Process]::Start($info)
        $pipe.WaitForConnectionAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $ready = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult() | ConvertFrom-Json
        if ($ready.Kind -ne 'ready' -or $ready.SessionId -ne $session) { throw 'Invalid ready handshake.' }
        if ($testCase -eq 'idle-stop') {
            $command = @{ ProtocolVersion = 1; SessionId = $session; RequestId = 'non-capture-test'; Command = 'stop' } | ConvertTo-Json -Compress
            $writer.WriteLine($command)
        }
        # No Start command is sent in either case. No AudioClient/capture object is created.
        if (-not $process.WaitForExit(12000)) { throw 'Host did not exit after idle stop / missing heartbeat.' }
        if ($process.ExitCode -ne 0) { throw "Host exit code $($process.ExitCode)." }
        $results += [ordered]@{ test = $testCase; status = 'PASS'; captureStarted = $false; exitCode = $process.ExitCode }
        Write-Host "PASS $testCase (no capture)"
    } finally {
        if ($writer) { $writer.Dispose() }
        if ($reader) { $reader.Dispose() }
        $pipe.Dispose()
        if ($process) { $process.Dispose() }
    }
}
$report = [ordered]@{ checkedAtUtc = [DateTime]::UtcNow.ToString('o'); tests = $results; liveCaptureHeartbeat = 'NOT_RUN' }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $projectRoot '.local\diagnostics\m0-host-smoke.json') -Encoding utf8
