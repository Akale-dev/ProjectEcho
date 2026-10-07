[CmdletBinding()]
param([ValidateSet('short','long','three-minute')][string]$Mode='short',[Parameter(Mandatory)][string]$EvidenceName,
 [string]$RuntimeSource='',
 [string]$AsrExecutable='H:\AI\Qwen\transcribe-077110e-ProjectEcho-20261004\vulkan\transcribe-cli.exe',
 [string]$AsrModel='H:\AI\Qwen\models\Qwen3-ASR-0.6B-Q8_0.gguf',
 [string]$AlignerExecutable='',
 [string]$AlignerModel='H:\AI\Qwen\Qwen3-ForcedAligner-0.6B-GGUF\qwen3-forced-aligner-0.6b-q8_0.gguf',
 [switch]$AlignmentVulkan,[switch]$AlignmentPython,[string]$AlignerBridgePath='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if(!$AlignerExecutable){$AlignerExecutable=Join-Path $root '.tools\qwen-aligner-cpu\bin\audiocpp_cli.exe'}
if($EvidenceName -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid evidence name'}
$output=Join-Path $root ('.local\M4\'+$EvidenceName)
$launch=$output+'-launch'
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath $launch)){throw 'Refuse existing evidence destination'}
New-Item -ItemType Directory -Path $launch -Force | Out-Null
$compiled=if($RuntimeSource){[IO.Path]::GetFullPath($RuntimeSource)}else{Join-Path $root 'tests\ProjectEcho.Tests\bin\Release\net10.0-windows'}
if(!$compiled.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Runtime source must be a project-local compiled or frozen test folder'}
$snapshot=Join-Path $root ('.local\M4\probe-runtime-'+$EvidenceName)
if(Test-Path -LiteralPath $snapshot){throw 'Refuse runtime snapshot overwrite'}
Copy-Item -LiteralPath $compiled -Destination $snapshot -Recurse
$snapshotFiles=@(Get-ChildItem -LiteralPath $compiled -File -Recurse | ForEach-Object {
 $relative=[IO.Path]::GetRelativePath($compiled,$_.FullName);$copy=Join-Path $snapshot $relative
 $sha=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
 if((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha){throw 'Snapshot copy mismatch; do not build concurrently while snapshotting'}
 @{RelativePath=$relative;Sha256=$sha}
})
@{Source=$compiled;Snapshot=$snapshot;Files=$snapshotFiles} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $launch 'runtime-snapshot.json') -Encoding utf8
$arguments=@('exec',(Join-Path $snapshot 'ProjectEcho.Tests.dll'),
 '--m4-real-qwen-probe',$Mode,$AsrExecutable,$AsrModel,$AlignerExecutable,$AlignerModel,$output)
if($AlignmentVulkan -and $AlignmentPython){throw 'Choose a single explicit alignment engine'}
if($AlignmentVulkan){$arguments+='--alignment-vulkan'}
if($AlignmentPython){if(!$AlignerBridgePath){throw 'Python alignment requires the selected local bridge path'};$arguments+=@('--alignment-python',$AlignerBridgePath)}
@{Wrapper=(Join-Path $root 'scripts\Dotnet.ps1');Arguments=$arguments} | Export-Clixml -LiteralPath (Join-Path $launch 'arguments.xml')
@'
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$config=Import-Clixml -LiteralPath (Join-Path $PSScriptRoot 'arguments.xml')
& $config.Wrapper -Arguments $config.Arguments
'@ | Set-Content -LiteralPath (Join-Path $launch 'invoke.ps1') -Encoding utf8
$psi=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
$psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
foreach($arg in @('-NoProfile','-File',(Join-Path $launch 'invoke.ps1'))){$psi.ArgumentList.Add($arg)}
$process=[Diagnostics.Process]::new();$process.StartInfo=$psi;[void]$process.Start()
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
$samples=0;$matched=0;$maxDedicated=0.0;$maxShared=0.0;$counterErrors=[Collections.Generic.List[string]]::new()
$counterFile=[IO.StreamWriter]::new((Join-Path $launch 'gpu-process-memory.jsonl'),$false,[Text.UTF8Encoding]::new($false))
try{
 while(!$process.WaitForExit(500)){
  $pidFile=Join-Path $output 'owned-pids.txt'
  if(!(Test-Path -LiteralPath $pidFile)){continue}
  $owned=@(Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | ForEach-Object {[int]$_})
  if($owned.Count -eq 0){continue}
  try{
   $counters=Get-Counter @('\GPU Process Memory(*)\Dedicated Usage','\GPU Process Memory(*)\Shared Usage') -ErrorAction Stop
   $samples++
   $hits=@($counters.CounterSamples | Where-Object {$_.InstanceName -match '^pid_(\d+)_' -and [int]$Matches[1] -in $owned})
   $matched+=$hits.Count
   $dedicated=($hits | Where-Object {$_.Path -like '*\dedicated usage'} | Measure-Object -Property CookedValue -Sum).Sum
   $shared=($hits | Where-Object {$_.Path -like '*\shared usage'} | Measure-Object -Property CookedValue -Sum).Sum
   $maxDedicated=[Math]::Max($maxDedicated,[double]$dedicated);$maxShared=[Math]::Max($maxShared,[double]$shared)
   $counterFile.WriteLine((@{Utc=[DateTimeOffset]::UtcNow.ToString('O');OwnedPids=$owned;DedicatedBytes=$dedicated;SharedBytes=$shared;Counters=@($hits | ForEach-Object {
    # Counter paths include the local computer name; retain only the actual
    # counter portion in reviewable evidence, without that identifying prefix.
    @{InstanceName=$_.InstanceName;Counter=($_.Path -replace '^\\\\[^\\]+','');CookedValue=$_.CookedValue}
   })} | ConvertTo-Json -Depth 4 -Compress));$counterFile.Flush()
  }catch{$counterErrors.Add($_.Exception.Message);Start-Sleep -Milliseconds 500}
 }
 $process.WaitForExit()
 [IO.File]::WriteAllText((Join-Path $launch 'stdout.log'),$stdout.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $launch 'stderr.log'),$stderr.GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false))
 [ordered]@{Schema='projectecho-m4-independent-gpu-counter-1';Status=if($matched -gt 0){'MEASURED'}else{'NOT_RUN'};Counter='Windows GPU Process Memory Dedicated/Shared Usage';ProcessFilter='Only exact configured executable paths started during this test, independently enumerated by probe';Polling='approximately 1 second; sampled peak, not exact driver allocation maximum';Samples=$samples;MatchedCounterSamples=$matched;PeakDedicatedBytes=$maxDedicated;PeakSharedBytes=$maxShared;Errors=$counterErrors;ProbeExitCode=$process.ExitCode;Output=$output} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $launch 'gpu-summary.json') -Encoding utf8
 Get-Content -LiteralPath (Join-Path $launch 'stdout.log') | Select-Object -Last 12
 if($process.ExitCode -ne 0){throw ('Real Qwen probe failed; retained '+$output)}
}finally{$counterFile.Dispose();$process.Dispose()}
