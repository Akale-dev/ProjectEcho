using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

internal static class M5SpeakerSavedPolicyProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string sourceRoot = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string allowed = Path.Combine(root, ".local", "M4Perf", "pause-avx2-01", "recordings");
        if (!string.Equals(sourceRoot.TrimEnd(Path.DirectorySeparatorChar), allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only the existing public/synthetic pause-policy fixtures are allowed.");
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[5]);
        if (Directory.Exists(output)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(output); var reports = new List<object>(); var errors = new List<string>();
        try
        {
            foreach (string name in new[] { "pause-excluded", "legacy-elapsed" })
            {
                string source = Path.Combine(sourceRoot, name), meeting = Path.Combine(output, name);
                var hashes = new Dictionary<string, string>();
                foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("No linked inputs.");
                    string relative = Path.GetRelativePath(source, path);
                    hashes.Add(relative, M3TranscriptTests.Hash(path));
                    string destination = Path.Combine(meeting, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(path, destination);
                }
                var transcript = TranscriptService.ListRevisions(meeting).First(revision => revision.Alignment is not null && revision.Provider.Contains("Qwen", StringComparison.Ordinal));
                var alignment = transcript.Alignment!;
                string wave = Directory.EnumerateFiles(meeting, "*.wav", SearchOption.AllDirectories)
                    .First(path => M3TranscriptTests.Hash(path) == alignment.InputWaveSha256);
                string policy = name == "pause-excluded" ? "pause-excluded-v1" : "elapsed-v1";
                Need(alignment.TimelinePolicy == policy && transcript.MeetingDurationMs == (name == "pause-excluded" ? 11000 : 28345), "Original timeline policy or duration changed.");
                var audio = new SpeakerAudioBinding(alignment.InputWaveSha256, alignment.SourceMixSha256, 16000, alignment.FrameCount, policy);
                var result = await SherpaDiarizationRuntime.RunAsync(new(meeting, transcript.MeetingId, wave, audio,
                    new(args[2], args[3], args[4], Threads: 2, ModelLicenseAcknowledged: true), true));
                Need(result.Status == "Succeeded" && result.Analysis is not null, result.Message);
                var revision = SpeakerService.CreateRevision(meeting, transcript.RevisionId, result.Analysis!.AnalysisId);
                Need(revision.Audio == audio && string.Concat(revision.Segments.Select(s => s.Text)) == string.Concat(transcript.Segments.Select(s => s.Text)) &&
                    revision.Segments.All(s => s.StartMs >= 0 && s.EndMs <= transcript.MeetingDurationMs), "Pause offset reapplied or source text lost.");
                SpeakerSubtitleExporter.Export(meeting, revision.RevisionId, Path.Combine(output, name + ".srt"));
                Need(SpeakerService.Load(meeting, revision.RevisionId).Audio.TimelinePolicy == policy, "Policy lost on reopen.");
                Need(hashes.All(row => M3TranscriptTests.Hash(Path.Combine(source, row.Key)) == row.Value), "Original fixture modified.");
                reports.Add(new { name, status = "PASS", policy, transcript.MeetingDurationMs, alignment.FrameCount, nativeAttempt = result.AttemptDirectory,
                    analysis = result.Analysis, sourceTranscript = transcript.RevisionId, speakerRevision = revision.RevisionId,
                    segments = revision.Segments.Count, unchangedSourceFiles = hashes.Count, sourceMode = "Existing public JFK/synthetic QPC fixture; real native CPU; no capture" });
            }
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", reports, errors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5 saved policies PASS" : string.Join("\n", errors));
        return errors.Count == 0 ? 0 : 1;
    }
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
