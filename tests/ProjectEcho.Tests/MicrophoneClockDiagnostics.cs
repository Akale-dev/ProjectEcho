using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>Opt-in three-second fixed-microphone metadata probe. Never reads or copies capture PCM.</summary>
internal static class MicrophoneClockDiagnostics
{
    internal static Task<int> RunAsync(string[] args) => Task.Run(() => Run(args));

    private static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--mic-clock-diagnostics" || string.IsNullOrWhiteSpace(args[1]) ||
            (args.Length == 3 && args[2] is not ("mix" or "native"))) return 2;
        string profile = args.Length == 3 ? args[2] : "mix";
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/MicrophoneClockDiagnostics");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var existing = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (existing.Length != 0) { Console.Error.WriteLine("Existing CaptureHost found; clock diagnostics refused."); return 2; } }
        finally { foreach (var process in existing) process.Dispose(); }
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + profile + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        Console.WriteLine("Fixed-microphone clock diagnostics, profile=" + profile + "; 3 seconds; metadata only, no PCM read/write.");
        Console.WriteLine("Evidence: " + run);
        var packets = new List<object>();
        var errors = new List<string>();
        object? metadata = null;
        string status = "FAILED";
        bool started = false, stopped = false, disposed = false;
        ulong? clockFrequency = null;
        AudioClient? client = null;
        Timer? hardDeadline = null;
        using var ready = new AutoResetEvent(false);
        using var packetFile = new StreamWriter(Path.Combine(run, "packets.jsonl"), false) { AutoFlush = true };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(args[1]);
            if (device.ID != args[1] || device.State != DeviceState.Active || device.DataFlow != DataFlow.Capture)
                throw new IOException("Explicit microphone is not active; no fallback attempted.");
            client = device.CreateAudioClient();
            WaveFormat mix = client.MixFormat;
            WaveFormat? native = null;
            byte[]? nativeBlob = null;
            string? nativeError = null;
            // NAudio 3.1.0 PropertyStore indexers/TryGetValue materialize the managed
            // BLOB while its PROPVARIANT is valid. GetValue's dangling pointer fields
            // are deliberately not used. This pointer is format metadata only.
            try
            {
                if (!device.Properties.TryGetValue<byte[]>(PropertyKeys.PKEY_AudioEngine_DeviceFormat, out var blob) || blob is null)
                    throw new InvalidDataException("DeviceFormat property is absent or not a managed BLOB.");
                nativeBlob = blob;
                if (blob.Length < 18 || blob.Length > 4096) throw new InvalidDataException("DeviceFormat BLOB length is invalid.");
                ushort extra = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(16, 2));
                if (18 + extra > blob.Length) throw new InvalidDataException("Truncated DeviceFormat BLOB.");
                if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(0, 2)) == 0xfffe && (extra < 22 || blob.Length < 40))
                    throw new InvalidDataException("Truncated extensible DeviceFormat BLOB.");
                var pin = GCHandle.Alloc(blob, GCHandleType.Pinned);
                try { native = WaveFormat.MarshalFromPtr(pin.AddrOfPinnedObject()); }
                finally { pin.Free(); }
            }
            catch (Exception ex) { nativeError = ex.GetType().Name + ": " + ex.Message; }
            bool mixSupported = client.IsFormatSupported(AudioClientShareMode.Shared, mix, out var mixClosest);
            bool nativeSupported = native is not null && client.IsFormatSupported(AudioClientShareMode.Shared, native);
            WaveFormat? requested = profile == "native" ? native : mix;
            bool selectedSupported = profile == "native" ? nativeSupported : mixSupported;
            metadata = new
            {
                Schema = "mic-clock-metadata-1", Os = Environment.OSVersion.Version.ToString(),
                Architecture = RuntimeInformation.OSArchitecture.ToString(), EndpointId = device.ID, EndpointName = device.FriendlyName,
                Profile = profile, MixFormat = Describe(mix), DeviceFormat = native is null ? null : Describe(native),
                DeviceFormatProperty = "PKEY_AudioEngine_DeviceFormat", DeviceFormatBlobHex = nativeBlob is null ? null : Convert.ToHexString(nativeBlob),
                DeviceFormatReadError = nativeError, MixSharedSupported = mixSupported,
                MixClosestFormat = mixClosest is null ? null : Describe(mixClosest), DeviceSharedSupported = nativeSupported,
                RequestedFormat = requested is null ? null : Describe(requested), SelectedSharedSupported = selectedSupported,
                StreamFlags = "EventCallback only; no AutoConvertPcm or SrcDefaultQuality", EndpointFallback = false,
                MetadataOnly = true, PcmRead = false, PcmCopied = false, PcmWritten = false,
                CaptureSeconds = 3, HardTimeoutSeconds = 5,
                Note = "DeviceFormat is the actual endpoint-reported engine format, not an assertion about physical hardware. Raw clock units are unmodified."
            };
            File.WriteAllText(Path.Combine(run, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
            if (requested is null || !selectedSupported)
                status = "UNSUPPORTED_NO_CAPTURE";
            else
            {
                // Independent process-wide ceiling remains armed through Stop and Dispose.
                // A hung native call cannot leave this diagnostics process capturing indefinitely.
                hardDeadline = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 0, 0, requested, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                var capture = client.AudioCaptureClient;
                var clock = client.AudioClockClient;
                clockFrequency = clock.Frequency;
                packetFile.WriteLine(JsonSerializer.Serialize(new { Type = "clock_frequency", RawFrequency = clockFrequency.Value.ToString(CultureInfo.InvariantCulture) }));
                client.Start(); started = true;
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(3))
                {
                    if (capture.GetNextPacketSize() <= 0) { ready.WaitOne(10); continue; }
                    // The returned PCM pointer is intentionally discarded. No dereference,
                    // Marshal.Copy, unmanaged span, waveform analysis or audio file exists here.
                    _ = capture.GetBuffer(out int frames, out var flags, out long devicePosition, out long packetQpc);
                    try
                    {
                        bool clockPositionAvailable = clock.GetPosition(out ulong clockPosition, out ulong clockQpc);
                        var packet = new
                        {
                            Type = "packet", Index = packets.Count, RawFrames = frames, Flags = (int)flags,
                            DevicePositionRaw = devicePosition.ToString(CultureInfo.InvariantCulture),
                            PacketQpc100ns = packetQpc.ToString(CultureInfo.InvariantCulture),
                            ClockPositionAvailable = clockPositionAvailable,
                            ClockPositionRaw = clockPosition.ToString(CultureInfo.InvariantCulture),
                            ClockQpc100ns = clockQpc.ToString(CultureInfo.InvariantCulture),
                            ObservedElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds
                        };
                        packets.Add(packet);
                        packetFile.WriteLine(JsonSerializer.Serialize(packet));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
                status = packets.Count > 0 ? "METADATA_COLLECTED" : "NO_PACKETS";
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name + ": " + ex.Message);
            File.WriteAllText(Path.Combine(run, "failure.txt"), ex.ToString());
        }
        finally
        {
            if (client is not null)
            {
                if (started)
                {
                    try { client.Stop(); stopped = true; }
                    catch (Exception ex) { errors.Add("stop:" + ex.GetType().Name + ": " + ex.Message); }
                }
                try { client.Dispose(); disposed = true; }
                catch (Exception ex) { errors.Add("dispose:" + ex.GetType().Name + ": " + ex.Message); }
            }
            hardDeadline?.Dispose();
        }
        if (errors.Count != 0) status = "FAILED";
        var report = new
        {
            Schema = "mic-clock-diagnostics-1", Status = status, Metadata = metadata,
            ClockFrequencyRaw = clockFrequency?.ToString(CultureInfo.InvariantCulture), PacketCount = packets.Count,
            Started = started, Stopped = stopped, ClientDisposed = disposed, PcmRead = false, PcmWritten = false,
            ClockUnitsConverted = false,
            Interpretation = "GetBuffer device position and IAudioClock position/frequency are separate raw observations. No assertion that they share units; no inferred frame rate or continuity acceptance.",
            Errors = errors, Packets = packets
        };
        string reportPath = Path.Combine(run, "report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{status}; packets={packets.Count}; stopped={stopped}; no PCM read or saved");
        Console.WriteLine("Report: " + reportPath);
        return status == "METADATA_COLLECTED" ? 0 : status == "UNSUPPORTED_NO_CAPTURE" ? 2 : 1;
    }

    private static object Describe(WaveFormat format) => new
    {
        format.SampleRate, format.Channels, format.BitsPerSample, format.BlockAlign, format.AverageBytesPerSecond,
        Encoding = format.Encoding.ToString(), FormatType = format.GetType().Name,
        ValidBitsPerSample = format is WaveFormatExtensible extensible ? (int?)extensible.ValidBitsPerSample : null,
        ChannelMask = format is WaveFormatExtensible channels ? (int?)channels.ChannelMask : null,
        SubFormat = format is WaveFormatExtensible subFormat ? subFormat.SubFormat.ToString() : null
    };
}
