using System.Buffers.Binary;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

/// <summary>Pure format gate checks. No endpoint enumeration, configuration or audio activation.</summary>
internal static class MicrophoneFormatPolicyTests
{
    internal static void Run()
    {
        var mix48 = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var native44 = new WaveFormatExtensible(44100, 16, 2);
        Reject<NotSupportedException>(() => MicrophoneFormatPolicy.Validate(mix48, native44), "Cross-rate microphone accepted.");
        var choice44 = MicrophoneFormatPolicy.Select(mix48, native44);
        Require(ReferenceEquals(choice44.Format, native44) &&
            choice44.ConversionFlags == (AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality),
            "Cross-rate shared capture did not request the endpoint's original format with WASAPI conversion.");
        Require(ReferenceEquals(MicrophoneFormatPolicy.Select(mix48, new WaveFormat(48000, 16, 2)).Format, mix48) &&
            MicrophoneFormatPolicy.Select(mix48, new WaveFormat(48000, 16, 2)).ConversionFlags == 0,
            "Validated same-rate capture changed its stream format or flags.");
        foreach (var deviceFormat in new WaveFormat[] { new WaveFormat(8000, 16, 1), new WaveFormat(16000, 16, 1),
            new WaveFormat(22050, 16, 1), new WaveFormatExtensible(44100, 32, 2, false, 24, 3),
            new WaveFormat(96000, 24, 2), WaveFormat.CreateIeeeFloatWaveFormat(192000, 2) })
        {
            var selected = MicrophoneFormatPolicy.Select(mix48, deviceFormat);
            Require(ReferenceEquals(selected.Format, deviceFormat) && selected.Format.SampleRate == deviceFormat.SampleRate,
                "Selection imposed 48k, discarded valid bits or changed the endpoint channel layout.");
            MicrophoneFormatPolicy.Validate(selected.Format, deviceFormat);
        }
        MicrophoneFormatPolicy.Validate(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), native44);
        MicrophoneFormatPolicy.Validate(mix48, new WaveFormat(48000, 16, 2));
        MicrophoneFormatPolicy.Validate(WaveFormat.CreateIeeeFloatWaveFormat(16000, 1), new WaveFormat(16000, 16, 1));
        Require(mix48.SampleRate == 48000 && native44.SampleRate == 44100, "Format gate modified inputs.");

