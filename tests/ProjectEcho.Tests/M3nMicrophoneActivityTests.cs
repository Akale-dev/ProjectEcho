using System.Buffers.Binary;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

internal static partial class M3mAutoStopTests
{
    internal static void RegisterM3n(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3n-microphone-activity");
        run("T244-SYNTH M3N reproduces M3M low-noise indefinite timer reset", ReproduceNoise);
        foreach (var format in new[] { new PcmFormat(8000, 1, 16, false, 2), new(48000, 1, 24, false, 3),
            new(44100, 1, 32, false, 4), new(48000, 2, 32, true, 8) })
            run($"T244-SYNTH M3N microphone RMS preserves quiet voice and rejects noise/DC {format.SampleRate}-{format.BitsPerSample}-{format.IsFloat}", () => MicrophoneFormats(format));
        run("T244-SYNTH M3N microphone detector is packet-size independent with stereo antiphase", PacketSizes);
        run("T244-SYNTH M3N inactive elapsed prefix cannot reuse stale microphone activity", MicrophoneGap);
        run("T244/T240-SYNTH M3N mic-on dual digital silence ends and saves", () => QuietMicrophone(Path.Combine(root, "silence"), false));
        run("T244/T240-SYNTH M3N mic-on low-noise and DC ends with original PCM unchanged", () => QuietMicrophone(Path.Combine(root, "noise"), true));
        run("T244-SYNTH M3N actual quiet microphone voice delays automatic end", () => QuietVoice(Path.Combine(root, "voice")));
        run("T249-SYNTH M3N enabling during recording starts a full new interval", () => LiveEnable(Path.Combine(root, "live-enable")));
        run("T249-SYNTH M3N disabling before expiry keeps both sources recording", () => LiveDisable(Path.Combine(root, "live-disable")));
        run("T249-SYNTH M3N interval changes are acknowledged and reset the timer", () => LiveInterval(Path.Combine(root, "live-interval")));
        run("T249-SYNTH M3N enable while paused waits until resume", () => LivePaused(Path.Combine(root, "live-paused")));
        run("T249-SYNTH M3N invalid and conflicting settings leave prior policy intact", () => LiveInvalid(Path.Combine(root, "live-invalid")));
        run("T249-SYNTH M3N settings cannot cancel an already started automatic save", () => LiveSaving(Path.Combine(root, "live-saving")));
    }
    private static void ReproduceNoise()
    {
        var prior = new RecordingAutoStopMonitor(new(true, 1)); prior.Reset(Epoch);
        for (int second = 1; second <= 120; second++) prior.Observe(Epoch + (ulong)second * 10_000_000, .0007f);
        Need(!prior.IsDue(Epoch + 1_200_000_000, false, false), "Old low-peak decision did not reproduce.");
        var detector = new MicrophoneSoundActivity(new(8000, 1, 16, false, 2));
        Need(detector.Observe(Pcm(new(8000, 1, 16, false, 2), .0007, 0), Epoch) == 0, "Low microphone noise is still classified as activity.");
    }
    private static void LiveEnable(string path)
    {
        using var fixture = new Fixture(path, enabled: false);
        for (int i = 0; i < 70; i++) fixture.Tick();
        var ack = Await(fixture.Session.ControlAsync("enable", "auto_stop_settings", automatic: new(true, 1)));
        Need(ack.AutoStopSettings == new RecordingAutoStopOptions(true, 1) && !ack.Paused, "Live enable was not acknowledged.");
        for (int i = 0; i < 60; i++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 130_000_000 && result.AutoStop!.SilenceMinutes == 1,
            "Enable inherited pre-enable inactivity.");
        var message = new ProjectEcho.CaptureHost.HostCommand(1, "synthetic", "enable", "auto_stop_settings", AutoStopSettings: new(true, 1));
        Need(System.Text.Json.JsonSerializer.Deserialize<ProjectEcho.CaptureHost.HostCommand>(System.Text.Json.JsonSerializer.Serialize(message))!.AutoStopSettings == ack.AutoStopSettings,
            "Live settings were lost in IPC serialization.");
    }
    private static void LiveDisable(string path)
    {
        using var fixture = new Fixture(path, mic: true);
        for (int i = 0; i < 59; i++) fixture.Tick();
        var ack = Await(fixture.Session.ControlAsync("disable", "auto_stop_settings", automatic: new(false, 1)));
        Need(ack.AutoStopSettings is { Enabled: false } && ack.MicrophoneEnabled, "Disabling changed microphone consent.");
        for (int i = 0; i < 10; i++) fixture.Tick();
        Need(!fixture.Session.Completion.IsCompleted && fixture.Factory.Sources.All(source => source.Stops == 0), "Disabling ended or restarted a source.");
        Await(fixture.Session.ControlAsync("reenable", "auto_stop_settings", automatic: new(true, 1)));
        for (int i = 0; i < 60; i++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 129_000_000, "Re-enable did not restart the interval.");
    }
    private static void LiveInterval(string path)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 59; i++) fixture.Tick();
        var five = Await(fixture.Session.ControlAsync("five", "auto_stop_settings", automatic: new(true, 5)));
        Need(five.AutoStopSettings?.SilenceMinutes == 5, "Changed minutes not confirmed.");
        for (int i = 0; i < 60; i++) fixture.Tick();
        Need(!fixture.Session.Completion.IsCompleted, "Old threshold remained active.");
        Await(fixture.Session.ControlAsync("one", "auto_stop_settings", automatic: new(true, 1)));
        for (int i = 0; i < 60; i++) fixture.Tick();
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 179_000_000 && result.AutoStop?.SilenceMinutes == 1, "Changed timer did not reset or persist.");
        string events = File.ReadAllText(Path.Combine(path, "journal", "events.jsonl"));
        Need(events.Contains("auto_stop_settings") && events.Contains("\"SilenceMinutes\":5"), "Setting changes were not retained in the journal.");
    }
    private static void LivePaused(string path)
    {
        using var fixture = new Fixture(path, enabled: false);
        fixture.Tick(); Await(fixture.Session.ControlAsync("pause", "pause"));
        var ack = Await(fixture.Session.ControlAsync("enable-paused", "auto_stop_settings", automatic: new(true, 1)));
        fixture.Advance(3600); Await(fixture.Session.ControlAsync("bookmark", "bookmark"));
        Need(ack.Paused && !fixture.Session.Completion.IsCompleted, "Enabling while paused started a running timer.");
        Await(fixture.Session.ControlAsync("resume", "resume"));
        for (int i = 0; i < 60; i++) fixture.Tick();
        Need(Await(fixture.Session.Completion).DurationUs == 61_000_000, "Paused enable polluted recording time.");
    }
    private static void LiveInvalid(string path)
    {
        using var fixture = new Fixture(path);
        bool rejected = false;
        try { Await(fixture.Session.ControlAsync("bad", "auto_stop_settings", automatic: new(true, 2))); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Need(rejected, "Invalid live threshold accepted.");
        var first = fixture.Session.ControlAsync("fixed", "auto_stop_settings", automatic: new(true, 1)); Await(first);
        Need(ReferenceEquals(first, fixture.Session.ControlAsync("fixed", "auto_stop_settings", automatic: new(true, 1))), "Duplicate settings request applied twice.");
        rejected = false;
        try { Await(fixture.Session.ControlAsync("fixed", "auto_stop_settings", automatic: new(false, 1))); }
        catch (InvalidOperationException) { rejected = true; }
        Need(rejected, "Conflicting request ID changed the policy.");
        for (int i = 0; i < 60; i++) fixture.Tick();
        Need(Await(fixture.Session.Completion).AutoStop is not null, "Rejected control disabled automatic stopping.");
    }
    private static void LiveSaving(string path)
    {
        using var fixture = new Fixture(path);
        for (int i = 0; i < 59; i++) fixture.Tick();
        var source = fixture.Factory.Latest("remote"); source.BlockStop = true;
        try
        {
            source.Push(fixture.RawNow, false); fixture.Advance(1); Need(source.StopEntered.Wait(Timeout), "Save gate missing.");
            bool rejected = false;
            try { Await(fixture.Session.ControlAsync("too-late", "auto_stop_settings", automatic: new(false, 1))); }
            catch (InvalidOperationException) { rejected = true; }
            Need(rejected, "A late uncheck reopened a closing recording.");
            source.AllowStop.Set(); Need(Await(fixture.Session.Completion).StorageComplete, "Late toggle damaged save.");
        }
        finally { source.AllowStop.Set(); }
    }
    private static void MicrophoneFormats(PcmFormat format)
    {
        foreach (double dc in new[] { 0d, .025, -.05 })
        {
            var detector = new MicrophoneSoundActivity(format);
            Need(detector.Observe(Pcm(format, .0007, dc), Epoch) == 0, "Noise/DC falsely reset microphone inactivity.");
        }
        Need(new MicrophoneSoundActivity(format).Observe(Pcm(format, .006, .025), Epoch) > 0,
            "A quiet -47 dBFS RMS tone was discarded as noise.");
    }
    private static void PacketSizes()
    {
        var format = new PcmFormat(48000, 2, 32, true, 8);
        foreach (double amplitude in new[] { .0007, .01 })
        foreach (int size in new[] { 1, 48, 127, 480, 1373, 48000 })
        {
            byte[] bytes = Pcm(format, amplitude, .02); var detector = new MicrophoneSoundActivity(format); int active = 0;
            for (int frame = 0; frame < 48000; frame += size)
            {
                int count = Math.Min(size, 48000 - frame);
                int found = detector.Observe(bytes.AsSpan(frame * 8, count * 8), Epoch + (ulong)frame * 10_000_000 / 48000);
                if (found > 0) active = frame + found;
            }
            Need(amplitude < .001 ? active == 0 : active > 47000, "Packet boundaries or opposite stereo phase changed activity.");
        }
    }
    private static void MicrophoneGap()
    {
        var format = new PcmFormat(8000, 1, 16, false, 2); var detector = new MicrophoneSoundActivity(format);
        byte[] voice = Pcm(format, .1, 0);
        _ = detector.Observe(voice.AsSpan(0, 14), Epoch);
        Need(detector.Observe(new byte[160], Epoch + 10_000_000) == 0, "A prior packet gap carried stale energy into silence.");
        Need(detector.Observe([], Epoch + 20_000_000) == 0, "No PCM manufactured activity.");
    }
    private static void QuietMicrophone(string path, bool noise)
    {
        using var fixture = new Fixture(path, mic: true);
        short Sample(int frame) => noise ? (short)(655 + 23 * Math.Sin(frame * 2 * Math.PI * 200 / 8000)) : (short)0;
        for (int i = 0; i < 60; i++) fixture.Tick(microphoneSamples: Sample);
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.AutoStop is { Reason: "no_audio_activity", MicrophonePacketMissing: false } &&
            result.DurationUs == 60_000_000 && Frames(path, "mic") == 480_000 && Frames(path, "remote") == 480_000,
            "Mic-on silence did not end with both complete original tracks.");
        var files = Directory.EnumerateFiles(Path.Combine(path, "sources", "mic"), "*.wav").Order(StringComparer.Ordinal).ToArray();
        long frameIndex = 0;
        foreach (string file in files)
        {
            using var reader = new WaveFileReader(file); var bytes = new byte[reader.Length];
            reader.ReadExactly(bytes);
            for (int offset = 0; offset < bytes.Length; offset += 2, frameIndex++)
                Need(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset)) == Sample((int)(frameIndex % 8000)), "Detector altered persisted microphone PCM.");
        }
    }
    private static void QuietVoice(string path)
    {
        using var fixture = new Fixture(path, mic: true);
        for (int i = 1; i <= 60; i++) fixture.Tick(microphoneSamples: frame => (short)((i == 59 ? 197 : 23) * Math.Sin(frame * 2 * Math.PI * 200 / 8000)));
        Need(!fixture.Session.Completion.IsCompleted, "Quiet voice did not postpone automatic stopping.");
        for (int i = 61; i <= 119; i++) fixture.Tick(microphoneSamples: frame => (short)(23 * Math.Sin(frame * 2 * Math.PI * 200 / 8000)));
        var result = Await(fixture.Session.Completion);
        Need(result.StorageComplete && result.DurationUs == 119_000_000, "Noise after speech prevented the subsequent end.");
    }
    private static byte[] Pcm(PcmFormat format, double amplitude, double dc)
    {
        int width = format.BitsPerSample / 8; var pcm = new byte[format.SampleRate * format.BlockAlign];
        for (int frame = 0; frame < format.SampleRate; frame++)
        for (int channel = 0; channel < format.Channels; channel++)
        {
            double value = dc + amplitude * Math.Sin(frame * 2 * Math.PI * 200 / format.SampleRate) * (channel == 0 ? 1 : -1);
            var sample = pcm.AsSpan(frame * format.BlockAlign + channel * width, width);
            if (format.IsFloat) BinaryPrimitives.WriteSingleLittleEndian(sample, (float)value);
            else if (width == 2) BinaryPrimitives.WriteInt16LittleEndian(sample, (short)(value * 32768));
            else if (width == 4) BinaryPrimitives.WriteInt32LittleEndian(sample, (int)(value * 2147483648));
            else { int number = (int)(value * 8388608); sample[0] = (byte)number; sample[1] = (byte)(number >> 8); sample[2] = (byte)(number >> 16); }
        }
        return pcm;
    }
}
