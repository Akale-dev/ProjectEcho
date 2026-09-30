using System.Diagnostics;
using System.Text.Json;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Export;

/// <summary>Read-only original verification and derived export after a completed two-hour mic test.</summary>
internal static class M3cLongAudioExportProbe
{
    internal static int Run(string[] args) => M2LongRunProbe.RunMicrophoneLongExport(args);
}

internal static partial class M2LongRunProbe
{
    internal static int RunMicrophoneLongExport(string[] args)
    {
        if (args.Length != 2 || args[0] != "--m3c-revalidate-mic-long") return 2;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string originalReport = SyntheticAudioProbe.SafeProjectPath(root, args[1]);
        string originalRun = Path.GetDirectoryName(originalReport)!;
        string allowed = Path.Combine(root, ".local", "M3cMicrophoneEndurance") + Path.DirectorySeparatorChar;
        if (!originalRun.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(originalRun).StartsWith("long-", StringComparison.Ordinal) ||
            Path.GetFileName(originalReport) != "probe-report.json") throw new IOException("Expected a completed local long microphone report.");
        using var runLock = new FileStream(Path.Combine(root, ".local", "recording-probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var liveHosts = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (liveHosts.Length != 0) throw new IOException("CaptureHost is still active; offline export refused."); }
        finally { foreach (var process in liveHosts) process.Dispose(); }
        string originalReportHash = Hash(originalReport);
        using var proof = JsonDocument.Parse(File.ReadAllBytes(originalReport));
        var p = proof.RootElement;
        if (p.GetProperty("Schema").GetString() != "m3c-microphone-endurance-1" || !p.GetProperty("IsLongRun").GetBoolean() ||
            p.GetProperty("RequestedSeconds").GetInt32() != LongSeconds || !p.GetProperty("Lifecycle").GetProperty("HostExited").GetBoolean() ||
            p.GetProperty("Lifecycle").GetProperty("ProducerExitCode").GetInt32() != 0 ||
            !p.GetProperty("Lifecycle").GetProperty("OwnedSelection").GetBoolean() || p.GetProperty("EndpointFallback").GetBoolean() ||
            p.GetProperty("MicrophoneId").ValueKind != JsonValueKind.String) throw new InvalidDataException("Long capture is not ready for offline validation.");
        string recording = Path.Combine(originalRun, "recording");
        if (!string.Equals(Path.GetFullPath(p.GetProperty("Storage").GetProperty("RecordingDirectory").GetString()!), recording, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recording directory does not match the original report.");
        string output = Path.Combine(originalRun, "offline-export-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output); Console.WriteLine("Offline long export evidence: " + output);
        var errors = new List<string>(); var observations = new List<string>(); var contentChecks = new List<object>();
        var hashes = new Dictionary<string, string>();
        FullMixResult? mix = null; ExportResult? export = null; CompressedCheck? full = null; var parts = new List<CompressedCheck>();
        object? recoverySummary = null; bool unchanged = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(70));
        try
        {
            foreach (var hash in p.GetProperty("Storage").GetProperty("OriginalHashes").EnumerateObject())
            {
                string file = SyntheticAudioProbe.SafeProjectPath(root, Path.Combine(recording, hash.Name));
                if (!file.StartsWith(recording + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Original hash path escaped recording.");
                string expected = hash.Value.GetString()!;
                if (Hash(file) != expected) throw new InvalidDataException("Original hash no longer matches capture report.");
                hashes.Add(file, expected);
            }
            var recovery = M1Recovery.Read(recording, deadline.Token);
            recoverySummary = new { recovery.IsCleanEnd, recovery.HasCheckpoint, recovery.Issues,
                SourceChunks = recovery.Capture.Tracks.Count, recovery.Capture.Duration100ns, recovery.Capture.Warnings };
            if (!recovery.HasCheckpoint || recovery.Issues.Count != 0) throw new InvalidDataException("Persisted index, hashes or boundaries did not validate.");
            Console.WriteLine("Verified original storage metadata; source continuity warnings retained. Creating full WAV/MP3.");
            mix = M2ExportService.RenderMeeting(recording, new MediaFoundationMp3Encoder(), deadline.Token);
            if (mix.CompressedPath is null) throw new InvalidDataException("Full MP3 was not created: " + string.Join(" | ", mix.Warnings));
            using var render = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(mix.Directory, "render.json")));
            var compressed = render.RootElement.GetProperty("compressed");
            long delay = compressed.GetProperty("encoderDelayFrames").GetInt64(), padding = compressed.GetProperty("trailingPaddingFrames").GetInt64();
            double[] markerTimes = p.GetProperty("RemoteMarkers").EnumerateArray().Where(marker => marker.GetProperty("Found").GetBoolean())
                .Select(marker => marker.GetProperty("StartSeconds").GetDouble()).ToArray();
            full = CheckCompressed(mix.CompressedPath, -delay / (double)Rate, markerTimes, deadline.Token);
            CheckDecodedMarkers(full, observations);
            if (full.DecodedFrames != mix.FrameCount + delay + padding || full.Sha256 != mix.CompressedSha256) errors.Add("full_MP3_EOF_or_hash_invalid");
            var fullContent = CompareDecodedMixWindows(mix.WavePath, mix.CompressedPath, 0, mix.FrameCount, delay,
                markerTimes.Select(time => (long)Math.Round(Math.Max(0, time - 1) * Rate)), deadline.Token);
            contentChecks.Add(new { File = Path.GetFileName(mix.CompressedPath), Windows = fullContent });
            if (fullContent.Any(window => !window.Pass)) errors.Add("full_MP3_content_difference");
            export = M2ExportService.ExportParts(mix, new(M2ExportFormat.Mp3, 20_000_000, 1800, 5, true), Path.Combine(output, "exports"), deadline.Token);
            if (export.Parts[0].StartFrame != 0 || export.Parts[^1].EndFrame != mix.FrameCount) errors.Add("export_timeline_incomplete");
            for (int index = 0; index < export.Parts.Count; index++)
            {
                var part = export.Parts[index];
                if (new FileInfo(part.Path).Length != part.ByteLength || part.ByteLength > 20_000_000 || Hash(part.Path) != part.Sha256) errors.Add("part_bytes_or_hash_invalid");
                if (index > 0 && part.StartFrame != export.Parts[index - 1].EndFrame - 5 * Rate) errors.Add("part_overlap_invalid");
                double[] expected = markerTimes.Where(time => time >= part.StartUs / 1_000_000d + 1 && time + 2 <= part.EndUs / 1_000_000d).ToArray();
                var decoded = CheckCompressed(part.Path, part.StartFrame / (double)Rate - part.EncoderDelayFrames / (double)Rate, expected, deadline.Token);
                parts.Add(decoded); CheckDecodedMarkers(decoded, observations);
                if (decoded.DecodedFrames != part.EndFrame - part.StartFrame + part.EncoderDelayFrames + part.TrailingPaddingFrames || decoded.DecodedFrames / (double)Rate > 1800)
                    errors.Add("part_EOF_or_duration_invalid");
                var content = CompareDecodedMixWindows(mix.WavePath, part.Path, part.StartFrame, part.EndFrame, part.EncoderDelayFrames,
                    markerTimes.Select(time => (long)Math.Round(Math.Max(0, time - 1) * Rate)), deadline.Token);
                contentChecks.Add(new { File = Path.GetFileName(part.Path), Windows = content });
                if (content.Any(window => !window.Pass)) errors.Add("part_content_difference:" + index);
            }
            unchanged = hashes.All(pair => Hash(pair.Key) == pair.Value) && Hash(originalReport) == originalReportHash;
            if (!unchanged) errors.Add("original_or_original_report_changed");
        }
        catch (Exception ex) { errors.Add(ex.ToString()); File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
        var report = new { Schema = "m3c-mic-long-offline-export-1", Status = errors.Count == 0 ? "PASS_WARNING_SOURCE_TIMELINE_EXPORT" : "FAIL",
            FinishedAtUtc = DateTimeOffset.UtcNow, OriginalReport = originalReport, OriginalReportSha256 = originalReportHash,
            OriginalCaptureStatus = p.GetProperty("Status").GetString(), OriginalCaptureQuality = p.GetProperty("QualityStatus").GetString(),
            Recovery = recoverySummary, Mix = mix, FullMp3 = full, Export = export, Parts = parts, CodecContentChecks = contentChecks,
            OriginalHashes = hashes, OriginalsAndCaptureReportUnchanged = unchanged, Observations = observations, Errors = errors,
            CaptureStarted = false, MicrophoneEnabled = false, PlayedAudio = false, Transcribed = false, Uploaded = false,
            Limitation = "Export validation of the entire saved timeline only. Original microphone discontinuities and source completeness warnings remain. This is not complete-capture or two-device synchronization acceptance." };
        string reportPath = Path.Combine(output, "report.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(report.Status + ": " + reportPath); return errors.Count == 0 ? 0 : 1;
    }
}
