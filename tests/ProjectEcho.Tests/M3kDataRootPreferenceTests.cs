using System.Text.Json;
using ProjectEcho.Core;

internal static class M3kDataRootPreferenceTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3k-data-root"); Directory.CreateDirectory(root);
        run("T50-SYNTH M3K first startup does not create a preference or replace legacy settings", () => Absent(root));
        run("T50-SYNTH M3K selected data directory survives independent preference reads", () => RoundTrip(root));
        run("T46/T50-SYNTH M3K internal data root moves with the portable application", () => PortableMove(root));
        run("T50-SYNTH M3K unavailable remembered root is retained without directory recreation", () => Missing(root));
        run("T50-SYNTH M3K malformed unsafe duplicate and future preferences are rejected", () => Invalid(root));
        run("T50-SYNTH M3K failed selection persistence leaves the previous preference untouched", () => FailedSave(root));
        run("T20/T50-SYNTH M3K root preference contains no prompt and leaves both data stores intact", () => Isolation(root));
    }

    private static (string App, string Pref, string A, string B) Fixture(string root, string name)
    {
        string app = Path.Combine(root, name), a = Path.Combine(app, "original-data"), b = Path.Combine(app, "selected-data");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        return (app, Path.Combine(app, "settings", "data-root.json"), a, b);
    }

    private static void Absent(string root)
    {
        var f = Fixture(root, "absent");
        string legacy = Path.Combine(f.A, "whisper.json"); File.WriteAllText(legacy, "existing settings");
        Need(PortableDataRootPreferences.Load(f.Pref, f.App) is null && !File.Exists(f.Pref) &&
            File.ReadAllText(legacy) == "existing settings", "首次启动创建了默认偏好或更改旧配置。");
    }

    private static void RoundTrip(string root)
    {
        var f = Fixture(root, "round-trip");
        PortableDataRootPreferences.Save(f.Pref, f.App, f.A);
        Need(PortableDataRootPreferences.Load(f.Pref, f.App) == f.A, "首次选择未记忆。");
        PortableDataRootPreferences.Save(f.Pref, f.App, f.B);
        string before = File.ReadAllText(f.Pref);
        Need(PortableDataRootPreferences.Load(f.Pref, f.App) == f.B &&
            PortableDataRootPreferences.Load(f.Pref, f.App) == f.B && File.ReadAllText(f.Pref) == before,
            "重启读取改写了偏好或恢复了默认路径。");
        string outsideApp = Path.Combine(root, "external-data"); Directory.CreateDirectory(outsideApp);
        PortableDataRootPreferences.Save(f.Pref, f.App, outsideApp);
        Need(PortableDataRootPreferences.Load(f.Pref, f.App) == outsideApp, "外部显式路径未保留。");
    }

    private static void PortableMove(string root)
    {
        var f = Fixture(root, "move-before");
        PortableDataRootPreferences.Save(f.Pref, f.App, f.B);
        string moved = Path.Combine(root, "move-after"); Directory.Move(f.App, moved);
        Need(PortableDataRootPreferences.Load(Path.Combine(moved, "settings", "data-root.json"), moved) ==
            Path.Combine(moved, "selected-data"), "便携文件夹一起搬动后仍指向旧绝对路径。");
    }

    private static void Missing(string root)
    {
        var f = Fixture(root, "missing"); PortableDataRootPreferences.Save(f.Pref, f.App, f.B);
        string saved = File.ReadAllText(f.Pref); Directory.Move(f.B, f.B + "-offline");
        Need(PortableDataRootPreferences.Load(f.Pref, f.App) == f.B && !Directory.Exists(f.B) &&
            File.ReadAllText(f.Pref) == saved, "不可用路径被静默替换或创建。");
    }

    private static void Invalid(string root)
    {
        var f = Fixture(root, "invalid"); Directory.CreateDirectory(Path.GetDirectoryName(f.Pref)!);
        string[] bad = ["{", "null", "{}", new string(' ', 16001),
            JsonSerializer.Serialize(new { schemaVersion = 2, dataRoot = f.A, relativeToApplication = false }),
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"dataRoot\":\"Data\",\"relativeToApplication\":true}",
            JsonSerializer.Serialize(new { schemaVersion = 1, dataRoot = "../outside", relativeToApplication = true }),
            JsonSerializer.Serialize(new { schemaVersion = 1, dataRoot = "Data", relativeToApplication = false }),
            JsonSerializer.Serialize(new { schemaVersion = 1, dataRoot = "\\\\server\\share\\data", relativeToApplication = false }),
            JsonSerializer.Serialize(new { schemaVersion = 1, dataRoot = f.A + ":stream", relativeToApplication = false })];
        foreach (string content in bad)
        {
            File.WriteAllText(f.Pref, content); bool rejected = false;
            try { _ = PortableDataRootPreferences.Load(f.Pref, f.App); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or ArgumentException) { rejected = true; }
            Need(rejected && File.ReadAllText(f.Pref) == content, "错误偏好被接受或覆盖。");
        }
    }

    private static void FailedSave(string root)
    {
        var f = Fixture(root, "failed-save"); PortableDataRootPreferences.Save(f.Pref, f.App, f.A);
        string before = File.ReadAllText(f.Pref); bool rejected = false;
        try { PortableDataRootPreferences.Save(f.Pref, f.App, Path.Combine(f.App, "absent")); }
        catch (DirectoryNotFoundException) { rejected = true; }
        Need(rejected && File.ReadAllText(f.Pref) == before && PortableDataRootPreferences.Load(f.Pref, f.App) == f.A,
            "失效新选择覆盖了旧记忆。");
        using var readLock = new FileStream(f.Pref, FileMode.Open, FileAccess.Read, FileShare.Read);
        rejected = false;
        try { PortableDataRootPreferences.Save(f.Pref, f.App, f.B); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        Need(rejected && File.ReadAllText(f.Pref) == before && Directory.GetFiles(Path.GetDirectoryName(f.Pref)!, "*.tmp").Length == 0,
            "写入锁定失败破坏旧偏好或留下临时文件。");
    }

    private static void Isolation(string root)
    {
        var f = Fixture(root, "isolation");
        string a = Path.Combine(f.A, "local-asr-settings.json"), b = Path.Combine(f.B, "local-asr-settings.json");
        File.WriteAllText(a, "first synthetic private prompt"); File.WriteAllText(b, "second synthetic private prompt");
        PortableDataRootPreferences.Save(f.Pref, f.App, f.A); PortableDataRootPreferences.Save(f.Pref, f.App, f.B);
        _ = PortableDataRootPreferences.Load(f.Pref, f.App);
        Need(File.ReadAllText(a) == "first synthetic private prompt" && File.ReadAllText(b) == "second synthetic private prompt" &&
            !File.ReadAllText(f.Pref).Contains("prompt", StringComparison.OrdinalIgnoreCase), "目录偏好混入或修改了识别设置。");
    }

    private static void Need(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
