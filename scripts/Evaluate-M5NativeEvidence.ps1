[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$folder=[IO.Path]::GetFullPath((Join-Path $root $EvidenceDirectory))
if(!$folder.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Project evidence required.'}
$summary=Get-Content (Join-Path $folder 'summary.json') -Raw|ConvertFrom-Json
$results=[Collections.Generic.List[object]]::new()
foreach($run in $summary.Runs){
 $raw=Get-Content (Join-Path $folder ($run.Id+'.stdout.txt')) -Raw
 $intervals=@([regex]::Matches($raw,'(?m)^(\d+\.\d+) -- (\d+\.\d+) speaker_(\d+)\s*$')|ForEach-Object{@{Start=[double]::Parse($_.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture);End=[double]::Parse($_.Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture);Speaker=$_.Groups[3].Value}})
 $details=[Collections.Generic.List[object]]::new();$weights=@{};$identityClips=@{};$total=0.0;$missing=0.0
 foreach($old in $run.Details){$g=$old.Gold;$duration=[double]$g.End-[double]$g.Start;$coverage=@{};$union=[Collections.Generic.List[object]]::new()
  foreach($x in $intervals){$a=[Math]::Max([double]$g.Start,[double]$x.Start);$b=[Math]::Min([double]$g.End,[double]$x.End);$ov=[Math]::Max(0.0,$b-$a);if($ov -gt 0.0){if(!$coverage.ContainsKey($x.Speaker)){$coverage[$x.Speaker]=0.0};$coverage[$x.Speaker]+=$ov;$union.Add(@{Start=$a;End=$b})}}
  $best=@($coverage.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 1);$winner=$null;$dominant=0.0;if($best.Count){$winner=$best[0].Key;$dominant=$best[0].Value}
  if(!$weights.ContainsKey($g.Speaker)){$weights[$g.Speaker]=@{};$identityClips[$g.Speaker]=[Collections.Generic.List[string]]::new()};if($null -ne $winner){$identityClips[$g.Speaker].Add($winner)}
  foreach($entry in $coverage.GetEnumerator()){if(!$weights[$g.Speaker].ContainsKey($entry.Key)){$weights[$g.Speaker][$entry.Key]=0.0};$weights[$g.Speaker][$entry.Key]+=$entry.Value}
  $unionDuration=0.0;$end=-1.0;foreach($region in @($union|Sort-Object Start,End)){if($region.Start -ge $end){$unionDuration+=$region.End-$region.Start}else{$unionDuration+=[Math]::Max(0.0,$region.End-$end)};$end=[Math]::Max($end,$region.End)}
  $total+=$duration;$missing+=[Math]::Max(0.0,$duration-$unionDuration)
  $details.Add(@{Gold=$g;Predicted=$winner;MajorityClipFraction=$dominant/$duration;Coverage=$coverage;UnassignedClipFraction=[Math]::Max(0.0,$duration-$unionDuration)/$duration})
 }
 $map=@{};foreach($identity in $weights.GetEnumerator()){$best=@($identity.Value.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 1);if($best.Count){$map[$identity.Key]=$best[0].Key}}
 $assigned=0.0;$wrong=0.0;foreach($d in $details){foreach($entry in $d.Coverage.GetEnumerator()){$assigned+=$entry.Value;if($entry.Key -ne $map[$d.Gold.Speaker]){$wrong+=$entry.Value}}}
 $split=@($identityClips.GetEnumerator()|Where-Object{@($_.Value|Select-Object -Unique).Count -gt 1}).Count;$merge=$map.Count-@($map.Values|Select-Object -Unique).Count;$count=@($intervals|ForEach-Object Speaker|Select-Object -Unique).Count
 $minimum=($details|ForEach-Object MajorityClipFraction|Measure-Object -Minimum).Minimum;$wrongClips=@($details|Where-Object {$null -eq $_.Predicted -or $_.Predicted -ne $map[$_.Gold.Speaker]}).Count
 $isRobust=$run.PSObject.Properties.Name -contains 'AssignedConfusionFraction';$gate=($run.ExitCode -eq 0 -and $count -eq 3 -and $split -eq 0 -and $merge -eq 0 -and $missing/$total -le 0.2)
 if($isRobust){$gate=$gate -and $assigned -gt 0 -and $wrong/$assigned -le 0.05}else{$gate=$gate -and $minimum -ge 0.9}
 $results.Add(@{Id=$run.Id;ExitCode=$run.ExitCode;ElapsedMs=$run.ElapsedMs;PeakWorkingSetBytes=$run.PeakWorkingSetBytes;DetectedCount=$count;SplitIdentitiesByClipMajority=$split;MergedDominantIdentities=$merge;KnownIdentityWithoutAnyAssignment=$weights.Count-$map.Count;UnassignedClipFraction=$missing/$total;MajorityClipFractionMinimum=$minimum;AssignedClipLabelDisagreementFraction=$(if($assigned -gt 0.0){$wrong/$assigned}else{1});WrongMajorityClips=$wrongClips;ClipCount=$details.Count;FrozenGatePass=$gate;GroupingReturnPass=($split -eq 0 -and $merge -eq 0 -and $wrongClips -eq 0 -and $count -eq 3);SpeakerMap=$map;Details=$details;SubtitleMisassignment='NOT_RUN-runtime-only; see production pipeline evidence';ScoringNote='Interval overlap uses explicit double precision; assigned disagreement counts clip-region competing labels (including intentional overlapping clips), not DER.'})
}
$target=Join-Path $folder 'scores-corrected.json';if(Test-Path $target){throw 'Preserve prior corrected score; use a new named version rather than overwrite.'}
@{Schema='m5-native-scoring-2';Correction='Original PowerShell Math.Max(0,double) selected integer overload and rounded overlap durations. Original summary/raw retained unchanged. Recomputed exact double interval intersections. Dominant speaker map now weighted by assigned duration; overlap clips remain separately identified.';SourceSummarySha256=(Get-FileHash (Join-Path $folder 'summary.json')).Hash.ToLowerInvariant();Results=$results;CoverageMeaning='Entire placed real-audio excerpt coverage includes internal source silence. No independent voice activity truth; never interpret as DER or natural-meeting accuracy.'}|ConvertTo-Json -Depth 14|Set-Content $target
$results|Select-Object Id,DetectedCount,SplitIdentitiesByClipMajority,MergedDominantIdentities,UnassignedClipFraction,MajorityClipFractionMinimum,AssignedClipLabelDisagreementFraction,FrozenGatePass
