using ProjectEcho.Capture.Windows;

internal static class CaptureThreadSchedulingTests
{
    internal static void Register(Action<string, Action> run)
    {
        run("T79-SYNTH MMCSS Audio NORMAL registration uses its calling thread and releases once", Success);
        run("T79-SYNTH MMCSS registration failure is visible and never sets priority or releases an absent handle", RegistrationFailure);
        run("T79-SYNTH MMCSS priority failure immediately reverts and preserves both errors", PriorityFailure);
        run("T79-SYNTH MMCSS final release failure is visible without repeated disposal calls", ReleaseFailure);
        run("T79-SYNTH MMCSS wrong-thread disposal refuses native release and allows owning-thread cleanup", WrongThread);
        run("T79-SYNTH MMCSS unavailable entrypoints return explicit diagnostics with no native resources leaked", Unavailable);
    }

    private static void Success()
    {
        var api = new FakeApi();
        var lease = CaptureThreadScheduling.TryEnter(api);
        var before = lease.Snapshot();
        Check(before.TaskName == "Audio" && before.Registered && before.PriorityApplied &&
            before.RegistrationThreadId == 41 && !before.Released && before.ErrorCode is null && before.ReleaseErrorCode is null,
            "Successful registration diagnostics differ.");
        Check(api.Events.SequenceEqual(new[] { "register:Audio:0", "priority:0" }), "Wrong task/index/relative priority requested.");
        lease.Dispose(); lease.Dispose();
        Check(lease.Diagnostic.Released && !before.Released && api.Events.Count(x => x == "revert") == 1,
            "Current snapshot or idempotent release is incorrect.");
    }

    private static void RegistrationFailure()
    {
        var api = new FakeApi { RegistrationError = 5 };
        using var lease = CaptureThreadScheduling.TryEnter(api);
        Check(!lease.Diagnostic.Registered && !lease.Diagnostic.PriorityApplied && !lease.Diagnostic.Released &&
            lease.Diagnostic.ErrorCode == 5 && api.Events.SequenceEqual(new[] { "register:Audio:0" }),
            "Registration failure was hidden or an absent handle was used.");
    }

    private static void PriorityFailure()
    {
        var api = new FakeApi { PriorityError = 87 };
        var lease = CaptureThreadScheduling.TryEnter(api);
        Check(lease.Diagnostic.Registered && !lease.Diagnostic.PriorityApplied && lease.Diagnostic.Released &&
            lease.Diagnostic.ErrorCode == 87 && api.Events[^1] == "revert", "Priority failure did not immediately revert.");
        lease.Dispose(); Check(api.Events.Count(x => x == "revert") == 1, "Successful rollback was repeated.");

        var rollback = new FakeApi { PriorityError = 5, ReleaseError = 6 };
        var retry = CaptureThreadScheduling.TryEnter(rollback);
        Check(retry.Diagnostic.Registered && !retry.Diagnostic.PriorityApplied && !retry.Diagnostic.Released &&
            retry.Diagnostic.ErrorCode == 5 && retry.Diagnostic.ReleaseErrorCode == 6, "Rollback failure lost an error.");
        rollback.ReleaseError = null; retry.Dispose(); retry.Dispose();
        Check(retry.Diagnostic.Released && retry.Diagnostic.ReleaseErrorCode == 6 &&
            rollback.Events.Count(x => x == "revert") == 2, "Final owning-thread cleanup or failure evidence is incorrect.");
    }

    private static void ReleaseFailure()
    {
        var api = new FakeApi { ReleaseError = 5 };
        var lease = CaptureThreadScheduling.TryEnter(api); lease.Dispose(); lease.Dispose();
        Check(lease.Diagnostic.Registered && lease.Diagnostic.PriorityApplied && !lease.Diagnostic.Released &&
            lease.Diagnostic.ReleaseErrorCode == 5 && api.Events.Count(x => x == "revert") == 1,
            "Release failure was treated as success or repeatedly retried.");
    }

    private static void WrongThread()
    {
        var api = new FakeApi(); var lease = CaptureThreadScheduling.TryEnter(api);
        api.ThreadId = 42;
        try { lease.Dispose(); throw new InvalidDataException("Wrong-thread release was accepted."); }
        catch (InvalidOperationException) { }
        Check(!lease.Diagnostic.Released && api.Events.All(x => x != "revert"), "Native Revert ran on another thread.");
        api.ThreadId = 41; lease.Dispose(); Check(lease.Diagnostic.Released, "Owning thread could not release after rejection.");
    }

    private static void Unavailable()
    {
        var missing = new DllNotFoundException("synthetic missing avrt");
        var api = new FakeApi { RegistrationException = missing };
        using var absent = CaptureThreadScheduling.TryEnter(api);
        Check(!absent.Diagnostic.Registered && absent.Diagnostic.ErrorCode == missing.HResult && api.Events.Count == 1,
            "Missing AVRT library was hidden or retried.");
        var entry = new EntryPointNotFoundException("synthetic missing priority");
        var partial = new FakeApi { PriorityException = entry };
        using var rolledBack = CaptureThreadScheduling.TryEnter(partial);
        Check(rolledBack.Diagnostic.Registered && !rolledBack.Diagnostic.PriorityApplied && rolledBack.Diagnostic.Released &&
            rolledBack.Diagnostic.ErrorCode == entry.HResult, "Interop priority failure leaked registration.");
        var releaseEntry = new EntryPointNotFoundException("synthetic missing revert");
        var releaseApi = new FakeApi { ReleaseException = releaseEntry };
        var failedRelease = CaptureThreadScheduling.TryEnter(releaseApi); failedRelease.Dispose();
        Check(!failedRelease.Diagnostic.Released && failedRelease.Diagnostic.ReleaseErrorCode == releaseEntry.HResult,
            "Unavailable Revert was reported as released.");
        var unexpected = new FakeApi { PriorityException = new InvalidOperationException("synthetic unexpected failure") };
        try { CaptureThreadScheduling.TryEnter(unexpected); throw new InvalidDataException("Unexpected error was hidden."); }
        catch (InvalidOperationException) { }
        Check(unexpected.Events[^1] == "revert", "Unexpected priority error leaked the acquired registration.");
    }

    private sealed class FakeApi : CaptureThreadScheduling.NativeApi
    {
        internal readonly List<string> Events = [];
        internal uint ThreadId = 41;
        internal int? RegistrationError, PriorityError, ReleaseError;
        internal Exception? RegistrationException, PriorityException, ReleaseException;
        private static readonly IntPtr Handle = new(73);
        internal override uint GetCurrentThreadId() => ThreadId;
        internal override IntPtr Register(string taskName, ref uint taskIndex, out int errorCode)
        {
            Events.Add($"register:{taskName}:{taskIndex}");
            if (RegistrationException is { } exception) throw exception;
            errorCode = RegistrationError ?? 0; taskIndex = 99;
            return RegistrationError is null ? Handle : IntPtr.Zero;
        }
        internal override bool SetPriority(IntPtr handle, int priority, out int errorCode)
        {
            Check(handle == Handle, "Unknown priority handle."); Events.Add("priority:" + priority);
            if (PriorityException is { } exception) throw exception;
            errorCode = PriorityError ?? 0; return PriorityError is null;
        }
        internal override bool Revert(IntPtr handle, out int errorCode)
        {
            Check(handle == Handle, "Unknown release handle."); Events.Add("revert");
            if (ReleaseException is { } exception) throw exception;
            errorCode = ReleaseError ?? 0; return ReleaseError is null;
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
