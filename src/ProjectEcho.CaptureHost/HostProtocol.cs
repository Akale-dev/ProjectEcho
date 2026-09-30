using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.CaptureHost;

public sealed record HostCommand(int ProtocolVersion, string SessionId, string RequestId, string Command,
    CaptureOptions? Options = null, RecordingOptions? Recording = null, string? DeviceId = null);
public sealed record HostEvent(int ProtocolVersion, string SessionId, string RequestId, string Kind,
    string? Message = null, CaptureLevels? Levels = null, CaptureResult? Result = null, string? MixedPath = null,
    RecordingAck? Ack = null, RecordingHealth? Health = null, RecordingResult? RecordingResult = null);
