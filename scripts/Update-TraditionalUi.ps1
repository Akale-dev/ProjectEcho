[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$resourceDirectory = Join-Path $root 'src/ProjectEcho.App/Localization'
# Windows NLS converts interface templates only. Never pass user meeting or transcript text here.
if (-not ('ProjectEchoUiNls' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProjectEchoUiNls {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern int LCMapStringEx(string locale, uint flags, string source, int length,
    IntPtr destination, int capacity, IntPtr version, IntPtr reserved, IntPtr handle);
  public static string Traditional(string source) {
    int capacity = source.Length * 3 + 16;
    IntPtr output = Marshal.AllocHGlobal(capacity * sizeof(char));
    try {
      int result = LCMapStringEx("zh-TW", 0x04000000, source, source.Length,
        output, capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
      if (result == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
      return Marshal.PtrToStringUni(output, result);
    } finally { Marshal.FreeHGlobal(output); }
  }
}
'@
}
$source = Get-Content -LiteralPath (Join-Path $resourceDirectory 'zh-CN.json') -Raw | ConvertFrom-Json -AsHashtable
$result = [ordered]@{}
foreach ($key in ($source.Keys | Sort-Object -CaseSensitive)) { $result[$key] = [ProjectEchoUiNls]::Traditional($key) }
$result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $resourceDirectory 'zh-Hant.json') -Encoding utf8
Write-Host ('Traditional interface templates: ' + $result.Count)
