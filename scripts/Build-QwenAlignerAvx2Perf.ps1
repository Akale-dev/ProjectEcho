[CmdletBinding()]
param([ValidateRange(1,8)][int]$Jobs=2)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source=Join-Path $root '.tools\M4Perf-cpu-source\audio.cpp-0.9.0'
$toolBin=Join-Path $root '.tools\vulkan-build\w64devkit\bin'
$build=Join-Path $root '.tools\qwen-aligner-avx2-perf'
$evidence=Join-Path $root '.local\M4Perf\cpu'
$archive=Join-Path $root '.local\M4\alignment\audio-source-v0.9.0.zip'
if((Get-FileHash -LiteralPath $archive).Hash -ne 'ECF4C67E80545176E3603DC3C8F91834ACCAAFF38FF5F3B3007208A1A286F6DA'){throw 'Unexpected reviewed audio.cpp v0.9.0 source archive'}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
if(!(Test-Path -LiteralPath $source)){
 Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $root '.tools\M4Perf-cpu-source')
}
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
$executionPath=Join-Path $source 'src\framework\core\execution_context.cpp'
$executionText=[IO.File]::ReadAllText($executionPath)
if(!$executionText.Contains('projectecho-aligner-backend:')){
 $executionText=$executionText.Replace('#include <utility>','#include <utility>'+[Environment]::NewLine+'#include <cstdio>')
 $entry='      backend_(init_backend(config)) {'
 $body=@'
      backend_(init_backend(config)) {
    if (backend_ != nullptr) {
        auto device = ggml_backend_get_device(backend_);
        std::fprintf(stderr, "projectecho-aligner-backend: %s\n", ggml_backend_name(backend_));
        std::fprintf(stderr, "projectecho-aligner-device: %s\n", device != nullptr ? ggml_backend_dev_description(device) : "Unavailable");
        std::fflush(stderr);
    }
'@
 if(!$executionText.Contains($entry)){throw 'Reviewed execution context point changed'}
 [IO.File]::WriteAllText($executionPath,$executionText.Replace($entry,$body),[Text.UTF8Encoding]::new($false))
}
$sinkPath=Join-Path $source 'app\workflow\file_sink.cpp'
$sinkText=[IO.File]::ReadAllText($sinkPath)
if(!$sinkText.Contains('projectecho-aligner-complete:')){
 $sinkText=$sinkText.Replace('#include <cstddef>','#include <cstddef>'+[Environment]::NewLine+'#include <cstdio>')
 $at=$sinkText.IndexOf('        policy.wav_options);',$sinkText.IndexOf('void emit_batch_item_result('))
 if($at -lt 0){throw 'Reviewed batch emission point changed'}
 $at+='        policy.wav_options);'.Length
 $sinkText=$sinkText.Insert($at,[Environment]::NewLine+'    std::cout.flush();'+[Environment]::NewLine+'    std::fprintf(stderr, "projectecho-aligner-complete: %s\n", request_id.c_str());'+[Environment]::NewLine+'    std::fflush(stderr);')
 [IO.File]::WriteAllText($sinkPath,$sinkText,[Text.UTF8Encoding]::new($false))
}
$manifest=Join-Path $PSScriptRoot 'whisper-portable.manifest'
$mainPath=Join-Path $source 'app\cli\main.cpp'
$mainText=[IO.File]::ReadAllText($mainPath)
if(!$mainText.Contains('projectecho-aligner-protocol: batch-v2')){
 $at=$mainText.IndexOf('print_build_info(std::cout);')
 if($at -lt 0){throw 'Reviewed version output point changed'}
 $at+='print_build_info(std::cout);'.Length
 $mainText=$mainText.Insert($at,[Environment]::NewLine+'            std::cout << "projectecho-aligner-protocol: batch-v2\n";')
 [IO.File]::WriteAllText($mainPath,$mainText,[Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $manifest -Destination (Join-Path $source 'app\cli\ProjectEcho-portable.manifest') -Force
$manifestPath=(Join-Path $source 'app\cli\ProjectEcho-portable.manifest').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $source 'app\cli\ProjectEcho-portable.rc'),'1 24 "'+$manifestPath+'"'+[Environment]::NewLine)
$cmake=Join-Path $source 'CMakeLists.txt'
# Do not let the archive source's CMake discover the unrelated outer ProjectEcho Git identity.
$cmakeText=[IO.File]::ReadAllText($cmake)
if(!$cmakeText.Contains('# ProjectEcho pinned upstream identity')){
 $marker=$cmakeText.IndexOf('"${CMAKE_CURRENT_SOURCE_DIR}/app/common/build_info_config.h.in"')
 $at=$cmakeText.LastIndexOf('configure_file(', $marker)
 if($marker -lt 0 -or $at -lt 0){throw 'Reviewed version configuration point changed'}
 $cmakeText=$cmakeText.Insert($at,'# ProjectEcho pinned upstream identity'+[Environment]::NewLine+'set(AUDIOCPP_GIT_SHA "795c45fb+raw-v1-avx2")'+[Environment]::NewLine+'set(AUDIOCPP_GIT_DATE "2026-09-30")'+[Environment]::NewLine)
 [IO.File]::WriteAllText($cmake,$cmakeText,[Text.UTF8Encoding]::new($false))
}
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
 '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_POLICY_VERSION_MINIMUM=3.5','-DAUDIOCPP_VERSION=0.9.0-ProjectEcho-raw-v1-avx2','-DAUDIOCPP_MODEL_SET=custom','-DAUDIOCPP_MODELS=qwen3_forced_aligner',
 '-DAUDIOCPP_DEPLOYMENT_BUILD=ON','-DENGINE_ENABLE_NATIVE_CPU=OFF','-DENGINE_ENABLE_OPENMP=OFF','-DENGINE_ENABLE_LLAMAFILE=OFF','-DENGINE_ENABLE_CUDA=OFF','-DENGINE_ENABLE_VULKAN=OFF','-DBUILD_SHARED_LIBS=OFF',
 '-DCMAKE_EXE_LINKER_FLAGS=-static','-DGGML_CCACHE=OFF','-DGGML_NATIVE=OFF','-DGGML_SSE42=ON','-DGGML_AVX=ON','-DGGML_AVX2=ON','-DGGML_FMA=ON','-DGGML_F16C=ON','-DGGML_BMI2=ON',
 '-DGGML_AVX_VNNI=OFF','-DGGML_AVX512=OFF','-DGGML_AVX512_VBMI=OFF','-DGGML_AVX512_VNNI=OFF','-DGGML_AVX512_BF16=OFF','-DGGML_AMX_TILE=OFF','-DGGML_AMX_INT8=OFF','-DGGML_AMX_BF16=OFF',
 '-DGGML_OPENMP=OFF','-DSPM_ENABLE_SHARED=OFF','-DSPM_BUILD_TEST=OFF')
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
 [ordered]@{Version='audio.cpp-v0.9.0-ProjectEcho-raw-v1-avx2';SourceCommit='795c45fb';SourceArchiveSha256=(Get-FileHash $archive).Hash.ToLowerInvariant();SourceLicense='Apache-2.0';CpuRequirements=@('SSE4.2','AVX','AVX2','FMA','F16C','BMI2');NativeTuning=$false;Avx512=$false;LocalChanges=@('Same raw-v1 timestamp preservation as M4 generic CPU','UTF-8 asInvoker longPathAware manifest','Explicit AVX2/FMA/F16C/BMI2/SSE42 instead of generic scalar kernels','Pinned upstream version metadata');OriginalProcessorSha256=(Get-FileHash $original).Hash.ToLowerInvariant();PatchedProcessorSha256=(Get-FileHash $processor).Hash.ToLowerInvariant();ConfigureArguments=$arguments;Binary=$exe;BinarySha256=(Get-FileHash $exe).Hash.ToLowerInvariant();ImportedDlls=$dependencies;FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'raw-aligner-build.json') -Encoding utf8
}finally{foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
