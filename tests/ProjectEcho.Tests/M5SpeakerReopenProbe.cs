using System.Diagnostics;
using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;

/// <summary>Rechecks a completed real long run with the final managed implementation without repeating inference.</summary>
internal static class M5SpeakerReopenProbe
{
    internal static int Run(string[] args)
    {
        if (args.Length != 3) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot(), summaryPath = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[2]);
        if (Directory.Exists(output)) throw new IOException("Use new evidence directory.");
        Directory.CreateDirectory(output); var watch = Stopwatch.StartNew(); var checks = new List<string>(); var errors = new List<string>();
        var mappings = new List<object>();
        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(summaryPath));
            if (report.RootElement.GetProperty("status").GetString() != "PASS") throw new InvalidDataException("Input pipeline was not completed.");
            string meeting = SyntheticAudioProbe.SafeProjectPath(root, report.RootElement.GetProperty("meeting").GetString()!);
            var originals = Directory.EnumerateFiles(meeting, "*.json", SearchOption.AllDirectories).ToDictionary(path => path, M3TranscriptTests.Hash);
            var versions = SpeakerService.ListRevisions(meeting);
            Need(versions.Count >= 5, "all_immutable_versions_load_under_final_validation");
            foreach (var revision in versions.Where(version => version.ParentRevisionId is null))
            {
                var source = TranscriptService.Load(meeting, revision.SourceTranscriptRevisionId);
                var analysis = SpeakerService.LoadAnalysis(meeting, revision.AnalysisId);
                var evidence = SpeakerAlignmentReader.Read(meeting, source, analysis.Audio);
                var mapped = SpeakerMapper.Map(source, analysis, evidence);
                Need(mapped.Count == revision.Segments.Count && mapped.Zip(revision.Segments).All(pair => pair.First.Text == pair.Second.Text &&
                    pair.First.StartMs == pair.Second.StartMs && pair.First.EndMs == pair.Second.EndMs && pair.First.SpeakerId == pair.Second.SpeakerId && pair.First.Status == pair.Second.Status),
                    "final_mapping_identical_to_completed_real_run");
                mappings.Add(new { revision.RevisionId, segments = mapped.Count, durationMs = revision.MeetingDurationMs,
                    policy = revision.Audio.TimelinePolicy, characters = mapped.Sum(segment => segment.Text.Length), uncertain = mapped.Count(segment => segment.NeedsConfirmation) });
            }
            foreach (var run in report.RootElement.GetProperty("runs").EnumerateArray().Where(run => run.GetProperty("phase").GetString() is "automatic" or "known-count"))
            {
                string attempt = SyntheticAudioProbe.SafeProjectPath(root, run.GetProperty("AttemptDirectory").GetString()!);
                var analysis = SpeakerService.LoadAnalysis(meeting, run.GetProperty("Analysis").GetProperty("AnalysisId").GetString()!);
                var parsed = SherpaDiarizationRuntime.ParseOutput(File.ReadAllText(Path.Combine(attempt, "native.stdout.txt")), analysis.Audio.FrameCount * 1000 / 16000);
                var overlap = SherpaDiarizationRuntime.DetectOverlaps(parsed);
                var order = SherpaDiarizationRuntime.OrderSpeakers(parsed, overlap);
                Need(parsed.Count == analysis.Intervals.Count && parsed.Select((interval, index) => interval.StartMs == analysis.Intervals[index].StartMs &&
                    interval.EndMs == analysis.Intervals[index].EndMs && overlap[index] == analysis.Intervals[index].Overlap &&
                    analysis.Speakers[order.ToList().IndexOf(interval.Speaker)].SpeakerId == analysis.Intervals[index].SpeakerId).All(value => value),
                    "final_native_parser_linear_overlap_and_first_reliable_order_match_full_raw");
            }
            var latest = versions.OrderByDescending(version => version.Corrections.Count).First();
            foreach (string extension in new[] { ".txt", ".srt", ".vtt" }) SpeakerSubtitleExporter.Export(meeting, latest.RevisionId, Path.Combine(output, "final" + extension));
            Need(SpeakerSearchIndex.Search(meeting, "人工确认甲").Any(hit => hit.RevisionId == latest.RevisionId), "final_sqlite_name_reference");
            Need(originals.All(pair => M3TranscriptTests.Hash(pair.Key) == pair.Value), "all_existing_authority_and_attempt_json_unchanged");
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", elapsedMs = watch.ElapsedMilliseconds,
            testedCoreSha256 = M3TranscriptTests.Hash(typeof(SpeakerService).Assembly.Location), checks, mappings, errors,
            note = "Final managed code validates/remaps saved real native output; no new inference or capture." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5 final reopen/remap PASS" : string.Join("\n", errors));
        return errors.Count == 0 ? 0 : 1;
        void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); checks.Add(message); }
    }
}
