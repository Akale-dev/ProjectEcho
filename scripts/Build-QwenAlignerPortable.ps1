[CmdletBinding()]
param([ValidateRange(1,8)][int]$Jobs=2)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source=Join-Path $root '.tools\audio-aligner-source\audio.cpp-0.9.0'
$toolBin=Join-Path $root '.tools\vulkan-build\w64devkit\bin'
$build=Join-Path $root '.tools\qwen-aligner-cpu'
$evidence=Join-Path $root '.local\M4\alignment'
$archive=Join-Path $evidence 'audio-source-v0.9.0.zip'
if((Get-FileHash -LiteralPath $archive).Hash -ne 'ECF4C67E80545176E3603DC3C8F91834ACCAAFF38FF5F3B3007208A1A286F6DA'){throw 'Unexpected reviewed audio.cpp v0.9.0 source archive'}
foreach($path in @($source,$toolBin,$build,$evidence)){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse linked build path'}
 }
}
$processor=Join-Path $source 'src\models\qwen3_forced_aligner\processor.cpp'
$original=Join-Path $evidence 'upstream-processor.cpp'
$text=[IO.File]::ReadAllText($processor)
if($text.Contains('projectecho-alignment-raw-v1')){$text=[IO.File]::ReadAllText($original)}
if(!$text.Contains('projectecho-alignment-raw-v1')){
 if(!(Test-Path -LiteralPath $original)){[IO.File]::WriteAllText($original,$text,[Text.UTF8Encoding]::new($false))}
 $text=$text.Replace('#include <cstdint>','#include <cstdint>'+[Environment]::NewLine+'#include <cstdio>')
 $text=$text.Replace('std::vector<int64_t> fix_timestamp(', '[[maybe_unused]] std::vector<int64_t> fix_timestamp(')
 $text=$text.Replace('    timestamp_ms = fix_timestamp(timestamp_ms);','    // projectecho-alignment-raw-v1: preserve acoustic predictions; never interpolate or repair.'+[Environment]::NewLine+'    (void)audio_frames; (void)clamp_timestamps_to_audio;'+[Environment]::NewLine+'    std::fprintf(stderr, "projectecho-alignment: raw-model-boundaries-v1; sample_rate=%d; timestamp_quantum_ms=%lld\n", sample_rate, static_cast<long long>(assets_.config.timestamp_segment_time_ms));')
 $start=$text.IndexOf('    // The official Qwen3 forced aligner can emit equal start/end timestamps')
 $end=$text.IndexOf('    return out;', $start)
 if($start -lt 0 -or $end -lt 0){throw 'Reviewed timestamp repair removal point changed'}
 $text=$text.Remove($start,$end-$start)
 [IO.File]::WriteAllText($processor,$text,[Text.UTF8Encoding]::new($false))
}
$manifest=Join-Path $PSScriptRoot 'whisper-portable.manifest'
Copy-Item -LiteralPath $manifest -Destination (Join-Path $source 'app\cli\ProjectEcho-portable.manifest') -Force
$manifestPath=(Join-Path $source 'app\cli\ProjectEcho-portable.manifest').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $source 'app\cli\ProjectEcho-portable.rc'),'1 24 "'+$manifestPath+'"'+[Environment]::NewLine)
$cmake=Join-Path $source 'CMakeLists.txt'
if(![IO.File]::ReadAllText($cmake).Contains('# ProjectEcho raw alignment portable executable')){
 [IO.File]::AppendAllText($cmake,@'

# ProjectEcho raw alignment portable executable
if(MINGW)
    enable_language(RC)
    target_sources(audiocpp_cli PRIVATE app/cli/ProjectEcho-portable.rc)
endif()
'@)
}
$arguments=@('-S',$source,'-B',$build,'-G','Ninja',('-DCMAKE_C_COMPILER='+$toolBin+'\gcc.exe'),('-DCMAKE_CXX_COMPILER='+$toolBin+'\g++.exe'),('-DCMAKE_MAKE_PROGRAM='+$toolBin+'\ninja.exe'),
 '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_POLICY_VERSION_MINIMUM=3.5','-DAUDIOCPP_VERSION=0.9.0-ProjectEcho-raw-v1','-DAUDIOCPP_MODEL_SET=custom','-DAUDIOCPP_MODELS=qwen3_forced_aligner',
 '-DAUDIOCPP_DEPLOYMENT_BUILD=ON','-DENGINE_ENABLE_NATIVE_CPU=OFF','-DENGINE_ENABLE_OPENMP=OFF','-DENGINE_ENABLE_LLAMAFILE=OFF','-DENGINE_ENABLE_CUDA=OFF','-DENGINE_ENABLE_VULKAN=OFF','-DBUILD_SHARED_LIBS=OFF',
 '-DCMAKE_EXE_LINKER_FLAGS=-static','-DGGML_CCACHE=OFF','-DGGML_NATIVE=OFF','-DGGML_SSE42=OFF','-DGGML_AVX=OFF','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_BMI2=OFF','-DGGML_OPENMP=OFF','-DSPM_ENABLE_SHARED=OFF','-DSPM_BUILD_TEST=OFF')
$previous=@{}; $settings=@{PATH=$toolBin+';'+$env:PATH;TEMP=(Join-Path $root '.local\temp');TMP=(Join-Path $root '.local\temp')}
foreach($key in $settings.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
try{
 [Diagnostics.Process]::GetCurrentProcess().PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 & (Join-Path $toolBin 'cmake.exe') @arguments
 if($LASTEXITCODE -ne 0){throw 'Aligner configure failed'}
 & (Join-Path $toolBin 'cmake.exe') --build $build --target audiocpp_cli --parallel $Jobs
 if($LASTEXITCODE -ne 0){throw 'Aligner build failed'}
 $exe=Join-Path $build 'bin\audiocpp_cli.exe'
 $dependencies=@(& (Join-Path $toolBin 'objdump.exe') -p $exe | Select-String 'DLL Name:' | ForEach-Object {($_.Line -split 'DLL Name:')[1].Trim()})
 [ordered]@{Version='audio.cpp-v0.9.0-ProjectEcho-raw-v1';SourceCommit='795c45fb';SourceArchiveSha256=(Get-FileHash $archive).Hash.ToLowerInvariant();SourceLicense='Apache-2.0';LocalChanges=@('Remove all timestamp interpolation and zero-span repair','UTF-8 asInvoker longPathAware manifest','Emit raw-boundary protocol diagnostic');OriginalProcessorSha256=(Get-FileHash $original).Hash.ToLowerInvariant();PatchedProcessorSha256=(Get-FileHash $processor).Hash.ToLowerInvariant();ConfigureArguments=$arguments;Binary=$exe;BinarySha256=(Get-FileHash $exe).Hash.ToLowerInvariant();ImportedDlls=$dependencies;FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'raw-aligner-build.json') -Encoding utf8
}finally{foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
