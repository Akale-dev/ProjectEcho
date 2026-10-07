[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProbeDirectory,[ValidateRange(0,1000)][int]$ToleranceMilliseconds=250)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$probe=[IO.Path]::GetFullPath($ProbeDirectory).TrimEnd('\')
if(!$probe.StartsWith((Join-Path $root '.local\M4\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Only project-local M4 evidence is accepted'}
if(!(Test-Path -LiteralPath (Join-Path $probe 'report.json'))){throw 'The native probe must finish before final resource attribution'}
$launch=$probe+'-launch'
$nativePath=Join-Path $probe 'process-resources.jsonl';$gpuPath=Join-Path $launch 'gpu-process-memory.jsonl'
$summaryPath=Join-Path $launch 'gpu-attribution-summary.json';$filteredPath=Join-Path $launch 'gpu-attributed.jsonl'
if((Test-Path -LiteralPath $summaryPath) -or (Test-Path -LiteralPath $filteredPath)){throw 'Refuse to overwrite an attribution result'}
function Utc($value){if($value -is [DateTime]){return $value.ToUniversalTime()};if($value -is [DateTimeOffset]){return $value.UtcDateTime};return [DateTimeOffset]::Parse([string]$value).UtcDateTime}
$byPid=@{};$segments=[Collections.Generic.List[object]]::new();$nativeSamples=0
foreach($line in [IO.File]::ReadLines($nativePath)){
 if(!$line){continue};$row=$line|ConvertFrom-Json;$nativeSamples++;$procId=[int]$row.Pid;$when=Utc $row.Utc
 if(!$byPid.ContainsKey($procId)){$byPid[$procId]=[Collections.Generic.List[object]]::new()}
 $history=$byPid[$procId];$segment=if($history.Count -gt 0){$history[$history.Count-1]}else{$null}
 $reason=if(!$segment){'first observation'}elseif($when -lt $segment.Last){'clock moved backwards'}elseif(($when-$segment.Last).TotalSeconds -gt 2){'sampling gap over two seconds'}elseif([double]$row.CpuMilliseconds+1 -lt $segment.LastCpu){'CPU cumulative counter reset'}elseif($row.Executable -ne $segment.Executable){'executable changed'}else{''}
 if($reason){
  $segment=[ordered]@{SegmentId=$segments.Count;Pid=$procId;Executable=$row.Executable;First=$when;Last=$when;LastCpu=[double]$row.CpuMilliseconds;Samples=0;Reason=$reason}
  $history.Add($segment);$segments.Add($segment)
 }
 $segment.Last=$when;$segment.LastCpu=[double]$row.CpuMilliseconds;$segment.Samples++
}
$writer=[IO.StreamWriter]::new([IO.FileStream]::new($filteredPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read),[Text.UTF8Encoding]::new($false))
$rawDedicated=0.0;$rawShared=0.0;$acceptedDedicated=0.0;$acceptedShared=0.0;$rowCount=0;$acceptedHits=0;$rejectedHits=0
$peakDedicatedEvidence=$null;$peakSharedEvidence=$null;$reasons=@{}
try{
 foreach($line in [IO.File]::ReadLines($gpuPath)){
  if(!$line){continue};$row=$line|ConvertFrom-Json;$rowCount++;$when=Utc $row.Utc
  $keep=[Collections.Generic.List[object]]::new();$drop=[Collections.Generic.List[object]]::new()
  $dedicated=0.0;$shared=0.0;$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
  foreach($hit in $row.Counters){
   $metric=if($hit.Counter){[string]$hit.Counter}else{[string]$hit.Path}
   $metric=$metric -replace '^\\\\[^\\]+',''
   if(!$seen.Add([string]$hit.InstanceName+'|'+$metric)){throw 'Duplicate counter instance in one raw GPU sample'}
   $intervalMatches=@();$reason=''
   if($hit.InstanceName -notmatch '^pid_(\d+)_'){$reason='counter has no PID'}
   else{
    $procId=[int]$Matches[1]
    if(!$byPid.ContainsKey($procId)){$reason='PID absent from independent native samples'}
    else{
     $intervalMatches=@($byPid[$procId] | Where-Object {
      $when -ge $_.First.AddMilliseconds(-$ToleranceMilliseconds) -and $when -le $_.Last.AddMilliseconds($ToleranceMilliseconds)
     })
     if($intervalMatches.Count -eq 0){$reason='outside independent native PID time interval'}
     elseif($intervalMatches.Count -ne 1){$reason='ambiguous PID interval'}
    }
   }
   if($reason){$rejectedHits++;if(!$reasons.ContainsKey($reason)){$reasons[$reason]=0};$reasons[$reason]++;$drop.Add(@{InstanceName=$hit.InstanceName;Counter=$metric;CookedValue=$hit.CookedValue;Reason=$reason});continue}
   $acceptedHits++;$keep.Add(@{InstanceName=$hit.InstanceName;Counter=$metric;CookedValue=$hit.CookedValue;SegmentId=$intervalMatches[0].SegmentId;Executable=$intervalMatches[0].Executable})
   if($metric.EndsWith('\dedicated usage',[StringComparison]::OrdinalIgnoreCase)){$dedicated+=[double]$hit.CookedValue}
   elseif($metric.EndsWith('\shared usage',[StringComparison]::OrdinalIgnoreCase)){$shared+=[double]$hit.CookedValue}
   else{throw 'Unexpected GPU memory counter type'}
  }
  $rawDedicated=[Math]::Max($rawDedicated,[double]$row.DedicatedBytes);$rawShared=[Math]::Max($rawShared,[double]$row.SharedBytes)
  if($dedicated -gt $acceptedDedicated){$acceptedDedicated=$dedicated;$peakDedicatedEvidence=@{Utc=$when;Bytes=$dedicated;Counters=$keep.ToArray()}}
  if($shared -gt $acceptedShared){$acceptedShared=$shared;$peakSharedEvidence=@{Utc=$when;Bytes=$shared;Counters=$keep.ToArray()}}
  $writer.WriteLine((@{Utc=$when;DedicatedBytes=$dedicated;SharedBytes=$shared;Accepted=$keep.ToArray();Rejected=$drop.ToArray()}|ConvertTo-Json -Depth 7 -Compress))
 }
}finally{$writer.Dispose()}
[ordered]@{Schema='projectecho-m4-gpu-temporal-attribution-1';Status=if($acceptedHits -gt 0){'MEASURED_WITH_NATIVE_PID_TIME_ATTRIBUTION'}else{'NOT_RUN'};CreatedUtc=[DateTimeOffset]::UtcNow;NativeSamples=$nativeSamples;GpuSamples=$rowCount;AcceptedCounterHits=$acceptedHits;RejectedCounterHits=$rejectedHits;RejectionReasons=$reasons;ToleranceMilliseconds=$ToleranceMilliseconds;IntervalSplit='new PID, >2s native sampling gap, executable change, CPU cumulative reset, or backwards timestamp';NativeIntervals=$segments.ToArray();RawPeakDedicatedBytes=$rawDedicated;RawPeakSharedBytes=$rawShared;AttributedPeakDedicatedBytes=$acceptedDedicated;AttributedPeakSharedBytes=$acceptedShared;PeakDedicatedEvidence=$peakDedicatedEvidence;PeakSharedEvidence=$peakSharedEvidence;NativeEvidenceSha256=(Get-FileHash -LiteralPath $nativePath -Algorithm SHA256).Hash.ToLowerInvariant();GpuEvidenceSha256=(Get-FileHash -LiteralPath $gpuPath -Algorithm SHA256).Hash.ToLowerInvariant();Note='Read-only attribution refinement after the run, not a performance parameter change. Each GPU counter timestamp must fall within exactly one independently observed native PID interval, expanded only by the documented 250ms default tolerance for nominal 200ms native sampling. Full raw data is retained. Recorded peaks remain sampled lower bounds, not exact driver-allocation maxima.'} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Output $summaryPath
