[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$EvidenceDirectory,[string[]]$Flags = @('--m3j-compact-layout-smoke','--m3j-transcript-editing-smoke','--m3d-transcript-version-smoke','--m3c-transcript-smoke','--m3h-fix-pause-smoke','--m3h-ui-smoke','--m3h-preview-smoke','--m3h-meeting-asr-smoke','--m3f-recording-validation-smoke','--m3f-replay-readiness-smoke','--m3e-log-smoke','--m3j-ui-smoke'))
$ErrorActionPreference = 'Stop'
$lite = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$root = Split-Path -Parent $lite
$work = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $work.StartsWith((Join-Path $lite '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep UI evidence under Lite .local.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$results = @()
foreach ($flag in $Flags) {
    $info = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden; $info.WorkingDirectory=$work
    $info.Environment['DOTNET_ROOT']=Join-Path $root '.tools/dotnet'; $info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT']
    $info.ArgumentList.Add($flag)
    $process=[Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(55000)) { $process.Kill(); $process.WaitForExit(); throw "UI timeout: $flag" }
        $results += @{flag=$flag;exitCode=$process.ExitCode}
        $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'runs.json') -Encoding utf8
        if ($process.ExitCode -ne 0) { throw "UI failed: $flag" }
        Write-Host "PASS $flag"
    } finally { $process.Dispose() }
}
