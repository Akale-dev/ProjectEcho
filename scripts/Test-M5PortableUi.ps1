[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PortableDirectory,[Parameter(Mandatory=$true)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$bundle=[IO.Path]::GetFullPath($PortableDirectory,$projectRoot)
$output=[IO.Path]::GetFullPath($EvidenceDirectory,$projectRoot)
if(-not $bundle.StartsWith((Join-Path $projectRoot 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase) -or
   -not $output.StartsWith((Join-Path $projectRoot '.local/M5')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only project artifact/evidence directories are allowed.'}
if(Test-Path -LiteralPath $output){throw 'Use a new evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$runs=@()
$flags=@('--m5-speaker-smoke','--m4-qwen-smoke','--m4-without-models-smoke','--m3h-fix-pause-smoke','--m3h-ui-smoke','--m3h-meeting-asr-smoke','--m3h-preview-smoke','--m3f-recording-validation-smoke','--m3f-replay-readiness-smoke','--m3g-ui-smoke')
if((Get-Content -LiteralPath (Join-Path $bundle 'portable.json') -Raw|ConvertFrom-Json).milestone -in @('M5B','M5C','M5D','M5Dfix')){$flags=@('--m5b-ui-smoke')+$flags}
if((Get-Content -LiteralPath (Join-Path $bundle 'portable.json') -Raw|ConvertFrom-Json).milestone -in @('M5D','M5Dfix')){$flags=@('--m5d-ui-smoke','--m3e-log-smoke')+$flags}
foreach($flag in $flags){
    $started=[DateTimeOffset]::UtcNow
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $bundle 'ProjectEcho.exe'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.WorkingDirectory=$projectRoot
    $fixtureFramework=$flag -eq '--m3h-meeting-asr-smoke'
    $info.Environment['DOTNET_ROOT']=Join-Path $projectRoot $(if($fixtureFramework){'.tools/dotnet'}else{'.local/no-runtime-for-m5-ui'})
    $info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT'];$info.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $info.ArgumentList.Add($flag)
    $process=[Diagnostics.Process]::Start($info)
    try{
        if(-not $process.WaitForExit(180000)){$process.Kill($true);$process.WaitForExit();throw ('UI timeout: '+$flag)}
        $exitCode=$process.ExitCode
    }finally{$process.Dispose()}
    $roots=@('M5D','M3E','M5B','M5','M4','M3H','M3Hfix','M3F','M3G')|ForEach-Object{Join-Path $projectRoot ('.local/'+$_)}
    $reports=@(Get-ChildItem -LiteralPath $roots -Filter report.json -File -Recurse | Where-Object{$_.LastWriteTimeUtc -ge $started.UtcDateTime})
    $passed=@($reports|Where-Object{
        $value=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
        if(-not $value.PSObject.Properties['Result']){return $false}
        $result=$value.Result
        return (($result.PSObject.Properties['Status'] -and $result.Status -eq 'PASS') -or ($result.PSObject.Properties['Passed'] -and $result.Passed -eq $true))
    })
    $runs += [ordered]@{flag=$flag;startedUtc=$started;finishedUtc=[DateTimeOffset]::UtcNow;exitCode=$exitCode;
        reports=@($passed|ForEach-Object{$_.FullName});status=if($exitCode -eq 0 -and $passed.Count -gt 0){'PASS'}else{'FAIL'};
        runtimeRootPolicy=if($fixtureFramework){'Project SDK only for explicit synthetic child CLI fixture'}else{'Invalid per-process runtime roots'}}
    $runs|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'runs.json') -Encoding utf8
    if($exitCode -ne 0 -or $passed.Count -eq 0){throw ('UI failed: '+$flag)}
    Write-Host ('PASS '+$flag)
}
[ordered]@{status='PASS';bundle=$bundle;os=[Environment]::OSVersion.Version.ToString();sourceMode='Actual WPF and synthetic files; no capture/mic; real-model UI is separate';cleanWindows='NOT_RUN; development-machine execution';runs=$runs}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
