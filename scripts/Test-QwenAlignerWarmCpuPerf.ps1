[CmdletBinding()]
param([string]$EvidenceName='warm-02',[ValidateRange(1,64)][int]$Threads=8,[ValidateRange(1,16)][int]$WindowCount=16,[string]$SequencePath='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot));$base=Join-Path $root '.local\M4Perf\cpu';$output=Join-Path $base $EvidenceName
if(Test-Path $output){throw 'Refuse evidence overwrite'}
New-Item -ItemType Directory $output | Out-Null
$manifest=Get-Content (Join-Path $base 'fixtures.json') -Raw | ConvertFrom-Json
$fixtures=@($manifest.Fixtures | Where-Object {$_.Id -like 'long02-*'});$requests=@()
foreach($f in $fixtures){if((Get-FileHash $f.Audio).Hash.ToLowerInvariant() -ne $f.InputSha256){throw 'Input hash changed'}}
for($index=0;$index -lt $WindowCount;$index++){$f=$fixtures[$index%$fixtures.Count];$requests+=@{id=('request-{0:D3}' -f $index);audio=$f.Audio;text=$f.Text;language=$f.Language}}
$sequence=Join-Path $output 'sequence.json'
if($SequencePath){
 $sequenceText=[IO.File]::ReadAllText([IO.Path]::GetFullPath($SequencePath,$root));$requests=@($sequenceText | ConvertFrom-Json)
 if($requests.Count -lt 1 -or $requests.Count -gt 16){throw 'Expected 1-16 fixed requests'}
 foreach($request in $requests){$matching=@($fixtures | Where-Object {$_.Audio -ceq $request.audio -and $_.Text -ceq $request.text -and $_.Language -ceq $request.language});if($matching.Count -ne 1){throw 'Sequence changed the fixed native audio/text pairing'}}
 $WindowCount=$requests.Count;[IO.File]::WriteAllText($sequence,$sequenceText,[Text.UTF8Encoding]::new($false))
}else{[IO.File]::WriteAllText($sequence,($requests | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))}
$exe=Join-Path $root '.tools\qwen-aligner-avx2-perf\bin\audiocpp_cli.exe'
$a=@('--task','align','--family','qwen3_forced_aligner','--model',$manifest.Model,'--backend','cpu','--threads',([string]$Threads),'--request-sequence',$sequence,'--words-out',(Join-Path $output 'warm.words.json'),'--batch-manifest-out',(Join-Path $output 'warm.manifest.json'),'--metrics','--log-file',(Join-Path $output 'warm.stages.log'))
$guard=$null;while(!$guard){try{$guard=[IO.FileStream]::new((Join-Path $root '.local\M4Perf\inference-benchmark.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{Start-Sleep -Milliseconds 500}}
try{
 $psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$output;$psi.Environment['PATH']=$env:SystemRoot+'\System32';$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false);foreach($v in $a){$psi.ArgumentList.Add($v)}
 $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$watch=[Diagnostics.Stopwatch]::StartNew();$started=[DateTimeOffset]::UtcNow;[void]$p.Start();$p.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 $stdout=$p.StandardOutput.ReadToEndAsync();$line=$p.StandardError.ReadLineAsync();$errors=[Collections.Generic.List[string]]::new();$markers=[Collections.Generic.List[object]]::new();$peak=0L
 function Consume([string]$value){if($null -eq $value){return};$errors.Add($value);if($value.StartsWith('projectecho-aligner-complete: ')){$id=$value.Substring('projectecho-aligner-complete: '.Length);$markers.Add([pscustomobject]@{Id=$id;ElapsedMs=$watch.ElapsedMilliseconds;ProcessStillRunning=(!$p.HasExited);WordsAlreadyExist=(Test-Path (Join-Path $output ('warm.words_'+$id+'.json')))})}}
 while(!$p.WaitForExit(20)){
  $p.Refresh();$peak=[Math]::Max($peak,$p.PeakWorkingSet64)
  while($line -and $line.IsCompleted){$value=$line.GetAwaiter().GetResult();Consume $value;if($null -eq $value){$line=$null}else{$line=$p.StandardError.ReadLineAsync()}}
  if($watch.Elapsed.TotalMinutes -gt 5){$p.Kill($true);$p.WaitForExit();throw 'Warm batch timeout'}
 }
 $p.WaitForExit();while($line){$value=$line.GetAwaiter().GetResult();Consume $value;if($null -eq $value){$line=$null}else{$line=$p.StandardError.ReadLineAsync()}}
 $watch.Stop();$out=$stdout.GetAwaiter().GetResult();[IO.File]::WriteAllText((Join-Path $output 'stdout.txt'),$out,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllLines((Join-Path $output 'stderr.txt'),$errors,[Text.UTF8Encoding]::new($false))
 $checks=@{ExitSuccess=($p.ExitCode -eq 0);CompletionCountAndFiles=($markers.Count -eq $WindowCount -and @($markers | Where-Object {!$_.WordsAlreadyExist}).Count -eq 0);FirstCompletionLive=($markers.Count -gt 0 -and $markers[0].ProcessStillRunning);ActualCpu=(@($errors | Where-Object {$_ -eq 'projectecho-aligner-backend: CPU'}).Count -gt 0);RawPredictions=(@($errors | Where-Object {$_ -like 'projectecho-alignment: raw-model-boundaries-v1;*'}).Count -eq $WindowCount)}
 [ordered]@{Schema='m4-perf-cpu-warm-batch-1';Executable=$exe;ExecutableSha256=(Get-FileHash $exe).Hash.ToLowerInvariant();Threads=$Threads;Arguments=$a;SequenceSha256=(Get-FileHash $sequence).Hash.ToLowerInvariant();StartUtc=$started.ToString('O');EndUtc=[DateTimeOffset]::UtcNow.ToString('O');ProcessWallMs=$watch.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;AudioSeconds=($WindowCount*30);CompletionMarkers=$markers;Checks=$checks} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
 $p.Dispose();if(@($checks.Values | Where-Object {!$_}).Count -gt 0){throw 'Warm batch contract failed'}
}finally{if($guard){$guard.Dispose()}}
