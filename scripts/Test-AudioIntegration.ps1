[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Write-Host 'Explicit live test: two test-owned synthetic tone processes; captures target process tree only; mic OFF.'
Write-Host 'About 6 seconds of low-level test tones. No meetings, browser automation, endpoint fallback or uploads.'
Write-Host 'A nonzero exit is a real test failure; inspect .local/AudioIntegration/<run>/probe-report.json.'
& (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments @('run','--project',(Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'),
    '--no-build','--no-restore','-c','Debug','--','--synthetic-audio-probe')
