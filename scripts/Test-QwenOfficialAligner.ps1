[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$FixtureManifest='.local/M4Perf/cpu/fixtures.json',
    [ValidateSet('legacy','hf')][string]$Engine='legacy',
    [ValidateSet('float32','bfloat16')][string]$Dtype='float32',
    [ValidateRange(1,16)][int]$Threads=6,
    [ValidateRange(0,4)][int]$WarmRepeats=2,
    [string]$Ids='long02-window-000000,long02-window-000001,long02-window-000002,independent-boundaries',
    [string]$RequestSequence='',
    [switch]$Compile
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$python=[IO.Path]::GetFullPath($PythonExecutable,$root);$output=[IO.Path]::GetFullPath($OutputDirectory,$root)
if(-not $python.StartsWith((Join-Path $root '.tools').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Use an isolated project-local Python executable.'}
if(-not $output.StartsWith((Join-Path $root '.local\M4Perf').TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Evidence output must remain in .local/M4Perf.'}
if(Test-Path -LiteralPath $output){throw 'Choose a new output directory.'}
foreach($path in @($python,$output)){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse reparse benchmark path.'}
 }
}
$gatePath=Join-Path $root '.local\M4Perf\inference-benchmark.lock'
# A live file handle is the lock. Stale file existence alone never blocks a run.
$gate=[IO.FileStream]::new($gatePath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$settings=@{TEMP=(Join-Path $root '.local\temp');TMP=(Join-Path $root '.local\temp');PYTHONNOUSERSITE='1';PYTHONDONTWRITEBYTECODE='1';HF_HUB_OFFLINE='1';TRANSFORMERS_OFFLINE='1';HF_HUB_DISABLE_TELEMETRY='1';GRADIO_ANALYTICS_ENABLED='False'}
$previous=@{}
foreach($key in $settings.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
try{
 $arguments=@('-I','-B',(Join-Path $PSScriptRoot 'Test-QwenOfficialAligner.py'),'--model',[IO.Path]::GetFullPath($ModelDirectory,$root),
  '--fixtures',[IO.Path]::GetFullPath($FixtureManifest,$root),'--output',$output,'--engine',$Engine,'--dtype',$Dtype,
  '--threads',[string]$Threads,'--warm-repeats',[string]$WarmRepeats,'--ids',$Ids)
 if($Compile){$arguments+=@('--compile')}
 if($RequestSequence){$arguments+=@('--request-sequence',[IO.Path]::GetFullPath($RequestSequence,$root))}
 $started=[DateTimeOffset]::UtcNow
 & $python @arguments
 $code=$LASTEXITCODE
 if(Test-Path -LiteralPath $output){[ordered]@{StartedUtc=$started;FinishedUtc=[DateTimeOffset]::UtcNow;ExitCode=$code;Lock=$gatePath;Python=$python;Arguments=$arguments;EnvironmentScope='Process only; no global PATH/packages changes'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $output 'invocation.json') -Encoding utf8}
 if($code -ne 0){throw ('Official aligner evaluation failed with code '+$code)}
}finally{
 foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}
 $gate.Dispose()
}
