using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectEcho.Core.Transcription;

internal static class TranscriptFiles
{
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    internal const int MaximumSegments = 50000;
    internal const int MaximumSegmentCharacters = 100000;
    internal const long MaximumDurationMs = 7L * 24 * 60 * 60 * 1000;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 32
    };

    internal static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("必须选择本地文件或会议目录。");
        string full = Path.GetFullPath(path);
        string driveRoot = Path.GetPathRoot(full)!;
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(driveRoot).DriveType == DriveType.Network ||
            full.AsSpan(driveRoot.Length).Contains(':'))
            throw new InvalidDataException("转写仅使用本地磁盘，不接受网络路径或备用数据流。");
        NoReparse(full);
        return full;
    }

    internal static void NoReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("转写不接受重解析点路径。");
    }

    internal static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("不安全的转写文件相对路径。");
        string result = Path.GetFullPath(Path.Combine(LocalPath(root), relative));
        if (!result.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("转写路径越界。");
        NoReparse(result); return result;
    }

    internal static string Meeting(string directory)
    {
        string root = LocalPath(directory);
        if (!Directory.Exists(root) || !File.Exists(Inside(root, "meeting.json")))
            throw new InvalidDataException("请选择已有 meeting.json 的已保存会议目录。");
        return root;
    }

    internal static FileStream ReadLock(string path) => new(LocalPath(path), FileMode.Open, FileAccess.Read,
        FileShare.Read, 65536, FileOptions.SequentialScan);

    internal static byte[] ReadBounded(string path, CancellationToken token, int maximumBytes = MaximumFileBytes)
    {
        using var file = ReadLock(path);
        if (file.Length > maximumBytes) throw new InvalidDataException("转写或映射文件超过大小限制（导入为 8 MiB）。");
        var result = new byte[checked((int)file.Length)]; file.ReadExactly(result); token.ThrowIfCancellationRequested(); return result;
    }

    internal static string Decode(byte[] bytes)
    {
        // Strict UTF-8 (optional BOM) or BOM-marked UTF-16; never silently replace invalid bytes.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        int skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        string text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
        if (text.Contains('\0')) throw new InvalidDataException("文本包含 NUL，不能作为转写导入。");
        return text;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string Hash(Stream file, CancellationToken token)
    {
        long position = file.Position; file.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int read;
        try { while ((read = file.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); } return Convert.ToHexStringLower(hash.GetHashAndReset()); }
        finally { file.Position = position; }
    }

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        NoReparse(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(true);
    }

    internal static void WriteJsonNew(string path, object value) => WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    internal static JsonDocument ParseJson(byte[] bytes)
    {
        var document = JsonDocument.Parse(Decode(bytes), new JsonDocumentOptions { MaxDepth = 32 });
        try { CheckDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new InvalidDataException("JSON 包含重复属性。"); CheckDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }

    internal static void ValidateIdentity(string meetingId, long durationMs)
    {
        if (string.IsNullOrWhiteSpace(meetingId) || meetingId.Length > 200 || durationMs <= 0 || durationMs > MaximumDurationMs)
            throw new InvalidDataException("会议标识或时长无效（转写上限为七天）。");
    }

    internal static void ValidateSegments(IReadOnlyList<TranscriptSegment> segments, long durationMs, bool allowEmpty = false)
    {
        if (segments is null || segments.Count > MaximumSegments || (!allowEmpty && segments.Count == 0)) throw new InvalidDataException("转写为空或超过五万段。");
        var ids = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var segment in segments)
        {
            if (segment is null || string.IsNullOrWhiteSpace(segment.SegmentId) || segment.SegmentId.Length > 200 || !ids.Add(segment.SegmentId) ||
                string.IsNullOrWhiteSpace(segment.Text) || segment.Text.Length > MaximumSegmentCharacters || segment.Text.Contains('\0') ||
                segment.SpeakerLabel?.Length > 200 || segment.SourceKind is not ("remote" or "microphone" or "mixed" or "unknown"))
                throw new InvalidDataException("转写段标识、文本或来源无效。");
            total += segment.Text.Length;
            if (total > MaximumFileBytes / 2) throw new InvalidDataException("转写总字符数超限。");
            if (segment.StartMs is null || segment.EndMs is null)
            {
                if (segment.StartMs is not null || segment.EndMs is not null || segment.TimestampQuality != "untimed")
                    throw new InvalidDataException("无时间段必须同时省略起止时间并标记 untimed。");
            }
            else if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs)
                throw new InvalidDataException($"转写段 {segment.SegmentId} 起止时间无效：{segment.StartMs}–{segment.EndMs} ms；开始须非负，结束须晚于开始。");
            else if (segment.EndMs > durationMs)
                throw new InvalidDataException($"转写段 {segment.SegmentId} 结束时间 {segment.EndMs} ms 超过完整会议时长 {durationMs} ms（超出 {segment.EndMs - durationMs} ms）。");
            else if (segment.TimestampQuality is not ("approximate" or "verified"))
                throw new InvalidDataException($"转写段 {segment.SegmentId} 的时间质量标记无效；定时段须为 approximate 或 verified。");
        }
    }
}
