using System.Text.Json;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.Export;

/// <summary>Reads and validates a saved render without generating, modifying or playing any audio.</summary>
public static class M2SavedMixLoader
{
    private const int Rate = 48000;
    private static readonly PcmFormat Format = new(Rate, 1, 16, false, 2);

    /// <summary>
    /// Null means there is no completed render. A stale, corrupt or legacy render
    /// throws: the caller can offer explicit regeneration, never silently re-render.
    /// </summary>
    public static FullMixResult? LoadLatest(string meetingDirectory, CancellationToken cancellationToken = default)
        => LoadLatest(meetingDirectory, validateUnusedCompressed: true, cancellationToken);

    /// <summary>
    /// Validates the actual listening file and every source/timeline dependency.
    /// A normal WAV does not depend on the separate compressed copy: that copy is
    /// omitted from this result rather than being represented as verified. RF64
    /// still requires the complete compressed hash/decode checks for WPF playback.
    /// </summary>
    public static FullMixResult? LoadLatestForListening(string meetingDirectory, CancellationToken cancellationToken = default)
        => LoadLatest(meetingDirectory, validateUnusedCompressed: false, cancellationToken);

    private static FullMixResult? LoadLatest(string meetingDirectory, bool validateUnusedCompressed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string meeting = M2FileSafety.FullPath(meetingDirectory);
        string derived = M2FileSafety.Inside(meeting, "derived");
        if (!Directory.Exists(derived)) return null;
        var candidates = new List<string>();
        foreach (string candidate in Directory.EnumerateDirectories(derived, "mix-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(candidate);
            if (name.Length != 36 || !Guid.TryParseExact(name[4..], "N", out _)) continue;
            string path = M2FileSafety.Inside(derived, name);
            if (File.Exists(M2FileSafety.Inside(path, "render.json"))) candidates.Add(path);
            if (candidates.Count > 1024) throw new InvalidDataException("完整混音副本过多，请先检查本会议目录。");
        }
        if (candidates.Count == 0) return null;
        string mix = candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).First();
        var held = new List<FileStream>();
        try
        {
            FileStream Hold(string path) { var file = M2FileSafety.LockedRead(path); held.Add(file); return file; }
            using var manifest = ReadJson(Hold(M2FileSafety.Inside(meeting, "meeting.json")));
            var index = Hold(M2FileSafety.Inside(meeting, "journal/capture-index.jsonl"));
            _ = Hold(M2FileSafety.Inside(meeting, "journal/events.jsonl"));
            using var render = ReadJson(Hold(M2FileSafety.Inside(mix, "render.json")));
            var r = render.RootElement;
            if (!r.TryGetProperty("sourceManifestSha256", out _) || !r.TryGetProperty("sourceIndexSha256", out _))
                throw new InvalidDataException("旧版完整混音缺少会议与时间索引指纹；请主动生成一次完整混音后再复用。");
            if (r.GetProperty("renderVersion").GetString() != M2ExportService.RenderVersion)
                throw new InvalidDataException("旧混音使用更早的时间映射规则；请主动重新生成完整混音，原件和旧混音均保留。");
            if (manifest.RootElement.GetProperty("schemaVersion").GetString() != "m1-recording-1" ||
                r.GetProperty("schemaVersion").GetString() != "m2-render-1" ||
                r.GetProperty("timeline").GetString() != "complete_elapsed_qpc_packet_mapping" ||
                ReadHash(r, "sourceManifestSha256") != M2FileSafety.Hash(held[0], cancellationToken) ||
                ReadHash(r, "sourceIndexSha256") != M2FileSafety.Hash(index, cancellationToken))
                throw new InvalidDataException("完整混音与所选会议或原始时间索引不一致；请检查后主动重新生成。");

            var recovery = M1Recovery.Read(meeting, cancellationToken);
            if (!recovery.HasCheckpoint || recovery.Issues.Count != 0 ||
                recovery.Capture.Tracks.Any(track => !track.WavePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("原件未通过独立存储与哈希检查，不能复用完整混音。 " + string.Join(" | ", recovery.Issues));
            long frames = M2TimelineRenderer.FramesForDuration(recovery.Capture.Duration100ns);
            if (r.GetProperty("sampleRate").GetInt32() != Rate || r.GetProperty("channels").GetInt32() != 1 ||
                r.GetProperty("frameCount").GetInt64() != frames ||
                r.GetProperty("duration100ns").GetInt64() != recovery.Capture.Duration100ns ||
                r.GetProperty("sourceStorageComplete").GetBoolean() != recovery.IsCleanEnd)
                throw new InvalidDataException("完整混音的格式、时长或来源完整性状态不一致。");

            var expected = manifest.RootElement.GetProperty("chunks").EnumerateArray().ToDictionary(
                item => M2FileSafety.Inside(meeting, item.GetProperty("Path").GetString()!),
                item => ReadHash(item, "FileSha256"), StringComparer.OrdinalIgnoreCase);
            var fingerprints = new List<SourceFingerprint>();
            foreach (var track in recovery.Capture.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!M2FileSafety.IsInside(meeting, track.WavePath) || !expected.TryGetValue(track.WavePath, out string? hash) ||
                    M2FileSafety.Hash(Hold(track.WavePath), cancellationToken) != hash)
                    throw new InvalidDataException("所选会议原轨路径或哈希改变，拒绝复用。");
                fingerprints.Add(new(track.SourceId, track.FrameCount, hash));
            }
            var saved = r.GetProperty("sourceHashes").EnumerateArray().Select(item => new SourceFingerprint(
                item.GetProperty("sourceId").GetString()!, item.GetProperty("frames").GetInt64(), ReadHash(item, "sha256"))).ToArray();
            static string Key(SourceFingerprint item) => $"{item.SourceId}:{item.Frames}:{item.Sha256}";
            if (!fingerprints.OrderBy(Key, StringComparer.Ordinal).SequenceEqual(saved.OrderBy(Key, StringComparer.Ordinal)))
                throw new InvalidDataException("完整混音的来源清单不属于所选会议。");

            var wave = r.GetProperty("wave");
            if (wave.GetProperty("file").GetString() != "mixed_full.wav" || wave.GetProperty("additionalDelayFrames").GetInt64() != 0)
                throw new InvalidDataException("完整混音 WAV 路径或时间偏移无效。");
            string wavePath = M2FileSafety.Inside(mix, "mixed_full.wav"), waveHash = ReadHash(wave, "sha256");
            if (M2FileSafety.Hash(Hold(wavePath), cancellationToken) != waveHash)
                throw new InvalidDataException("完整混音 WAV 已改变或损坏。");
            bool requiresCompressedPlayback;
            using (var reader = new M2WaveReader(wavePath, Format))
            {
                if (reader.FrameCount != frames) throw new InvalidDataException("完整混音 WAV 的实际帧数不完整。");
                requiresCompressedPlayback = reader.IsRf64;
            }
            var peak = wave.GetProperty("truePeak").Deserialize<TruePeakMeasurement>();
            ValidatePeak(peak, frames);
            double gain = wave.GetProperty("appliedGain").GetDouble();
            if (!double.IsFinite(gain) || gain <= 0 || gain > 1) throw new InvalidDataException("完整混音增益记录无效。");

            string? compressedPath = null, compressedHash = null;
            var compressed = r.GetProperty("compressed");
            if (compressed.ValueKind != JsonValueKind.Null && (validateUnusedCompressed || requiresCompressedPlayback))
            {
                string name = compressed.GetProperty("file").GetString()!;
                if (name is not ("mixed_full.m4a" or "mixed_full.mp3")) throw new InvalidDataException("压缩混音路径无效。");
                compressedPath = M2FileSafety.Inside(mix, name); compressedHash = ReadHash(compressed, "sha256");
                if (M2FileSafety.Hash(Hold(compressedPath), cancellationToken) != compressedHash)
                    throw new InvalidDataException("完整压缩混音已改变或损坏。");
                // Reopening must not turn container metadata into proof of a complete file.
                var decoded = MediaFoundationM4aEncoder.DecodeProbe(compressedPath, cancellationToken);
                long delay = compressed.GetProperty("encoderDelayFrames").GetInt64(), padding = compressed.GetProperty("trailingPaddingFrames").GetInt64();
                if (decoded.SampleRate != Rate || decoded.Channels != 1 || delay < 0 || padding < 0 ||
                    decoded.DecodedFrames != compressed.GetProperty("decodedFrames").GetInt64() ||
                    (Int128)decoded.DecodedFrames != (Int128)frames + delay + padding)
                    throw new InvalidDataException("压缩混音的实际解码时间轴不完整。");
                ValidatePeak(decoded.TruePeak, decoded.DecodedFrames);
            }
            string[] warnings = r.GetProperty("warnings").EnumerateArray().Select(item => item.GetString() ??
                throw new InvalidDataException("混音提示字段无效。")).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return new(wavePath, compressedPath, mix, Rate, frames, recovery.Capture.Duration100ns / 10, warnings)
            {
                WaveSha256 = waveHash, CompressedSha256 = compressedHash,
                SourceStorageComplete = recovery.IsCleanEnd, WaveTruePeak = peak, WaveAppliedGain = gain
            };
        }
        finally { foreach (var stream in held) stream.Dispose(); }
    }

    private static JsonDocument ReadJson(FileStream stream)
    {
        if (stream.Length is <= 0 or > 8_000_000) throw new InvalidDataException("混音或会议清单大小无效。");
        var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        try { RejectDuplicateProperties(json.RootElement); return json; }
        catch { json.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("混音清单包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string ReadHash(JsonElement element, string name)
    {
        string? hash = element.GetProperty(name).GetString();
        if (hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("SHA-256 指纹字段无效。");
        return hash;
    }

    private static void ValidatePeak(TruePeakMeasurement? peak, long frames)
    {
        if (peak is null || peak.FrameCount != frames || peak.SampleRate != Rate ||
            peak.Algorithm != TruePeakProtection.Algorithm || !double.IsFinite(peak.SamplePeak) || peak.SamplePeak < 0 ||
            !double.IsFinite(peak.TruePeakLinear) || peak.TruePeakLinear < peak.SamplePeak ||
            peak.TruePeakLinear > TruePeakProtection.ProcessingCeilingLinear + 1e-7 ||
            (peak.TruePeakLinear == 0 ? peak.DbTp is not null : peak.DbTp is null || !double.IsFinite(peak.DbTp.Value) ||
                Math.Abs(peak.DbTp.Value - 20 * Math.Log10(peak.TruePeakLinear)) > 1e-7))
            throw new InvalidDataException("完整混音的真峰值或帧数记录无效。");
    }

    private sealed record SourceFingerprint(string SourceId, long Frames, string Sha256);
}
