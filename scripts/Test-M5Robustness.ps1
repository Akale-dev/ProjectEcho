[CmdletBinding()]
param([string]$OutputDirectory='.local/M5/robustness-01')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(!$output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Project output required.'}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$bundle=Join-Path $root '.local/M5/sherpa-bundle';$exe=Join-Path $bundle 'Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe'
$seg=Join-Path $bundle 'Models/Sherpa/pyannote-segmentation-3.0.onnx';$emb=Join-Path $bundle 'Models/Sherpa/3dspeaker-eres2net.onnx'
$source=Join-Path $root '.local/M5/downloads/four-speakers-zh.wav';$utf8=[Text.UTF8Encoding]::new($false)
function Save($name,$value){[IO.File]::WriteAllText((Join-Path $output $name),($value|ConvertTo-Json -Depth 14),$utf8)}
function Hash($path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
Add-Type -TypeDefinition @'
using System;using System.IO;using System.Text;
public static class M5Robust {
 public static short[] Read(string p){using var r=new BinaryReader(File.OpenRead(p));r.BaseStream.Position=12;while(r.BaseStream.Position<r.BaseStream.Length){string s=new string(r.ReadChars(4));int n=r.ReadInt32();if(s=="data"){var a=new short[n/2];for(int i=0;i<a.Length;i++)a[i]=r.ReadInt16();return a;}r.BaseStream.Position+=n+(n&1);}throw new Exception("data");}
 public static void Write(string p,short[] a){using var f=new FileStream(p,FileMode.CreateNew);using var w=new BinaryWriter(f);w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write(36+a.Length*2);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write((short)1);w.Write((short)1);w.Write(16000);w.Write(32000);w.Write((short)2);w.Write((short)16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write(a.Length*2);foreach(short x in a)w.Write(x);}
 public static void Place(short[] target,short[] source,double start,double length,double at,double gain){int s=(int)(start*16000),n=(int)(length*16000),o=(int)(at*16000);for(int i=0;i<n;i++)target[o+i]=(short)Math.Clamp(target[o+i]+Math.Round(source[s+i]*gain),-32768,32767);}
 public static short[] Transform(short[] input,string kind){var output=new short[input.Length];double low=0,high=0,last=0;var rng=new Random(505);double energy=0;foreach(short x in input)energy+=(double)x*x;double sigma=Math.Sqrt(energy/input.Length)/Math.Pow(10,10.0/20);for(int i=0;i<input.Length;i++){double x=input[i];if(kind=="telephone"){high=0.89*(high+x-last);last=x;low+=0.70*(high-low);if(i%2==0)output[i]=(short)(Math.Clamp(Math.Round(low/256)*256,-32768,32767));else output[i]=output[i-1];continue;}if(kind=="noise")x+=(rng.NextDouble()*2-1)*Math.Sqrt(3)*sigma;if(kind=="echo"&&i>=1920)x+=input[i-1920]*0.45;output[i]=(short)Math.Clamp(Math.Round(x),-32768,32767);}return output;}
}
'@
$pcm=[M5Robust]::Read($source);$base=New-Object short[] (65*16000)
$gold=@(@{Start=1.0;End=6.8;Speaker='A'},@{Start=13.0;End=16.5;Speaker='B'},@{Start=25.0;End=30.8;Speaker='A'},@{Start=37.0;End=40.0;Speaker='C'},@{Start=49.0;End=54.8;Speaker='A'})
$sourceStarts=@(0.6,7.1,0.6,13.85,0.6)
for($i=0;$i -lt 5;$i++){[M5Robust]::Place($base,$pcm,$sourceStarts[$i],$gold[$i].End-$gold[$i].Start,$gold[$i].Start,1)}
$fixtures=[Collections.Generic.List[object]]::new()
foreach($kind in @('telephone','noise','echo')){$path=Join-Path $output ($kind+'.wav');[M5Robust]::Write($path,[M5Robust]::Transform($base,$kind));$fixtures.Add(@{Id=$kind;Path=$path;Gold=$gold;Known=3;Kind='real voice with controlled signal transform'})}
$short=New-Object short[] (65*16000);[Array]::Copy($base,$short,$base.Length);[Array]::Clear($short,13*16000,4*16000);[M5Robust]::Place($short,$pcm,7.1,0.22,13,1)
$shortGold=@($gold|ForEach-Object{if($_.Speaker -eq 'B'){@{Start=13.0;End=13.22;Speaker='B'}}else{$_}})
$path=Join-Path $output 'short-interjection.wav';[M5Robust]::Write($path,$short);$fixtures.Add(@{Id='short-interjection';Path=$path;Gold=$shortGold;Known=3;Kind='constructed 220ms excerpt, not a natural complete reply'})
$overlap=New-Object short[] (65*16000);[Array]::Copy($base,$overlap,$base.Length);[M5Robust]::Place($overlap,$pcm,7.1,2.4,26,0.65)
$path=Join-Path $output 'overlap.wav';[M5Robust]::Write($path,$overlap);$fixtures.Add(@{Id='overlap';Path=$path;Gold=@($gold)+@(@{Start=26.0;End=28.4;Speaker='B'});Known=3;Kind='additive two-voice constructed overlap; no separate transcript invented'})
# Boundary WAVs are for the production adapter's <=10 second padding policy.
foreach($length in @(9.999,10.0,10.001)){$a=New-Object short[] ([int]($length*16000));[M5Robust]::Place($a,$pcm,0.6,5.8,0.5,1);[M5Robust]::Write((Join-Path $output ('boundary-'+$length.ToString([Globalization.CultureInfo]::InvariantCulture)+'.wav')),$a)}
Save 'fixtures.json' @{SourceSha256=(Hash $source);Fixtures=$fixtures;FrozenGate=@{Count=3;Split=0;Merge=0;MaximumAssignedConfusion=0.05;MaximumUnassignedClipFraction=0.2;FrozenBeforeRuns=$true};IndependentNaturalOverlapGroundTruth='NOT_RUN';SimilarTimbreGroundTruth='NOT_RUN';CrossLanguageSameSpeaker='NOT_RUN';Note='Controlled excerpts include internal pauses. Clip coverage is not speech DER; short/overlap trials are intentionally difficult and may fail.'}
$runs=[Collections.Generic.List[object]]::new()
foreach($fixture in $fixtures){foreach($known in @(0,3)){
 $id=$fixture.Id+'-'+$(if($known){'known'}else{'auto'});$psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$output;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.StandardOutputEncoding=$utf8;$psi.StandardErrorEncoding=$utf8;$psi.Environment['PATH']=$env:SystemRoot+'\System32'
 $args=@(('--segmentation.pyannote-model='+$seg),('--embedding.model='+$emb),'--segmentation.provider=cpu','--embedding.provider=cpu','--segmentation.num-threads=2','--embedding.num-threads=2','--min-duration-on=0','--min-duration-off=0','--clustering.cluster-threshold=0.9');if($known){$args+='--clustering.num-clusters=3'};$args+=$fixture.Path;foreach($arg in $args){$psi.ArgumentList.Add($arg)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$watch=[Diagnostics.Stopwatch]::StartNew();$null=$p.Start();$p.PriorityClass='BelowNormal';$outTask=$p.StandardOutput.ReadToEndAsync();$errTask=$p.StandardError.ReadToEndAsync();$peak=0L
 while(!$p.WaitForExit(100)){$p.Refresh();$peak=[Math]::Max($peak,$p.PeakWorkingSet64);if($watch.Elapsed.TotalMinutes -gt 3){$p.Kill($true);throw 'Timeout'}}
 $out=$outTask.GetAwaiter().GetResult();$err=$errTask.GetAwaiter().GetResult();[IO.File]::WriteAllText((Join-Path $output ($id+'.stdout.txt')),$out,$utf8);[IO.File]::WriteAllText((Join-Path $output ($id+'.stderr.txt')),$err,$utf8)
 $intervals=@([regex]::Matches($out,'(?m)^(\d+\.\d+) -- (\d+\.\d+) speaker_(\d+)\s*$')|ForEach-Object{@{Start=[double]::Parse($_.Groups[1].Value,[Globalization.CultureInfo]::InvariantCulture);End=[double]::Parse($_.Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture);Speaker=$_.Groups[3].Value}})
 $details=@();$identity=@{};$total=0.0;$missing=0.0;$assigned=0.0;$confused=0.0
 foreach($g in $fixture.Gold){$coverage=@{};foreach($x in $intervals){$ov=[Math]::Max(0.0,[Math]::Min([double]$g.End,[double]$x.End)-[Math]::Max([double]$g.Start,[double]$x.Start));if($ov -gt 0){if(!$coverage.ContainsKey($x.Speaker)){$coverage[$x.Speaker]=0.0};$coverage[$x.Speaker]+=$ov}}
  $winner=@($coverage.GetEnumerator()|Sort-Object Value -Descending|Select-Object -First 1);$label=$null;if($winner.Count){$label=$winner[0].Key};if(!$identity.ContainsKey($g.Speaker)){$identity[$g.Speaker]=[Collections.Generic.List[string]]::new()};if($null -ne $label){$identity[$g.Speaker].Add($label)};$details+=@{Gold=$g;Predicted=$label;Coverage=$coverage}
 }
 $map=@{};foreach($entry in $identity.GetEnumerator()){$best=@($entry.Value|Group-Object|Sort-Object Count -Descending|Select-Object -First 1);if($best.Count){$map[$entry.Key]=$best[0].Name}}
 foreach($d in $details){$duration=$d.Gold.End-$d.Gold.Start;$total+=$duration;$sum=0.0;foreach($entry in $d.Coverage.GetEnumerator()){$sum+=$entry.Value;if($entry.Key -ne $map[$d.Gold.Speaker]){$confused+=$entry.Value}};$assigned+=$sum;$missing+=[Math]::Max(0,$duration-$sum)}
 $split=@($identity.GetEnumerator()|Where-Object {@($_.Value|Select-Object -Unique).Count -gt 1}).Count;$merge=$map.Count-@($map.Values|Select-Object -Unique).Count;$count=@($intervals|ForEach-Object Speaker|Select-Object -Unique).Count
 $run=@{Id=$id;ExitCode=$p.ExitCode;ElapsedMs=$watch.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;InputSha256=(Hash $fixture.Path);DetectedCount=$count;SplitIdentities=$split;MergedIdentities=$merge;AssignedConfusionFraction=$(if($assigned){$confused/$assigned}else{1});UnassignedClipFraction=$missing/$total;ClipMajorityWrongCount=@($details|Where-Object {$null -eq $_.Predicted -or $_.Predicted -ne $map[$_.Gold.Speaker]}).Count;ClipCount=$details.Count;Intervals=$intervals;Details=$details;SpeakerMap=$map;SubtitleMisassignment='NOT_RUN-no-ASR-transcript-in-runtime-probe';Pass=($p.ExitCode -eq 0 -and $count -eq 3 -and $split -eq 0 -and $merge -eq 0 -and $missing/$total -le 0.2 -and $assigned -gt 0 -and $confused/$assigned -le 0.05)}
 $runs.Add($run);Save ($id+'.json') $run;Save 'summary.json' @{Runs=$runs;ExecutableSha256=(Hash $exe);SegmentationSha256=(Hash $seg);EmbeddingSha256=(Hash $emb);Meaning='model-estimated grouping of controlled real-voice transforms; not natural meeting or speech DER'};$p.Dispose();Write-Output ($id+': '+$run.Pass+' clusters='+$count+' split='+$split+' merge='+$merge)
}}

