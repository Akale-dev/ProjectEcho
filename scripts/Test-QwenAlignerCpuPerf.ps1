[CmdletBinding()]
param([string]$EvidenceName='matrix-01',[switch]$SkipGeneric,[switch]$OnlyLongWindows,[ValidateRange(1,3)][int]$LongThreadRepeats=2)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if($EvidenceName -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid evidence name'}
$base=Join-Path $root '.local\M4Perf\cpu';$output=Join-Path $base $EvidenceName
if(Test-Path -LiteralPath $output){throw 'Refuse benchmark overwrite'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$manifest=Get-Content (Join-Path $base 'fixtures.json') -Raw | ConvertFrom-Json
$generic=Join-Path $root '.tools\qwen-aligner-cpu\bin\audiocpp_cli.exe'
$avx2=Join-Path $root '.tools\qwen-aligner-avx2-perf\bin\audiocpp_cli.exe'
if((Get-FileHash $generic).Hash.ToLowerInvariant() -ne 'eca956f4f15ae7c93ccc1b9cece89439233d735212f7ab35326753b96b86014f'){throw 'Old generic baseline changed'}
if((Get-FileHash $manifest.Model).Hash.ToLowerInvariant() -ne $manifest.ModelSha256){throw 'Alignment model changed'}
foreach($fixture in $manifest.Fixtures){if((Get-FileHash $fixture.Audio).Hash.ToLowerInvariant() -ne $fixture.InputSha256){throw ('Input changed: '+$fixture.Id)}}
$rows=[Collections.Generic.List[object]]::new()
$lockPath=Join-Path $root '.local\M4Perf\inference-benchmark.lock'
$guard=$null;$wait=[Diagnostics.Stopwatch]::StartNew()
while($null -eq $guard){try{$guard=[IO.FileStream]::new($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}catch [IO.IOException]{if($wait.Elapsed.TotalMinutes -gt 20){throw 'Timed out waiting for exclusive benchmark lock'};Start-Sleep -Milliseconds 500}}
try{
 $owner=[Text.Encoding]::UTF8.GetBytes('cpu-avx2-perf '+$EvidenceName+' '+[DateTimeOffset]::UtcNow.ToString('O'));$guard.SetLength(0);$guard.Write($owner);$guard.Flush()
 function Run([string]$id,[string]$exe,[string[]]$arguments,[double]$audioSeconds,[object]$fixture,[int]$threads,[string]$variant){
  $psi=[Diagnostics.ProcessStartInfo]::new($exe);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$output
  $psi.Environment['PATH']=$env:SystemRoot+'\System32';$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
  $psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
  foreach($a in $arguments){$psi.ArgumentList.Add($a)}
  $p=[Diagnostics.Process]::new();$p.StartInfo=$psi;$watch=[Diagnostics.Stopwatch]::StartNew();$utc=[DateTimeOffset]::UtcNow
  [void]$p.Start();$p.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
  $stdout=$p.StandardOutput.ReadToEndAsync();$stderr=$p.StandardError.ReadToEndAsync();$peak=0L;$peakPrivate=0L;$cpu=0d
  while(!$p.WaitForExit(50)){$p.Refresh();$peak=[Math]::Max($peak,$p.PeakWorkingSet64);$peakPrivate=[Math]::Max($peakPrivate,$p.PrivateMemorySize64);$cpu=[Math]::Max($cpu,$p.TotalProcessorTime.TotalMilliseconds);if($watch.Elapsed.TotalMinutes -gt 8){$p.Kill($true);$p.WaitForExit();throw ('Timed out: '+$id)}}
  $p.WaitForExit();$watch.Stop();$out=$stdout.GetAwaiter().GetResult();$err=$stderr.GetAwaiter().GetResult();$exit=$p.ExitCode;$p.Dispose()
  [IO.File]::WriteAllText((Join-Path $output ($id+'.stdout.txt')),$out,[Text.UTF8Encoding]::new($false));[IO.File]::WriteAllText((Join-Path $output ($id+'.stderr.txt')),$err,[Text.UTF8Encoding]::new($false))
  $result=[pscustomobject][ordered]@{Id=$id;Variant=$variant;Threads=$threads;Executable=$exe;ExecutableSha256=(Get-FileHash $exe).Hash.ToLowerInvariant();Arguments=$arguments;AudioSeconds=$audioSeconds;StartUtc=$utc.ToString('O');EndUtc=[DateTimeOffset]::UtcNow.ToString('O');ProcessWallMs=$watch.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;PeakPrivateBytes=$peakPrivate;SampledCpuMilliseconds=$cpu;ExitCode=$exit;RawDiagnosticCount=([regex]::Matches($err,'projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80')).Count;Fixture=$fixture}
  $rows.Add($result);$rows | ConvertTo-Json -Depth 9 | Set-Content (Join-Path $output 'runs.json') -Encoding utf8
  Write-Output ($id+' '+$watch.ElapsedMilliseconds+' ms exit='+$exit)
  if($exit -ne 0 -or $result.RawDiagnosticCount -eq 0){throw ('Native run failed raw contract: '+$id)}
 }
 $all=@($manifest.Fixtures);$long=@($all | Where-Object {$_.Id -like 'long02-*'})
 if($OnlyLongWindows){$all=$long}
 foreach($fixture in $all){
  foreach($variant in $(if($SkipGeneric){@('avx2')}else{@('generic','avx2')})){
   $id=$variant+'-t6-'+$fixture.Id;$words=Join-Path $output ($id+'.words.json')
   $a=@('--task','align','--family','qwen3_forced_aligner','--model',$manifest.Model,'--backend','cpu','--audio',$fixture.Audio,'--text',$fixture.Text,'--language',$fixture.Language,'--threads','6','--words-out',$words,'--metrics','--log-file',(Join-Path $output ($id+'.stages.log')))
   Run $id $(if($variant -eq 'generic'){$generic}else{$avx2}) $a $fixture.AudioSeconds $fixture 6 $variant
  }
 }
 foreach($threads in @(4,8)){foreach($fixture in $long){
  $id='avx2-t'+$threads+'-'+$fixture.Id;$words=Join-Path $output ($id+'.words.json')
  $a=@('--task','align','--family','qwen3_forced_aligner','--model',$manifest.Model,'--backend','cpu','--audio',$fixture.Audio,'--text',$fixture.Text,'--language',$fixture.Language,'--threads',([string]$threads),'--words-out',$words,'--metrics','--log-file',(Join-Path $output ($id+'.stages.log')))
  Run $id $avx2 $a $fixture.AudioSeconds $fixture $threads 'avx2'
 }}
 for($repeat=2;$repeat -le $LongThreadRepeats;$repeat++){foreach($threads in @(8,6,4)){foreach($fixture in $long){
  $id='avx2-t'+$threads+'-'+$fixture.Id+'-r'+$repeat;$words=Join-Path $output ($id+'.words.json')
  $a=@('--task','align','--family','qwen3_forced_aligner','--model',$manifest.Model,'--backend','cpu','--audio',$fixture.Audio,'--text',$fixture.Text,'--language',$fixture.Language,'--threads',([string]$threads),'--words-out',$words,'--metrics','--log-file',(Join-Path $output ($id+'.stages.log')))
  Run $id $avx2 $a $fixture.AudioSeconds $fixture $threads 'avx2'
 }}}
 $winner=$rows | Where-Object {$_.Variant -eq 'avx2' -and $_.Fixture.Id -like 'long02-*'} | Group-Object Threads | ForEach-Object{[pscustomobject]@{Threads=[int]$_.Name;SumMs=($_.Group.ProcessWallMs | Measure-Object -Sum).Sum;RequestCount=$_.Count}} | Sort-Object SumMs | Select-Object -First 1
 $requests=@();foreach($round in 1..2){foreach($fixture in $long){$requests+=@{id=('r'+$round+'-'+$fixture.Id);audio=$fixture.Audio;text=$fixture.Text;language=$fixture.Language}}}
 $sequence=Join-Path $output 'sequence.json';[IO.File]::WriteAllText($sequence,($requests | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
 $id='avx2-warm-t'+$winner.Threads
 $a=@('--task','align','--family','qwen3_forced_aligner','--model',$manifest.Model,'--backend','cpu','--threads',([string]$winner.Threads),'--request-sequence',$sequence,'--words-out',(Join-Path $output 'warm.words.json'),'--batch-manifest-out',(Join-Path $output 'warm.manifest.json'),'--metrics','--log-file',(Join-Path $output 'warm.stages.log'))
 Run $id $avx2 $a 180 $null $winner.Threads 'avx2-warm-session'
 [ordered]@{Schema='m4-perf-cpu-matrix-1';SelectedThreadsByObservedThreeWindowSum=$winner;ProcessColdDefinition='New process/model/session for every cold invocation; Windows file cache not flushed or controlled';ExclusiveBenchmarkLock=$lockPath;SameAudioSameText=$true;ModelSha256=$manifest.ModelSha256;OriginalGenericExeUnchanged=((Get-FileHash $generic).Hash.ToLowerInvariant() -eq 'eca956f4f15ae7c93ccc1b9cece89439233d735212f7ab35326753b96b86014f');TimingOnlyPendingOutputAndBoundaryComparison=$true;Runs=$rows} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
}finally{if($null -ne $guard){$guard.Dispose()}}
