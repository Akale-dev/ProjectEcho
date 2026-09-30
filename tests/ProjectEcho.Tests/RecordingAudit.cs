using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Core;

/// <summary>Offline aggregate-only checks: no device enumeration, capture, playback or network.</summary>
internal static class RecordingAudit
{
    public static int Run(string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        if (root is null || args.Length != 2) { Console.Error.WriteLine("Usage: --audit-recording <project-local directory>"); return 2; }
        var errors = new List<string>();
        var observations = new List<object>();
        var inputKind = "UNVERIFIED_LOCAL_ARTIFACTS";
        try
        {
            var directory = SafePath(root.FullName, args[1]);
            if (File.Exists(SafePath(directory, Path.Combine(directory, "TEST_FIXTURE")))) inputKind = "TEST_FIXTURE_NOT_CAPTURE";
            Inspect(directory, errors, observations);
        }
        catch (Exception ex) { errors.Add("audit_exception:" + ex.GetType().Name); } // Do not leak exception paths/source identities.
        var status = errors.Count == 0 ? "PASS_WITH_LIMITATIONS" : "FAIL";
        var report = new { checkedAtUtc = DateTimeOffset.UtcNow, status, inputKind, errors, observations,
            audiblePlayback = "NOT_RUN", expectedSignalUnverified = true, platformAcceptance = "NOT_ESTABLISHED",
            limitations = "Offline persisted artifacts only. No source attribution, browser isolation, listening or in-memory mic privacy proof." };
        try
        {
            var diagnostics = SafePath(root.FullName, Path.Combine(root.FullName, ".local", "diagnostics"));
            Directory.CreateDirectory(diagnostics);
            var output = Path.Combine(diagnostics, "recording-audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine($"{status}; errors={errors.Count}; audiblePlayback=NOT_RUN; expectedSignalUnverified=true");
            foreach (var error in errors) Console.WriteLine("ERROR " + error);
            Console.WriteLine("Evidence: " + output);
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL evidence_write:" + ex.GetType().Name); return 1; }
        return errors.Count == 0 ? 0 : 1;
    }

    internal static void Inspect(string directory, List<string> errors, List<object> observations)
    {
        void Check(bool valid, string code) { if (!valid) errors.Add(code); }
        var manifestPath = SafePath(directory, Path.Combine(directory, "capture-result.json"));
        if (!File.Exists(manifestPath)) { errors.Add("missing_capture_result"); return; }
        if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024) throw new InvalidDataException();
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var json = manifest.RootElement;
        Check(json.GetProperty("schemaVersion").GetString() == "m0-spike-1", "manifest_schema");
        var result = json.GetProperty("result").Deserialize<CaptureResult>() ?? throw new InvalidDataException();
        var microphoneEnabled = json.GetProperty("microphoneEnabled").GetBoolean();
        var mode = json.GetProperty("sourceMode").GetString();
        Check(mode is "ProcessTree" or "EndpointLoopback", "source_mode");
        Check(result.Complete, "capture_incomplete");
        Check(result.Duration100ns > 0 && result.Duration100ns <= 60L * 10_000_000, "duration_out_of_bounds");
        Check(PathEquals(result.SessionDirectory, directory), "manifest_directory_mismatch");
        Check(!json.GetProperty("selection").GetProperty("reusableSelection").GetBoolean(), "reusable_selection");
        if (mode == "EndpointLoopback") Check(json.GetProperty("selection").GetProperty("endpointScopeConfirmed").GetBoolean(), "endpoint_consent_missing");
        Check(result.Tracks.Count is >= 1 and <= 2, "track_count");
        Check(result.Tracks.Select(t => t.SourceId).Distinct().Count() == result.Tracks.Count, "duplicate_track");
        Check(result.Tracks.All(t => t.SourceId is "remote" or "mic") && result.Tracks.Any(t => t.SourceId == "remote"), "track_identity");
        Check(microphoneEnabled == result.Tracks.Any(t => t.SourceId == "mic"), "mic_state_track_mismatch");
        var sources = SafePath(directory, Path.Combine(directory, "sources"));
        if (!microphoneEnabled)
        {
            Check(json.GetProperty("selection").GetProperty("microphoneDeviceId").ValueKind == JsonValueKind.Null, "mic_off_device");
            Check(!Directory.Exists(sources) || !Directory.EnumerateFileSystemEntries(sources, "mic*", SearchOption.TopDirectoryOnly).Any(), "mic_off_file");
        }
        observations.Add(new { mode, microphoneEnabled, result.Complete, durationSeconds = result.Duration100ns / 10_000_000d,
            captureWarningCount = result.Warnings.Count });
        var indexPath = SafePath(directory, Path.Combine(directory, "journal", "capture-index.jsonl"));
        Check(PathEquals(result.IndexPath, indexPath), "manifest_index_mismatch");
        var records = new List<(long Sequence, JsonElement Data)>();
        if (!File.Exists(indexPath)) errors.Add("missing_capture_index");
        else
        {
            if (new FileInfo(indexPath).Length > 32 * 1024 * 1024) throw new InvalidDataException();
            foreach (var line in File.ReadLines(indexPath, Encoding.UTF8))
            {
                using var document = JsonDocument.Parse(line);
                var record = document.RootElement;
                long sequence = records.Count;
                Check(record.GetProperty("sequence").GetInt64() == sequence, "index_sequence:" + sequence);
                Check(record.GetProperty("schemaVersion").GetInt32() == 1 && !record.GetProperty("isSynthetic").GetBoolean(), "index_schema:" + sequence);
                var data = record.GetProperty("data");
                // GetRawText preserves the exact serialized data token, including escape sequences and spaces.
                var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));
                Check(string.Equals(hash, record.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "index_hash:" + sequence);
                records.Add((sequence, data.Clone()));
            }
        }
        var sessionRecords = records.Where(r => Type(r.Data) == "session").ToArray();
        Check(sessionRecords.Length == 1 && sessionRecords[0].Sequence == 0
            && U64(sessionRecords[0].Data, "epochQpc100ns") == result.EpochQpc100ns, "index_session");
        foreach (var record in records)
        {
            Check(Type(record.Data) is "session" or "segment" or "packet" or "boundary" or "checkpoint", "index_unknown_type:" + record.Sequence);
            if (!microphoneEnabled)
            {
                Check(!record.Data.TryGetProperty("segmentId", out var segment) || segment.GetString() != "mic-1", "mic_off_index:" + record.Sequence);
                if (Type(record.Data) == "checkpoint")
                    Check(record.Data.GetProperty("chunks").EnumerateArray().All(c => c.GetProperty("chunkId").GetString() != "mic-1"), "mic_off_checkpoint:" + record.Sequence);
            }
        }
        var checkpoints = records.Where(r => Type(r.Data) == "checkpoint").ToArray();
        foreach (var checkpoint in checkpoints)
            Check(checkpoint.Data.GetProperty("coveredThroughSequence").GetInt64() == checkpoint.Sequence - 1, "checkpoint_sequence:" + checkpoint.Sequence);
        observations.Add(new { indexRecords = records.Count, checkpoints = checkpoints.Length });
        long expectedMixedFrames = checked((long)(((Int128)result.Duration100ns * 48000 + 9_999_999) / 10_000_000));
        foreach (var track in result.Tracks.Where(t => t.SourceId is "remote" or "mic"))
        {
            var id = track.SourceId;
            var segment = id + "-1";
            track.Format.Validate();
            Check(records.Count(r => Type(r.Data) == "segment" && r.Data.GetProperty("segmentId").GetString() == segment) == 1, id + ":segment_count");
            var indexed = records.Where(r => Type(r.Data) == "packet" && r.Data.GetProperty("segmentId").GetString() == segment).ToArray();
            Check(indexed.Length == track.Packets.Count, id + ":index_packet_count");
            long nextFrame = 0;
            double maxGapUs = 0, maxOverlapUs = 0;
            for (var i = 0; i < track.Packets.Count; i++)
            {
                var packet = track.Packets[i];
                Check(packet.WaveFrameOffset == nextFrame && packet.FrameCount > 0 && packet.RawFrameCount >= packet.FrameCount, id + ":packet_frames:" + i);
                nextFrame = checked(nextFrame + packet.FrameCount);
                Check(packet.TimestampValid && (packet.Flags & 4) == 0 && packet.QpcPosition100ns >= result.EpochQpc100ns, id + ":timestamp:" + i);
                Check(packet.DevicePositionValid, id + ":device_position_unreliable:" + i);
                Check((packet.Flags & ~7) == 0, id + ":unknown_flags:" + i);
                if (i > 0)
                {
                    var prior = track.Packets[i - 1];
                    Check((packet.Flags & 1) == 0, id + ":noninitial_discontinuity:" + i);
                    Check(packet.QpcPosition100ns > prior.QpcPosition100ns, id + ":qpc_not_increasing:" + i);
                    Check((Int128)packet.DevicePosition >= (Int128)prior.DevicePosition + prior.RawFrameCount, id + ":device_overlap:" + i);
                    double gap = (double)((Int128)packet.QpcPosition100ns - prior.QpcPosition100ns) / 10
                        - prior.FrameCount * 1_000_000d / track.Format.SampleRate;
                    maxGapUs = Math.Max(maxGapUs, gap); maxOverlapUs = Math.Max(maxOverlapUs, -gap);
                }
                if (i < indexed.Length)
                {
                    var data = indexed[i].Data;
                    var flags = data.GetProperty("flags").EnumerateArray().Aggregate(0, (value, item) => value | (item.GetString() switch
                        { "data_discontinuity" => 1, "silent" => 2, "timestamp_error" => 4, _ => 8 }));
                    var timestampQuality = data.GetProperty("timestampQuality").GetString();
                    Check(timestampQuality == packet.GetTimestampQuality(), id + ":index_timestamp_quality:" + i);
                    var indexPacket = new CapturedPacket(data.GetProperty("chunkFrameOffset").GetInt64(), data.GetProperty("frameCount").GetInt32(),
                        data.GetProperty("rawFrameCount").GetInt32(), U64(data, "deviceFramePosition"), U64(data, "qpcPosition100ns"),
                        flags, timestampQuality is "device_referenced" or "qpc_referenced",
                        data.TryGetProperty("devicePositionValid", out var deviceQuality) ? deviceQuality.GetBoolean() : timestampQuality != "qpc_referenced");
                    Check(indexPacket == packet, id + ":index_packet_mismatch:" + i);
                    Check(data.GetProperty("path").GetString()?.Replace('\\', '/') == "sources/" + id + ".wav"
                        && data.GetProperty("chunkId").GetString() == segment && data.GetProperty("retainedFrameOffset").GetInt32() == 0, id + ":index_packet_location:" + i);
                    Check(data.GetProperty("sessionStartUs").GetInt64() == (packet.TimestampValid
                        ? Math.Max(0, AudioClock.SessionMicroseconds(packet.QpcPosition100ns, result.EpochQpc100ns)) : 0), id + ":index_time:" + i);
                }
                var numerator = (((Int128)packet.QpcPosition100ns - result.EpochQpc100ns) * track.Format.SampleRate + (Int128)packet.FrameCount * 10_000_000) * 48000;
                var denominator = (Int128)10_000_000 * track.Format.SampleRate;
                expectedMixedFrames = Math.Max(expectedMixedFrames, checked((long)((numerator + denominator / 2) / denominator)));
            }
            Check(nextFrame == track.FrameCount && nextFrame > 0, id + ":frame_total");
            var boundaries = records.Where(r => Type(r.Data) == "boundary" && r.Data.GetProperty("segmentId").GetString() == segment).ToArray();
            Check(boundaries.Length == 1, id + ":boundary_count");
            foreach (var boundary in boundaries)
            {
                Check(boundary.Data.GetProperty("operation").GetString() == "stop"
                    && boundary.Data.GetProperty("acceptedThroughSequence").GetInt64() == (indexed.Length > 0 ? indexed[^1].Sequence : -1), id + ":boundary_sequence");
                Check(boundary.Data.GetProperty("discardedFrameCount").GetInt64() == 0, id + ":boundary_discard");
                Check(boundary.Data.GetProperty("effectiveSessionUs").GetInt64() >= 0
                    && boundary.Data.GetProperty("effectiveSessionUs").GetInt64() <= result.Duration100ns / 10, id + ":boundary_time");
            }
            var wavePath = SafePath(directory, Path.Combine(sources, id + ".wav"));
            Check(PathEquals(track.WavePath, wavePath), id + ":manifest_wave_path");
            var wave = InspectWave(wavePath, track.Format, id, errors);
            if (wave is not null)
            {
                Check(wave.FrameCount == track.FrameCount, id + ":wave_frames");
                foreach (var checkpoint in checkpoints)
                {
                    var chunks = checkpoint.Data.GetProperty("chunks").EnumerateArray().Where(c => c.GetProperty("chunkId").GetString() == segment).ToArray();
                    Check(chunks.Length == 1, id + ":checkpoint_chunk:" + checkpoint.Sequence);
                    var expectedFrames = indexed.Where(p => p.Sequence < checkpoint.Sequence).Sum(p => p.Data.GetProperty("frameCount").GetInt64());
                    foreach (var chunk in chunks)
                        Check(chunk.GetProperty("committedFrameCount").GetInt64() == expectedFrames
                            && chunk.GetProperty("committedPcmBytes").GetInt64() == expectedFrames * track.Format.BlockAlign
                            && chunk.GetProperty("dataOffsetBytes").GetInt64() == wave.DataOffset, id + ":checkpoint_watermark:" + checkpoint.Sequence);
                }
                Check(checkpoints.Length > 0 && indexed.All(p => p.Sequence < checkpoints[^1].Sequence), id + ":final_checkpoint");
            }
            observations.Add(new { sourceId = id, packets = track.Packets.Count, track.FrameCount,
                silentPackets = track.Packets.Count(p => (p.Flags & 2) != 0), discontinuityPackets = track.Packets.Count(p => (p.Flags & 1) != 0),
                timestampErrorPackets = track.Packets.Count(p => (p.Flags & 4) != 0), maxGapUs, maxOverlapUs, wave });
        }
        var previewPath = SelectPreviewPath(directory, result.Complete, errors);
        var mixed = previewPath is null ? null : InspectWave(previewPath, new(48000, 2, 16, false, 4), "mixed", errors);
        if (mixed is not null) Check(mixed.FrameCount == expectedMixedFrames, "mixed_timeline_frames");
        observations.Add(new { mixed, expectedMixedFrames, previewFile = previewPath is null ? null : Path.GetFileName(previewPath) });
    }

