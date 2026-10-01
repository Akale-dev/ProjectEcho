using System.Text.Json;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.Export;

/// <summary>Offline derived files only. No device capture, playback, networking or original-file writes.</summary>
public static class M2ExportService
{
    internal const string RenderVersion = "m2-qpc-mono-3-mic-gap-preserving";
    private const string CaptureTimeline = "complete_elapsed_qpc_packet_mapping";
    private const int MaximumSizeRetries = 8;
    private const int MaximumParts = 10000;
    private static readonly PcmFormat MixFormat = new(M2TimelineRenderer.SampleRate, 1, 16, false, 2);

    public static FullMixResult RenderMeeting(string sessionDirectory, CancellationToken cancellationToken = default) =>
        RenderMeeting(sessionDirectory, new MediaFoundationM4aEncoder(), cancellationToken);

    /// <summary>Encoder injection supports deterministic fault/size tests. Null requests only the verified WAV.</summary>
    public static FullMixResult RenderMeeting(string sessionDirectory, IM2AudioEncoder? encoder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); string source = M2FileSafety.FullPath(sessionDirectory);
        var locks = new List<FileStream>(); var generated = new List<string>(); string? outputDirectory = null;
        bool waveFinalized = false;
        try
        {
            // Sharing excludes writers/deletion throughout verification and rendering.
            foreach (string name in new[] { "meeting.json", "journal/capture-index.jsonl", "journal/events.jsonl" })
                locks.Add(M2FileSafety.LockedRead(M2FileSafety.Inside(source, name)));
            var recovery = M1Recovery.Read(source, cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            if (!recovery.HasCheckpoint || recovery.Issues.Count != 0 ||
                recovery.Capture.Tracks.Any(track => !track.WavePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("原件未通过存储/哈希验证；请先检查恢复状态，不能将缺失或损坏来源当作完整混音。 " + string.Join(" | ", recovery.Issues));
            var capture = recovery.Capture;
            long frames = M2TimelineRenderer.FramesForDuration(capture.Duration100ns);
            using var manifest = JsonDocument.Parse(locks[0]); locks[0].Position = 0;
            var expected = manifest.RootElement.GetProperty("chunks").EnumerateArray().ToDictionary(
                item => M2FileSafety.Inside(source, item.GetProperty("Path").GetString()!),
                item => item.GetProperty("FileSha256").GetString()!, StringComparer.OrdinalIgnoreCase);
            var sourceHashes = new List<object>();
            foreach (var track in capture.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!M2FileSafety.IsInside(source, track.WavePath) || !expected.TryGetValue(track.WavePath, out string? hash))
                    throw new InvalidDataException("来源文件不在已验证清单内。");
                var held = M2FileSafety.LockedRead(track.WavePath); locks.Add(held);
                if (M2FileSafety.Hash(held, cancellationToken) != hash) throw new InvalidDataException("验证后来源文件发生改变。");
                sourceHashes.Add(new { sha256 = hash, sourceId = track.SourceId, frames = track.FrameCount });
            }
            M2FileSafety.RequireDiskSpace(source, checked(frames * 4));
            string derived = M2FileSafety.Inside(source, "derived"); Directory.CreateDirectory(derived);
            outputDirectory = M2FileSafety.Inside(derived, "mix-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(outputDirectory);
            string wave = M2FileSafety.Inside(outputDirectory, "mixed_full.wav"), partial = wave + ".partial";
            generated.Add(partial); generated.Add(wave);
            M2TimelineRenderer.Render(capture, partial, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using (var check = new M2WaveReader(partial, MixFormat))
                if (check.FrameCount != frames) throw new InvalidDataException("混音 WAV 解码帧数与完整时间轴不同。");
            string protectedWave = M2FileSafety.Inside(outputDirectory, "mixed_full.peak-protected.wav");
            generated.Add(protectedWave);
            var peakProtection = TruePeakProtection.ProtectWave(partial, protectedWave, cancellationToken);
            ValidateTruePeak(peakProtection.After, frames);
            M2FileSafety.DeleteGenerated(outputDirectory, partial);
            M2FileSafety.NoReparse(wave); File.Move(protectedWave, wave, false); waveFinalized = true;
            string waveHash = M2FileSafety.Hash(wave, cancellationToken);
            var warnings = capture.Warnings.Concat(new[]
            {
                "依据每个原始包的 API QPC 重新锚定并在线性重采样时校正相邻包时间；真实两小时双轨同步尚待验收。",
                "混音保留全部经过时间和缺口；固定来源增益及整文件峰值保护只改变派生文件，不改变原始音轨或时间位置。"
            }).ToList();
            if (capture.Tracks.Sum(track => track.FrameCount) == 0) warnings.Add("来源未收到 PCM；输出为已确认经过时间的占位静音，不代表已采到静音。");
            if (!recovery.IsCleanEnd)
                warnings.Add("来源存储标记为不完整/有警告；已验证原件保留，全时段混音按已确认缺口补静音，不代表完整捕获或同步验收通过。");
            string? compressed = null, compressedHash = null; AudioEncodingResult? encoded = null;
            if (encoder is not null)
            {
                string target = M2FileSafety.Inside(outputDirectory, "mixed_full" + ValidExtension(encoder.Extension));
                string encodingPartial = M2FileSafety.Inside(outputDirectory, "mixed_full.pending" + ValidExtension(encoder.Extension)); generated.Add(encodingPartial); generated.Add(target);
                try
                {
                    encoded = encoder.Encode(wave, encodingPartial, cancellationToken);
                    ValidateEncoded(encoded, frames, encodingPartial);
                    cancellationToken.ThrowIfCancellationRequested(); M2FileSafety.NoReparse(target); File.Move(encodingPartial, target, false);
                    string verifiedCompressedHash = M2FileSafety.Hash(target, cancellationToken);
                    compressed = target; compressedHash = verifiedCompressedHash;
                    if (encoded.TimingWarning is { } timing) warnings.Add(timing);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    compressed = null; compressedHash = null; encoded = null;
                    M2FileSafety.DeleteGenerated(outputDirectory, encodingPartial);
                    M2FileSafety.DeleteGenerated(outputDirectory, target);
                    warnings.Add("完整 WAV 已保存；压缩副本生成失败，可重试：" + ex.Message);
                }
            }
            string renderManifest = M2FileSafety.Inside(outputDirectory, "render.json"); generated.Add(renderManifest + ".partial");
            M2FileSafety.AtomicJson(renderManifest, new
            {
                schemaVersion = "m2-render-1", renderVersion = RenderVersion,
                sourceManifestSha256 = M2FileSafety.Hash(locks[0], cancellationToken),
                sourceIndexSha256 = M2FileSafety.Hash(locks[1], cancellationToken),
                sourceStorageComplete = recovery.IsCleanEnd,
                sampleRate = MixFormat.SampleRate, channels = 1, frameCount = frames, duration100ns = capture.Duration100ns,
                sourceGain = M2TimelineRenderer.SourceGain, peakCeiling = M2TimelineRenderer.PeakCeiling,
                timeline = CaptureTimeline, sourceHashes,
                wave = new { file = "mixed_full.wav", sha256 = waveHash, truePeak = peakProtection.After,
                    beforeTruePeakDbTp = peakProtection.Before.DbTp, appliedGain = peakProtection.AppliedGain,
                    processingCeilingDbTp = TruePeakProtection.ProcessingCeilingDbTp, additionalDelayFrames = 0 },
                compressed = compressed is null ? null : new { file = Path.GetFileName(compressed), sha256 = compressedHash,
                    decodedFrames = encoded!.DecodedFrames, encoderDelayFrames = encoded.EncoderDelayFrames, trailingPaddingFrames = encoded.TrailingPaddingFrames,
                    truePeak = encoded.TruePeak, appliedGain = encoded.AppliedGain, encodingAttempts = encoded.EncodingAttempts,
                    containerTimeline = encoded.ContainerTimeline, mp3Timeline = encoded.Mp3Timeline }, warnings
            }, cancellationToken);
            return new(wave, compressed, outputDirectory, MixFormat.SampleRate, frames, capture.Duration100ns / 10, warnings)
                { WaveSha256 = waveHash, CompressedSha256 = compressedHash, SourceStorageComplete = recovery.IsCleanEnd,
                    WaveTruePeak = peakProtection.After, WaveAppliedGain = peakProtection.AppliedGain };
        }
        catch
        {
            if (outputDirectory is not null)
                foreach (string path in generated)
                    if (!(waveFinalized && Path.GetFileName(path) == "mixed_full.wav"))
                        M2FileSafety.DeleteGenerated(outputDirectory, path);
            throw;
        }
        finally { foreach (var file in locks) file.Dispose(); }
    }

    public static ExportResult ExportParts(FullMixResult fullMix, ExportOptions options, string destinationRoot,
        CancellationToken cancellationToken = default) => ExportParts(fullMix, options, destinationRoot,
            options.Format switch
            {
                M2ExportFormat.M4a => new MediaFoundationM4aEncoder(),
                M2ExportFormat.Mp3 => new MediaFoundationMp3Encoder(),
                _ => null
            }, cancellationToken);

    public static ExportResult ExportParts(FullMixResult fullMix, ExportOptions options, string destinationRoot,
        IM2AudioEncoder? encoder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fullMix); ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options); cancellationToken.ThrowIfCancellationRequested();
        string destination = M2FileSafety.FullPath(destinationRoot), mixRoot = M2FileSafety.FullPath(fullMix.Directory);
        string wave = M2FileSafety.Inside(mixRoot, "mixed_full.wav");
        if (!string.Equals(wave, M2FileSafety.FullPath(fullMix.WavePath), StringComparison.OrdinalIgnoreCase) || fullMix.SampleRate != MixFormat.SampleRate)
            throw new InvalidDataException("只能导出明确选定的完整派生混音。");
        if (M2FileSafety.IsInside(mixRoot, destination) || string.Equals(destination, mixRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("导出目录不能写入混音原目录。");
        using var held = M2FileSafety.LockedRead(wave);
        string mixHash = M2FileSafety.Hash(held, cancellationToken);
        if (fullMix.WaveSha256.Length != 64 || fullMix.WaveSha256 != mixHash) throw new InvalidDataException("完整混音 SHA-256 不一致，拒绝导出被改动的文件。");
        using var provenanceLock = M2FileSafety.LockedRead(M2FileSafety.Inside(mixRoot, "render.json"));
        using var provenance = JsonDocument.Parse(provenanceLock);
        var provenanceRoot = provenance.RootElement;
        string? sourceRenderVersion = provenanceRoot.TryGetProperty("renderVersion", out var versionField) && versionField.ValueKind == JsonValueKind.String
            ? versionField.GetString() : null;
        string? sourceTimeline = provenanceRoot.TryGetProperty("timeline", out var timelineField) && timelineField.ValueKind == JsonValueKind.String
            ? timelineField.GetString() : null;
        bool supportedRecording = sourceRenderVersion == RenderVersion && sourceTimeline == CaptureTimeline;
        bool supportedImport = sourceRenderVersion == AudioImportService.SchemaVersion && sourceTimeline == AudioImportService.Timeline &&
            provenanceRoot.TryGetProperty("captureClockMetadataAvailable", out var clocksAvailable) && clocksAvailable.ValueKind == JsonValueKind.False;
        if (!supportedRecording && !supportedImport)
            throw new InvalidDataException("派生混音版本或时间轴不受支持；旧录音混音须主动重新生成，不能将其标为当前版本。");
        if (provenance.RootElement.GetProperty("schemaVersion").GetString() != "m2-render-1" ||
            provenance.RootElement.GetProperty("wave").GetProperty("sha256").GetString() != mixHash ||
            provenance.RootElement.GetProperty("sourceStorageComplete").GetBoolean() != fullMix.SourceStorageComplete ||
            provenance.RootElement.GetProperty("frameCount").GetInt64() != fullMix.FrameCount)
            throw new InvalidDataException("派生混音清单与输出不同。");
        using var reader = new M2WaveReader(wave, MixFormat);
        if (reader.FrameCount != fullMix.FrameCount || reader.FrameCount <= 0) throw new InvalidDataException("混音时间轴为空或改变。");
        bool compressed = options.Format is M2ExportFormat.M4a or M2ExportFormat.Mp3;
        string extension = options.Format switch { M2ExportFormat.M4a => ".m4a", M2ExportFormat.Mp3 => ".mp3", _ => ".wav" };
        if (compressed && encoder is null) throw new NotSupportedException("未提供经过验证的压缩编码器。");
        if (compressed && (ValidExtension(encoder!.Extension) != extension || encoder.BitsPerSecond <= 0))
            throw new NotSupportedException("所选格式与编码器不同或码率无效。");
        long overlap = (long)Math.Round(options.OverlapSeconds * MixFormat.SampleRate);
        // Reserve format-specific delay/padding, then enforce the actual EOF length.
        int overhead = options.Format switch { M2ExportFormat.M4a => 3072, M2ExportFormat.Mp3 => MediaFoundationMp3Encoder.MaximumOverheadFrames, _ => 0 };
        long durationLimit = (long)Math.Floor(options.MaxDurationSeconds * MixFormat.SampleRate) - overhead;
        long estimated = compressed
            ? Math.Max(1, (long)Math.Floor(options.MaxFileBytes * 0.95 * 8 / encoder!.BitsPerSecond * MixFormat.SampleRate))
            : MonoPcmWriter.FramesFittingByteLimit(options.MaxFileBytes);
        long planned = Math.Min(durationLimit, estimated);
        if (planned <= 0 || (fullMix.FrameCount > planned && planned <= overlap))
            throw new ArgumentException("大小预算容不下可推进的分片及重叠；请增大限制或减少重叠。");
        long expectedParts = fullMix.FrameCount <= planned ? 1 :
            1 + (fullMix.FrameCount - planned + planned - overlap - 1) / (planned - overlap);
        if (expectedParts > MaximumParts) throw new ArgumentException("限制会产生超过一万份分片；请增大大小/时长限制或减少重叠。");
        long exportFrames = checked(fullMix.FrameCount + (expectedParts - 1) * overlap);
        M2FileSafety.RequireDiskSpace(destination, checked(exportFrames * 2 + Math.Min(fullMix.FrameCount, planned) * 2 + expectedParts * 4096));
        Directory.CreateDirectory(destination);
        string output = M2FileSafety.Inside(destination, "export-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output); var generated = new List<string>(); var parts = new List<ExportPartResult>();
        string? localReceipt = null;
        try
        {
            long start = 0, previousEnd = 0;
            while (start < fullMix.FrameCount)
            {
                if (parts.Count >= MaximumParts) throw new InvalidOperationException("实际编码重试后超过一万份分片上限。");
                cancellationToken.ThrowIfCancellationRequested(); long count = Math.Min(planned, fullMix.FrameCount - start);
                bool accepted = false;
                for (int attempt = 0; attempt < MaximumSizeRetries; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (start + count < fullMix.FrameCount && count <= overlap)
                        throw new InvalidOperationException("实际编码大小无法满足重叠与连续推进要求。");
                    string fileName = $"part-{parts.Count + 1:D4}" + extension;
                    string target = M2FileSafety.Inside(output, fileName), temporary = compressed
                        ? M2FileSafety.Inside(output, $"part-{parts.Count + 1:D4}.pending" + extension) : target + ".partial";
                    string pcm = compressed ? M2FileSafety.Inside(output, $"work-{parts.Count + 1:D4}.wav") : temporary;
                    generated.Add(pcm); generated.Add(temporary); generated.Add(target);
                    WriteWaveRange(reader, pcm, start, count, cancellationToken);
                    AudioEncodingResult? encoded = null;
                    TruePeakProtectionResult? waveProtection = null;
                    if (compressed)
                    {
                        try { encoded = encoder!.Encode(pcm, temporary, cancellationToken); ValidateEncoded(encoded, count, temporary); }
                        finally { M2FileSafety.DeleteGenerated(output, pcm); }
                    }
                    else
                    {
                        // Cutting a waveform can introduce a new endpoint overshoot even
                        // when the complete mix was already protected. Measure every part.
                        string guarded = M2FileSafety.Inside(output, $"part-{parts.Count + 1:D4}.peak-protected.wav");
                        generated.Add(guarded);
                        waveProtection = TruePeakProtection.ProtectWave(temporary, guarded, cancellationToken);
                        ValidateTruePeak(waveProtection.After, count);
                        M2FileSafety.DeleteGenerated(output, temporary);
                        File.Move(guarded, temporary, false);
                    }
                    long bytes = new FileInfo(temporary).Length;
                    double decodedDuration = encoded?.DurationSeconds ?? (double)count / MixFormat.SampleRate;
                    if (bytes <= options.MaxFileBytes && decodedDuration <= options.MaxDurationSeconds)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); M2FileSafety.NoReparse(target); File.Move(temporary, target, false);
                        parts.Add(new(target, bytes, ToUs(start), ToUs(start + count), ToUs(Math.Max(0, previousEnd - start)),
                            M2FileSafety.Hash(target, cancellationToken), encoded?.DurationSeconds ?? (double)count / MixFormat.SampleRate)
                        {
                            StartFrame = start, EndFrame = start + count, EncoderDelayFrames = encoded?.EncoderDelayFrames ?? 0,
                            TrailingPaddingFrames = encoded?.TrailingPaddingFrames ?? 0, TimingWarning = encoded?.TimingWarning,
                            TruePeak = encoded?.TruePeak ?? waveProtection!.After,
                            AppliedGain = encoded?.AppliedGain ?? waveProtection!.AppliedGain,
                            EncodingAttempts = encoded?.EncodingAttempts ?? 1, ContainerTimeline = encoded?.ContainerTimeline,
                            Mp3Timeline = encoded?.Mp3Timeline
                        });
                        previousEnd = start + count; accepted = true; break;
                    }
                    M2FileSafety.DeleteGenerated(output, temporary);
                    double ratio = Math.Min((double)options.MaxFileBytes / bytes, options.MaxDurationSeconds / decodedDuration);
                    long shorter = Math.Min(count - 1, (long)Math.Floor(count * ratio * 0.90));
                    if (shorter < 1) throw new InvalidOperationException("实际文件头/编码开销超过大小限制。");
                    count = shorter;
                }
                if (!accepted) throw new InvalidOperationException("实际编码大小在有限重试后仍超限；没有标记为成功。");
                if (previousEnd == fullMix.FrameCount) break;
                long next = previousEnd - overlap;
                if (next <= start) throw new InvalidDataException("分片不能向前推进。");
                start = next;
            }
            ValidateCoverage(parts, fullMix.FrameCount);
            string? manifest = null;
            // No title, time-of-recording, source paths, URLs, profiles, source PCM or diagnostics.
            var exportManifest = new
                {
                    schemaVersion = "m2-export-1", renderVersion = sourceRenderVersion, format = extension[1..],
                    sourceStorageComplete = fullMix.SourceStorageComplete,
                    sampleRate = MixFormat.SampleRate, channels = 1, sourceMixSha256 = mixHash, completeTimelineFrames = fullMix.FrameCount,
                    timeline = sourceTimeline, maxFileBytes = options.MaxFileBytes,
                    maxDurationSeconds = options.MaxDurationSeconds, overlapSeconds = options.OverlapSeconds,
                    parts = parts.Select(part => new { file = Path.GetFileName(part.Path), byteLength = part.ByteLength,
                        sha256 = part.Sha256, decodedDurationSeconds = part.DecodedDurationSeconds,
                        startFrame = part.StartFrame, endFrame = part.EndFrame, startUs = part.StartUs, endUs = part.EndUs,
                        overlapWithPreviousUs = part.OverlapWithPreviousUs, encoderDelayFrames = part.EncoderDelayFrames,
                        trailingPaddingFrames = part.TrailingPaddingFrames, timingWarning = part.TimingWarning,
                        truePeak = part.TruePeak, appliedGain = part.AppliedGain, encodingAttempts = part.EncodingAttempts,
                        containerTimeline = part.ContainerTimeline, mp3Timeline = part.Mp3Timeline,
                        spans = new[] { new { fileStartFrame = part.EncoderDelayFrames, sourceStartFrame = part.StartFrame, frameCount = part.EndFrame - part.StartFrame } } })
                };
            if (options.IncludeManifest)
            {
                manifest = M2FileSafety.Inside(output, "export-manifest.json"); generated.Add(manifest); generated.Add(manifest + ".partial");
                M2FileSafety.AtomicJson(manifest, exportManifest, cancellationToken);
            }
            // The user can omit the portable manifest without destroying local
            // provenance/time mapping. This receipt stays beside the derived mix,
            // never in the selected upload folder and never beside original PCM.
            string receipts = M2FileSafety.Inside(mixRoot, "export-records");
            Directory.CreateDirectory(receipts);
            localReceipt = M2FileSafety.Inside(receipts, Path.GetFileName(output) + ".json");
            M2FileSafety.AtomicJson(localReceipt, new
            {
                schemaVersion = "m2-local-export-1", outputDirectory = output,
                sourceRenderSha256 = M2FileSafety.Hash(provenanceLock, cancellationToken),
                sourceHashes = provenance.RootElement.GetProperty("sourceHashes").Clone(),
                manifestWasSelected = options.IncludeManifest, export = exportManifest
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            long total = checked(parts.Sum(part => part.ByteLength) + (manifest is null ? 0 : new FileInfo(manifest).Length));
            return new(output, parts, manifest, total) { LocalReceiptPath = localReceipt };
        }
        catch
        {
            foreach (string file in generated.Distinct(StringComparer.OrdinalIgnoreCase)) M2FileSafety.DeleteGenerated(output, file);
            if (localReceipt is not null)
            {
                M2FileSafety.DeleteGenerated(mixRoot, localReceipt);
                M2FileSafety.DeleteGenerated(mixRoot, localReceipt + ".partial");
            }
            throw;
        }
    }

    internal static void WriteWaveRange(M2WaveReader reader, string target, long start, long count, CancellationToken token)
    {
        using var writer = new MonoPcmWriter(target, count);
        for (long local = 0; local < count; local += 8192)
        { token.ThrowIfCancellationRequested(); int frames = (int)Math.Min(8192, count - local); writer.Write(reader.ReadFrames(start + local, frames), preservePcm16: true); }
        writer.Finish();
    }

    private static long ToUs(long frame) => checked((long)((Int128)frame * 1_000_000 / MixFormat.SampleRate));
    private static string ValidExtension(string extension) => extension is ".m4a" or ".mp3" ? extension : throw new NotSupportedException("当前只交付经验证的 M4A/MP3 压缩格式。");

    private static void ValidateOptions(ExportOptions options)
    {
        if (!Enum.IsDefined(options.Format) || options.MaxFileBytes < 46 || options.MaxFileBytes > 64L * 1024 * 1024 * 1024 ||
            !double.IsFinite(options.MaxDurationSeconds) || !double.IsFinite(options.OverlapSeconds) ||
            options.MaxDurationSeconds <= 0 || options.MaxDurationSeconds > 86400 || options.OverlapSeconds < 0 ||
            options.OverlapSeconds >= options.MaxDurationSeconds)
            throw new ArgumentException("导出格式、大小、时长或重叠设置无效；最大时长必须大于重叠。");
    }

    private static void ValidateEncoded(AudioEncodingResult encoded, long sourceFrames, string expectedPath)
    {
        if (!string.Equals(Path.GetFullPath(encoded.Path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase) ||
            encoded.SampleRate != MixFormat.SampleRate || encoded.Channels != 1 || encoded.SourceFrames != sourceFrames ||
            encoded.DecodedFrames < sourceFrames || encoded.EncoderDelayFrames < 0 || encoded.TrailingPaddingFrames < 0 ||
            encoded.DecodedFrames != sourceFrames + encoded.EncoderDelayFrames + encoded.TrailingPaddingFrames ||
            encoded.ByteLength != new FileInfo(expectedPath).Length || encoded.ByteLength <= 0 ||
            !double.IsFinite(encoded.DurationSeconds) || Math.Abs(encoded.DurationSeconds - (double)encoded.DecodedFrames / encoded.SampleRate) > 1.0 / encoded.SampleRate)
            throw new InvalidDataException("压缩输出的实际解码帧数/声道/字节数未通过完整性检查。");
        ValidateTruePeak(encoded.TruePeak, encoded.DecodedFrames);
        if (Path.GetExtension(expectedPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (encoded.Mp3Timeline is null || encoded.Mp3Timeline.CalibratedDelayFrames != encoded.EncoderDelayFrames ||
                encoded.Mp3Timeline.CalibrationCorrelation is not { } correlation || !double.IsFinite(correlation) || correlation < .8)
                throw new InvalidDataException("MP3 输出缺少独立音频帧或本机编解码延迟校验。");
            Mp3FrameProbe.ValidateAgainstDecoded(encoded.Mp3Timeline, encoded.DecodedFrames, encoded.SampleRate);
        }
        else
        {
            if (encoded.ContainerTimeline is null) throw new InvalidDataException("压缩输出缺少独立容器时间表校验。");
            M4aTimelineProbe.ValidateAgainstDecoded(encoded.ContainerTimeline, encoded.DecodedFrames, encoded.SampleRate);
        }
        if (!double.IsFinite(encoded.AppliedGain) || encoded.AppliedGain <= 0 || encoded.AppliedGain > 1 ||
            encoded.EncodingAttempts is < 1 or > 4)
            throw new InvalidDataException("压缩输出峰值保护参数无效。");
    }

    private static void ValidateTruePeak(TruePeakMeasurement? peak, long expectedFrames)
    {
        if (peak is null || peak.FrameCount != expectedFrames || peak.SampleRate != MixFormat.SampleRate ||
            peak.Algorithm != TruePeakProtection.Algorithm || !double.IsFinite(peak.SamplePeak) || peak.SamplePeak < 0 ||
            !double.IsFinite(peak.TruePeakLinear) || peak.TruePeakLinear < peak.SamplePeak ||
            peak.TruePeakLinear > TruePeakProtection.ProcessingCeilingLinear + 1e-7 ||
            (peak.TruePeakLinear == 0 ? peak.DbTp is not null : peak.DbTp is null || !double.IsFinite(peak.DbTp.Value) ||
                Math.Abs(peak.DbTp.Value - 20 * Math.Log10(peak.TruePeakLinear)) > 1e-7))
            throw new InvalidDataException("输出真峰值/帧数未通过独立复核。");
    }

    private static void ValidateCoverage(IReadOnlyList<ExportPartResult> parts, long frames)
    {
        if (parts.Count == 0 || parts[0].StartFrame != 0 || parts[^1].EndFrame != frames)
            throw new InvalidDataException("分片没有完整覆盖首尾。");
        long end = 0;
        foreach (var part in parts)
        {
            if (part.StartFrame > end || part.EndFrame <= end || part.EndFrame > frames)
                throw new InvalidDataException("分片时间轴有缺口或没有前进。");
            end = part.EndFrame;
        }
    }
}
