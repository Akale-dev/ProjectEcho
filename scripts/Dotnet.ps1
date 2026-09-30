[CmdletBinding()]
param(
    [string]$DotnetPath = '',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dotnet = $DotnetPath
if (-not $dotnet) {
    $localSdk = Join-Path $root '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk -PathType Leaf) { $dotnet = $localSdk }
    else {
        $installed = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($installed) { $dotnet = $installed.Source }
    }
}
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'No SDK launcher found. Install the approved SDK in .tools\dotnet or supply -DotnetPath; no download is automatic.'
}
$dotnet = (Resolve-Path -LiteralPath $dotnet).Path
$settings = @{
    DOTNET_ROOT = (Split-Path -Parent $dotnet)
    DOTNET_CLI_HOME = (Join-Path $root '.local\dotnet-home')
    NUGET_PACKAGES = (Join-Path $root '.local\nuget-packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $root '.local\nuget-http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $root '.local\nuget-plugin-cache')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    DOTNET_NOLOGO = '1'
    MSBUILDDISABLENODEREUSE = '1'
    TEMP = (Join-Path $root '.local\temp')
    TMP = (Join-Path $root '.local\temp')
}
New-Item -ItemType Directory -Path $settings.TEMP -Force | Out-Null
$previous = @{}
foreach ($key in $settings.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}
Push-Location $root
try {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet exited with code {0}' -f $LASTEXITCODE) }
} finally {
    Pop-Location
    foreach ($key in $settings.Keys) {
        if ($null -eq $previous[$key]) {
            Remove-Item -LiteralPath ('Env:\' + $key) -ErrorAction SilentlyContinue
        } else {
            [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
        }
    }
}
