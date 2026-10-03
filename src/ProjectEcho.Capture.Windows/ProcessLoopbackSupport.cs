using System.Runtime.Versioning;

namespace ProjectEcho.Capture.Windows;

internal enum ProcessLoopbackAvailability { Unavailable, CompatibilityAttempt, DocumentedBaseline }

/// <summary>Build eligibility only; it never enumerates, activates or starts audio.</summary>
internal sealed record ProcessLoopbackCapability(Version WindowsVersion, ProcessLoopbackAvailability Availability)
{
    internal bool CanAttempt => Availability != ProcessLoopbackAvailability.Unavailable;
    internal string VersionDescription => $"Windows {WindowsVersion.Major}.{WindowsVersion.Minor} build {WindowsVersion.Build}（运行时报告）";
    internal string PreflightMessage => Availability switch
    {
        ProcessLoopbackAvailability.CompatibilityAttempt =>
            $"{VersionDescription}：进程回环为 19041–20347 兼容尝试，尚未在此目标机验证；仅在点击开始后激活所选进程树，失败不会自动切换到端点录音。请主动短试录核对声音范围和完整保存。",
        ProcessLoopbackAvailability.DocumentedBaseline =>
            $"{VersionDescription}：达到进程回环参考基线 20348；实际激活、声音范围和完整保存仍须用户主动验证。",
        _ => $"{VersionDescription}：进程回环兼容尝试至少需要 Windows 10 build 19041；本平台不尝试激活。如需端点兼容模式，请自行选择并明确确认范围。"
    };
}

internal static class ProcessLoopbackSupport
{
    // Microsoft's sample states 20348. OBS explicitly enables process filtering
    // at 19041, and the locked NAudio 3.1.0 XML documents 19041. Keep the earlier
    // range a visible, unverified attempt rather than a hardware support claim.
    // https://github.com/obsproject/obs-studio/blob/master/plugins/win-wasapi/plugin-main.cpp
    internal static ProcessLoopbackCapability Current => Evaluate(Environment.OSVersion.Version, OperatingSystem.IsWindows());

    // The NAudio call sits inside a deferred activation callback. Its local
    // guard gives the platform analyzer the same runtime decision as preflight.
    [SupportedOSPlatformGuard("windows10.0.19041")]
    internal static bool CanAttemptCurrent => Current.CanAttempt;

    internal static ProcessLoopbackCapability Evaluate(Version windowsVersion, bool isWindows = true)
    {
        ArgumentNullException.ThrowIfNull(windowsVersion);
        var availability = !isWindows || windowsVersion < new Version(10, 0, 19041)
            ? ProcessLoopbackAvailability.Unavailable
            : windowsVersion < new Version(10, 0, 20348)
                ? ProcessLoopbackAvailability.CompatibilityAttempt
                : ProcessLoopbackAvailability.DocumentedBaseline;
        return new(windowsVersion, availability);
    }

    /// <summary>Called only by a recording start path after source identity validation.</summary>
    internal static T Activate<T>(Func<T> activation) => Activate(Current, activation);

    internal static T Activate<T>(ProcessLoopbackCapability capability, Func<T> activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!capability.CanAttempt) throw new PlatformNotSupportedException(capability.PreflightMessage);
        try { return activation(); }
        catch (Exception error)
        {
            string attempt = capability.Availability == ProcessLoopbackAvailability.CompatibilityAttempt ? "兼容尝试" : "激活";
            throw new ProcessLoopbackActivationException(
                $"所选进程树回环{attempt}失败（{capability.VersionDescription}，HRESULT 0x{error.HResult:X8}）：{error.Message}。未切换到端点录音。", error);
        }
    }

    private sealed class ProcessLoopbackActivationException : InvalidOperationException
    {
        internal ProcessLoopbackActivationException(string message, Exception inner) : base(message, inner) => HResult = inner.HResult;
    }
}
