using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

/// <summary>Explicit local-case validation/recovery. Reports counts and hashes, never speech text.</summary>
internal static class M5IntervalFixProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || args[4] is not ("validate" or "recover")) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string source = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string bundle = SyntheticAudioProbe.SafeProjectPath(root, args[2]);
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[3]);
        if (Directory.Exists(output)) throw new IOException("Use a new evidence directory.");
        Directory.CreateDirectory(output);
        var errors = new List<string>(); var checks = new List<string>(); object? outcome = null;
        try
        {
            var original = TranscriptService.Load(source, args[5]);
            var alignment = original.Alignment ?? throw new InvalidDataException("This explicit probe requires the selected transcript's bound audio evidence.");
            var existing = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, M3TranscriptTests.Hash);
            string input = Directory.EnumerateFiles(source, "*.wav", SearchOption.AllDirectories)
                .First(path => M3TranscriptTests.Hash(path) == alignment.InputWaveSha256);
            string meeting = source;
            if (args[4] == "validate")
            {
                meeting = Path.Combine(output, "isolated", Path.GetFileName(source)); Directory.CreateDirectory(meeting);
                File.Copy(Path.Combine(source, "meeting.json"), Path.Combine(meeting, "meeting.json"));
                foreach (string path in Directory.EnumerateFiles(Path.Combine(source, "transcripts"), "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(meeting, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
                }
                string destinationWave = Path.Combine(meeting, "verified-speaker-input.wav"); File.Copy(input, destinationWave); input = destinationWave;
            }
            var audio = new SpeakerAudioBinding(alignment.InputWaveSha256, alignment.SourceMixSha256, alignment.SampleRate,
                alignment.FrameCount, alignment.TimelinePolicy);
            var config = new SherpaDiarizationConfiguration(Path.Combine(bundle, "Tools/Sherpa/sherpa-onnx-offline-speaker-diarization.exe"),
                Path.Combine(bundle, "Models/Sherpa/pyannote-segmentation-3.0.onnx"), Path.Combine(bundle, "Models/Sherpa/3dspeaker-eres2net.onnx"),
                Threads: 2, ModelLicenseAcknowledged: true);
            var result = await SherpaDiarizationRuntime.RunAsync(new(meeting, original.MeetingId, input, audio, config, original.IsSynthetic));
            Need(result.Status == "Succeeded" && result.Analysis is not null, "native_cpu_completed_with_bound_eof_rule: " + result.Message);
            var analysis = result.Analysis!;
            var revision = SpeakerService.CreateRevision(meeting, original.RevisionId, analysis.AnalysisId);
            Need(string.Concat(revision.Segments.Select(segment => segment.Text)) == string.Concat(original.Segments.Select(segment => segment.Text)), "selected_transcript_all_characters_preserved");
            Need(revision.Audio == audio && revision.Segments.All(segment => segment.StartMs >= 0 && segment.EndMs <= original.MeetingDurationMs), "same_pause_policy_and_bounded_subtitle_times");
            Need(analysis.Warnings.Any(warning => warning.Contains("末帧中心", StringComparison.Ordinal)) &&
                revision.Warnings.Any(warning => warning.Contains("末帧中心", StringComparison.Ordinal)), "normalization_visible_and_persistent");
            using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
            var normalization = attempt.RootElement.GetProperty("timestampNormalization").Clone();
            Need(normalization.GetProperty("adjustments").GetArrayLength() > 0, "raw_end_and_adjusted_end_auditable");
            Need(existing.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value), "all_preexisting_source_files_hash_unchanged");
            Need(JsonSerializer.Serialize(SpeakerService.Load(meeting, revision.RevisionId).Segments) == JsonSerializer.Serialize(revision.Segments), "saved_revision_reopens_without_loss");
            outcome = new { result.Status, analysisId = analysis.AnalysisId, revisionId = revision.RevisionId, sourceRevisionId = original.RevisionId,
                inputFrames = audio.FrameCount, inputWaveSha256 = audio.InputWaveSha256, timelinePolicy = audio.TimelinePolicy,
                durationMs = original.MeetingDurationMs, originalSegments = original.Segments.Count, speakerSegments = revision.Segments.Count,
                speakerGroups = analysis.Speakers.Count, intervals = analysis.Intervals.Count, normalization,
                unchangedFiles = existing.Count, elapsedMs = analysis.Engine.ElapsedMilliseconds, result.AttemptDirectory,
                warnings = analysis.Warnings, nativeOutputSha256 = M3TranscriptTests.Hash(Path.Combine(result.AttemptDirectory, "native.stdout.txt")) };
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL",
            mode = args[4], scope = "User-requested local case; no audio capture, microphone, playback or upload. No speech text in this report.",
            checks, errors, outcome }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5 interval case PASS; text not printed" : string.Join("\n", errors));
        return errors.Count == 0 ? 0 : 1;
        void Need(bool value, string check) { if (!value) throw new InvalidDataException(check); checks.Add(check); }
    }
}
