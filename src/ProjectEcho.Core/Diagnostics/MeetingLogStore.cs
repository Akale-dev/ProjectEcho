using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diagnostics;

public sealed record MeetingLogEntry(string EventId, DateTimeOffset TimestampUtc, string Category, string Level,
    string Message, string? EventCode, string? ShareableDetails)
{
    public int SchemaVersion { get; init; } = 1;
}

public sealed record MeetingLogWriteResult(bool Success, string? Error, MeetingLogEntry? Entry);
public sealed record MeetingLogReadResult(IReadOnlyList<MeetingLogEntry> Entries, int InvalidLineCount, string? Error);

/// <summary>
/// Append-only, per-meeting operational history. This is not recording clock evidence and must never be
/// called on the audio callback path. Failures are returned to the caller without failing its main task.
/// </summary>
public static class MeetingLogStore
{
    public const string FileName = "meeting-log.jsonl";
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumLineBytes = 256 * 1024;
    private const int MaximumMessageCharacters = 24 * 1024;
    private static readonly object[] Gates = Enumerable.Range(0, 128).Select(_ => new object()).ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    /// <param name="message">Local diagnostic text only. Never pass transcript words, titles or prompts.</param>
    /// <param name="shareableDetails">Optional, deliberately selected product diagnostic text for export.
    /// Untrusted local messages are omitted from export unless explicitly selected here. Paths, URLs,
    /// local usernames and supplied sensitive values are redacted again during export.</param>
    /// <param name="eventId">Optional stable ID for importing an old event exactly once.</param>
    public static MeetingLogWriteResult TryAppend(string meetingDirectory, string category, string level, string message,
        string? eventCode = null, DateTimeOffset? timestampUtc = null, string? eventId = null, string? shareableDetails = null)
    {
        try
        {
            string path = LogPath(meetingDirectory);
            var entry = new MeetingLogEntry(eventId ?? Guid.NewGuid().ToString("N"),
                (timestampUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(), category, level, message, eventCode, shareableDetails);
            Validate(entry);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
            if (bytes.Length > MaximumLineBytes) throw new InvalidDataException("会议 Log 单条记录过大。");
            lock (Gate(path))
            {
                TranscriptFiles.NoReparse(path);
                // FileShare.Read excludes other-process writers; in-process writers share the same gate.
                // A competing process receives a visible failure instead of overwriting a successful append.
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length > MaximumFileBytes - bytes.Length - 2)
                    throw new InvalidDataException("会议 Log 已达到 64 MiB 上限；本次操作本身不受影响。");
                if (eventId is not null)
                {
                    var previous = ReadStream(stream);
                    var existing = previous.Entries.FirstOrDefault(item => item.EventId == eventId);
                    if (existing is not null) return new(true, null, existing);
                }
                // Preserve a partial/crashed final line byte-for-byte. A delimiter keeps all future events readable.
                if (stream.Length > 0)
                {
                    stream.Position = stream.Length - 1;
                    if (stream.ReadByte() != '\n') { stream.Position = stream.Length; stream.WriteByte((byte)'\n'); }
                }
                stream.Position = stream.Length;
                stream.Write(bytes); stream.WriteByte((byte)'\n'); stream.Flush(true);
                return new(true, null, entry);
            }
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            return new(false, "会议 Log 未能保存：" + error.Message, null);
        }
    }

    /// <summary>Reads valid lines even after isolated corruption. Missing logs are normal for older meetings.</summary>
    public static MeetingLogReadResult Read(string meetingDirectory)
    {
        try
        {
            string path = LogPath(meetingDirectory);
            lock (Gate(path))
            {
                if (!File.Exists(path)) return new([], 0, null);
                TranscriptFiles.NoReparse(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    65536, FileOptions.SequentialScan);
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("会议 Log 超过 64 MiB 读取上限。");
                return ReadStream(stream);
            }
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            return new([], 0, "会议 Log 未能读取：" + error.Message);
        }
    }

