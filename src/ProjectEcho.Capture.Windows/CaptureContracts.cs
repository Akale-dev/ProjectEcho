namespace ProjectEcho.Capture.Windows;

public enum SourceMode { ProcessTree, EndpointLoopback }
public sealed record AudioDevice(string Id, string Name);
public sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath, string Name);
public sealed record ProcessSource(ProcessIdentity Root, ProcessIdentity AudioProcess,
    string EndpointId, string EndpointName, string SessionState, string DisplayName);
public sealed record SourceSnapshot(IReadOnlyList<AudioDevice> RenderDevices,
    IReadOnlyList<AudioDevice> Microphones, IReadOnlyList<ProcessSource> ProcessSources,
    IReadOnlyList<string> Warnings, int WindowsBuild, bool ProcessLoopbackBuildEligible);
public sealed record CaptureOptions(SourceMode Mode, ProcessSource? ProcessSource,
    string? RenderDeviceId, bool EndpointScopeConfirmed, bool MicrophoneEnabled,
    string? MicrophoneDeviceId, string SessionDirectory, string Title, int MaximumSeconds = 10);
public sealed record PreflightResult(bool CanStart, IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings, long EstimatedBytesPerSecond, long AvailableBytes, string ScopeDescription);
public sealed record CaptureLevels(float RemotePeak, float MicrophonePeak,
    long RemoteFrames, long MicrophoneFrames, string Status);
