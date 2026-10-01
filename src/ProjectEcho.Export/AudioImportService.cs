using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ProjectEcho.Core;

namespace ProjectEcho.Export;

public sealed record ImportedAudioMeeting(string MeetingDirectory, FullMixResult Mix, string OriginalPath, string OriginalSha256);

/// <summary>Imports an existing local file, never captures devices or invents capture clocks.</summary>
public static class AudioImportService
{
    public const string SchemaVersion = "m3-audio-import-1";
    internal const string Timeline = "imported_decoded_media_from_zero";
    private const int Rate = 48000;
    private const long MaximumFrames = 12L * 3600 * Rate;

    public static ImportedAudioMeeting Import(string sourcePath, string recordingsRoot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = M2FileSafety.FullPath(sourcePath), root = M2FileSafety.FullPath(recordingsRoot);
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3" or ".m4a")) throw new InvalidDataException("录音导入支持 PCM/Float WAV、MP3、M4A。");
        using var original = M2FileSafety.LockedRead(source);
        if (original.Length is <= 0 or > 32L * 1024 * 1024 * 1024) throw new InvalidDataException("导入文件为空或超过 32 GiB。");
        M2FileSafety.RequireDiskSpace(root, original.Length);
        Directory.CreateDirectory(root);
        string id = "import-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N");
        string pending = M2FileSafety.Inside(root, ".pending-" + id), final = M2FileSafety.Inside(root, id);
        Directory.CreateDirectory(pending);
        string originalRelative = "sources/original" + extension, mixRelative = "derived/imported";
        string copied = M2FileSafety.Inside(pending, originalRelative), mixRoot = M2FileSafety.Inside(pending, mixRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(copied)!); Directory.CreateDirectory(mixRoot);
        // An interrupted import retains its owned pending directory for inspection, but is never listed as a meeting.
        string originalHash = M2FileSafety.Hash(original, cancellationToken);
        using (var destination = new FileStream(copied, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new byte[65536]; int count;
            while ((count = original.Read(bytes)) != 0)
            { cancellationToken.ThrowIfCancellationRequested(); destination.Write(bytes, 0, count); }
            destination.Flush(true);
        }
        if (M2FileSafety.Hash(copied, cancellationToken) != originalHash) throw new InvalidDataException("原始录音副本哈希不符。");
        var measurement = Decode(copied, extension, null, 1, cancellationToken);
        M2FileSafety.RequireDiskSpace(root, checked(measurement.Frames * 4 + 4096));
        string raw = M2FileSafety.Inside(mixRoot, "decoded.pending.wav"), wave = M2FileSafety.Inside(mixRoot, "mixed_full.wav");
        double gain = measurement.Peak > .95 ? .95 / measurement.Peak : 1;
        using (var writer = new MonoPcmWriter(raw, measurement.Frames))
        {
            var second = Decode(copied, extension, writer, gain, cancellationToken);
            if (second != measurement) throw new InvalidDataException("导入解码的重复帧数/峰值检查不一致。");
            writer.Finish();
        }
        var protection = TruePeakProtection.ProtectWave(raw, wave, cancellationToken);
        M2FileSafety.DeleteGenerated(mixRoot, raw);
        string waveHash = M2FileSafety.Hash(wave, cancellationToken);
        long duration100ns = checked((long)((Int128)measurement.Frames * 10_000_000 / Rate));
        string[] warnings = [
            "外部录音：已校验复制和完整解码；原录音的采集范围、同意、设备时钟和缺口未知。",
            "回放/字幕时间以本次完整解码文件的 0 秒为起点；全部声道平均合并，未拼接不同来源。",
            extension == ".wav" ? "保留原始 WAV；派生回放为 48 kHz 单声道 PCM16。" :
                "压缩源的编码延迟/尾部填充由 Windows 解码器解释；外部播放器字幕偏差需核对，不宣称采集时钟精度。"
        ];
        M2FileSafety.AtomicJson(M2FileSafety.Inside(mixRoot, "render.json"), new
        {
            schemaVersion = "m2-render-1", renderVersion = SchemaVersion, sourceStorageComplete = true,
            sampleRate = Rate, channels = 1, frameCount = measurement.Frames, duration100ns,
            timeline = Timeline, captureClockMetadataAvailable = false,
            sourceHashes = new[] { new { sha256 = originalHash, sourceId = "imported-file", frames = measurement.Frames } },
            wave = new { file = "mixed_full.wav", sha256 = waveHash, truePeak = protection.After,
                appliedGain = gain * protection.AppliedGain, additionalDelayFrames = 0 }, compressed = (object?)null, warnings
        }, cancellationToken);
        M2FileSafety.AtomicJson(M2FileSafety.Inside(pending, "meeting.json"), new
        {
            schemaVersion = SchemaVersion, title = SessionDefaults.Title(DateTimeOffset.Now), project = SessionDefaults.DefaultProject,
            importedAt = DateTimeOffset.UtcNow, isSynthetic = false, duration100ns,
            captureClockMetadataAvailable = false, recordingTimeKnown = false, timeline = Timeline,
            original = new { file = originalRelative, sha256 = originalHash, byteLength = original.Length },
            mixDirectory = mixRelative
        }, cancellationToken);
        // Validate the same disk-only reader used after restarting before publishing the directory.
        _ = Load(pending, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        M2FileSafety.NoReparse(final); Directory.Move(pending, final);
        return new(final, new FullMixResult(Path.GetFullPath(Path.Combine(final, mixRelative, "mixed_full.wav")), null,
            Path.GetFullPath(Path.Combine(final, mixRelative)), Rate, measurement.Frames, duration100ns / 10, warnings)
            { WaveSha256 = waveHash, SourceStorageComplete = true, WaveTruePeak = protection.After,
                WaveAppliedGain = gain * protection.AppliedGain }, Path.GetFullPath(Path.Combine(final, originalRelative)), originalHash);
    }

    public static bool IsImportedMeeting(string meetingDirectory)
    {
        string manifest = M2FileSafety.Inside(M2FileSafety.FullPath(meetingDirectory), "meeting.json");
        if (!File.Exists(manifest)) return false;
        using var json = ReadJson(manifest);
        return json.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.GetString() == SchemaVersion;
    }

    public static ImportedAudioMeeting Load(string meetingDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string meeting = M2FileSafety.FullPath(meetingDirectory);
        using var manifest = ReadJson(M2FileSafety.Inside(meeting, "meeting.json"));
        var node = manifest.RootElement;
        if (node.GetProperty("schemaVersion").GetString() != SchemaVersion ||
            node.GetProperty("captureClockMetadataAvailable").GetBoolean() ||
            node.GetProperty("timeline").GetString() != Timeline)
            throw new InvalidDataException("这不是受支持的外部录音会议。");
        var original = node.GetProperty("original");
        string originalPath = M2FileSafety.Inside(meeting, original.GetProperty("file").GetString()!);
        string originalHash = original.GetProperty("sha256").GetString()!;
        using var held = M2FileSafety.LockedRead(originalPath);
        if (held.Length != original.GetProperty("byteLength").GetInt64() || M2FileSafety.Hash(held, cancellationToken) != originalHash)
            throw new InvalidDataException("导入原件已改变；拒绝把损坏的会议显示为已校验。");
        string mixRoot = M2FileSafety.Inside(meeting, node.GetProperty("mixDirectory").GetString()!);
        using var render = ReadJson(M2FileSafety.Inside(mixRoot, "render.json"));
        var r = render.RootElement; var wave = r.GetProperty("wave");
        string wavePath = M2FileSafety.Inside(mixRoot, wave.GetProperty("file").GetString()!);
        string waveHash = wave.GetProperty("sha256").GetString()!;
        using var reader = new M2WaveReader(wavePath);
        long duration100ns = checked((long)((Int128)reader.FrameCount * 10_000_000 / Rate));
        if (r.GetProperty("schemaVersion").GetString() != "m2-render-1" || r.GetProperty("renderVersion").GetString() != SchemaVersion ||
            !r.GetProperty("sourceStorageComplete").GetBoolean() || r.GetProperty("sampleRate").GetInt32() != Rate ||
            r.GetProperty("sourceHashes")[0].GetProperty("sha256").GetString() != originalHash ||
            reader.Format != new PcmFormat(Rate, 1, 16, false, 2) || reader.FrameCount is <= 0 or > MaximumFrames ||
            r.GetProperty("frameCount").GetInt64() != reader.FrameCount || r.GetProperty("duration100ns").GetInt64() != duration100ns ||
            node.GetProperty("duration100ns").GetInt64() != duration100ns || M2FileSafety.Hash(wavePath, cancellationToken) != waveHash)
            throw new InvalidDataException("外部录音的回放文件、帧数、时长或哈希校验失败。");
        string[] warnings = r.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()!).ToArray();
        return new(meeting, new FullMixResult(wavePath, null, mixRoot, Rate, reader.FrameCount, duration100ns / 10, warnings)
            { WaveSha256 = waveHash, SourceStorageComplete = true,
                WaveAppliedGain = wave.GetProperty("appliedGain").GetDouble(),
                WaveTruePeak = wave.GetProperty("truePeak").Deserialize<TruePeakMeasurement>() }, originalPath, originalHash);
    }

