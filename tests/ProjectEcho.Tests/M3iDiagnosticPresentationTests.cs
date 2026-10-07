using System.Text.Json;
using ProjectEcho.Core;
using ProjectEcho.Core.Diagnostics;

internal static class M3iDiagnosticPresentationTests
{
    private const string Device = "remote 设备帧计数不可依赖；保留原值，以实际API QPC标记时间。长时/双轨同步未验收。";
    private const string Legacy = "原件及索引已完成验证；旧式完整捕获/设备时钟条件未通过，Complete仍为false。";
    private const string Mix = "混音依据原始 API QPC 和设备帧重建时间；连续设备帧采用有界平稳映射，保留断续与真实缺口。真实两小时双轨同步尚待验收。";
    private const string Neutral = "远端音频使用系统 API 时间戳计时；原始时钟已保留。";
    private const string ClockConcern = "原件和索引已校验；音频时钟完整性仍需检查。";

    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3i-diagnostic-presentation"); Directory.CreateDirectory(root);
        run("M3I-SYNTH successful recording shows neutral process clock details without development acceptance prose", KnownTemplates);
        run("M3I-SYNTH unknown augmented errors real gaps no PCM and disk failures remain verbatim", RealFailures);
        run("M3I-SYNTH legacy Complete concern without known process limitation remains visible", UnexplainedClock);
        run("M3I-SYNTH display projection leaves Complete storage flags original warnings and clock files unchanged", () => Originals(root));
        run("M3I-SYNTH new projected Log appends preserve original legacy Log bytes and history", () => History(root));
    }

    private static void KnownTemplates()
    {
        string raw = "录音原轨已保存。\r\n" + Device + "\r\n" + Legacy + "\r\n" + Mix + "\r\n混音保持原有音量。";
        string visible = RecordingDiagnosticPresentation.Project(raw);
        Need(visible == "录音原轨已保存。\r\n" + Neutral + "\r\n混音保持原有音量。", "没有精准移除开发说明/替换能力说明。");
        Need(RecordingDiagnosticPresentation.Project(visible) == visible, "显示层重复投影改变结果。");
        Need(!visible.Contains("同步已验收") && !visible.Contains("Complete=true"), "显示层伪称同步/Complete通过。");
        foreach (string localized in new[]
        {
            "The mix reconstructs timing from original API QPC timestamps and device frames. Continuous device frames use a bounded, steady mapping while preserving discontinuities and actual gaps. Two-hour synchronization between real tracks remains unverified.",
            "混音依據原始 API QPC 和裝置影格重建時間；連續裝置影格採用有界平穩映射，保留斷續與真實缺口。真實兩小時雙軌同步尚待驗收。"
        }) Need(RecordingDiagnosticPresentation.Project(localized) == "", "旧三语处理记录中的精确已知模板未投影。");
    }

    private static void RealFailures()
    {
        string[] warnings =
        [
            "remote 来源故障；不替换或扩大范围：音频包超过已协商缓冲区。",
            "remote 存在未观察到音频包的时间区间；已保存明确缺口，不当作采集的静音。",
            "remote 设备帧位置异常；原始设备位置与有效 API QPC 保留，完整性降级。",
            "无效API时间戳；有歧义PCM未进入写盘队列。",
            "remote 本段未收到有效音频包，不等于静音。",
            "保存失败，原件保留供恢复：磁盘空间不足。",
            "来源存储标记为不完整/有警告；全时段混音按已确认缺口补静音。",
            "未确认完整保存；不能称为完整录音。",
            "unknown warning: clock error, Complete=false",
            Device + " 另外检测到真实故障。",
            "mic 设备帧计数不可依赖；保留原值，以实际API QPC标记时间。长时/双轨同步未验收。",
            "附加前缀：" + Mix,
            Legacy + " 附加错误代码=123。"
        ];
        foreach (string warning in warnings)
        {
            Need(RecordingDiagnosticPresentation.Project(warning) == warning, "真实或未知诊断被隐藏：" + warning);
            Need(RecordingDiagnosticPresentation.Project(Device + "\n" + warning).EndsWith(warning, StringComparison.Ordinal),
                "已知能力提示遮盖了同场真实异常。");
        }
        string unknown = "未知一\r\n未知二\r未知三\n";
        Need(RecordingDiagnosticPresentation.Project(unknown) == unknown, "未知诊断或换行被重写。");
    }

    private static void UnexplainedClock()
    {
        Need(RecordingDiagnosticPresentation.Project(Legacy) == ClockConcern,
            "没有已知QPC能力说明时，唯一时钟异常被消除。");
        Need(RecordingDiagnosticPresentation.Project("另外的来源错误\n" + Legacy).Contains(ClockConcern),
            "未知来源错误场景丢失时钟待检查信息。");
        Need(RecordingDiagnosticPresentation.Project(Mix) == "", "独立固定开发验收说明仍占据操作结果。");
    }

    private static void Originals(string root)
    {
        var warnings = new[] { Device, Legacy, Mix, "remote 存在真实时间缺口。" };
        var capture = new CaptureResult(root, "synthetic-index.jsonl", DateTimeOffset.UtcNow, 100, 200,
            [], false, warnings);
        string manifest = Path.Combine(root, "original-meeting.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            capture.Complete, storageComplete = true, warnings,
            qpcPosition100ns = 123456789UL, devicePosition = 0UL, devicePositionValid = false
        }));
        string oldProcessing = Path.Combine(root, "old-processing-result.json");
        File.WriteAllText(oldProcessing, JsonSerializer.Serialize(new { Text = string.Join("\n", warnings) }));
        string manifestHash = M3TranscriptTests.Hash(manifest), processingHash = M3TranscriptTests.Hash(oldProcessing);
        _ = RecordingDiagnosticPresentation.Project(string.Join("\n", capture.Warnings));
        using (var parsed = JsonDocument.Parse(File.ReadAllBytes(oldProcessing)))
            _ = RecordingDiagnosticPresentation.Project(parsed.RootElement.GetProperty("Text").GetString()!);
        Need(!capture.Complete && ReferenceEquals(capture.Warnings, warnings) && capture.Warnings.SequenceEqual(warnings) &&
            M3TranscriptTests.Hash(manifest) == manifestHash && M3TranscriptTests.Hash(oldProcessing) == processingHash,
            "显示层修改Complete、原始warnings或旧文件。");
    }

    private static void History(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "log");
        string raw = Device + "\n" + Legacy + "\n" + Mix;
        Need(MeetingLogStore.TryAppend(meeting, "recording", "warning", raw, eventId: "old-raw").Success, "旧日志夹具失败。");
        string path = Path.Combine(meeting, MeetingLogStore.FileName);
        byte[] before = File.ReadAllBytes(path);
        string current = RecordingDiagnosticPresentation.Project(raw);
        Need(MeetingLogStore.TryAppend(meeting, "recording", "info", current, eventId: "new-display", shareableDetails: current).Success,
            "新日志无法追加。");
        byte[] after = File.ReadAllBytes(path);
        var entries = MeetingLogStore.Read(meeting).Entries;
        Need(after.AsSpan(0, before.Length).SequenceEqual(before) && entries.Count == 2 &&
            entries[0].Message == raw && entries[0].Level == "warning" &&
            entries[1].Message.Contains(Neutral) && !entries[1].Message.Contains("未验收") &&
            !entries[1].Message.Contains("Complete仍为false"), "旧日志被重写或新日志保留开发验收噪声。");
    }

    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
}
