[CmdletBinding()]
param(
 [string]$OfficialEvidence='.local/M4Perf/python/legacy-f32-01',
 [string]$ProductionEvidence='.local/M4Perf/python/production-three-minute-t6',
 [string]$OutputPath='.local/M4Perf/python/raw-sentence-comparison.json'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$core=Join-Path $root 'artifacts\ProjectEcho-win-x64-20261004-M4-Final\ProjectEcho.Core.dll'
[Reflection.Assembly]::LoadFrom($core)|Out-Null
$fixtures=(Get-Content (Join-Path $root '.local\M4Perf\cpu\fixtures.json') -Raw|ConvertFrom-Json).Fixtures
$result=[Collections.Generic.List[object]]::new()
function Compare-One([string]$Id,[object]$Fixture,[string]$Words){
 $failure=$null;$sentences=@();$truth=$null;$nativeDifferences=@()
 try{
  $mapped=[ProjectEcho.Core.Transcription.QwenAlignmentSentenceMapper]::Map($Fixture.Text,$Words,[long]$Fixture.FrameCount)
  $sentences=@($mapped|ForEach-Object{@{Text=$Fixture.Text.Substring($_.TextStart,$_.TextLength);TextStart=$_.TextStart;TextLength=$_.TextLength;StartFrame=$_.LocalStartFrame;EndFrame=$_.LocalEndFrame}})
  if(($sentences.Text -join '') -cne $Fixture.Text){throw 'The production mapper did not preserve the exact original text.'}
  if($Fixture.PSObject.Properties['GenericBaselineWords']){
   $baseline=[ProjectEcho.Core.Transcription.QwenAlignmentSentenceMapper]::Map($Fixture.Text,$Fixture.GenericBaselineWords,[long]$Fixture.FrameCount)
   if($mapped.Count -ne $baseline.Count){throw 'Sentence count differs from the same-input native baseline.'}
   for($i=0;$i -lt $mapped.Count;$i++){$nativeDifferences+=@{Index=$i;StartDifferenceMs=($mapped[$i].LocalStartFrame-$baseline[$i].LocalStartFrame)/16d;EndDifferenceMs=($mapped[$i].LocalEndFrame-$baseline[$i].LocalEndFrame)/16d}}
  }
  if($Fixture.PSObject.Properties['BoundaryReference']){
   $labels=(Get-Content $Fixture.BoundaryReference -Raw|ConvertFrom-Json).Labels
   if($labels.Count -ne $mapped.Count){throw 'Independent boundary reference sentence count differs.'}
   $items=@();for($i=0;$i -lt $mapped.Count;$i++){$items+=@{Index=$i;StartErrorMs=($mapped[$i].LocalStartFrame-$labels[$i].ActiveStartFrame)/16d;EndErrorMs=($mapped[$i].LocalEndFrame-$labels[$i].ActiveEndFrame)/16d}}
   $truth=@{Passed=(@($items|Where-Object{[Math]::Abs($_.StartErrorMs) -gt 350 -or [Math]::Abs($_.EndErrorMs) -gt 500}).Count -eq 0);StartToleranceMs=350;EndToleranceMs=500;Items=$items}
  }
 }catch{$failure=$_.Exception.ToString()}
 $result.Add([ordered]@{Id=$Id;Fixture=$Fixture.Id;RawWords=$Words;RawWordsSha256=(Get-FileHash $Words).Hash.ToLowerInvariant();
  ProductionMapperPassed=($null -eq $failure);OriginalText=$Fixture.Text;Sentences=$sentences;NativeSentenceDifferences=$nativeDifferences;
  IndependentBoundaryTruth=$truth;Error=$failure;OfficialRepairedTimesUsed=$false})
}
$official=[IO.Path]::GetFullPath($OfficialEvidence,$root)
$report=Get-Content (Join-Path $official 'report.json') -Raw|ConvertFrom-Json
foreach($run in $report.runs){$fixture=$fixtures|Where-Object{$_.Id -eq $run.id}|Select-Object -First 1;Compare-One ('official-'+$run.id+'-'+$run.repeat) $fixture (Join-Path $official $run.raw_output)}
$production=[IO.Path]::GetFullPath($ProductionEvidence,$root)
$sequence=(Get-Content (Join-Path $root '.local\M4Perf\three-minute\fixtures.json') -Raw|ConvertFrom-Json).Fixtures
foreach($round in 1..2){foreach($entry in $sequence){$fixture=$fixtures|Where-Object{$_.Id -eq $entry.SourceFixture}|Select-Object -First 1;Compare-One ('production-'+$round+'-'+$entry.Id) $fixture (Join-Path $production ('round-'+$round+'/official.words_'+$entry.Id+'.json'))}}
$allPassed=@($result|Where-Object{-not $_.ProductionMapperPassed -or ($_.IndependentBoundaryTruth -and -not $_.IndependentBoundaryTruth.Passed)}).Count -eq 0
$summary=[ordered]@{Status=if($allPassed){'PASS_RAW_SENTENCE_AND_FIXED_BOUNDARY_CONTRACT'}else{'FAIL'};CheckedUtc=[DateTimeOffset]::UtcNow;
 Core=$core;CoreSha256=(Get-FileHash $core).Hash.ToLowerInvariant();ModelInferenceExecuted=$false;Comparisons=@($result);
 Scope='Real official Python raw predictions checked by deployed production C# sentence mapper. Fixed synthetic boundary truth only; no representative natural-meeting accuracy claim. Official repair output is never used.'}
$path=[IO.Path]::GetFullPath($OutputPath,$root)
if(-not $path.StartsWith((Join-Path $root '.local\M4Perf').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path $path)){throw 'Use a new project-local comparison output.'}
$summary|ConvertTo-Json -Depth 12|Set-Content $path -Encoding utf8
Write-Host ($summary.Status+': '+$path)
if(-not $allPassed){throw 'One or more actual raw sentence/boundary checks failed.'}