    // Selecting a readable preview never clears capture_incomplete or clock-quality errors.
    internal static string? SelectPreviewPath(string directory, bool complete, List<string> errors)
    {
        const string degradedPrefix = "mixed-preview-incomplete-";
        var candidates = new List<string>();
        bool invalidCandidate = false;
        var legacy = SafePath(directory, Path.Combine(directory, "mixed-preview.wav"));
        if (File.Exists(legacy) || Directory.Exists(legacy)) AddCandidate(legacy);
        if (!complete)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory,
                degradedPrefix + "*.wav", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(entry);
                if (name.Length != degradedPrefix.Length + 32 + 4 ||
                    !name.StartsWith(degradedPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(name.Substring(degradedPrefix.Length, 32), "N", out _))
                {
                    errors.Add("mixed:invalid_preview_name");
                    invalidCandidate = true;
                    continue;
                }
                AddCandidate(entry);
            }
        }
        if (invalidCandidate) return null;
        if (candidates.Count == 0) { errors.Add("mixed:missing_wave"); return null; }
        if (candidates.Count != 1) { errors.Add("mixed:ambiguous_preview"); return null; }
        return candidates[0];

        void AddCandidate(string entry)
        {
            try
            {
                var path = SafePath(directory, entry);
                var partial = SafePath(directory, path + ".partial");
                if (!File.Exists(path))
                {
                    errors.Add("mixed:preview_not_file");
                    invalidCandidate = true;
                }
                else if (File.Exists(partial) || Directory.Exists(partial))
                {
                    errors.Add("mixed:preview_not_finalized");
                    invalidCandidate = true;
                }
                else candidates.Add(path);
            }
            catch (InvalidDataException)
            {
                errors.Add("mixed:unsafe_preview_path");
                invalidCandidate = true;
            }
        }
    }

    private static WaveSummary? InspectWave(string path, PcmFormat expected, string label, List<string> errors)
    {
        if (!File.Exists(path)) { errors.Add(label + ":missing_wave"); return null; }
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 100 * 1024 * 1024) throw new InvalidDataException();
            using var binary = new BinaryReader(file, Encoding.UTF8, true);
            if (binary.ReadUInt32() != 0x46464952 || binary.ReadUInt32() + 8L != file.Length || binary.ReadUInt32() != 0x45564157) throw new InvalidDataException();
            long dataOffset = -1, dataBytes = -1;
            while (file.Position + 8 <= file.Length)
            {
                var id = binary.ReadUInt32(); var length = binary.ReadUInt32(); var end = file.Position + length;
                if (end > file.Length) throw new InvalidDataException();
                if (id == 0x61746164) { if (dataOffset >= 0) throw new InvalidDataException(); dataOffset = file.Position; dataBytes = length; }
                file.Position = end + (length & 1);
            }
            using var reader = new WaveFileReader(path);
            var format = reader.WaveFormat;
            var floatGuid = new Guid("00000003-0000-0010-8000-00aa00389b71");
            var pcmGuid = new Guid("00000001-0000-0010-8000-00aa00389b71");
            var extensible = format as WaveFormatExtensible;
            var isFloat = extensible is not null ? extensible.SubFormat == floatGuid : format.Encoding == WaveFormatEncoding.IeeeFloat;
            if (extensible is not null ? extensible.SubFormat != floatGuid && extensible.SubFormat != pcmGuid
                : format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)) throw new InvalidDataException();
            var actual = new PcmFormat(format.SampleRate, format.Channels, format.BitsPerSample, isFloat, format.BlockAlign,
                extensible is not null ? unchecked((uint)extensible.ChannelMask) : 0);
            if (actual != expected || reader.Length != dataBytes || dataBytes < 0 || dataBytes % expected.BlockAlign != 0)
                throw new InvalidDataException();
            var buffer = new byte[expected.BlockAlign * 2048];
            long samples = 0, nonzero = 0; double peak = 0, sumSquares = 0; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (count % expected.BlockAlign != 0) throw new InvalidDataException();
                var width = expected.BitsPerSample / 8;
                for (var i = 0; i < count; i += width)
                {
                    var span = buffer.AsSpan(i, width);
                    double sample = expected.IsFloat ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span))
                        : width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(span) / 32768d
                        : width == 3 ? ((span[0] | span[1] << 8 | span[2] << 16) << 8 >> 8) / 8388608d
                        : BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648d;
                    if (!double.IsFinite(sample)) throw new InvalidDataException();
                    samples++; if (sample != 0) nonzero++; peak = Math.Max(peak, Math.Abs(sample)); sumSquares += sample * sample;
                }
            }
            return new(reader.Length / expected.BlockAlign, reader.Length, dataOffset, peak, samples > 0 ? Math.Sqrt(sumSquares / samples) : 0, nonzero);
        }
        catch (Exception ex) { errors.Add(label + ":wave_exception:" + ex.GetType().Name); return null; }
    }

    private static string? Type(JsonElement data) => data.GetProperty("type").GetString();
    private static ulong U64(JsonElement data, string name) => ulong.Parse(data.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string SafePath(string root, string supplied)
    {
        var full = Path.GetFullPath(supplied);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        for (FileSystemInfo? current = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full); current is not null;)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return full;
    }
    private sealed record WaveSummary(long FrameCount, long PcmBytes, long DataOffset, double Peak, double Rms, long NonzeroSamples);
}
