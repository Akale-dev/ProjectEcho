using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Explicit public-voice native integration; synthetic subtitle text tests storage, not ASR quality.</summary>
internal static class M5SpeakerPipelineProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string manifest = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[7]);
        if (Directory.Exists(output)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var errors = new List<string>(); var runs = new List<object>();
        string? meeting = null; var watch = Stopwatch.StartNew();
        try
        {
            using var fixtureJson = JsonDocument.Parse(File.ReadAllText(manifest));
            var fixture = fixtureJson.RootElement.GetProperty("Fixtures").EnumerateArray().Single(row => row.GetProperty("Id").GetString() == args[2]);
            string wave = SyntheticAudioProbe.SafeProjectPath(root, fixture.GetProperty("Path").GetString()!);
            string inputHash = M3TranscriptTests.Hash(wave);
            var imported = AudioImportService.Import(wave, Path.Combine(output, "中文会议"));
            meeting = imported.MeetingDirectory;
            var prepared = AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256, Path.Combine(meeting, "speaker-input"));
            long duration = (long)Math.Round(imported.Mix.FrameCount * 1000m / imported.Mix.SampleRate);
            var gold = fixture.GetProperty("Gold").EnumerateArray().Select((row, index) => new Gold(index,
                (long)(row.GetProperty("Start").GetDecimal() * 1000), (long)(row.GetProperty("End").GetDecimal() * 1000), row.GetProperty("Speaker").GetString()!)).ToArray();
            string sourcePath = Path.Combine(output, "合成保真字幕.srt"); var srt = new StringBuilder();
            foreach (var row in gold) srt.Append(row.Index + 1).Append('\n').Append(Time(row.Start)).Append(" --> ").Append(Time(row.End))
                .Append('\n').Append($"测试文字{row.Index:D4}：收入没有下降，单位需要核对。🙂").Append("\n\n");
            File.WriteAllText(sourcePath, srt.ToString(), new UTF8Encoding(false));
            var source = TranscriptService.Import(new(meeting, Path.GetFileName(meeting), duration, sourcePath, "明确合成的保真/归属测试字幕；不是音频转写", IsSynthetic: true));
            string body = string.Concat(source.Segments.Select(segment => segment.Text));
            var audio = new SpeakerAudioBinding(prepared.Sha256, imported.Mix.WaveSha256, 16000, prepared.Frames, "file-v1");
            var configuration = new SherpaDiarizationConfiguration(args[3], args[4], args[5],
                KnownSpeakerCount: args[6].EndsWith("-known", StringComparison.Ordinal) ? gold.Select(row => row.Speaker).Distinct().Count() : null,
                Threads: 2, ModelLicenseAcknowledged: true);
            // Sixth payload is the requested mode. Short covers rerun/cancel/failure; long runs one whole-file analysis.
            bool longMode = args[6] is "long" or "long-known";
            var native = await SherpaDiarizationRuntime.RunAsync(new(meeting, source.MeetingId, prepared.Path, audio, configuration, true));
            runs.Add(new { phase = configuration.KnownSpeakerCount is null ? "automatic" : "known-count", native.Status, native.Message, native.AttemptDirectory, native.Analysis });
            Need(native.Status == "Succeeded" && native.Analysis is not null, "native_cpu_production_adapter_success");
            var analysis = native.Analysis!;
            var speaker = SpeakerService.CreateRevision(meeting, source.RevisionId, analysis.AnalysisId);
            Need(string.Concat(speaker.Segments.Select(segment => segment.Text)) == body, "all_source_characters_preserved_once");
            var assignments = speaker.Segments.Select(segment => new { segment.SourceSegmentId, segment.SpeakerId, segment.Status }).ToArray();
            int unassigned = assignments.Count(segment => segment.SpeakerId is null);
            var goldBySegment = source.Segments.Select((segment, index) => (segment.SegmentId, Gold: gold[index].Speaker)).ToDictionary(row => row.SegmentId, row => row.Gold);
            int split = assignments.Where(row => row.SpeakerId is not null).GroupBy(row => goldBySegment[row.SourceSegmentId]).Count(group => group.Select(row => row.SpeakerId).Distinct().Count() > 1);
            int merged = assignments.Where(row => row.SpeakerId is not null).GroupBy(row => row.SpeakerId).Count(group => group.Select(row => goldBySegment[row.SourceSegmentId]).Distinct().Count() > 1);
            Need(unassigned < assignments.Length && split == 0 && merged == 0, "nontrivial_subtitle_same_speaker_return_grouping");
            runs.Add(new { phase = "synthetic-subtitle-attribution", total = assignments.Length, unassigned, split, merged,
                unassignedFraction = unassigned / (double)assignments.Length, note = "Synthetic timed text placed at public-voice reference regions; not a claim of recognized speech accuracy." });
            var first = speaker.Segments.First(segment => segment.SpeakerId is not null);
            var named = SpeakerService.Rename(meeting, speaker.RevisionId, first.SpeakerId!, "人工确认甲");
            var separated = SpeakerService.SplitSpeaker(meeting, named.RevisionId, [first.SegmentId]);
            string newId = separated.Segments.First(segment => segment.SegmentId == first.SegmentId).SpeakerId!;
            var mergedBack = SpeakerService.Merge(meeting, separated.RevisionId, newId, first.SpeakerId!);
            var corrected = SpeakerService.Assign(meeting, mergedBack.RevisionId, first.SegmentId, first.SpeakerId);
            Need(corrected.Corrections.Count == 4 && corrected.Speakers.Single(s => s.SpeakerId == first.SpeakerId).Label == "人工确认甲", "rename_assign_split_merge_preserve_internal_id");
            foreach (bool labels in new[] { false, true }) foreach (string extension in new[] { ".txt", ".srt", ".vtt" })
            {
                string destination = Path.Combine(output, (labels ? "带标签" : "纯正文") + extension);
                SpeakerSubtitleExporter.Export(meeting, corrected.RevisionId, destination, labels);
                if (extension != ".txt")
                {
                    var roundtrip = TranscriptService.Import(new(meeting, source.MeetingId, duration, destination, IsSynthetic: true));
                    Need(roundtrip.Segments.Count == corrected.Segments.Count && roundtrip.Segments.Zip(corrected.Segments).All(pair =>
                        pair.First.StartMs == pair.Second.StartMs && pair.First.EndMs == pair.Second.EndMs &&
                        pair.First.SpeakerLabel is null && pair.First.Text == (labels ? "[说话人 " + SpeakerDisplay.Label(corrected, pair.Second) + "] " : "") + pair.Second.Text),
                        "roundtrip_" + extension + (labels ? "_labels_are_plain_text" : "_complete_text_and_time"));
                }
            }
            var hits = SpeakerSearchIndex.Search(meeting, "人工确认甲");
            Need(hits.Any(hit => hit.RevisionId == corrected.RevisionId), "name_search_finds_selected_correction");
            File.WriteAllBytes(Path.Combine(meeting, "diarization", "search.sqlite3"), [1, 2, 3]);
            Need(SpeakerSearchIndex.Search(meeting, "人工确认甲").SequenceEqual(hits), "corrupted_sqlite_rebuild_keeps_same_identity");
            Need(JsonSerializer.Serialize(SpeakerService.Load(meeting, corrected.RevisionId).Segments) == JsonSerializer.Serialize(corrected.Segments), "reopen_keeps_segments");
            if (!longMode)
            {
                var hinted = await SherpaDiarizationRuntime.RunAsync(new(meeting, source.MeetingId, prepared.Path, audio, configuration with { KnownSpeakerCount = gold.Select(row => row.Speaker).Distinct().Count() }, true));
                runs.Add(new { phase = "known-count-rerun", hinted.Status, hinted.Message, hinted.AttemptDirectory, hinted.Analysis });
                Need(hinted.Status == "Succeeded" && hinted.Analysis?.AnalysisId != analysis.AnalysisId, "known_count_rerun_independent_analysis");
                Need(SpeakerService.Load(meeting, corrected.RevisionId).Speakers.Single(s => s.SpeakerId == first.SpeakerId).Label == "人工确认甲", "rerun_does_not_overwrite_human_name");
                int before = SpeakerService.ListAnalyses(meeting).Count;
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var canceled = await SherpaDiarizationRuntime.RunAsync(new(meeting, source.MeetingId, prepared.Path, audio, configuration, true), cancellation.Token);
                runs.Add(new { phase = "actual-native-cancel", canceled.Status, canceled.Message, canceled.AttemptDirectory });
                Need(canceled.Status == "Cancelled" && canceled.Analysis is null && SpeakerService.ListAnalyses(meeting).Count == before, "cancel_publishes_no_completed_analysis");
                string invalid = Path.Combine(output, "invalid-model.onnx"); File.WriteAllText(invalid, "intentional invalid native model");
                var failed = await SherpaDiarizationRuntime.RunAsync(new(meeting, source.MeetingId, prepared.Path, audio, configuration with { EmbeddingModelPath = invalid }, true));
                runs.Add(new { phase = "actual-native-invalid-model", failed.Status, failed.Message, failed.AttemptDirectory });
                Need(failed.Status == "Failed" && failed.Analysis is null && SpeakerService.ListAnalyses(meeting).Count == before, "native_failure_isolated_no_completed_analysis");
            }
            Need(M3TranscriptTests.Hash(wave) == inputHash && M3TranscriptTests.Hash(imported.OriginalPath) == imported.OriginalSha256 &&
                M3TranscriptTests.Hash(prepared.Path) == prepared.Sha256 && string.Concat(TranscriptService.Load(meeting, source.RevisionId).Segments.Select(segment => segment.Text)) == body,
                "original_audio_and_transcript_hashes_unchanged");
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", elapsedMs = watch.ElapsedMilliseconds,
            sourceMode = "Public real voices with explicitly synthetic text; no capture/mic, not ASR accuracy", meeting, checks, errors, runs }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5 pipeline PASS" : string.Join("\n", errors));
        return errors.Count == 0 ? 0 : 1;
        void Need(bool condition, string check) { if (!condition) throw new InvalidDataException(check); checks.Add(check); }
    }
    private sealed record Gold(int Index, long Start, long End, string Speaker);
    private static string Time(long value) => string.Create(CultureInfo.InvariantCulture, $"{value / 3600000:D2}:{value / 60000 % 60:D2}:{value / 1000 % 60:D2},{value % 1000:D3}");
}
