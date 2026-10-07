using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;
using ProjectEcho.Export;

if (args.Length == 1 && args[0] == "--m3m-lifetime-child") { _ = Console.ReadLine(); return 0; }

if (args.Length > 0 && args[0] == "-m" && args.Contains("-osrt"))
    return await M3WhisperTests.RunFixtureAsync(args);

if (args.Length == 1 && args[0] == "--version" || args.Length == 3 && args[0] == "-I" && args[2] == "--version")
    return M4QwenAdapterTests.FixtureVersion();

if (args.Contains("--model") && (args.Contains("--batch-jsonl") || args.Contains("--words-out")))
    return await M4QwenAdapterTests.Fixture(args);

if (args.Length > 0 && args[0] == "--m3-real-whisper-probe")
    return await M3RealWhisperProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3c-microphone-endurance-probe")
    return await M3cMicrophoneEnduranceProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3c-synthetic-speech-probe")
    return await M3cSyntheticSpeechProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3c-revalidate-mic-long")
    return M3cLongAudioExportProbe.Run(args);

if (args.Length > 0 && args[0] == "--list-audio-devices")
    return AudioDeviceDiagnostics.Run(args);

if (args.Length > 0 && args[0] == "--m2-sync-probe")
    return await M2LongRunProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m2-endurance-probe")
    return await M2LongRunProbe.RunEnduranceAsync(args);

if (args.Length > 0 && args[0] == "--m2-concurrent-export-probe")
    return await M2ConcurrentExportProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3d-real-whisper-probe")
    return await M3dRealWhisperProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3d-vulkan-full-probe")
    return await M3dVulkanFullProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3h-meter-preview-probe")
    return await M3hAudioLevelPreviewProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m3hfix-asr-probe")
    return await M3hFixAsrProbe.RunAsync(args);

if (args.Length == 2 && args[0] == "--m4-window-long-probe")
    return M4QwenWindowTests.RunLongProbe(args[1]);

if (args.Length > 0 && args[0] == "--m4-real-qwen-probe")
    return await M4RealQwenProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m4-qwen-pause-probe")
    return await M4QwenPauseProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m4-qwen-index-probe")
    return M4QwenIndexProbe.Run(args);

if (args.Length > 0 && args[0] == "--m5-speaker-pipeline-probe")
    return await M5SpeakerPipelineProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m5-speaker-mapping-probe")
    return await M5SpeakerMappingProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m5-speaker-saved-policy-probe")
    return await M5SpeakerSavedPolicyProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m5-runtime-boundary-probe")
    return await M5RuntimeBoundaryProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m5-speaker-reopen-probe")
    return M5SpeakerReopenProbe.Run(args);

if (args.Length > 0 && args[0] == "--m5-interval-fix-probe")
    return await M5IntervalFixProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m5b-case-probe")
    return await M5BCaseProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m2-revalidate-long-run")
    return await M2LongRunProbe.RunRevalidationAsync(args);

if (args.Length > 0 && args[0] == "--wpf-playback-source")
    return await WpfPlaybackSource.RunAsync(args);

if (args.Length > 0 && args[0] == "--wpf-playback-integration")
    return await WpfPlaybackIntegration.RunAsync(args);

if (args.Length > 0 && args[0] == "--m1-microphone-probe")
    return await M1MicrophoneProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--mic-clock-diagnostics")
    return await MicrophoneClockDiagnostics.RunAsync(args);

if (args.Length > 0 && args[0] == "--mic-format-ab-probe")
    return await MicrophoneFormatAbProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--mic-scheduling-stress-probe")
    return await MicSchedulingStressProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--cpu-stress-child")
    return await MicSchedulingStressProbe.RunCpuChildAsync(args);

if (args.Length > 0 && args[0] == "--tone-report")
    return ToneAnalysis.Run(args);

if (args.Length > 0 && args[0] == "--audit-recording")
    return RecordingAudit.Run(args);

if (args.Length > 0 && args[0] == "--clock-capability")
    return ClockEvidence.Run(args);

if (args.Length > 0 && args[0] == "--probe-tone-source")
    return await ProbeToneSource.RunAsync(args);

if (args.Length > 0 && args[0] == "--synthetic-audio-probe")
    return await SyntheticAudioProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m1-audio-probe")
    return await M1IntegrationProbe.RunAsync(args);

