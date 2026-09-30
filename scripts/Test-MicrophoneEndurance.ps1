[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$MicrophoneId,
    [ValidateSet('short','long')][string]$Mode = 'short',
    [string]$QualificationReport = '',
    [string]$OutputDeviceId = '',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Mode -eq 'long' -and [string]::IsNullOrWhiteSpace($QualificationReport)) {
    throw 'Long endurance requires the successful 40-second report for the exact microphone.'
}
Write-Host 'Explicit fixed microphone + newly created synthetic process endurance. Local evidence only.'
Write-Host '40-second short precheck or two real hours, followed by full WAV/MP3 and split MP3 EOF validation.'
Write-Host 'No system volume/routing change, microphone replay, transcript or upload. This is NOT acoustic synchronization acceptance.'
$probeArguments = @('--m3c-microphone-endurance-probe', $MicrophoneId, $Mode)
if ($Mode -eq 'long') { $probeArguments += $QualificationReport }
if (-not [string]::IsNullOrWhiteSpace($OutputDeviceId)) { $probeArguments += $OutputDeviceId }
& (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments (@('run','--project',
    (Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c',$Configuration,'--') + $probeArguments)
