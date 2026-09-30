[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WhisperExecutable,
    [Parameter(Mandatory = $true)][string]$ModelPath,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$evidenceParent = Join-Path $projectRoot '.local\M3cSyntheticSpeech'
$evidenceFolder = Join-Path $evidenceParent ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceFolder -Force | Out-Null
$voice = New-Object -ComObject SAPI.SpVoice
$voiceTokens = $voice.GetVoices()
$chinese = $null
$english = $null
for ($index = 0; $index -lt $voiceTokens.Count; $index++) {
    $candidate = $voiceTokens.Item($index)
    $language = $candidate.GetAttribute('Language')
    if ($language -eq '804' -and $null -eq $chinese) { $chinese = $candidate }
    if ($language -eq '409' -and $null -eq $english) { $english = $candidate }
}
if ($null -eq $chinese -or $null -eq $english) { throw 'Installed Mandarin and English SAPI voices are required. No voice will be downloaded.' }
$mandarin = @(
    '这是本地会议录音的合成测试，不是真实会议。今天讨论三个问题：项目进度、声音保存和字幕校对。',
    '第一项任务是保留原始录音。第二项任务是导出完整文本。暂停期间不采集麦克风，恢复以后继续保存。',
    '请检查日期、数字和专有名词。预算是二十万元，预计需要三十分钟。完成测试以后，所有文件留在本机，不上传到外部服务。'
)
$englishText = @(
    'This is a synthetic test of the local meeting recorder, not a real meeting. We are reviewing recording quality, project progress, and subtitle editing.',
    'The first task is to preserve the original audio. The second task is to export the complete transcript. The budget is twenty thousand dollars and the estimated duration is thirty minutes.',
    'Please check dates, numbers, and technical names. Project Echo records a selected process tree. Microsoft Windows provides the audio interface. These test files remain on this computer.'
)
$definitions = @(
    @{Id='mandarin'; Language='zh'; Utterances=@($mandarin | ForEach-Object { @{Language='zh';Text=$_} })},
    @{Id='english'; Language='en'; Utterances=@($englishText | ForEach-Object { @{Language='en';Text=$_} })},
    @{Id='mixed'; Language='auto'; Utterances=@(
        @{Language='zh';Text='这是中英文交替的本地合成测试。今天我们检查会议录音和字幕，不使用真实会议数据。'},
        @{Language='en';Text='The application is called Project Echo. It supports local recording, subtitle search, and text export.'},
        @{Language='zh';Text='现在检查数字，预算是二十万元，录音长度是三十分钟。请不要把模型输出当作已经确认的事实。'},
        @{Language='en';Text='The next review is scheduled for October first. The microphone is independent of the meeting mute button. Keep the original files.'},
        @{Language='zh';Text='最后检查完整时间轴。所有测试资料只保存在本机，不自动上传。'}
    )}
)
$fixtures = @()
try {
    $voice.Rate = 0
    foreach ($definition in $definitions) {
        $wavePath = Join-Path $evidenceFolder ($definition.Id + '.wav')
        $fileStream = New-Object -ComObject SAPI.SpFileStream
        try {
            # SAFT16kHz16BitMono = 18; SSFMCreateForWrite = 3. The output is a file stream, never a render device.
            $fileStream.Format.Type = 18
            $fileStream.Open($wavePath, 3, $false)
            $voice.AudioOutputStream = $fileStream
            foreach ($utterance in $definition.Utterances) {
                $voice.Voice = if ($utterance.Language -eq 'zh') { $chinese } else { $english }
                [void]$voice.Speak($utterance.Text, 0)
            }
            $fileStream.Close()
        } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($fileStream) }
        $fixtures += [ordered]@{ Id=$definition.Id; Language=$definition.Language; WavePath=$wavePath;
            ExpectedText=($definition.Utterances.Text -join ' '); Utterances=$definition.Utterances;
            Sha256=(Get-FileHash -LiteralPath $wavePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [ordered]@{ Schema='m3c-synthetic-speech-fixtures-1'; CreatedAtUtc=[DateTimeOffset]::UtcNow;
        Source='Locally authored fixed text synthesized with installed Windows SAPI voices'; IsSynthetic=$true;
        AudioPlayed=$false; CaptureStarted=$false; MicrophoneEnabled=$false; Uploaded=$false;
        Voices=@($chinese.GetDescription(),$english.GetDescription()); Fixtures=$fixtures }
    $manifestPath = Join-Path $evidenceFolder 'fixtures.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Host ('Synthetic speech evidence: ' + $evidenceFolder)
    & (Join-Path $PSScriptRoot 'Dotnet.ps1') -Arguments @('run','--project',
        (Join-Path $projectRoot 'tests/ProjectEcho.Tests/ProjectEcho.Tests.csproj'), '--no-build','--no-restore','-c',$Configuration,'--',
        '--m3c-synthetic-speech-probe',$WhisperExecutable,$ModelPath,$manifestPath)
} finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($voice) }
