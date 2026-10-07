using System.Text.Json;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core;

/// <summary>Only the last explicitly selected data location; never ASR settings, prompts or meeting data.</summary>
public static class PortableDataRootPreferences
{
    private sealed record Preference(int SchemaVersion, string DataRoot, bool RelativeToApplication);

    public static string? Load(string preferencePath, string applicationDirectory)
    {
        string path = TranscriptFiles.LocalPath(preferencePath);
        byte[] bytes;
        try { bytes = TranscriptFiles.ReadBounded(path, CancellationToken.None, 16000); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using var document = TranscriptFiles.ParseJson(bytes);
        var saved = document.RootElement.Deserialize<Preference>(TranscriptFiles.Json)
            ?? throw new InvalidDataException("数据目录偏好为空，请重新选择数据目录。");
        if (saved.SchemaVersion != 1 || string.IsNullOrWhiteSpace(saved.DataRoot))
            throw new InvalidDataException("数据目录偏好无效，请重新选择数据目录。");
        if (!saved.RelativeToApplication) return ValidatePath(saved.DataRoot);
        string application = ValidatePath(applicationDirectory);
        return TranscriptFiles.Inside(application, saved.DataRoot);
    }

    public static void Save(string preferencePath, string applicationDirectory, string dataRoot)
    {
        string path = TranscriptFiles.LocalPath(preferencePath);
        string selected = ValidatePath(dataRoot), application = ValidatePath(applicationDirectory);
        if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("所选数据目录不存在，未更改已记住的目录。");
        string relative = Path.GetRelativePath(application, selected);
        bool inside = relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var saved = new Preference(1, inside ? relative.Replace(Path.DirectorySeparatorChar, '/') : selected, inside);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(saved, TranscriptFiles.Json);
        if (bytes.Length > 16000) throw new InvalidDataException("数据目录路径过长。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        TranscriptFiles.NoReparse(path);
        string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            TranscriptFiles.WriteNew(pending, bytes);
            TranscriptFiles.NoReparse(path);
            File.Move(pending, path, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("数据目录必须是完整的本地磁盘路径。");
        return Path.TrimEndingDirectorySeparator(TranscriptFiles.LocalPath(path));
    }
}
