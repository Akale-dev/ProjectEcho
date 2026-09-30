[CmdletBinding()]
param([string]$DotnetPath = '')
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run this read-only environment check on Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root '.local\diagnostics'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$build = [int]$reg.CurrentBuildNumber
$displayVersion = ''
if ($reg.PSObject.Properties.Name -contains 'DisplayVersion') { $displayVersion = [string]$reg.DisplayVersion }
$dotnet = $null
if ($DotnetPath) {
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) { throw 'The specified dotnet.exe does not exist.' }
    $dotnet = (Resolve-Path -LiteralPath $DotnetPath).Path
} elseif (Test-Path -LiteralPath (Join-Path $root '.tools\dotnet\dotnet.exe')) {
    $dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
} else {
    $cmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
$sdks = @()
if ($dotnet) { $sdks = @(& (Join-Path $PSScriptRoot 'Dotnet.ps1') -DotnetPath $dotnet -Arguments @('--list-sdks') | ForEach-Object { [string]$_ }) }
$gitVersion = 'not found'
$git = Get-Command git.exe -ErrorAction SilentlyContinue
if ($git) { $gitVersion = [string](& $git.Source --version) }
$report = [ordered]@{
    schemaVersion = 1
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    registryProductName = [string]$reg.ProductName
    windowsBuild = $build
    windowsUpdateBuildRevision = [int]$reg.UBR
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    displayVersion = $displayVersion
    is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
    is64BitProcess = [Environment]::Is64BitProcess
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    processLoopbackBuildEligible = ($build -ge 20348)
    processLoopbackActivation = 'NOT_TESTED'
    zoomDesktop = 'NOT_TESTED'
    webexDesktop = 'NOT_TESTED'
    browserCapture = 'NOT_TESTED'
    microphonePermission = 'NOT_TESTED'
    dotnetSdks = $sdks
    sdkPin = (Get-Content -LiteralPath (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    gitVersion = $gitVersion
    notes = @('Build eligibility is not API success or platform certification.', 'No recording, browser launch, installation or system settings changes are performed.', 'Keep this report local; inspect before sharing.')
}
$path = Join-Path $outDir 'environment.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
Write-Host ('Windows build: {0}; process loopback build eligibility: {1}' -f $build, ($build -ge 20348))
Write-Host ('Saved local report: {0}' -f $path)
Write-Host 'Actual capture and device permissions are NOT TESTED by this script.'
