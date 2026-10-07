[CmdletBinding()]
param([string]$ModelPath='H:\AI\Qwen\Qwen3-ForcedAligner-0.6B-GGUF\qwen3-forced-aligner-0.6b-q8_0.gguf',
 [string]$QwenOutputs='.local\M4\runtime\vulkan-native-02\batch.stdout.jsonl',
 [string]$EvidenceName='native-final-01')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if($EvidenceName -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid evidence name'}
$output=Join-Path $root ('.local\M4\alignment\'+$EvidenceName)
if(Test-Path -LiteralPath $output){throw 'Refuse evidence overwrite'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sourceExe=Join-Path $root '.tools\qwen-aligner-cpu\bin\audiocpp_cli.exe'
$exeDir=Join-Path $output '中文程序';New-Item -ItemType Directory $exeDir | Out-Null
$exe=Join-Path $exeDir '句子定位.exe';Copy-Item $sourceExe $exe
$rows=@(Get-Content -LiteralPath (Join-Path $root $QwenOutputs) | ForEach-Object {ConvertFrom-Json $_} | Where-Object {$_.file})
$runs=[Collections.Generic.List[object]]::new()
function Invoke-Alignment([string]$Id,$Row,[int]$CancelMs=0){
 $words=Join-Path $output ($Id+'-原始词界.json')
 $arguments=@('--task','align','--family','qwen3_forced_aligner','--model',$ModelPath,'--backend','cpu','--audio',$Row.file,'--text',$Row.text,'--language','Chinese','--words-out',$words,'--threads','6','--metrics')
 $psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$output
 $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
 foreach($a in $arguments){$psi.ArgumentList.Add($a)}
 $psi.Environment['PATH']=$env:SystemRoot+'\System32'
 $process=[Diagnostics.Process]::new();$process.StartInfo=$psi;$timer=[Diagnostics.Stopwatch]::StartNew();[void]$process.Start();$process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$peak=0L;$cancelled=$false
 while(!$process.WaitForExit(100)){
  $process.Refresh();$peak=[Math]::Max($peak,$process.PeakWorkingSet64)
  if($CancelMs -gt 0 -and $timer.ElapsedMilliseconds -ge $CancelMs){$process.Kill($true);$cancelled=$true;$process.WaitForExit();break}
  if($timer.Elapsed.TotalMinutes -gt 10){$process.Kill($true);throw 'Alignment probe timed out'}
 }
 $process.WaitForExit();$timer.Stop();$out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult()
 [IO.File]::WriteAllText((Join-Path $output ($Id+'.stdout.txt')),$out,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText((Join-Path $output ($Id+'.stderr.txt')),$err,[Text.UTF8Encoding]::new($false))
 $run=[ordered]@{Id=$Id;Input=$Row.file;InputSha256=(Get-FileHash $Row.file).Hash.ToLowerInvariant();OriginalText=$Row.text;Arguments=$arguments;ExitCode=$process.ExitCode;ElapsedMs=$timer.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;Cancelled=$cancelled;Exited=$process.HasExited;RawBoundaryProtocol=($err.Contains('projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80'));WordsJson=$words;OutputCreated=(Test-Path -LiteralPath $words)}
 $runs.Add($run);$process.Dispose();$runs | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $output 'runs.json') -Encoding utf8
 Write-Output ($Id+': exit='+$run.ExitCode+' elapsed='+$run.ElapsedMs+' raw='+$run.RawBoundaryProtocol)
}
foreach($row in $rows){Invoke-Alignment ([IO.Path]::GetFileNameWithoutExtension($row.file)) $row}
Invoke-Alignment 'cancel' $rows[-1] 1500
Invoke-Alignment 'retry' $rows[-1]
$successes=@($runs | Where-Object {!$_.Cancelled})
$checks=[ordered]@{RawNativeRunsSucceeded=(@($successes | Where-Object {$_.ExitCode -ne 0 -or !$_.RawBoundaryProtocol -or !$_.OutputCreated}).Count -eq 0);CancellationExited=($runs[-2].Cancelled -and $runs[-2].Exited -and $runs[-2].ExitCode -ne 0);CancelledOutputNotPublished=(!$runs[-2].OutputCreated);RetrySucceeded=($runs[-1].ExitCode -eq 0 -and $runs[-1].OutputCreated);ChineseExecutableAndOutputPaths=$true;MinimalProcessPath=$true}
[ordered]@{Schema='projectecho-m4-native-alignment-1';Os=[Environment]::OSVersion.Version.ToString();Exe=$exe;ExeSha256=(Get-FileHash $exe).Hash.ToLowerInvariant();Model=$ModelPath;ModelSha256=(Get-FileHash $ModelPath).Hash.ToLowerInvariant();ModelBytes=(Get-Item $ModelPath).Length;Backend='CPU';TimestampQuantumMs=80;Checks=$checks;Runs=$runs;CaptureStarted=$false;MicrophoneEnabled=$false;Uploaded=$false;CleanWindowsTested=$false;QualityScope='Raw acoustic model predictions; numerical and sentence aggregation checks separate. Not a natural meeting quality claim.'} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
if(@($checks.Values | Where-Object {!$_}).Count -gt 0){throw 'Alignment probe checks failed'}
