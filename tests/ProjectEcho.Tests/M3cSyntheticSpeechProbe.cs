using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Explicit local-file-only model test against fixed SAPI speech; never plays or captures audio.</summary>
internal static class M3cSyntheticSpeechProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--m3c-synthetic-speech-probe") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string manifestPath = SyntheticAudioProbe.SafeProjectPath(root, args[3]);
        string output = Path.GetDirectoryName(manifestPath)!;
        string reportPath = Path.Combine(output, "asr-report.json");
        if (File.Exists(reportPath)) throw new IOException("ASR report already exists; use a new fixture folder.");
        var errors = new List<string>();
        var evidence = new List<object>();
        var immutable = new Dictionary<string, string>();
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var data = manifest.RootElement;
        if (data.GetProperty("Schema").GetString() != "m3c-synthetic-speech-fixtures-1" ||
            !data.GetProperty("IsSynthetic").GetBoolean() || data.GetProperty("AudioPlayed").GetBoolean() ||
            data.GetProperty("CaptureStarted").GetBoolean() || data.GetProperty("MicrophoneEnabled").GetBoolean())
            throw new InvalidDataException("Expected local generated speech fixtures with no playback or capture.");
        string modelHash = Hash(args[2]);
        immutable.Add(Path.GetFullPath(args[1]), Hash(args[1])); immutable.Add(Path.GetFullPath(args[2]), modelHash);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        foreach (var fixture in data.GetProperty("Fixtures").EnumerateArray())
        {
            string id = fixture.GetProperty("Id").GetString()!;
            if (id is not ("mandarin" or "english" or "mixed")) throw new InvalidDataException("Unknown fixture id.");
            string input = SyntheticAudioProbe.SafeProjectPath(root, fixture.GetProperty("WavePath").GetString()!);
            string expectedHash = fixture.GetProperty("Sha256").GetString()!;
            if (Hash(input) != expectedHash) throw new InvalidDataException("Synthetic input hash mismatch.");
            immutable.Add(input, expectedHash);
            string expected = fixture.GetProperty("ExpectedText").GetString()!;
            var progress = new ConcurrentQueue<LocalTranscriptionProgress>();
            try
            {
                var imported = AudioImportService.Import(input, Path.Combine(output, "recordings"), deadline.Token);
                immutable.Add(imported.OriginalPath, imported.OriginalSha256);
                immutable.Add(imported.Mix.WavePath, imported.Mix.WaveSha256);
                long milliseconds = (long)Math.Round(imported.Mix.FrameCount * 1000d / imported.Mix.SampleRate);
                var prepared = AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256,
                    Path.Combine(imported.MeetingDirectory, "transcripts", "inputs"), deadline.Token);
                immutable.Add(prepared.Path, prepared.Sha256);
                var config = new LocalTranscriptionConfiguration(WhisperCppLocalTranscriber.BackendId,
                    Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), fixture.GetProperty("Language").GetString()!, 2,
                    "base (multilingual), synthetic Windows SAPI fixture", modelHash, true);
                var request = new LocalTranscriptionRequest(imported.MeetingDirectory, Path.GetFileName(imported.MeetingDirectory),
                    milliseconds, prepared.Path, prepared.Sha256, config, IsSynthetic: true);
                Console.WriteLine("Synthetic speech ASR: " + id);
                var result = await Task.Run(() => LocalTranscriptionBackends.Default.RunAsync(request, deadline.Token, new InlineProgress(progress.Enqueue)));
                bool pipelinePass = result.Status == LocalTranscriptionStatus.Succeeded && result.Revision is { IsSynthetic: true } revision &&
                    revision.Segments.Count > 0 && revision.Segments.All(segment => segment.StartMs >= 0 && segment.EndMs > segment.StartMs &&
                        segment.EndMs <= milliseconds && segment.TimestampQuality == "approximate");
                if (!pipelinePass) errors.Add(id + ":production_pipeline_or_subtitle_range_failed:" + result.Message);
                string actual = result.Revision is null ? "" : string.Join(" ", result.Revision.Segments.Select(segment => segment.Text));
                string normalizedReference = Normalize(expected), normalizedHypothesis = Normalize(actual);
                // A rejected timestamp contract means no validated hypothesis was published.
                // Do not turn that pipeline failure into a claimed 100% model word error.
                var characterScore = result.Revision is null ? null : Score(normalizedReference.Select(character => character.ToString()).ToArray(),
                    normalizedHypothesis.Select(character => character.ToString()).ToArray());
                var wordScore = id == "english" && result.Revision is not null ? Score(Words(expected), Words(actual)) : null;
                int[] percents = progress.Where(value => value.Percent.HasValue).Select(value => value.Percent!.Value).ToArray();
                bool progressValid = percents.All(value => value is >= 0 and <= 100) && percents.Zip(percents.Skip(1)).All(pair => pair.First < pair.Second);
                if (!progressValid) errors.Add(id + ":invalid_reported_progress");
                if (result.Revision is not null)
                {
                    TranscriptSubtitleExporter.Export(imported.MeetingDirectory, result.Revision.RevisionId, Path.Combine(output, id + ".srt"));
                    TranscriptSubtitleExporter.ExportText(imported.MeetingDirectory, result.Revision.RevisionId, Path.Combine(output, id + ".txt"));
                }
                evidence.Add(new { Id = id, Language = config.Language, DurationMilliseconds = milliseconds, PipelinePassed = pipelinePass,
                    Reference = expected, Hypothesis = actual, NormalizedReference = normalizedReference, NormalizedHypothesis = normalizedHypothesis,
                    HasValidatedTranscript = result.Revision is not null, CharacterErrorRate = characterScore, EnglishWordErrorRate = wordScore,
                    result.Status, result.ElapsedMilliseconds, result.AttemptDirectory, result.Message,
                    SegmentCount = result.Revision?.Segments.Count, Segments = result.Revision?.Segments,
                    EngineProgress = progress.ToArray(), EngineReportedPercent = percents.Length > 0, ProgressValid = progressValid });
            }
            catch (Exception ex) { errors.Add(id + ":" + ex); }
        }
        var hashes = immutable.Select(pair => new { Path = pair.Key, ExpectedSha256 = pair.Value, Unchanged = Hash(pair.Key) == pair.Value }).ToArray();
        if (hashes.Any(item => !item.Unchanged)) errors.Add("source_or_model_changed");
        var report = new { Schema = "m3c-synthetic-speech-asr-1", Status = errors.Count == 0 ? "PASS_SYNTHETIC_PIPELINE_QUALITY_MEASURED" : "FAIL",
            CheckedAtUtc = DateTimeOffset.UtcNow, Os = Environment.OSVersion.Version.ToString(), Runtime = Environment.Version.ToString(),
            Fixtures = evidence, OriginalHashes = hashes, Errors = errors,
            CaptureStarted = false, MicrophoneEnabled = false, AudioPlayed = false, Uploaded = false,
            QualityInterpretation = "Observed edit rates on three fixed locally synthesized samples only. No natural meeting accuracy threshold or representative accuracy claim. Character normalization removes punctuation and whitespace and lowercases letters; it does not equate spelled numbers with digits, convert Chinese scripts or translate words. English WER uses lowercase alphanumeric word tokens. Mixed-language CER treats each remaining letter/digit/Han character as one symbol. Differences may include transcription, translation, numeral spelling or script variants.",
            TimestampInterpretation = "Model times are approximate, not forced alignment or speaker identity validation." };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report.Status + ": " + reportPath);
        return errors.Count == 0 ? 0 : 1;
    }

    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string[] Words(string value) => Regex.Matches(value.ToLowerInvariant(), "[a-z0-9]+(?:'[a-z]+)?").Select(match => match.Value).ToArray();
    private static EditScore Score(string[] reference, string[] hypothesis)
    {
        var matrix = new int[reference.Length + 1, hypothesis.Length + 1];
        for (int i = 0; i <= reference.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= hypothesis.Length; j++) matrix[0, j] = j;
        for (int i = 1; i <= reference.Length; i++)
            for (int j = 1; j <= hypothesis.Length; j++)
                matrix[i, j] = Math.Min(matrix[i - 1, j] + 1, Math.Min(matrix[i, j - 1] + 1,
                    matrix[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1)));
        int row = reference.Length, column = hypothesis.Length, substitutions = 0, insertions = 0, deletions = 0;
        while (row > 0 || column > 0)
        {
            if (row > 0 && column > 0 && matrix[row, column] == matrix[row - 1, column - 1] + (reference[row - 1] == hypothesis[column - 1] ? 0 : 1))
            { if (reference[row - 1] != hypothesis[column - 1]) substitutions++; row--; column--; }
            else if (row > 0 && matrix[row, column] == matrix[row - 1, column] + 1) { deletions++; row--; }
            else { insertions++; column--; }
        }
        return new(reference.Length, hypothesis.Length, substitutions, deletions, insertions,
            reference.Length == 0 ? null : (substitutions + deletions + insertions) / (double)reference.Length);
    }
    private sealed record EditScore(int ReferenceSymbols, int HypothesisSymbols, int Substitutions, int Deletions, int Insertions, double? ErrorRate);
    private sealed class InlineProgress(Action<LocalTranscriptionProgress> report) : IProgress<LocalTranscriptionProgress>
    { public void Report(LocalTranscriptionProgress value) => report(value); }
}
