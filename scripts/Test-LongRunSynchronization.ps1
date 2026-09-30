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
    throw 'Long testing requires a successful short acoustic qualification report for this exact microphone.'
}
Write-Host 'Explicit fixed microphone + test-owned process acoustic synchronization test. Local evidence only.'
Write-Host 'No system volume/routing changes, no microphone replay, no transcript or upload. Long mode records for two hours.'
$probeArguments = @('--m2-sync-probe', $MicrophoneId, $Mode)
if ($Mode -eq 'long') { $probeArguments += $QualificationReport }
if (-not [string]::IsNullOrWhiteSpace($OutputDeviceId)) { $probeArguments += $OutputDeviceId }
& (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments (@('run','--project',
    (Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c',$Configuration,'--') + $probeArguments)
