[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PortableDirectory,[Parameter(Mandatory=$true)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$bundle=[IO.Path]::GetFullPath($PortableDirectory,$root);$output=[IO.Path]::GetFullPath($EvidenceDirectory,$root)
if(-not $bundle.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase) -or
    -not $output.StartsWith((Join-Path $root '.local/M4')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only project generated artifacts and M4 evidence are allowed.'}
foreach($path in @($bundle,$output)){
    for($part=$path;$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'No linked test paths.'}
    }
}
if(Test-Path -LiteralPath $output){throw 'Use a new evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$runs=@()
$flags=@('--m4-qwen-smoke','--m4-without-models-smoke','--m3h-fix-pause-smoke','--m3h-ui-smoke','--m3h-meeting-asr-smoke','--m3h-preview-smoke','--m3f-recording-validation-smoke','--m3f-replay-readiness-smoke','--m3g-ui-smoke')
foreach($flag in $flags){
    $started=[DateTimeOffset]::UtcNow
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $bundle 'ProjectEcho.exe'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.WorkingDirectory=$root
    # This legacy smoke explicitly launches the developer's framework-dependent synthetic CLI.
    # Its fixture alone needs the project SDK runtime; all other UI checks invalidate runtime roots.
    $needsFixtureFramework=$flag -eq '--m3h-meeting-asr-smoke'
    $info.Environment['DOTNET_ROOT']=Join-Path $root $(if($needsFixtureFramework){'.tools/dotnet'}else{'.local/no-runtime-for-m4-ui'})
    $info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT'];$info.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $info.ArgumentList.Add($flag)
    $process=[Diagnostics.Process]::Start($info)
    try{
        if(-not $process.WaitForExit(180000)){$process.Kill($true);$process.WaitForExit();throw ('UI test timeout: '+$flag)}
        $exitCode=$process.ExitCode
    }finally{$process.Dispose()}
    $reports=@(Get-ChildItem -LiteralPath (Join-Path $root '.local/M4'),(Join-Path $root '.local/M3H'),(Join-Path $root '.local/M3Hfix'),(Join-Path $root '.local/M3F'),(Join-Path $root '.local/M3G') -Filter report.json -File -Recurse |
        Where-Object{$_.LastWriteTimeUtc -ge $started.UtcDateTime})
    $passed=@($reports|Where-Object{
        $value=Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json
        if(-not $value.PSObject.Properties['Result']){return $false}
        $result=$value.Result
        return (($result.PSObject.Properties['Status'] -and $result.Status -eq 'PASS') -or
            ($result.PSObject.Properties['Passed'] -and $result.Passed -eq $true))
    })
    $runs += [ordered]@{flag=$flag;startedUtc=$started;finishedUtc=[DateTimeOffset]::UtcNow;exitCode=$exitCode;reports=@($passed|ForEach-Object{$_.FullName});
        runtimeRootPolicy=if($needsFixtureFramework){'Project SDK only for the explicit synthetic child CLI; not evidence of SDK-free fixture execution'}else{'Invalid per-process runtime roots'};
        status=if($exitCode -eq 0 -and $passed.Count -gt 0){'PASS'}else{'FAIL'}}
    $runs | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'runs.json') -Encoding utf8
    if($exitCode -ne 0 -or $passed.Count -eq 0){throw ('UI test failed: '+$flag)}
    Write-Host ('PASS '+$flag)
}
[ordered]@{status='PASS';bundle=$bundle;os=[Environment]::OSVersion.Version.ToString();sourceMode='Actual WPF with synthetic files; no native capture/mic or model inference';
    runtimeRootPolicy='Invalid roots except one documented legacy developer synthetic CLI fixture; native Qwen and self-contained Host checked independently';runs=$runs;cleanWindows='NOT_RUN; this is target development Windows'} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
