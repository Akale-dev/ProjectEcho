using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diagnostics;

/// <summary>Defense in depth for explicitly selected diagnostic text, not a classifier for private prose.</summary>
internal sealed class MeetingLogRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly string[] sensitive;
    internal bool DetailsAvailable { get; private set; } = true;

    internal MeetingLogRedactor(string meetingDirectory, IEnumerable<string>? sensitiveValues)
    {
        var values = new List<string> { meetingDirectory, Environment.UserName, Environment.MachineName };
        if (sensitiveValues is not null) values.AddRange(sensitiveValues);
        // Read only these two bounded metadata files. Never read transcript, audio, prompt or profile files.
        foreach (string name in new[] { "meeting.json", "filing.json" })
        {
            try
            {
                string path = TranscriptFiles.Inside(meetingDirectory, name);
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                using var document = TranscriptFiles.ParseJson(TranscriptFiles.ReadBounded(path, default, 2_000_000));
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("会议题名元数据格式异常。");
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name.Equals("title", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("project", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.String) values.Add(property.Value.GetString()!);
                        else if (property.Value.ValueKind != JsonValueKind.Null) throw new InvalidDataException("会议题名字段格式异常。");
                    }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                JsonException or System.Security.SecurityException or NotSupportedException)
            {
                // A damaged meeting is precisely when users need an export. Keep event times/categories/codes,
                // but withhold all free-form details if title redaction cannot be established from disk.
                DetailsAvailable = false;
            }
        }
        sensitive = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length).ToArray();
    }

    internal string Redact(string text)
    {
        // A private field may contain continuation lines. Omit the remainder of that diagnostic block
        // instead of assuming the next unlabelled line is safe. Do not rely on the current machine's
        // username when exporting a meeting copied from another computer.
        text = Replace(text, @"(?ims)^\s*(?:title|meeting\s*title|project|transcript|text|body|prompt|initial[ _-]?prompt|user(?:[ _-]?name)?|account(?:[ _-]?name)?|computer(?:[ _-]?name)?|会议标题|标题|项目|转写正文|字幕正文|正文|文本|提示词|会议链接|用户名|账户|账号|计算机名)\s*[:=：].*$", "[private field redacted]");
        text = Replace(text, @"(?i)\b[a-z][a-z0-9+.-]{1,30}://[^\s<>""']+", "[URL]");
        // Quoted paths and unquoted Windows paths may contain spaces. Redacting the remainder of the
        // path's line is intentional: keeping a basename can disclose meeting titles and usernames.
        text = Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n""<>|]*", "[local path]");
        text = Replace(text, @"(?<![\w:])(?:/|\.{1,2}[\\/]|~[\\/])(?:[^\s<>""']+[\\/])+[^\s<>""']*", "[local path]");
        text = Replace(text, @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[email]");
        foreach (string value in sensitive) text = text.Replace(value, "[private]", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string Replace(string value, string pattern, string replacement) =>
        Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);
}
