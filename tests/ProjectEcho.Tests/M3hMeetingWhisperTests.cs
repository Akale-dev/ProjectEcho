using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3hMeetingWhisperTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3h-meeting-asr");
        run("T35/T50-SYNTH M3H defaults affect new meetings only and never copy prompts", () => DefaultIsolation(Path.Combine(root, "defaults")));
        run("T35-SYNTH M3H meeting prompts and last attempt configurations survive reopen independently", () => AttemptAndDraftIsolation(Path.Combine(root, "drafts")));
        run("T35-SYNTH M3H legacy latest failed/cancelled attempts migrate without changing originals or CUDA", () => LegacyAttempts(Path.Combine(root, "legacy")));
        run("T35-SYNTH M3H unreadable legacy attempts qualify recovered readable settings and preserve every original", () => UnreadableAttemptWarning(Path.Combine(root, "unreadable")));
        run("T35/T50-SYNTH M3H explicit repair freezes null migration baseline before replacing unreadable defaults", () => NullBaselineRepair(Path.Combine(root, "repair")));
        run("T35/T47-SYNTH M3H per-meeting paths relocate with portable application", () => Relocation(Path.Combine(root, "relocation")));
        run("T35/T50-SYNTH M3H malformed oversized and unsafe settings preserve prior files", () => Boundaries(Path.Combine(root, "boundaries")));
    }

    private static void DefaultIsolation(string root)
    {
        var initial = Configuration(root) with { InitialPrompt = "legacy global prompt must not spread" };
        string settings = Path.Combine(root, "Data", "settings"), path = Path.Combine(settings, "whisper.json");
        var baseline = MeetingWhisperSettingsStore.EnsureLegacyDefaults(settings, initial, root);
        string old = Meeting(root, "older-unvisited"), first = Meeting(root, "first"), next = Meeting(root, "new");
        MeetingWhisperSettingsStore.SnapshotDefaults(first, initial, root);
        var newer = initial with { Threads = 7, Language = "zh", InitialPrompt = "A-only prompt" };
        MeetingWhisperSettingsStore.SaveDefaults(path, newer, root);
        var loaded = PortableWhisperConfiguration.Load(path, root)!;
        Need(loaded.InitialPrompt is null && loaded.Threads == 7, "Default prompt leaked or new defaults were not saved.");
        var frozen = MeetingWhisperSettingsStore.EnsureLegacyDefaults(settings, newer, root);
        Need(frozen == baseline && frozen?.InitialPrompt is null, "Legacy migration baseline changed after saving defaults.");
        MeetingWhisperSettingsStore.SnapshotDefaults(next, loaded, root);
        Need(MeetingWhisperSettingsStore.LoadOrCreate(old, frozen, root).Configuration?.Threads == 2, "Unvisited old meeting inherited later defaults.");
        Need(MeetingWhisperSettingsStore.LoadOrCreate(first, newer, root).Configuration?.Threads == 2, "Existing new meeting lost its creation snapshot.");
        var fresh = MeetingWhisperSettingsStore.LoadOrCreate(next, frozen, root);
        Need(fresh.Configuration?.Threads == 7 && fresh.PromptDraft is null, "New meeting failed to get defaults without prompt.");
    }

    private static void AttemptAndDraftIsolation(string root)
    {
        var initial = Configuration(root); string a = Meeting(root, "a"), b = Meeting(root, "b");
        MeetingWhisperSettingsStore.SnapshotDefaults(a, initial, root); MeetingWhisperSettingsStore.SnapshotDefaults(b, initial, root);
        var attempted = initial with { Threads = 6, Language = "en", Hardware = LocalTranscriptionHardware.Auto, InitialPrompt = "A recognition prompt" };
        MeetingWhisperSettingsStore.SaveAttemptConfiguration(a, attempted, root);
        MeetingWhisperSettingsStore.SavePrompt(a, "A later unsent draft", root);
        MeetingWhisperSettingsStore.SavePrompt(b, "B private draft", root);
        var reopenedA = MeetingWhisperSettingsStore.LoadOrCreate(a, initial with { Threads = 9 }, root);
        var reopenedB = MeetingWhisperSettingsStore.LoadOrCreate(b, attempted, root);
        Need(reopenedA.Configuration == attempted && reopenedA.PromptDraft == "A later unsent draft", "Prompt draft overwrote last attempt options or last used prompt.");
        Need(reopenedB.Configuration == initial && reopenedB.PromptDraft == "B private draft", "A configuration or prompt leaked to B.");
        MeetingWhisperSettingsStore.SavePrompt(a, "", root);
        Need(MeetingWhisperSettingsStore.LoadOrCreate(a, initial, root).PromptDraft == "", "Cleared prompt came back after reopen.");
    }

    private static void LegacyAttempts(string root)
    {
        var baseline = Configuration(root); string meeting = Meeting(root, "legacy");
        string earlier = WriteAttempt(meeting, "z-earlier", "2026-10-01T01:00:00Z", "Succeeded", "Cpu", "old", "ggml-base", 3);
        string latest = WriteAttempt(meeting, "a-latest", "2026-10-02T01:00:00Z", "Cancelled", "Cuda", "cancelled prompt", "ggml-large-v3", 8);
        var originals = new[] { earlier, latest, Path.Combine(meeting, "meeting.json") }.ToDictionary(path => path, File.ReadAllText);
        var restored = MeetingWhisperSettingsStore.LoadOrCreate(meeting, baseline, root);
        Need(restored.Configuration is { Threads: 8, Hardware: LocalTranscriptionHardware.Cuda, Language: "zh", ModelPath: "" } &&
            restored.PromptDraft == "cancelled prompt" && restored.MigrationNotice is not null,
            "Latest cancelled settings/CUDA were lost or absent legacy model silently substituted.");
        Need(originals.All(pair => File.ReadAllText(pair.Key) == pair.Value), "Migration rewrote authoritative files.");
        string failed = Meeting(root, "failed"); WriteAttempt(failed, "only", "2026-10-02T01:00:00Z", "Failed", "Vulkan", "failed prompt", "ggml-base", 4);
        var recoveredFailed = MeetingWhisperSettingsStore.LoadOrCreate(failed, baseline, root);
        Need(recoveredFailed.Configuration?.Threads == 4 && recoveredFailed.Configuration.Hardware == LocalTranscriptionHardware.Vulkan &&
            recoveredFailed.PromptDraft == "failed prompt", "Failed attempt was ignored during migration.");
    }

    private static void Relocation(string root)
    {
        string app = Path.Combine(root, "old"), moved = Path.Combine(root, "中文新位置");
        var initial = Configuration(app); var expected = Configuration(moved);
        string meeting = Meeting(app, "a"), target = Meeting(moved, "a");
        MeetingWhisperSettingsStore.SnapshotDefaults(meeting, initial, app);
        MeetingWhisperSettingsStore.SavePrompt(meeting, "portable prompt", app);
        string sourcePath = Path.Combine(meeting, MeetingWhisperSettingsStore.RelativePath);
        string targetPath = Path.Combine(target, MeetingWhisperSettingsStore.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!); File.Copy(sourcePath, targetPath);
        var restored = MeetingWhisperSettingsStore.LoadOrCreate(target, null, moved);
        Need(restored.Configuration == expected && restored.PromptDraft == "portable prompt", "Meeting settings did not relocate relative paths.");
        Need(!File.ReadAllText(sourcePath).Contains(app.Replace("\\", "\\\\"), StringComparison.Ordinal), "Portable meeting snapshot stored app absolute path.");
    }

    private static void UnreadableAttemptWarning(string root)
    {
        var configuration = Configuration(root); string meeting = Meeting(root, "mixed-readable-and-corrupt");
        WriteAttempt(meeting, "readable", "2026-10-01T00:00:00Z", "Succeeded", "Auto", "earlier prompt", "ggml-base", 3);
        string corrupt = WriteAttempt(meeting, "newer-corrupt", "2026-10-02T00:00:00Z", "Failed", "Cpu", "newer prompt", "ggml-base", 7);
        File.WriteAllText(corrupt, "{ damaged latest attempt");
        string wrongType = WriteAttempt(meeting, "bad-time-type", "2026-10-02T01:00:00Z", "Failed", "Cpu", "unused", "ggml-base", 4);
        File.WriteAllText(wrongType, "{\"importedAt\":42}");
        Directory.CreateDirectory(Path.Combine(meeting, "transcripts", "asr", "interrupted-before-attempt-write"));
        var originals = Directory.EnumerateFiles(meeting, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        var restored = MeetingWhisperSettingsStore.LoadOrCreate(meeting, configuration, root);
        string notice = restored.MigrationNotice ?? "";
        Need(restored.Configuration?.Threads == 3 && restored.PromptDraft == "earlier prompt" &&
            notice.Contains("最近可读", StringComparison.Ordinal) && notice.Contains("可能不是最后一次", StringComparison.Ordinal) &&
            !notice.Contains("已恢复上次", StringComparison.Ordinal),
            "Fallback to an older readable attempt was falsely labelled as the last recognition configuration.");
        Need(MeetingWhisperSettingsStore.LoadExisting(meeting, root)?.MigrationNotice == restored.MigrationNotice,
            "Reopening erased uncertainty about the migrated attempt.");
        Need(originals.All(pair => File.ReadAllText(pair.Key) == pair.Value), "Migration rewrote a readable or damaged ASR original.");
    }

    private static void NullBaselineRepair(string root)
    {
        var chosen = Configuration(root) with { Threads = 11 };
        string settings = Path.Combine(root, "Data", "settings"), path = Path.Combine(settings, "whisper.json");
        Directory.CreateDirectory(settings); File.WriteAllText(path, "invalid global configuration");
        string old = Meeting(root, "unvisited-old"), created = Meeting(root, "new-after-repair");
        Expect<JsonException>(() => PortableWhisperConfiguration.Load(path, root));
        var baseline = MeetingWhisperSettingsStore.EnsureLegacyDefaults(settings, null, root);
        Need(baseline is null, "Unreadable defaults created invented old meeting settings.");
        MeetingWhisperSettingsStore.SaveDefaults(path, chosen, root);
        var repaired = PortableWhisperConfiguration.Load(path, root)!;
        Need(repaired.Threads == 11, "Explicit valid defaults did not replace corrupted JSON.");
        baseline = MeetingWhisperSettingsStore.EnsureLegacyDefaults(settings, repaired, root);
        Need(baseline is null && MeetingWhisperSettingsStore.LoadOrCreate(old, baseline, root).Configuration is null,
            "The first valid replacement leaked into an existing unvisited meeting.");
        MeetingWhisperSettingsStore.SnapshotDefaults(created, repaired, root);
        Need(MeetingWhisperSettingsStore.LoadExisting(created, root)?.Configuration?.Threads == 11,
            "A new meeting failed to inherit repaired defaults.");
    }

    private static void Boundaries(string root)
    {
        var configuration = Configuration(root); string meeting = Meeting(root, "safe");
        MeetingWhisperSettingsStore.SnapshotDefaults(meeting, configuration, root);
        string path = Path.Combine(meeting, MeetingWhisperSettingsStore.RelativePath), original = File.ReadAllText(path);
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.SavePrompt(meeting, new string('x', 4001), root));
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.SaveAttemptConfiguration(meeting, configuration with { Threads = 0 }, root));
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.SaveAttemptConfiguration(meeting, configuration with { ModelPath = "../outside.bin" }, root));
        Need(File.ReadAllText(path) == original, "Rejected settings destroyed prior atomic snapshot.");
        string cudaDefault = Path.Combine(root, "cuda-default.json");
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.SaveDefaults(cudaDefault, configuration with { Hardware = LocalTranscriptionHardware.Cuda }, root));
        Need(!File.Exists(cudaDefault), "Unsupported CUDA persisted as new defaults.");
        File.WriteAllText(path, new string('x', 64001));
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.LoadOrCreate(meeting, configuration, root));
        Need(new FileInfo(path).Length == 64001, "Oversized snapshot was overwritten by defaults.");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"SchemaVersion\":1}");
        Expect<InvalidDataException>(() => MeetingWhisperSettingsStore.LoadOrCreate(meeting, configuration, root));
        Need(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic writes leaked temporary settings files.");
    }

    private static WhisperConfiguration Configuration(string root)
    {
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "custom.exe"), model = Path.Combine(root, "ggml-base.bin");
        File.WriteAllText(executable, "Never execute synthetic fixture."); File.WriteAllText(model, "Never infer synthetic fixture.");
        return new(executable, model, ModelName: "ggml-base", ModelLicenseAcknowledged: true);
    }

    private static string Meeting(string root, string name)
    {
        string directory = Path.Combine(root, "Data", "Recordings", name); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meeting.json"), "{\"isSynthetic\":true}"); return directory;
    }

    private static string WriteAttempt(string meeting, string name, string importedAt, string status, string hardware, string prompt, string model, int threads)
    {
        string directory = Path.Combine(meeting, "transcripts", "asr", name); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "attempt.json"); File.WriteAllText(path, JsonSerializer.Serialize(new
        { importedAt, status, requestedHardware = hardware, initialPrompt = prompt, modelName = model, threads, language = "zh", gpuDeviceIndex = 1, modelLicenseAcknowledged = true }));
        return path;
    }
    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidDataException("Expected " + typeof(T).Name); }
}
