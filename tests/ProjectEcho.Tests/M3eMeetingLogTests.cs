using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Diagnostics;

internal static class M3eMeetingLogTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3e-meeting-log"); Directory.CreateDirectory(root);
        run("M3E-SYNTH per-meeting Log reopens with UTC timestamps and idempotent legacy events", () => Isolation(root));
        run("M3E-SYNTH concurrent Log writers preserve every successful event", () => Concurrent(root));
        run("M3E-SYNTH corrupt and truncated Log lines preserve valid history and future appends", () => Corruption(root));
        run("M3E-SYNTH unavailable Log storage returns failure without creating recording directories", () => FailureIsolation(root));
        run("T50/M3E-SYNTH shareable Log redacts identities and includes no transcript prompt or audio", () => RedactedExport(root));
        run("T50/M3E-SYNTH Log export refuses originals and implicit overwrite", () => ExportSafety(root));
        run("T50/M3E-SYNTH corrupt meeting metadata exports event facts while withholding private details", () => CorruptMetadataExport(root));
    }

    private static void Isolation(string root)
    {
        string a = Meeting(root, "isolation-a"), b = Meeting(root, "isolation-b");
        var clock = new DateTimeOffset(2026, 10, 2, 18, 32, 44, TimeSpan.FromHours(8));
        var first = MeetingLogStore.TryAppend(a, "recording", "info", "录音保存，mic disabled。",
            "RECORDING_SAVED", clock, "legacy-recording-1", "durationMs=32831; source=endpoint; mic=false");
        Need(first.Success && first.Entry!.TimestampUtc.Offset == TimeSpan.Zero && first.Entry.TimestampUtc == clock, "时间戳没有保存为实际UTC时间。");
        Need(MeetingLogStore.TryAppend(b, "asr", "error", "合成失败", "ASR_FAILED").Success, "B日志保存失败。");
        var duplicate = MeetingLogStore.TryAppend(a, "recording", "info", "重复导入不能覆盖原记录", eventId: "legacy-recording-1");
        Need(duplicate.Success && duplicate.Entry!.Message == first.Entry!.Message, "幂等导入覆盖了已有事件。");
        var reopenedA = MeetingLogStore.Read(a);
        var reopenedB = MeetingLogStore.Read(b);
        Need(reopenedA.Error is null && reopenedB.Error is null && reopenedA.InvalidLineCount == 0 &&
            reopenedA.Entries.Count == 1 && reopenedB.Entries.Count == 1 &&
            reopenedA.Entries.Single().EventCode == "RECORDING_SAVED" && reopenedB.Entries.Single().EventCode == "ASR_FAILED",
            "A/B历史混淆或重读丢失事件。");
        Need(File.Exists(Path.Combine(a, MeetingLogStore.FileName)), "日志未持久化到会议目录。");
    }

    private static void Concurrent(string root)
    {
        string meeting = Meeting(root, "concurrent");
        Parallel.For(0, 120, i => Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "info", "synthetic event " + i,
            eventId: "concurrent-" + i).Success, "并发日志追加失败。"));
        // The same old event can be encountered concurrently while switching between meeting pages.
        Parallel.For(0, 20, _ => Need(MeetingLogStore.TryAppend(meeting, "mix", "warning", "synthetic old event",
            eventId: "legacy-mix-1").Success, "并发幂等导入失败。"));
        var read = MeetingLogStore.Read(meeting);
        Need(read.Error is null && read.InvalidLineCount == 0 && read.Entries.Count == 121 &&
            read.Entries.Select(item => item.EventId).Distinct().Count() == 121, "并发日志丢失、交叉或重复。");
    }

    private static void Corruption(string root)
    {
        string meeting = Meeting(root, "corruption");
        Need(MeetingLogStore.TryAppend(meeting, "recording", "info", "before broken tail", eventId: "before").Success, "初始写入失败。");
        string log = Path.Combine(meeting, MeetingLogStore.FileName);
        File.AppendAllText(log, "{broken middle}\n", new UTF8Encoding(false));
        File.AppendAllText(log, "{\"eventId\":\"partial", new UTF8Encoding(false));
        byte[] damagedPrefix = File.ReadAllBytes(log);
        var damaged = MeetingLogStore.Read(meeting);
        Need(damaged.Error is null && damaged.InvalidLineCount == 2 && damaged.Entries.Count == 1, "损坏行阻挡之前历史。");
        Need(MeetingLogStore.TryAppend(meeting, "export", "info", "after broken tail", eventId: "after").Success, "截断尾行使后续日志无法写入。");
        byte[] after = File.ReadAllBytes(log);
        Need(after.AsSpan(0, damagedPrefix.Length).SequenceEqual(damagedPrefix), "修复日志时更改或删除了旧字节。");
        var repaired = MeetingLogStore.Read(meeting);
        Need(repaired.Error is null && repaired.InvalidLineCount == 2 && repaired.Entries.Select(item => item.EventId).SequenceEqual(new[] { "before", "after" }),
            "追加分隔符后有效历史丢失。");
        // Invalid UTF-8 and oversized lines are isolated without decoding replacement characters or unbounded memory.
        using (var file = new FileStream(log, FileMode.Append)) { file.Write(new byte[] { 0xff, 0xfe, 0x0a }); file.Write(new byte[256 * 1024 + 5]); file.WriteByte(10); }
        Need(MeetingLogStore.TryAppend(meeting, "edit", "info", "after malformed UTF8", eventId: "last").Success, "坏编码阻断新事件。");
        var final = MeetingLogStore.Read(meeting);
        Need(final.Entries.Count == 3 && final.InvalidLineCount == 4, "坏UTF8或超长行未单独报告。");
    }

    private static void FailureIsolation(string root)
    {
        string missing = Path.Combine(root, "must-not-create");
        var missingResult = MeetingLogStore.TryAppend(missing, "recording", "info", "not started");
        Need(!missingResult.Success && missingResult.Error is not null && !Directory.Exists(missing), "日志提前创建录音目录或未返回失败。");
        string meeting = Meeting(root, "failure");
        string path = Path.Combine(meeting, MeetingLogStore.FileName);
        using (var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var result = MeetingLogStore.TryAppend(meeting, "asr", "error", "synthetic failure");
            Need(!result.Success && result.Error is not null, "锁定日志未以结果报告失败。");
        }
        Need(MeetingLogStore.TryAppend(meeting, "recording", "info", "main recording can still save").Success, "日志解除锁定后不能恢复。");
        var badCategory = MeetingLogStore.TryAppend(meeting, "private-title", "info", "invalid");
        Need(!badCategory.Success && badCategory.Error is not null, "任意私密分类被写入可分享字段。");
        Need(MeetingLogStore.Read(meeting).Entries.Count == 1, "失败写入改变已有成功历史。");
    }

    private static void RedactedExport(string root)
    {
        string meeting = Meeting(root, "redaction");
        const string title = "Private acquisition meeting";
        const string project = "Private client Delta";
        const string transcript = "CONFIDENTIAL TRANSCRIPT WORDS 731";
        const string prompt = "SECRET INITIAL PROMPT 493";
        File.WriteAllText(Path.Combine(meeting, "meeting.json"), JsonSerializer.Serialize(new { title, project }));
        File.WriteAllText(Path.Combine(meeting, "filing.json"), JsonSerializer.Serialize(new { title = "Renamed private meeting", project }));
        string audio = Path.Combine(meeting, "private.wav"); File.WriteAllBytes(audio, new byte[] { 1, 2, 3, 4 });
        string transcriptFile = Path.Combine(meeting, "private-transcript.txt"); File.WriteAllText(transcriptFile, transcript);
        var details = string.Join('\n', "durationMs=32831; elapsedMs=42800; Ver. M3E; exitCode=1; E_ASR_TIMING",
            "C:\\Users\\Sensitive User\\Secret Meeting\\recording.wav",
            "\\\\server-private\\share-secret\\meeting.wav", "/home/someone/private/audio.wav",
            "https://meet.example.test/secret-room?token=private", "person.private@example.test",
            title, project, "Renamed private meeting", Environment.UserName, Environment.MachineName,
            "initial_prompt=" + prompt, "transcript: " + transcript, "explicit private value");
        Need(MeetingLogStore.TryAppend(meeting, "asr", "error", transcript + "\n" + prompt, "E_ASR_TIMING",
            shareableDetails: details).Success, "合成诊断保存失败。");
        Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "warning", "UNTRUSTED LOCAL ONLY " + transcript).Success,
            "本机诊断保存失败。");
        Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "info", "privacy fixture", shareableDetails:
            "durationMs=32831\nusername: ForeignMachineUser\nprivate account continuation").Success, "账号合成诊断失败。");
        Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "info", "privacy fixture", shareableDetails:
            "initial_prompt: " + prompt + "\nPRIVATE MULTILINE PROMPT CONTINUATION").Success, "多行prompt合成诊断失败。");
        Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "info", "privacy fixture", shareableDetails:
            "zoommtg://private-room-ID/launch?token=meeting-secret").Success, "私有协议URL合成诊断失败。");
        var snapshot = Directory.EnumerateFiles(meeting).ToDictionary(file => file, File.ReadAllBytes);
        string destination = Path.Combine(root, "shareable-log.txt");
        MeetingLogStore.ExportText(meeting, destination, new[] { "explicit private value" });
        string exported = File.ReadAllText(destination);
        foreach (string forbidden in new[] { title, project, "Renamed private meeting", transcript, prompt, "Sensitive User", "Secret Meeting", "server-private",
            "share-secret", "someone", "secret-room", "token=private", "person.private@example.test", "UNTRUSTED LOCAL ONLY", "explicit private value",
            "ForeignMachineUser", "private account continuation", "PRIVATE MULTILINE PROMPT CONTINUATION", "private-room-ID", "meeting-secret" })
            Need(!exported.Contains(forbidden, StringComparison.OrdinalIgnoreCase), "Log 导出泄露：" + forbidden);
        Need(exported.Contains("durationMs=32831") && exported.Contains("elapsedMs=42800") && exported.Contains("exitCode=1") &&
            exported.Contains("E_ASR_TIMING") && exported.Contains("Ver. M3E"), "诊断导出错误丢失时长、版本或错误码。");
        Need(snapshot.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) && Directory.EnumerateFiles(meeting).Count() == snapshot.Count,
            "Log 导出更改或新增了会议原件。");
        Need(!Directory.Exists(Path.Combine(root, "shareable-log")), "Log 导出意外打包会议文件。");
    }

    private static void ExportSafety(string root)
    {
        string meeting = Meeting(root, "export-safety");
        Need(MeetingLogStore.TryAppend(meeting, "export", "info", "export test").Success, "初始日志失败。");
        ExpectFailure(() => MeetingLogStore.ExportText(meeting, Path.Combine(meeting, "private-transcript.txt")));
        string destination = Path.Combine(root, "existing-log.txt");
        File.WriteAllText(destination, "existing user document");
        ExpectFailure(() => MeetingLogStore.ExportText(meeting, destination));
        Need(File.ReadAllText(destination) == "existing user document", "未经授权覆盖用户现有文件。");
        ExpectFailure(() => MeetingLogStore.ExportText(meeting, Path.Combine(root, "not-a-log.wav")));
        Need(!Directory.EnumerateFiles(root, ".projectecho-log-*.tmp").Any(), "失败导出未清理自己的临时文件。");
        string other = Meeting(root, "other-meeting");
        File.WriteAllText(Path.Combine(other, "meeting.json"), "{}");
        ExpectFailure(() => MeetingLogStore.ExportText(meeting, Path.Combine(other, "original.txt")));
    }

    private static void CorruptMetadataExport(string root)
    {
        string meeting = Meeting(root, "metadata-corruption");
        string manifest = Path.Combine(meeting, "meeting.json");
        const string brokenManifest = "{\"title\":\"PRIVATE DAMAGED TITLE";
        File.WriteAllText(manifest, brokenManifest);
        var timestamp = new DateTimeOffset(2026, 10, 2, 8, 15, 56, TimeSpan.Zero);
        Need(MeetingLogStore.TryAppend(meeting, "diagnostic", "error", "PRIVATE DAMAGED TITLE details",
            "METADATA_INVALID", timestamp, shareableDetails: "PRIVATE DAMAGED TITLE full diagnostics").Success, "损坏清单阻断日志追加。");
        string exported = MeetingLogStore.ExportText(meeting, Path.Combine(root, "damaged-metadata-log.txt"));
        string text = File.ReadAllText(exported);
        Need(text.Contains(timestamp.ToString("O")) && text.Contains("[diagnostic]") && text.Contains("[error]") &&
            text.Contains("METADATA_INVALID") && text.Contains("详情隐私降级省略") && !text.Contains("PRIVATE DAMAGED TITLE"),
            "损坏元数据导出未保留事实或泄露了无法脱敏的详情。");
        Need(File.ReadAllText(manifest) == brokenManifest, "诊断导出改写损坏原清单。");
    }

    private static string Meeting(string root, string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("不安全的Log导出未被拒绝。");
    }
}
