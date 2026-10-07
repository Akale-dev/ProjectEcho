[CmdletBinding()]
param([string]$OutputDirectory='.local/M5/sherpa-bundle')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(!$output.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output must stay inside ProjectEcho.'}
for($ancestor=$output;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
 if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Refuse reparse bundle directory.'}
}
if(Test-Path -LiteralPath $output){throw 'Use a new bundle output directory.'}
& (Join-Path $PSScriptRoot 'Setup-M5SherpaDependencies.ps1')
$bin=Join-Path $root '.local/M5/runtime/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release-no-tts/bin'
$provenance=Join-Path $root '.local/M5/provenance'
$toolsDir=Join-Path $output 'Tools/Sherpa';$models=Join-Path $output 'Models/Sherpa';$licenses=Join-Path $output 'LICENSES/Sherpa'
New-Item -ItemType Directory -Force $toolsDir,$models,$licenses | Out-Null
foreach($file in @('sherpa-onnx-offline-speaker-diarization.exe','onnxruntime.dll','onnxruntime_providers_shared.dll')){Copy-Item -LiteralPath (Join-Path $bin $file) -Destination $toolsDir}
Copy-Item -LiteralPath (Join-Path $root '.local/M5/runtime/sherpa-onnx-pyannote-segmentation-3-0/model.onnx') -Destination (Join-Path $models 'pyannote-segmentation-3.0.onnx')
Copy-Item -LiteralPath (Join-Path $root '.local/M5/downloads/embedding.onnx') -Destination (Join-Path $models '3dspeaker-eres2net.onnx')
foreach($file in @('3dspeaker-LICENSE.txt','eres2net-model-card.md','onnxruntime-LICENSE.txt','onnxruntime-ThirdPartyNotices.txt','hclust-cpp-LICENSE.txt','kaldi-native-fbank-LICENSE.txt','eigen-COPYING.MPL2.txt','json-v3.12.0-LICENSE.txt','kissfft-COPYING.txt','kissfft-BSD-3-Clause.txt')){Copy-Item -LiteralPath (Join-Path $provenance $file) -Destination $licenses}
Copy-Item -LiteralPath (Join-Path $provenance 'LICENSE') -Destination (Join-Path $licenses 'sherpa-onnx-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root '.local/M5/runtime/sherpa-onnx-pyannote-segmentation-3-0/LICENSE') -Destination (Join-Path $licenses 'pyannote-LICENSE.txt')
# The official narrow-char CLI loses non-ACP path characters. Change only the copied PE manifest,
# leaving upstream runtime/model files and the original downloaded executable intact.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class M5Manifest {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr BeginUpdateResource(string path,bool deleteExisting);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateResource(IntPtr update,IntPtr type,IntPtr name,ushort language,byte[] bytes,uint count);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool EndUpdateResource(IntPtr update,bool discard);
 public static void Apply(string path,byte[] bytes){var h=BeginUpdateResource(path,false);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();if(!UpdateResource(h,new IntPtr(24),new IntPtr(1),1033,bytes,(uint)bytes.Length)){EndUpdateResource(h,true);throw new System.ComponentModel.Win32Exception();}if(!EndUpdateResource(h,false))throw new System.ComponentModel.Win32Exception();}
}
'@
$manifest=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'whisper-portable.manifest')).Replace('ProjectEcho.WhisperPortable','ProjectEcho.SherpaPortable')
$utf8=[Text.UTF8Encoding]::new($false)
$exe=Join-Path $toolsDir 'sherpa-onnx-offline-speaker-diarization.exe'
[M5Manifest]::Apply($exe,$utf8.GetBytes($manifest))
[IO.File]::WriteAllText((Join-Path $licenses 'ProjectEcho-Sherpa.manifest'),$manifest,$utf8)
function Hash($path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
$config=@{Schema='m5-bundled-sherpa-1';RuntimeVersion='1.13.8';RuntimeCommit='11afbd00';OnnxRuntimeVersion='1.28.2';Hardware='CPU';Executable='Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe';SegmentationModel='Models/Sherpa/pyannote-segmentation-3.0.onnx';EmbeddingModel='Models/Sherpa/3dspeaker-eres2net.onnx';Threads=2;ClusteringThreshold=0.9;KnownSpeakerCount=$null;OriginalExecutableSha256=(Hash (Join-Path $bin 'sherpa-onnx-offline-speaker-diarization.exe'));ExecutableSha256=(Hash $exe);SegmentationSha256=(Hash (Join-Path $models 'pyannote-segmentation-3.0.onnx'));EmbeddingSha256=(Hash (Join-Path $models '3dspeaker-eres2net.onnx'));Modification='Only PE manifest: per-process UTF-8 code page, longPathAware, asInvoker; no global changes';Source='https://github.com/k2-fsa/sherpa-onnx/releases/tag/v1.13.8';ModelProvenance='https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html';ResourceEvidence='M5 three-hour constructed real-voice test; not natural meeting accuracy';NoTestAudioBundled=$true}
[IO.File]::WriteAllText((Join-Path $output 'bundled-sherpa.json'),($config|ConvertTo-Json -Depth 10),$utf8)
$notice=@{Schema='m5-third-party-1';Components=@(@{Name='sherpa-onnx';Version='1.13.8';License='Apache-2.0';Source=$config.Source},@{Name='ONNX Runtime';Version='1.28.2';License='MIT plus bundled third-party notices';Source='https://github.com/microsoft/onnxruntime/tree/v1.28.2'},@{Name='Pyannote segmentation 3.0 ONNX';License='MIT';Source='https://github.com/k2-fsa/sherpa-onnx/releases/tag/speaker-segmentation-models'},@{Name='3D-Speaker ERes2Net base zh-cn 16k ONNX';License='Apache-2.0';Source='https://modelscope.cn/models/iic/speech_eres2net_base_sv_zh-cn_3dspeaker_16k'},@{Name='hclust-cpp';Version='2026-02-25';License='BSD';Source='https://github.com/csukuangfj/hclust-cpp/tree/2026-02-25'},@{Name='kaldi-native-fbank';Version='1.22.3';License='Apache-2.0';Source='https://github.com/csukuangfj/kaldi-native-fbank/tree/v1.22.3'},@{Name='kissfft';Version='febd4caeed32e33ad8b2e0bb5ea77542c40f18ec';License='BSD-3-Clause';Source='https://github.com/mborgerding/kissfft/tree/febd4caeed32e33ad8b2e0bb5ea77542c40f18ec'},@{Name='Eigen';Version='5.0.1';License='MPL-2.0';Source='https://gitlab.com/libeigen/eigen/-/tree/5.0.1'},@{Name='nlohmann/json';Version='3.12.0';License='MIT';Source='https://github.com/nlohmann/json/tree/v3.12.0'});Files=@(Get-ChildItem -LiteralPath $output -Recurse -File|ForEach-Object {@{Path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');Bytes=$_.Length;Sha256=(Hash $_.FullName)}})}
[IO.File]::WriteAllText((Join-Path $output 'THIRD_PARTY.sherpa.json'),($notice|ConvertTo-Json -Depth 10),$utf8)
Write-Output $output

