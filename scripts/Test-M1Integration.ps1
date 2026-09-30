[CmdletBinding()]
param(
    [ValidateSet('Recording','Disconnect','Crash','Silence')][string]$Scenario = 'Recording',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$probe = switch ($Scenario) {
    'Recording' { '--m1-audio-probe' }
    'Disconnect' { '--m1-disconnect-probe' }
    'Crash' { '--m1-crash-probe' }
    'Silence' { '--m1-silence-probe' }
}
Write-Host "M1 synthetic Windows test: $Scenario; owned test process only, microphone OFF; no endpoint fallback."
Write-Host 'Clock/device limitations are reported separately. Original files remain in the project .local test directories.'
& (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments @('run','--project',(Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c',$Configuration,'--',$probe)
