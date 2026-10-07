[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$FleursMetadataPath,
    [Parameter(Mandatory=$true)][string]$QwenCpuExecutable,
    [Parameter(Mandatory=$true)][string]$QwenVulkanExecutable,
    [Parameter(Mandatory=$true)][string]$QwenModelPath,
    [Parameter(Mandatory=$true)][string]$WhisperExecutable,
    [Parameter(Mandatory=$true)][string]$WhisperBaseModelPath,
    [Parameter(Mandatory=$true)][string]$WhisperLargeModelPath,
    [switch]$GenerateOnly,
    [switch]$ReuseFixtures
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Quality outputs must stay in ProjectEcho.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false, $true)
function Save-Json([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 14), $utf8) }
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Normalize-Text([string]$Text) { [regex]::Replace($Text.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant(), '[^\p{L}\p{Nd}]', '') }

# The following test-only helper uses bounded rows for edit distance and ordinary PCM WAV files.
# It never records, plays audio, downloads a model, or changes a system setting.
Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class M4QualityPcm {
    public static byte[] Header(int frames) {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36+frames*2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames*2);
        return memory.ToArray();
    }
    public static short[] Read(string path) {
        using var file = File.OpenRead(path); using var reader = new BinaryReader(file);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("RIFF required");
        int size = reader.ReadInt32(); if (new string(reader.ReadChars(4)) != "WAVE" || size + 8L != file.Length) throw new InvalidDataException("WAV length");
        bool fmt = false; short[] samples = null;
        while (file.Position < file.Length) {
            string kind = new string(reader.ReadChars(4)); int bytes = reader.ReadInt32(); long next = file.Position + bytes + (bytes & 1);
            if (bytes < 0 || next > file.Length) throw new InvalidDataException("WAV chunk");
            if (kind == "fmt ") {
                if (reader.ReadInt16()!=1 || reader.ReadInt16()!=1 || reader.ReadInt32()!=16000 || reader.ReadInt32()!=32000 || reader.ReadInt16()!=2 || reader.ReadInt16()!=16) throw new InvalidDataException("16k mono PCM16 required"); fmt=true;
            }
            if (kind == "data") { samples=new short[bytes/2]; for(int i=0;i<samples.Length;i++) samples[i]=reader.ReadInt16(); }
            file.Position=next;
        }
        if(!fmt || samples==null) throw new InvalidDataException("Missing PCM"); return samples;
    }
    public static void Write(string path, short[] pcm) {
        using var file = new FileStream(path, FileMode.CreateNew); using var writer = new BinaryWriter(file);
        writer.Write(Header(pcm.Length)); foreach(short sample in pcm) writer.Write(sample); file.Flush(true);
    }
    public static void Noise(string path, int frames) {
        var rng=new Random(404); var pcm=new short[frames]; for(int i=0;i<frames;i++) pcm[i]=(short)rng.Next(-1700,1701); Write(path,pcm);
    }
    public static double AddNoise(string source, string path) {
        var pcm=Read(source); double energy=0; foreach(short x in pcm) energy+=(double)x*x;
        double sigma=Math.Sqrt(energy/pcm.Length)/Math.Pow(10,12.0/20); var rng=new Random(404); var output=new short[pcm.Length];
        for(int i=0;i<pcm.Length;i++) output[i]=(short)Math.Clamp(Math.Round(pcm[i]+(rng.NextDouble()*2-1)*sigma*Math.Sqrt(3)),short.MinValue,short.MaxValue);
        Write(path,output); return 12;
    }
    public static int Distance(string a, string b) {
        var previous=new int[b.Length+1];var current=new int[b.Length+1];for(int j=0;j<=b.Length;j++)previous[j]=j;
        for(int i=1;i<=a.Length;i++){current[0]=i;for(int j=1;j<=b.Length;j++)current[j]=Math.Min(Math.Min(previous[j]+1,current[j-1]+1),previous[j-1]+(a[i-1]==b[j-1]?0:1));var swap=previous;previous=current;current=swap;}
        return previous[b.Length];
    }
}
'@

