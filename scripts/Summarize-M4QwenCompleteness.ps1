[CmdletBinding()]
param([Parameter(Mandatory)][string]$LongProbe,[Parameter(Mandatory)][string]$ShortProbe)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
foreach($path in @($LongProbe,$ShortProbe)){
 if(![IO.Path]::GetFullPath($path).StartsWith((Join-Path $root '.local\M4\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Only project-local M4 evidence is accepted'}
}
$long=[IO.Path]::GetFullPath($LongProbe);$short=[IO.Path]::GetFullPath($ShortProbe)
$output=Join-Path $long 'completeness-comparison.json'
if(Test-Path -LiteralPath $output){throw 'Refuse completeness report overwrite'}
$longReport=Get-Content -LiteralPath (Join-Path $long 'report.json') -Raw|ConvertFrom-Json
$shortReport=Get-Content -LiteralPath (Join-Path $short 'report.json') -Raw|ConvertFrom-Json
$longRun=@($longReport.Runs|Where-Object Id -eq 'three_hour_full_inference')
$shortRun=@($shortReport.Runs|Where-Object Id -eq 'padded_ninety_second_baseline')
if($longRun.Count -ne 1 -or $shortRun.Count -ne 1 -or $longReport.Status -ne 'PASS' -or $shortReport.Status -ne 'PASS'){throw 'Both completed real probe reports must pass before this independent completeness check'}
$longRun=$longRun[0];$shortRun=$shortRun[0]
$longMap=Get-Content -LiteralPath (Join-Path $long 'long-fixture-map.json') -Raw|ConvertFrom-Json
$shortMap=Get-Content -LiteralPath (Join-Path $short 'long-fixture-map.json') -Raw|ConvertFrom-Json
if($longMap.WindowCount -ne 360 -or $shortMap.WindowCount -ne 3 -or $longMap.FramesPerWindow -ne 480000 -or $shortMap.FramesPerWindow -ne 480000){throw 'This verifier is specific to the documented 360 x 30s versus 3 x 30s fixed fixture experiment'}
function Read-Rows([string]$attempt){
 $rows=@{}
 foreach($line in [IO.File]::ReadLines((Join-Path $attempt 'asr.stdout.txt'))){
  if(!$line){continue};$row=$line|ConvertFrom-Json;if($row.type -eq 'batch_header'){continue}
  $name=[IO.Path]::GetFileName([string]$row.file)
  if($name -notmatch '^window-(\d{6})\.wav$' -or $row.PSObject.Properties.Name -contains 'error'){throw 'Unexpected or failed ASR row'}
  $index=[int]$Matches[1];if($rows.ContainsKey($index)){throw 'Duplicate ASR window'};$rows[$index]=$row
 }
 return ,$rows
}
function Pcm-Hash([string]$path){
 $file=[IO.File]::OpenRead($path);$hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
 try{
  $header=[byte[]]::new(44);$file.ReadExactly($header,0,44)
  $ascii=[Text.Encoding]::ASCII
  if($ascii.GetString($header,0,4) -ne 'RIFF' -or $ascii.GetString($header,8,8) -ne 'WAVEfmt ' -or $ascii.GetString($header,36,4) -ne 'data' -or
   [BitConverter]::ToUInt32($header,16) -ne 16 -or [BitConverter]::ToUInt16($header,20) -ne 1 -or [BitConverter]::ToUInt16($header,22) -ne 1 -or
   [BitConverter]::ToUInt32($header,24) -ne 16000 -or [BitConverter]::ToUInt16($header,34) -ne 16 -or
   [BitConverter]::ToUInt32($header,40) -ne 960000 -or $file.Length -ne 960044){throw 'Unexpected generated 30s PCM16 WAV; do not assume a header offset for other input'}
  $buffer=[byte[]]::new(65536)
  while(($count=$file.Read($buffer,0,$buffer.Length)) -gt 0){$hash.AppendData($buffer,0,$count)}
  return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
 }finally{$file.Dispose();$hash.Dispose()}
}
function Without-Whitespace([string]$text){return [Text.RegularExpressions.Regex]::Replace($text,'\s+','')}
$baselineRows=Read-Rows $shortRun.AttemptDirectory;$longRows=Read-Rows $longRun.AttemptDirectory
if($baselineRows.Count -ne 3 -or $longRows.Count -ne 360){throw 'ASR output count differs from the full fixture map'}
$baseline=@{}
foreach($window in $shortMap.Windows){
 $index=[int]$window.Index;$name='window-'+$index.ToString('D6')+'.wav'
 $baseline[$window.Fixture]=@{Index=$index;Text=[string]$baselineRows[$index].text;RawText=[string]$baselineRows[$index].raw_text;SourceSha256=$window.SourceSha256;PcmSha256=(Pcm-Hash (Join-Path $shortRun.AttemptDirectory $name))}
}
$comparisons=[Collections.Generic.List[object]]::new();$rawCombined=[Text.StringBuilder]::new()
foreach($window in $longMap.Windows){
 $index=[int]$window.Index;if(!$longRows.ContainsKey($index) -or !$baseline.ContainsKey($window.Fixture)){throw 'Missing mapped source or ASR row'}
 $expected=$baseline[$window.Fixture];$actual=$longRows[$index];$name='window-'+$index.ToString('D6')+'.wav'
 $pcm=Pcm-Hash (Join-Path $longRun.AttemptDirectory $name)
 $sentences=@($longRun.Revision.Segments|Where-Object {[Math]::Floor([double]$_.StartMs/30000) -eq $index})
 $sentenceText=$sentences.Text -join ''
 [void]$rawCombined.Append([string]$actual.text)
 $comparisons.Add([ordered]@{Index=$index;Fixture=$window.Fixture;SourceHashMatches=($window.SourceSha256 -eq $expected.SourceSha256);PcmSha256=$pcm;PcmMatchesBaseline=($pcm -eq $expected.PcmSha256);
  TextMatchesBaseline=[string]::Equals([string]$actual.text,$expected.Text,[StringComparison]::Ordinal);
  RawEnvelopeMatchesBaseline=[string]::Equals([string]$actual.raw_text,$expected.RawText,[StringComparison]::Ordinal);
  SentenceCount=$sentences.Count;RawTextEqualsSentencesIgnoringWhitespace=[string]::Equals((Without-Whitespace ([string]$actual.text)),(Without-Whitespace $sentenceText),[StringComparison]::Ordinal);
  TextDifference=if([string]::Equals([string]$actual.text,$expected.Text,[StringComparison]::Ordinal)){$null}else{@{Baseline=$expected.Text;Actual=$actual.text}}})
}
$normalized=$longRun.Revision.Segments.Text -join ''
$allText=[string]::Equals((Without-Whitespace $rawCombined.ToString()),(Without-Whitespace $normalized),[StringComparison]::Ordinal)
$differences=@($comparisons|Where-Object {!$_.SourceHashMatches -or !$_.PcmMatchesBaseline -or !$_.TextMatchesBaseline -or !$_.RawEnvelopeMatchesBaseline -or !$_.RawTextEqualsSentencesIgnoringWhitespace})
[ordered]@{Schema='projectecho-m4-long-window-completeness-1';Status=if($differences.Count -eq 0 -and $allText){'PASS_REPETITION_COMPLETENESS_NOT_ACCURACY'}else{'DIFFERENCES_RECORDED'};CheckedUtc=[DateTimeOffset]::UtcNow;
 BaselineProbe=$short;LongProbe=$long;BaselinePurpose='The same three padded source windows, real independent short native inference, not ground-truth text';WindowsCompared=$comparisons.Count;SourcePcmFramesPerWindow=480000;AllRawTextEqualsNormalizedSentencesIgnoringWhitespace=$allText;
 DifferenceCount=$differences.Count;LastWindow=$comparisons[$comparisons.Count-1];Baseline=$baseline;Comparisons=$comparisons.ToArray();
 LongAsrOutputSha256=(Get-FileHash -LiteralPath (Join-Path $longRun.AttemptDirectory 'asr.stdout.txt') -Algorithm SHA256).Hash.ToLowerInvariant();BaselineAsrOutputSha256=(Get-FileHash -LiteralPath (Join-Path $shortRun.AttemptDirectory 'asr.stdout.txt') -Algorithm SHA256).Hash.ToLowerInvariant();
 Note='Read-only completion audit after actual full-model processing. Identical PCM and repeated transcripts detect dropped tails/state contamination within this fixture. They do not establish recognition accuracy; the natural short source still has the known cesium/uranium error. All raw ASR characters/punctuation must survive sentence normalization; only whitespace is ignored for that comparison.'} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output $output
