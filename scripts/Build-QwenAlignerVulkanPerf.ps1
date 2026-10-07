[CmdletBinding()]
param([ValidateRange(1,4)][int]$Jobs=2)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source=Join-Path $root '.tools\M4Perf-vulkan-source'
$build=Join-Path $root '.tools\qwen-aligner-vulkan-perf'
$evidence=Join-Path $root '.local\M4Perf\vulkan'
$toolBin=Join-Path $root '.tools\vulkan-build\w64devkit\bin'
$sdk=Join-Path $root '.tools\vulkan-build\msys2\ucrt64'
$baseline=Join-Path $root '.tools\audio-aligner-source\audio.cpp-0.9.0'
foreach($path in @($source,$build,$evidence,$toolBin,$sdk)){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse linked build path'}
 }
}
if(!(Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))){throw 'Prepare the independent v0.9.0 raw-v1 source copy first; never build in the M4 baseline directory'}
foreach($path in @((Join-Path $toolBin 'cmake.exe'),(Join-Path $toolBin 'g++.exe'),(Join-Path $sdk 'bin\glslc.exe'),(Join-Path $sdk 'include\vulkan\vulkan.hpp'),(Join-Path $sdk 'lib\libvulkan-1.dll.a'))){if(!(Test-Path -LiteralPath $path)){throw ('Missing existing prerequisite: '+$path)}}
New-Item -ItemType Directory -Path $evidence -Force|Out-Null
$processor=Join-Path $source 'src\models\qwen3_forced_aligner\processor.cpp'
if(![IO.File]::ReadAllText($processor).Contains('projectecho-alignment-raw-v1')){throw 'Raw acoustic boundary patch absent'}
if((Get-FileHash -LiteralPath $processor).Hash -ne (Get-FileHash -LiteralPath (Join-Path $baseline 'src\models\qwen3_forced_aligner\processor.cpp')).Hash){throw 'Do not change the frozen raw-boundary processor in this build recipe'}
$manifest=Join-Path $PSScriptRoot 'whisper-portable.manifest'
Copy-Item -LiteralPath $manifest -Destination (Join-Path $source 'app\cli\ProjectEcho-portable.manifest') -Force
$manifestPath=(Join-Path $source 'app\cli\ProjectEcho-portable.manifest').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $source 'app\cli\ProjectEcho-portable.rc'),'1 24 "'+$manifestPath+'"'+[Environment]::NewLine)
# Actual backend is read from the initialized ggml handle, never inferred from
# the requested flag. This is diagnostic-only and kept in this independent copy.
$contextFile=Join-Path $source 'src\framework\core\execution_context.cpp'
$context=[IO.File]::ReadAllText($contextFile)
if(!$context.Contains('projectecho-aligner-backend:')){
 Copy-Item -LiteralPath $contextFile -Destination (Join-Path $evidence 'execution-context-before.cpp')
 $context=$context.Replace('#include <utility>','#include <utility>'+[Environment]::NewLine+'#include <cstdio>')
 $anchor='    if (backend_ != nullptr && config_.threads > 0) {'
 if(!$context.Contains($anchor)){throw 'Reviewed backend diagnostic insertion point changed'}
 $diagnostic=@'
    if (backend_ != nullptr) {
        const auto device = ggml_backend_get_device(backend_);
        std::fprintf(stderr, "projectecho-aligner-backend: %s\n", ggml_backend_name(backend_));
        std::fprintf(stderr, "projectecho-aligner-device: %s\n", device ? ggml_backend_dev_description(device) : "unknown");
    }
'@
 $context=$context.Replace($anchor,$diagnostic+[Environment]::NewLine+$anchor)
 [IO.File]::WriteAllText($contextFile,$context,[Text.UTF8Encoding]::new($false))
}
$sinkFile=Join-Path $source 'app\workflow\file_sink.cpp'
$sink=[IO.File]::ReadAllText($sinkFile)
if(!$sink.Contains('projectecho-aligner-complete:')){
 Copy-Item -LiteralPath $sinkFile -Destination (Join-Path $evidence 'file-sink-before.cpp')
 if(!$sink.Contains('#include <cstdio>')){$sink='#include <cstdio>'+[Environment]::NewLine+$sink}
 $needle='        policy.wav_options);'
 if(!$sink.Contains($needle)){throw 'Reviewed batch output completion point changed'}
 $sink=$sink.Replace($needle,$needle+[Environment]::NewLine+'    std::cout.flush();'+[Environment]::NewLine+'    std::fprintf(stderr, "projectecho-aligner-complete: %s\n", request_id.c_str());'+[Environment]::NewLine+'    std::fflush(stderr);')
 [IO.File]::WriteAllText($sinkFile,$sink,[Text.UTF8Encoding]::new($false))
}
$cliFile=Join-Path $source 'app\cli\main.cpp'
$cli=[IO.File]::ReadAllText($cliFile)
if(!$cli.Contains('projectecho-aligner-protocol: batch-v2')){
 Copy-Item -LiteralPath $cliFile -Destination (Join-Path $evidence 'cli-before.cpp')
 $needle='            minitts::app::print_build_info(std::cout);'
 if(!$cli.Contains($needle)){throw 'Reviewed --version protocol insertion point changed'}
 $cli=$cli.Replace($needle,$needle+[Environment]::NewLine+'            std::cout << "projectecho-aligner-protocol: batch-v2\n";')
 [IO.File]::WriteAllText($cliFile,$cli,[Text.UTF8Encoding]::new($false))
}
$arguments=@('-S',$source,'-B',$build,'-G','Ninja',('-DCMAKE_C_COMPILER='+$toolBin+'\gcc.exe'),('-DCMAKE_CXX_COMPILER='+$toolBin+'\g++.exe'),('-DCMAKE_MAKE_PROGRAM='+$toolBin+'\ninja.exe'),
 '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_POLICY_VERSION_MINIMUM=3.5','-DAUDIOCPP_VERSION=0.9.0-ProjectEcho-raw-v1-vulkan-perf','-DAUDIOCPP_MODEL_SET=custom','-DAUDIOCPP_MODELS=qwen3_forced_aligner',
 '-DAUDIOCPP_DEPLOYMENT_BUILD=ON','-DENGINE_ENABLE_NATIVE_CPU=OFF','-DENGINE_ENABLE_OPENMP=OFF','-DENGINE_ENABLE_LLAMAFILE=OFF','-DENGINE_ENABLE_CUDA=OFF','-DENGINE_ENABLE_VULKAN=ON','-DBUILD_SHARED_LIBS=OFF',
 '-DCMAKE_EXE_LINKER_FLAGS=-static','-DGGML_CCACHE=OFF','-DGGML_NATIVE=OFF','-DGGML_SSE42=OFF','-DGGML_AVX=OFF','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_BMI2=OFF','-DGGML_OPENMP=OFF','-DSPM_ENABLE_SHARED=OFF','-DSPM_BUILD_TEST=OFF',
 ('-DVulkan_GLSLC_EXECUTABLE='+$sdk+'\bin\glslc.exe'),('-DVulkan_INCLUDE_DIR='+$sdk+'\include'),('-DVulkan_LIBRARY='+$sdk+'\lib\libvulkan-1.dll.a'),('-DSPIRV-Headers_DIR='+$sdk+'\share\cmake\SPIRV-Headers'))
