using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ProjectEcho.Capture.Windows;

/// <summary>
/// Chooses a shared capture stream in the endpoint's device-frame rate domain.
/// The actual captured format, raw device counter and raw QPC remain unmodified.
/// RAW selection is local to a new client on the same explicitly selected endpoint.
/// No path changes endpoint settings or starts capture; initialization is explicit.
/// </summary>
internal static class MicrophoneFormatPolicy
{
    internal const string SharedDefault = "shared-default";
    internal const string SharedRaw = "shared-raw";
    internal const string SharedConverted = "shared-converted";

    /// <summary>
    /// Selects and optionally initializes a microphone client, but never starts it.
    /// The caller owns the returned client. RAW properties precede its first MixFormat read:
    /// NAudio caches that property and SetClientProperties does not invalidate the cache.
    /// </summary>
    internal static MicrophoneClientSelection<AudioClient> OpenClient(MMDevice device, bool initialize = false)
    {
        var deviceFormat = ReadDeviceFormat(device);
        return OpenClient(device.CreateAudioClient, client => client.MixFormat, client =>
        {
            if (!client.SupportsAudioClient2) throw new RawFormatUnavailableException("raw_client2_unavailable");
            client.SetClientProperties(AudioStreamCategory.Other, AudioClientStreamOptions.Raw);
        }, deviceFormat, initialize ? InitializeClient : null);

        static void InitializeClient(AudioClient client, WaveFormat format, AudioClientStreamFlags flags) =>
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback | flags,
                0, 0, format, Guid.Empty);
    }

    /// <summary>
    /// Injectable negotiation only: callbacks may query/configure/initialize, never Start.
    /// Every failed or unused client is released; only a successful result transfers ownership.
    /// </summary>
    internal static MicrophoneClientSelection<TClient> OpenClient<TClient>(Func<TClient> createClient,
        Func<TClient, WaveFormat> readMixFormat, Action<TClient> configureRaw, WaveFormat deviceReportedFormat,
        Action<TClient, WaveFormat, AudioClientStreamFlags>? initializeClient = null) where TClient : IDisposable
    {
        ArgumentNullException.ThrowIfNull(createClient); ArgumentNullException.ThrowIfNull(readMixFormat);
        ArgumentNullException.ThrowIfNull(configureRaw); ArgumentNullException.ThrowIfNull(deviceReportedFormat);
        ValidateSupportedFormat(deviceReportedFormat);
        WaveFormat defaultMix;
        TClient baseline = createClient();
        try
        {
            defaultMix = readMixFormat(baseline); ValidateSupportedFormat(defaultMix);
            if (defaultMix.SampleRate == deviceReportedFormat.SampleRate)
            {
                // Same-rate endpoints retain the original client, format, flags and processing mode.
                initializeClient?.Invoke(baseline, defaultMix, 0);
                return new(baseline, defaultMix, 0, SharedDefault, null, defaultMix.SampleRate, deviceReportedFormat.SampleRate);
            }
        }
        catch { baseline.Dispose(); throw; }
        baseline.Dispose();

        // A new client is essential: setting RAW after reading MixFormat can return NAudio's
        // cached default format. All factories here close over the same exact MMDevice.
        string fallbackReason;
        TClient raw = createClient();
        string stage = "configure";
        try
        {
            configureRaw(raw);
            stage = "format";
            WaveFormat rawMix = readMixFormat(raw);
            if (!IsUsableRawFormat(rawMix, deviceReportedFormat))
                throw new RawFormatUnavailableException("raw_format_not_in_device_rate_domain");
            stage = "initialize";
            initializeClient?.Invoke(raw, rawMix, 0);
            return new(raw, rawMix, 0, SharedRaw, null, defaultMix.SampleRate, deviceReportedFormat.SampleRate);
        }
        catch (Exception ex)
        {
            raw.Dispose();
            if (!CanFallbackFromRaw(ex)) throw;
            fallbackReason = ex is RawFormatUnavailableException unavailable ? unavailable.Reason
                : $"raw_{stage}_unsupported_0x{ex.HResult:X8}";
        }

        // No client has ever started. An explicitly unsupported RAW mode/format can retry
        // the old converted configuration, on another fresh client of the SAME endpoint.
        TClient fallback = createClient();
        try
        {
            WaveFormat fallbackMix = readMixFormat(fallback);
            var selected = Select(fallbackMix, deviceReportedFormat);
            initializeClient?.Invoke(fallback, selected.Format, selected.ConversionFlags);
            return new(fallback, selected.Format, selected.ConversionFlags,
                selected.ConversionFlags == 0 ? SharedDefault : SharedConverted, fallbackReason,
                fallbackMix.SampleRate, deviceReportedFormat.SampleRate);
        }
        catch { fallback.Dispose(); throw; }
    }

    internal static bool IsUsableRawFormat(WaveFormat rawMix, WaveFormat deviceReportedFormat)
    {
        ArgumentNullException.ThrowIfNull(rawMix); ArgumentNullException.ThrowIfNull(deviceReportedFormat);
        try { ValidateSupportedFormat(rawMix); return rawMix.SampleRate == deviceReportedFormat.SampleRate; }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or OverflowException)
        { return false; }
    }

    // Deliberately excludes E_ACCESSDENIED, DEVICE_INVALIDATED, RESOURCES_INVALIDATED,
    // endpoint/service failures and generic E_INVALIDARG. Those errors are not format fallback.
    internal static bool CanFallbackFromRaw(Exception exception) => exception is RawFormatUnavailableException ||
        exception is COMException && exception.HResult is AudioClientErrorCode.RawModeUnsupported
            or AudioClientErrorCode.UnsupportedFormat or unchecked((int)0x80004002) or unchecked((int)0x80004001);

    private sealed class RawFormatUnavailableException(string reason) : NotSupportedException(reason)
    {
        internal string Reason { get; } = reason;
    }

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

internal sealed record MicrophoneClientSelection<TClient>(TClient Client, WaveFormat Format,
    AudioClientStreamFlags ConversionFlags, string ProcessingMode, string? FallbackReason,
    int DefaultMixRate, int DeviceReportedRate) : IDisposable where TClient : IDisposable
{
    public void Dispose() => Client.Dispose();
}
