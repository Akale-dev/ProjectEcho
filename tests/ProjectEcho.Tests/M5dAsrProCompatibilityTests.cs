using System.Reflection;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5dAsrProCompatibilityTests
{
    private const int Rate = 16000, Frames = Rate * 4;
    private const long DurationMs = 4000;
    private const string Prefix = "synthetic-m3e-timestamp-m5d-";
    private sealed record Word(int Id, string Text, long From, long To);
    private sealed record Cue(long From, long To, string Text, Word[] Tokens);

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m5d-asr-pro"); Directory.CreateDirectory(root);
        run("P5-02/T35/T38 SYNTH M5D zero and overrun preservation keeps audio-bound raw tokens without timing or assigning untimed words", () => WhisperToSpeakers(root));
        run("P5-02/T50 SYNTH M5D Whisper audio mix frame timeline and raw token hashes remain strict", () => Bindings(root));
        run("P5-03/T38 SYNTH M5D ASR rerun preserves transcript edits and meeting-local speaker revisions", () => Immutable(root));
        run("P4-03/P5-02 SYNTH M5D production Qwen aligned import retains raw word evidence and speaker mapping", () => Qwen(root));
    }

    private static void WhisperToSpeakers(string root)
    {
        foreach (string variant in new[] { "zero", "overrun", "after-eof", "zero-recovered", "overrun-recovered" })
        {
            var (request, run) = Run(root, variant, variant);
            var source = Success(run);
            Need(source.Segments.Count == 3, "ASR normalization changed neighboring sentence topology.");
            var provenance = source.Alignment ?? throw new InvalidDataException("M5 audio-bound token provenance was lost.");
            string evidence = Path.Combine(request.MeetingDirectory, provenance.EvidenceFile);
            Need(provenance.Policy == "whisper-exact-token-boundaries-v1" &&
                provenance.InputWaveSha256 == request.ExpectedWaveSha256 && provenance.SourceMixSha256 == request.SourceMixSha256 &&
                provenance.SampleRate == Rate && provenance.FrameCount == Frames && provenance.TimelinePolicy == request.TimelinePolicy &&
                M3TranscriptTests.Hash(evidence) == M3TranscriptTests.Hash(Path.Combine(run.AttemptDirectory, "whisper-output.json")),
                "Whisper original token evidence or audio/frame/pause binding changed.");
            var audio = Audio(request);
            var mapping = SpeakerAlignmentReader.Read(request.MeetingDirectory, source, audio);
            var byId = source.Segments.ToDictionary(segment => segment.SegmentId);
            Need(mapping.Boundaries.Count >= 2 && mapping.Boundaries.All(boundary =>
                byId[boundary.SegmentId].StartMs is { } start && byId[boundary.SegmentId].EndMs is { } end &&
                boundary.LeftEndMs > start && boundary.RightStartMs < end && boundary.LeftEndMs <= boundary.RightStartMs &&
                boundary.RightStartMs <= DurationMs),
                "Normal Whisper boundaries were lost or untimed/out-of-audio words acquired a split boundary.");
            var analysis = Analysis(request.MeetingDirectory, request.MeetingId, audio);
            var speaker = SpeakerService.CreateRevision(request.MeetingDirectory, source.RevisionId, analysis.AnalysisId);
            Need(string.Concat(speaker.Segments.Select(segment => segment.Text)) == string.Concat(source.Segments.Select(segment => segment.Text)),
                "Speaker mapping removed, invented, or reordered ASR words.");
            var middle = source.Segments[1];
            if (!variant.EndsWith("recovered", StringComparison.Ordinal))
            {
                Need(middle.StartMs is null && middle.EndMs is null &&
                    !mapping.Boundaries.Any(boundary => boundary.SegmentId == middle.SegmentId), "Untimed ASR text acquired token time.");
                var untimed = speaker.Segments.Where(segment => segment.SourceSegmentId == middle.SegmentId).ToArray();
                Need(untimed.Length == 1 && untimed[0].Status == "untimed" && untimed[0].StartMs is null &&
                    untimed[0].EndMs is null && untimed[0].SpeakerId is null && untimed[0].Text == middle.Text,
                    "Untimed ASR words acquired inferred time, speaker identity, or a cut.");
                string txt = Path.Combine(root, variant + "-speakers.txt");
                SpeakerSubtitleExporter.Export(request.MeetingDirectory, speaker.RevisionId, txt, false);
                Need(File.ReadAllText(txt).Contains(middle.Text, StringComparison.Ordinal), "Speaker TXT omitted untimed words.");
                Bad(() => SpeakerSubtitleExporter.Export(request.MeetingDirectory, speaker.RevisionId,
                    Path.Combine(root, variant + "-forbidden.srt"), false));
            }
            else
            {
                Need(middle.StartMs is not null && middle.EndMs == 1800, "Supported own-token recovery lost its valid caption time.");
                if (variant == "overrun-recovered")
                    Need(mapping.Boundaries.Any(boundary => boundary.SegmentId == middle.SegmentId &&
                        boundary.LeftEndMs == 1400 && boundary.RightStartMs == 1400), "Recovered in-range token boundary disappeared.");
                // Zero-cue tokens that contradict the raw envelope remain conservative for
                // within-sentence speaker splitting; caption recovery is not fabricated word evidence.
            }
            Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256 &&
                TranscriptService.Load(request.MeetingDirectory, source.RevisionId).Alignment == provenance,
                "Speaker processing changed audio or transcript provenance.");
        }
    }

    private static void Bindings(string root)
    {
        var (request, run) = Run(root, "binding", "overrun");
        var source = Success(run); var audio = Audio(request);
        foreach (var wrong in new[]
        {
            audio with { InputWaveSha256 = new string('f', 64) },
            audio with { SourceMixSha256 = new string('e', 64) },
            audio with { FrameCount = audio.FrameCount + 1 },
            audio with { TimelinePolicy = "elapsed-v1" }
        }) Bad(() => SpeakerAlignmentReader.Read(request.MeetingDirectory, source, wrong));
        string evidence = Path.Combine(request.MeetingDirectory, source.Alignment!.EvidenceFile);
        byte[] original = File.ReadAllBytes(evidence);
        try
        {
            File.WriteAllBytes(evidence, original.Concat(new byte[] { 32 }).ToArray());
            Bad(() => SpeakerAlignmentReader.Read(request.MeetingDirectory, source, audio));
            Bad(() => TranscriptService.Load(request.MeetingDirectory, source.RevisionId));
        }
        finally { File.WriteAllBytes(evidence, original); }
        Need(SpeakerAlignmentReader.Read(request.MeetingDirectory, source, audio).Boundaries.Count >= 2,
            "Restored original evidence no longer validates.");
    }

    private static void Immutable(string root)
    {
        var (request, run) = Run(root, "immutable", "overrun");
        var source = Success(run); var analysis = Analysis(request.MeetingDirectory, request.MeetingId, Audio(request));
        var speaker = SpeakerService.CreateRevision(request.MeetingDirectory, source.RevisionId, analysis.AnalysisId);
        var named = SpeakerService.Rename(request.MeetingDirectory, speaker.RevisionId, speaker.Speakers[0].SpeakerId, "合成手填显示名");
        var reviewed = TranscriptService.CreateRevision(request.MeetingDirectory, source.RevisionId,
            source.Segments.Select((segment, index) => index == 0 ? segment with { Text = "Alpha corrected." } : segment).ToArray());
        string namedPath = Path.Combine(request.MeetingDirectory, "diarization", "revisions", named.RevisionId, "revision.json");
        string oldSpeakerHash = M3TranscriptTests.Hash(namedPath);
        var editedEvidence = SpeakerAlignmentReader.Read(request.MeetingDirectory, reviewed, Audio(request));
        Need(editedEvidence.Boundaries.All(boundary => boundary.SegmentId != source.Segments[0].SegmentId),
            "Edited wording reused stale original token boundaries.");
        var rerun = Success(WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult());
        Need(rerun.RevisionId != source.RevisionId &&
            TranscriptService.Load(request.MeetingDirectory, source.RevisionId).Segments.SequenceEqual(source.Segments) &&
            TranscriptService.Load(request.MeetingDirectory, reviewed.RevisionId).Segments[0].Text == "Alpha corrected." &&
            SpeakerService.Load(request.MeetingDirectory, named.RevisionId).Speakers[0].DisplayName == "合成手填显示名" &&
            M3TranscriptTests.Hash(namedPath) == oldSpeakerHash, "Rerun changed a previous transcript or speaker correction.");
    }

    private static void Qwen(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "qwen-preserved"), wave = WriteWave(meeting);
        const string text = "甲方，乙方。";
        string waveHash = M3TranscriptTests.Hash(wave), mixHash = new string('b', 64), attempt = Guid.NewGuid().ToString("N");
        string attemptDirectory = Path.Combine(meeting, "transcripts", "asr", attempt); Directory.CreateDirectory(attemptDirectory);
        string words = Path.Combine(attemptDirectory, "words.json");
        File.WriteAllText(words, JsonSerializer.Serialize(new[]
        {
            new { word = "甲方", start_sample = 0, end_sample = 32000 },
            new { word = "乙方", start_sample = 32000, end_sample = 64000 }
        }));
        byte[] alignment = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "m4-qwen-alignment-1", expectedWaveSha256 = waveHash, timelinePolicy = "pause-excluded-v1", attempt,
            windowEvidence = new[] { new
            {
                window = new { inputStartFrame = 0, inputFrameCount = Frames },
                evidence = new { originalTextSha256 = QwenWindowAlignment.TextSha256(text) },
                rawWords = "words.json", rawWordsSha256 = M3TranscriptTests.Hash(words),
                sentences = new[] { new { text, textStart = 0, startMs = 0, endMs = DurationMs } }
            } }
        });
        var request = new LocalTranscriptionRequest(meeting, "qwen-preserved", DurationMs, wave, waveHash,
            new(QwenTranscriber.BackendId, "synthetic-not-invoked.exe", "synthetic-not-loaded.bin", ModelName: "synthetic Qwen evidence"),
            true, mixHash, "pause-excluded-v1");
        TranscriptSegment[] segments = [new("segment-000001", 0, DurationMs, null, "unknown", text, "approximate")];
        MethodInfo import = typeof(TranscriptService).GetMethod("ImportAligned", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production Qwen aligned import was removed.");
        var source = (TranscriptRevision)import.Invoke(null,
            [request, text, segments, alignment, (long)Frames, QwenTranscriber.Policy, CancellationToken.None])!;
        Need(source.Alignment is { } provenance && provenance.Policy == QwenTranscriber.Policy &&
            provenance.InputWaveSha256 == waveHash && provenance.SourceMixSha256 == mixHash &&
            provenance.FrameCount == Frames && provenance.TimelinePolicy == "pause-excluded-v1" &&
            File.ReadAllBytes(Path.Combine(meeting, provenance.EvidenceFile)).SequenceEqual(alignment),
            "Qwen alignment provenance or raw evidence was changed by Whisper forward-port.");
        var audio = new SpeakerAudioBinding(waveHash, mixHash, Rate, Frames, "pause-excluded-v1");
        var mapping = SpeakerAlignmentReader.Read(meeting, source, audio);
        Need(mapping.Boundaries.Count == 1 && mapping.Boundaries[0].TextOffset == 3 &&
            mapping.Boundaries[0].LeftEndMs == 2000 && mapping.Boundaries[0].RightStartMs == 2000, "Qwen word boundaries changed.");
        var analysis = Analysis(meeting, "qwen-preserved", audio, qwen: true);
        var speaker = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
        Need(speaker.Segments.Count == 2 && speaker.Segments.All(segment => segment.Status == "assigned") &&
            string.Concat(speaker.Segments.Select(segment => segment.Text)) == text, "Qwen speaker mapping lost original text.");
        Bad(() => import.Invoke(null, [request, text, segments, alignment, (long)Frames + 160, QwenTranscriber.Policy, CancellationToken.None]));
        Need(TranscriptService.ListRevisions(meeting).Count == 1, "Invalid Qwen frame binding saved a revision.");
    }

    private static (WhisperRequest Request, WhisperRunResult Run) Run(string root, string name, string variant)
    {
        string meeting = M3TranscriptTests.Meeting(root, name), wave = WriteWave(meeting);
        string model = M3TranscriptTests.Input(meeting, "fixture.bin", Prefix + variant);
        var request = new WhisperRequest(meeting, name, DurationMs, wave, M3TranscriptTests.Hash(wave),
            new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model,
                ModelName: "synthetic M5D compatibility fixture; not actual ASR", ModelLicenseAcknowledged: true),
            true, new string('b', 64), "pause-excluded-v1");
        return (request, WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult());
    }

    private static string WriteWave(string meeting)
    {
        string path = Path.Combine(meeting, "synthetic-input.wav");
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + Frames * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(Rate); writer.Write(Rate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(Frames * 2);
        for (int index = 0; index < Frames; index++) writer.Write((short)(index % 97));
        return path;
    }

    private static SpeakerAudioBinding Audio(WhisperRequest request) =>
        new(request.ExpectedWaveSha256, request.SourceMixSha256, Rate, Frames, request.TimelinePolicy);

    private static SpeakerAnalysis Analysis(string meeting, string id, SpeakerAudioBinding audio, bool qwen = false)
    {
        MeetingSpeaker[] speakers = [new(Guid.NewGuid().ToString("N"), "A"), new(Guid.NewGuid().ToString("N"), "B")];
        SpeakerInterval[] intervals = qwen
            ? [new(0, 2000, speakers[0].SpeakerId), new(2000, DurationMs, speakers[1].SpeakerId)]
            : [new(0, 1400, speakers[0].SpeakerId), new(1400, 2000, speakers[1].SpeakerId),
               new(2000, 2500, speakers[0].SpeakerId), new(2500, DurationMs, speakers[1].SpeakerId)];
        return SpeakerService.SaveAnalysis(meeting, new(1, id, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, audio,
            new("synthetic", "1", new string('c', 64), new string('d', 64), new string('e', 64), 2, .9, "synthetic", "synthetic-global"),
            speakers, intervals, [], true));
    }

    internal static int WriteFixture(string output, string variant)
    {
        bool recovered = variant.EndsWith("recovered", StringComparison.Ordinal);
        string middleText = recovered ? "Recovered phrase." : "Uncertain words kept.";
        long start = variant == "after-eof" ? 4100 : 1100;
        long end = variant.StartsWith("zero", StringComparison.Ordinal) ? 1100 : variant == "after-eof" ? 4400 : 4500;
        Word[] middle = variant switch
        {
            "zero" => [new(100, middleText, 1100, 1100)],
            "zero-recovered" => [new(100, "Recovered ", 1000, 1300), new(101, "phrase.", 1300, 1800)],
            "overrun-recovered" => [new(100, "Recovered ", 1100, 1400), new(101, "phrase.", 1400, 1800)],
            "after-eof" => [new(100, "Uncertain ", 4100, 4200), new(101, "words ", 4200, 4300), new(102, "kept.", 4300, 4400)],
            _ => [new(100, "Uncertain ", 1100, 1900), new(101, "words ", 1900, 2500), new(102, "kept.", 2500, 4400)]
        };
        Cue[] cues =
        [
            new(0, 1000, "Alpha beta.", [new(100, "Alpha ", 0, 400), new(101, "beta.", 400, 1000)]),
            new(start, end, middleText, middle),
            new(2000, 3000, "Gamma delta.", [new(100, "Gamma ", 2000, 2500), new(101, "delta.", 2500, 3000)])
        ];
        File.WriteAllText(output + ".srt", string.Concat(cues.Select((cue, index) =>
            $"{index + 1}\n{Time(cue.From)} --> {Time(cue.To)}\n{cue.Text}\n\n")), new UTF8Encoding(false));
        File.WriteAllText(output + ".json", JsonSerializer.Serialize(new
        {
            model = new { multilingual = true },
            transcription = cues.Select(cue => new
            {
                text = cue.Text, offsets = new { from = cue.From, to = cue.To },
                tokens = cue.Tokens.Select(word => new { id = word.Id, text = word.Text, offsets = new { from = word.From, to = word.To } })
            })
        }), new UTF8Encoding(false));
        File.WriteAllText(output + ".txt", string.Join('\n', cues.Select(cue => cue.Text)), new UTF8Encoding(false));
        Console.Error.WriteLine("whisper_print_progress_callback: progress = 100%");
        return 0;
    }

    private static string Time(long value) => $"00:00:{value / 1000:D2},{value % 1000:D3}";
    private static TranscriptRevision Success(WhisperRunResult result)
    { Need(result.Status == "Succeeded" && result.Revision is not null, result.Message); return result.Revision!; }
    private static void Bad(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid bound evidence was accepted.");
    }
    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
