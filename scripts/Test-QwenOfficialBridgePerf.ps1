[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$PythonExecutable,
 [Parameter(Mandatory=$true)][string]$ModelDirectory,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [string]$RequestSequence='.local/M4Perf/three-minute/requests.json',
 [string]$Bridge='scripts/qwen_aligner_bridge.py',
 [ValidateRange(1,16)][int]$Threads=6,
 [ValidateRange(1,2)][int]$Rounds=2
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$output=[IO.Path]::GetFullPath($OutputDirectory,$root)
if(-not $output.StartsWith((Join-Path $root '.local\M4Perf').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be project-local evaluation evidence.'}
if(Test-Path -LiteralPath $output){throw 'Use a new evidence directory.'}
$python=[IO.Path]::GetFullPath($PythonExecutable,$root);$bridgePath=[IO.Path]::GetFullPath($Bridge,$root);$sequence=[IO.Path]::GetFullPath($RequestSequence,$root)
$requests=@(Get-Content $sequence -Raw -Encoding utf8|ConvertFrom-Json)
if($requests.Count -ne 6){throw 'This comparison requires the same six original 30-second inputs.'}
$sequenceHash=(Get-FileHash $sequence).Hash.ToLowerInvariant();$utf8=[Text.UTF8Encoding]::new($false)
$gate=[IO.FileStream]::new((Join-Path $root '.local\M4Perf\inference-benchmark.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$results=@()
try{
 [IO.Directory]::CreateDirectory($output)|Out-Null
 for($round=1;$round -le $Rounds;$round++){
  $folder=Join-Path $output ('round-'+$round);[IO.Directory]::CreateDirectory($folder)|Out-Null
  $localSequence=Join-Path $folder 'requests.json';Copy-Item -LiteralPath $sequence -Destination $localSequence
  if((Get-FileHash $localSequence).Hash.ToLowerInvariant() -ne $sequenceHash){throw 'Shared request bytes changed.'}
  $prefix=Join-Path $folder 'official.words.json';$manifest=Join-Path $folder 'official.manifest.json'
  $arguments=@('-I','-B',$bridgePath,'--task','align','--family','qwen3_forced_aligner','--model',[IO.Path]::GetFullPath($ModelDirectory,$root),
    '--backend','cpu','--threads',[string]$Threads,'--request-sequence',$localSequence,'--words-out',$prefix,'--batch-manifest-out',$manifest,'--metrics')
  $start=[Diagnostics.ProcessStartInfo]::new($python);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$folder
  $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=$utf8;$start.StandardErrorEncoding=$utf8
  foreach($argument in $arguments){[void]$start.ArgumentList.Add($argument)}
  $start.Environment['PATH']=Join-Path $env:SystemRoot 'System32';$start.Environment['TEMP']=Join-Path $root '.local\temp';$start.Environment['TMP']=$start.Environment['TEMP']
  $start.Environment['HF_HUB_OFFLINE']='1';$start.Environment['TRANSFORMERS_OFFLINE']='1';$start.Environment['HF_HUB_DISABLE_TELEMETRY']='1'
  $process=[Diagnostics.Process]::new();$process.StartInfo=$start;$watch=[Diagnostics.Stopwatch]::StartNew();$started=[DateTimeOffset]::UtcNow
  [void]$process.Start();try{$process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal}catch{}
  $stdout=$process.StandardOutput.ReadToEndAsync();$next=$process.StandardError.ReadLineAsync()
  $errors=[Collections.Generic.List[string]]::new();$markers=[Collections.Generic.List[object]]::new();$peak=0L;$privatePeak=0L
  function Consume([string]$Line){
   if($null -eq $Line){return};$errors.Add($Line)
   if($Line.StartsWith('projectecho-aligner-complete: ')){
    $id=$Line.Substring('projectecho-aligner-complete: '.Length)
    $markers.Add([pscustomobject]@{Id=$id;ElapsedMs=$watch.ElapsedMilliseconds;ProcessStillRunning=(!$process.HasExited);WordsAlreadyExist=(Test-Path (Join-Path $folder ('official.words_'+$id+'.json')))})
   }
  }
  try{
   while(-not $process.HasExited){
    if($next.IsCompleted){$line=$next.GetAwaiter().GetResult();if($null -ne $line){Consume $line;$next=$process.StandardError.ReadLineAsync()}else{break}}
    $process.Refresh();$peak=[Math]::Max($peak,$process.PeakWorkingSet64);$privatePeak=[Math]::Max($privatePeak,$process.PrivateMemorySize64)
    if($watch.Elapsed.TotalMinutes -gt 3){$process.Kill($true);throw 'The bounded three-minute-input comparison exceeded three minutes wall time.'}
    [void]$process.WaitForExit(20)
   }
   while($null -ne $next){$line=$next.GetAwaiter().GetResult();if($null -eq $line){break};Consume $line;$next=$process.StandardError.ReadLineAsync()}
   $process.WaitForExit();$out=$stdout.GetAwaiter().GetResult();$wall=$watch.ElapsedMilliseconds
   [IO.File]::WriteAllText((Join-Path $folder 'stdout.txt'),$out,$utf8);[IO.File]::WriteAllLines((Join-Path $folder 'stderr.txt'),[string[]]$errors,$utf8)
   $checks=@{ExitSuccess=($process.ExitCode -eq 0);CompleteSix=($markers.Count -eq 6);CompletionOrder=(($markers.Id -join '|') -eq ($requests.id -join '|'));
    FilesBeforeCompletion=(@($markers|Where-Object{-not $_.WordsAlreadyExist}).Count -eq 0);ActualCpu=($errors.Contains('projectecho-aligner-backend: CPU'));
    RawPredictions=(@($errors|Where-Object{$_ -eq 'projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80'}).Count -eq 6)}
   $detail=if(Test-Path $manifest){Get-Content $manifest -Raw|ConvertFrom-Json}else{$null}
   $row=[ordered]@{Round=$round;Threads=$Threads;StartedUtc=$started;FinishedUtc=[DateTimeOffset]::UtcNow;ExitCode=$process.ExitCode;
    ProcessWallMs=$wall;PeakWorkingSetBytes=$peak;PeakPrivateBytes=$privatePeak;RequestSequenceSha256=$sequenceHash;
    BridgeSha256=(Get-FileHash $bridgePath).Hash.ToLowerInvariant();PythonExecutable=$python;Arguments=$arguments;Markers=@($markers);Checks=$checks;Manifest=$detail}
   $row|ConvertTo-Json -Depth 12|Set-Content (Join-Path $folder 'summary.json') -Encoding utf8
   $results+=,$row
   Write-Host ('Official Python F32 production bridge t'+$Threads+' round'+$round+': '+$wall+'ms')
   if(@($checks.Values|Where-Object{-not $_}).Count -gt 0){throw 'Production bridge run failed; original diagnostics retained.'}
  }finally{if(-not $process.HasExited){$process.Kill($true)};$process.Dispose()}
 }
 [ordered]@{Status='PASS_EXECUTION_NOT_ACCURACY';InputSeconds=180;RequestSequenceSha256=$sequenceHash;Runs=$results;CpuOnly=$true;
  TimingScope='Fresh process including interpreter startup, imports, model load, six actual alignments, raw output fsync and diagnostics; no benchmark-only model hashing inside timed process.'}|
  ConvertTo-Json -Depth 14|Set-Content (Join-Path $output 'summary.json') -Encoding utf8
}finally{$gate.Dispose()}