    /// <summary>Exports only log events to a separate TXT, never enumerating or packaging meeting files.</summary>
    public static string ExportText(string meetingDirectory, string destinationPath,
        IEnumerable<string>? sensitiveValues = null, bool overwrite = false)
    {
        string meeting = ExistingDirectory(meetingDirectory);
        string destination = TranscriptFiles.LocalPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 TXT 诊断 Log 文件。");
        // Also protect incomplete meetings without a manifest, including this log itself.
        if (destination.StartsWith(meeting + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请将 Log 导出到会议原件目录之外。");
        for (string? parent = Path.GetDirectoryName(destination); parent is not null; parent = Path.GetDirectoryName(parent))
            if (File.Exists(Path.Combine(parent, "meeting.json")) || File.Exists(Path.Combine(parent, FileName)))
                throw new InvalidDataException("请将 Log 导出到会议原件目录之外。");
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Log 输出目录不存在。");
        var history = Read(meeting);
        if (history.Error is not null) throw new IOException(history.Error);
        var redactor = new MeetingLogRedactor(meeting, sensitiveValues);
        var text = new StringBuilder("ProjectEcho Ver. M3F — meeting diagnostic Log\n");
        text.Append("UTC timestamps; schema=1; exportedUtc=").Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("仅包含操作诊断。路径、账号、URL及会议题名已脱敏；未经选择的本机详情省略。\n");
        text.Append("不含音频、转写正文、提示词或会议文件附件。\n");
        if (!redactor.DetailsAvailable)
            text.Append("会议元数据损坏或不可读取，详情隐私降级省略；保留时间、分类、级别及错误码。\n");
        if (history.InvalidLineCount > 0)
            text.Append("损坏或未完成 Log 行：").Append(history.InvalidLineCount).Append("（原文件保留，以下为可读记录）。\n");
        text.Append('\n');
        foreach (var entry in history.Entries.OrderBy(item => item.TimestampUtc))
        {
            text.Append(entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [").Append(entry.Level).Append("] [").Append(entry.Category).Append(']');
            if (entry.EventCode is not null) text.Append(" code=").Append(redactor.Redact(entry.EventCode));
            text.Append('\n').Append(!redactor.DetailsAvailable ? "元数据损坏或不可读取，详情隐私降级省略。" :
                entry.ShareableDetails is null ? "本机详情未选入分享 Log。" : redactor.Redact(entry.ShareableDetails)).Append("\n\n");
        }
        byte[] bytes = Utf8.GetBytes(text.ToString());
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("分享 Log 超过 64 MiB 上限。");
        string temporary = Path.Combine(directory, ".projectecho-log-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            TranscriptFiles.WriteNew(temporary, bytes);
            TranscriptFiles.NoReparse(destination);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    private static MeetingLogReadResult ReadStream(Stream stream)
    {
        stream.Position = 0;
        var entries = new List<MeetingLogEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var line = new MemoryStream();
        var buffer = new byte[65536];
        bool oversized = false;
        int invalid = 0, count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
            for (int i = 0; i < count; i++)
            {
                byte value = buffer[i];
                if (value == '\n') { FinishLine(); continue; }
                if (line.Length < MaximumLineBytes) line.WriteByte(value); else oversized = true;
            }
        if (line.Length > 0 || oversized) FinishLine();
        return new(entries, invalid, null);

        void FinishLine()
        {
            if (line.Length == 0 && !oversized) return;
            try
            {
                if (oversized) throw new InvalidDataException("Log 行超限。");
                var bytes = line.ToArray();
                // Same duplicate-property and strict UTF-8 rules as other authoritative local metadata.
                using var document = TranscriptFiles.ParseJson(bytes);
                var entry = document.RootElement.Deserialize<MeetingLogEntry>(Json)
                    ?? throw new InvalidDataException("Log 行为空。");
                Validate(entry);
                if (!ids.Add(entry.EventId)) throw new InvalidDataException("Log 事件标识重复。");
                entries.Add(entry with { TimestampUtc = entry.TimestampUtc.ToUniversalTime() });
            }
            catch (Exception error) when (IsStorageFailure(error)) { invalid++; }
            finally { line.SetLength(0); oversized = false; }
        }
    }

    private static void Validate(MeetingLogEntry entry)
    {
        if (entry.SchemaVersion != 1 || string.IsNullOrWhiteSpace(entry.EventId) || entry.EventId.Length > 256 ||
            entry.EventId.Any(char.IsControl) || entry.TimestampUtc == default ||
            entry.Category is not ("recording" or "mix" or "export" or "asr" or "edit" or "diagnostic") ||
            entry.Level is not ("info" or "warning" or "error") ||
            string.IsNullOrWhiteSpace(entry.Message) || entry.Message.Length > MaximumMessageCharacters || entry.Message.Contains('\0') ||
            entry.ShareableDetails?.Length > MaximumMessageCharacters || entry.ShareableDetails?.Contains('\0') == true ||
            entry.EventCode is not null && (entry.EventCode.Length is 0 or > 100 ||
                entry.EventCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))))
            throw new InvalidDataException("会议 Log 记录字段无效。");
    }

    private static string ExistingDirectory(string directory)
    {
        string result = Path.TrimEndingDirectorySeparator(TranscriptFiles.LocalPath(directory));
        if (!Directory.Exists(result)) throw new DirectoryNotFoundException("会议目录尚未创建，Log 不会提前创建录音目录。");
        return result;
    }

    private static string LogPath(string directory) => TranscriptFiles.Inside(ExistingDirectory(directory), FileName);
    private static object Gate(string path) => Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)Gates.Length];
    private static bool IsStorageFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or JsonException or System.Security.SecurityException or NotSupportedException;
}
