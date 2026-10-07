using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProjectEcho.Capture.Windows;

public sealed record RecordingAutoStopOptions(bool Enabled = false, int SilenceMinutes = 5)
{
    internal void Validate()
    {
        if (SilenceMinutes is not (1 or 5 or 10))
            throw new ArgumentOutOfRangeException(nameof(SilenceMinutes), "自动结束时长只能为1、5或10分钟。");
    }
}

// Inactivity is a user-selected stop condition, not proof of captured silence or completeness.
public sealed record RecordingAutoStopInfo(string Reason, int SilenceMinutes, long TriggeredSessionUs,
    bool RemotePacketMissing, bool MicrophonePacketMissing);

internal sealed class RecordingAutoStopMonitor(RecordingAutoStopOptions options)
{
    internal const float AudiblePeak = .0001f; // Same conservative -80 dBFS floor as recording health.
    private ulong lastActivity;
    internal void Reset(ulong now) => lastActivity = now;
    internal void Observe(ulong packetEnd, float peak)
    {
        if (peak > AudiblePeak && float.IsFinite(peak)) lastActivity = Math.Max(lastActivity, packetEnd);
    }
    internal bool IsDue(ulong now, bool paused, bool microphonePreparing) => options.Enabled && !paused &&
        !microphonePreparing && now >= lastActivity && now - lastActivity >= (ulong)options.SilenceMinutes * 600_000_000;
}

internal interface IRecordingProcessLifetime : IDisposable
{
    bool HasExited { get; }
}

/// <summary>A pinned root-process handle. PID reuse and child-process replacement cannot impersonate exit.</summary>
internal sealed class RecordingProcessLifetime : IRecordingProcessLifetime
{
    private readonly SafeProcessHandle handle;
    internal RecordingProcessLifetime(ProcessIdentity identity)
    {
        // Observation only: SYNCHRONIZE + QUERY_LIMITED_INFORMATION, never ALL_ACCESS.
        handle = OpenProcess(0x00101000, false, identity.ProcessId);
        try
        {
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法监测所选应用进程。");
            var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || !QueryFullProcessImageName(handle, 0, path, ref length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法确认所选应用进程身份。");
            if (HasExited || DateTime.FromFileTimeUtc(created).Ticks != identity.StartTimeUtcTicks ||
                !string.Equals(path.ToString(), identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("所选应用已退出或身份改变；请刷新并重新选择。");
        }
        catch { handle.Dispose(); throw; }
    }
    public bool HasExited => WaitForSingleObject(handle, 0) switch
    {
        0 => true,
        258 => false,
        _ => throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法确认所选应用的退出状态。")
    };
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
