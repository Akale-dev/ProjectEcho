[CmdletBinding()]
param([ValidateSet('Cpu','Vulkan')][string]$Backend='Vulkan',
 [string]$ModelPath='H:\AI\Qwen\models\Qwen3-ASR-0.6B-Q8_0.gguf',
 [string]$ExecutablePath='',[string]$EvidenceName='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if(!$ExecutablePath){$ExecutablePath=Join-Path $root ('.tools\qwen-'+$Backend.ToLowerInvariant()+'\bin\transcribe-cli.exe')}
if(!$EvidenceName){$EvidenceName=$Backend.ToLowerInvariant()+'-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')}
if($EvidenceName -notmatch '^[a-zA-Z0-9-]+$'){throw 'Invalid evidence name'}
$output=Join-Path $root ('.local\M4\runtime\'+$EvidenceName)
if(Test-Path -LiteralPath $output){throw 'Refuse evidence overwrite'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$fixtures=Join-Path $root '.local\M4\runtime\合成样本'
if(!(Test-Path -LiteralPath (Join-Path $fixtures 'fixtures.json'))){
 New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
 $voice=New-Object -ComObject SAPI.SpVoice
 try{
  $voices=$voice.GetVoices();$zh=$null;$en=$null
  for($i=0;$i -lt $voices.Count;$i++){$v=$voices.Item($i);if($v.GetAttribute('Language') -eq '804'){$zh=$v};if($v.GetAttribute('Language') -eq '409'){$en=$v}}
  if(!$zh -or !$en){throw 'Installed Mandarin and English voices required; no voice is automatically downloaded'}
  $definitions=@(
   @{Id='mandarin';Parts=@(@{Language='zh';Text='这是中文合成测试，不是真实会议。营业收入增长百分之十二，但净利润没有增长。请在十月四日复核二十万元的预算。'})},
   @{Id='mixed';Parts=@(@{Language='zh';Text='这是中英文合成测试，不是真实会议。今天核对现金流和预算。'},@{Language='en';Text='Revenue increased by twelve percent, but net profit did not increase. Please review the cash flow on October fourth.'},@{Language='zh';Text='请保留原始录音，不要上传。预算是二十万元。最后一句用于检查输出是否完整。'})}
  )
  $created=@()
  foreach($definition in $definitions){
   $path=Join-Path $fixtures ($definition.Id+'.wav');$stream=New-Object -ComObject SAPI.SpFileStream
   try{$stream.Format.Type=18;$stream.Open($path,3,$false);$voice.AudioOutputStream=$stream;$voice.Rate=0
    foreach($part in $definition.Parts){$voice.Voice=if($part.Language -eq 'zh'){$zh}else{$en};[void]$voice.Speak($part.Text,0)}
    $stream.Close()
   }finally{[void][Runtime.InteropServices.Marshal]::ReleaseComObject($stream)}
   $created+=@{Id=$definition.Id;Path=$path;ExpectedText=($definition.Parts.Text -join ' ');Parts=$definition.Parts;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
  }
  @{Schema='projectecho-m4-synthetic-fixtures-1';Synthetic=$true;AudioPlayed=$false;CaptureStarted=$false;MicrophoneEnabled=$false;Uploaded=$false;Voices=@($zh.GetDescription(),$en.GetDescription());Fixtures=$created} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $fixtures 'fixtures.json') -Encoding utf8
 }finally{[void][Runtime.InteropServices.Marshal]::ReleaseComObject($voice)}
}
$fixtureManifest=Get-Content -LiteralPath (Join-Path $fixtures 'fixtures.json') -Raw | ConvertFrom-Json
$public=Join-Path $root '.tools\transcribe-source\samples'
$batch=@((Join-Path $public 'zh-short.wav'),(Join-Path $public 'zh-long.wav'))+@($fixtureManifest.Fixtures.Path)
$beforeHashes=@($batch | ForEach-Object {(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash})
$batchPath=Join-Path $output '中文批次.list'
[IO.File]::WriteAllLines($batchPath,$batch,[Text.UTF8Encoding]::new($false))
$baseArgs=@('--model',$ModelPath,'--backend',$Backend.ToLowerInvariant(),'--threads','2','--n-ctx','2048','--timestamps','none')
if($Backend -eq 'Vulkan'){$baseArgs+=@('--device','0')}
$runs=[Collections.Generic.List[object]]::new()
function Invoke-Probe([string]$Id,[string[]]$Arguments,[int]$CancelMs=0){
 $psi=[Diagnostics.ProcessStartInfo]::new($ExecutablePath);$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
 $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true;$psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false);$psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
 foreach($a in $Arguments){$psi.ArgumentList.Add($a)}
 # Empty the process search PATH so only Windows system DLLs can satisfy imports.
 $psi.Environment['PATH']=$env:SystemRoot+'\System32';$psi.WorkingDirectory=$output
 $process=[Diagnostics.Process]::new();$process.StartInfo=$psi;$sw=[Diagnostics.Stopwatch]::StartNew()
 [void]$process.Start();$process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$peak=0L;$cancelled=$false
 while(!$process.WaitForExit(100)){
  $process.Refresh();$peak=[Math]::Max($peak,$process.PeakWorkingSet64)
  if($CancelMs -gt 0 -and $sw.ElapsedMilliseconds -ge $CancelMs){$process.Kill($true);$cancelled=$true;$process.WaitForExit();break}
  if($sw.Elapsed.TotalMinutes -gt 15){$process.Kill($true);throw 'Runtime probe timed out'}
 }
 $process.WaitForExit();$sw.Stop();$outText=$stdout.GetAwaiter().GetResult();$errText=$stderr.GetAwaiter().GetResult()
 [IO.File]::WriteAllText((Join-Path $output ($Id+'.stdout.jsonl')),$outText,[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $output ($Id+'.stderr.log')),$errText,[Text.UTF8Encoding]::new($false))
 $run=[ordered]@{Id=$Id;Arguments=$Arguments;ExitCode=$process.ExitCode;ElapsedMs=$sw.ElapsedMilliseconds;PeakWorkingSetBytes=$peak;ProcessTreeKilled=$cancelled;ProcessExited=$process.HasExited;ActualBackend=if($errText -match 'projectecho-backend: ([^\r\n]+)'){$Matches[1]}else{$null};StdoutSha256=(Get-FileHash -LiteralPath (Join-Path $output ($Id+'.stdout.jsonl')) -Algorithm SHA256).Hash.ToLowerInvariant()}
 $runs.Add($run);$process.Dispose();$runs | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'runs.json') -Encoding utf8
 Write-Output ($Id+': exit='+$run.ExitCode+' elapsedMs='+$run.ElapsedMs+' backend='+$run.ActualBackend)
}
Invoke-Probe 'batch' ($baseArgs+@('--batch',$batchPath,'--batch-jsonl'))
Invoke-Probe 'cancel' ($baseArgs+@('--batch',$batchPath,'--batch-jsonl')) 1500
Invoke-Probe 'retry' ($baseArgs+@('--batch',$batchPath,'--batch-jsonl'))
Invoke-Probe 'invalid-device' ($baseArgs+@('--device','999','--batch',$batchPath,'--batch-jsonl'))
Invoke-Probe 'unsupported-timestamps' ($baseArgs+@('--timestamps','word','--batch',$batchPath,'--batch-jsonl'))
Invoke-Probe 'language-zh' ($baseArgs+@('-l','zh',(Join-Path $public 'zh-short.wav')))
$rows=@(Get-Content -LiteralPath (Join-Path $output 'batch.stdout.jsonl') | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object {$_.file})
$allInputs=@($batch | ForEach-Object {@{Path=$_;Sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$retryRows=@(Get-Content -LiteralPath (Join-Path $output 'retry.stdout.jsonl') | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object {$_.file})
$checks=[ordered]@{
 BatchSucceeded=($runs[0].ExitCode -eq 0 -and $rows.Count -eq $batch.Count -and @($rows | Where-Object {$_.error -or !$_.text}).Count -eq 0)
 RequestedBackendUsed=($runs[0].ActualBackend -eq $(if($Backend -eq 'Cpu'){'CPU'}else{'Vulkan0'}))
 CancellationExited=($runs[1].ProcessTreeKilled -and $runs[1].ProcessExited -and $runs[1].ExitCode -ne 0)
 RetrySucceeded=($runs[2].ExitCode -eq 0 -and ($rows.text -join "`n") -ceq ($retryRows.text -join "`n"))
 InvalidDeviceRejected=($runs[3].ExitCode -ne 0)
 UnsupportedTimestampsRejected=($runs[4].ExitCode -ne 0)
 ChineseHintSucceeded=($runs[5].ExitCode -eq 0)
 InputHashesUnchanged=(($beforeHashes -join ',').ToLowerInvariant() -eq ($allInputs.Sha256 -join ','))
 NoNativeTimestamps=(@($rows | Where-Object {$_.segments}).Count -eq 0)
}
[ordered]@{Schema='projectecho-m4-native-runtime-probe-1';BackendRequested=$Backend;Executable=$ExecutablePath;ExecutableSha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant();Model=$ModelPath;ModelSha256=(Get-FileHash -LiteralPath $ModelPath -Algorithm SHA256).Hash.ToLowerInvariant();Rows=$rows.Count;ExpectedRows=$batch.Count;Checks=$checks;InputHashes=$allInputs;Runs=$runs;SyntheticManifest=(Join-Path $fixtures 'fixtures.json');CaptureStarted=$false;MicrophoneEnabled=$false;Uploaded=$false;CleanWindowsTested=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
if(@($checks.Values | Where-Object {!$_}).Count -gt 0){throw 'Runtime probe checks failed; see summary.json'}
Write-Output ('Evidence: '+$output)
