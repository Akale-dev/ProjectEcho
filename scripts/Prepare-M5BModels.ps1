[CmdletBinding()]
param(
    [string]$OutputDirectory = '.local/M5B/sherpa-bundle',
    [string]$BaseBundleDirectory = '.local/M5/sherpa-bundle-03',
    [string]$CacheDirectory = '.local/M5B',
    [switch]$Download
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$localRoot = [IO.Path]::GetFullPath((Join-Path $root '.local'))
$utf8 = [Text.UTF8Encoding]::new($false)
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Safe-Path([string]$parent, [string]$relative) {
    if ([IO.Path]::IsPathRooted($relative)) { throw 'Relative project-local paths are required.' }
    $path = [IO.Path]::GetFullPath((Join-Path $parent $relative))
    if (!$path.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes its declared root.' }
    for ($ancestor = $path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refuse reparse paths.' }
    }
    return $path
}
function Local-Path([string]$relative) {
    $path = Safe-Path $root $relative
    if (!$path.StartsWith($localRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Model caches and overlays must stay under project .local.' }
    return $path
}
function Copy-Verified([string]$source, [string]$destination, [string]$sha) {
    if (!(Test-Path -LiteralPath $source -PathType Leaf) -or (Hash $source) -ne $sha) { throw "Missing or hash-mismatched public input: $source" }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    if ((Hash $destination) -ne $sha) { throw "Copy verification failed: $destination" }
}
$output = Local-Path $OutputDirectory
$base = Local-Path $BaseBundleDirectory
$cache = Local-Path $CacheDirectory
if (Test-Path -LiteralPath $output) { throw 'Use a new overlay output directory; prior evidence is never overwritten.' }
$lockPath = Join-Path $PSScriptRoot 'm5b-model-sources.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$baseNoticePath = Safe-Path $base 'THIRD_PARTY.sherpa.json'
if ((Hash $baseNoticePath) -ne $lock.BaseBundleNoticeSha256) { throw 'Base bundle notice does not match the validated M5 overlay.' }
$baseNotice = Get-Content -LiteralPath $baseNoticePath -Raw | ConvertFrom-Json
foreach ($file in $baseNotice.Files) {
    if ($file.Path -ne 'bundled-sherpa.json' -and $file.Path -notmatch '^(Tools|Models|LICENSES)/Sherpa/[^/]+$') { throw 'Unexpected base-bundle file scope.' }
    $source = Safe-Path $base $file.Path
    if ((Hash $source) -ne $file.Sha256 -or (Get-Item -LiteralPath $source).Length -ne $file.Bytes) { throw "Base public file verification failed: $($file.Path)" }
}
foreach ($file in $lock.Downloads) {
    $source = Safe-Path $cache $file.Path
    if (!(Test-Path -LiteralPath $source)) {
        if (!$Download) { throw "Missing $($file.Path); developer may explicitly use -Download to fetch the pinned public source." }
        if ([Uri]$file.Url -isnot [Uri] -or ([Uri]$file.Url).Scheme -ne 'https') { throw 'Only pinned HTTPS public sources are allowed.' }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($source)) -Force | Out-Null
        # No shell, upload, authentication, global environment change or automatic recording-time download.
        Invoke-WebRequest -Uri $file.Url -OutFile $source
    }
    if ((Hash $source) -ne $file.Sha256) { throw "Public cached input hash mismatch: $($file.Path)" }
    if ($file.PSObject.Properties.Name -contains 'Bytes' -and (Get-Item -LiteralPath $source).Length -ne $file.Bytes) { throw 'Public input length mismatch.' }
}
New-Item -ItemType Directory -Path $output | Out-Null
foreach ($file in $baseNotice.Files) { Copy-Verified (Safe-Path $base $file.Path) (Safe-Path $output $file.Path) $file.Sha256 }
Copy-Verified $baseNoticePath (Safe-Path $output 'LICENSES/Sherpa/THIRD_PARTY.sherpa-M5-original.json') $lock.BaseBundleNoticeSha256
Copy-Verified (Safe-Path $base 'bundled-sherpa.json') (Safe-Path $output 'LICENSES/Sherpa/bundled-sherpa-M5-original.json') (Hash (Safe-Path $base 'bundled-sherpa.json'))
foreach ($file in $lock.Downloads) { Copy-Verified (Safe-Path $cache $file.Path) (Safe-Path $output $file.BundlePath) $file.Sha256 }
Copy-Verified $lockPath (Safe-Path $output 'LICENSES/Sherpa/m5b-model-sources.json') (Hash $lockPath)
$attribution = @(
    '# NVIDIA NeMo TitaNet-Large ONNX attribution', '',
    "Model: $($lock.Model.Name)", "Original model author: $($lock.Model.Author)",
    "Model license: $($lock.Model.License) ($($lock.Model.LicenseUrl))", "Original model and model card: $($lock.Model.Source)",
    "ONNX distribution: $($lock.Model.ConversionRelease)", "Conversion source reference: $($lock.Model.ConversionSource)",
    "Conversion code: $($lock.Model.ConversionCopyright); $($lock.Model.ConversionSourceLicense). See sherpa-onnx-LICENSE.txt.",
    '', $lock.Model.Changes, $lock.Model.Endorsement, '',
    'The original model card, full CC-BY-4.0 text, conversion source and its Apache-2.0 license are included alongside this notice.',
    'No user data, test audio, transcript or extracted identity features are distributed.',
    'The ONNX model requires only the bundled native CPU runtime at use time; Python is not a product dependency.'
) -join "`n"
[IO.File]::WriteAllText((Safe-Path $output 'LICENSES/Sherpa/TitaNet-ATTRIBUTION.md'), $attribution + "`n", $utf8)
$config = Get-Content -LiteralPath (Safe-Path $output 'bundled-sherpa.json') -Raw | ConvertFrom-Json -AsHashtable
$config.Schema = 'm5b-bundled-sherpa-2'
$config.EmbeddingModel = $lock.DefaultEmbeddingModel
$config.EmbeddingSha256 = Hash (Safe-Path $output $lock.DefaultEmbeddingModel)
$config.LegacyEmbeddingModel = $lock.LegacyEmbeddingModel
$config.LegacyEmbeddingSha256 = Hash (Safe-Path $output $lock.LegacyEmbeddingModel)
$config.ModelPresets = @(
    [ordered]@{ Id='titanet-large'; Name='NVIDIA TitaNet-Large'; Path=$lock.DefaultEmbeddingModel; Sha256=$config.EmbeddingSha256; License='CC-BY-4.0'; DefaultForNewBundledPreset=$true },
    [ordered]@{ Id='eres2net'; Name='3D-Speaker ERes2Net zh-cn'; Path=$lock.LegacyEmbeddingModel; Sha256=$config.LegacyEmbeddingSha256; License='Apache-2.0'; DefaultForNewBundledPreset=$false }
)
$config.ExistingConfigurationPolicy = 'Preserve existing meeting and user paths/preferences; model changes require explicit selection; never replace prior analysis or manual revisions.'
$config.ResourceEvidence = 'ERes2Net: prior M5 three-hour constructed test. TitaNet: M5B public 65s and 1205s whole-file CPU runs; three-hour resource and natural full-meeting accuracy NOT_RUN.'
$config.ModelProvenance = $lock.Model.Source
[IO.File]::WriteAllText((Safe-Path $output 'bundled-sherpa.json'), ($config | ConvertTo-Json -Depth 12), $utf8)
$components = @($baseNotice.Components) + @([ordered]@{Name=$lock.Model.Name;Author=$lock.Model.Author;License=$lock.Model.License;Source=$lock.Model.Source;ConversionSource=$lock.Model.ConversionSource;Modification=$lock.Model.Changes})
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {[ordered]@{Path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');Bytes=$_.Length;Sha256=(Hash $_.FullName)}})
$notice = [ordered]@{Schema='m5b-third-party-2';BaseNoticeSha256=$lock.BaseBundleNoticeSha256;ModelSourceManifestSha256=(Hash $lockPath);Components=$components;Files=$files;NoTestAudioBundled=$true;NoUserDataBundled=$true}
[IO.File]::WriteAllText((Safe-Path $output 'THIRD_PARTY.sherpa.json'), ($notice | ConvertTo-Json -Depth 14), $utf8)
# Verify the final named closure; metadata has deliberately no self-referential checksum.
foreach ($file in $files) { if ((Hash (Safe-Path $output $file.Path)) -ne $file.Sha256) { throw 'Final public overlay hash verification failed.' } }
[PSCustomObject]@{Output=$output;FileCount=$files.Count+1;Bytes=(Get-ChildItem -LiteralPath $output -Recurse -File|Measure-Object Length -Sum).Sum;NoticeSha256=(Hash (Safe-Path $output 'THIRD_PARTY.sherpa.json'));DefaultEmbedding=$config.EmbeddingModel;PriorSettingsPreserved=$true}
