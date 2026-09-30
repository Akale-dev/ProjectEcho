[CmdletBinding()]
param([string]$OutputName = 'whpx-capability-readonly.json')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName) { throw 'Use a file name only.' }
$evidence = Join-Path $root '.local\diagnostics\m2-20260929'
$temp = Join-Path $root '.local\temp'
New-Item -ItemType Directory -Path $evidence,$temp -Force | Out-Null
$report = [ordered]@{CheckedUtc=[DateTimeOffset]::UtcNow.ToString('o'); Scope='Read-only capability query; no feature, service, partition or VM created/modified'}
try { $report.System = Get-CimInstance Win32_ComputerSystem -Property HypervisorPresent -ErrorAction Stop | Select-Object HypervisorPresent }
catch { $report.SystemError = $_.Exception.Message }
try { $report.Features = @(Get-CimInstance Win32_OptionalFeature -Filter "Name='HypervisorPlatform' OR Name='VirtualMachinePlatform' OR Name='Microsoft-Hyper-V-All'" -ErrorAction Stop | Select-Object Name,InstallState,Status) }
catch { $report.FeaturesError = $_.Exception.Message }
$report.PlatformDllPresent = Test-Path 'C:\Windows\System32\WinHvPlatform.dll'
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
try {
    $env:TEMP = $temp; $env:TMP = $temp
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class ProjectEchoWhpxReadOnly
{
    [DllImport("WinHvPlatform.dll", ExactSpelling=true)]
    public static extern int WHvGetCapability(uint code, out ulong result, uint size, out uint written);
}
'@
    [ulong]$capability = 0; [uint]$written = 0
    $hr = [ProjectEchoWhpxReadOnly]::WHvGetCapability(0, [ref]$capability, 8, [ref]$written)
    $report.NativeHypervisorPresent = @{HRESULT=('0x{0:X8}' -f $hr); WrittenBytes=$written; Value=$capability; QuerySucceeded=($hr -eq 0); Present=($hr -eq 0 -and $capability -ne 0)}
    $hrFeatures = [ProjectEchoWhpxReadOnly]::WHvGetCapability(1, [ref]$capability, 8, [ref]$written)
    $report.NativeFeatures = @{HRESULT=('0x{0:X8}' -f $hrFeatures); WrittenBytes=$written; Value=$capability; QuerySucceeded=($hrFeatures -eq 0)}
} catch { $report.NativeError = $_.Exception.ToString() }
finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence $OutputName) -Encoding utf8
$report | ConvertTo-Json -Depth 6
