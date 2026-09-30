using System.Text.Json;
using ProjectEcho.Capture.Windows;

internal static class AudioDeviceDiagnostics
{
    internal static int Run(string[] args)
    {
        if (args.Length != 1) return 2;
        var snapshot = SourceDiscovery.Enumerate();
        var report = new { CheckedAtUtc = DateTimeOffset.UtcNow, snapshot.WindowsBuild,
            snapshot.Microphones, snapshot.RenderDevices, RenderDeviceCount = snapshot.RenderDevices.Count,
            CaptureStarted = false, MicrophoneClientCreated = false, snapshot.Warnings };
        string root = SyntheticAudioProbe.FindProjectRoot();
        string folder = SyntheticAudioProbe.SafeProjectPath(root, ".local/diagnostics");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "microphone-inventory-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Evidence: " + path);
        return 0;
    }
}