$fixturePath = Join-Path $output 'fixtures.json'
if (-not $ReuseFixtures) {
    if (Test-Path -LiteralPath $fixturePath) { throw 'Use a new output directory, or explicit -ReuseFixtures.' }
    $fixtures = [Collections.Generic.List[object]]::new()
    $metadata = [IO.File]::ReadAllLines([IO.Path]::GetFullPath($FleursMetadataPath), $utf8)
    foreach ($sample in @(@{Id='fleurs-zh-short'; File='zh-short.wav'; Utterance='17639585860488007329.wav'}, @{Id='fleurs-zh-long'; File='zh-long.wav'; Utterance='5655534691025010514.wav'})) {
        $rows = @($metadata | ForEach-Object { ,($_ -split "`t") } | Where-Object { $_[1] -eq $sample.Utterance })
        if ($rows.Count -ne 1) { throw 'FLEURS gold ID not unique or not found.' }
        $path = Join-Path $output ($sample.Id+'.wav')
        Copy-Item -LiteralPath (Join-Path $projectRoot ('.tools/transcribe-source/samples/'+$sample.File)) -Destination $path
        $fixtures.Add([ordered]@{Id=$sample.Id; WavePath=$path; Reference=$rows[0][2]; Language='zh'; Kind='natural-read-speech';
            Source='google/fleurs cmn_hans_cn test'; UtteranceId=$sample.Utterance; License='CC-BY-4.0';
            SourceUrl='https://huggingface.co/datasets/google/fleurs/blob/main/data/cmn_hans_cn/test.tsv';
            MetadataSha256=(Hash-File $FleursMetadataPath); Sha256=(Hash-File $path); Frames=[M4QualityPcm]::Read($path).Length;
            KeyFacts=@()})
    }
    $definitions = @(
        @{Id='sapi-finance-zh'; Language='zh'; Utterances=@(
            @{Language='zh'; Text='本季度收入为十二点五亿元，同比增长百分之三点二。净债务不是二十亿元，而是十二亿元。利差从一百二十个基点扩大到一百五十个基点。'}
        ); KeyFacts=@(
            @{Id='revenue-12.5-yiyuan-unit'; Pattern='(?:十二点五|12[.点]5)亿元'},
            @{Id='growth-3.2-percent'; Pattern='(?:百分之(?:三点二|3[.点]2)|3[.点]2%)'},
            @{Id='negated-debt-20'; Pattern='不是(?:二十|20)亿元'},
            @{Id='actual-debt-12'; Pattern='而是(?:十二|12)亿元'},
            @{Id='spread-direction-120-to-150'; Pattern='从(?:一百二十|120)(?:个)?基点扩大到(?:一百五十|150)(?:个)?基点'})},
        @{Id='sapi-finance-mixed'; Language='auto'; Utterances=@(
            @{Language='en'; Text='The EBITDA margin is twelve point five percent, not twenty percent.'},
            @{Language='zh'; Text='净杠杆为三点二倍，债券到期日是二零二七年六月十五日。'}
        ); KeyFacts=@(
            @{Id='ebitda'; Pattern='(?i)EBITDA'},
            @{Id='margin-12.5-percent'; Pattern='(?i)(?:twelve point five percent|12\.5(?: percent|%))'},
            @{Id='negated-margin-20'; Pattern='(?i)not (?:twenty percent|20(?: percent|%))'},
            @{Id='leverage-3.2'; Pattern='(?:三点二|3[.点]2)倍'},
            @{Id='maturity-date'; Pattern='(?:二零二七|2027)年(?:六|6)月(?:十五|15)日'})}
    )
    $voice = New-Object -ComObject SAPI.SpVoice
    try {
        $tokens=$voice.GetVoices(); $zh=$null; $en=$null
        for($i=0;$i -lt $tokens.Count;$i++){ $candidate=$tokens.Item($i); if($candidate.GetAttribute('Language') -eq '804' -and $null -eq $zh){$zh=$candidate}; if($candidate.GetAttribute('Language') -eq '409' -and $null -eq $en){$en=$candidate} }
        if($null -eq $zh -or $null -eq $en){throw 'Installed SAPI Mandarin and English voices required; no download or registry change.'}
        $voice.Rate=0
        foreach($definition in $definitions){
            $path=Join-Path $output ($definition.Id+'.wav'); $stream=New-Object -ComObject SAPI.SpFileStream
            try {
                $stream.Format.Type=18; $stream.Open($path,3,$false); $voice.AudioOutputStream=$stream
                foreach($utterance in $definition.Utterances){$voice.Voice=if($utterance.Language -eq 'zh'){$zh}else{$en};[void]$voice.Speak($utterance.Text,0)}
                $stream.Close()
            } finally {[void][Runtime.InteropServices.Marshal]::ReleaseComObject($stream)}
            $fixtures.Add([ordered]@{Id=$definition.Id;WavePath=$path;Reference=($definition.Utterances.Text -join ' ');Language=$definition.Language;
                Kind='synthetic-sapi';Source='Locally authored fixed financial boundary script';Voices=@($zh.GetDescription(),$en.GetDescription());
                Sha256=(Hash-File $path);Frames=[M4QualityPcm]::Read($path).Length;KeyFacts=$definition.KeyFacts})
        }
    } finally {[void][Runtime.InteropServices.Marshal]::ReleaseComObject($voice)}
    $speech=$fixtures | Where-Object {$_.Id -eq 'sapi-finance-zh'}
    $noisyPath=Join-Path $output 'sapi-finance-noise12db.wav'; [void][M4QualityPcm]::AddNoise($speech.WavePath,$noisyPath)
    $fixtures.Add([ordered]@{Id='sapi-finance-noise12db';WavePath=$noisyPath;Reference=$speech.Reference;Language='zh';Kind='synthetic-sapi-noise';
        Source='Same SAPI signal plus seeded uniform noise, nominal full-file 12 dB SNR';Sha256=(Hash-File $noisyPath);Frames=[M4QualityPcm]::Read($noisyPath).Length;KeyFacts=$speech.KeyFacts})
    foreach($id in @('digital-zero','noise-only')){
        $path=Join-Path $output ($id+'.wav');if($id -eq 'digital-zero'){[M4QualityPcm]::Write($path,[short[]]::new(80000))}else{[M4QualityPcm]::Noise($path,80000)}
        $fixtures.Add([ordered]@{Id=$id;WavePath=$path;Reference='';Language='auto';Kind='synthetic-no-speech';Source='Deterministic PCM fixture, no spoken content';Sha256=(Hash-File $path);Frames=80000;KeyFacts=@()})
    }
    Save-Json $fixturePath ([ordered]@{Schema='m4-fixed-quality-fixtures-1';CreatedUtc=[DateTimeOffset]::UtcNow;Captured=$false;Played=$false;Uploaded=$false;
        Thresholds='Comparative measurements only; no representative natural-meeting pass threshold or preferred-engine claim. Any text on known silent/noise-only samples is a hallucination failure.';
        Fixtures=@($fixtures)})
}
$manifest=Get-Content -LiteralPath $fixturePath -Raw -Encoding utf8 | ConvertFrom-Json
foreach($fixture in $manifest.Fixtures){if((Hash-File $fixture.WavePath) -ne $fixture.Sha256){throw 'Fixture changed after gold was frozen.'}}
if($GenerateOnly){ Write-Host $fixturePath; return }

