[CmdletBinding()]
param([string]$Destination = 'H:\AI\Whisper')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Explicit developer setup only. The application never invokes this downloader.
$destinationRoot = [IO.Path]::GetFullPath($Destination)
if ($destinationRoot -ne 'H:\AI\Whisper') { throw 'This reviewed setup is scoped to the user-authorized H:\AI\Whisper directory.' }
for ($ancestor = $destinationRoot; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked setup directories are not allowed.' }
}
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
foreach ($name in @('downloads','models','samples','licenses')) { New-Item -ItemType Directory -Path (Join-Path $destinationRoot $name) -Force | Out-Null }
function Get-CheckedFile([string]$Url, [string]$Path, [string]$Hash = '') {
    if (Test-Path -LiteralPath $Path) {
        if (-not $Hash -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash) { return }
        throw ('Existing file differs from expected checksum: ' + $Path)
    }
    $partial = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.download'
    & curl.exe --fail --location --retry 3 --connect-timeout 20 --max-time 600 --silent --show-error --output $partial $Url
    if ($LASTEXITCODE -ne 0) { throw ('Download failed; partial evidence preserved: ' + $Url) }
    if ($Hash -and (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) { throw ('SHA-256 mismatch; not installed: ' + $Url) }
    Move-Item -LiteralPath $partial -Destination $Path
}
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/whisper.cpp/releases/tags/b5130'
$asset = $release.assets | Where-Object name -eq 'whisper-bin-x64.zip'
$archiveHash = 'f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c'
if ($asset.digest -ne ('sha256:' + $archiveHash)) { throw 'Official release metadata changed; review before installing.' }
$modelHash = '60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe'
$modelInfo = Invoke-RestMethod -Uri 'https://huggingface.co/api/models/ggerganov/whisper.cpp'
$modelUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/' + $modelInfo.sha + '/ggml-base.bin'
$archive = Join-Path $destinationRoot 'downloads\whisper-b5130-bin-x64.zip'
Get-CheckedFile $asset.browser_download_url $archive $archiveHash
$cliDirectory = Join-Path $destinationRoot 'whisper-b5130'
if (-not (Test-Path -LiteralPath $cliDirectory)) {
    # Validate archive paths before extraction. Never extract over an existing installation.
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try { foreach ($entry in $zip.Entries) {
        $entryPath = [IO.Path]::GetFullPath((Join-Path $cliDirectory $entry.FullName))
        if (-not $entryPath.StartsWith($cliDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive entry.' }
    } } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $cliDirectory
}
Get-CheckedFile $modelUrl (Join-Path $destinationRoot 'models\ggml-base.bin') $modelHash
Get-CheckedFile 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/b5130/samples/jfk.wav' (Join-Path $destinationRoot 'samples\jfk.wav')
Get-CheckedFile 'https://raw.githubusercontent.com/ggml-org/whisper.cpp/b5130/LICENSE' (Join-Path $destinationRoot 'licenses\whisper.cpp-LICENSE.txt')
Get-CheckedFile 'https://raw.githubusercontent.com/openai/whisper/main/LICENSE' (Join-Path $destinationRoot 'licenses\Whisper-model-LICENSE.txt')
Get-CheckedFile ('https://huggingface.co/ggerganov/whisper.cpp/raw/' + $modelInfo.sha + '/README.md') (Join-Path $destinationRoot 'licenses\model-card.md')
$executables = @(Get-ChildItem -LiteralPath $cliDirectory -Recurse -File -Filter whisper-cli.exe)
if ($executables.Count -ne 1) { throw 'Expected exactly one whisper-cli.exe in official CPU archive.' }
$configuration = [ordered]@{
    ExecutablePath=$executables[0].FullName; ModelPath=(Join-Path $destinationRoot 'models\ggml-base.bin')
    Language='auto'; Threads=2; ModelName='whisper.cpp base (multilingual)'; ExpectedModelSha256=$modelHash; ModelLicenseAcknowledged=$true
}
$configuration | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destinationRoot 'projectecho-whisper.json') -Encoding utf8
$inventory = @(Get-ChildItem -LiteralPath $cliDirectory -File -Recurse | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($destinationRoot,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{
    installedAt=[DateTimeOffset]::UtcNow.ToString('o'); sourceMode='PUBLIC FILES / NO CAPTURE / NO UPLOAD'
    release=$release.html_url; tag=$release.tag_name; releasePublishedAt=$release.published_at
    archive=@{url=$asset.browser_download_url;bytes=$asset.size;sha256=$archiveHash;upstreamDigestVerified=$true}
    model=@{name='base';multilingual=$true;revision=$modelInfo.sha;url=$modelUrl;bytes=(Get-Item -LiteralPath $configuration.ModelPath).Length;sha256=$modelHash;license='MIT'}
    sample=@{url='https://raw.githubusercontent.com/ggml-org/whisper.cpp/b5130/samples/jfk.wav';sha256=(Get-FileHash -LiteralPath (Join-Path $destinationRoot 'samples\jfk.wav') -Algorithm SHA256).Hash.ToLowerInvariant();description='Official whisper.cpp public JFK speech test sample'}
    files=$inventory
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destinationRoot 'provenance.json') -Encoding utf8
Write-Output ('CLI: ' + $configuration.ExecutablePath)
Write-Output ('Model: ' + $configuration.ModelPath)
Write-Output 'Official archive and multilingual base SHA-256 verified. No global installation or PATH changes.'