if (args.Length > 0 && args[0] == "--m1-silence-probe")
    return await M1SilenceProbe.RunAsync(args);

if (args.Length > 0 && args[0] is "--m1-disconnect-probe" or "--m1-crash-probe")
    return await M1FailureProbes.RunAsync(args);

if (args.Length != 0 && !(args.Length == 2 && args[0] == "--output"))
{
    Console.Error.WriteLine("Unknown test arguments. Default tests never record/play audio; live testing requires --synthetic-audio-probe.");
    return 2;
}

// No extra test-framework packages; only existing production dependencies.
// The default path below has no audio device enumeration, activation or capture.
var suppliedOutput = args.Length == 2 && args[0] == "--output" ? args[1] : null;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("在 ProjectEcho 工程内运行测试，或构建完整工程后重试。");
var output = Path.GetFullPath(suppliedOutput ?? Path.Combine(root.FullName, ".local", "TestResults", "Current", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
if (!output.StartsWith(root.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("合成测试输出必须留在本项目内。");
Directory.CreateDirectory(output);
var results = new List<TestResult>();

Run("M0-SYNTH controlled producer waveform", ProbeToneSource.TestWaveform);
Run("M0-SYNTH probe signal and isolation fixtures", () => ProbeSignalChecks.RunSyntheticTests(output));
Run("M0-SYNTH probe consent scope and started handshake", () => ProbeSafetyTests.Run(output));
Run("M1-SYNTH real microphone probe range assertions", M1MicrophoneProbe.RunSyntheticChecks);
Run("T54-SYNTH health snapshot ordering never underflows", RecordingHealthTests.Run);
M3mAutoStopTests.Register(Run, output);
M3mAutoStopTests.RegisterM3n(Run, output);
Run("M1-SYNTH microphone reported/client rate policy", MicrophoneFormatPolicyTests.Run);
MicrophoneFormatPolicyTests.RegisterTimelineTests(Run, output);
M3bMicrophoneClockTests.Register(Run, output);
EndpointPacketClockTests.Register(Run, output);
M2PacketOverlapTests.Register(Run, output);
EndpointAudioQualityTests.Register(Run, output);
MicrophonePreparationTests.Register(Run, output);
ProcessLoopbackSupportTests.Register(Run);
M3cMicrophoneGapRenderTests.Register(Run, output);
MicrophoneProcessingMetadataTests.Register(Run, output);
MicrophoneRawNegotiationTests.Register(Run);
CaptureThreadSchedulingTests.Register(Run);
CapturePacketScratchTests.Register(Run);
Run("M0 explicit clock capability preserves limited QPC status", ClockCapabilityTests.Run);
M1StorageTests.Register(Run, output);
M1ValidationIsolationTests.Register(Run, output);
M2CodecTests.Register(Run, output);
M2Mp3Tests.Register(Run, output);
M3AudioPreparationTests.Register(Run, output);
M3AudioImportTests.Register(Run, output);
M3SubtitleExportTests.Register(Run, output);
M3SentenceSegmentationTests.Register(Run, output);
M3jSentenceBoundaryTests.Register(Run, output);
M3TranscriptTests.Register(Run, output);
M3jTranscriptDraftTests.Register(Run, output);
M3kTimestampInputTests.Register(Run, output);
M3kDataRootPreferenceTests.Register(Run, output);
M3cSqliteTests.Register(Run, output);
M3cTranscriptTests.Register(Run, output);
M3WhisperTests.Register(Run, output);
M3LocalTranscriberTests.Register(Run, output);
M3dTranscriptVersionTests.Register(Run, output);
M3dWhisperTests.Register(Run, output);
M3dPortableWhisperTests.Register(Run, output);
M3eWhisperTimestampTests.Register(Run, output);
M3iWhisperTimestampTests.Register(Run, output);
M3iAsrEndBoundaryTests.Register(Run, output);
M3iDiagnosticPresentationTests.Register(Run, output);
M3iMixProgressTests.Register(Run, output);
M3iRecoveryProgressTests.Register(Run, output);
M5dAsrProCompatibilityTests.Register(Run, output);
M5dQwenPartialAlignmentTests.Register(Run, output);
M3gTimelinePrecisionTests.Register(Run, output);
M3hMeetingWhisperTests.Register(Run, output);
M3hAudioLevelPreviewTests.Register(Run);
M3hMeterDisplayTests.Register(Run);
M3hFixPauseTests.Register(Run, output);
M3hFixStorageTests.Register(Run, output);
M4QwenWindowTests.Register(Run, output);
M4AlignmentSentenceTests.Register(Run, output);
M4QwenAdapterTests.Register(Run, output);
M4QwenPortableSettingsTests.Register(Run, output);
M5SpeakerIndexTests.Register(Run, output);
M5SpeakerCoreTests.Register(Run, output);
M5SpeakerRuntimeTests.Register(Run, output);
M5DiarizationTimestampTests.Register(Run, output);
M5BLocalOverlapTests.Register(Run, output);
M5BSpeakerTextTests.Register(Run, output);
M3eMeetingLogTests.Register(Run, output);
M4aTimelineTests.Register(Run, output);
TruePeakTests.Register(Run, output);
M2ExportTests.Register(Run, output);
M2SavedMixLoaderTests.Register(Run, output);
M2LargeWaveTests.Register(Run, output);
Run("T24-SYNTH acoustic marker detector rejects silence and verifies known delays", M2LongRunProbe.RunSyntheticChecks);
Run("T26/T28-SYNTH WPF rendered tail detects integer truncation and timing errors", WpfPlaybackIntegration.RunSyntheticChecks);
Run("M2-SYNTH ended long-run revalidation scope and readiness guards", M2LongRunProbe.RunRevalidationGuardChecks);
Run("M1-SYNTH real writer and capture-thread control boundaries", () => M1RecordingTests.Run(output));

Run("M0-SYNTH raw packet clock health", CaptureClockHealthTests.Run);
Run("M0-SYNTH offline audit rejects repeated QPC", () => RecordingAuditClockTests.Run(output));
Run("M0-SYNTH writer open failure never starts capture", () => CaptureLifecycleTests.WriterFailure(output));
Run("M0-SYNTH cancellation while writer prepares never starts capture", CaptureLifecycleTests.CancelDuringWriterStartup);
Run("M0-SYNTH all sources stop before tail work and release", CaptureLifecycleTests.StopsPrecedeTailWork);
Run("M0-SYNTH Host IPC backpressure and independent watchdog", HostTransportSafetyTests.Run);
Run("M0-SYNTH degraded preview audit selection", () => PreviewSelectionTests.Run(output));
Run("M0 packet clock qualities survive JSON and legacy records", () =>
{
    var legacy = JsonSerializer.Deserialize<CapturedPacket>("{\"WaveFrameOffset\":0,\"FrameCount\":480,\"RawFrameCount\":480,\"DevicePosition\":0,\"QpcPosition100ns\":100000,\"Flags\":0,\"TimestampValid\":true}")!;
    Check(legacy.DevicePositionValid, "Legacy packet compatibility changed; audit must still independently check its counter.");
    var qpcOnly = legacy with { DevicePositionValid = false };
    var roundTrip = JsonSerializer.Deserialize<CapturedPacket>(JsonSerializer.Serialize(qpcOnly))!;
    Equal(qpcOnly, roundTrip);
    Equal("qpc_referenced", roundTrip.GetTimestampQuality());
    Equal("device_referenced", legacy.GetTimestampQuality());
    Equal("invalid", (qpcOnly with { TimestampValid = false }).GetTimestampQuality());
});

Run("T55-SYNTH playback denied in Recording/Paused/Stopping/Faulted", () =>
{
    foreach (var state in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping, SessionState.Faulted })
        Check(!SessionRules.CanPlayback(state), $"Unexpected playback permission in {state}");
    Check(SessionRules.CanPlayback(SessionState.Idle) && SessionRules.CanPlayback(SessionState.Saved), "Idle/Saved should permit playback.");
    Check(SessionRules.CanPlayback(SessionState.SavedIncomplete) && SessionRules.CanStart(SessionState.SavedIncomplete),
        "A finalized incomplete preview must allow audition and a fresh meeting.");
    Check(SessionRules.CanPlayback(SessionState.StoppedWithError) && SessionRules.CanStart(SessionState.StoppedWithError),
        "A confirmed stopped failed capture must not disable historical playback.");
});
Run("M0 default title and unclassified project", () =>
{
    Check(SessionDefaults.DefaultProject == "未分类", "Project default changed.");
    Check(SessionDefaults.Title(new DateTimeOffset(2026, 9, 28, 4, 5, 6, TimeSpan.FromHours(8))) == "2026-09-28 04-05-06", "Title is not local timestamp.");
});
Run("T22-SYNTH raw QPC frequency converted to API 100ns", () =>
{
    Equal(10_000_000L, AudioClock.CounterTo100ns(24_000_000, 24_000_000));
    Equal(20_000_000L, AudioClock.CounterTo100ns(48_000_000, 24_000_000));
    Throws<ArgumentOutOfRangeException>(() => AudioClock.CounterTo100ns(1, 0));
});
Run("T22-SYNTH large API QPC preserves precise frame and signed offset", () =>
{
    const ulong epoch = 18_000_000_000_000_000_000;
    Equal(480L, AudioClock.FrameAt(epoch + 100_000, epoch, 48_000));
    Equal(-480L, AudioClock.FrameAt(epoch - 100_000, epoch, 48_000));
    Equal(10_000L, AudioClock.SessionMicroseconds(epoch + 100_000, epoch));
});
Run("T51/T52-SYNTH cutoff counts only complete pre-boundary frames", () =>
{
    Equal(48, AudioClock.CompleteFramesBefore(100_000, 110_000, 48_000, 480));
    Equal(47, AudioClock.CompleteFramesBefore(100_000, 109_999, 48_000, 480));
    Equal(0, AudioClock.CompleteFramesBefore(100_000, 99_999, 48_000, 480));
    Equal(480, AudioClock.CompleteFramesBefore(100_000, 900_000, 48_000, 480));
});
Run("T57-SYNTH disk budget uses negotiated raw formats", () =>
{
    var remote = DiskBudget.PcmBytes(48_000, 8, TimeSpan.FromHours(1));
    var mic = DiskBudget.PcmBytes(48_000, 4, TimeSpan.FromHours(1));
    Equal(2_073_600_000L, remote + mic);
    Equal(4L, DiskBudget.PcmBytes(48_000, 4, TimeSpan.FromTicks(1)));
    Throws<OverflowException>(() => DiskBudget.PcmBytes(int.MaxValue, int.MaxValue, TimeSpan.MaxValue));
});
Run("M0 peaks tolerate invalid meter input without NaN", () =>
{
    Near(.8f, AudioLevels.Peak([-.8f, .1f, float.NaN, float.PositiveInfinity]));
    Near(1, AudioLevels.Peak([2]));
    Near(0, AudioLevels.Peak([]));
});
Run("T23/T25/T26-SYNTH simultaneous mixing retains delayed longest tail", () =>
{
    var remote = Track("remote-overlay", 48_000, Enumerable.Repeat(.2f, 480).ToArray(), [Packet(0, 480, 0)]);
    var mic = Track("mic-overlay", 48_000, Enumerable.Repeat(.6f, 480).ToArray(), [Packet(0, 480, 50_000)]);
    var rendered = Render("overlap", 100_000, remote, mic);
    Equal(720L, rendered.FrameCount); // Does not stop at the shorter source or requested duration.
    var pcm = ReadRendered(rendered.Path);
    Near(.1f, pcm[0]);
    Near(.4f, pcm[300 * 2]);
    Near(.3f, pcm[719 * 2]); // Exact last output frame retains the longer mic tail.
});
Run("M0 incomplete QPC-only preview preserves failure and original PCM", () =>
{
    var track = Track("qpc-only-preview", 48_000, Enumerable.Repeat(.2f, 480).ToArray(),
        [Packet(0, 480, 0) with { DevicePositionValid = false }]);
    var originalHash = SHA256.HashData(File.ReadAllBytes(track.WavePath));
    var result = Result(100_000, track) with { Complete = false, Warnings = ["device position unavailable"] };
    var rendered = TimelineWaveRenderer.Render(result, Path.Combine(output, "qpc-only-preview.wav"));
    Check(!result.Complete, "Preview must not promote capture completeness.");
    Equal(480L, rendered.FrameCount);
    Check(rendered.Warnings.Contains("device position unavailable") && rendered.Warnings.Count >= 3,
        "Capture and incomplete-preview warnings must survive.");
    Check(SHA256.HashData(File.ReadAllBytes(track.WavePath)).SequenceEqual(originalHash), "Preview changed original PCM.");
    Near(.2f, ReadRendered(rendered.Path)[478 * 2]);
    var invalid = track with { Packets = [track.Packets[0] with { TimestampValid = false }] };
    Throws<InvalidDataException>(() => TimelineWaveRenderer.Render(result with { Tracks = [invalid] },
        Path.Combine(output, "bad-qpc-incomplete.wav")));
});
Run("T13/T14/T23-SYNTH explicit packet gaps and full elapsed duration", () =>
{
    var track = Track("remote-gaps", 48_000, Enumerable.Repeat(.4f, 960).ToArray(), [Packet(0, 480, 0), Packet(480, 480, 200_000)]);
    var rendered = Render("gaps", 400_000, track);
    Equal(1920L, rendered.FrameCount);
    var pcm = ReadRendered(rendered.Path);
    Near(.4f, pcm[479 * 2]);
    Near(0, pcm[480 * 2]);
    Near(0, pcm[959 * 2]);
    Near(.4f, pcm[960 * 2]);
    Near(.4f, pcm[1439 * 2]);
    Near(0, pcm[1919 * 2]);
});
Run("T23-SYNTH 44.1kHz arbitrary packet boundaries do not accumulate rounding", () =>
{
    var secondQpc = (ulong)Math.Round(421d * AudioClock.UnitsPerSecond / 44_100);
    var track = Track("remote-resample", 44_100, Enumerable.Repeat(.3f, 882).ToArray(), [Packet(0, 421, 0), Packet(421, 461, secondQpc)]);
    var rendered = Render("resampled", 200_000, track);
    Equal(960L, rendered.FrameCount);
    Near(.3f, ReadRendered(rendered.Path)[959 * 2]);
});
Run("T54-SYNTH no packets remain distinguishable from acquired silence", () =>
{
    var empty = Track("empty-source", 48_000, [], []);
    var rendered = Render("no-packets", 100_000, empty);
    Equal(480L, rendered.FrameCount);
    Check(rendered.Warnings.Any(w => w.Contains("未收到音频包")), "No-data warning missing.");
    Check(ReadRendered(rendered.Path).All(v => v == 0), "Gap render should be silent.");
});
Run("T22-SYNTH invalid timestamps refuse false alignment and preserve originals", () =>
{
    var track = Track("invalid-time", 48_000, [.2f], [Packet(0, 1, 0) with { TimestampValid = false }]);
    var hash = SHA256.HashData(File.ReadAllBytes(track.WavePath));
    Throws<InvalidDataException>(() => Render("invalid-time", 100_000, track));
    Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(track.WavePath))), "Original changed after failed render.");
    Check(!File.Exists(Path.Combine(output, "invalid-time-mixed.wav")), "Failed alignment produced playback file.");
});
Run("M0 source overlap and frame mismatch are visible failures", () =>
{
    var overlap = Track("overlap-invalid", 48_000, [.2f, .3f], [Packet(0, 1, 0), Packet(1, 1, 0)]);
    Throws<InvalidDataException>(() => Render("overlap-invalid", 100_000, overlap));
    var mismatch = Track("mismatch-invalid", 48_000, [.2f], [Packet(0, 1, 0)]) with { FrameCount = 2 };
    Throws<InvalidDataException>(() => Render("mismatch-invalid", 100_000, mismatch));
});
Run("T25-SYNTH render refuses original overwrite", () =>
{
    var track = Track("immutable", 48_000, [.2f], [Packet(0, 1, 0)]);
    var hash = SHA256.HashData(File.ReadAllBytes(track.WavePath));
    Throws<InvalidOperationException>(() => TimelineWaveRenderer.Render(Result(100_000, track), track.WavePath));
    Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(track.WavePath))), "Original changed.");
});
Run("M0 PCM16/24/32 source conversion and stereo channel preservation", () =>
{
    foreach (var bits in new[] { 16, 24, 32 })
    {
        var format = new PcmFormat(48_000, 2, bits, false, 2 * bits / 8);
        var path = Path.Combine(output, $"pcm{bits}.wav");
        WriteSyntheticWave(path, format, [-.5f, .25f]);
        var track = new CapturedTrack($"pcm{bits}", path, format, 1, [Packet(0, 1, 0)]);
        var pcm = ReadRendered(Render($"pcm{bits}", 209, track).Path);
        Near(-.5f, pcm[0]);
        Near(.25f, pcm[1]);
    }
});
Run("M0 corrupt WAV length and non-finite float data rejected", () =>
{
    var track = Track("bad-length", 48_000, [.2f], [Packet(0, 1, 0)]);
    using (var stream = new FileStream(track.WavePath, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - 1);
    Throws<InvalidDataException>(() => Render("bad-length", 100_000, track));
    var nonfinite = Track("bad-float", 48_000, [float.NaN], [Packet(0, 1, 0)]);
    Throws<InvalidDataException>(() => Render("bad-float", 100_000, nonfinite));
});
Run("M0 NAudio 3.1 WaveFileWriter float32 header renders and reads back", () =>
{
    var format = new PcmFormat(48_000, 2, 32, true, 8);
    var track = NaudioTrack("naudio-float", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), format, [.2f, -.3f, .4f, -.5f]);
    var pcm = ReadRendered(Render("naudio-float", 417, track).Path);
    Near(.2f, pcm[0]); Near(-.3f, pcm[1]); Near(.4f, pcm[2]); Near(-.5f, pcm[3]);
});
Run("M0 NAudio extensible float and 24-in-32 PCM headers retain layout", () =>
{
    foreach (var useFloat in new[] { true, false })
    {
        var id = useFloat ? "naudio-extensible-float" : "naudio-extensible-pcm24in32";
        var format = new PcmFormat(48_000, 2, 32, useFloat, 8, 3);
        var waveFormat = new WaveFormatExtensible(48_000, 32, 2, useFloat, useFloat ? 32 : 24, 3);
        // These exact fractions have zero low 8 bits in PCM32, making them valid 24-in-32 samples.
        var track = NaudioTrack(id, waveFormat, format, [-.5f, .25f]);
        var pcm = ReadRendered(Render(id, 209, track).Path);
        Near(-.5f, pcm[0]); Near(.25f, pcm[1]);
    }
});
Run("M0 NAudio PCM24 odd data size and padded RIFF both render exact final frame", () =>
{
    var format = new PcmFormat(48_000, 1, 24, false, 3);
    var track = NaudioTrack("naudio-pcm24-odd", new WaveFormat(48_000, 24, 1), format, [-.5f]);
    Near(-.5f, ReadRendered(Render("naudio-pcm24-odd", 209, track).Path)[0]);
    // Test the other legal reader case explicitly: terminal odd-sized data with its RIFF pad.
    var bytes = File.ReadAllBytes(track.WavePath);
    var data = FindWaveData(bytes);
    Equal(3, data.Size);
    Console.WriteLine($"NAudio PCM24 odd data: {data.Size} bytes; RIFF terminal pad bytes: {bytes.Length - data.Offset - data.Size}.");
    if (bytes.Length == data.Offset + data.Size)
    {
        Array.Resize(ref bytes, bytes.Length + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
    }
    var paddedPath = Path.Combine(output, "naudio-pcm24-padded-source.wav");
    File.WriteAllBytes(paddedPath, bytes);
    var paddedTrack = track with { WavePath = paddedPath, SourceId = "naudio-pcm24-padded" };
    Near(-.5f, ReadRendered(Render("naudio-pcm24-padded", 209, paddedTrack).Path)[0]);
});
foreach (var (id, waveFormat, format) in new (string, WaveFormat, PcmFormat)[]
{
    ("float32", WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), new(48_000, 2, 32, true, 8)),
    ("pcm16", new WaveFormat(48_000, 16, 2), new(48_000, 2, 16, false, 4)),
    ("pcm24-odd", new WaveFormat(48_000, 24, 1), new(48_000, 1, 24, false, 3)),
    ("extensible-float", new WaveFormatExtensible(48_000, 32, 2, true, 32, 3), new(48_000, 2, 32, true, 8, 3)),
    ("extensible-pcm24in32", new WaveFormatExtensible(48_000, 32, 2, false, 24, 3), new(48_000, 2, 32, false, 8, 3))
})
Run($"T53-SYNTH production WAV checkpoint data offsets and payload: {id}", () =>
{
    var path = Path.Combine(output, $"checkpoint-{id}.wav.partial");
    var samples = Enumerable.Range(0, 3 * format.Channels).Select(i => i % 2 == 0 ? -.5f : .25f).ToArray();
    var payload = SyntheticPcmBytes(format, samples);
    long savedOffset;
    using (var file = new CaptureWaveFile(path, waveFormat))
    {
        // This is the same file owner and stored offset that CaptureSession.TrackWriter uses.
        savedOffset = file.DataOffset;
        VerifyCheckpoint(0);
        file.Writer.Write(payload, 0, format.BlockAlign);
        VerifyCheckpoint(format.BlockAlign);
        file.Writer.Write(payload, format.BlockAlign, payload.Length - format.BlockAlign);
        VerifyCheckpoint(payload.Length);

        void VerifyCheckpoint(int committedBytes)
        {
            file.Writer.Flush();
            file.Stream.Flush(true);
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var snapshot = new MemoryStream();
            reader.CopyTo(snapshot);
            var bytes = snapshot.ToArray();
            var data = FindWaveData(bytes);
            Equal((long)data.Offset, file.DataOffset);
            Equal(committedBytes, data.Size);
            Check(file.DataOffset + committedBytes <= bytes.Length, "Checkpoint extends past committed WAV bytes.");
            Check(bytes.AsSpan((int)file.DataOffset, committedBytes).SequenceEqual(payload.AsSpan(0, committedBytes)),
                "Checkpoint offset does not locate exactly the committed PCM.");
        }
    }
    var finalized = File.ReadAllBytes(path);
    var finalData = FindWaveData(finalized);
    Equal(savedOffset, (long)finalData.Offset);
    Equal(payload.Length, finalData.Size);
    Check(finalized.AsSpan(finalData.Offset, finalData.Size).SequenceEqual(payload), "Finalization changed checkpoint PCM.");
});
Run("S01-SYNTH ProjectEcho actual assembly and shell roots rejected", () =>
{
    foreach (var name in new[] { "ProjectEcho", "projectecho.app", "ProjectEcho.CaptureHost", "explorer", "powershell" })
    {
        var identity = new ProcessIdentity(500_000, 1, Path.Combine(output, name + ".exe"), name);
        Check(SourceDiscovery.IsBroadRoot(identity), $"Broad process root accepted: {name}");
        Throws<InvalidOperationException>(() => SourceDiscovery.ValidateSource(new(identity, identity, "synthetic", "synthetic", "synthetic", "synthetic")));
    }
    var renamed = new ProcessIdentity(500_000, 1, Path.Combine(output, "ProjectEcho.exe"), "misleading-display-name");
    Check(SourceDiscovery.IsBroadRoot(renamed), "Executable identity must reject the actual App assembly name.");
    Check(!SourceDiscovery.IsBroadRoot(new(500_000, 1, Path.Combine(output, "SyntheticMeeting.exe"), "SyntheticMeeting")), "Ordinary fake identity is not intrinsically a broad root.");
});
Run("S01-SYNTH missing PID and changed start time rejected without audio access", () =>
{
    using var current = System.Diagnostics.Process.GetCurrentProcess();
    var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks, current.MainModule!.FileName!, current.ProcessName);
    Check(SourceDiscovery.IdentityMatches(identity), "Current process identity should match its own metadata.");
    Check(!SourceDiscovery.IdentityMatches(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 }), "Changed process start time accepted.");
    Check(!SourceDiscovery.IdentityMatches(identity with { ExecutablePath = Path.Combine(output, "fake-process.exe") }), "Changed executable path accepted.");
    var absent = new ProcessIdentity(int.MaxValue, 1, Path.Combine(output, "fake-process.exe"), "SyntheticMeeting");
    Check(!SourceDiscovery.IdentityMatches(absent), "Missing PID matched.");
    Throws<InvalidOperationException>(() => SourceDiscovery.ValidateSource(new(absent, absent, "synthetic", "synthetic", "synthetic", "synthetic")));
});

