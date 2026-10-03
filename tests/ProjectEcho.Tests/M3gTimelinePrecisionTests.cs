using System.Globalization;
using ProjectEcho.Core;
using ProjectEcho.Core.Diagnostics;
using ProjectEcho.Core.Transcription;

internal static class M3gTimelinePrecisionTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3g-timeline-precision"); Directory.CreateDirectory(root);
        run("T38/T53-SYNTH M3G hundredth display preserves long hours and does not round across boundaries", Display);
        run("T38/T59-SYNTH M3G hundredth UI retains original millisecond subtitle and search/export times", () => PreserveSubtitleTimes(root));
        run("T53-SYNTH M3G event log retains original fractional UTC ticks and displays boundary hundredths", () => PreserveLogTimes(root));
    }

    private static void Display()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Need(TimelineTimestamp.FromMilliseconds(1230) == "00:00:01.23" &&
                TimelineTimestamp.FromMilliseconds(59999) == "00:00:59.99" &&
                TimelineTimestamp.FromMilliseconds(60000) == "00:01:00.00", "百分秒格式、边界或文化分隔符错误。");
            Need(TimelineTimestamp.FromMilliseconds(25 * 3600000L + 1230) == "25:00:01.23", "超过24小时的时间轴回绕。");
            Need(TimelineTimestamp.FromMicroseconds(1_239_999) == "00:00:01.23" &&
                TimelineTimestamp.FromMicroseconds(1_240_000) == "00:00:01.24" &&
                TimelineTimestamp.FromMicroseconds(-1) == "00:00:00.00" &&
                TimelineTimestamp.FromMilliseconds(long.MinValue) == "00:00:00.00", "微秒边界显示发生提前取整或溢出。");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static void PreserveSubtitleTimes(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "hundredths");
        string source = M3TranscriptTests.Input(root, "hundredths.srt",
            "1\n00:00:01,239 --> 00:00:01,379\n同一秒内第一句。\n\n2\n00:00:01,381 --> 00:00:01,499\n同一秒内第二句。\n");
        var revision = TranscriptService.Import(new(meeting, "hundredths", 2000, source, IsSynthetic: true));
        Need(revision.Segments[0].StartMs == 1239 && revision.Segments[1].StartMs == 1381 &&
            TimelineTimestamp.FromMilliseconds(revision.Segments[0].StartMs!.Value) == "00:00:01.23" &&
            TimelineTimestamp.FromMilliseconds(revision.Segments[1].StartMs!.Value) == "00:00:01.38",
            "UI 精度改变了原始毫秒，或同秒字幕不可区分。");
        var hit = TranscriptService.Search(meeting, "第二句").Single();
        Need(hit.StartMs == 1381 && hit.EndMs == 1499, "检索丢失原始毫秒。");
        string exported = Path.Combine(root, "hundredths-export.srt");
        TranscriptSubtitleExporter.Export(meeting, revision.RevisionId, exported);
        var roundtrip = TranscriptService.Import(new(meeting, "hundredths", 2000, exported, IsSynthetic: true));
        Need(roundtrip.Segments.Select(value => (value.StartMs, value.EndMs, value.Text)).SequenceEqual(
            revision.Segments.Select(value => (value.StartMs, value.EndMs, value.Text))), "百分秒界面把持久字幕/导出截成了百分秒或整秒。");
        Need(TranscriptService.Load(meeting, revision.RevisionId).Segments.SequenceEqual(revision.Segments) &&
            M3TranscriptTests.Hash(source) == revision.OriginalSha256, "显示/导出修改了原始版本。");
    }

    private static void PreserveLogTimes(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "events");
        var timestamp = DateTimeOffset.Parse("2026-10-02T12:34:56.1234567Z", CultureInfo.InvariantCulture);
        const long effectiveSessionUs = 1_239_999;
        string message = "已确认标记此刻，生效时间 " + TimelineTimestamp.FromMicroseconds(effectiveSessionUs) + "。";
        var result = MeetingLogStore.TryAppend(meeting, "recording", "info", message,
            timestampUtc: timestamp, shareableDetails: message);
        Need(result.Success, result.Error ?? "会议 Log 写入失败。");
        var entry = MeetingLogStore.Read(meeting).Entries.Single();
        Need(entry.TimestampUtc == timestamp && entry.Message.Contains("00:00:01.23", StringComparison.Ordinal),
            "事件实际 UTC 精度丢失或生效时间仍只有整秒。");
        string export = Path.Combine(root, "events-export.txt");
        MeetingLogStore.ExportText(meeting, export);
        Need(File.ReadAllText(export).Contains("12:34:56.1234567", StringComparison.Ordinal), "诊断导出丢失原始UTC小数精度。");
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
