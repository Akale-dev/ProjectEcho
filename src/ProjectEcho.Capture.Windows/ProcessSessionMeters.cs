using System.Runtime.InteropServices;

namespace ProjectEcho.Capture.Windows;

internal sealed record ProcessSessionMeterResult(float Peak, int MatchingSessions, bool AmbiguousScope);

/// <summary>
/// Uses the Windows Core Audio COM contract, preserving the positive GetProcessId
/// HRESULT that locked NAudio 3.1.0's high-level GetProcessID property discards.
/// A multi-process session's peak cannot be attributed safely to one selected
/// process tree, so it is never read. This class creates no audio/capture client.
/// </summary>
internal sealed class ProcessSessionMeters : IDisposable
{
    private readonly OwnedCom<IDeviceEnumeratorNative> devices;
    internal ProcessSessionMeters()
    {
        var classId = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var interfaceId = typeof(IDeviceEnumeratorNative).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, IntPtr.Zero, 1, ref interfaceId, out var pointer));
        devices = OwnedCom<IDeviceEnumeratorNative>.Take(pointer);
    }

    internal ProcessSessionMeterResult Read(string endpointId, Func<uint, bool> inSelectedTree)
    {
        Marshal.ThrowExceptionForHR(devices.Value.GetDevice(endpointId, out var devicePointer));
        using var device = OwnedCom<IDeviceNative>.Take(devicePointer);
        var managerId = typeof(ISessionManagerNative).GUID;
        Marshal.ThrowExceptionForHR(device.Value.Activate(ref managerId, 23, IntPtr.Zero, out var managerPointer));
        using var manager = OwnedCom<ISessionManagerNative>.Take(managerPointer);
        Marshal.ThrowExceptionForHR(manager.Value.GetSessionEnumerator(out var sessionsPointer));
        using var sessions = OwnedCom<ISessionEnumeratorNative>.Take(sessionsPointer);
        Marshal.ThrowExceptionForHR(sessions.Value.GetCount(out int count));
        float peak = 0; int matching = 0;
        for (int i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(sessions.Value.GetSession(i, out var sessionPointer));
            using var session = OwnedCom<ISessionControlNative>.Take(sessionPointer);
            Marshal.ThrowExceptionForHR(session.Value.GetState(out var state));
            if (state != 1) continue; // AudioSessionStateActive
            int system = session.Value.IsSystemSoundsSession();
            Marshal.ThrowExceptionForHR(system);
            if (system == 0) continue;
            int processStatus = session.Value.GetProcessId(out uint pid);
            Marshal.ThrowExceptionForHR(processStatus);
            if (pid == 0 || !inSelectedTree(pid)) continue;
            // AUDCLNT_S_NO_SINGLE_PROCESS = 0x0889000D: pid is only the creator.
            // Unknown positive success statuses are equally unproven in scope.
            if (!HasSingleProcess(processStatus)) return new(0, matching, true);
            var meter = (IMeterNative)session.Instance;
            Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float value));
            if (AudioLevelPreviewReading.FromPeak(value).State == AudioLevelPreviewState.Unavailable)
                throw new InvalidDataException("Windows session meter returned an invalid peak.");
            peak = Math.Max(peak, value); matching++;
        }
        return new(peak, matching, false);
    }

    internal static bool HasSingleProcess(int getProcessIdHResult) => getProcessIdHResult == 0;
    public void Dispose() => devices.Dispose();

    /// <summary>
    /// A unique RCW belongs only to this scope; release the original native ref
    /// after wrapping, then all RCW/QI references on this owner MTA. No reflection
    /// over NAudio private members or process-global COM identity cache is used.
    /// </summary>
    private sealed class OwnedCom<T> : IDisposable where T : class
    {
        internal object Instance { get; }
        internal T Value { get; }
        private OwnedCom(object instance) { Instance = instance; Value = (T)instance; }
        internal static OwnedCom<T> Take(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw new InvalidDataException("Windows returned an empty COM interface.");
            object? instance = null;
            try
            {
                instance = Marshal.GetUniqueObjectForIUnknown(pointer);
                return new(instance);
            }
            catch { if (instance is not null) Marshal.FinalReleaseComObject(instance); throw; }
            finally { Marshal.Release(pointer); }
        }
        public void Dispose() => Marshal.FinalReleaseComObject(Instance);
    }

    // Exact IUnknown vtable order/GUIDs from Windows mmdeviceapi.h,
    // audiopolicy.h and endpointvolume.h; unused slots retain their ABI shapes.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumeratorNative
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int state, out IntPtr result);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IntPtr result);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr result);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceNative
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr activation, out IntPtr result);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr result);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionManagerNative
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid session, uint flags, out IntPtr result);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr result);
        [PreserveSig] int GetSessionEnumerator(out IntPtr result);
        [PreserveSig] int RegisterSessionNotification(IntPtr callback);
        [PreserveSig] int UnregisterSessionNotification(IntPtr callback);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string session, IntPtr callback);
        [PreserveSig] int UnregisterDuckNotification(IntPtr callback);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionEnumeratorNative
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IntPtr result);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISessionControlNative
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr callback);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr callback);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMeterNative
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, IntPtr values);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
        ref Guid interfaceId, out IntPtr result);
}
