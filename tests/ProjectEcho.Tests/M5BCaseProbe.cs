using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

/// <summary>Explicit private local-case comparison; never prints or exports the spoken words.</summary>
internal static class M5BCaseProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (6 or 7)) return 2;
        int threads = args.Length == 7 ? int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture) : 2;
        string root = SyntheticAudioProbe.FindProjectRoot(), source = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string bundle = SyntheticAudioProbe.SafeProjectPath(root, args[2]), output = SyntheticAudioProbe.SafeProjectPath(root, args[3]);
        if (Directory.Exists(output)) throw new IOException("Choose a new evidence directory.");
        Directory.CreateDirectory(output); var checks = new List<string>(); var errors = new List<string>(); object? result = null;
        try
        {
            var transcript = TranscriptService.Load(source, args[4]); var alignment = transcript.Alignment!;
            var authorityHashes = Directory.EnumerateFiles(Path.Combine(source, "diarization"), "*.json", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
            string wave = Directory.EnumerateFiles(Path.Combine(source, "transcripts"), "*.wav", SearchOption.AllDirectories).First(path => M3TranscriptTests.Hash(path) == alignment.InputWaveSha256);
            string meeting = Path.Combine(output, "isolated", Path.GetFileName(source)); Directory.CreateDirectory(meeting);
            File.Copy(Path.Combine(source, "meeting.json"), Path.Combine(meeting, "meeting.json"));
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "transcripts"), "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked source rejected.");
                string destination = Path.Combine(meeting, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            string input = Path.Combine(meeting, Path.GetRelativePath(source, wave));
            var audio = new SpeakerAudioBinding(alignment.InputWaveSha256, alignment.SourceMixSha256, 16000, alignment.FrameCount, alignment.TimelinePolicy);
            var run = await SherpaDiarizationRuntime.RunAsync(new(meeting, transcript.MeetingId, input, audio, new(
                Path.Combine(bundle, "Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe"), Path.Combine(bundle, "Models/Sherpa/pyannote-segmentation-3.0.onnx"),
                Path.Combine(bundle, "Models/Sherpa/nemo_en_titanet_large.onnx"), 2, threads, ModelLicenseAcknowledged: true), transcript.IsSynthetic));
            Need(run.Status == "Succeeded" && run.Analysis is not null, "native_TitaNet_known2_completed");
            var speakers = SpeakerService.CreateRevision(meeting, transcript.RevisionId, run.Analysis!.AnalysisId);
            Need(string.Concat(speakers.Segments.Select(segment => segment.Text)) == string.Concat(transcript.Segments.Select(segment => segment.Text)), "all_original_text_preserved");
            // Human reference stays in an explicit ignored local file, not in product/test source.
            string goldPath = SyntheticAudioProbe.SafeProjectPath(root, args[5]);
            if (new FileInfo(goldPath).Length > 65536) throw new InvalidDataException("Local reference too large.");
            using var reference = JsonDocument.Parse(File.ReadAllText(goldPath));
            var gold = reference.RootElement.EnumerateArray().ToDictionary(row => row.GetProperty("startMs").GetInt64(), row => row.GetProperty("speakerCode").GetString()!);
            if (gold.Count is < 1 or > 1000 || gold.Any(row => row.Key < 0 || row.Key >= transcript.MeetingDurationMs || string.IsNullOrWhiteSpace(row.Value)))
                throw new InvalidDataException("Invalid local speaker reference.");
            var scores = new List<object>(); int correct = 0, wrong = 0, uncertain = 0;
            foreach (var row in gold)
            {
                var original = transcript.Segments.Single(segment => segment.StartMs == row.Key);
                var mapped = speakers.Segments.Where(segment => segment.SourceSegmentId == original.SegmentId).ToArray();
                string[] labels = mapped.Select(segment => segment.SpeakerId is null ? "?" : speakers.Speakers.Single(speaker => speaker.SpeakerId == segment.SpeakerId).Code).ToArray();
                string verdict = mapped.Any(segment => segment.NeedsConfirmation) ? "UNCERTAIN" : labels.All(label => label == row.Value) ? "CORRECT" : "WRONG";
                if (verdict == "CORRECT") correct++; else if (verdict == "WRONG") wrong++; else uncertain++;
                scores.Add(new { startMs = row.Key, endMs = original.EndMs, expected = row.Value, labels, verdict, statuses = mapped.Select(segment => segment.Status).ToArray() });
            }
            Need(authorityHashes.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value) && M3TranscriptTests.Hash(wave) == audio.InputWaveSha256 &&
                SpeakerService.TranscriptFingerprint(TranscriptService.Load(source, transcript.RevisionId)) == SpeakerService.TranscriptFingerprint(transcript), "original_audio_transcript_and_existing_manual_versions_unchanged");
            Need(JsonSerializer.Serialize(SpeakerService.Load(meeting, speakers.RevisionId)) == JsonSerializer.Serialize(speakers), "speaker_revision_reopens");
            result = new { frames = audio.FrameCount, policy = audio.TimelinePolicy, inputSha256 = audio.InputWaveSha256, groups = speakers.Speakers.Count,
                originalSegments = transcript.Segments.Count, speakerSegments = speakers.Segments.Count, originalCharacters = transcript.Segments.Sum(segment => segment.Text.Length),
                keptManualFiles = authorityHashes.Count, correct, wrong, uncertain, scores, remainingRows = "NOT_SCORED", threads, analysisId = speakers.AnalysisId,
                speakerRevisionId = speakers.RevisionId, run.AttemptDirectory, run.Analysis.Engine };
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", checks, errors, result,
            scope = "Only the explicit local single-speaker reference rows are scored; other rows are not counted as accurate. No source mutation, text export, audio playback, capture or upload." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5B local case completed; consult correctness/uncertainty separately" : string.Join("\n", errors));
        return errors.Count == 0 ? 0 : 1;
        void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); checks.Add(message); }
    }
}
