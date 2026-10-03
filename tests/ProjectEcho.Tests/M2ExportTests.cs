using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

internal static class M2ExportTests
{
    private const ulong Epoch = 30_000_000_000;

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m2-export"); Directory.CreateDirectory(root);
        run("T22/T24/T26/T53-SYNTH M2 disk-only streaming mono mix preserves multichunk format pause and tail", () => Timeline(root));
        run("T23-SYNTH M2 packet QPC corrects known synthetic clock drift markers", () => Drift(root));
        run("T28/T29/T32/T33-SYNTH M2 WAV parts preserve exact PCM complete coverage overlap and explicit manifest", () => WaveParts(root));
        run("T29/T32-SYNTH M2 actual encoded byte and decoded duration limits retry finitely", () => SizeRetries(root));
        run("T20/T33/T50-SYNTH M2 rejects corrupt originals changed mixes paths and cancelled exports", () => Safety(root));
        run("T14/T24-SYNTH M2 empty callback session keeps elapsed placeholder and rejects zero time", () => Empty(root));
        run("T27-SYNTH M2 uniform peak protection and independent 4x windowed-sinc peak fixture", () => Peak(root));
        run("T28/T29/T32-WINDOWS M2 full mix and M4A parts actually decode within byte and duration limits", () => RealCodecIntegration(root));
        run("T14/T33/T53-SYNTH M2 verified declared-gap recording preserves elapsed mix and incomplete-source flag", () => DeclaredGap(root));
        run("T20/T28-WINDOWS M2 post-rename compressed hash failure preserves usable WAV fallback", () => CompressionHashFailure(root));
        run("T33/T50-SYNTH export rechecks cached mix renderer version and preserves current source timeline", () => ExportVersion(root));
    }

    private static void ExportVersion(string root)
    {
        string session = Small(root, "export-version", 0.05);
        var mix = M2ExportService.RenderMeeting(session, encoder: null);
        var originalHashes = OriginalHashes(session);
        string metadata = Path.Combine(mix.Directory, "render.json"); byte[] original = File.ReadAllBytes(metadata);
        string destinationRoot = Path.Combine(root, "export-version-results");
        var mutations = new (string Name, Action<JsonObject> Apply)[]
        {
            ("legacy-v2", value => value["renderVersion"] = "m2-qpc-mono-2-rf64-truepeak"),
            ("legacy-v3", value => value["renderVersion"] = "m2-qpc-mono-3-mic-gap-preserving"),
            ("legacy-v4", value => value["renderVersion"] = "m2-qpc-mono-4-device-clock-preserving"),
            ("unknown", value => value["renderVersion"] = "unknown-renderer"),
            ("missing", value => value.Remove("renderVersion")),
            ("null", value => value["renderVersion"] = null),
            ("wrong-type", value => value["renderVersion"] = 3),
            ("missing-timeline", value => value.Remove("timeline")),
            ("changed-timeline", value => value["timeline"] = AudioImportService.Timeline),
            ("false-import", value => { value["renderVersion"] = AudioImportService.SchemaVersion; value["timeline"] = AudioImportService.Timeline; })
        };
        try
        {
            foreach (var mutation in mutations)
            {
                var data = JsonNode.Parse(original)!.AsObject(); mutation.Apply(data); File.WriteAllText(metadata, data.ToJsonString());
                string destination = Path.Combine(destinationRoot, mutation.Name);
                // Keep the same already-created FullMixResult: export must independently
                // inspect on-disk provenance instead of trusting its in-memory identity.
                Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0, true), destination));
                Need(!Directory.Exists(destination), "版本拒绝之后仍创建了导出目录。");
            }
        }
        finally { File.WriteAllBytes(metadata, original); }
        var result = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 1_000_000, 30, 0, true), Path.Combine(destinationRoot, "current"));
        using var exported = JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
        using var receipt = JsonDocument.Parse(File.ReadAllText(result.LocalReceiptPath!));
        foreach (var manifest in new[] { exported.RootElement, receipt.RootElement.GetProperty("export") })
            Need(manifest.GetProperty("renderVersion").GetString() == M2ExportService.RenderVersion &&
                manifest.GetProperty("timeline").GetString() == "complete_elapsed_qpc_packet_mapping", "当前录音的renderer版本或时间轴未传递。");
        Need(OriginalHashes(session).OrderBy(pair => pair.Key).SequenceEqual(originalHashes.OrderBy(pair => pair.Key)), "导出版本校验修改了原件。");
    }

    private static void Timeline(string root)
    {
        string session = Path.Combine(root, "long-timeline"); const double duration = 65.013;
        using (var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "仅本地敏感标题不应导出", true))
        {
            WriteSegment(storage, "remote-a", "remote", 8000, 1, 0, 31.011, _ => 0.25f);
            WriteSegment(storage, "remote-b", "remote", 16000, 2, 34, duration - 34, _ => 0.5f);
            WriteSegment(storage, "mic-a", "mic", 44100, 1, 2, 59, _ => 0.125f);
            storage.Finish((long)Math.Round(duration * 10_000_000), true, []);
        }
        var hashes = OriginalHashes(session); var result = M2ExportService.RenderMeeting(session, encoder: null);
        Need(result.FrameCount == 3_120_624 && result.DurationUs == 65_013_000, "完整时间轴帧数改变。");
        using var wav = new WaveFileReader(result.WavePath);
        Need(wav.WaveFormat.Channels == 1 && wav.WaveFormat.SampleRate == 48000, "不是完整 mono mix。");
        Near(Sample(wav, 1), 0.25f * M2TimelineRenderer.SourceGain); Near(Sample(wav, 5), 0.375f * M2TimelineRenderer.SourceGain);
        Near(Sample(wav, 32), 0.125f * M2TimelineRenderer.SourceGain); Near(Sample(wav, 35), 0.625f * M2TimelineRenderer.SourceGain);
        Near(Sample(wav, 64), 0.5f * M2TimelineRenderer.SourceGain); Near(Sample(wav, duration - 0.0001), 0.5f * M2TimelineRenderer.SourceGain);
        Need(OriginalHashes(session).OrderBy(x => x.Key).SequenceEqual(hashes.OrderBy(x => x.Key)), "渲染修改了原始轨或索引。");
    }

    private static void Drift(string root)
    {
        string session = Path.Combine(root, "clock-drift"); const int rate = 48000, packetFrames = 480, seconds = 100;
        double[] markers = [1, 50, 99];
        using (var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "QPC已知漂移合成", true))
        {
            foreach (var source in new[] { (Id: "remote", Skew: 1.0015), (Id: "mic", Skew: 0.9985) })
            {
                storage.AddSegment(source.Id + "-1", source.Id, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
                long frames = (long)Math.Floor(seconds * rate / source.Skew), sent = 0;
                while (sent < frames)
                {
                    int count = (int)Math.Min(packetFrames, frames - sent); var pcm = new byte[count * 4];
                    for (int frame = 0; frame < count; frame++)
                    {
                        double time = (sent + frame) * source.Skew / rate;
                        float value = markers.Any(marker => time >= marker && time < marker + 0.01) ? 0.5f : 0;
                        BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(frame * 4), value);
                    }
                    ulong qpc = Epoch + (ulong)Math.Round(sent * source.Skew * 10_000_000 / rate);
                    storage.WritePacket(source.Id + "-1", pcm, new(sent, count, count, (ulong)sent, qpc, 0, true)); sent += count;
                }
                storage.WriteBoundary(source.Id + "-1", "stop", "stop", Epoch + seconds * 10_000_000UL, (ulong)frames, 0, null);
            }
            storage.Finish(seconds * 10_000_000L, true, []);
        }
        var result = M2ExportService.RenderMeeting(session, encoder: null);
        using var wav = new WaveFileReader(result.WavePath);
        foreach (double marker in markers)
        {
            Near(Sample(wav, marker + 0.005), (float)(M2TimelineRenderer.SourceGain * result.WaveAppliedGain), 0.004f);
            Near(Sample(wav, marker - 0.020), 0, 0.0001f);
            Near(Sample(wav, marker + 0.030), 0, 0.0001f);
        }
        File.WriteAllText(Path.Combine(root, "synthetic-drift-evidence.json"), JsonSerializer.Serialize(new
        { sourceSkew = new[] { 1.0015, 0.9985 }, markersSeconds = markers, nominalUncorrectedEndSeparationMs = 300,
            assertionWindowMs = 20, outputFrames = result.FrameCount, realTwoHourSync = "NOT_RUN" }));
    }

    private static void WaveParts(string root)
    {
        string session = Small(root, "wave-parts", 2.35); var mix = M2ExportService.RenderMeeting(session, encoder: null);
        string longRoot = Path.Combine(root, string.Concat(Enumerable.Repeat("安全导出长中文目录", 20)));
        var result = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 96044, 1, 0.25, true), longRoot);
        Need(result.Parts[0].Path.Length > 260, "没有实际覆盖超过260字符的中文路径。");
        Need(result.Parts.Count == 3 && result.Parts[^1].EndFrame == mix.FrameCount, "尾分片遗漏。");
        Need(result.Parts.All(p => p.ByteLength <= 96044 && p.DecodedDurationSeconds <= 1), "WAV实际大小/时长超限。");
        Need(result.Parts[1].OverlapWithPreviousUs == 250000, "重叠映射错误。");
        byte[] full = WaveBytes(mix.WavePath);
        foreach (var part in result.Parts)
        {
            byte[] audio = WaveBytes(part.Path);
            Need(audio.SequenceEqual(full.AsSpan(checked((int)part.StartFrame * 2), audio.Length).ToArray()), "分片没有保留逐帧相同 PCM。");
        }
        string manifest = File.ReadAllText(result.ManifestPath!);
        Need(!manifest.Contains(session, StringComparison.OrdinalIgnoreCase) && !manifest.Contains("本地敏感") && !manifest.Contains("sources/") && !manifest.Contains("meeting.json"), "最小清单泄露来源资料。");
        Need(Directory.GetFiles(result.Directory).Length == result.Parts.Count + 1, "导出夹包含未经选择的原轨或临时文件。");
        var noManifest = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 10_000_000, 10, 0, false), Path.Combine(root, "explicit-no-manifest"));
        Need(noManifest.ManifestPath is null && Directory.GetFiles(noManifest.Directory).Length == 1, "没有显式选择清单却导出了清单。");
        Need(noManifest.LocalReceiptPath is not null && File.Exists(noManifest.LocalReceiptPath) &&
            noManifest.LocalReceiptPath.StartsWith(mix.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "未选导出清单时丢失本地溯源或时间映射。");
        using (var receipt = JsonDocument.Parse(File.ReadAllText(noManifest.LocalReceiptPath!)))
        {
            var local = receipt.RootElement;
            Need(!local.GetProperty("manifestWasSelected").GetBoolean() && local.GetProperty("sourceHashes").GetArrayLength() > 0 &&
                local.GetProperty("export").GetProperty("parts")[0].GetProperty("endFrame").GetInt64() == mix.FrameCount,
                "本地导出记录缺少来源哈希或完整分片时间映射。");
        }
        // Last remaining span is shorter than overlap; the last accepted part still
        // advances through the real ending and must not start an infinite loop.
        var shortTail = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 96044, 1, 0.9, false), Path.Combine(root, "short-tail"));
        Need(shortTail.Parts.Count < 30 && shortTail.Parts[^1].EndFrame == mix.FrameCount, "小于重叠的尾段漏音/没有结束。");
    }

    private static void SizeRetries(string root)
    {
        string session = Small(root, "fake-codec", 3.2); var mix = M2ExportService.RenderMeeting(session, encoder: null);
        var codec = new BudgetEncoder();
        var result = M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a, 5000, 2, 0.1, true), Path.Combine(root, "actual-budget"), codec);
        Need(codec.Calls > result.Parts.Count && result.Parts.All(p => p.ByteLength <= 5000 && p.DecodedDurationSeconds <= 2), "没有按实际字节重试。");
        Need(result.Parts[0].StartFrame == 0 && result.Parts[^1].EndFrame == mix.FrameCount, "重试导致首尾丢失。");
        var durationCodec = new BudgetEncoder { DelayPadding = 48000, BytesPerSecond = 1000 };
        var limited = M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a, 50000, 2, 0.1), Path.Combine(root, "decoded-duration-budget"), durationCodec);
        Need(durationCodec.Calls > limited.Parts.Count && limited.Parts.All(p => p.DecodedDurationSeconds <= 2), "未按解码时长重试。");
        var impossible = new BudgetEncoder { FixedBytes = 50000, BytesPerSecond = 0 };
        Reject<InvalidOperationException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a, 1000, 2, 0), Path.Combine(root, "impossible-codec"), impossible));
        Need(impossible.Calls <= 8, "超过有限重试次数。");
        Need(!Directory.EnumerateFiles(Path.Combine(root, "impossible-codec"), "*", SearchOption.AllDirectories).Any(), "失败导出留下伪成功片段。");
    }

    private static void Safety(string root)
    {
        string session = Small(root, "safety", 0.3); var mix = M2ExportService.RenderMeeting(session, encoder: null);
        foreach (var options in new[] { new ExportOptions(M2ExportFormat.Wave, 45), new(M2ExportFormat.Wave, 100, 2, 2),
            new(M2ExportFormat.Wave, 100, double.NaN, 0), new(M2ExportFormat.Wave, 46, 1, 0.5) })
            Reject<ArgumentException>(() => M2ExportService.ExportParts(mix, options, Path.Combine(root, "invalid-settings")));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Reject<OperationCanceledException>(() => M2ExportService.RenderMeeting(session, encoder: null, cancelled.Token));
            Reject<OperationCanceledException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave), Path.Combine(root, "pre-cancelled"), cancelled.Token));
            Need(!Directory.Exists(Path.Combine(root, "pre-cancelled")), "预取消仍创建导出。");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelling = new BudgetEncoder { Cancellation = cancellation };
            Reject<OperationCanceledException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a), Path.Combine(root, "mid-cancelled"), cancelling, cancellation.Token));
            Need(!Directory.EnumerateFiles(Path.Combine(root, "mid-cancelled"), "*", SearchOption.AllDirectories).Any(), "取消留下已提交或临时文件。");
        }
        string junction = Path.Combine(root, "unsafe-junction"), actual = Path.Combine(root, "junction-target"); Directory.CreateDirectory(actual);
        var launch = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        launch.ArgumentList.Add("-NoProfile"); launch.ArgumentList.Add("-NonInteractive"); launch.ArgumentList.Add("-Command");
        launch.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Target '{actual.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using (var process = Process.Start(launch)!)
        { Need(process.WaitForExit(10000) && process.ExitCode == 0, "真实junction fixture没有建立。"); }
        Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave), junction));
        Need(!Directory.EnumerateFileSystemEntries(actual).Any(), "重解析目标写入文件。");
        Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix with { WavePath = M1Recovery.Read(session).Capture.Tracks[0].WavePath }, new(M2ExportFormat.Wave), root));
        using (var tamper = new FileStream(mix.WavePath, FileMode.Open, FileAccess.Write)) { tamper.Position = 44; tamper.WriteByte(77); }
        Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave), root));
        string damaged = Small(root, "tampered-source", 0.1); string source = M1Recovery.Read(damaged).Capture.Tracks[0].WavePath;
        using (var tamper = new FileStream(source, FileMode.Open, FileAccess.Write)) { tamper.Position = tamper.Length - 2; tamper.WriteByte(99); }
        Reject<InvalidDataException>(() => M2ExportService.RenderMeeting(damaged, encoder: null));
        Need(!Directory.Exists(Path.Combine(damaged, "derived")), "坏源仍产生派生输出。");
    }

    private static void Empty(string root)
    {
        string session = Path.Combine(root, "no-callback");
        using (var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "无callback", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 1_234_560, 0, 0, null);
            storage.Finish(1_234_560, true, []);
        }
        var mix = M2ExportService.RenderMeeting(session, encoder: null);
        Need(mix.FrameCount == 5926 && WaveBytes(mix.WavePath).All(b => b == 0) && mix.Warnings.Any(w => w.Contains("占位静音")), "无callback被丢时长或被称作采集静音。");
        string zero = Path.Combine(root, "zero-time");
        using (var storage = new M1Storage(zero, Epoch, DateTimeOffset.UtcNow, "零时长", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch, 0, 0, null); storage.Finish(0, true, []);
        }
        Reject<InvalidDataException>(() => M2ExportService.RenderMeeting(zero, encoder: null));
    }

    private static void Peak(string root)
    {
        string session = Path.Combine(root, "peak-fixture");
        using (var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "同相双源峰值合成", true))
        {
            foreach (string source in new[] { "remote", "mic" })
                WriteSegment(storage, source + "-1", source, 48000, 1, 0, 1, time => (float)Math.Sin(2 * Math.PI * 997 * time));
            storage.Finish(10_000_000, true, []);
        }
        var mix = M2ExportService.RenderMeeting(session, encoder: null); byte[] bytes = WaveBytes(mix.WavePath);
        var pcm = Enumerable.Range(0, bytes.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)) / 32768.0).ToArray();
        double samplePeak = pcm.Max(Math.Abs), peak4x = samplePeak;
        // Independent 4x windowed-sinc fixture check; this is not an ITU-conformance
        // certification and does not promote untested real-recording true peaks.
        for (int i = 16; i < pcm.Length - 16; i++)
            for (int phase = 1; phase <= 3; phase++)
            {
                double value = 0, normalization = 0;
                for (int tap = -15; tap <= 16; tap++)
                {
                    double x = tap - phase / 4.0;
                    double sinc = Math.Sin(Math.PI * x) / (Math.PI * x);
                    double window = 0.5 + 0.5 * Math.Cos(Math.PI * x / 16);
                    value += pcm[i + tap] * sinc * window; normalization += sinc * window;
                }
                peak4x = Math.Max(peak4x, Math.Abs(value / normalization));
            }
        Need(samplePeak <= 0.801 && peak4x < Math.Pow(10, -1.0 / 20), "此合成夹具超过-1dB插值峰值目标。");
        File.WriteAllText(Path.Combine(root, "peak-fixture-evidence.json"), JsonSerializer.Serialize(new
        { sourceGain = M2TimelineRenderer.SourceGain, appliedGain = mix.WaveAppliedGain, samplePeak,
            windowedSinc4xPeak = peak4x, estimatedDb = 20 * Math.Log10(peak4x), realProgramTruePeak = "NOT_VERIFIED" }));
    }

    private static void RealCodecIntegration(string root)
    {
        string session = Small(root, "real-m4a-service", 2.35); var original = OriginalHashes(session);
        var mix = M2ExportService.RenderMeeting(session);
        Need(mix.CompressedPath is not null, "默认完整 M4A 未生成：" + string.Join(" | ", mix.Warnings));
        var fullProbe = MediaFoundationM4aEncoder.DecodeProbe(mix.CompressedPath!);
        Need(fullProbe.DecodedFrames >= mix.FrameCount && fullProbe.DecodedFrames <= mix.FrameCount + 3071 && fullProbe.Channels == 1,
            "完整 M4A 未通过解码长度检查。");
        var result = M2ExportService.ExportParts(mix, new(M2ExportFormat.M4a, 20000, 1, 0.1, true), Path.Combine(root, "real-m4a-export"));
        Need(result.Parts.Count >= 3 && result.Parts[^1].EndFrame == mix.FrameCount, "M4A 分片尾部缺失。");
        foreach (var part in result.Parts)
        {
            var probe = MediaFoundationM4aEncoder.DecodeProbe(part.Path);
            Need(probe.ByteLength == part.ByteLength && probe.ByteLength <= 20000 && probe.DurationSeconds <= 1 &&
                probe.DecodedFrames == part.EndFrame - part.StartFrame + part.TrailingPaddingFrames && part.EncoderDelayFrames == 0,
                "M4A 实际解码/字节/时长不符合导出结果。");
        }
        Need(OriginalHashes(session).OrderBy(x => x.Key).SequenceEqual(original.OrderBy(x => x.Key)), "压缩/导出修改原始轨。");
        File.WriteAllText(Path.Combine(root, "actual-m4a-export-evidence.json"), JsonSerializer.Serialize(new
        { fullMixFrames = mix.FrameCount, fullDecodedFrames = fullProbe.DecodedFrames, parts = result.Parts.Count,
            maxActualBytes = result.Parts.Max(p => p.ByteLength), maxActualDecodedSeconds = result.Parts.Max(p => p.DecodedDurationSeconds),
            sourceTimelineEndFrame = result.Parts[^1].EndFrame, media = "synthetic PCM only; no device capture/playback" }));
    }

    private static void DeclaredGap(string root)
    {
        string session = Path.Combine(root, "verified-incomplete-gap");
        using (var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "private-title", true))
        {
            storage.AddSegment("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(8000, 1));
            var pcm = new byte[640];
            for (int frame = 0; frame < 160; frame++) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(frame * 4), 0.5f);
            storage.WritePacket("remote-1", pcm, new(0, 160, 160, 0, Epoch, 0, true));
            storage.WritePacket("remote-1", pcm, new(160, 160, 160, 160, Epoch + 280_000, 1, true));
            storage.WriteBoundary("remote-1", "stop", "stop", Epoch + 480_000, 320, 0, null);
            storage.Finish(480_000, false, ["private-warning-context"]);
        }
        var recovery = M1Recovery.Read(session);
        Need(recovery.HasCheckpoint && !recovery.IsCleanEnd && recovery.Issues.Count == 0, "合成缺口没有形成已验证但明确不完整来源。");
        var original = OriginalHashes(session); var mix = M2ExportService.RenderMeeting(session, encoder: null);
        Need(!mix.SourceStorageComplete && mix.FrameCount == 2304 && mix.Warnings.Any(w => w.Contains("不代表完整捕获")), "来源不完整状态被清洗。");
        using (var wav = new WaveFileReader(mix.WavePath))
        { Near(Sample(wav, 0.01), 0.5f * M2TimelineRenderer.SourceGain); Near(Sample(wav, 0.024), 0); Near(Sample(wav, 0.0479), 0.5f * M2TimelineRenderer.SourceGain); }
        var export = M2ExportService.ExportParts(mix, new(M2ExportFormat.Wave, 10000, 1, 0, true), Path.Combine(root, "declared-gap-export"));
        using var render = JsonDocument.Parse(File.ReadAllText(Path.Combine(mix.Directory, "render.json")));
        string manifest = File.ReadAllText(export.ManifestPath!); using var exported = JsonDocument.Parse(manifest);
        Need(!render.RootElement.GetProperty("sourceStorageComplete").GetBoolean() && !exported.RootElement.GetProperty("sourceStorageComplete").GetBoolean(), "清单漏记不完整来源标记。");
        Need(!manifest.Contains("private-warning-context") && !manifest.Contains("private-title"), "最小导出清单泄露来源警告或标题。");
        Need(OriginalHashes(session).OrderBy(x => x.Key).SequenceEqual(original.OrderBy(x => x.Key)), "缺口混音修改原件。");
        Reject<InvalidDataException>(() => M2ExportService.ExportParts(mix with { SourceStorageComplete = true }, new(M2ExportFormat.Wave), root));
    }

    private static void CompressionHashFailure(string root)
    {
        string session = Small(root, "compressed-hash-sharing-failure", .35);
        var original = OriginalHashes(session);
        FullMixResult result;
        bool renamed, encodedSuccessfully;
        using (var encoder = new ReadLockedAfterEncoding())
        {
            result = M2ExportService.RenderMeeting(session, encoder);
            renamed = encoder.WaitForFinalRename();
            encodedSuccessfully = encoder.EncodingCompleted;
        } // Release the delete-shared handle before checking final filesystem cleanup.
        Need(encodedSuccessfully && renamed, "共享冲突没有覆盖真实AAC完成后 pending→最终文件 的路径。");
        Need(result.CompressedPath is null && result.CompressedSha256 is null,
            "压缩哈希失败仍提交了压缩路径或哈希。");
        Need(result.Warnings.Any(warning => warning.Contains("压缩副本生成失败")), "压缩哈希失败警告丢失。");
        using (var wave = new M2WaveReader(result.WavePath))
            Need(wave.FrameCount == 16800 && wave.FrameCount == result.FrameCount,
                "压缩哈希故障破坏了已完成WAV或完整帧数。");
        Need(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.WavePath))) == result.WaveSha256,
            "WAV回退结果不是可读取的已验证文件。");
        using var render = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "render.json")));
        Need(render.RootElement.GetProperty("compressed").ValueKind == JsonValueKind.Null,
            "WAV回退清单没有明确记录压缩副本为空。");
        Need(!Directory.EnumerateFiles(result.Directory, "*.m4a").Any() &&
            !Directory.EnumerateFiles(result.Directory, "*.partial").Any(), "失败压缩文件或临时文件未清理。");
        Need(OriginalHashes(session).OrderBy(x => x.Key).SequenceEqual(original.OrderBy(x => x.Key)),
            "压缩哈希故障修改了原轨。");
        File.WriteAllText(Path.Combine(root, "compressed-hash-sharing-evidence.json"), JsonSerializer.Serialize(new
        {
            realAacEncodeCompleted = encodedSuccessfully, finalRenameObserved = renamed,
            failureMethod = "FileAccess.ReadWrite + FileShare.Delete: allow rename/delete, deny SHA256 read",
            wavFrames = result.FrameCount, wavHashVerified = true, compressedPathNull = true,
            manifestCompressedNull = true, failedDerivedFilesRemoved = true, originalsUnchanged = true,
            result.Warnings
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ReadLockedAfterEncoding : IM2AudioEncoder, IDisposable
    {
        private readonly MediaFoundationM4aEncoder encoder = new();
        private readonly ManualResetEventSlim finalRename = new(false);
        private FileStream? held;
        private FileSystemWatcher? watcher;
        public string Extension => encoder.Extension;
        public int BitsPerSecond => encoder.BitsPerSecond;
        public bool EncodingCompleted { get; private set; }
        public AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken)
        {
            var result = encoder.Encode(pcmWavePath, destinationPath, cancellationToken);
            string expectedSource = Path.GetFullPath(destinationPath);
            string expectedTarget = Path.Combine(Path.GetDirectoryName(expectedSource)!, "mixed_full.m4a");
            watcher = new FileSystemWatcher(Path.GetDirectoryName(expectedSource)!) { NotifyFilter = NotifyFilters.FileName };
            watcher.Renamed += (_, change) =>
            {
                if (string.Equals(change.OldFullPath, expectedSource, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(change.FullPath, expectedTarget, StringComparison.OrdinalIgnoreCase)) finalRename.Set();
            };
            watcher.EnableRaisingEvents = true;
            held = new FileStream(destinationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            EncodingCompleted = true;
            return result;
        }
        public bool WaitForFinalRename() => finalRename.Wait(TimeSpan.FromSeconds(3));
        public void Dispose()
        {
            watcher?.Dispose();
            held?.Dispose();
            // A queued native watcher callback may still call Set while disposing.
            // Keep this tiny managed event alive until the test object is collected.
        }
    }

    private static string Small(string root, string name, double duration)
    {
        string session = Path.Combine(root, name);
        using var storage = new M1Storage(session, Epoch, DateTimeOffset.UtcNow, "本地敏感标题", true);
        WriteSegment(storage, "remote-1", "remote", 8000, 1, 0, duration, time => (float)(0.4 * Math.Sin(2 * Math.PI * 331 * time)));
        storage.Finish((long)Math.Round(duration * 10_000_000), true, []); return session;
    }

    private static void WriteSegment(M1Storage storage, string id, string source, int rate, int channels, double start, double duration, Func<double, float> sample)
    {
        storage.AddSegment(id, source, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        int frames = (int)Math.Round(duration * rate), sent = 0;
        while (sent < frames)
        {
            int count = Math.Min(1001, frames - sent); byte[] pcm = new byte[count * channels * 4];
            for (int frame = 0; frame < count; frame++)
                for (int channel = 0; channel < channels; channel++)
                    BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan((frame * channels + channel) * 4), sample(start + (double)(sent + frame) / rate));
            ulong qpc = Epoch + (ulong)Math.Round(start * 10_000_000) + (ulong)((long)sent * 10_000_000 / rate);
            storage.WritePacket(id, pcm, new(sent, count, count, (ulong)sent, qpc, 0, true)); sent += count;
        }
        ulong end = Epoch + (ulong)Math.Round(start * 10_000_000) + (ulong)(((long)frames * 10_000_000 + rate - 1) / rate);
        storage.WriteBoundary(id, "stop", "stop", end, (ulong)frames, 0, null);
    }

    private sealed class BudgetEncoder : IM2AudioEncoder
    {
        public string Extension => ".m4a";
        public int BitsPerSecond => 48000;
        public int Calls;
        internal int FixedBytes = 700, BytesPerSecond = 15000, DelayPadding;
        internal CancellationTokenSource? Cancellation;
        public AudioEncodingResult Encode(string pcmWavePath, string destinationPath, CancellationToken cancellationToken)
        {
            Calls++; using var wav = new WaveFileReader(pcmWavePath); long frames = wav.Length / 2;
            int bytes = checked(FixedBytes + (int)Math.Ceiling(frames / 48000.0 * BytesPerSecond));
            File.WriteAllBytes(destinationPath, new byte[bytes]); Cancellation?.Cancel(); cancellationToken.ThrowIfCancellationRequested();
            return new(destinationPath, bytes, frames + DelayPadding, 48000, 1, "synthetic-test-only", (frames + DelayPadding) / 48000.0,
                frames, 0, DelayPadding, "Synthetic codec fixture; not compressed-format acceptance.")
                {
                    TruePeak = new(0, 0, null, frames + DelayPadding, 48000, TruePeakProtection.Algorithm),
                    ContainerTimeline = new(48000, (ulong)(frames + DelayPadding), (ulong)(frames + DelayPadding),
                        48000, (ulong)(frames + DelayPadding), (ulong)(frames + DelayPadding), 1, (ulong)bytes)
                };
        }
    }

    private static float Sample(WaveFileReader wav, double seconds)
    { wav.Position = (long)Math.Round(seconds * 48000) * 2; var b = new byte[2]; Need(wav.Read(b, 0, 2) == 2, "尾帧读取失败。"); return BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f; }
    private static byte[] WaveBytes(string path)
    { using var reader = new WaveFileReader(path); byte[] bytes = new byte[reader.Length]; Need(reader.Read(bytes, 0, bytes.Length) == bytes.Length, "WAV解码不完整。"); return bytes; }
    private static Dictionary<string, string> OriginalHashes(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetRelativePath(directory, file).StartsWith("derived" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
    private static void Near(float actual, float expected, float tolerance = 0.0002f) => Need(Math.Abs(actual - expected) <= tolerance, $"混音样本不符: {actual} vs {expected}");
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("应拒绝但操作完成: " + typeof(T).Name); }
}
