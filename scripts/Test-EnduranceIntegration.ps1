[CmdletBinding()]
param(
    [switch]$Short,
    [string]$OutputDeviceId = '',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Write-Host 'Remote-only endurance test: only a newly created low-amplitude synthetic process is recorded.'
Write-Host 'Microphone remains disabled. Default duration is two real hours; -Short runs the 26-second precheck.'
Write-Host 'This validates long recording/export only, NOT independent-device synchronization.'
$probeArguments = @('--m2-endurance-probe', $(if ($Short) { 'short' } else { 'long' }))
if (-not [string]::IsNullOrWhiteSpace($OutputDeviceId)) { $probeArguments += $OutputDeviceId }
& (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments (@('run','--project',
    (Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c',$Configuration,'--') + $probeArguments)
