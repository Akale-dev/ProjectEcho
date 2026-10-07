[CmdletBinding()]
param([string]$RuntimeDirectory='H:\AI\Qwen\python-aligner-runtime')
$ErrorActionPreference='Stop'
$runtime=[IO.Path]::GetFullPath($RuntimeDirectory).TrimEnd('\')
if($runtime -ne 'H:\AI\Qwen\python-aligner-runtime'){throw 'Only the authorized new external runtime may be patched'}
$source=Join-Path $runtime 'Lib\site-packages\qwen_asr\inference\qwen3_forced_aligner.py'
for($ancestor=$source;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
    if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked runtime rejected'}
}
$before=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
if($before -ne 'fb586127507c5980d11c6d848b530e652afad3ae0707cba6eb0c90377b2a2111'){throw 'Official source differs from the reviewed qwen-asr 0.0.6 file; no patch applied'}
$patchDirectory=Join-Path $runtime 'LOCAL-PATCHES'
if(Test-Path -LiteralPath $patchDirectory){throw 'Preserve existing patch provenance; do not overwrite'}
New-Item -ItemType Directory -Path $patchDirectory|Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $patchDirectory 'qwen3_forced_aligner.py.original')
$text=[IO.File]::ReadAllText($source)
$newline=if($text.Contains("`r`n")){"`r`n"}else{"`n"}
$needle='    def tokenize_japanese(self, text: str) -> List[str]:'+$newline
if(([regex]::Matches($text,'(?m)^import nagisa\r?$')).Count -ne 1 -or !$text.Contains($needle)){throw 'Expected patch sites absent'}
$text=$text.Replace('import nagisa'+$newline,'').Replace($needle,$needle+'        # ProjectEcho: load the Japanese-only dependency only for Japanese text.'+$newline+'        import nagisa'+$newline)
[IO.File]::WriteAllText($source,$text,[Text.UTF8Encoding]::new($false))
$after=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
@'
--- qwen_asr/inference/qwen3_forced_aligner.py (qwen-asr 0.0.6 original)
+++ qwen_asr/inference/qwen3_forced_aligner.py (ProjectEcho local patch)
@@
-import nagisa
 import torch
@@
     def tokenize_japanese(self, text: str) -> List[str]:
+        # ProjectEcho: load the Japanese-only dependency only for Japanese text.
+        import nagisa
         words = nagisa.tagging(text).words
'@ | Set-Content -LiteralPath (Join-Path $patchDirectory 'lazy-japanese-import.diff') -Encoding utf8
[ordered]@{
    Schema='projectecho-qwen-official-local-patch-1';CreatedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    File='Lib/site-packages/qwen_asr/inference/qwen3_forced_aligner.py';OriginalSha256=$before;PatchedSha256=$after
    UpstreamPackage='qwen-asr 0.0.6';UpstreamSourceCommit='7c6daf77a2421100f5fb066495372c00129d39ff'
    Change='Move nagisa import into tokenize_japanese, retaining all Chinese/English tokenization, model computation and raw timestamps unchanged.'
    Reason='nagisa eagerly loads a Japanese model through a narrow Windows file API and fails under non-ASCII runtime paths even for Chinese/English input.'
    Scope='Only this external optional Python runtime; original comparison venv, original source, native runtimes and old delivered packages remain unchanged.'
    JapaneseUnicodeRuntime='NOT_RUN; this patch postpones Japanese loading and does not repair its Windows non-ASCII file API.'
    Validation='NOT_RUN until separate real-model smoke'
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $patchDirectory 'provenance.json') -Encoding utf8
Write-Output ('Patched optional runtime Japanese import: '+$after)