$previous=@{};$settings=@{PATH=$toolBin+';'+$env:PATH;TEMP=(Join-Path $root '.local\temp');TMP=(Join-Path $root '.local\temp');GIT_CEILING_DIRECTORIES=(Split-Path -Parent $source)}
foreach($key in $settings.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
$started=[DateTimeOffset]::UtcNow
try{
 [Diagnostics.Process]::GetCurrentProcess().PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 & (Join-Path $toolBin 'cmake.exe') @arguments
 if($LASTEXITCODE -ne 0){throw 'Vulkan aligner configure failed'}
 & (Join-Path $toolBin 'cmake.exe') --build $build --target audiocpp_cli --parallel $Jobs
 if($LASTEXITCODE -ne 0){throw 'Vulkan aligner build failed'}
 $exe=Join-Path $build 'bin\audiocpp_cli.exe'
 $dependencies=@(& (Join-Path $toolBin 'objdump.exe') -p $exe|Select-String 'DLL Name:'|ForEach-Object {($_.Line-split'DLL Name:')[1].Trim()})
 [ordered]@{SourceVersion='audio.cpp-v0.9.0';SourceCommit='795c45fb';SourceArchiveSha256='ecf4c67e80545176e3603dc3c8f91834accaaff38ff5f3b3007208a1a286f6da';SourceDirectory=$source;ConfigureArguments=$arguments;LocalChanges=@('Preserved M4 raw-v1 no-interpolation patch','Preserved process-local UTF-8 manifest','Report actual initialized backend/device','Flush completed request marker after its output files');RawProcessorSha256=(Get-FileHash $processor).Hash.ToLowerInvariant();BackendDiagnosticSourceSha256=(Get-FileHash $contextFile).Hash.ToLowerInvariant();BatchResultSourceSha256=(Get-FileHash $sinkFile).Hash.ToLowerInvariant();Binary=$exe;BinarySha256=(Get-FileHash $exe).Hash.ToLowerInvariant();ImportedDlls=$dependencies;StartedUtc=$started.ToString('O');FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'build-provenance.json') -Encoding utf8
 Write-Output ('Built: '+$exe)
}finally{foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
