[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$EvidenceDirectory,[string[]]$Flags = @('--m3p-timing-smoke','--m5f-timing-smoke','--m3n-playback-smoke','--m3g-time-smoke','--m3m-auto-stop-smoke','--m3l-ui-smoke','--m3k-timestamp-input-smoke','--m3k-data-root-smoke','--m3j-compact-layout-smoke','--m3j-transcript-editing-smoke','--m3d-transcript-version-smoke','--m3c-transcript-smoke','--m3h-fix-pause-smoke','--m3h-ui-smoke','--m3h-preview-smoke','--m3h-meeting-asr-smoke','--m3f-recording-validation-smoke','--m3f-replay-readiness-smoke','--m3e-log-smoke','--m3j-ui-smoke','--m5b-ui-smoke','--m5-speaker-smoke','--m4-qwen-smoke','--m4-without-models-smoke','--m3g-ui-smoke'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

$work = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $work.StartsWith((Join-Path $root '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep UI evidence under ProjectEcho .local.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$results = @()
$failed = @()
foreach ($flag in $Flags) {
    $info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden; $info.WorkingDirectory=$work
    $info.Environment['DOTNET_ROOT']=Join-Path $root '.tools/dotnet'; $info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT']
    $info.ArgumentList.Add($flag)
    $process=[Diagnostics.Process]::Start($info)
    try {
        $timedOut = -not $process.WaitForExit(55000)
        if ($timedOut) { $process.Kill(); $process.WaitForExit() }
        $results += @{flag=$flag;exitCode=$process.ExitCode;timeout=$timedOut}
        $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'runs.json') -Encoding utf8
        if ($timedOut -or $process.ExitCode -ne 0) { $failed += $flag; Write-Host "FAIL $flag timeout=$timedOut" }
        else { Write-Host "PASS $flag" }
    } finally { $process.Dispose() }
}

if ($failed.Count -gt 0) { throw ("UI failures (all entries attempted): " + ($failed -join ", ")) }
