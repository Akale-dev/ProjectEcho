[CmdletBinding()]
param(
    [string]$OutputDirectory = '.local/M5/native-01',
    [switch]$LongRun,
    [switch]$GenerateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(!$output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Outputs must stay in ProjectEcho.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$utf8=[Text.UTF8Encoding]::new($false)
$exe=Join-Path $root '.local/M5/runtime/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts/bin/sherpa-onnx-offline-speaker-diarization.exe'
$seg=Join-Path $root '.local/M5/runtime/sherpa-onnx-pyannote-segmentation-3-0/model.onnx'
$emb=Join-Path $root '.local/M5/downloads/embedding.onnx'
$source=Join-Path $root '.local/M5/downloads/four-speakers-zh.wav'
function Save-Json($path,$value){[IO.File]::WriteAllText($path,($value|ConvertTo-Json -Depth 14),$utf8)}
function Hash($path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
public static class M5Fixture {
 public static short[] Read(string path) {
  using var r=new BinaryReader(File.OpenRead(path));
  if(new string(r.ReadChars(4))!="RIFF")throw new InvalidDataException("RIFF");
  r.ReadInt32(); if(new string(r.ReadChars(4))!="WAVE")throw new InvalidDataException("WAVE");
  bool fmt=false;
  while(r.BaseStream.Position<r.BaseStream.Length){string k=new string(r.ReadChars(4));int n=r.ReadInt32();long next=r.BaseStream.Position+n+(n&1);
   if(k=="fmt "){fmt=r.ReadInt16()==1 && r.ReadInt16()==1 && r.ReadInt32()==16000;}
   if(k=="data"){if(!fmt)throw new InvalidDataException("16k PCM mono");var pcm=new short[n/2];for(int i=0;i<pcm.Length;i++)pcm[i]=r.ReadInt16();return pcm;}
   r.BaseStream.Position=next;
  }throw new InvalidDataException("data");
 }
 public static void Create(string path,int seconds){using var f=new FileStream(path,FileMode.CreateNew);using var w=new BinaryWriter(f);w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+seconds*32000);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(seconds*32000);f.SetLength(44L+seconds*32000);}
 public static void Place(string path,short[] source,double sourceStart,double seconds,double targetStart,double gain){using var f=new FileStream(path,FileMode.Open,FileAccess.Write);f.Position=44+(long)Math.Round(targetStart*16000)*2;using var w=new BinaryWriter(f);int start=(int)Math.Round(sourceStart*16000),count=(int)Math.Round(seconds*16000);for(int i=0;i<count;i++)w.Write((short)Math.Clamp(Math.Round(source[start+i]*gain),short.MinValue,short.MaxValue));}
}
'@
$fixtures=[Collections.Generic.List[object]]::new()
$pcm=[M5Fixture]::Read($source)
# Controlled identities derive from repeated real source excerpts, not the model's output IDs.
# Upstream's published example labels identify the additional A2 source excerpt; it is reported separately.
$voices=@(@{Label='A';Start=0.6;Length=5.8},@{Label='B';Start=7.1;Length=3.5},@{Label='A';Start=0.6;Length=5.8},@{Label='C';Start=13.85;Length=3.0},@{Label='A';Start=0.6;Length=5.8})
$short=Join-Path $output 'aba-ca.wav';[M5Fixture]::Create($short,65)
$gold=[Collections.Generic.List[object]]::new()
for($i=0;$i -lt 5;$i++){ $v=$voices[$i];$start=1+$i*12;[M5Fixture]::Place($short,$pcm,$v.Start,$v.Length,$start,1);$gold.Add(@{Start=$start;End=$start+$v.Length;Speaker=$v.Label}) }
$fixtures.Add(@{Id='aba-ca';Path=$short;Seconds=65;Gold=$gold.ToArray();Kind='constructed-real-voice-return'})
$longGap=Join-Path $output 'long-gap.wav';[M5Fixture]::Create($longGap,1205)
$gold=[Collections.Generic.List[object]]::new()
$starts=@(1,15,601,615,1198)
for($i=0;$i -lt 5;$i++){$v=$voices[$i];[M5Fixture]::Place($longGap,$pcm,$v.Start,$v.Length,$starts[$i],$(if($i -eq 4){0.5}else{1}));$gold.Add(@{Start=$starts[$i];End=$starts[$i]+$v.Length;Speaker=$v.Label})}
$fixtures.Add(@{Id='long-gap';Path=$longGap;Seconds=1205;Gold=$gold.ToArray();Kind='constructed-real-voice-20min-return-half-volume'})
if($LongRun){
 $long=Join-Path $output 'three-hours.wav';[M5Fixture]::Create($long,10800);$gold=[Collections.Generic.List[object]]::new()
 for($i=0;$i -lt 360;$i++){$v=$voices[$i%5];$start=$i*30+1;[M5Fixture]::Place($long,$pcm,$v.Start,$v.Length,$start,1);$gold.Add(@{Start=$start;End=$start+$v.Length;Speaker=$v.Label})}
 # Explicit final speech close to EOF verifies that the actual end is processed.
 [M5Fixture]::Place($long,$pcm,7.1,3.5,10796,1);$gold.Add(@{Start=10796;End=10799.5;Speaker='B'})
 $fixtures.Add(@{Id='three-hours';Path=$long;Seconds=10800;Gold=$gold.ToArray();Kind='constructed-real-voice-resource-stress-not-natural-meeting'})
}
Save-Json (Join-Path $output 'fixtures.json') @{SourcePath=$source;SourceSha256=(Hash $source);SourceUrl='https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/0-four-speakers-zh.wav';SourceDistribution='public-upstream-test-only-not-bundled';Fixtures=$fixtures;Gate=@{AutoCount=3;MinimumMajorityPurity=0.9;MaximumUnassigned=0.2;IdentityReturn='same source speaker always same majority cluster';Threshold=0.9;FrozenBeforeEvaluation=$true}}
if($GenerateOnly){return}
$runs=[Collections.Generic.List[object]]::new()
foreach($fixture in $fixtures){foreach($known in @(0,3)){
 if($fixture.Id -eq 'three-hours' -and $known -eq 3){continue}
 $id=$fixture.Id+'-'+$(if($known){'known'}else{'auto'});$psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$output
 $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.StandardOutputEncoding=$utf8;$psi.StandardErrorEncoding=$utf8
 $psi.Environment['PATH']=$env:SystemRoot+'\System32'
 $args=@(('--segmentation.pyannote-model='+$seg),('--embedding.model='+$emb),'--segmentation.provider=cpu','--embedding.provider=cpu','--segmentation.num-threads=2','--embedding.num-threads=2','--segmentation.pyannote-window-shift-ratio=0.1','--min-duration-on=0','--min-duration-off=0','--clustering.cluster-threshold=0.9')
 if($known){$args+=('--clustering.num-clusters='+$known)};$args+=$fixture.Path
 foreach($a in $args){$psi.ArgumentList.Add($a)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$clock=[Diagnostics.Stopwatch]::StartNew();$null=$p.Start();$p.PriorityClass='BelowNormal';$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync();$peak=0L;$private=0L;$cpu=0.0
 while(!$p.WaitForExit(200)){$p.Refresh();$peak=[Math]::Max($peak,$p.PeakWorkingSet64);$private=[Math]::Max($private,$p.PrivateMemorySize64);$cpu=[Math]::Max($cpu,$p.TotalProcessorTime.TotalMilliseconds);if($clock.Elapsed.TotalMinutes -gt 50){$p.Kill($true);throw 'Native diarization exceeded 50 minute bound.'}}
 $out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult();[IO.File]::WriteAllText((Join-Path $output ($id+'.stdout.txt')),$out,$utf8);[IO.File]::WriteAllText((Join-Path $output ($id+'.stderr.txt')),$err,$utf8)
 $intervals=@([regex]::Matches($out,'(?m)^(\d+\.\d+) -- (\d+\.\d+) speaker_(\d+)\s*$') | ForEach-Object {@{Start=[double]::Parse($_.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture);End=[double]::Parse($_.Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture);Speaker=$_.Groups[3].Value}})
 $details=@();$byIdentity=@{};$total=0.0;$unassigned=0.0
 foreach($g in $fixture.Gold){$dur=$g.End-$g.Start;$total+=$dur;$coverage=@{};foreach($x in $intervals){$ov=[Math]::Max(0.0,[Math]::Min([double]$g.End,[double]$x.End)-[Math]::Max([double]$g.Start,[double]$x.Start));if($ov -gt 0){if(!$coverage.ContainsKey($x.Speaker)){$coverage[$x.Speaker]=0.0};$coverage[$x.Speaker]+=$ov}}
  $best=@($coverage.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 1);$winner=$null;$covered=0.0;if($best.Count){$winner=$best[0].Key;$covered=$best[0].Value};$unassigned+=[Math]::Max(0,$dur-($coverage.Values|Measure-Object -Sum).Sum)
  if(!$byIdentity.ContainsKey($g.Speaker)){$byIdentity[$g.Speaker]=[Collections.Generic.List[string]]::new()};if($null -ne $winner){$byIdentity[$g.Speaker].Add($winner)};$details+=@{Gold=$g;Predicted=$winner;Purity=$covered/$dur}
 }
 $split=@($byIdentity.GetEnumerator()|Where-Object {@($_.Value|Select-Object -Unique).Count -gt 1}).Count
 $majorities=@($byIdentity.GetEnumerator()|ForEach-Object {$g=@($_.Value|Group-Object|Sort-Object Count -Descending|Select-Object -First 1);if($g.Count){$g[0].Name}});$merges=$majorities.Count-@($majorities|Select-Object -Unique).Count
 $count=@($intervals|ForEach-Object Speaker|Select-Object -Unique).Count
 $minimum=($details|ForEach-Object Purity|Measure-Object -Minimum).Minimum
 $run=@{Id=$id;ExitCode=$p.ExitCode;ElapsedMs=$clock.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;PeakPrivateBytes=$private;CpuMs=$cpu;InputSha256=(Hash $fixture.Path);KnownSpeakerCount=$known;DetectedCount=$count;SplitIdentities=$split;MergedIdentities=$merges;UnassignedFraction=$unassigned/$total;MajorityPurityMinimum=$minimum;Intervals=$intervals;Details=$details;ActualDevice='CPU';GroupingPass=($p.ExitCode -eq 0 -and $count -eq 3 -and $split -eq 0 -and $merges -eq 0);Pass=($p.ExitCode -eq 0 -and $count -eq 3 -and $split -eq 0 -and $merges -eq 0 -and $unassigned/$total -le 0.2 -and $minimum -ge 0.9);CoverageMeaning='clip interval coverage includes internal source pauses, not a voice activity truth or DER'}
 $runs.Add($run);Save-Json (Join-Path $output ($id+'.json')) $run;Save-Json (Join-Path $output 'summary.json') @{Schema='m5-native-probe-1';Os=[Environment]::OSVersion.VersionString;ExecutableSha256=(Hash $exe);SegmentationSha256=(Hash $seg);EmbeddingSha256=(Hash $emb);Runs=$runs;NaturalMeetingQuality='NOT_RUN';CrossLanguageSameSpeaker='NOT_RUN'}
 $p.Dispose();Write-Output ($id+': '+$run.Pass+'; '+$run.ElapsedMs+' ms; clusters='+$count+'; splits='+$split+'; merges='+$merges)
}}