        byte[] nativeBlob = FormatBlob(native44);
        var parsedNative = MicrophoneFormatPolicy.ParseDeviceFormat(nativeBlob);
        Require(parsedNative.SampleRate == 44100 && parsedNative.BitsPerSample == 16 && parsedNative.Channels == 2 &&
            parsedNative is WaveFormatExtensible, "Endpoint-reported extensible format was not preserved.");
        MicrophoneFormatPolicy.Validate(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), parsedNative);
        var parsedPcm = MicrophoneFormatPolicy.ParseDeviceFormat(FormatBlob(new WaveFormat(48000, 16, 2)));
        MicrophoneFormatPolicy.Validate(mix48, parsedPcm);
        var parsedFloat = MicrophoneFormatPolicy.ParseDeviceFormat(FormatBlob(mix48));
        Require(parsedFloat.Encoding == WaveFormatEncoding.IeeeFloat && parsedFloat.SampleRate == 48000, "Float device format changed.");

        Reject<InvalidDataException>(() => MicrophoneFormatPolicy.ParseDeviceFormat([]), "Empty device format accepted.");
        Reject<InvalidDataException>(() => MicrophoneFormatPolicy.ParseDeviceFormat(nativeBlob[..^1]), "Truncated extensible format accepted.");
        var invalidLength = nativeBlob.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(invalidLength.AsSpan(16, 2), ushort.MaxValue);
        Reject<InvalidDataException>(() => MicrophoneFormatPolicy.ParseDeviceFormat(invalidLength), "Out-of-bounds format cbSize accepted.");
        var invalidRate = nativeBlob.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(invalidRate.AsSpan(8, 4), 1);
        Reject<InvalidDataException>(() => MicrophoneFormatPolicy.ParseDeviceFormat(invalidRate), "Inconsistent format byte rate accepted.");
        var invalidSubformat = nativeBlob.ToArray(); invalidSubformat[24] = 99;
        Reject<NotSupportedException>(() => MicrophoneFormatPolicy.ParseDeviceFormat(invalidSubformat), "Unsupported extensible subformat accepted.");
        Reject<NotSupportedException>(() => MicrophoneFormatPolicy.Validate(WaveFormat.CreateIeeeFloatWaveFormat(48000, 4), new WaveFormat(48000, 16, 2)),
            "Unsupported microphone channel count accepted.");
        Reject<NotSupportedException>(() => MicrophoneFormatPolicy.Select(mix48, new WaveFormat(44100, 16, 4)),
            "Selection silently downmixed unsupported channels.");
    }

    internal static void RegisterTimelineTests(Action<string, Action> run, string output)
    {
        foreach (int rate in new[] { 16000, 22050, 44100, 48000, 96000 })
            run($"M3b/T15/T22/T25/T52-SYNTH mic {rate} Hz preserves raw clocks, off boundary, pause gap and complete mix/ASR", () =>
                VerifyTimeline(output, rate));
    }

    private static void VerifyTimeline(string output, int rate)
    {
        const ulong epoch = 10_000_000_000;
        const long duration = 35_000_000;
        string root = Path.Combine(output, "microphone-formats", rate.ToString());
        var microphone = MicrophoneFormatPolicy.Select(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormat(rate, 16, 1));
        // At 48 kHz the existing shared Float32 stereo stream remains the original.
        var format = microphone.Format;
        var expected = new Dictionary<string, List<CapturedPacket>>();
        using (var storage = new M1Storage(root, epoch, DateTimeOffset.UtcNow, "合成多速率麦克风边界", true))
        {
            Add("remote-1", "remote", WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), 0, 35_000_000, .1f, "stop");
            Add("mic-1", "mic", format, 2_500_000, 10_013_337, .2f, "mic_off");
            // Fixed endpoint reopened after mic-off; a format change is negotiated
            // again for the new segment, without converting already saved originals.
            var reopened = MicrophoneFormatPolicy.Select(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormat(44100, 16, 1));
            Add("mic-2", "mic", reopened.Format, 22_500_000, 2_013_337, .2f, "pause");
            storage.Finish(duration, true, []);

            void Add(string id, string source, WaveFormat wave, ulong start, ulong length, float level, string operation)
            {
                storage.AddSegment(id, source, wave);
                expected[id] = [];
                int total = checked((int)((UInt128)length * (uint)wave.SampleRate / AudioClock.UnitsPerSecond));
                var health = new CaptureClockHealth();
                int offset = 0;
                var random = new Random(wave.SampleRate + (int)start);
                while (offset < total)
                {
                    int kept = Math.Min(random.Next(100, 701), total - offset);
                    bool last = offset + kept == total;
                    int raw = kept + (last ? 9 : 0);
                    ulong qpc = epoch + start + (ulong)((UInt128)(uint)offset * AudioClock.UnitsPerSecond / (uint)wave.SampleRate);
                    var observed = health.Observe(offset, (long)qpc, raw, false, epoch);
                    Require(observed.QpcValid && observed.DevicePositionValid, "Native-rate raw device/QPC clocks became invalid.");
                    int before = AudioClock.CompleteFramesBefore(qpc, epoch + start + length, wave.SampleRate, raw);
                    Require(before == kept, "Cutoff retained a fractional/post-boundary frame or lost a complete frame.");
                    var packet = new CapturedPacket(offset, kept, raw, (ulong)offset, qpc, 0, true, true);
                    storage.WritePacket(id, Samples(wave, kept, level), packet);
                    expected[id].Add(packet); offset += kept;
                }
                storage.WriteBoundary(id, id + "-close", operation, epoch + start + length, (ulong)total, 9, "after_boundary");
            }
        }
        var recovered = M1Recovery.Read(root);
        Require(recovered.IsCleanEnd && recovered.Capture.Complete, "Saved multi-rate microphone failed independent recovery: " + string.Join(" | ", recovered.Issues));
        var originals = recovered.Capture.Tracks.ToDictionary(track => track.WavePath, track => Hash(track.WavePath));
        foreach (var track in recovered.Capture.Tracks)
        {
            string id = track.SourceId == "remote" ? "remote-1" : track.Packets[0].QpcPosition100ns == epoch + 2_500_000 ? "mic-1" : "mic-2";
            Require(track.Packets.SequenceEqual(expected[id]), "Disk recovery rewrote raw packet frames, device count, QPC or retained range.");
            using var reader = new M2WaveReader(track.WavePath);
            Require(reader.FrameCount == expected[id].Sum(p => p.FrameCount), "Original includes forbidden mic suffix PCM.");
            Require(reader.Format.SampleRate == (id == "mic-1" ? rate : id == "mic-2" ? 44100 : 48000), "Original sample rate was rewritten.");
        }
        string mixed = Path.Combine(root, "mixed.wav");
        M2TimelineRenderer.Render(recovered.Capture, mixed, CancellationToken.None);
        using (var mix = new M2WaveReader(mixed))
        {
            Require(mix.FrameCount == 168000, "Different mic rate/ending truncated the complete 3.5-second meeting.");
            float At(double seconds) => mix.ReadFrames((long)(seconds * 48000), 1)[0];
            Require(Math.Abs(At(.1) - .1 * M2TimelineRenderer.SourceGain) < .0001 && Math.Abs(At(.5) - .3 * M2TimelineRenderer.SourceGain) < .0001 &&
                Math.Abs(At(1.5) - .1 * M2TimelineRenderer.SourceGain) < .0001 && Math.Abs(At(2.3) - .3 * M2TimelineRenderer.SourceGain) < .0001 && Math.Abs(At(3.49) - .1 * M2TimelineRenderer.SourceGain) < .0001,
                "Multi-rate mixing moved the mic boundary, filled the pause or lost the remote tail.");
        }
        var asr = AsrAudioPreparation.Prepare(mixed, Hash(mixed), Path.Combine(root, "asr"));
        using (var copy = new M2WaveReader(asr.Path))
            Require(copy.Format.SampleRate == 16000 && copy.FrameCount == 56000, "ASR copy lost the full elapsed timeline.");
        Require(originals.All(pair => Hash(pair.Key) == pair.Value), "Mixing or ASR modified original microphone files.");
    }

    private static byte[] Samples(WaveFormat format, int frames, float value)
    {
        var pcm = new byte[frames * format.BlockAlign];
        for (int i = 0; i < frames * format.Channels; i++)
            if (format.BitsPerSample == 32) BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4), value);
            else BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(value * 32768));
        return pcm;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static byte[] FormatBlob(WaveFormat format)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) format.Serialize(writer);
        byte[] bytes = stream.ToArray();
        if (bytes.Length < 4 || BinaryPrimitives.ReadInt32LittleEndian(bytes) != bytes.Length - 4)
            throw new InvalidDataException("Synthetic NAudio fmt fixture length mismatch.");
        var payload = bytes[4..];
        // WAV PCM fmt may omit cbSize. The endpoint property contains WAVEFORMATEX,
        // whose PCM representation includes the explicit two-byte zero cbSize.
        if (payload.Length == 16)
        {
            var native = new byte[18]; payload.CopyTo(native, 0); return native;
        }
        return payload;
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
