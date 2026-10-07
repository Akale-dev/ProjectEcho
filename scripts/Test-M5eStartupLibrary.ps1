param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(-not $evidence.StartsWith((Join-Path $root '.local')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $evidence)){throw 'Use a fresh evidence folder within ProjectEcho .local.'}
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$preference=Join-Path $evidence 'settings/data-root.json'
$selected=Join-Path $evidence 'remembered-data'
$offline=Join-Path $evidence 'remembered-data-offline'
[IO.Directory]::CreateDirectory($selected) | Out-Null
$meeting=Join-Path $selected 'M1Recordings/startup-fixture'
[IO.Directory]::CreateDirectory($meeting) | Out-Null
$fixture=Join-Path $meeting 'meeting.json'
'{"schemaVersion":"m1-recording-1","isSynthetic":true,"title":"Startup library synthetic fixture","duration100ns":20000000}' | Set-Content -LiteralPath $fixture -Encoding utf8
$originalHash=(Get-FileHash -LiteralPath $fixture).Hash
function Run-Probe([string]$name,[bool]$select){
    $work=Join-Path $evidence $name
    [IO.Directory]::CreateDirectory($work) | Out-Null
    $info=[Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.WorkingDirectory=$work
    $info.Environment['DOTNET_ROOT']=Join-Path $root '.tools/dotnet'
    $info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT']
    $info.ArgumentList.Add('--m3l-startup-library-probe');$info.ArgumentList.Add($preference)
    if($select){$info.ArgumentList.Add($selected)}
    $process=[Diagnostics.Process]::Start($info)
    try{if(-not $process.WaitForExit(25000)){$process.Kill();throw 'Data-root probe timed out'};if($process.ExitCode -ne 0){throw "Probe failed: $name"}}finally{$process.Dispose()}
    return Get-Content -LiteralPath (Join-Path $work '.local/M3K-TimeFix/data-root-process.json') -Raw | ConvertFrom-Json
}
$first=Run-Probe 'choose' $true
$hash=(Get-FileHash -LiteralPath $preference).Hash
$second=Run-Probe 'restart' $false
if($first.ProcessId -eq $second.ProcessId -or $first.DataRoot -ne $selected -or $second.DataRoot -ne $selected -or -not $second.Available){throw 'Restart did not restore the same selected root.'}
# Only these two explicitly generated directories may be moved, never an arbitrary remembered path.
foreach($path in @($selected,$offline)){
    if(-not [IO.Path]::GetFullPath($path).StartsWith($evidence+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture path'}
}
Move-Item -LiteralPath $selected -Destination $offline
try{
    $missing=Run-Probe 'unavailable' $false
    if($missing.Available -or $missing.CanStart -or $missing.CanImport -or $missing.DataRoot -ne $selected -or (Test-Path -LiteralPath $selected) -or (Get-FileHash -LiteralPath $preference).Hash -ne $hash){throw 'Unavailable remembered root was replaced or recreated.'}
}finally{Move-Item -LiteralPath $offline -Destination $selected}
$restored=Run-Probe 'reconnected' $false
if(-not $first.LibraryRead -or -not $second.LibraryRead -or -not $restored.LibraryRead -or $first.MeetingCount -ne 1 -or $second.MeetingCount -ne 1 -or $restored.MeetingCount -ne 1 -or $missing.LibraryRead -or $first.SelectedMeeting -or (Get-FileHash -LiteralPath $fixture).Hash -ne $originalHash){throw 'Startup library did not refresh automatically and preserve the original metadata.'}
if(-not $restored.Available -or $restored.DataRoot -ne $selected -or (Get-FileHash -LiteralPath $preference).Hash -ne $hash){throw 'Reconnected root was not restored.'}
[ordered]@{status='PASS';differentRealProcesses=$true;noFallbackOrRecreation=$true;preferenceUnchangedAfterRead=$true;physicalDiskRemoval='NOT_RUN; generated folder renamed';runs=@($first,$second,$missing,$restored)} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'report.json')
Write-Host 'PASS startup library refreshed in separate app processes; unavailable root retained without fallback.'
