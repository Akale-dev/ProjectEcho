[CmdletBinding()]
param([string]$Destination='H:\AI\Qwen\python-aligner-runtime')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$destinationRoot=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if($destinationRoot -ne 'H:\AI\Qwen\python-aligner-runtime'){throw 'This installation is restricted to the explicitly authorized fresh external runtime folder'}
if(Test-Path -LiteralPath $destinationRoot){throw 'Refuse existing Python runtime overwrite'}
$pythonSource=Join-Path $root '.tools\python-3.12.10-nuget\tools'
$siteSource=Join-Path $root '.tools\qwen-official-venv\Lib\site-packages'
$evidence=Join-Path $root '.local\M4Perf\python-runtime'
$redist=Join-Path $evidence 'redist-x64-minimum'
foreach($path in @($destinationRoot,$pythonSource,$siteSource,$redist)){
 for($ancestor=$path;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
  if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse paths are not accepted'}
 }
}
$pythonSignature=Get-AuthenticodeSignature -LiteralPath (Join-Path $pythonSource 'python.exe')
if($pythonSignature.Status -ne 'Valid' -or $pythonSignature.SignerCertificate.Subject -notmatch 'Python Software Foundation'){throw 'Original official Python signature is not valid'}
$redistInfo=Get-Content -LiteralPath (Join-Path $evidence 'downloads\redist-provenance.json') -Raw|ConvertFrom-Json
if($redistInfo.SignatureStatus -ne 'Valid' -or $redistInfo.Sha256 -ne '843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c'){throw 'Unexpected Microsoft runtime source'}
$dlls=@(Get-ChildItem -LiteralPath $redist -File -Filter '*.dll_amd64')
if($dlls.Count -ne 12){throw 'Expected the reviewed x64 minimum VC payload'}
foreach($dll in $dlls){$signature=Get-AuthenticodeSignature -LiteralPath $dll.FullName;if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw ('Runtime DLL signature rejected: '+$dll.Name)}}
foreach($directory in @($pythonSource,$siteSource)){
 if(@(Get-ChildItem -LiteralPath $directory -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0){throw 'Source runtime contains links'}
}
# A full official base interpreter, not the venv launcher or its absolute home.
Copy-Item -LiteralPath $pythonSource -Destination $destinationRoot -Recurse
$site=Join-Path $destinationRoot 'Lib\site-packages'
New-Item -ItemType Directory -Path $site -Force|Out-Null
foreach($item in Get-ChildItem -LiteralPath $siteSource -Force){Copy-Item -LiteralPath $item.FullName -Destination $site -Recurse -Force}
if(Test-Path -LiteralPath (Join-Path $destinationRoot 'pyvenv.cfg')){throw 'A portable runtime must not retain a venv home stub'}
$runtimeDlls=@()
foreach($dll in $dlls){
 $name=$dll.Name.Substring(0,$dll.Name.Length-'_amd64'.Length);$target=Join-Path $destinationRoot $name
 Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
 $runtimeDlls+=@{File=$name;Sha256=(Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant();Version=$dll.VersionInfo.FileVersion}
}
[IO.File]::WriteAllText((Join-Path $destinationRoot 'python312._pth'),"python312.zip`n.`nDLLs`nLib`nLib\site-packages`nimport site`n",[Text.UTF8Encoding]::new($false))
$siteCustomize=@'
"""Process-local portable/offline defaults. No system Python or PATH changes."""
from pathlib import Path
import os
import sys
_root = Path(__file__).resolve().parents[2]
for _key in ("HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_DATASETS_OFFLINE", "HF_HUB_DISABLE_TELEMETRY", "HF_HUB_DISABLE_IMPLICIT_TOKEN", "DO_NOT_TRACK"):
    os.environ[_key] = "1"
os.environ["GRADIO_ANALYTICS_ENABLED"] = "False"
for _key in ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"):
    os.environ.pop(_key, None)
for _key, _folder in {"HF_HOME": "huggingface", "TORCH_HOME": "torch", "NUMBA_CACHE_DIR": "numba", "GRADIO_TEMP_DIR": "gradio"}.items():
    os.environ[_key] = str(_root / ".cache" / _folder)
for _stream in (sys.stdout, sys.stderr):
    if _stream is not None and hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="strict")
'@
[IO.File]::WriteAllText((Join-Path $site 'sitecustomize.py'),$siteCustomize,[Text.UTF8Encoding]::new($false))
foreach($name in @('huggingface','torch','numba','gradio')){New-Item -ItemType Directory -Path (Join-Path $destinationRoot ('.cache\'+$name)) -Force|Out-Null}
$licenses=Join-Path $destinationRoot 'PORTABLE-LICENSES';New-Item -ItemType Directory -Path $licenses|Out-Null
Copy-Item -LiteralPath (Join-Path $pythonSource 'LICENSE.txt') -Destination (Join-Path $licenses 'Python-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $evidence 'downloads\VC-LICENSE.html') -Destination (Join-Path $licenses 'Microsoft-VC-License.html')
Copy-Item -LiteralPath (Join-Path $root '.local\M4Perf\python\legacy-requirements-lock.txt') -Destination (Join-Path $destinationRoot 'requirements-lock.txt')
foreach($name in @('torch-install-report.json','qwen-install-report.json')){Copy-Item -LiteralPath (Join-Path $root ('.local\M4Perf\python\'+$name)) -Destination (Join-Path $destinationRoot $name)}
Copy-Item -LiteralPath (Join-Path $evidence 'downloads\redist-provenance.json') -Destination (Join-Path $destinationRoot 'vc-redist-provenance.json')
@'
# Optional, external Qwen official Python CPU runtime

This is a full local Python 3.12.10 interpreter plus the reviewed offline CPU
packages, without the venv launcher or an absolute pyvenv home. python312._pth
binds imports to this folder. Run python.exe -I -B with the application's reviewed
bridge script; no global Python, PATH, registry or administrator installation is
required. It is separate from the application ZIP and contains no Qwen model.

Process-local sitecustomize forces Hugging Face offline/telemetry-off settings,
uses only this folder's cache directories, disables implicit HF credentials,
and selects UTF-8 streams. Models must already exist at an explicit local path.

LOCAL-PATCHES records one explicit source patch: the Japanese-only nagisa import
is deferred to tokenize_japanese. Chinese/English tokenization, model computation,
and raw timestamp predictions remain unchanged. The original source, diff and
before/after hashes are retained. This is the official model/package with a
documented local loading patch, not an unmodified upstream runtime. Japanese
Unicode-path loading is not validated by this Chinese/English deployment.

LOCAL-PATCHES records one explicit source patch: the Japanese-only nagisa import
is deferred to tokenize_japanese. Chinese/English tokenization, model computation,
and raw timestamp predictions remain unchanged. The original source, diff and
before/after hashes are retained. This is the official model/package with a
documented local loading patch, not an unmodified upstream runtime. Japanese
Unicode-path loading is not validated by this Chinese/English deployment.

Python and wheel licenses remain with Python LICENSE.txt and each package's
dist-info/license resources. App-local VC DLLs were extracted from the signed
Microsoft VC v14 Redistributable 14.51.36247; no installer was executed. Its
license is in PORTABLE-LICENSES. Microsoft redistribution rules still apply;
this local setup does not grant additional redistribution rights.

This is not the old-CPU compatibility route. Selecting compatibility mode must
use the separately tested generic native engine rather than claim Torch can run
without optional SIMD. Clean-machine and other-CPU testing remain separate.
'@ | Set-Content -LiteralPath (Join-Path $destinationRoot 'PORTABLE-README.md') -Encoding utf8
[ordered]@{Schema='projectecho-official-python-portable-1';CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O');Destination=$destinationRoot;Python='3.12.10 official NuGet tools';PythonExeSha256=(Get-FileHash -LiteralPath (Join-Path $destinationRoot 'python.exe')).Hash.ToLowerInvariant();PythonSigner=$pythonSignature.SignerCertificate.Subject;PackagesSource=$siteSource;RequirementsLockSha256=(Get-FileHash -LiteralPath (Join-Path $destinationRoot 'requirements-lock.txt')).Hash.ToLowerInvariant();VcRuntimeDlls=$runtimeDlls;VcInstallerExecuted=$false;GlobalInstallationPerformed=$false;GlobalPathModified=$false;QwenModelsIncluded=$false;NormalRecordingExportRequiresPython=$false;RuntimeValidation='NOT_RUN until separate moved-path offline smoke';RedistributionNote='External user-initiated local configuration; retain upstream and Microsoft licensing terms.'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $destinationRoot 'portable-runtime.json') -Encoding utf8
Write-Output ('External runtime prepared for validation: '+$destinationRoot)
& (Join-Path $PSScriptRoot 'Patch-QwenOfficialPythonLazyJapanese.ps1') -RuntimeDirectory $destinationRoot
& (Join-Path $PSScriptRoot 'Patch-QwenOfficialPythonLazyJapanese.ps1') -RuntimeDirectory $destinationRoot
