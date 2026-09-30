# Developer-only, fixed-page loopback server. Does not launch a browser or start audio.
#Requires -Version 7.0
[CmdletBinding()]
param([ValidateRange(1, 60)][int]$DurationMinutes = 15)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-PlainPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths are not allowed.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-PlainPath $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'AGENTS.md') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $projectRoot 'ProjectEcho.slnx') -PathType Leaf)) {
    throw 'Run this script from the ProjectEcho scripts directory.'
}
$pagePath = Join-Path $projectRoot 'tests/audio_probe.html'
Assert-PlainPath $pagePath
$page = [IO.File]::ReadAllBytes($pagePath)
$html = [Text.Encoding]::UTF8.GetString($page)
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($page)).ToLowerInvariant()
function Inline-Hashes([string]$Tag) {
    $matches = [regex]::Matches($html, "(?is)<$Tag\b[^>]*>(.*?)</$Tag>")
    if ($matches.Count -ne 1) { throw "Expected exactly one inline $Tag block." }
    $inline = $matches[0].Groups[1].Value.Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($inline)
    return "'sha256-$([Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($bytes)))'"
}
$csp = "default-src 'none'; script-src $(Inline-Hashes 'script'); style-src $(Inline-Hashes 'style'); connect-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"
foreach ($directory in @((Join-Path $projectRoot '.local'), (Join-Path $projectRoot '.local/diagnostics'))) {
    if (-not (Test-Path -LiteralPath $directory)) { [void][IO.Directory]::CreateDirectory($directory) }
    Assert-PlainPath $directory
}
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try {
    $listener.Start(8)
    $port = $listener.LocalEndpoint.Port
    $expires = [DateTimeOffset]::UtcNow.AddMinutes($DurationMinutes)
    $url = "http://127.0.0.1:$port/audio_probe.html"
    $readyPath = Join-Path $projectRoot ".local/diagnostics/audio-probe-ready-$([guid]::NewGuid().ToString('N')).json"
    $ready = [ordered]@{ url = $url; processId = $PID; expiresAtUtc = $expires.ToString('O'); pageSha256 = $hash }
    $readyBytes = [Text.Encoding]::UTF8.GetBytes(($ready | ConvertTo-Json))
    $readyFile = [IO.File]::Open($readyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $readyFile.Write($readyBytes, 0, $readyBytes.Length) } finally { $readyFile.Dispose() }
    Write-Output "Ready: $readyPath"
    Write-Output "URL: $url"
    while ([DateTimeOffset]::UtcNow -lt $expires) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 50; continue }
        $client = $listener.AcceptTcpClient()
        try {
            if (-not [Net.IPAddress]::IsLoopback($client.Client.RemoteEndPoint.Address)) { continue }
            $stream = $client.GetStream()
            $stream.ReadTimeout = 2000
            $stream.WriteTimeout = 2000
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
            $buffer = [Collections.Generic.List[byte]]::new()
            $complete = $false
            while ($buffer.Count -lt 8192 -and [DateTimeOffset]::UtcNow -lt $deadline) {
                $stream.ReadTimeout = [Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
                $value = $stream.ReadByte()
                if ($value -lt 0) { break }
                $buffer.Add([byte]$value)
                $n = $buffer.Count
                if ($n -ge 4 -and $buffer[$n-4] -eq 13 -and $buffer[$n-3] -eq 10 -and
                    $buffer[$n-2] -eq 13 -and $buffer[$n-1] -eq 10) { $complete = $true; break }
            }
            $status = '400 Bad Request'
            $body = [Text.Encoding]::UTF8.GetBytes('Request rejected.')
            $type = 'text/plain; charset=utf-8'
            $headOnly = $false
            if ($complete) {
                $lines = [Text.Encoding]::ASCII.GetString($buffer.ToArray()) -split "`r`n"
                $request = [regex]::Match($lines[0], '^(GET|HEAD|[A-Z]+) ([^ ]+) HTTP/1\.[01]$')
                $hostLines = @($lines | Where-Object { $_ -match '(?i)^Host:' })
                $hostValid = $false
                # Header names are case-insensitive; require an exact numeric loopback authority.
                if ($hostLines.Count -eq 1) { $hostValid = $hostLines[0] -match "(?i)^Host:[ \t]*127\.0\.0\.1:$port[ \t]*$" }
                if ($request.Success -and $hostValid) {
                    $method = $request.Groups[1].Value
                    $headOnly = $method -eq 'HEAD'
                    if ($method -notin @('GET', 'HEAD')) { $status = '405 Method Not Allowed' }
                    elseif ($request.Groups[2].Value -cne '/audio_probe.html') { $status = '404 Not Found' }
                    else { $status = '200 OK'; $body = $page; $type = 'text/html; charset=utf-8' }
                }
            } elseif ($buffer.Count -ge 8192) { $status = '431 Request Header Fields Too Large' }
            $headers = "HTTP/1.1 $status`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nConnection: close`r`nCache-Control: no-store`r`nX-Content-Type-Options: nosniff`r`nPermissions-Policy: microphone=(), camera=(), display-capture=(), geolocation=()`r`nContent-Security-Policy: $csp`r`n"
            if ($status.StartsWith('405')) { $headers += "Allow: GET, HEAD`r`n" }
            $headerBytes = [Text.Encoding]::ASCII.GetBytes($headers + "`r`n")
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            if (-not $headOnly) { $stream.Write($body, 0, $body.Length) }
        } catch [IO.IOException] {
            # Timeout/disconnect: close without persisting request contents.
        } catch [Net.Sockets.SocketException] {
            # A disconnected local client must not extend the server lifetime.
        } finally { $client.Dispose() }
    }
} finally { $listener.Stop() }
