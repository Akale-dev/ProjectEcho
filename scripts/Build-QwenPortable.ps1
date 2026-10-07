[CmdletBinding()]
param([ValidateSet('Cpu','Vulkan')][string]$Backend='Cpu',[ValidateRange(1,4)][int]$Jobs=2)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source=Join-Path $root '.tools\transcribe-source'
$toolBin=Join-Path $root '.tools\vulkan-build\w64devkit\bin'
$sdk=Join-Path $root '.tools\vulkan-build\msys2\ucrt64'
$build=Join-Path $root ('.tools\qwen-'+$Backend.ToLowerInvariant())
$evidence=Join-Path $root '.local\M4\runtime'
$lockedCommit='077110eb00ca6880e13bef4ddebf766b3b76896c'
foreach($path in @($source,$toolBin,$sdk,$build,$evidence)){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse linked build path'}
 }
}
$actualCommit=(& git -C $source rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $actualCommit -ne $lockedCommit){throw 'Unexpected transcribe.cpp source revision'}
foreach($required in @((Join-Path $toolBin 'cmake.exe'),(Join-Path $toolBin 'g++.exe'),(Join-Path $source 'CMakeLists.txt'))){if(!(Test-Path -LiteralPath $required)){throw ('Missing reviewed build prerequisite: '+$required)}}
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
# Local UTF-8 argv/fopen support, no global locale or registry changes.
$manifest=Join-Path $PSScriptRoot 'whisper-portable.manifest'
$cliDirectory=Join-Path $source 'examples\cli'
Copy-Item -LiteralPath $manifest -Destination (Join-Path $cliDirectory 'ProjectEcho-portable.manifest') -Force
$manifestResourcePath=(Join-Path $cliDirectory 'ProjectEcho-portable.manifest').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $cliDirectory 'ProjectEcho-portable.rc'),'1 24 "'+$manifestResourcePath+'"'+[Environment]::NewLine)
$cliCMake=Join-Path $cliDirectory 'CMakeLists.txt'
$cliSource=Join-Path $cliDirectory 'main.cpp'
$cliText=[IO.File]::ReadAllText($cliSource)
if(!$cliText.Contains('projectecho-backend:')){
 # Batch JSON stdout stays machine-readable; the actual selected backend is
 # emitted on stderr after successful model load, from the upstream API.
 $loadEnd='        struct transcribe_session_params cp;'
 $at=$cliText.IndexOf($loadEnd,[StringComparison]::Ordinal)
 if($at -lt 0){throw 'Reviewed batch model-load insertion point changed'}
 $cliText=$cliText.Insert($at,'        std::fprintf(stderr, "projectecho-backend: %s\n", transcribe_model_backend(model));'+[Environment]::NewLine+[Environment]::NewLine)
 [IO.File]::WriteAllText($cliSource,$cliText,[Text.UTF8Encoding]::new($false))
}
if(![IO.File]::ReadAllText($cliCMake).Contains('# ProjectEcho portable UTF-8 manifest')){
 [IO.File]::AppendAllText($cliCMake,@'

# ProjectEcho portable UTF-8 manifest (build metadata only; no ASR algorithm change).
if(MINGW)
    enable_language(RC)
    target_sources(transcribe-cli PRIVATE ProjectEcho-portable.rc)
endif()
'@)
}
$arguments=@('-S',$source,'-B',$build,'-G','Ninja',('-DCMAKE_C_COMPILER='+$toolBin+'\gcc.exe'),('-DCMAKE_CXX_COMPILER='+$toolBin+'\g++.exe'),
 ('-DCMAKE_MAKE_PROGRAM='+$toolBin+'\ninja.exe'),'-DCMAKE_BUILD_TYPE=Release','-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
 '-DTRANSCRIBE_BUILD_SHARED=OFF','-DTRANSCRIBE_GGML_BACKEND_DL=OFF','-DCMAKE_EXE_LINKER_FLAGS=-static',
 '-DTRANSCRIBE_X86_CONSERVATIVE=ON','-DTRANSCRIBE_USE_OPENMP=OFF','-DTRANSCRIBE_USE_SYSTEM_BLAS=OFF',
 '-DGGML_CCACHE=OFF','-DTRANSCRIBE_BUILD_TESTS=OFF','-DTRANSCRIBE_BUILD_EXAMPLES=ON','-DTRANSCRIBE_BUILD_TOOLS=OFF',
 ('-DTRANSCRIBE_VULKAN='+$(if($Backend -eq 'Vulkan'){'ON'}else{'OFF'})))
if($Backend -eq 'Vulkan'){
 foreach($required in @('bin\glslc.exe','include\vulkan\vulkan.hpp','lib\libvulkan-1.dll.a','share\cmake\SPIRV-Headers\SPIRV-HeadersConfig.cmake')){
  if(!(Test-Path -LiteralPath (Join-Path $sdk $required))){throw ('Missing Vulkan build prerequisite: '+$required)}
 }
 $arguments+=@(('-DVulkan_GLSLC_EXECUTABLE='+$sdk+'\bin\glslc.exe'),('-DVulkan_INCLUDE_DIR='+$sdk+'\include'),('-DVulkan_LIBRARY='+$sdk+'\lib\libvulkan-1.dll.a'),('-DSPIRV-Headers_DIR='+$sdk+'\share\cmake\SPIRV-Headers'))
}
$previous=@{}; $settings=@{PATH=$toolBin+';'+$env:PATH;TEMP=(Join-Path $root '.local\temp');TMP=(Join-Path $root '.local\temp');CC=(Join-Path $toolBin 'gcc.exe');CXX=(Join-Path $toolBin 'g++.exe')}
foreach($key in $settings.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
$started=[DateTimeOffset]::UtcNow
try{
 [Diagnostics.Process]::GetCurrentProcess().PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 & (Join-Path $toolBin 'cmake.exe') @arguments
 if($LASTEXITCODE -ne 0){throw 'Qwen configure failed'}
 & (Join-Path $toolBin 'cmake.exe') --build $build --target transcribe-cli --parallel $Jobs
 if($LASTEXITCODE -ne 0){throw 'Qwen build failed'}
 $exe=Join-Path $build 'bin\transcribe-cli.exe'
 if(!(Test-Path -LiteralPath $exe)){throw 'Qwen CLI output missing'}
 $dependencies=@(& (Join-Path $toolBin 'objdump.exe') -p $exe | Select-String 'DLL Name:' | ForEach-Object {($_.Line -split 'DLL Name:')[1].Trim()})
 [ordered]@{Backend=$Backend;SourceRepository='https://github.com/handy-computer/transcribe.cpp';SourceCommit=$actualCommit;SourceLicense='MIT';SourceDirectory=$source;LocalChanges=@('UTF-8 executable manifest','Actual batch backend diagnostic on stderr');CliSourceSha256=(Get-FileHash $cliSource -Algorithm SHA256).Hash.ToLowerInvariant();ConfigureArguments=$arguments;Compiler='w64devkit2.9.1-GCC16.2.0';CMake='4.4.2';Ninja='1.13.2';CpuBaseline='Conservative baseline x64; no AVX/AVX2/FMA/F16C/BMI2/SSE4.2/native tuning';StaticCompilerRuntime=$true;WindowsManifest='UTF-8 activeCodePage; longPathAware; asInvoker';ManifestSha256=(Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant();OpenMp=$false;Binary=$exe;BinarySha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();ImportedDlls=$dependencies;StartedUtc=$started.ToString('O');FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence ('qwen-'+$Backend.ToLowerInvariant()+'-build.json')) -Encoding utf8
 Write-Output ('Built: '+$exe)
}finally{foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
