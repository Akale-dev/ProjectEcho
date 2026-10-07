[CmdletBinding()]
param([string]$WorkerDirectory='.local/M5C/native-worker-04-faultfix', [string]$EvidenceDirectory='.local/M5C/native-fault-tests-05')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Local([string]$p){$x=if([IO.Path]::IsPathRooted($p)){[IO.Path]::GetFullPath($p)}else{[IO.Path]::GetFullPath((Join-Path $root $p))};if(!$x.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Project-local paths required.'};for($a=$x;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse reparse test path.'}};return $x}
$out=Local $EvidenceDirectory;if(Test-Path -LiteralPath $out){throw 'Use new fault evidence directory.'};New-Item -ItemType Directory -Force $out|Out-Null
$exe=Join-Path (Local $WorkerDirectory) 'Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe'
$seg=Local '.local/M5/runtime/sherpa-onnx-pyannote-segmentation-3-0/model.onnx'
$emb=Local 'artifacts/ProjectEcho-win-x64-20261004-M5B-Final/Models/Sherpa/nemo_en_titanet_large.onnx'
$eres=Local '.local/M5/downloads/embedding.onnx'
$invalid=Join-Path $out 'invalid.onnx';[IO.File]::WriteAllBytes($invalid,[byte[]](1,2,3,4,5));$empty=Join-Path $out 'empty.onnx';[IO.File]::WriteAllBytes($empty,[byte[]]::new(0));$missing=Join-Path $out 'missing.onnx'
$wav=Join-Path $out 'synthetic-silence.wav';$stream=[IO.File]::Create($wav);$writer=[IO.BinaryWriter]::new($stream)
try{$writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write(32036);$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write(16);$writer.Write([int16]1);$writer.Write([int16]1);$writer.Write(16000);$writer.Write(32000);$writer.Write([int16]2);$writer.Write([int16]16);$writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write(32000);$writer.Write([byte[]]::new(32000))}finally{$writer.Dispose();$stream.Dispose()}
$cases=@(
 @{Id='bad-embedding-known';Seg=$seg;Emb=$invalid;Count=2;Expected=2},
 @{Id='bad-embedding-auto';Seg=$seg;Emb=$invalid;Count=-1;Expected=2},
 @{Id='empty-embedding';Seg=$seg;Emb=$empty;Count=2;Expected=2},
 @{Id='missing-embedding';Seg=$seg;Emb=$missing;Count=2;Expected=2},
 @{Id='wrong-role-embedding';Seg=$seg;Emb=$seg;Count=2;Expected=2},
 @{Id='bad-segmentation';Seg=$invalid;Emb=$emb;Count=2;Expected=2},
 @{Id='missing-segmentation';Seg=$missing;Emb=$emb;Count=-1;Expected=2},
 @{Id='silence-nemo-known';Seg=$seg;Emb=$emb;Count=2;Expected=0},
 @{Id='silence-nemo-auto';Seg=$seg;Emb=$emb;Count=-1;Expected=0},
 @{Id='silence-eres-known';Seg=$seg;Emb=$eres;Count=2;Expected=0}
)
$results=@()
foreach($c in $cases){
 $psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.Environment['PATH']=$env:SystemRoot+'\System32'
 foreach($a in @(('--segmentation.pyannote-model='+$c.Seg),('--embedding.model='+$c.Emb),('--clustering.num-clusters='+$c.Count),'--segmentation.num-threads=2','--embedding.num-threads=2',$wav)){$psi.ArgumentList.Add($a)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$clock=[Diagnostics.Stopwatch]::StartNew();$null=$p.Start();$exact=$false;try{$module=$p.MainModule;if($null -ne $module){$exact=$module.FileName -eq $exe}}catch{};$stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync();$finished=$p.WaitForExit(10000)
 if(!$finished){if(!$exact){throw 'Refuse termination of unexpected executable.'};$p.Kill($true);$p.WaitForExit()}
 $text=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult();[IO.File]::WriteAllText((Join-Path $out ($c.Id+'.stdout.txt')),$text);[IO.File]::WriteAllText((Join-Path $out ($c.Id+'.stderr.txt')),$err)
 $intervals=[regex]::Matches($text,'(?m)^\d+\.\d+ -- \d+\.\d+ speaker_').Count
 $results+=[pscustomobject]@{Id=$c.Id;NaturalExit=$finished;ExitCode=$p.ExitCode;ElapsedMs=$clock.ElapsedMilliseconds;Intervals=$intervals;Expected=$c.Expected;Pass=($finished -and $p.ExitCode -eq $c.Expected -and $intervals -eq 0 -and ($c.Expected -eq 0 -or !$text.Contains('Started')))}
 $p.Dispose()
}
@{Schema='m5c-native-fault-preflight-1';ExecutableSha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant();PrivateAudio=$false;SyntheticSilenceSha256=(Get-FileHash -LiteralPath $wav).Hash.ToLowerInvariant();Cases=$results}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $out 'summary.json') -Encoding utf8
$results|Format-Table
if(@($results|Where-Object{!$_.Pass}).Count){throw 'Fault checks failed; preserve evidence.'}

