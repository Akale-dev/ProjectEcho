using System.Text.Json;
using ProjectEcho.Core;

internal static class ClockEvidence
{
    internal static int Run(string[] args)
    {
        if (args.Length != 2) return 2;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        if (root is null) return 2;
        string directory = SyntheticAudioProbe.SafeProjectPath(root.FullName, args[1]);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "capture-result.json")));
        var result = document.RootElement.GetProperty("result").Deserialize<CaptureResult>()!;
        string mode = document.RootElement.GetProperty("sourceMode").GetString()!;
        var reports = result.Tracks.Select(track => new
        {
            track.SourceId,
            clock = ClockCapabilityEvaluator.Evaluate(track.SourceId == "mic" ? ClockSourceKind.Microphone
                : mode == "ProcessTree" ? ClockSourceKind.ProcessTree : ClockSourceKind.EndpointLoopback, track, result.EpochQpc100ns)
        }).ToArray();
        bool usable = reports.Length != 0 && reports.All(report => report.clock.IsPacketTimelineUsable);
        string output = Path.Combine(root.FullName, ".local", "diagnostics", $"clock-capability-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            checkedAtUtc = DateTimeOffset.UtcNow, classification = usable ? "FEASIBLE_WITH_EXPLICIT_CLOCK_LIMITS" : "INVALID_OR_UNPROVEN",
            legacyComplete = result.Complete, legacyAuditUnchanged = true, mode, reports,
            storageValidatedByThisCommand = false, crossSourceSynchronizationValidated = false,
            note = "This classifies actual API clocks, not storage or hardware accuracy. All-zero device values remain unchanged."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{(usable ? "FEASIBLE_WITH_EXPLICIT_CLOCK_LIMITS" : "INVALID_OR_UNPROVEN")}; legacyComplete={result.Complete}; synchronization=NOT_VALIDATED");
        Console.WriteLine("Evidence: " + output);
        return usable ? 0 : 1;
    }
}
