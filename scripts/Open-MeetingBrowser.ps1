[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Chrome','Edge')][string]$Browser = 'Chrome',
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [string]$MeetingUrl = 'about:blank',
    [string]$BrowserPath = ''
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
if (-not [IO.Path]::IsPathRooted($DataRoot)) { throw 'DataRoot must be an absolute, dedicated data directory.' }
if ($DataRoot -match '["\r\n]' -or $MeetingUrl -match '["\r\n]') { throw 'Quotes or line breaks are not permitted in arguments.' }
$data = [IO.Path]::GetFullPath($DataRoot)
if ($data.TrimEnd('\') -eq [IO.Path]::GetPathRoot($data).TrimEnd('\')) { throw 'Choose a dedicated folder, not a drive root.' }
if ($MeetingUrl -ne 'about:blank') {
    $uri = $null
    if (-not [Uri]::TryCreate($MeetingUrl,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -ne 'https') { throw 'Only an HTTPS meeting address or about:blank is accepted.' }
}
$exe = $BrowserPath
if (-not $exe) {
    $relative = 'Google\Chrome\Application\chrome.exe'
    if ($Browser -eq 'Edge') { $relative = 'Microsoft\Edge\Application\msedge.exe' }
    foreach ($basePath in @($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA)) {
        if ($basePath) { $candidate = Join-Path $basePath $relative; if (Test-Path -LiteralPath $candidate -PathType Leaf) { $exe = $candidate; break } }
    }
}
if (-not $exe -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Browser not found. Supply BrowserPath for an approved installed/portable browser. This script does not download a browser.' }
$profile = Join-Path $data ('BrowserProfiles\Meetings\{0}' -f $Browser)
Write-Host 'This dedicated browser INSTANCE can include audio from every tab opened in it.'
Write-Host 'The profile may contain login information. Never include it in exports or Git.'
Write-Host 'No recording is started. Meeting addresses are not saved or printed by this script.'
if ($PSCmdlet.ShouldProcess($Browser,'Open dedicated meeting browser profile')) {
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    $arguments = @(( '--user-data-dir="{0}"' -f $profile),'--no-first-run','--new-window',('"{0}"' -f $MeetingUrl))
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    Write-Host ('Launch PID: {0}. This is NOT a verified audio root PID; the recorder must inspect the actual process/audio sessions.' -f $process.Id)
}
