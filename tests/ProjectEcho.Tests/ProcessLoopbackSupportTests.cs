using System.Runtime.InteropServices;
using ProjectEcho.Capture.Windows;

/// <summary>Version-policy and injected activation checks. No audio API, endpoint or microphone is opened.</summary>
internal static class ProcessLoopbackSupportTests
{
    internal static void Register(Action<string, Action> run)
    {
        run("T01/T02-SYNTH process-loopback build 19041 compatibility range is explicit and distinct from 20348 baseline", Versions);
        run("T01/T02-SYNTH unsupported process-loopback builds never invoke activation", Unsupported);
        run("T02/T12-SYNTH process-loopback compatibility activation is single-shot and preserves failure HRESULT without fallback", Activation);
    }

    private static void Versions()
    {
        foreach (var (version, expected) in new (Version, ProcessLoopbackAvailability)[]
        {
            (new(6, 3, 9600), ProcessLoopbackAvailability.Unavailable),
            (new(9, 0, 26200), ProcessLoopbackAvailability.Unavailable),
            (new(10, 0, 19040), ProcessLoopbackAvailability.Unavailable),
            (new(10, 0, 19041), ProcessLoopbackAvailability.CompatibilityAttempt),
            (new(10, 0, 19043, 2364), ProcessLoopbackAvailability.CompatibilityAttempt),
            (new(10, 0, 20347), ProcessLoopbackAvailability.CompatibilityAttempt),
            (new(10, 0, 20348), ProcessLoopbackAvailability.DocumentedBaseline),
            (new(10, 0, 22000), ProcessLoopbackAvailability.DocumentedBaseline),
            (new(10, 0, 26200), ProcessLoopbackAvailability.DocumentedBaseline),
            (new(11, 0, 0), ProcessLoopbackAvailability.DocumentedBaseline)
        })
        {
            var capability = ProcessLoopbackSupport.Evaluate(version);
            Need(capability.Availability == expected && capability.WindowsVersion == version,
                "Version boundary was misclassified or the reported OS version was replaced.");
            Need(capability.CanAttempt == (expected != ProcessLoopbackAvailability.Unavailable), "Eligibility disagrees with the classified range.");
            Need(capability.PreflightMessage.Contains($"build {version.Build}（运行时报告）"), "Preflight omitted the actual reported build.");
            if (expected == ProcessLoopbackAvailability.CompatibilityAttempt)
                Need(capability.PreflightMessage.Contains("19041–20347 兼容尝试") &&
                    capability.PreflightMessage.Contains("尚未在此目标机验证") && capability.PreflightMessage.Contains("点击开始后"),
                    "The compatibility range was presented as a verified capture capability or implicit activation.");
        }
        Need(!ProcessLoopbackSupport.Evaluate(new(10, 0, 26200), isWindows: false).CanAttempt,
            "A non-Windows platform was accepted from its numeric version alone.");
    }

    private static void Unsupported()
    {
        foreach (var capability in new[]
        {
            ProcessLoopbackSupport.Evaluate(new(10, 0, 19040)),
            ProcessLoopbackSupport.Evaluate(new(10, 0, 26200), isWindows: false)
        })
        {
            int called = 0;
            try
            {
                ProcessLoopbackSupport.Activate(capability, () => { called++; return new object(); });
                throw new InvalidDataException("Unsupported build reached activation.");
            }
            catch (PlatformNotSupportedException error)
            {
                Need(called == 0 && error.Message.Contains("19041"), "Unsupported activation was attempted or the minimum requirement was lost.");
            }
        }
    }

    private static void Activation()
    {
        foreach (int build in new[] { 19041, 19043, 20347, 20348, 26200 })
        {
            var capability = ProcessLoopbackSupport.Evaluate(new(10, 0, build));
            var opened = new object();
            int calls = 0;
            var actual = ProcessLoopbackSupport.Activate(capability, () => { calls++; return opened; });
            Need(calls == 1 && ReferenceEquals(actual, opened), "Successful activation retried or replaced the requested source.");

            const int hresult = unchecked((int)0x8889000A);
            var failure = new COMException("synthetic activation failure", hresult);
            calls = 0;
            try
            {
                ProcessLoopbackSupport.Activate<object>(capability, () => { calls++; throw failure; });
                throw new InvalidDataException("Failed activation returned success.");
            }
            catch (InvalidOperationException error)
            {
                Need(calls == 1 && ReferenceEquals(error.InnerException, failure) && error.HResult == hresult,
                    "Failure retried activation, lost its original exception or changed its HRESULT.");
                Need(error.Message.Contains("0x8889000A") && error.Message.Contains($"build {build}") &&
                    error.Message.Contains("未切换到端点录音"), "Activation failure omitted build, HRESULT or explicit source-scope outcome.");
                Need(error.Message.Contains("兼容尝试失败") == (build < 20348), "Compatibility and baseline activation failures became indistinguishable.");
            }
        }
    }

    private static void Need(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