var summary = new
{
    isSynthetic = true,
    hardwareCaptureRun = false,
    timeUtc = DateTimeOffset.UtcNow,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    passed = results.Count(r => r.Passed),
    failed = results.Count(r => !r.Passed),
    tests = results
};
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Synthetic tests: {summary.passed} passed, {summary.failed} failed. No hardware capture performed.");
Console.WriteLine($"Evidence: {output}");
return summary.failed == 0 ? 0 : 1;

void Run(string name, Action action)
{
    try
    {
        action();
        results.Add(new TestResult(name, true, null));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        results.Add(new TestResult(name, false, ex.ToString()));
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}
CapturedPacket Packet(long offset, int frames, ulong relativeQpc)
    => new(offset, frames, frames, (ulong)offset, 1_000_000_000 + relativeQpc, 0, true);
CapturedTrack Track(string id, int rate, float[] samples, IReadOnlyList<CapturedPacket> packets)
{
    var format = new PcmFormat(rate, 1, 32, true, 4);
    var path = Path.Combine(output, id + "-source.wav");
    WriteSyntheticWave(path, format, samples);
    return new CapturedTrack(id, path, format, samples.Length, packets);
}
CaptureResult Result(long duration, params CapturedTrack[] tracks)
    => new(output, Path.Combine(output, "synthetic-index-not-used.jsonl"), DateTimeOffset.UtcNow, 1_000_000_000, duration, tracks, true, []);
RenderResult Render(string id, long duration, params CapturedTrack[] tracks)
    => TimelineWaveRenderer.Render(Result(duration, tracks), Path.Combine(output, id + "-mixed.wav"));
CapturedTrack NaudioTrack(string id, WaveFormat waveFormat, PcmFormat format, float[] samples)
{
    var path = Path.Combine(output, id + "-source.wav");
    var payload = SyntheticPcmBytes(format, samples);
    // Same constructor, Write method and Dispose/finalization used by CaptureSession.TrackWriter.
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
    using (var writer = new WaveFileWriter(stream, waveFormat)) writer.Write(payload, 0, payload.Length);
    using (var reader = new WaveFileReader(path))
    {
        Equal((long)payload.Length, reader.Length);
        var roundTrip = new byte[payload.Length];
        Equal(payload.Length, reader.Read(roundTrip, 0, roundTrip.Length));
        Check(payload.SequenceEqual(roundTrip), "NAudio source PCM changed during WAV write/read.");
    }
    var frames = samples.Length / format.Channels;
    return new(id, path, format, frames, [Packet(0, frames, 0)]);
}
static void Check(bool value, string failure) { if (!value) throw new Exception(failure); }
static void Equal<T>(T expected, T actual) where T : IEquatable<T>
    => Check(expected.Equals(actual), $"Expected {expected}, got {actual}.");
static void Near(float expected, float actual)
    => Check(Math.Abs(expected - actual) <= .0001f, $"Expected {expected}, got {actual}.");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static float[] ReadRendered(string path)
{
    var bytes = File.ReadAllBytes(path);
    Check(bytes.Length >= 44 && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)) == bytes.Length - 44, "Output WAV data size wrong.");
    var result = new float[(bytes.Length - 44) / 2];
    for (var i = 0; i < result.Length; i++) result[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2)) / 32768f;
    return result;
}
static void WriteSyntheticWave(string path, PcmFormat format, float[] samples)
{
    var size = samples.Length * format.BitsPerSample / 8;
    using var writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
    writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)(format.IsFloat ? 3 : 1)); writer.Write((short)format.Channels);
    writer.Write(format.SampleRate); writer.Write(format.SampleRate * format.BlockAlign);
    writer.Write((short)format.BlockAlign); writer.Write((short)format.BitsPerSample);
    writer.Write("data"u8); writer.Write(size);
    writer.Write(SyntheticPcmBytes(format, samples));
}
static byte[] SyntheticPcmBytes(PcmFormat format, float[] samples)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    foreach (var sample in samples)
    {
        if (format.IsFloat) writer.Write(sample);
        else if (format.BitsPerSample == 16) writer.Write((short)(sample * 32768));
        else if (format.BitsPerSample == 32) writer.Write((int)(sample * 2147483648d));
        else
        {
            var value = (int)(sample * 8388608);
            writer.Write((byte)value); writer.Write((byte)(value >> 8)); writer.Write((byte)(value >> 16));
        }
    }
    writer.Flush();
    return stream.ToArray();
}
static (int Offset, int Size) FindWaveData(byte[] bytes)
{
    for (var offset = 12; offset + 8 <= bytes.Length;)
    {
        var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x61746164) return (offset + 8, size);
        if (size < 0) throw new InvalidDataException("Invalid test WAV chunk length.");
        offset = checked(offset + 8 + size + (size & 1));
    }
    throw new InvalidDataException("Test WAV has no data chunk.");
}
internal sealed record TestResult(string Name, bool Passed, string? Error);
