[CmdletBinding()]
param([string]$LongEvidence='.local/M4/real-long-02',[string]$PortableDirectory='artifacts/E74/候选',
    [string]$EvidenceDirectory='.local/M4/post-long-candidate')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$long=[IO.Path]::GetFullPath($LongEvidence,$root);$bundle=[IO.Path]::GetFullPath($PortableDirectory,$root)
$output=[IO.Path]::GetFullPath($EvidenceDirectory,$root)
foreach($path in @($long,$bundle,$output)){
    if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Only authorized project-generated test paths.'}
    for($part=$path;$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'No linked test directories.'}
    }
}
if(Test-Path -LiteralPath $output){throw 'Use a fresh post-long evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$testSource=Join-Path $root 'tests/ProjectEcho.Tests/bin/Release/net10.0-windows'
$testSnapshot=Join-Path $output 'test-runtime'
Copy-Item -LiteralPath $testSource -Destination $testSnapshot -Recurse
$frozen=Get-Content -LiteralPath ($long+'-launch/tested-component-hashes.json') -Raw|ConvertFrom-Json
foreach($component in $frozen.Components){
    $source=Join-Path $frozen.Source $component.RelativePath;$destination=Join-Path $testSnapshot $component.RelativePath
    if((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $component.Sha256){throw 'Frozen production DLL changed.'}
    Copy-Item -LiteralPath $source -Destination $destination -Force
    if((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $component.Sha256){throw 'Post-long runtime is not the tested production code.'}
}
$waiting=[Diagnostics.Stopwatch]::StartNew()
Write-Host 'Waiting for complete three-hour report and independent GPU measurement to finish. No model runs overlap.'
while(-not (Test-Path -LiteralPath (Join-Path $long 'report.json')) -or -not (Test-Path -LiteralPath ($long+'-launch/gpu-summary.json'))){
    if($waiting.Elapsed -gt [TimeSpan]::FromHours(5)){throw 'Long-run completion wait exceeded five hours; no post-long model was started.'}
    Start-Sleep -Seconds 10
}
$longReport=Get-Content -LiteralPath (Join-Path $long 'report.json') -Raw|ConvertFrom-Json
if($longReport.Status -ne 'PASS'){throw 'Three-hour test did not pass; no post-long model was started.'}
[ordered]@{longReportSha256=(Get-FileHash -LiteralPath (Join-Path $long 'report.json')).Hash.ToLowerInvariant();startedUtc=[DateTimeOffset]::UtcNow;longFinishedUtc=$longReport.FinishedUtc}|
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'start-after-long.json') -Encoding utf8

# User authorized H:\AI\Qwen explicitly. Hard links add Chinese names without copying model weights.
$modelRoot=[IO.Path]::GetFullPath('H:\AI\Qwen')
$aliases=Join-Path $modelRoot '中文路径验证'
if((Test-Path -LiteralPath $aliases) -and ((Get-Item -LiteralPath $aliases).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Model alias directory is linked.'}
[IO.Directory]::CreateDirectory($aliases)|Out-Null
$modelPairs=@(
    @{Source=Join-Path $modelRoot 'models/Qwen3-ASR-0.6B-Q8_0.gguf';Target=Join-Path $aliases '千问识别0.6B-Q8_0.gguf'},
    @{Source=Join-Path $modelRoot 'Qwen3-ForcedAligner-0.6B-GGUF/qwen3-forced-aligner-0.6b-q8_0.gguf';Target=Join-Path $aliases '句子定位0.6B-Q8_0.gguf'}
)
foreach($pair in $modelPairs){
    foreach($path in @($pair.Source,$pair.Target)){
        $full=[IO.Path]::GetFullPath($path)
        if(-not $full.StartsWith($modelRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Model path escaped authorized Qwen folder.'}
        if((Test-Path -LiteralPath $full) -and ((Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Model is a reparse point.'}
    }
    if(-not (Test-Path -LiteralPath $pair.Target)){New-Item -ItemType HardLink -Path $pair.Target -Value $pair.Source|Out-Null}
    if((Get-FileHash -LiteralPath $pair.Source).Hash -ne (Get-FileHash -LiteralPath $pair.Target).Hash){throw 'Existing Chinese alias model differs; refusing overwrite.'}
}
$model=$modelPairs[0].Target;$alignmentModel=$modelPairs[1].Target
$aligner=Join-Path $bundle 'Tools/qwen-aligner/audiocpp_cli.exe'
$fixtureManifest=Get-Content -LiteralPath (Join-Path $root '.local/M4/runtime/合成样本/fixtures.json') -Raw|ConvertFrom-Json
$mixed=($fixtureManifest.Fixtures|Where-Object Id -eq 'mixed').Path
$cases=@(
    @{Name='cpu-natural-chinese';Audio=Join-Path $root '.tools/transcribe-source/samples/zh-short.wav';Cli=Join-Path $bundle 'Tools/qwen-cpu/transcribe-cli.exe';Cpu=$true},
    @{Name='vulkan-mixed-sentences';Audio=$mixed;Cli=Join-Path $bundle 'Tools/qwen-vulkan/transcribe-cli.exe';Cpu=$false}
)
$runs=@()
foreach($case in $cases){
    $started=[DateTimeOffset]::UtcNow
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $bundle 'ProjectEcho.exe'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.WorkingDirectory=$root
    $info.Environment['DOTNET_ROOT']=Join-Path $root '.local/no-runtime-m4-real-ui';$info.Environment['DOTNET_ROOT_X64']=$info.Environment['DOTNET_ROOT'];$info.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $info.Environment['PATH']=[Environment]::SystemDirectory
    foreach($value in @('--m4-real-qwen-smoke','--audio',$case.Audio,'--cli',$case.Cli,'--model',$model,'--aligner',$aligner,'--aligner-model',$alignmentModel)){$info.ArgumentList.Add($value)}
    if($case.Cpu){$info.ArgumentList.Add('--cpu')}
    $process=[Diagnostics.Process]::Start($info)
    try{if(-not $process.WaitForExit(480000)){$process.Kill($true);$process.WaitForExit();throw ('Real UI timeout: '+$case.Name)};$exitCode=$process.ExitCode}finally{$process.Dispose()}
    $reports=@(Get-ChildItem -LiteralPath (Join-Path $root '.local/M4') -Directory -Filter 'real-ui-*'|
        Where-Object{$_.CreationTimeUtc -ge $started.UtcDateTime}|ForEach-Object{Join-Path $_.FullName 'report.json'}|Where-Object{Test-Path -LiteralPath $_})
    $runs += [ordered]@{name=$case.Name;startedUtc=$started;endedUtc=[DateTimeOffset]::UtcNow;exitCode=$exitCode;reports=$reports;systemPathOnly=$true;invalidRuntimeRoots=$true}
    $runs|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'real-ui-runs.json') -Encoding utf8
    if($exitCode -ne 0 -or $reports.Count -ne 1 -or (Get-Content -LiteralPath $reports[0] -Raw|ConvertFrom-Json).Result.Status -ne 'PASS'){throw ('Real UI failed: '+$case.Name)}
    Write-Host ('PASS real UI: '+$case.Name)
}
$pauseOutput=Join-Path $output 'pause-timeline'
& (Join-Path $root 'scripts/Dotnet.ps1') -Arguments @('exec',(Join-Path $testSnapshot 'ProjectEcho.Tests.dll'),
    '--m4-qwen-pause-probe','--cli',(Join-Path $bundle 'Tools/qwen-vulkan/transcribe-cli.exe'),'--model',$model,
    '--aligner',$aligner,'--aligner-model',$alignmentModel,'--audio',(Join-Path $root '.tools/transcribe-source/samples/jfk.wav'),'--output',$pauseOutput) *> (Join-Path $output 'pause.log')
[ordered]@{status='PASS';sourceMode='Fixed public and SAPI audio, actual UI and native models, muted replay, no capture/microphone/upload';
    completedUtc=[DateTimeOffset]::UtcNow;bundle=$bundle;chineseModelAliases=$modelPairs;uiRuns=$runs;pauseEvidence=$pauseOutput}|
    ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Host ('PASS post-long UI and new/legacy pause chain: '+$output)