function Invoke-QualityProcess([string]$Exe,[string[]]$ArgumentList,[string]$Prefix) {
    if(Test-Path -LiteralPath ($Prefix+'.result.json')){throw 'Never overwrite a measured run; use a new output directory.'}
    $start=[Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Exe));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.StandardOutputEncoding=$utf8;$start.StandardErrorEncoding=$utf8
    $start.WorkingDirectory=Split-Path -Parent $Prefix
    foreach($argument in $ArgumentList){[void]$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start;$timer=[Diagnostics.Stopwatch]::StartNew()
    [void]$process.Start(); try{$process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal}catch{}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$peak=0L
    while(-not $process.WaitForExit(100)){$process.Refresh();$peak=[Math]::Max($peak,$process.WorkingSet64);if($timer.Elapsed.TotalMinutes -gt 15){$process.Kill($true);throw 'Quality inference timed out after 15 minutes.'}}
    $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult();[IO.File]::WriteAllText($Prefix+'.stdout.txt',$text,$utf8);[IO.File]::WriteAllText($Prefix+'.stderr.txt',$errorText,$utf8)
    $result=[ordered]@{Executable=[IO.Path]::GetFullPath($Exe);ExecutableSha256=(Hash-File $Exe);Arguments=$ArgumentList;ExitCode=$process.ExitCode;ElapsedMs=$timer.ElapsedMilliseconds;ObservedPeakWorkingSetBytes=$peak;Stdout=$Prefix+'.stdout.txt';Stderr=$Prefix+'.stderr.txt'}
    Save-Json ($Prefix+'.result.json') $result;$process.Dispose();return $result
}
$batchPath=Join-Path $output 'qwen-batch.txt';[IO.File]::WriteAllLines($batchPath,[string[]]$manifest.Fixtures.WavePath,$utf8)
$evidence=[Collections.Generic.List[object]]::new()
foreach($engine in @(@{Id='qwen-cpu';Exe=$QwenCpuExecutable;Backend='cpu'},@{Id='qwen-vulkan';Exe=$QwenVulkanExecutable;Backend='vulkan'})){
    Write-Host ('Quality: '+$engine.Id)
    $run=Invoke-QualityProcess $engine.Exe @('--model',[IO.Path]::GetFullPath($QwenModelPath),'--backend',$engine.Backend,'--device','0','--threads','4','--n-ctx','2048','--timestamps','none','--batch',$batchPath,'--batch-jsonl') (Join-Path $output $engine.Id)
    if($run.ExitCode -ne 0){throw ($engine.Id+' failed; diagnostics retained.')}
    $rows=@(Get-Content -LiteralPath $run.Stdout -Encoding utf8 | ForEach-Object {if($_.Trim().StartsWith('{')){$_|ConvertFrom-Json}})
    $modelHash=Hash-File $QwenModelPath
    foreach($fixture in $manifest.Fixtures){
        $matches=@($rows|Where-Object{$_.PSObject.Properties.Name -contains 'file' -and $_.file -eq $fixture.WavePath})
        if($matches.Count -ne 1 -or $matches[0].PSObject.Properties.Name -contains 'error'){throw 'Qwen output missing, duplicate, or error.'}
        $evidence.Add([ordered]@{Engine=$engine.Id;Fixture=$fixture.Id;Hypothesis=[string]$matches[0].text;RawOutput=$matches[0];Run=$run;ModelSha256=$modelHash})
    }
}
foreach($engine in @(@{Id='whisper-base';Model=$WhisperBaseModelPath},@{Id='whisper-large-v3';Model=$WhisperLargeModelPath})){
    $modelHash=Hash-File $engine.Model
    foreach($fixture in $manifest.Fixtures){
        Write-Host ('Quality: '+$engine.Id+' '+$fixture.Id);$prefix=Join-Path $output ($engine.Id+'-'+$fixture.Id)
        $run=Invoke-QualityProcess $WhisperExecutable @('-m',[IO.Path]::GetFullPath($engine.Model),'-f',$fixture.WavePath,'-l',$fixture.Language,'-t','4','-otxt','-ojf','-of',$prefix) $prefix
        if($run.ExitCode -ne 0 -or -not (Test-Path -LiteralPath ($prefix+'.txt'))){throw ($engine.Id+' failed; diagnostics retained.')}
        $hypothesis=[IO.File]::ReadAllText($prefix+'.txt',$utf8).Trim()
        $evidence.Add([ordered]@{Engine=$engine.Id;Fixture=$fixture.Id;Hypothesis=$hypothesis;Run=$run;ModelSha256=$modelHash})
    }
}
$scores=@(foreach($row in $evidence){
    $fixture=$manifest.Fixtures|Where-Object{$_.Id -eq $row.Fixture};$reference=Normalize-Text $fixture.Reference;$hypothesis=Normalize-Text $row.Hypothesis
    $distance=[M4QualityPcm]::Distance($reference,$hypothesis)
    $facts=@(foreach($fact in $fixture.KeyFacts){[ordered]@{Id=$fact.Id;Pattern=$fact.Pattern;Matched=[regex]::IsMatch($row.Hypothesis,$fact.Pattern)}})
    [ordered]@{Engine=$row.Engine;Fixture=$row.Fixture;Kind=$fixture.Kind;Reference=$fixture.Reference;Hypothesis=$row.Hypothesis;
        ReferenceSymbols=$reference.Length;HypothesisSymbols=$hypothesis.Length;EditDistance=$distance;
        CharacterErrorRate=if($reference.Length -gt 0){$distance/[double]$reference.Length}else{$null};
        KnownNoSpeechHallucination=($reference.Length -eq 0 -and $hypothesis.Length -gt 0);KeyFacts=$facts;
        MissingKeyFactCount=@($facts|Where-Object{-not $_.Matched}).Count;Run=$row.Run;ModelSha256=$row.ModelSha256}
})
Save-Json (Join-Path $output 'quality-report.json') ([ordered]@{Schema='m4-fixed-quality-report-1';MeasuredUtc=[DateTimeOffset]::UtcNow;
    Os=[Environment]::OSVersion.VersionString;FixtureManifestSha256=(Hash-File $fixturePath);Scores=$scores;
    Interpretation='CER removes punctuation/whitespace, lowercases, and NFKC normalizes. It does not convert spelled numbers/digits, Chinese scripts, or translations. Key-fact regexes were frozen with fixtures; matches are not semantic proof. Raw whole-file CLI quality comparison is separate from application chunking/alignment acceptance. Natural Chinese is read speech; natural mixed/financial/telephone meetings remain NOT_RUN.';
    Captured=$false;Played=$false;Uploaded=$false;AsrTimestampsValidated=$false})
Write-Host (Join-Path $output 'quality-report.json')
