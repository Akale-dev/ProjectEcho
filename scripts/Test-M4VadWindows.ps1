[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$FixtureManifestPath,
    [Parameter(Mandatory=$true)][string]$VadExecutable,
    [Parameter(Mandatory=$true)][string]$VadModelDirectory,
    [Parameter(Mandatory=$true)][string]$QwenExecutable,
    [Parameter(Mandatory=$true)][string]$QwenModelPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $output.StartsWith($projectRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay in ProjectEcho.'}
if(Test-Path -LiteralPath $output){throw 'Use a new evidence directory.'}
New-Item -ItemType Directory -Path $output | Out-Null
$utf8=[Text.UTF8Encoding]::new($false,$true)
function Hash-File([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Save-Json([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 14),$utf8)}
function Normalize-Text([string]$Text){[regex]::Replace($Text.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant(),'[^\p{L}\p{Nd}]','')}
Add-Type -TypeDefinition @'
using System; using System.IO;
public static class M4VadFixturePcm {
 public static short[] Read(string path) {
  using var f=File.OpenRead(path);using var r=new BinaryReader(f);f.Position=12;
  while(f.Position<f.Length){string id=new string(r.ReadChars(4));int len=r.ReadInt32();long next=f.Position+len+(len&1);if(len<0||next>f.Length)throw new InvalidDataException();
   if(id=="data"){var p=new short[len/2];for(int i=0;i<p.Length;i++)p[i]=r.ReadInt16();return p;}f.Position=next;}
  throw new InvalidDataException();
 }
 public static void Write(string path,short[] p,int start,int count){
  using var f=new FileStream(path,FileMode.CreateNew);using var w=new BinaryWriter(f);
  w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));w.Write(36+count*2);w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);
  w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(System.Text.Encoding.ASCII.GetBytes("data"));w.Write(count*2);
  for(int i=start;i<start+count;i++)w.Write(p[i]);f.Flush(true);
 }
 public static int Distance(string a,string b){var p=new int[b.Length+1];var q=new int[b.Length+1];for(int j=0;j<=b.Length;j++)p[j]=j;for(int i=1;i<=a.Length;i++){q[0]=i;for(int j=1;j<=b.Length;j++)q[j]=Math.Min(Math.Min(p[j]+1,q[j-1]+1),p[j-1]+(a[i-1]==b[j-1]?0:1));var t=p;p=q;q=t;}return p[b.Length];}
}
'@
function Run-Local([string]$Executable,[string[]]$ArgumentList,[string]$Prefix){
 $s=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable));$s.UseShellExecute=$false;$s.CreateNoWindow=$true;$s.RedirectStandardOutput=$true;$s.RedirectStandardError=$true;$s.StandardOutputEncoding=$utf8;$s.StandardErrorEncoding=$utf8
 foreach($a in $ArgumentList){[void]$s.ArgumentList.Add($a)};$p=[Diagnostics.Process]::new();$p.StartInfo=$s;$clock=[Diagnostics.Stopwatch]::StartNew();[void]$p.Start()
 $out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();$peak=0L
 while(-not $p.WaitForExit(50)){$p.Refresh();$peak=[Math]::Max($peak,$p.WorkingSet64);if($clock.Elapsed.TotalMinutes -gt 10){$p.Kill($true);throw 'Native VAD/window probe timeout.'}}
 [IO.File]::WriteAllText($Prefix+'.stdout.txt',$out.GetAwaiter().GetResult(),$utf8);[IO.File]::WriteAllText($Prefix+'.stderr.txt',$err.GetAwaiter().GetResult(),$utf8)
 $result=[ordered]@{ExitCode=$p.ExitCode;ElapsedMs=$clock.ElapsedMilliseconds;ObservedPeakWorkingSetBytes=$peak;Arguments=$ArgumentList;Stdout=$Prefix+'.stdout.txt';Stderr=$Prefix+'.stderr.txt'}
 Save-Json ($Prefix+'.run.json') $result;$p.Dispose();if($result.ExitCode -ne 0){throw 'Native VAD/window CLI failed; diagnostics retained.'};return $result
}
$manifest=Get-Content -LiteralPath $FixtureManifestPath -Raw -Encoding utf8|ConvertFrom-Json
$fixtures=[Collections.Generic.List[object]]::new()
foreach($item in $manifest.Fixtures){if((Hash-File $item.WavePath) -ne $item.Sha256){throw 'Fixture input changed.'};$fixtures.Add($item)}
# Explicitly synthetic serial fixture, not a product mixed meeting or a natural meeting-quality claim.
$parts=@($manifest.Fixtures|Where-Object{$_.Id -in @('fleurs-zh-long','sapi-finance-zh','sapi-finance-mixed')})
$samples=[Collections.Generic.List[short]]::new();foreach($part in $parts){if($samples.Count -gt 0){$samples.AddRange([short[]]::new(3200))};$samples.AddRange([M4VadFixturePcm]::Read($part.WavePath))}
$composite=Join-Path $output 'synthetic-serial-boundary-corpus.wav';$array=$samples.ToArray();[M4VadFixturePcm]::Write($composite,$array,0,$array.Length)
$fixtures.Add([pscustomobject]@{Id='synthetic-serial-boundary-corpus';WavePath=$composite;Reference=($parts.Reference -join ' ');Frames=$array.Length;Sha256=(Hash-File $composite);Kind='synthetic-serial-public-and-sapi'})
$results=[Collections.Generic.List[object]]::new();$allWindows=[Collections.Generic.List[object]]::new()
foreach($fixture in $fixtures){
 Write-Host ('Native Silero / full-coverage cuts: '+$fixture.Id)
 $prefix=Join-Path $output $fixture.Id;$segmentsPath=$prefix+'-segments.json';$chunksPath=$prefix+'-speech-only-candidate.json'
 $run=Run-Local $VadExecutable @('--task','vad','--family','silero_vad','--model',[IO.Path]::GetFullPath($VadModelDirectory),'--backend','cpu','--threads','2','--audio',$fixture.WavePath,'--segments-out',$segmentsPath,'--vad-chunks-out',$chunksPath,'--vad-chunk-max-seconds','30','--vad-chunk-merge-gap-seconds','0.5','--vad-chunk-padding-seconds','0.25','--metrics') $prefix
 if(Test-Path -LiteralPath $segmentsPath){$segments=@(Get-Content -LiteralPath $segmentsPath -Raw|ConvertFrom-Json)}
 elseif((Test-Path -LiteralPath $chunksPath) -and (Get-Content -LiteralPath $chunksPath -Raw).Trim() -eq '[]'){$segments=@()}
 else{throw 'VAD returned no segment evidence and no explicit empty chunk output.'}
 $total=[int]$fixture.Frames;$pcm=[M4VadFixturePcm]::Read($fixture.WavePath)
 if($pcm.Length -ne $total){throw 'Fixture frame count differs.'}
 $previous=0;$speechFrames=0L;$gaps=[Collections.Generic.List[object]]::new()
 foreach($segment in $segments){if($segment.start_sample -lt $previous -or $segment.end_sample -le $segment.start_sample -or $segment.end_sample -gt $total){throw 'Invalid VAD frame bounds.'};if($segment.start_sample -gt $previous){$gaps.Add(@{Start=$previous;End=[int]$segment.start_sample})};$speechFrames+=$segment.end_sample-$segment.start_sample;$previous=[int]$segment.end_sample}
 if($previous -lt $total){$gaps.Add(@{Start=$previous;End=$total})}
 $windowCount=[int][Math]::Ceiling($total/480000.0)
 foreach($strategy in @('balanced','silero-gap-cuts-full-coverage')){
  $cuts=[Collections.Generic.List[int]]::new();$cuts.Add(0)
  for($i=1;$i -lt $windowCount;$i++){
   $target=[int][Math]::Ceiling($total*$i/[double]$windowCount);$lower=[Math]::Max($cuts[$cuts.Count-1]+160000,$total-($windowCount-$i)*480000)
   $upper=[Math]::Min($cuts[$cuts.Count-1]+480000,$total-($windowCount-$i)*160000);$cut=[int][Math]::Clamp($target,$lower,$upper)
   if($strategy -ne 'balanced'){
    $candidates=@(foreach($gap in $gaps){if($gap.End-$gap.Start -ge 800){$middle=[int](($gap.Start+$gap.End)/2);if($middle -ge $lower -and $middle -le $upper){[pscustomobject]@{Frame=$middle;Distance=[Math]::Abs($middle-$target)}}}})
    if($candidates.Count -gt 0){$cut=($candidates|Sort-Object Distance|Select-Object -First 1).Frame}
   }
   $cuts.Add($cut)
  }
  $cuts.Add($total);$owned=0L;$windows=[Collections.Generic.List[object]]::new()
  for($i=0;$i -lt $cuts.Count-1;$i++){
   $start=$cuts[$i];$count=$cuts[$i+1]-$start;if($count -le 0 -or $count -gt 480000){throw 'Invalid full-coverage cut.'}
   $path=Join-Path $output ($fixture.Id+'-'+$strategy+'-'+$i+'.wav');[M4VadFixturePcm]::Write($path,$pcm,$start,$count)
   $readback=[M4VadFixturePcm]::Read($path);for($j=0;$j -lt $count;$j++){if($readback[$j] -ne $pcm[$start+$j]){throw 'Window changed source PCM.'}}
   $window=[pscustomobject]@{Fixture=$fixture.Id;Strategy=$strategy;Index=$i;StartFrame=$start;FrameCount=$count;WavePath=$path;Sha256=(Hash-File $path)};$windows.Add($window);$allWindows.Add($window);$owned+=$count
  }
  if($owned -ne $total){throw 'Full-coverage planner omitted source frames.'}
  $results.Add([ordered]@{Fixture=$fixture.Id;Kind=$fixture.Kind;Reference=$fixture.Reference;Strategy=$strategy;TotalFrames=$total;ProcessedFrames=$owned;
   DetectedSpeechFrames=$speechFrames;SpeechOnlyCandidateWouldSkipFrames=$total-$speechFrames;VadRun=$run;Windows=@($windows)})
 }
}
Save-Json (Join-Path $output 'frozen-window-plan.json') @($results)
$batch=Join-Path $output 'window-batch.txt';[IO.File]::WriteAllLines($batch,[string[]]$allWindows.WavePath,$utf8)
$qwen=Run-Local $QwenExecutable @('--model',[IO.Path]::GetFullPath($QwenModelPath),'--backend','vulkan','--device','0','--threads','4','--n-ctx','2048','--timestamps','none','--batch',$batch,'--batch-jsonl') (Join-Path $output 'qwen-windows')
$rows=@(Get-Content -LiteralPath $qwen.Stdout -Encoding utf8|ForEach-Object{if($_.Trim().StartsWith('{')){$_|ConvertFrom-Json}})
$scores=@(foreach($result in $results){
 $texts=@(foreach($window in $result.Windows){$matching=@($rows|Where-Object{$_.PSObject.Properties.Name -contains 'file' -and $_.file -eq $window.WavePath});if($matching.Count -ne 1 -or $matching[0].PSObject.Properties.Name -contains 'error'){throw 'Window inference missing or failed.'};[string]$matching[0].text})
 $actual=$texts -join ' ';$gold=Normalize-Text $result.Reference;$normalized=Normalize-Text $actual;$distance=[M4VadFixturePcm]::Distance($gold,$normalized)
 [ordered]@{Fixture=$result.Fixture;Strategy=$result.Strategy;Kind=$result.Kind;Reference=$result.Reference;Hypothesis=$actual;WindowHypotheses=$texts;
  EditDistance=$distance;ReferenceSymbols=$gold.Length;CharacterErrorRate=if($gold.Length -gt 0){$distance/[double]$gold.Length}else{$null};
  KnownNoSpeechHallucination=($gold.Length -eq 0 -and $normalized.Length -gt 0);TotalFrames=$result.TotalFrames;ProcessedFrames=$result.ProcessedFrames;
  Windows=$result.Windows;VadRun=$result.VadRun;DetectedSpeechFrames=$result.DetectedSpeechFrames}
})
Save-Json (Join-Path $output 'vad-window-comparison.json') ([ordered]@{Schema='m4-vad-full-coverage-comparison-1';MeasuredUtc=[DateTimeOffset]::UtcNow;
 VadExecutable=[IO.Path]::GetFullPath($VadExecutable);VadExecutableSha256=(Hash-File $VadExecutable);VadModelSha256=(Hash-File (Join-Path $VadModelDirectory 'silero_vad_16k.safetensors'));
 QwenExecutableSha256=(Hash-File $QwenExecutable);QwenModelSha256=(Hash-File $QwenModelPath);QwenRun=$qwen;Scores=$scores;
 Decision='Candidate only. Silero assists cut locations while all PCM including non-speech remains processed. No VAD range is sentence alignment or proof that discarded audio is safe. No speech-only skipping is adopted. Compare errors without moving thresholds after results.';
 Captured=$false;Played=$false;Uploaded=$false;NaturalMeetingCoverage='NOT_RUN';SentenceAlignmentValidated=$false})
Write-Host (Join-Path $output 'vad-window-comparison.json')
