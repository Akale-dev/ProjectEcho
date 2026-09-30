using System.Buffers.Binary;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;

/// <summary>Pure format gate checks. No endpoint enumeration, configuration or audio activation.</summary>
internal static class MicrophoneFormatPolicyTests
{
    internal static void Run()
    {
        var mix48 = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var native44 = new WaveFormatExtensible(44100, 16, 2);
        Reject<NotSupportedException>(() => MicrophoneFormatPolicy.Validate(mix48, native44), "Cross-rate microphone accepted.");
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
    }

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
