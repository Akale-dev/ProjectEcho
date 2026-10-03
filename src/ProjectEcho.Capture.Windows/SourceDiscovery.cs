using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace ProjectEcho.Capture.Windows;

/// <summary>Only enumerates identity/metadata. No capture client is initialized or started here.</summary>
public static class SourceDiscovery
{
    private static readonly HashSet<string> BroadRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "explorer", "svchost", "services", "wininit", "winlogon",
        "csrss", "smss", "dwm", "sihost", "taskhostw", "dllhost", "rundll32", "cmd", "powershell", "pwsh",
        "projectecho", "projectecho.app", "projectecho.capturehost"
    };

    public static SourceSnapshot Enumerate()
    {
        var render = new List<AudioDevice>();
        var microphones = new List<AudioDevice>();
        var sources = new List<ProcessSource>();
        var warnings = new List<string>();
        var parents = ReadParents();
        using var enumerator = new MMDeviceEnumerator();
        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            for (int d = 0; d < devices.Count; d++)
            {
                using var device = devices[d];
                render.Add(new(device.ID, device.FriendlyName));
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int s = 0; s < sessions.Count; s++)
                    {
                        using var session = sessions[s];
                        if (session.IsSystemSoundsSession || session.GetProcessID == 0) continue;
                        var audioProcess = TryIdentity(checked((int)session.GetProcessID));
                        if (audioProcess is null) continue;
                        foreach (var root in Ancestors(audioProcess, parents))
                        {
                            if (IsBroadRoot(root)) break;
                            sources.Add(new(root, audioProcess, device.ID, device.FriendlyName, session.State.ToString(),
                                $"{root.Name} · PID {root.ProcessId} 进程树 ← 音频 PID {audioProcess.ProcessId} · {device.FriendlyName} · {session.State}"));
                        }
                    }
                }
                catch (Exception ex) { warnings.Add($"输出设备会话枚举失败：{ex.GetType().Name} (0x{ex.HResult:X8})"); }
            }
        }
        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            for (int i = 0; i < devices.Count; i++)
            {
                using var device = devices[i];
                microphones.Add(new(device.ID, device.FriendlyName));
            }
        }
        var capability = ProcessLoopbackSupport.Current;
        int build = capability.WindowsVersion.Build;
        return new(render, microphones, sources.DistinctBy(x => (x.Root.ProcessId, x.AudioProcess.ProcessId, x.EndpointId)).ToArray(),
            warnings, build, capability.CanAttempt);
    }

    public static PreflightResult Preflight(CaptureOptions options)
    {
        var errors = new List<string>();
        var warnings = new List<string>
        {
            "预检不开始采集；实际激活、麦克风权限、声音范围及回放仍须用户主动短试录。",
            "M0 仅限最多 10 秒样机试录；长期录音、恢复与双设备漂移尚未验收。"
        };
        long bytesPerSecond = 0, available = 0;
        string scope = "未选来源";
        if (options.MaximumSeconds is < 1 or > 10) errors.Add("M0 试录时长必须为 1–10 秒。");
        using var enumerator = new MMDeviceEnumerator();
        if (options.Mode == SourceMode.ProcessTree)
        {
            var capability = ProcessLoopbackSupport.Current;
            if (!capability.CanAttempt) errors.Add(capability.PreflightMessage);
            else warnings.Add(capability.PreflightMessage);
            if (options.ProcessSource is null) errors.Add("请从真实音频会话候选中选择进程树。");
            else
            {
                try { ValidateSource(options.ProcessSource); }
                catch (Exception ex) { errors.Add(ex.Message); }
                scope = $"{options.ProcessSource.Root.Name} PID {options.ProcessSource.Root.ProcessId} 及其子进程全部声音（不是单标签页或单人）";
            }
            bytesPerSecond = 48000 * 2 * 4;
        }
        else if (options.Mode == SourceMode.EndpointLoopback)
        {
            if (!options.EndpointScopeConfirmed) errors.Add("必须明确确认：所选输出端点上的所有应用声音都会被采集。");
            try
            {
                using var device = GetExactDevice(enumerator, options.RenderDeviceId, DataFlow.Render);
                scope = $"端点兼容模式：{device.FriendlyName} 上所有应用声音";
                using var client = device.CreateAudioClient();
                var format = client.MixFormat;
                CaptureSession.Describe(format).Validate();
                if (format.Channels > 2) errors.Add("M0 仅验证单/双声道混音路径；所选端点为多声道，请选择单/双声道端点。");
                bytesPerSecond = format.AverageBytesPerSecond;
            }
            catch (Exception ex) { errors.Add($"所选输出端点不可用：{ex.Message}"); }
        }
        else errors.Add("未知来源模式。");
        if (options.MicrophoneEnabled)
        {
            try
            {
                using var device = GetExactDevice(enumerator, options.MicrophoneDeviceId, DataFlow.Capture);
                using var selected = MicrophoneFormatPolicy.OpenClient(device);
                var format = selected.Format;
                CaptureSession.Describe(format).Validate();
                if (format.Channels > 2) errors.Add("M0 仅支持单/双声道 mic 原轨；不自动修改设备配置。");
                bytesPerSecond += format.AverageBytesPerSecond;
                warnings.Add($"麦克风原轨请求 {format.SampleRate} Hz / {format.BitsPerSample}-bit / {format.Channels} 声道" +
                    (selected.ProcessingMode == MicrophoneFormatPolicy.SharedRaw
                        ? "（同设备 RAW 共享格式，不加采样率转换；可能与默认处理模式的音色不同）"
                        : selected.ConversionFlags != 0 ? $"（共享引擎为 {selected.DefaultMixRate} Hz，将转换到设备帧计数的采样率）" : "") +
                    "；不修改系统格式。实际初始化及设备时钟质量需试录核验。");
                if (selected.FallbackReason is not null)
                    warnings.Add("所选设备 RAW 模式或格式不可用，预检保留同设备兼容转换方案：" + selected.FallbackReason);
                warnings.Add("独立 mic 已明确启用；会议平台静音不会关闭本程序 mic，也可能录到回声/多人。");
            }
            catch (Exception ex) { errors.Add($"所选麦克风不可用，不会替换：{ex.Message}"); }
        }
        else warnings.Add("麦克风关闭：不创建、初始化或启动 mic 采集流。");
        try
        {
            string full = Path.GetFullPath(options.SessionDirectory);
            available = new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
            long budget = checked(bytesPerSecond * Math.Max(1, options.MaximumSeconds) * 4 + 64L * 1024 * 1024);
            if (available < budget) errors.Add("磁盘可用空间低于原轨、混音和临时文件预留预算。");
        }
        catch (Exception ex) { errors.Add($"输出目录预检失败：{ex.GetType().Name}"); }
        return new(errors.Count == 0, errors, warnings, bytesPerSecond, available, scope);
    }

    public static bool IdentityMatches(ProcessIdentity identity)
    {
        var current = TryIdentity(identity.ProcessId);
        return current is not null && current.StartTimeUtcTicks == identity.StartTimeUtcTicks
            && string.Equals(current.ExecutablePath, identity.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBroadRoot(ProcessIdentity identity) => identity.ProcessId <= 4
        || identity.ProcessId == Environment.ProcessId || BroadRoots.Contains(identity.Name)
        || Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals("ProjectEcho", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(identity.ExecutablePath).Equals("ProjectEcho.CaptureHost", StringComparison.OrdinalIgnoreCase);

    public static void ValidateSource(ProcessSource source)
    {
        if (IsBroadRoot(source.Root)) throw new InvalidOperationException("拒绝系统、shell 或本程序等范围过宽的进程树。");
        if (!IdentityMatches(source.Root) || !IdentityMatches(source.AudioProcess))
            throw new InvalidOperationException("来源已退出、身份不可读取或 PID 已复用；请刷新并重新选择。");
        if (!Ancestors(source.AudioProcess, ReadParents()).Any(x => x == source.Root))
            throw new InvalidOperationException("实际音频进程已不在所选进程树；请刷新并重新选择。");
    }

    internal static MMDevice GetExactDevice(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("未选择设备。");
        var device = enumerator.GetDevice(id);
        if (device.State != DeviceState.Active || device.DataFlow != flow)
        {
            device.Dispose();
            throw new InvalidOperationException("原选择设备不存在或不是活动状态；不自动替换。");
        }
        return device;
    }

    internal static ProcessIdentity? TryIdentity(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            if (process.SessionId != current.SessionId) return null;
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            return new(pid, process.StartTime.ToUniversalTime().Ticks, path, process.ProcessName);
        }
        catch { return null; }
    }

    private static IEnumerable<ProcessIdentity> Ancestors(ProcessIdentity first, Dictionary<int, int> parents)
    {
        var seen = new HashSet<int>();
        var current = first;
        for (int depth = 0; depth < 32 && seen.Add(current.ProcessId); depth++)
        {
            yield return current;
            if (!parents.TryGetValue(current.ProcessId, out int parent) || parent == current.ProcessId) yield break;
            var next = TryIdentity(parent);
            // A parent newer than the child means the snapshot contains a reused parent PID.
            if (next is null || next.StartTimeUtcTicks > current.StartTimeUtcTicks) yield break;
            current = next;
        }
    }

    internal static Dictionary<int, int> ReadParents()
    {
        var parents = new Dictionary<int, int>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return parents;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExeFile = "" };
        if (!Process32First(snapshot, ref entry)) return parents;
        do { parents[unchecked((int)entry.ProcessId)] = unchecked((int)entry.ParentProcessId); }
        while (Process32Next(snapshot, ref entry));
        return parents;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
