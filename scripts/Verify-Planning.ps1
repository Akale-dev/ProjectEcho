#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Push-Location $root
try {
    $requirements = @(Import-Csv -LiteralPath 'REQUIREMENTS.csv')
    $acceptance = @(Import-Csv -LiteralPath 'tests\acceptance.csv')
    $testIds = @($acceptance.test_id)
    if (@($testIds | Select-Object -Unique).Count -ne $testIds.Count) { throw 'Duplicate test IDs.' }
    if (@($requirements.requirement_id | Select-Object -Unique).Count -ne $requirements.Count) { throw 'Duplicate requirement IDs.' }
    foreach ($requirement in $requirements) {
        foreach ($testId in ($requirement.test_ids -split ';')) {
            if ($testId -notin $testIds) { throw "Unknown test reference: $testId" }
        }
    }
    if ([IO.File]::ReadAllText((Join-Path $root 'FIRST_PROMPT.txt')) -cne
        [IO.File]::ReadAllText((Join-Path $root 'prompts\01_START_M0.txt'))) {
        throw 'FIRST_PROMPT and M0 prompt differ.'
    }
    $jsonFiles = @(Get-ChildItem -LiteralPath 'schemas','examples' -Filter '*.json' -File)
    $jsonFiles += @(Get-Item -LiteralPath 'global.json','tools\dependencies.json','handoff-manifest.json')
    foreach ($file in $jsonFiles) {
        if (-not (Test-Json -Json (Get-Content -LiteralPath $file.FullName -Raw))) { throw "Invalid JSON: $($file.Name)" }
    }
    $pairs = @(
        @('meeting.synthetic.json', 'meeting.schema.json'),
        @('export.synthetic.json', 'export-manifest.schema.json'),
        @('transcript.synthetic.json', 'transcript.schema.json'),
        @('analysis.synthetic.json', 'analysis.schema.json')
    )
    foreach ($pair in $pairs) {
        $examplePath = Join-Path 'examples' $pair[0]
        $schemaPath = Join-Path 'schemas' $pair[1]
        if (-not (Test-Json -Json (Get-Content -LiteralPath $examplePath -Raw) -SchemaFile $schemaPath)) {
            throw "Example does not match schema: $examplePath"
        }
    }
    $indexLineCount = 0
    foreach ($line in (Get-Content -LiteralPath 'examples\capture-index.synthetic.jsonl')) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $indexLineCount++
        if (-not (Test-Json -Json $line -SchemaFile 'schemas\capture-index-record.schema.json')) {
            throw "Capture index record $indexLineCount does not match schema."
        }
    }
    $scriptFiles = @(Get-ChildItem -LiteralPath 'scripts' -Filter '*.ps1' -File)
    foreach ($file in $scriptFiles) {
        $scriptTokens = $null
        $scriptErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$scriptTokens, [ref]$scriptErrors)
        if ($scriptErrors.Count -gt 0) { throw "PowerShell parse failed: $($file.Name): $scriptErrors" }
    }
    $report = [ordered]@{
        checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        scope = 'Planning structure only; no product/audio acceptance'
        requirementCount = $requirements.Count
        testCount = $acceptance.Count
        notRunCount = @($acceptance | Where-Object status -eq 'NOT_RUN').Count
        validRequirementReferences = $true
        m0PromptCopiesIdentical = $true
        jsonSyntaxFileCount = $jsonFiles.Count
        schemaExamplePairCount = $pairs.Count
        captureIndexRecordCount = $indexLineCount
        powershellParsedFileCount = $scriptFiles.Count
        result = 'PASS_PLANNING_STRUCTURE_ONLY'
    }
    New-Item -ItemType Directory -Path '.local\diagnostics' -Force | Out-Null
    $report | ConvertTo-Json | Set-Content -LiteralPath '.local\diagnostics\planning-checks.json' -Encoding utf8
    $report | ConvertTo-Json
} finally { Pop-Location }