    private static JsonDocument ReadJson(string path)
    {
        using var file = M2FileSafety.LockedRead(path);
        if (file.Length > 2_000_000) throw new InvalidDataException("导入元数据过大。");
        return JsonDocument.Parse(file);
    }

    private sealed record Measurement(long Frames, double Peak);
    private static Measurement Decode(string path, string extension, MonoPcmWriter? writer, double gain, CancellationToken token)
    {
        using var input = new SourceSamples(path, extension);
        ISampleProvider converted = input;
        if (input.WaveFormat.SampleRate != Rate) converted = new WdlResamplingSampleProvider(input, Rate);
        float[] samples = new float[16384]; long frames = 0; double peak = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int count = converted.Read(samples);
            if (count == 0) break;
            frames = checked(frames + count);
            if (frames > MaximumFrames) throw new InvalidDataException("导入录音目前最多支持 12 小时。");
            for (int i = 0; i < count; i++)
            {
                if (!float.IsFinite(samples[i])) throw new InvalidDataException("导入音频含无效样本。");
                peak = Math.Max(peak, Math.Abs((double)samples[i]));
                if (writer is not null) samples[i] *= (float)gain;
            }
            writer?.Write(samples.AsSpan(0, count), preservePcm16: true);
        }
        if (frames == 0) throw new InvalidDataException("导入音频没有可解码帧。");
        if (input.ExpectedFrames is { } expected && input.ReadFrames != expected)
            throw new InvalidDataException("WAV 未完整解码至末尾。");
        long expectedOutput = checked((long)Math.Round(input.ReadFrames * (double)Rate / input.WaveFormat.SampleRate));
        if (Math.Abs(frames - expectedOutput) > 1) throw new InvalidDataException("导入重采样未保留完整时长。");
        return new(frames, peak);
    }

    private sealed class SourceSamples : ISampleProvider, IDisposable
    {
        private readonly M2WaveReader? wave;
        private readonly MediaFoundationReader? compressed;
        private readonly ISampleProvider? samples;
        private readonly int channels;
        public long ReadFrames { get; private set; }
        public long? ExpectedFrames => wave?.FrameCount;
        public WaveFormat WaveFormat { get; }
        public SourceSamples(string path, string extension)
        {
            try
            {
                int rate;
                if (extension == ".wav") { wave = new(path); channels = wave.Format.Channels; rate = wave.Format.SampleRate; }
                else
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("压缩音频导入需要 Windows Media Foundation。");
                    MediaFoundationApi.Startup();
                    compressed = new(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
                    channels = compressed.WaveFormat.Channels; rate = compressed.WaveFormat.SampleRate;
                    samples = compressed.ToSampleProvider();
                }
                if (channels is < 1 or > 8 || rate is < 8000 or > 384000) throw new InvalidDataException("导入音频的声道或采样率不受支持。");
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
            }
            catch { Dispose(); throw; }
        }
        public int Read(Span<float> buffer)
        {
            int count; float[] input;
            if (wave is not null)
            {
                count = (int)Math.Min(buffer.Length, wave.FrameCount - ReadFrames);
                input = wave.ReadFrames(ReadFrames, count);
            }
            else
            {
                input = new float[checked(buffer.Length * channels)];
                int received = samples!.Read(input);
                if (received % channels != 0) throw new InvalidDataException("解码帧不完整。");
                count = received / channels;
            }
            for (int i = 0; i < count; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++) sum += input[i * channels + c];
                buffer[i] = (float)(sum / channels);
            }
            ReadFrames += count; return count;
        }
        public void Dispose() { wave?.Dispose(); compressed?.Dispose(); }
    }
}
