using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

internal static class M3AudioImportTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("M3-SYNTH external WAV import preserves original, tail, silence and disk-only reload", () => Wave(output));
        run("M3-WINDOWS MP3 external import decodes to EOF and keeps canonical timeline", () => Compressed(output, new MediaFoundationMp3Encoder()));
        run("M3-WINDOWS M4A external import decodes to EOF and keeps canonical timeline", () => Compressed(output, new MediaFoundationM4aEncoder()));
        run("M3-SYNTH imported original/mix/metadata tamper and unsafe path rejected", () => Tamper(output));
        run("M3-SYNTH failed/cancelled import never publishes completed meeting", () => Failed(output));
        run("M3/T33/T50-SYNTH imported audio export retains import version timeline and rejects altered provenance", () => ExportProvenance(output));
    }

    private static void ExportProvenance(string output)
    {
        string root = Path.Combine(output, "m3-import-export-version"), source = Source(root);
        string sourceHash = Hash(source);
        var imported = AudioImportService.Import(source, Path.Combine(root, "meetings"));
        string renderPath = Path.Combine(imported.Mix.Directory, "render.json"); byte[] original = File.ReadAllBytes(renderPath);
        foreach (bool includeManifest in new[] { true, false })
        {
            var exported = M2ExportService.ExportParts(imported.Mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0, includeManifest), Path.Combine(root, "exports"));
            using var receipt = JsonDocument.Parse(File.ReadAllText(exported.LocalReceiptPath!));
            Verify(receipt.RootElement.GetProperty("export"));
            if (includeManifest)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(exported.ManifestPath!)); Verify(manifest.RootElement);
            }
            else Check(exported.ManifestPath is null, "用户未选择清单却导出清单。");
            Check(exported.Parts.Single().EndFrame == imported.Mix.FrameCount, "导入全文导出损失尾帧。");
        }
        var changes = new Action<JsonObject>[]
        {
            value => value["renderVersion"] = M2ExportService.RenderVersion,
            value => value.Remove("renderVersion"),
            value => value["renderVersion"] = "m2-qpc-mono-2-rf64-truepeak",
            value => value["timeline"] = "complete_elapsed_qpc_packet_mapping",
            value => value["captureClockMetadataAvailable"] = true
        };
        try
        {
            foreach (var change in changes)
            {
                var data = JsonNode.Parse(original)!.AsObject(); change(data); File.WriteAllText(renderPath, data.ToJsonString());
                Throws<InvalidDataException>(() => M2ExportService.ExportParts(imported.Mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0), Path.Combine(root, "refused")));
                Check(!Directory.Exists(Path.Combine(root, "refused")), "篡改导入版本后仍产生导出。");
            }
        }
        finally { File.WriteAllBytes(renderPath, original); }
        Check(Hash(source) == sourceHash && Hash(imported.OriginalPath) == sourceHash && Hash(imported.Mix.WavePath) == imported.Mix.WaveSha256,
            "版本检查改变了导入原件或可听副本。");
        static void Verify(JsonElement manifest) => Check(manifest.GetProperty("renderVersion").GetString() == AudioImportService.SchemaVersion &&
            manifest.GetProperty("timeline").GetString() == "imported_decoded_media_from_zero", "导入音频被标记为mic renderer v3或错误时间轴。");
    }

    private static string Source(string root, int rate = 16000, int channels = 2)
    {
        Directory.CreateDirectory(root); string path = Path.Combine(root, "synthetic.wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        for (int i = 0; i < rate * 3; i++)
        {
            double time = (double)i / rate;
            float sample = time is >= .1 and < .4 or >= 2.6 and < 2.95 ? (float)(.3 * Math.Sin(2 * Math.PI * 440 * time)) : 0;
            for (int c = 0; c < channels; c++) writer.WriteSample(sample * (c == 0 ? 1 : .5f));
        }
        return path;
    }

    private static void Wave(string output)
    {
        string root = Path.Combine(output, "m3-import-wave"), source = Source(root), before = Hash(source);
        var result = AudioImportService.Import(source, Path.Combine(root, "meetings"));
        Check(Hash(source) == before && Hash(result.OriginalPath) == before, "Original changed during import.");
        Check(AudioImportService.IsImportedMeeting(result.MeetingDirectory), "Imported schema not recognized.");
        Check(!Directory.Exists(Path.Combine(result.MeetingDirectory, "journal")), "Imported audio fabricated capture clocks.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.MeetingDirectory, "meeting.json")));
        Check(!manifest.RootElement.GetProperty("captureClockMetadataAvailable").GetBoolean(), "Import claims capture clock evidence.");
        var reopened = AudioImportService.Load(result.MeetingDirectory);
        Check(reopened.Mix.WaveSha256 == result.Mix.WaveSha256 && reopened.Mix.FrameCount == result.Mix.FrameCount, "Disk reload changed identity.");
        Check(result.Mix.WavePath == reopened.Mix.WavePath && result.Mix.Directory == reopened.Mix.Directory
            && result.OriginalPath == reopened.OriginalPath, "Import and disk reload returned different path separators.");
        Check(new Uri(result.Mix.WavePath).LocalPath == result.Mix.WavePath, "Imported playback URI changes the returned path.");
        using var wave = new M2WaveReader(reopened.Mix.WavePath);
        Check(wave.Format.SampleRate == 48000 && wave.Format.Channels == 1 && Math.Abs(wave.FrameCount - 144000) <= 1, "Full timeline changed.");
        Check(Rms(wave.ReadFrames(4800, 8000)) > .1 && Rms(wave.ReadFrames(126000, 10000)) > .1, "Head or tail audio lost.");
        Check(wave.ReadFrames(48000, 48000).All(s => s == 0), "Pause silence removed or changed.");
        string subtitle = Path.Combine(root, "original.srt");
        File.WriteAllText(subtitle, "1\n00:00:02,600 --> 00:00:02,950\n尾部字幕\n");
        var revision = TranscriptService.Import(new(result.MeetingDirectory, Path.GetFileName(result.MeetingDirectory),
            (result.Mix.DurationUs + 999) / 1000, subtitle, "test", IsSynthetic: true));
        Check(revision.Segments.Single().StartMs == 2600, "Subtitle time changed after import.");
        var asr = AsrAudioPreparation.Prepare(reopened.Mix.WavePath, reopened.Mix.WaveSha256, Path.Combine(result.MeetingDirectory, "asr-input"));
        Check(Math.Abs(asr.Frames - 48000) <= 1, "Imported ASR timeline truncated.");
        var export = M2ExportService.ExportParts(reopened.Mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0), Path.Combine(root, "exports"));
        Check(export.Parts.Single().EndFrame == reopened.Mix.FrameCount, "Imported recording export loses tail.");
    }

    private static void Compressed(string output, IM2AudioEncoder encoder)
    {
        string root = Path.Combine(output, "m3-import-" + encoder.Extension[1..]), source = Source(root, 48000, 1);
        string encoded = Path.Combine(root, "speech" + encoder.Extension);
        var encoding = encoder.Encode(source, encoded, default); string before = Hash(encoded);
        var imported = AudioImportService.Import(encoded, Path.Combine(root, "meetings"));
        Check(Hash(encoded) == before && imported.OriginalSha256 == before, "Compressed original changed.");
        Check(imported.Mix.FrameCount == encoding.DecodedFrames, "Import did not preserve decoder EOF timeline.");
        using var wave = new M2WaveReader(AudioImportService.Load(imported.MeetingDirectory).Mix.WavePath);
        Check(Rms(wave.ReadFrames(126000, 10000)) > .1, "Compressed import lost audible tail.");
    }

    private static void Tamper(string output)
    {
        string root = Path.Combine(output, "m3-import-tamper"), source = Source(root);
        foreach (string kind in new[] { "original", "mix", "path", "duration" })
        {
            var result = AudioImportService.Import(source, Path.Combine(root, kind));
            if (kind is "original" or "mix")
            {
                string path = kind == "original" ? result.OriginalPath : result.Mix.WavePath;
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                file.Position = file.Length - 2; file.WriteByte(123);
            }
            else
            {
                string path = Path.Combine(result.MeetingDirectory, "meeting.json");
                var document = JsonNode.Parse(File.ReadAllText(path))!;
                if (kind == "path") document["original"]!["file"] = "../../synthetic.wav";
                else document["duration100ns"] = 1;
                File.WriteAllText(path, document.ToJsonString());
            }
            Throws<InvalidDataException>(() => AudioImportService.Load(result.MeetingDirectory));
        }
    }

    private static void Failed(string output)
    {
        string root = Path.Combine(output, "m3-import-failed"), source = Source(root), destination = Path.Combine(root, "meetings");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Throws<OperationCanceledException>(() => AudioImportService.Import(source, destination, cancelled.Token));
        Check(!Directory.Exists(destination), "Pre-cancelled import published data.");
        string corrupt = Path.Combine(root, "broken.wav"); File.WriteAllText(corrupt, "invalid audio");
        Throws<InvalidDataException>(() => AudioImportService.Import(corrupt, destination));
        Check(!Directory.EnumerateDirectories(destination).Any(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal)),
            "Failed import published a completed meeting.");
        Check(File.ReadAllText(corrupt) == "invalid audio", "Failed import changed input.");
        Throws<InvalidDataException>(() => AudioImportService.Import(source + ":stream", destination));
    }

    private static double Rms(float[] values) => Math.Sqrt(values.Select(x => (double)x * x).Average());
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action operation) where T : Exception
    { try { operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
