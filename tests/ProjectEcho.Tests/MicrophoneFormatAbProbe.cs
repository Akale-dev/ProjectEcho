using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>
/// Explicit fixed-device, metadata-only WASAPI format comparison. Never dereferences the returned PCM pointer.
/// This mirrors fresh source openings used by initial mic, late mic-on and resume; it is not a Host boundary test.
/// </summary>
internal static class MicrophoneFormatAbProbe
{
    private const int SecondsPerSegment = 3;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static Task<int> RunAsync(string[] args) => Task.Run(() => Run(args));

    private static int Run(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--mic-format-ab-probe" || string.IsNullOrWhiteSpace(args[1]) ||
            (args.Length == 3 && args[2] != "explore")) return 2;
        bool explore = args.Length == 3;
        string root = SyntheticAudioProbe.FindProjectRoot();
        string parent = SyntheticAudioProbe.SafeProjectPath(root, ".local/MicrophoneFormatAb");
        Directory.CreateDirectory(parent);
        string lockPath = Path.Combine(root, ".local", "recording-probe.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Probe lock is a reparse point.");
        using var runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var hosts = Process.GetProcessesByName("ProjectEcho.CaptureHost");
        try { if (hosts.Length != 0) throw new IOException("Existing CaptureHost found; microphone diagnostics refused."); }
        finally { foreach (var host in hosts) host.Dispose(); }
        var selected = SourceDiscovery.Enumerate().Microphones.SingleOrDefault(device => device.Id == args[1])
            ?? throw new IOException("Explicit microphone unavailable; no fallback attempted.");
        string run = Path.Combine(parent, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "MICROPHONE_METADATA_ONLY_NO_PCM"),
            "Explicitly authorized fixed microphone. GetBuffer PCM pointer is discarded without dereference or copy. No WAV, replay, ASR, meeting access or upload. Endpoint settings unchanged.");
        Console.WriteLine("Mic A/B metadata evidence: " + run);
        var errors = new List<string>();
        var rejectedConfigurations = new List<string>();
        var summaries = new List<SegmentSummary>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(run, "run.json"), JsonSerializer.Serialize(new
        {
            Schema = "microphone-format-ab-metadata-1", StartedAtUtc = startedAt, Os = Environment.OSVersion.Version.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            SelectedMicrophone = selected, Mode = explore ? "explore" : "baseline-ab", SecondsPerSegment, Rounds = 3,
            MaximumActiveSeconds = explore ? 45 : 54, HardDeadlineSeconds = 150,
            PcmRead = false, PcmCopied = false, PcmWritten = false, PlayedAudio = false, Uploaded = false,
            ChangedDeviceSettings = false, EndpointFallback = false,
            ProductionAssembly = new { typeof(MicrophoneFormatPolicy).Assembly.Location,
                Sha256 = Hash(typeof(MicrophoneFormatPolicy).Assembly.Location) },
            TestAssembly = new { typeof(MicrophoneFormatAbProbe).Assembly.Location,
                Sha256 = Hash(typeof(MicrophoneFormatAbProbe).Assembly.Location) },
            Plan = explore
                ? "Three rounds of fresh client openings for raw-shared-mix, raw-reported-direct, plain-device-no-convert, production-no-src-quality and client3-default-period. RAW properties are set before the first MixFormat read. API rejection before Start is retained and never retried with a different format. No Communications, exclusive mode or system setting change."
                : "Three rounds, alternating production/shared-mix then shared-mix/production then production/shared-mix. Each profile gets initial_on, first_late_mic_on and reopen_after_pause source openings. Every opening creates a fresh client; the prior client is stopped and disposed before a 300ms no-client interval. No remote source or actual Host session is created."
        }, Json));
        // Independent of any native call or managed cleanup: never leave a hung diagnostic microphone active.
        using var hardDeadline = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        var expectedEndpointFormats = new Dictionary<string, string>();
        for (int round = 0; round < 3 && errors.Count == 0; round++)
        {
            string[] profiles = explore ? ["raw-shared-mix", "raw-reported-direct", "plain-device-no-convert", "production-no-src-quality", "client3-default-period"]
                : ["production", "shared-mix"];
            if (round % 2 != 0) Array.Reverse(profiles);
            foreach (string profile in profiles)
            {
                foreach (string scenario in explore ? ["fresh_on"] : new[] { "initial_on", "first_late_mic_on", "reopen_after_pause" })
                {
                    // There is no capture client during this interval. This is not a muted/zero-gain stream.
                    Thread.Sleep(300);
                    string id = $"round-{round + 1}-{profile}-{scenario}";
                    var summary = CaptureSegment(args[1], profile, id, run, expectedEndpointFormats);
                    summaries.Add(summary);
                    Console.WriteLine($"{id}: {summary.Status}; packets={summary.PacketCount}; rawFrames={summary.RawFrames}; stopped={summary.Stopped}; disposed={summary.ClientDisposed}");
                    if (summary.Errors.Count != 0)
                    {
                        if (explore && !summary.Started && summary.ClientDisposed && summary.Errors.All(error =>
                            error.StartsWith("Initialize:", StringComparison.Ordinal) || error.StartsWith("InitializeSharedAudioStream:", StringComparison.Ordinal) ||
                            error.StartsWith("GetSharedModeEnginePeriod:", StringComparison.Ordinal) ||
                            error.StartsWith("SetClientProperties_Other_Raw_before_first_MixFormat:", StringComparison.Ordinal)))
                            rejectedConfigurations.AddRange(summary.Errors.Select(error => id + ":" + error));
                        else { errors.AddRange(summary.Errors.Select(error => id + ":" + error)); break; }
                    }
                }
                if (errors.Count != 0) break;
            }
        }
        string status = errors.Count == 0 && summaries.Count == (explore ? 15 : 18)
            ? rejectedConfigurations.Count == 0 ? "METADATA_COLLECTED" : "METADATA_COLLECTED_WITH_REJECTIONS" : "FAILED";
        var report = new
        {
            Schema = "microphone-format-ab-metadata-1", Status = status, StartedAtUtc = startedAt, FinishedAtUtc = DateTimeOffset.UtcNow,
            Mode = explore ? "explore" : "baseline-ab", Segments = summaries, Errors = errors, RejectedConfigurationErrors = rejectedConfigurations,
            PcmRead = false, PcmCopied = false, PcmWritten = false, PlayedAudio = false, Uploaded = false,
            ContinuityVerdict = "DIAGNOSTIC_ONLY_NO_CAPTURE_COMPLETENESS_OR_PRIVACY_BOUNDARY_PASS_CLAIM",
            Interpretation = "Raw GetBuffer frame counts, device positions, QPC and flags are unchanged. Device counter units are measured against QPC and IAudioClock independently; no conversion to captured-frame units is assumed. Initial packet lengths and all positive/negative nominal QPC differences remain in evidence. A production format with automatic conversion is compared with the same endpoint's shared mix format without conversion flags."
        };
        string reportPath = Path.Combine(run, "report.json"); File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));
        Console.WriteLine(status + "; report: " + reportPath);
        return status != "FAILED" ? 0 : 1;
    }

    private static SegmentSummary CaptureSegment(string microphoneId, string profile, string id, string directory, Dictionary<string, string> expectedEndpointFormats)
    {
        var packets = new List<RawPacket>(); var errors = new List<string>();
        AudioClient? client = null;
        bool started = false, stopped = false, disposed = false;
        ulong? frequency = null;
        long? startBefore = null, startAfter = null, stopBefore = null, stopAfter = null;
        int? bufferFrames = null;
        long? streamLatency = null, defaultDevicePeriod = null, minimumDevicePeriod = null;
        int requestedRate = 0, deviceRate = 0;
        object? metadata = null;
        string apiStage = "GetDevice";
        using var ready = new AutoResetEvent(false);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(microphoneId);
            if (device.ID != microphoneId || device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
                throw new IOException("Selected microphone is not active; no fallback attempted.");
            client = device.CreateAudioClient();
            bool raw = profile is "raw-shared-mix" or "raw-reported-direct";
            if (raw)
            {
                apiStage = "SetClientProperties_Other_Raw_before_first_MixFormat";
                if (!client.SupportsAudioClient2) throw new NotSupportedException("IAudioClient2 is unavailable for RAW properties.");
                client.SetClientProperties(AudioStreamCategory.Other, AudioClientStreamOptions.Raw);
            }
            apiStage = "First_MixFormat_read";
            WaveFormat mix = client.MixFormat;
            WaveFormat reported = MicrophoneFormatPolicy.ReadDeviceFormat(device);
            string endpointFormats = JsonSerializer.Serialize(new { Mix = Describe(mix), Device = Describe(reported) });
            if (!expectedEndpointFormats.TryGetValue(profile, out string? expected)) expectedEndpointFormats.Add(profile, endpointFormats);
            else if (endpointFormats != expected) throw new IOException("Endpoint format changed within the profile during A/B; no device was changed or substituted.");
            string reportedFormat = JsonSerializer.Serialize(Describe(reported));
            if (!expectedEndpointFormats.TryGetValue("reported-device", out string? expectedReported)) expectedEndpointFormats.Add("reported-device", reportedFormat);
            else if (expectedReported != reportedFormat) throw new IOException("Endpoint-reported format changed during diagnostics; no setting was changed.");
            var production = MicrophoneFormatPolicy.Select(mix, reported);
            WaveFormat requested = profile switch
            {
                "production" or "production-no-src-quality" => production.Format,
                "raw-reported-direct" or "plain-device-no-convert" => reported,
                _ => mix
            };
            AudioClientStreamFlags flags = AudioClientStreamFlags.EventCallback | (profile switch
            {
                "production" => production.ConversionFlags,
                "production-no-src-quality" => AudioClientStreamFlags.AutoConvertPcm,
                _ => 0
            });
            AudioClientPeriodInfo? period = null;
            if (profile == "client3-default-period")
            {
                apiStage = "GetSharedModeEnginePeriod";
                if (!client.SupportsAudioClient3) throw new NotSupportedException("IAudioClient3 unavailable.");
                period = client.GetSharedModeEnginePeriod(requested);
            }
            requestedRate = requested.SampleRate; deviceRate = reported.SampleRate;
            metadata = new { Profile = profile, MixFormat = Describe(mix), ReportedDeviceFormat = Describe(reported),
                RequestedFormat = Describe(requested), Flags = flags.ToString(), FlagsNumeric = (long)flags,
                RawPropertiesBeforeFirstMixFormatRead = raw, StreamCategory = "Other (explicit only for RAW)",
                SupportsAudioClient2 = client.SupportsAudioClient2, SupportsAudioClient3 = client.SupportsAudioClient3,
                EnginePeriods = period, Initialization = period.HasValue ? "InitializeSharedAudioStream_default_period" : "Initialize_shared_event",
                Note = "IsFormatSupported without conversion flags is not used to reject the production AutoConvertPcm request. Initialize is the actual validation." };
            File.WriteAllText(Path.Combine(directory, id + "-metadata.json"), JsonSerializer.Serialize(metadata, Json));
            apiStage = period.HasValue ? "InitializeSharedAudioStream" : "Initialize";
            if (period.HasValue) client.InitializeSharedAudioStream(flags, period.Value.DefaultPeriodInFrames, requested, Guid.Empty);
            else client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, requested, Guid.Empty);
            apiStage = "ReadInitializedBufferMetadata";
            bufferFrames = client.BufferSize;
            streamLatency = client.StreamLatency;
            defaultDevicePeriod = client.DefaultDevicePeriod;
            minimumDevicePeriod = client.MinimumDevicePeriod;
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            var clock = client.AudioClockClient; frequency = clock.Frequency;
            apiStage = "Start"; startBefore = Now(); client.Start(); started = true; startAfter = Now();
            apiStage = "GetBuffer_metadata_loop";
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(SecondsPerSegment))
            {
                if (capture.GetNextPacketSize() <= 0) { ready.WaitOne(10); continue; }
                long observedBefore = Now();
                _ = capture.GetBuffer(out int frames, out var packetFlags, out long devicePosition, out long packetQpc);
                try
                {
                    bool available = clock.GetPosition(out ulong clockPosition, out ulong clockQpc);
                    packets.Add(new(packets.Count, frames, (int)packetFlags, devicePosition, packetQpc,
                        observedBefore, Now(), available, clockPosition, clockQpc));
                }
                finally { capture.ReleaseBuffer(frames); }
            }
        }
        catch (Exception ex) { errors.Add(apiStage + ":" + ex.GetType().Name + ":HResult=0x" + ex.HResult.ToString("X8") + ": " + ex.Message); }
        finally
        {
            if (client is not null)
            {
                if (started)
                    try { stopBefore = Now(); client.Stop(); stopped = true; stopAfter = Now(); }
                    catch (Exception ex) { errors.Add("stop:" + ex.GetType().Name + ": " + ex.Message); }
                try { client.Dispose(); disposed = true; }
                catch (Exception ex) { errors.Add("dispose:" + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        // Write after Stop/Dispose: diagnostic file IO is never on the active packet-reading path.
        using (var output = new StreamWriter(Path.Combine(directory, id + "-packets.jsonl"), false))
            foreach (var packet in packets) output.WriteLine(JsonSerializer.Serialize(packet));
        if (packets.Count == 0 && errors.Count == 0) errors.Add("no_packets_observed");
        var transitions = new List<object>();
        for (int i = 1; i < packets.Count; i++)
        {
            var prior = packets[i - 1]; var current = packets[i];
            decimal qpcDelta = current.PacketQpc100ns - (decimal)prior.PacketQpc100ns;
            decimal deviceDelta = current.DevicePositionRaw - (decimal)prior.DevicePositionRaw;
            decimal? returnedFrameGap = requestedRate > 0 ? qpcDelta - prior.RawFrames * 10_000_000m / requestedRate : null;
            // Keep startup transitions, flagged packets, device advance mismatch and QPC changes beyond one returned-frame interval.
            if (i <= 5 || current.Flags != 0 || prior.Flags != 0 || deviceDelta != prior.RawFrames ||
                (returnedFrameGap.HasValue && Math.Abs(returnedFrameGap.Value) > 10_000_000m / Math.Max(1, requestedRate)))
                transitions.Add(new { Index = i, PreviousFrames = prior.RawFrames, CurrentFrames = current.RawFrames,
                    DeviceDeltaRaw = deviceDelta, QpcDelta100ns = qpcDelta, QpcGapComparedWithReturnedFrames100ns = returnedFrameGap,
                    current.Flags, PriorFlags = prior.Flags });
        }
        RawPacket? first = packets.FirstOrDefault(), last = packets.LastOrDefault();
        decimal elapsedQpc = first is not null && last is not null ? last.PacketQpc100ns - (decimal)first.PacketQpc100ns : 0;
        long frameAdvance = packets.Take(Math.Max(0, packets.Count - 1)).Sum(packet => (long)packet.RawFrames);
        var timing = new
        {
            StartBeforeQpc100ns = startBefore, StartAfterQpc100ns = startAfter, StopBeforeQpc100ns = stopBefore, StopAfterQpc100ns = stopAfter,
            ClockFrequencyRaw = frequency?.ToString(CultureInfo.InvariantCulture), RequestedRate = requestedRate, ReportedDeviceRate = deviceRate,
            BufferCapacityFrames = bufferFrames, BufferCapacityMilliseconds = bufferFrames.HasValue && requestedRate > 0
                ? bufferFrames.Value * 1000d / requestedRate : (double?)null,
            StreamLatency100ns = streamLatency, DefaultDevicePeriod100ns = defaultDevicePeriod, MinimumDevicePeriod100ns = minimumDevicePeriod,
            MaximumGapBetweenObservedReadCalls100ns = packets.Count > 1
                ? packets.Zip(packets.Skip(1)).Max(pair => pair.Second.ObservedBeforeQpc100ns - pair.First.ObservedAfterQpc100ns) : (long?)null,
            MaximumObservedGetBufferCallMinusPacketQpc100ns = packets.Count > 0
                ? packets.Max(packet => packet.ObservedBeforeQpc100ns - packet.PacketQpc100ns) : (long?)null,
            DeviceCounterUnitsPerSecondObserved = elapsedQpc > 0 ? (last!.DevicePositionRaw - (decimal)first!.DevicePositionRaw) * 10_000_000m / elapsedQpc : (decimal?)null,
            ReturnedFramesPerSecondObserved = elapsedQpc > 0 ? frameAdvance * 10_000_000m / elapsedQpc : (decimal?)null,
            InitialFrames = packets.Take(6).Select(packet => packet.RawFrames).ToArray(),
            DiscontinuityPackets = packets.Count(packet => (packet.Flags & 1) != 0), TimestampErrorPackets = packets.Count(packet => (packet.Flags & 4) != 0),
            NonMonotonicQpcTransitions = packets.Zip(packets.Skip(1)).Count(pair => pair.Second.PacketQpc100ns <= pair.First.PacketQpc100ns),
            Transitions = transitions
        };
        var summary = new SegmentSummary(id, profile, errors.Count == 0 ? "METADATA_COLLECTED" : !started && disposed ? "REJECTED_BEFORE_START" : "FAILED", packets.Count,
            packets.Sum(packet => (long)packet.RawFrames), started, stopped, disposed, metadata, timing, errors);
        File.WriteAllText(Path.Combine(directory, id + "-summary.json"), JsonSerializer.Serialize(summary, Json));
        return summary;
    }

    private static long Now() => AudioClock.CounterTo100ns(Stopwatch.GetTimestamp(), Stopwatch.Frequency);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static object Describe(WaveFormat format) => new
    {
        format.SampleRate, format.Channels, format.BitsPerSample, format.BlockAlign, format.AverageBytesPerSecond,
        Encoding = format.Encoding.ToString(), Type = format.GetType().Name,
        ValidBits = format is WaveFormatExtensible valid ? (int?)valid.ValidBitsPerSample : null,
        ChannelMask = format is WaveFormatExtensible mask ? (int?)mask.ChannelMask : null
    };
    private sealed record RawPacket(int Index, int RawFrames, int Flags, long DevicePositionRaw, long PacketQpc100ns,
        long ObservedBeforeQpc100ns, long ObservedAfterQpc100ns, bool AudioClockPositionAvailable, ulong AudioClockPositionRaw, ulong AudioClockQpc100ns);
    private sealed record SegmentSummary(string Id, string Profile, string Status, int PacketCount, long RawFrames,
        bool Started, bool Stopped, bool ClientDisposed, object? Metadata, object Timing, IReadOnlyList<string> Errors);
}
