[CmdletBinding()]
param(
    [string]$MicrophoneId = '',
    [string]$OutputDeviceId = '',
    [switch]$List,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$arguments = @('run','--project',(Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c',$Configuration,'--')
if ($List -or [string]::IsNullOrWhiteSpace($MicrophoneId)) {
    & (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments ($arguments + '--list-audio-devices')
    Write-Host 'No recording started. Pass the explicitly selected -MicrophoneId to run the short microphone test.'
} else {
    Write-Host 'Explicit real-microphone test: fixed selected device, about 20 seconds total / 10 seconds mic, recording cap 30 seconds.'
    Write-Host 'Remote audio is only a newly created synthetic process. Local evidence only; no microphone playback, transcript or upload.'
    $probeArguments = @('--m1-microphone-probe',$MicrophoneId)
    if (-not [string]::IsNullOrWhiteSpace($OutputDeviceId)) { $probeArguments += $OutputDeviceId }
    & (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments ($arguments + $probeArguments)
}
