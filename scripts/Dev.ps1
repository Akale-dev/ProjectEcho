[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Restore','Build','Test','Run','Publish')][string]$Action,
    [string]$DotnetPath = ''
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Use a Windows development session for this WPF project.' }
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'src\ProjectEcho.App\ProjectEcho.App.csproj'
$test = Join-Path $root 'tests\ProjectEcho.Tests\ProjectEcho.Tests.csproj'
$sln = Join-Path $root 'ProjectEcho.slnx'
if (-not (Test-Path -LiteralPath $sln)) { $sln = Join-Path $root 'ProjectEcho.sln' }
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $sln)) { throw 'This is a development handoff, not a finished app. Ask Codex to create the real solution and projects in M0 first.' }
function Invoke-CheckedDotnet([string[]]$Arguments) {
    & (Join-Path $PSScriptRoot 'Dotnet.ps1') -DotnetPath $DotnetPath -Arguments $Arguments
}
Push-Location $root
try {
    switch ($Action) {
        'Restore' {
            Write-Host 'Restore may contact configured NuGet sources. Run only after approving dependency access.'
            Invoke-CheckedDotnet -Arguments @('restore',$sln,'-r','win-x64','--configfile',(Join-Path $root 'NuGet.Config'))
        }
        'Build' { Invoke-CheckedDotnet -Arguments @('build',$sln,'--no-restore','-c','Debug') }
        'Test' {
            if (-not (Test-Path -LiteralPath $test)) { throw 'No actual test project exists yet. A checklist is not a passed test suite.' }
            # M0 uses a dependency-free executable with explicit assertions and a nonzero failure exit code.
            # dotnet test on a non-test SDK project can return success without executing it.
            Invoke-CheckedDotnet -Arguments @('run','--project',$test,'--no-restore','-c','Debug')
        }
        'Run' { Invoke-CheckedDotnet -Arguments @('run','--project',$app,'--no-restore','-c','Debug') }
        'Publish' {
            & (Join-Path $PSScriptRoot 'Publish-Portable.ps1') -DotnetPath $DotnetPath
        }
    }
} finally {
    Pop-Location
}
