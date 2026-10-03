[CmdletBinding()]
param([ValidateSet('Cpu','Vulkan')][string]$Backend, [string]$BuildName='', [int]$Jobs=2)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source=Join-Path $projectRoot '.tools\whisper-source\whisper.cpp-b5130'
$toolBin=Join-Path $projectRoot '.tools\vulkan-build\w64devkit\bin'
if(!$BuildName){$BuildName='whisper-b5130-'+$Backend.ToLowerInvariant()}
if($BuildName -notmatch '^whisper-b5130-[a-z0-9-]+$' -or $Jobs -lt 1 -or $Jobs -gt 4){throw 'Invalid local build name or bounded job count'}
$build=Join-Path $projectRoot ('.tools\'+$BuildName)
$evidence=Join-Path $projectRoot '.local\M3D-Vulkan'
foreach($required in @((Join-Path $toolBin 'cmake.exe'),(Join-Path $toolBin 'g++.exe'),(Join-Path $source 'CMakeLists.txt'))){if(!(Test-Path -LiteralPath $required)){throw ('Missing reviewed build prerequisite: '+$required)}}
foreach($path in @($source,$toolBin,$build)){
 for($ancestor=$path;$ancestor -and $ancestor.Length -ge $projectRoot.Length;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse linked build path'}
 }
}
$sdk=Join-Path $projectRoot '.tools\vulkan-build\msys2\ucrt64'
# Windows narrow argv and fopen must use UTF-8 even when the unpacked path or
# initial prompt contains Chinese. This is an application-local manifest, not
# a system locale/code-page change. No inference algorithm source is changed.
$manifest=Join-Path $PSScriptRoot 'whisper-portable.manifest'
$cliDirectory=Join-Path $source 'examples\cli'
Copy-Item -LiteralPath $manifest -Destination (Join-Path $cliDirectory 'ProjectEcho-portable.manifest') -Force
$manifestResourcePath=(Join-Path $cliDirectory 'ProjectEcho-portable.manifest').Replace('\','/')
[IO.File]::WriteAllText((Join-Path $cliDirectory 'ProjectEcho-portable.rc'), '1 24 "'+$manifestResourcePath+'"'+[Environment]::NewLine)
$cliCMake=Join-Path $cliDirectory 'CMakeLists.txt'
if(![IO.File]::ReadAllText($cliCMake).Contains('# ProjectEcho portable UTF-8 manifest')){
 [IO.File]::AppendAllText($cliCMake,@'

# ProjectEcho portable UTF-8 manifest (build metadata only; no ASR algorithm change).
if(MINGW)
    enable_language(RC)
    target_sources(whisper-cli PRIVATE ProjectEcho-portable.rc)
endif()
'@)
}
$arguments=@('-S',$source,'-B',$build,'-G','Ninja',('-DCMAKE_C_COMPILER='+$toolBin+'\gcc.exe'),('-DCMAKE_CXX_COMPILER='+$toolBin+'\g++.exe'),
 ('-DCMAKE_MAKE_PROGRAM='+$toolBin+'\ninja.exe'),'-DCMAKE_BUILD_TYPE=Release','-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
 '-DBUILD_SHARED_LIBS=OFF','-DGGML_STATIC=ON','-DCMAKE_EXE_LINKER_FLAGS=-static','-DGGML_NATIVE=OFF',
 '-DGGML_AVX=OFF','-DGGML_AVX2=OFF','-DGGML_FMA=OFF','-DGGML_F16C=OFF','-DGGML_AVX512=OFF','-DGGML_SSE42=OFF','-DGGML_BMI2=OFF',
 '-DGGML_OPENMP=OFF','-DGGML_OPENMP_FETCH=OFF','-DGGML_CCACHE=OFF','-DGGML_BACKEND_DL=OFF',
 '-DWHISPER_CURL=OFF','-DWHISPER_SDL2=OFF','-DWHISPER_BUILD_TESTS=OFF','-DWHISPER_BUILD_SERVER=OFF','-DWHISPER_BUILD_EXAMPLES=ON',
 ('-DGGML_VULKAN='+$(if($Backend -eq 'Vulkan'){'ON'}else{'OFF'})))
if($Backend -eq 'Vulkan'){
 foreach($required in @('bin\glslc.exe','include\vulkan\vulkan.hpp','lib\libvulkan-1.dll.a','share\cmake\SPIRV-Headers\SPIRV-HeadersConfig.cmake')){
  if(!(Test-Path -LiteralPath (Join-Path $sdk $required))){throw ('Missing Vulkan build prerequisite: '+$required)}
 }
 $arguments+=@(('-DVulkan_GLSLC_EXECUTABLE='+$sdk+'\bin\glslc.exe'),('-DVulkan_INCLUDE_DIR='+$sdk+'\include'),('-DVulkan_LIBRARY='+$sdk+'\lib\libvulkan-1.dll.a'),('-DSPIRV-Headers_DIR='+$sdk+'\share\cmake\SPIRV-Headers'))
}
$previous=@{}; $settings=@{PATH=$toolBin+';'+$env:PATH;TEMP=(Join-Path $projectRoot '.local\temp');TMP=(Join-Path $projectRoot '.local\temp');GIT_CEILING_DIRECTORIES=(Split-Path -Parent $source);CC=(Join-Path $toolBin 'gcc.exe');CXX=(Join-Path $toolBin 'g++.exe')}
foreach($key in $settings.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$settings[$key],'Process')}
$started=[DateTimeOffset]::UtcNow
try{
 [Diagnostics.Process]::GetCurrentProcess().PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
 & (Join-Path $toolBin 'cmake.exe') @arguments
 if($LASTEXITCODE -ne 0){throw 'Whisper configure failed'}
 & (Join-Path $toolBin 'cmake.exe') --build $build --target whisper-cli --parallel $Jobs
 if($LASTEXITCODE -ne 0){throw 'Whisper build failed'}
 $exe=Join-Path $build 'bin\whisper-cli.exe'
 if(!(Test-Path -LiteralPath $exe)){throw 'Whisper output missing'}
 @{Backend=$Backend;SourceTag='b5130';SourceCommit='927cfce34f31707e17f2bff35c349632fb9e2c3a';SourceArchiveSha256='b268c24559fa6256befe3341e0e1a6a4106bdb636aef84b37df872b081f39c21';SourceDirectory=$source;ConfigureArguments=$arguments;Gcc='w64devkit2.9.1-GCC16.2.0';CMake='4.4.2';Ninja='1.13.2';CpuBaseline='x64 without AVX/AVX2/FMA/F16C/SSE4.2/BMI2; native tuning disabled';StaticCompilerRuntime=$true;WindowsManifest='UTF-8 activeCodePage; longPathAware; asInvoker';ManifestSha256=(Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant();OpenMp=$false;Binary=$exe;BinarySha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();StartedUtc=$started.ToString('O');FinishedUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence ($BuildName+'-build.json')) -Encoding utf8
 Write-Output ('Built: '+$exe)
}finally{foreach($key in $settings.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
