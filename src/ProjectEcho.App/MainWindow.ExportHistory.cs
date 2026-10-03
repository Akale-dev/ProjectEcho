using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private const string ExportHistoryFileName = "processing-result.json";
    private readonly Dictionary<string, MeetingExportStatus> meetingExportStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> savedMixStatuses = new(StringComparer.OrdinalIgnoreCase);
    private string? activeExportMeetingDirectory;

    // This is a UI receipt, never proof that the source or a derived audio file is intact.
    // Playback still goes through the independent saved-mix validation pipeline.
    private sealed record MeetingExportStatus(string Text, string? ExportDirectory, string? ExportReceipt = null);
    private sealed record SavedExportStatus(string SchemaVersion, string SourceManifestSha256,
        DateTimeOffset UpdatedAtUtc, string Text, string? ExportDirectory, string? ExportReceipt = null, string? ExportReceiptSha256 = null);
    private sealed record ProcessingMixMetadata(string Directory, string WaveSha256, long FrameCount, long DurationUs,
        bool SourceStorageComplete, IReadOnlyList<string> Warnings, bool HistoricalOnly);

    private void RefreshSelectedExportStatus()
    {
        lastExportDirectory = null;
        if (selectedMeetingDirectory is not { } directory)
        {
            ExportStatus.Text = UiText.T("请先在会议库选择会议；处理结果按会议分别保存。");
            return;
        }
        EnsureMeetingExportStatus(directory);
        meetingExportStatuses.TryGetValue(directory, out var result);
        ExportStatus.Text = result?.Text ?? UiText.T("所选会议暂无处理结果；可生成完整混音或导出音频。");
        if (savedMixStatuses.TryGetValue(directory, out string? validation))
            ExportStatus.Text += "\n\n" + validation;
        if (result?.ExportDirectory is { } exportDirectory)
        {
            try { lastExportDirectory = ValidateMeetingExportDirectory(exportDirectory); }
            catch { /* A moved/deleted output must not enable an unrelated Open folder button. */ }
        }
    }

    private void EnsureMeetingExportStatus(string directory)
    {
        if (meetingExportStatuses.ContainsKey(directory)) return;
        string path = Path.Combine(directory, ExportHistoryFileName);
        try
        {
            ValidateRecordingDirectory(directory);
            RejectReparseAncestors(path);
            if (!File.Exists(path)) return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 512_000) throw new InvalidDataException("处理记录大小无效。");
            var saved = JsonSerializer.Deserialize<SavedExportStatus>(stream, new JsonSerializerOptions
            { MaxDepth = 8, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            if (saved is null || saved.SchemaVersion != "meeting-processing-result-1" ||
                string.IsNullOrWhiteSpace(saved.Text) || saved.Text.Length > 60_000 ||
                saved.SourceManifestSha256 != MeetingManifestHash(directory) || saved.UpdatedAtUtc == default)
                throw new InvalidDataException("处理记录不属于当前会议或格式无效。");
            string? exportDirectory = null;
            string? receipt = null;
            if (saved.ExportDirectory is { } relative)
            {
                // Store relative to Data so a complete portable directory can move safely.
                if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(part => part is "." or ".."))
                    throw new InvalidDataException("处理记录的导出目录无效。");
                exportDirectory = ValidateMeetingExportDirectory(Path.Combine(dataRoot, relative), mustExist: false);
                if (saved.ExportReceipt is not { } receiptRelative || Path.IsPathRooted(receiptRelative) ||
                    saved.ExportReceiptSha256 is not { Length: 64 })
                    throw new InvalidDataException("处理记录缺少本会议的导出凭据。");
                receipt = ValidateMeetingExportReceipt(directory, exportDirectory, Path.Combine(directory, receiptRelative), saved.ExportReceiptSha256);
            }
            meetingExportStatuses[directory] = new(UiText.T("上次处理记录（文件仍须独立校验）：\n") + saved.Text, exportDirectory, receipt);
        }
        catch (Exception ex)
        {
            meetingExportStatuses[directory] = new(UiText.T("所选会议的处理记录无法读取；原件保持不变：") + ex.Message, null);
        }
    }

    private void SetMeetingExportStatus(string directory, string text, bool persist = false, string? exportDirectory = null, string? exportReceipt = null)
    {
        string category = string.Equals(directory, activeExportMeetingDirectory, StringComparison.OrdinalIgnoreCase) ? activeProcessingLogCategory :
            exportDirectory is not null || text.Contains("导出", StringComparison.Ordinal) || text.Contains("導出", StringComparison.Ordinal) || text.Contains("匯出", StringComparison.Ordinal) || text.Contains("export", StringComparison.OrdinalIgnoreCase) ? "export" : "mix";
        var statusAt = DateTimeOffset.UtcNow;
        LogMeeting(directory, category, text, DiagnosticLevel(text), persist ? "legacy-processing-" + statusAt.UtcTicks : null, statusAt);
        EnsureMeetingExportStatus(directory);
        var previous = meetingExportStatuses.GetValueOrDefault(directory);
        var result = new MeetingExportStatus(text, exportDirectory ?? previous?.ExportDirectory, exportReceipt ?? previous?.ExportReceipt);
        meetingExportStatuses[directory] = result;
        if (persist)
        {
            string? temporary = null;
            try
            {
                ValidateRecordingDirectory(directory);
                string path = Path.Combine(directory, ExportHistoryFileName);
                RejectReparseAncestors(path);
                string? relative = result.ExportDirectory is null ? null :
                    Path.GetRelativePath(dataRoot, ValidateMeetingExportDirectory(result.ExportDirectory, mustExist: false));
                string? receipt = result.ExportDirectory is null ? null :
                    ValidateMeetingExportReceipt(directory, result.ExportDirectory, result.ExportReceipt ?? "", expectedSha256: null);
                var saved = new SavedExportStatus("meeting-processing-result-1", MeetingManifestHash(directory),
                    statusAt, text.Length <= 60_000 ? text : text[..59_900] + UiText.T("\n[记录过长，详细信息见原处理文件]"), relative,
                    receipt is null ? null : Path.GetRelativePath(directory, receipt), receipt is null ? null : ProcessingFileHash(receipt));
                temporary = Path.Combine(directory, ".processing-result-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, saved); stream.Flush(true); }
                RejectReparseAncestors(path);
                File.Move(temporary, path, true); temporary = null;
            }
            catch (Exception ex)
            {
                meetingExportStatuses[directory] = result with
                { Text = text + "\n" + UiText.T("处理结果仅在本次打开期间保留；记录写入失败：") + ex.Message };
            }
            finally
            {
                if (temporary is not null)
                    try { RejectReparseAncestors(temporary); File.Delete(temporary); } catch { }
            }
        }
        if (string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase)) RefreshSelectedExportStatus();
    }

    private void SetSavedMixStatus(string directory, string text)
    {
        LogMeeting(directory, "diagnostic", text, DiagnosticLevel(text));
        savedMixStatuses[directory] = text;
        if (string.Equals(selectedMeetingDirectory, directory, StringComparison.OrdinalIgnoreCase)) RefreshSelectedExportStatus();
    }

    // Older releases did not save the UI result. Reconstruct only from the selected
    // meeting's independently validated mix and its own source-bound local receipt.
    private void RestoreLegacyProcessingResult(string directory, FullMixResult mix)
        => RestoreLegacyProcessingResult(directory, new ProcessingMixMetadata(mix.Directory, mix.WaveSha256, mix.FrameCount,
            mix.DurationUs, mix.SourceStorageComplete, mix.Warnings, HistoricalOnly: false));

    private void RestoreLegacyProcessingResult(string directory, ProcessingMixMetadata mix)
    {
        if (meetingExportStatuses.ContainsKey(directory)) return;
        string historical = mix.HistoricalOnly ? UiText.T("历史处理记录；旧版音频尚未重新校验，请重新生成混音后回听或导出。\n") : "";
        string receipts = Path.Combine(mix.Directory, "export-records");
        RejectReparseAncestors(receipts);
        if (Directory.Exists(receipts))
        {
            var candidates = Directory.EnumerateFiles(receipts, "export-*.json", SearchOption.TopDirectoryOnly).Take(1025).ToArray();
            if (candidates.Length > 1024) throw new InvalidDataException("本会议的导出记录过多。");
            string? receipt = candidates.OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (receipt is not null)
            {
                string output = ValidateMeetingExportDirectory(Path.Combine(dataRoot, "Exports", Path.GetFileNameWithoutExtension(receipt)), mustExist: false);
                ValidateMeetingExportReceipt(directory, output, receipt, expectedSha256: null);
                using var json = JsonDocument.Parse(File.ReadAllBytes(receipt));
                var saved = json.RootElement.GetProperty("export");
                if (saved.GetProperty("schemaVersion").GetString() != "m2-export-1" ||
                    saved.GetProperty("sourceMixSha256").GetString() != mix.WaveSha256 ||
                    saved.GetProperty("completeTimelineFrames").GetInt64() != mix.FrameCount)
                    throw new InvalidDataException("历史导出记录与已校验混音不一致。");
                var parts = saved.GetProperty("parts");
                if (parts.GetArrayLength() is < 1 or > 10000) throw new InvalidDataException("历史导出分片记录无效。");
                string text = historical + UiText.F($"历史导出记录：{parts.GetArrayLength()} 片；导出文件尚未重新校验。\n") +
                    (mix.SourceStorageComplete ? "" : UiText.T("来源有完整性警告；分片保留经过时间和静音缺口，不代表声音全部录到。\n")) +
                    string.Join("\n", parts.EnumerateArray().Select(part =>
                    {
                        string name = part.GetProperty("file").GetString() ?? "";
                        long bytes = part.GetProperty("byteLength").GetInt64(), start = part.GetProperty("startUs").GetInt64(), end = part.GetProperty("endUs").GetInt64();
                        if (Path.GetFileName(name) != name || name.Length is < 1 or > 200 || bytes <= 0 || start < 0 || end <= start)
                            throw new InvalidDataException("历史导出分片字段无效。");
                        return UiText.F($"{name} · {bytes:N0} 字节 · {start / 1_000_000d:F3}–{end / 1_000_000d:F3} 秒");
                    }));
                if (mix.HistoricalOnly) text += "\n" + string.Join("\n", mix.Warnings.Select(UiText.T));
                SetMeetingExportStatus(directory, text, exportDirectory: output, exportReceipt: receipt);
                return;
            }
        }
        SetMeetingExportStatus(directory, historical + (mix.HistoricalOnly
            ? UiText.F($"历史混音记录时长：{mix.DurationUs / 1_000_000d:F3} 秒。")
            : UiText.F($"所选会议已有完整回放：{mix.DurationUs / 1_000_000d:F3} 秒。")) + "\n" + string.Join("\n", mix.Warnings.Select(UiText.T)));
    }

    private static ProcessingMixMetadata? ReadHistoricalMixMetadata(string directory, CancellationToken token)
    {
        string derived = Path.Combine(directory, "derived");
        RejectReparseAncestors(derived);
        if (!Directory.Exists(derived)) return null;
        var candidates = Directory.EnumerateDirectories(derived, "mix-*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).Length == 36 && Guid.TryParseExact(Path.GetFileName(path)[4..], "N", out _))
            .Where(path => File.Exists(Path.Combine(path, "render.json"))).Take(1025).ToArray();
        if (candidates.Length > 1024) throw new InvalidDataException("完整混音副本过多。");
        string? mix = candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
        if (mix is null) return null;
        token.ThrowIfCancellationRequested();
        using var render = ReadProcessingJson(Path.Combine(mix, "render.json"));
        using var manifest = ReadProcessingJson(Path.Combine(directory, "meeting.json"));
        var r = render.RootElement;
        if (r.GetProperty("schemaVersion").GetString() != "m2-render-1" ||
            manifest.RootElement.GetProperty("schemaVersion").GetString() != "m1-recording-1" ||
            r.GetProperty("renderVersion").GetString() is not ("m2-qpc-mono-2-rf64-truepeak" or
                "m2-qpc-mono-3-mic-gap-preserving" or "m2-qpc-mono-4-device-clock-preserving") ||
            r.GetProperty("timeline").GetString() != "complete_elapsed_qpc_packet_mapping" ||
            r.GetProperty("sourceManifestSha256").GetString() != MeetingManifestHash(directory) ||
            r.GetProperty("sourceIndexSha256").GetString() != ProcessingFileHash(Path.Combine(directory, "journal", "capture-index.jsonl"), token, long.MaxValue))
            throw new InvalidDataException("旧版处理记录格式未知或不属于当前会议与时间索引。");
        long duration = r.GetProperty("duration100ns").GetInt64(), frames = r.GetProperty("frameCount").GetInt64();
        if (duration <= 0 || duration != manifest.RootElement.GetProperty("duration100ns").GetInt64() ||
            r.GetProperty("sampleRate").GetInt32() != 48000 || r.GetProperty("channels").GetInt32() != 1 ||
            frames != checked((long)(((Int128)duration * 48000 + 9_999_999) / 10_000_000)) ||
            r.GetProperty("wave").GetProperty("file").GetString() != "mixed_full.wav")
            throw new InvalidDataException("旧版处理记录的时间轴或文件名无效。");
        string? waveHash = r.GetProperty("wave").GetProperty("sha256").GetString();
        var compressed = r.GetProperty("compressed");
        if (waveHash is not { Length: 64 } || waveHash.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            (compressed.ValueKind != JsonValueKind.Null && compressed.GetProperty("file").GetString() is not ("mixed_full.m4a" or "mixed_full.mp3")))
            throw new InvalidDataException("旧版处理记录的音频字段无效。");
        string[] warnings = r.GetProperty("warnings").EnumerateArray().Select(item => item.GetString() ??
            throw new InvalidDataException("旧版处理提示无效。")).ToArray();
        if (warnings.Length > 1024 || warnings.Sum(item => (long)item.Length) > 60_000)
            throw new InvalidDataException("旧版处理提示超过读取上限。");
        token.ThrowIfCancellationRequested();
        // No audio files are opened, cached or offered for playback by this metadata-only fallback.
        return new(mix, waveHash, frames, duration / 10, r.GetProperty("sourceStorageComplete").GetBoolean(), warnings, HistoricalOnly: true);
    }

    private static JsonDocument ReadProcessingJson(string path)
    {
        RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 8_000_000) throw new InvalidDataException("处理清单大小无效。");
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private string ValidateMeetingExportDirectory(string directory, bool mustExist = true)
    {
        string full = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.Combine(dataRoot, "Exports")), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("export-", StringComparison.Ordinal) || full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
            throw new InvalidDataException("处理记录只能指向当前数据目录中的导出目录。");
        RejectReparseAncestors(full);
        if (mustExist && !Directory.Exists(full)) throw new DirectoryNotFoundException("导出目录已不存在。");
        return full;
    }

    private static string MeetingManifestHash(string directory)
    {
        return ProcessingFileHash(Path.Combine(directory, "meeting.json"));
    }

    private static string ProcessingFileHash(string path, CancellationToken token = default, long maximumLength = 8_000_000)
    {
        RejectReparseAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximumLength) throw new InvalidDataException("会议清单大小无效。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int read;
        while ((read = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ValidateMeetingExportReceipt(string directory, string exportDirectory, string receipt, string? expectedSha256)
    {
        string full = Path.GetFullPath(receipt), derived = Path.GetFullPath(Path.Combine(directory, "derived"));
        var receiptDirectory = new DirectoryInfo(Path.GetDirectoryName(full)!);
        var mixDirectory = receiptDirectory.Parent;
        if (receiptDirectory.Name != "export-records" || mixDirectory is null ||
            !string.Equals(mixDirectory.Parent?.FullName, derived, StringComparison.OrdinalIgnoreCase) ||
            !(mixDirectory.Name.StartsWith("mix-", StringComparison.Ordinal) || mixDirectory.Name == "imported") ||
            Path.GetFileName(full) != Path.GetFileName(exportDirectory) + ".json")
            throw new InvalidDataException("导出凭据不属于所选会议。");
        RejectReparseAncestors(full);
        string hash = ProcessingFileHash(full);
        if (expectedSha256 is not null && hash != expectedSha256) throw new InvalidDataException("导出凭据已改变。");
        using var json = JsonDocument.Parse(File.ReadAllBytes(full), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.GetProperty("schemaVersion").GetString() != "m2-local-export-1" ||
            root.GetProperty("sourceRenderSha256").GetString() != ProcessingFileHash(Path.Combine(mixDirectory.FullName, "render.json")) ||
            Path.GetFileName(root.GetProperty("outputDirectory").GetString()) != Path.GetFileName(exportDirectory))
            throw new InvalidDataException("导出凭据与所选混音或输出目录不一致。");
        return full;
    }
}
