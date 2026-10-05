using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using ProjectEcho.Core.Diagnostics;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private string? recordingLogDirectory;
    private readonly List<(DateTimeOffset Time, string Message)> pendingRecordingLog = new();
    private readonly Dictionary<string, string> meetingLogErrors = new(StringComparer.OrdinalIgnoreCase);
    private bool refreshingMeetingLog;

    private void LogMeeting(string? directory, string category, string message, string level = "info",
        string? eventId = null, DateTimeOffset? timestamp = null, string? exportSummary = null)
    {
        if (directory is null) return;
        if (category is "recording" or "mix" or "export" or "diagnostic")
        {
            message = PresentRecordingDiagnostic(message);
            if (exportSummary is not null) exportSummary = PresentRecordingDiagnostic(exportSummary);
            if (string.IsNullOrWhiteSpace(message)) return;
        }
        if (message.Length > 16000)
        {
            string id = eventId ?? Guid.NewGuid().ToString("N");
            for (int offset = 0; offset < message.Length; offset += 16000)
                LogMeeting(directory, category, message.Substring(offset, Math.Min(16000, message.Length - offset)), level,
                    id + "-part-" + offset, timestamp, exportSummary);
            return;
        }
        var result = MeetingLogStore.TryAppend(directory, category, level, message,
            timestampUtc: timestamp, eventId: eventId, shareableDetails: exportSummary ?? message);
        if (!result.Success) meetingLogErrors[directory] = result.Error ?? "Log 写入失败。";
        else meetingLogErrors.Remove(directory);
        if (!refreshingMeetingLog && string.Equals(directory, selectedMeetingDirectory, StringComparison.OrdinalIgnoreCase))
            RefreshMeetingLog(importHistory: false);
    }

    // Status belongs to the active capture, never whichever historical meeting is selected.
    // Do not create the directory before CaptureHost's exclusive storage initialization.
    private void LogRecordingStatus(string message)
    {
        if (recordingLogDirectory is not { } directory) return;
        pendingRecordingLog.Add((DateTimeOffset.UtcNow, message));
        if (!Directory.Exists(directory)) return;
        foreach (var entry in pendingRecordingLog)
            LogMeeting(directory, "recording", entry.Message, DiagnosticLevel(entry.Message), timestamp: entry.Time);
        pendingRecordingLog.Clear();
    }

    private static string DiagnosticLevel(string text) =>
        text.Contains("失败", StringComparison.Ordinal) || text.Contains("失敗", StringComparison.Ordinal) || text.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("error", StringComparison.OrdinalIgnoreCase) ? "error" :
        text.Contains("警告", StringComparison.Ordinal) || text.Contains("取消", StringComparison.Ordinal) ||
        text.Contains("warning", StringComparison.OrdinalIgnoreCase) || text.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? "warning" : "info";

    private void RefreshMeetingLog(bool importHistory = true)
    {
        if (MeetingLogText is null || refreshingMeetingLog) return;
        refreshingMeetingLog = true;
        try
        {
            ExportMeetingLogButton.IsEnabled = selectedMeetingDirectory is not null && !deletingMeeting;
            if (selectedMeetingDirectory is not { } directory)
            { MeetingLogText.Text = UiText.T("请先在会议库选择会议。"); return; }
            if (importHistory) ImportLegacyMeetingLogSafe(directory);
            var history = MeetingLogStore.Read(directory);
            MeetingLogText.Text = string.Join("\n\n", history.Entries.OrderBy(entry => entry.TimestampUtc).Select(entry =>
                $"[{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff zzz}] [{entry.Category}/{entry.Level}]\n{entry.Message}"));
            if (history.Entries.Count == 0) MeetingLogText.Text = UiText.T("所选会议暂无历史记录。");
            if (history.InvalidLineCount > 0) MeetingLogText.Text += $"\n\nLog: {history.InvalidLineCount} 行损坏；其余记录已保留。";
            if (history.Error is { } error) MeetingLogText.Text += "\n\n" + error;
            if (meetingLogErrors.TryGetValue(directory, out var writeError)) MeetingLogText.Text += "\n\n" + writeError;
        }
        catch (Exception ex) { MeetingLogText.Text = UiText.T("Log 读取失败：") + ex.Message; }
        finally { refreshingMeetingLog = false; }
    }

    private void RefreshMeetingLog_Click(object sender, RoutedEventArgs e) => RefreshMeetingLog();

    private void RecoveryStatus_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Library-wide notices have no meeting owner. Keep them visible in Diagnostics,
        // without assigning a filter/import/list failure to an unrelated selected meeting.
        if (StatusBox is not null && RecoveryStatus is not null) StatusBox.Text = RecoveryStatus.Text;
    }

    private void ExportMeetingLog_Click(object sender, RoutedEventArgs e)
    {
        if (selectedMeetingDirectory is not { } directory || deletingMeeting) return;
        var dialog = new SaveFileDialog
        {
            Title = UiText.T("导出会议 Log"), Filter = "UTF-8 Log|*.txt", AddExtension = true, DefaultExt = ".txt",
            FileName = "ProjectEcho-Log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportLegacyMeetingLogSafe(directory);
            // Create-new only: a diagnostic export may never overwrite an original or prior export.
            MeetingLogStore.ExportText(directory, dialog.FileName);
            LogMeeting(directory, "export", "会议 Log 已导出（已去除本机路径；不含音频、转写正文或提示词）。");
            MeetingLogExportStatus.Text = UiText.T("Log 已导出：") + dialog.FileName;
        }
        catch (Exception ex)
        {
            LogMeeting(directory, "export", "Log 导出失败：" + ex.Message, "error");
            MeetingLogExportStatus.Text = UiText.T("Log 导出失败，请选择尚不存在的文件名：") + ex.Message;
        }
    }

    // Read only metadata from earlier releases. Never import stdout/stderr, prompts or transcript text.
    private void ImportLegacyMeetingLogSafe(string directory)
    {
        try { ImportLegacyMeetingLog(directory); }
        catch (Exception ex)
        {
            LogMeeting(directory, "diagnostic", "部分历史记录无法读取；已保存的 Log 仍可查看与导出。" + ex.Message,
                "error", eventId: "legacy-import-incomplete", exportSummary: "部分历史元数据无法读取；历史导入不完整，既有 Log 仍保留。");
        }
    }

    private void ImportLegacyMeetingLog(string directory)
    {
        ValidateRecordingDirectory(directory);
        var existing = MeetingLogStore.Read(directory).Entries.Select(entry => entry.EventId).ToHashSet(StringComparer.Ordinal);
        void ImportEvent(string category, string message, string level = "info", string? eventId = null, DateTimeOffset? timestamp = null, string? summary = null)
        {
            if (eventId is not null && !existing.Add(eventId)) return;
            // Legacy free-form text is local-only. Only selected structural data / known errors are shared.
            LogMeeting(directory, category, message, level, eventId, timestamp, summary ?? LegacyDiagnosticSummary(message));
        }
        void ImportFile(string path, Action<JsonElement> import)
        {
            if (!File.Exists(path)) return;
            try { using var document = ReadProcessingJson(path); import(document.RootElement); }
            catch (Exception ex)
            {
                ImportEvent("diagnostic", "历史元数据无法读取：" + ex.Message, "error",
                    "legacy-read-error-" + Path.GetRelativePath(directory, path));
            }
        }
        static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        static DateTimeOffset Time(JsonElement root, string property, string path) =>
            root.TryGetProperty(property, out var value) && value.TryGetDateTimeOffset(out var parsed) ? parsed : File.GetLastWriteTimeUtc(path);
        string manifest = Path.Combine(directory, "meeting.json");
        ImportFile(manifest, root =>
        {
            var start = Time(root, "startedAtUtc", manifest);
            string schema = Text(root, "schemaVersion");
            if (schema is not ("m1-recording-1" or "m3-audio-import-1")) return;
            long duration = root.TryGetProperty("duration100ns", out var durationValue) ? durationValue.GetInt64() : 0;
            if (duration < 0 || duration > TimeSpan.TicksPerDay * 7) throw new InvalidDataException("历史时长无效。");
            bool complete = root.TryGetProperty("storageComplete", out var completeValue) && completeValue.GetBoolean();
            string message = $"历史{(schema == "m1-recording-1" ? "录音保存" : "音频导入")}记录；时长 {duration / 10_000_000d:F3} 秒；StorageComplete={complete}。\n" +
                (root.TryGetProperty("warnings", out var warnings) ? string.Join("\n", warnings.EnumerateArray().Select(w => w.GetString())) : "");
            ImportEvent("recording", message, complete ? "info" : "warning", "legacy-recording-manifest", start.AddTicks(duration),
                $"历史录音/导入时长 {duration / 10_000_000d:F3} 秒；StorageComplete={complete}。\n" + LegacyDiagnosticSummary(message));
        });
        string processing = Path.Combine(directory, ExportHistoryFileName);
        ImportFile(processing, root =>
        {
            // This snapshot is a receipt, not an integrity guarantee. Bind it to its source manifest.
            if (Text(root, "SchemaVersion") != "meeting-processing-result-1" || Text(root, "SourceManifestSha256") != MeetingManifestHash(directory)) return;
            string message = Text(root, "Text"); var time = Time(root, "UpdatedAtUtc", processing);
            ImportEvent("mix", "历史处理记录（非重新校验）：\n" + message, DiagnosticLevel(message), "legacy-processing-" + time.UtcTicks, time);
        });
        foreach (string kind in new[] { "asr", "original", "reviewed" })
        {
            string parent = Path.Combine(directory, "transcripts", kind); RejectReparseAncestors(parent);
            if (!Directory.Exists(parent)) continue;
            var children = Directory.EnumerateDirectories(parent).Take(2001).ToArray();
            if (children.Length > 2000) throw new InvalidDataException("历史转写记录超过 2000 个读取上限。");
            foreach (string child in children)
            {
                RejectReparseAncestors(child);
                string path = Path.Combine(child, kind == "asr" ? "attempt.json" : "revision.json");
                ImportFile(path, root =>
                {
                    string id = "legacy-" + kind + "-" + Path.GetFileName(child);
                    var time = Time(root, "importedAt", path);
                    if (kind == "asr")
                    {
                        string schema = Text(root, "schemaVersion");
                        if (schema is not ("m3-whisper-attempt-1" or "m3-whisper-attempt-2")) return;
                        string status = Text(root, "status"), message = Text(root, "message");
                        string elapsed = root.TryGetProperty("elapsedMilliseconds", out var ms) ? ms.GetInt64().ToString() : "unknown";
                        ImportEvent("asr", $"本地识别 {status}；耗时 {elapsed} ms；硬件 {Text(root, "requestedHardware")}。\n{message}",
                            status == "Failed" ? "error" : status == "Cancelled" ? "warning" : "info", id, time,
                            $"历史本地识别状态 {(status is "Failed" or "Succeeded" or "Cancelled" or "NoSpeech" ? status : "Unknown")}；耗时 {elapsed} ms。\n" + LegacyDiagnosticSummary(message));
                    }
                    else ImportEvent("edit", kind == "reviewed" ? "历史文字修订已保存（保留父版本）。" : "历史转写原稿已保存。", eventId: id, timestamp: time);
                });
            }
        }
    }

    private static string LegacyDiagnosticSummary(string message)
    {
        message = RecordingDiagnosticPresentation.Project(message);
        var selected = new List<string>();
        foreach (string known in new[] { "转写起止时间倒置、越界或质量标记无效。", "原件及索引已完成验证", "未观察到音频包的时间区间",
            "API QPC 存在小幅抖动", "Complete仍为false", "本地转写已取消", "M4A 已生成并完成解码检查", "原始输出保留" })
            if (message.Contains(known, StringComparison.Ordinal)) selected.Add(known);
        selected.Add("旧版自由文本详情保存在本机 Log，分享版仅列已识别诊断；重新执行失败任务可生成新版详细诊断。");
        return string.Join("\n", selected);
    }
}
