using System.Buffers.Binary;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ProjectEcho.Capture.Windows;

/// <summary>
/// Chooses a shared capture stream in the endpoint's device-frame rate domain.
/// The actual captured format, raw device counter and raw QPC remain unmodified.
/// This class neither changes endpoint settings nor initializes an audio stream.
/// </summary>
internal static class MicrophoneFormatPolicy
{
    internal static MicrophoneStreamFormat Select(WaveFormat mixFormat, WaveFormat deviceReportedFormat)
    {
        ArgumentNullException.ThrowIfNull(mixFormat);
        ArgumentNullException.ThrowIfNull(deviceReportedFormat);
        ValidateSupportedFormat(mixFormat);
        ValidateSupportedFormat(deviceReportedFormat);
        // GetMixFormat may be 48 kHz while the same endpoint reports 44.1 kHz and
        // GetBuffer's device positions advance in that device domain. Request the
        // endpoint format explicitly: AUTOCONVERTPCM permits the shared audio
        // engine to deliver it even when IsFormatSupported without that flag says
        // no. It does not alter system settings or make a different device eligible.
        bool conversion = mixFormat.SampleRate != deviceReportedFormat.SampleRate;
        WaveFormat captured = conversion ? deviceReportedFormat : mixFormat;
        Validate(captured, deviceReportedFormat);
        return new(captured, conversion
            ? AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality
            : 0);
    }

    internal static WaveFormat ReadDeviceFormat(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
            throw new NotSupportedException("所选麦克风不是活动采集端点；不会更换设备。");
        if (!device.Properties.TryGetValue<byte[]>(PropertyKeys.PKEY_AudioEngine_DeviceFormat, out var blob) || blob is null)
            throw new NotSupportedException("无法读取所选麦克风的设备格式，不能确认当前时钟适用条件；尚未启动采集，不会更换设备。");
        return ParseDeviceFormat(blob);
    }

    internal static WaveFormat ParseDeviceFormat(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        // PKEY_AudioEngine_DeviceFormat holds WAVEFORMATEX, including cbSize. Unlike
        // a WAV's optional 16-byte PCM fmt chunk, the property must have this header.
        if (blob.Length is < 18 or > 4096) throw new InvalidDataException("麦克风设备格式长度无效。");
        ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0, 2));
        ushort extra = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(16, 2));
        if (18 + extra != blob.Length) throw new InvalidDataException("麦克风设备格式的扩展长度与实际数据不符。");
        if (encoding is not (1 or 3 or 0xfffe)) throw new NotSupportedException("当前麦克风仅支持 PCM 或 Float32 原轨格式。");
        if ((encoding == 0xfffe && extra != 22) || (encoding != 0xfffe && extra != 0))
            throw new InvalidDataException("麦克风设备格式扩展结构无效。");

        WaveFormat format;
        if (encoding == 0xfffe)
        {
            int channels = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(2));
            int rate = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4));
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(14));
            int validBits = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(18));
            int mask = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(20));
            Guid subFormat = new(blob.AsSpan(24, 16));
            bool floating = subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (!floating && subFormat != new Guid("00000001-0000-0010-8000-00aa00389b71"))
                throw new NotSupportedException("不支持的麦克风设备子格式。");
            format = new WaveFormatExtensible(rate, bits, channels, floating, validBits, mask);
        }
        else
        {
            using var stream = new MemoryStream(blob, writable: false);
            using var reader = new BinaryReader(stream);
            format = WaveFormat.FromFormatChunk(reader, blob.Length);
            if (stream.Position != stream.Length) throw new InvalidDataException("麦克风设备格式含未解析字段。");
        }
        if (format.BlockAlign != BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(12)) ||
            format.AverageBytesPerSecond != BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8)))
            throw new InvalidDataException("麦克风设备格式的帧宽或字节率不符。");
        ValidateSupportedFormat(format);
        return format;
    }

    internal static void Validate(WaveFormat mixFormat, WaveFormat deviceReportedFormat)
    {
        ArgumentNullException.ThrowIfNull(mixFormat);
        ArgumentNullException.ThrowIfNull(deviceReportedFormat);
        ValidateSupportedFormat(mixFormat);
        ValidateSupportedFormat(deviceReportedFormat);
        if (mixFormat.SampleRate != deviceReportedFormat.SampleRate)
            throw new NotSupportedException($"所选麦克风共享格式为 {mixFormat.SampleRate} Hz，设备报告格式为 {deviceReportedFormat.SampleRate} Hz。" +
                "采集流须使用设备帧计数的采样率；不能把不同速率的帧数直接比较。尚未启动采集，不会更换麦克风或修改系统格式。");
    }

    private static void ValidateSupportedFormat(WaveFormat format)
    {
        var description = CaptureSession.Describe(format);
        description.Validate();
        if (format.Channels > 2) throw new NotSupportedException("当前麦克风仅支持单声道或双声道原轨。");
        if (format.AverageBytesPerSecond != checked(format.SampleRate * format.BlockAlign))
            throw new InvalidDataException("麦克风格式的字节率与采样率、帧宽不符。");
        if (format is WaveFormatExtensible extensible &&
            (extensible.ValidBitsPerSample <= 0 || extensible.ValidBitsPerSample > format.BitsPerSample ||
             (description.IsFloat && extensible.ValidBitsPerSample != 32)))
            throw new InvalidDataException("麦克风设备格式的有效位深无效。");
    }
}

internal sealed record MicrophoneStreamFormat(WaveFormat Format, AudioClientStreamFlags ConversionFlags);
