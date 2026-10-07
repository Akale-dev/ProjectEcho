using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Explicit real Windows ASR/alignment test against public or fixed local SAPI fixtures, never capture.</summary>
internal static class M4RealQwenProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static async Task<int> RunAsync(string[] args)
    {
        bool python = args.Length == 9 && args[7] == "--alignment-python";
        if ((!python && args.Length is not (7 or 8)) || args[0] != "--m4-real-qwen-probe" || args[1] is not ("short" or "long" or "three-minute") ||
            args.Length == 8 && args[7] != "--alignment-vulkan") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[6]);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Use a new empty evidence directory; previous results are retained.");
        Directory.CreateDirectory(output);
        string executable = Path.GetFullPath(args[2]), model = Path.GetFullPath(args[3]);
        string aligner = Path.GetFullPath(args[4]), alignmentModel = Path.GetFullPath(args[5]);
        var config = new LocalTranscriptionConfiguration("qwen3-asr", executable, model, "auto", 6,
            "Qwen3-ASR-0.6B-Q8_0", ModelLicenseAcknowledged: true, Hardware: LocalTranscriptionHardware.Vulkan,
            GpuDeviceIndex: 0, QwenAlignment: python ? new("", "", Engine: QwenAlignmentEngine.OfficialPython,
                Python: new(aligner, alignmentModel, Path.GetFullPath(args[8]))) : new(aligner, alignmentModel,
                args.Contains("--alignment-vulkan") ? LocalTranscriptionHardware.Vulkan : LocalTranscriptionHardware.Cpu));
        var immutable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<object>(); var errors = new List<string>(); var runs = new List<object>();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromHours(6));
        using var samplerStop = new CancellationTokenSource();
        var ownedPids = new ConcurrentDictionary<int, byte>();
        Task sampler = Task.CompletedTask;
        string phase = "setup";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Real native probe requires Windows.");
            foreach (string path in new[] { executable, model, aligner }) immutable.TryAdd(path, Hash(path));
            if (python)
            {
                foreach (string path in Directory.GetFiles(alignmentModel)) immutable.Add(path, Hash(path));
                immutable.Add(Path.GetFullPath(args[8]), Hash(Path.GetFullPath(args[8])));
            }
            else immutable.Add(alignmentModel, Hash(alignmentModel));
            if (immutable[model] != "f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8")
                throw new InvalidDataException("This probe requires the pinned external Qwen 0.6B Q8_0 model.");
            var fixtures = ReadFixtures(root);
            foreach (var fixture in fixtures) immutable.Add(fixture.Path, Hash(fixture.Path));
            sampler = Task.Run(async () =>
            {
                using var stream = new StreamWriter(new FileStream(Path.Combine(output, "process-resources.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                while (!samplerStop.IsCancellationRequested)
                {
                    foreach (string name in new[] { Path.GetFileNameWithoutExtension(executable), Path.GetFileNameWithoutExtension(aligner) }.Distinct())
                    foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            string? path = process.MainModule?.FileName;
                            if (process.StartTime.ToUniversalTime() < started.UtcDateTime ||
                                (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, aligner, StringComparison.OrdinalIgnoreCase))) continue;
                            if (ownedPids.TryAdd(process.Id, 0)) File.AppendAllText(Path.Combine(output, "owned-pids.txt"), process.Id + "\n");
                            await stream.WriteLineAsync(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Phase = phase, Pid = process.Id,
                                Executable = Path.GetFileName(path), process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64,
                                CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds }));
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    await stream.FlushAsync();
                    try { await Task.Delay(200, samplerStop.Token); } catch (OperationCanceledException) { break; }
                }
            });

            if (args[1] == "short")
            {
                foreach (var fixture in fixtures)
                {
                    phase = fixture.Id;
                    var request = Prepare(fixture.Path, fixture.Id);
                    var result = await Run(fixture.Id, request, deadline.Token);
                    Validate(result, request, fixture.Id);
                    if (result.Revision is not { } revision) continue;
                    string joined = string.Join("", revision.Segments.Select(s => s.Text));
                    if (fixture.Id == "mixed") Check(joined.Contains("最后一句", StringComparison.Ordinal) && joined.Contains("not increase", StringComparison.OrdinalIgnoreCase), "mixed_tail_and_negation_present");
                    if (fixture.Id == "mandarin") Check(joined.Contains("没有增长", StringComparison.Ordinal) && joined.Contains("二十万元", StringComparison.Ordinal), "mandarin_negation_budget_present");
                    foreach (string extension in new[] { ".srt", ".vtt" })
                    {
                        string export = Path.Combine(output, fixture.Id + extension);
                        TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, export);
                        var roundTrip = TranscriptService.Import(new(request.MeetingDirectory, request.MeetingId, request.MeetingDurationMs, export, IsSynthetic: true));
                        // Existing subtitle writer/parser trims cue-edge whitespace. Raw Qwen
                        // text remains separately immutable; no letters/punctuation/times are normalized.
                        Check(revision.Segments.Select(s => (s.StartMs, s.EndMs, Text: s.Text.Trim())).SequenceEqual(roundTrip.Segments.Select(s => (s.StartMs, s.EndMs, Text: s.Text.Trim()))), fixture.Id + "_" + extension[1..] + "_roundtrip_sentence_content_and_exact_milliseconds");
                    }
                    if (fixture.Id != "mandarin") continue;
                    var reviewed = TranscriptService.CreateRevision(request.MeetingDirectory, revision.RevisionId,
                        revision.Segments.Select((s, index) => index == 0 ? s with { Text = s.Text + "（合成修订保留测试）" } : s).ToArray(), "Synthetic edit, not human accuracy validation.");
                    Preserve(Path.Combine(request.MeetingDirectory, "transcripts", "original"));
                    Preserve(Path.Combine(request.MeetingDirectory, "transcripts", "reviewed"));
                    int before = TranscriptService.ListRevisions(request.MeetingDirectory).Count;
                    using (var cancel = new CancellationTokenSource())
                    {
                        var progress = new InlineProgress(value => { if (value.Stage == LocalTranscriptionStage.Aligning) cancel.Cancel(); });
                        var cancelled = await Run("cancel_on_alignment", request, cancel.Token, progress);
                        Check(cancelled.Status == LocalTranscriptionStatus.Cancelled && cancelled.Revision is null && TranscriptService.ListRevisions(request.MeetingDirectory).Count == before, "cancelled_attempt_publishes_no_version");
                    }
                    var failure = await Run("invalid_device", request with { Configuration = config with { GpuDeviceIndex = 63 } }, deadline.Token);
                    Check(failure.Status == LocalTranscriptionStatus.Failed && failure.Revision is null && TranscriptService.ListRevisions(request.MeetingDirectory).Count == before, "failed_attempt_publishes_no_version");
                    var retry = await Run("retry_after_cancel_failure", request, deadline.Token);
                    Validate(retry, request, "retry");
                    Check(retry.AttemptDirectory != result.AttemptDirectory && retry.Revision?.RevisionId != revision.RevisionId, "retry_uses_independent_attempt_and_original");
                    var reopened = TranscriptService.Load(request.MeetingDirectory, reviewed.RevisionId);
                    Check(reopened.ParentRevisionId == revision.RevisionId && reopened.Segments.SequenceEqual(reviewed.Segments), "reviewed_text_parent_and_sentence_timing_preserved");
                }
                string padded = Path.Combine(output, "九十秒长会边界小样.wav");
                CreateLongWave(padded, fixtures.Where(f => f.Id is "zh-short" or "mandarin" or "english").ToArray(), output, 3);
                immutable.Add(padded, Hash(padded));
                var paddedRequest = Prepare(padded, "padded-ninety-seconds");
                var paddedResult = await Run("padded_ninety_second_baseline", paddedRequest, deadline.Token);
                Validate(paddedResult, paddedRequest, "padded_ninety_second");
            }
            else
            {
                // Every 30-second window contains a complete known fixture and real zero padding.
                // This is a repetitive synthetic stress track, not natural three-hour meeting quality.
                bool longRun = args[1] == "long";
                int expectedWindows = longRun ? 360 : 6;
                string runId = longRun ? "three_hour" : "three_minute", title = longRun ? "三小时完整推理" : "三分钟完整推理";
                phase = "construct_" + runId;
                var eligible = fixtures.Where(f => f.Id is "zh-short" or "mandarin" or "english").ToArray();
                string input = Path.Combine(output, title + ".wav");
                CreateLongWave(input, eligible, output, expectedWindows);
                phase = runId + "_full_asr_then_alignment";
                var request = Prepare(input, title);
                immutable.Add(input, Hash(input));
                Check(request.MeetingDurationMs == expectedWindows * 30000L, runId + "_exact_input_duration_ms");
                var result = await Run(runId + "_full_inference", request, deadline.Token);
                Validate(result, request, runId);
                if (result.Revision is { } revision)
                {
                    Check(revision.Segments.First().StartMs < 15000 && revision.Segments.Last().EndMs > (expectedWindows - 1) * 30000L, "first_and_final_windows_have_timed_text");
                    var windows = revision.Segments.GroupBy(s => s.StartMs!.Value / 30000).ToDictionary(g => g.Key, g => g.Count());
                    Check(Enumerable.Range(0, expectedWindows).All(i => windows.ContainsKey(i)), "all_" + expectedWindows + "_spoken_windows_have_aligned_sentences");
                    TranscriptSubtitleExporter.Export(request.MeetingDirectory, revision.RevisionId, Path.Combine(output, runId + ".srt"));
                    TranscriptSubtitleExporter.ExportText(request.MeetingDirectory, revision.RevisionId, Path.Combine(output, runId + ".txt"));
                    var reopened = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
                    Check(reopened.Segments.SequenceEqual(revision.Segments), runId + "_reopen_exact_sentences");
                }
            }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine("M4 real Qwen probe: " + ex.Message); }
        finally
        {
            samplerStop.Cancel(); await sampler;
            foreach (var pair in immutable)
            {
                try { Check(Hash(pair.Key) == pair.Value, "immutable_sha256:" + pair.Key); }
                catch (Exception ex) { errors.Add("Immutable recheck: " + ex.Message); }
            }
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                Schema = "m4-real-qwen-probe-1", Mode = args[1], Status = errors.Count == 0 ? "PASS" : "FAIL", StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow,
                Environment = new { OS = Environment.OSVersion.ToString(), Build = os?.GetValue("CurrentBuildNumber"), Ubr = os?.GetValue("UBR"), Runtime = RuntimeInformation.FrameworkDescription },
                Config = config, Runs = runs, Checks = checks, Errors = errors, Immutable = immutable, OwnedNativePids = ownedPids.Keys.Order().ToArray(),
                CaptureStarted = false, MicrophoneEnabled = false, AudioPlayed = false, Uploaded = false,
                Limitations = new[] { "Public natural and fixed SAPI clips only; no representative meeting accuracy claim.", "Three-hour mode repeats complete voiced fixtures plus silence; validates real full-model processing/coverage/resources, not natural long-meeting accuracy.", "Times are independent forced-aligner acoustic estimates. Text correctness and boundary listening remain distinct.", "GPU VRAM requires independent companion counter evidence; absence is NOT_RUN." }
            }, Json));
        }
        Console.WriteLine($"M4 REAL QWEN {(errors.Count == 0 ? "PASS" : "FAIL")}: {Path.Combine(output, "report.json")}");
        return errors.Count == 0 ? 0 : 1;

        void Check(bool passed, string name) { checks.Add(new { Name = name, Passed = passed }); if (!passed) errors.Add(name); }
        void Preserve(string directory) { foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) immutable.TryAdd(path, Hash(path)); }
        LocalTranscriptionRequest Prepare(string source, string id)
        {
            var imported = AudioImportService.Import(source, Path.Combine(output, "中文会议-" + id), deadline.Token);
            string meeting = imported.MeetingDirectory;
            immutable.Add(imported.OriginalPath, imported.OriginalSha256); immutable.Add(imported.Mix.WavePath, imported.Mix.WaveSha256);
            var prepared = AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256,
                Path.Combine(meeting, "transcripts", "inputs"), deadline.Token);
            immutable.Add(prepared.Path, prepared.Sha256);
            long ms = checked((long)Math.Round(imported.Mix.FrameCount * 1000m / imported.Mix.SampleRate));
            return new(meeting, Path.GetFileName(meeting), ms, prepared.Path, prepared.Sha256, config,
                IsSynthetic: true, SourceMixSha256: imported.Mix.WaveSha256, TimelinePolicy: "file-v1");
        }
        async Task<LocalTranscriptionResult> Run(string id, LocalTranscriptionRequest request, CancellationToken token, IProgress<LocalTranscriptionProgress>? extra = null)
        {
            Console.WriteLine("M4 REAL QWEN starting: " + id);
            var events = new List<LocalTranscriptionProgress>();
            var progress = new InlineProgress(value => { phase = id + ":" + value.Stage; events.Add(value);
                File.AppendAllText(Path.Combine(output, "progress.jsonl"), JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow, Run = id, Stage = value.Stage.ToString(), value.Percent }) + "\n");
                extra?.Report(value); if (value.Percent is { } percent && percent % 10 == 0) Console.WriteLine($"{id}: {value.Stage} {percent}%"); });
            var result = await Task.Run(() => new QwenTranscriber().RunAsync(request, token, progress), CancellationToken.None);
            JsonElement? attempt = File.Exists(Path.Combine(result.AttemptDirectory, "attempt.json")) ? JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json"))).RootElement.Clone() : null;
            runs.Add(new { Id = id, Status = result.Status.ToString(), result.Message, result.ElapsedMilliseconds, result.AttemptDirectory, Revision = result.Revision, Progress = events, Attempt = attempt });
            Console.WriteLine($"{id}: {result.Status}; {result.ElapsedMilliseconds}ms; {result.Message}");
            File.WriteAllText(Path.Combine(output, "runs-in-progress.json"), JsonSerializer.Serialize(runs, Json));
            return result;
        }
        void Validate(LocalTranscriptionResult result, LocalTranscriptionRequest request, string id)
        {
            Check(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision is not null, id + "_full_pipeline_success");
            if (result.Revision is not { } revision) return;
            Check(revision.MeetingId == request.MeetingId && revision.Alignment?.InputWaveSha256 == request.ExpectedWaveSha256 && revision.Alignment.TimelinePolicy == "file-v1", id + "_identity_hash_and_single_timeline");
            Check(revision.Segments.Count > 0 && revision.Segments.All(s => s.StartMs >= 0 && s.EndMs > s.StartMs && s.EndMs <= request.MeetingDurationMs && s.TimestampQuality == "approximate"), id + "_positive_sentence_intervals_within_audio");
            Check(TranscriptService.Load(request.MeetingDirectory, revision.RevisionId).Segments.SequenceEqual(revision.Segments), id + "_disk_reopen_preserves_sentences");
        }
    }

    private static List<Fixture> ReadFixtures(string root)
    {
        string pub = Path.Combine(root, ".tools", "transcribe-source", "samples");
        var result = new List<Fixture> { new("zh-short", Path.Combine(pub, "zh-short.wav")), new("zh-long", Path.Combine(pub, "zh-long.wav")), new("english", Path.Combine(pub, "jfk.wav")) };
        string[] hashes = ["e79d33c7a26e88f5ceb46478182c6ce1157c5c5a5d7f28ef2bb54af5e3fdf1e4", "e98bb07b0e9fb20e4dea9636f52416ab0c8f7a68284981fbf1afb3c8949ea3b4", "59dfb9a4acb36fe2a2affc14bacbee2920ff435cb13cc314a08c13f66ba7860e"];
        for (int i = 0; i < result.Count; i++) if (Hash(result[i].Path) != hashes[i]) throw new InvalidDataException("Public input hash mismatch: " + result[i].Id);
        string manifest = Path.Combine(root, ".local", "M4", "runtime", "合成样本", "fixtures.json");
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!json.RootElement.GetProperty("Synthetic").GetBoolean() || json.RootElement.GetProperty("CaptureStarted").GetBoolean() || json.RootElement.GetProperty("AudioPlayed").GetBoolean()) throw new InvalidDataException("Expected file-only synthetic fixture manifest.");
        foreach (var row in json.RootElement.GetProperty("Fixtures").EnumerateArray())
        {
            string id = row.GetProperty("Id").GetString()!;
            if (id is not ("mandarin" or "mixed")) throw new InvalidDataException("Unknown synthetic fixture.");
            string path = SyntheticAudioProbe.SafeProjectPath(root, row.GetProperty("Path").GetString()!);
            if (Hash(path) != row.GetProperty("Sha256").GetString()) throw new InvalidDataException("Synthetic fixture hash mismatch.");
            result.Add(new(id, path));
        }
        return result;
    }
    private static void CreateLongWave(string path, IReadOnlyList<Fixture> fixtures, string output, int windowCount = 360)
    {
        const int framesPerWindow = 480000;
        var blocks = new List<byte[]>();
        foreach (var fixture in fixtures)
        {
            using var reader = new M2WaveReader(fixture.Path);
            if (reader.Format.SampleRate != 16000 || reader.Format.Channels != 1 || reader.Format.BitsPerSample != 16 || reader.FrameCount > framesPerWindow) throw new InvalidDataException("Long probe requires complete <=30s 16k mono PCM16 clips.");
            byte[] bytes = new byte[framesPerWindow * 2]; reader.ReadExactly(bytes.AsSpan(0, checked((int)reader.DataLength))); blocks.Add(bytes);
        }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + framesPerWindow * 2 * windowCount); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(framesPerWindow * 2 * windowCount);
        for (int i = 0; i < windowCount; i++) file.Write(blocks[i % blocks.Count]); file.Flush(true);
        File.WriteAllText(Path.Combine(output, "long-fixture-map.json"), JsonSerializer.Serialize(new { SyntheticStress = true, SampleRate = 16000, FramesPerWindow = framesPerWindow, WindowCount = windowCount,
            Windows = Enumerable.Range(0, windowCount).Select(i => new { Index = i, StartFrame = (long)i * framesPerWindow, FrameCount = framesPerWindow, Fixture = fixtures[i % fixtures.Count].Id, SourceSha256 = Hash(fixtures[i % fixtures.Count].Path), Padding = "digital-zero after complete source PCM" }) }, Json));
    }
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private sealed record Fixture(string Id, string Path);
    private sealed class InlineProgress(Action<LocalTranscriptionProgress> action) : IProgress<LocalTranscriptionProgress> { public void Report(LocalTranscriptionProgress value) => action(value); }
}
