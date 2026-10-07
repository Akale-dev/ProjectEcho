[CmdletBinding()]
param([string]$EvidenceName='matrix-01')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$base=Join-Path $root '.local\M4Perf\cpu';$output=Join-Path $base $EvidenceName
$runs=@(Get-Content (Join-Path $output 'runs.json') -Raw | ConvertFrom-Json);$manifest=Get-Content (Join-Path $base 'fixtures.json') -Raw | ConvertFrom-Json
[Reflection.Assembly]::LoadFrom((Join-Path $root 'artifacts\ProjectEcho-win-x64-20261004-M4-Final\ProjectEcho.Core.dll')) | Out-Null
$comparisons=[Collections.Generic.List[object]]::new()
function Compare-QwenBoundaries([string]$id,[object]$fixture,[string]$actual,[string]$expected){
 $a=@(Get-Content -LiteralPath $actual -Raw | ConvertFrom-Json);$b=@(Get-Content -LiteralPath $expected -Raw | ConvertFrom-Json)
 $wordTextIdentical=$a.Count -eq $b.Count;$differences=@();$maxStart=0L;$maxEnd=0L
 if($wordTextIdentical){for($i=0;$i -lt $a.Count;$i++){
  if($a[$i].word -cne $b[$i].word){$wordTextIdentical=$false}
  $sd=[long]$a[$i].start_sample-[long]$b[$i].start_sample;$ed=[long]$a[$i].end_sample-[long]$b[$i].end_sample
  $maxStart=[Math]::Max($maxStart,[Math]::Abs($sd));$maxEnd=[Math]::Max($maxEnd,[Math]::Abs($ed))
  if($sd -ne 0 -or $ed -ne 0){$differences+=@{Index=$i;Word=$a[$i].word;StartDifferenceFrames=$sd;EndDifferenceFrames=$ed}}
 }}
 $sentences=@();$valid=$false;$errorMessage=$null;$boundary=$null
 try{
  $mapped=[ProjectEcho.Core.Transcription.QwenAlignmentSentenceMapper]::Map($fixture.Text,$actual,[long]$fixture.FrameCount)
  $expectedMap=[ProjectEcho.Core.Transcription.QwenAlignmentSentenceMapper]::Map($fixture.Text,$expected,[long]$fixture.FrameCount)
  $sentences=@($mapped);$valid=$true
  $sentenceDifferences=@();for($i=0;$i -lt $mapped.Count;$i++){$sentenceDifferences+=@{Index=$i;Text=$fixture.Text.Substring($mapped[$i].TextStart,$mapped[$i].TextLength);StartMs=$mapped[$i].LocalStartFrame/16d;EndMs=$mapped[$i].LocalEndFrame/16d;StartDifferenceMs=($mapped[$i].LocalStartFrame-$expectedMap[$i].LocalStartFrame)/16d;EndDifferenceMs=($mapped[$i].LocalEndFrame-$expectedMap[$i].LocalEndFrame)/16d}}
  if($fixture.BoundaryReference){
   $truth=Get-Content $fixture.BoundaryReference -Raw | ConvertFrom-Json;$items=@();for($i=0;$i -lt $mapped.Count;$i++){$items+=@{Index=$i;StartErrorMs=($mapped[$i].LocalStartFrame-$truth.Labels[$i].ActiveStartFrame)/16d;EndErrorMs=($mapped[$i].LocalEndFrame-$truth.Labels[$i].ActiveEndFrame)/16d}}
   $boundary=@{Passed=($mapped.Count -eq $truth.Labels.Count -and @($items | Where-Object {[Math]::Abs($_.StartErrorMs) -gt 350 -or [Math]::Abs($_.EndErrorMs) -gt 500}).Count -eq 0);StartToleranceMs=350;EndToleranceMs=500;Items=$items}
  }
 }catch{$errorMessage=$_.Exception.ToString();$sentenceDifferences=@()}
 $comparisons.Add([ordered]@{Id=$id;FixtureId=$fixture.Id;Actual=$actual;Expected=$expected;WordCount=$a.Count;ExpectedWordCount=$b.Count;WordTextIdentical=$wordTextIdentical;RawWordBoundariesExactlyEqual=($wordTextIdentical -and $differences.Count -eq 0);ChangedWords=$differences.Count;MaxStartDifferenceMs=$maxStart/16d;MaxEndDifferenceMs=$maxEnd/16d;RawSentenceContractPassed=$valid;SentenceCount=$sentences.Count;SentenceDifferences=$sentenceDifferences;WordDifferences=$differences;IndependentBoundaryTruth=$boundary;Error=$errorMessage})
}
foreach($run in $runs | Where-Object {$_.Variant -eq 'avx2'}){
 $baseline=Join-Path $output ('generic-t6-'+$run.Fixture.Id+'.words.json')
 if(!(Test-Path $baseline)){$baseline=$run.Fixture.GenericBaselineWords}
 Compare-QwenBoundaries $run.Id $run.Fixture (Join-Path $output ($run.Id+'.words.json')) $baseline
}
$warm=$runs | Where-Object {$_.Variant -eq 'avx2-warm-session' -and $_.ExitCode -eq 0} | Select-Object -First 1
if($warm){foreach($round in 1..2){foreach($fixture in $manifest.Fixtures | Where-Object {$_.Id -like 'long02-*'}){
 $cold=$runs | Where-Object {$_.Variant -eq 'avx2' -and $_.Threads -eq $warm.Threads -and $_.Fixture.Id -eq $fixture.Id} | Select-Object -First 1
 Compare-QwenBoundaries ('warm-r'+$round+'-'+$fixture.Id) $fixture (Join-Path $output ('warm.words_r'+$round+'-'+$fixture.Id+'.json')) (Join-Path $output ($cold.Id+'.words.json'))
}}}
$coldSpeed=@();foreach($fixture in $manifest.Fixtures){
 $g=$runs | Where-Object {$_.Variant -eq 'generic' -and $_.Fixture.Id -eq $fixture.Id} | Select-Object -First 1
 $a=$runs | Where-Object {$_.Variant -eq 'avx2' -and $_.Threads -eq 6 -and $_.Fixture.Id -eq $fixture.Id} | Select-Object -First 1
 if($g -and $a){$coldSpeed+=@{Fixture=$fixture.Id;AudioSeconds=$fixture.AudioSeconds;GenericProcessMs=$g.ProcessWallMs;Avx2ProcessMs=$a.ProcessWallMs;Speedup=$g.ProcessWallMs/[double]$a.ProcessWallMs;GenericRtf=$g.ProcessWallMs/($fixture.AudioSeconds*1000);Avx2Rtf=$a.ProcessWallMs/($fixture.AudioSeconds*1000)}}
}
$threadMatrix=@($runs | Where-Object {$_.Variant -eq 'avx2' -and $_.Fixture.Id -like 'long02-*'} | Group-Object Threads | ForEach-Object {@{Threads=[int]$_.Name;RequestCount=$_.Count;SumProcessMs=($_.Group.ProcessWallMs | Measure-Object -Sum).Sum;MeanProcessMs=($_.Group.ProcessWallMs | Measure-Object -Average).Average}})
[ordered]@{Schema='m4-cpu-perf-comparison-1';SameAudioTextFrames=$true;ColdSpeed=$coldSpeed;ThreadMatrix=$threadMatrix;AllWordsPreserved=(@($comparisons | Where-Object {!$_.WordTextIdentical}).Count -eq 0);AllRawSentenceContractsPassed=(@($comparisons | Where-Object {!$_.RawSentenceContractPassed}).Count -eq 0);AllIndependentBoundaryTruthsPassed=(@($comparisons | Where-Object {$_.IndependentBoundaryTruth -and !$_.IndependentBoundaryTruth.Passed}).Count -eq 0);Comparisons=$comparisons} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'comparison.json') -Encoding utf8
$coldSpeed | Format-Table -AutoSize; $threadMatrix | ConvertTo-Json -Depth 3
