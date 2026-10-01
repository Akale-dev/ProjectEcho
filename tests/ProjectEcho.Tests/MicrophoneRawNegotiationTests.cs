using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ProjectEcho.Capture.Windows;

internal static class MicrophoneRawNegotiationTests
{
    private static readonly WaveFormat Device16 = new(16000, 16, 1);
    private static readonly WaveFormat Mix48 = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private static readonly WaveFormat Raw16 = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
    private const int UnsupportedRaw = unchecked((int)0x88890027), UnsupportedFormat = unchecked((int)0x88890008);

    internal static void Register(Action<string, Action> run)
    {
        run("T61/T78-SYNTH RAW properties precede fresh-client format read and initialization; no capture is started", PreferredRaw);
        run("T61-SYNTH same-rate microphone keeps existing shared format and one client", SameRate);
        run("T61/T78-SYNTH unsupported RAW configure format or initialization disposes and retries only the same factory", Fallback);
        run("T07/T61-SYNTH permissions device loss and unexpected RAW errors cannot trigger fallback", FatalFailures);
        run("T57/T61-SYNTH preflight RAW format budgets actual float bytes without initializing", Preflight);
    }

    private static void PreferredRaw()
    {
        var fake = new Scenario();
        using (var choice = fake.Select())
        {
            Check(choice.ProcessingMode == "shared-raw" && choice.Format == Raw16 && choice.ConversionFlags == 0 &&
                choice.DefaultMixRate == 48000 && choice.DeviceReportedRate == 16000, "RAW selection changed format/rate or retained SRC.");
            Check(fake.Events.SequenceEqual(new[] { "create1", "mix1", "dispose1", "create2", "raw2", "mix2", "init2" }), "RAW lifecycle/order differs.");
            Check(fake.Clients[0].Disposed == 1 && fake.Clients[1].Disposed == 0 && choice.FallbackReason is null, "RAW ownership lost.");
        }
        Check(fake.Clients.All(c => c.Disposed == 1), "Successful RAW client was leaked or double disposed.");
    }

    private static void SameRate()
    {
        var fake = new Scenario { Device = new WaveFormat(48000, 16, 2) };
        using var choice = fake.Select();
        Check(choice.Format == Mix48 && choice.ProcessingMode == "shared-default" && choice.ConversionFlags == 0 &&
            fake.Events.SequenceEqual(new[] { "create1", "mix1", "init1" }), "Same-rate device processing changed.");
    }

    private static void Fallback()
    {
        foreach (string phase in new[] { "raw", "mix", "init", "rate", "channels" })
        {
            var fake = new Scenario { FailRawPhase = phase };
            using (var choice = fake.Select())
            {
                Check(choice.ProcessingMode == "shared-converted" && choice.Format == Device16 && choice.FallbackReason is not null,
                    "Unsupported RAW did not retain the same-device compatible format.");
                Check(choice.ConversionFlags == (AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality), "Fallback dropped quality flags.");
                Check(fake.Clients.Count == 3 && fake.Clients[0].Disposed == 1 && fake.Clients[1].Disposed == 1 && fake.Clients[2].Disposed == 0,
                    "Failed RAW/default query client leaked or returned client was disposed.");
                Check(fake.Events[^3..].SequenceEqual(new[] { "create3", "mix3", "init3" }), "Fallback reused the RAW client or its cached format.");
            }
            Check(fake.Clients.All(c => c.Disposed == 1), "Fallback disposal mismatch.");
        }
    }

    private static void FatalFailures()
    {
        foreach (int code in new[] { unchecked((int)0x80070005), unchecked((int)0x88890004), unchecked((int)0x88890026),
            unchecked((int)0x8889000A), unchecked((int)0x80070057), unchecked((int)0x80004005) })
            foreach (string phase in new[] { "raw", "mix", "init" })
            {
                var fake = new Scenario { FailRawPhase = phase, FailureCode = code };
                try { fake.Select().Dispose(); throw new InvalidDataException("Fatal RAW error was converted to fallback."); }
                catch (COMException ex) when (ex.HResult == code) { }
                Check(fake.Clients.Count == 2 && fake.Clients.All(c => c.Disposed == 1), "Fatal RAW failure opened another client or leaked one.");
            }
        var legacyFail = new Scenario { FailRawPhase = "init", FailFallback = true };
        try { legacyFail.Select().Dispose(); throw new InvalidDataException("Failed compatible Initialize was accepted."); }
        catch (COMException ex) when (ex.HResult == UnsupportedFormat) { }
        Check(legacyFail.Clients.Count == 3 && legacyFail.Clients.All(c => c.Disposed == 1), "Failed fallback client leaked.");
        Check(!MicrophoneFormatPolicy.CanFallbackFromRaw(new InvalidOperationException()) &&
            !MicrophoneFormatPolicy.CanFallbackFromRaw(new NotSupportedException()), "Unclassified errors permitted fallback.");
    }

    private static void Preflight()
    {
        var fake = new Scenario();
        using var choice = fake.Select(initialize: false);
        Check(choice.Format.AverageBytesPerSecond == 64000 && choice.Format.BlockAlign == 4 &&
            fake.Events.All(e => !e.StartsWith("init", StringComparison.Ordinal)), "Preflight initialized capture or budgeted old 16-bit bytes.");
    }

    private sealed class Client(int id, List<string> events) : IDisposable
    {
        internal int Id = id, Disposed; internal bool Raw;
        public void Dispose() { Disposed++; events.Add("dispose" + Id); }
    }

    private sealed class Scenario
    {
        internal readonly List<Client> Clients = [];
        internal readonly List<string> Events = [];
        internal WaveFormat Device = Device16;
        internal string? FailRawPhase;
        internal int FailureCode = UnsupportedRaw;
        internal bool FailFallback;
        internal MicrophoneClientSelection<Client> Select(bool initialize = true) => MicrophoneFormatPolicy.OpenClient(
            Create, ReadMix, ConfigureRaw, Device, initialize ? Initialize : null);
        private Client Create() { var c = new Client(Clients.Count + 1, Events); Clients.Add(c); Events.Add("create" + c.Id); return c; }
        private void ConfigureRaw(Client c) { Events.Add("raw" + c.Id); Fail("raw"); c.Raw = true; }
        private WaveFormat ReadMix(Client c)
        {
            Events.Add("mix" + c.Id); Check(c.Disposed == 0, "Disposed client queried.");
            if (!c.Raw) return Mix48;
            Fail("mix");
            return FailRawPhase switch { "rate" => Mix48, "channels" => WaveFormat.CreateIeeeFloatWaveFormat(16000, 4), _ => Raw16 };
        }
        private void Initialize(Client c, WaveFormat f, AudioClientStreamFlags flags)
        {
            Events.Add("init" + c.Id); Check(c.Disposed == 0, "Disposed client initialized.");
            if (c.Raw) Fail("init");
            if (c.Id == 3 && FailFallback) throw new COMException("synthetic incompatible fallback", UnsupportedFormat);
        }
        private void Fail(string phase) { if (FailRawPhase == phase) throw new COMException("synthetic " + phase, FailureCode); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
