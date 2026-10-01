using System.Runtime.InteropServices;

namespace ProjectEcho.Capture.Windows;

/// <summary>
/// Scheduling-request evidence only, never evidence of audio continuity. ErrorCode and
/// ReleaseErrorCode contain a returned Win32 error, or the HRESULT of an interop-load failure.
/// Registered/PriorityApplied record successful calls; Released records a successful revert.
/// </summary>
public sealed record CaptureSchedulingDiagnostic(string TaskName, bool Registered, bool PriorityApplied,
    uint RegistrationThreadId, bool Released, int? ErrorCode, int? ReleaseErrorCode);

/// <summary>
/// One thread-affine MMCSS registration for a real capture thread. Synthetic callers must
/// skip TryEnter(), or inject NativeApi. No registry/service/process-priority changes occur.
/// </summary>
internal static class CaptureThreadScheduling
{
    internal const string TaskName = "Audio";
    internal const int NormalPriority = 0;

    internal static Lease TryEnter() => TryEnter(new WindowsNativeApi());

    internal static Lease TryEnter(NativeApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        uint threadId;
        try { threadId = api.GetCurrentThreadId(); }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        { return new Lease(api, 0, ex.HResult); }
        var lease = new Lease(api, threadId);
        uint index = 0;
        try
        {
            IntPtr handle = api.Register(TaskName, ref index, out int error);
            if (handle == IntPtr.Zero) { lease.RecordError(error); return lease; }
            lease.AcceptRegistration(handle);
            if (api.SetPriority(handle, NormalPriority, out error)) lease.AcceptPriority();
            else
            {
                lease.RecordError(error);
                // A partial request must not be reported as Audio/NORMAL success.
                // Revert immediately on this same thread; final Dispose retries once only if needed.
                lease.RevertAfterFailedPriority();
            }
        }
        catch (Exception ex) when (IsInteropUnavailable(ex))
        {
            lease.RecordError(ex.HResult);
            lease.RevertAfterFailedPriority();
        }
        catch
        {
            // Unexpected managed errors still release any acquired registration before propagating.
            lease.RevertAfterFailedPriority();
            throw;
        }
        return lease;
    }

    private static bool IsInteropUnavailable(Exception exception) => exception is
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    internal sealed class Lease : IDisposable
    {
        private readonly NativeApi api;
        private readonly uint ownerThreadId;
        private IntPtr handle;
        private bool disposed;
        private CaptureSchedulingDiagnostic diagnostic;

        internal Lease(NativeApi api, uint threadId, int? error = null)
        {
            this.api = api; ownerThreadId = threadId;
            diagnostic = new(TaskName, false, false, threadId, false, error, null);
        }

        public CaptureSchedulingDiagnostic Diagnostic => Volatile.Read(ref diagnostic);
        public CaptureSchedulingDiagnostic Snapshot() => Diagnostic;

        internal void RecordError(int error) => Update(Diagnostic with { ErrorCode = error });
        internal void AcceptRegistration(IntPtr value)
        {
            handle = value; Update(Diagnostic with { Registered = true });
        }
        internal void AcceptPriority() => Update(Diagnostic with { PriorityApplied = true });

        internal void RevertAfterFailedPriority() => Revert();

        public void Dispose()
        {
            if (disposed) return;
            if (handle != IntPtr.Zero && api.GetCurrentThreadId() != ownerThreadId)
                throw new InvalidOperationException("MMCSS 注册必须由原采集线程释放；未在其他线程调用 Revert。");
            disposed = true;
            Revert();
        }

        private void Revert()
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                if (api.Revert(handle, out int error))
                {
                    handle = IntPtr.Zero;
                    Update(Diagnostic with { Released = true });
                }
                else Update(Diagnostic with { ReleaseErrorCode = Diagnostic.ReleaseErrorCode ?? error });
            }
            catch (Exception ex) when (IsInteropUnavailable(ex))
            { Update(Diagnostic with { ReleaseErrorCode = Diagnostic.ReleaseErrorCode ?? ex.HResult }); }
        }

        private void Update(CaptureSchedulingDiagnostic value) => Volatile.Write(ref diagnostic, value);
        // Intentionally no finalizer/SafeHandle: Windows requires Revert on the registration thread.
    }

    internal abstract class NativeApi
    {
        internal abstract uint GetCurrentThreadId();
        internal abstract IntPtr Register(string taskName, ref uint taskIndex, out int errorCode);
        internal abstract bool SetPriority(IntPtr handle, int priority, out int errorCode);
        internal abstract bool Revert(IntPtr handle, out int errorCode);
    }

    private sealed class WindowsNativeApi : NativeApi
    {
        internal override uint GetCurrentThreadId() => NativeGetCurrentThreadId();
        internal override IntPtr Register(string taskName, ref uint taskIndex, out int errorCode)
        {
            IntPtr result = AvSetMmThreadCharacteristics(taskName, ref taskIndex);
            errorCode = result == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
            return result;
        }
        internal override bool SetPriority(IntPtr handle, int priority, out int errorCode)
        {
            bool result = AvSetMmThreadPriority(handle, priority);
            errorCode = result ? 0 : Marshal.GetLastPInvokeError();
            return result;
        }
        internal override bool Revert(IntPtr handle, out int errorCode)
        {
            bool result = AvRevertMmThreadCharacteristics(handle);
            errorCode = result ? 0 : Marshal.GetLastPInvokeError();
            return result;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint NativeGetCurrentThreadId();

        [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

        [DllImport("avrt.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
    }
}
