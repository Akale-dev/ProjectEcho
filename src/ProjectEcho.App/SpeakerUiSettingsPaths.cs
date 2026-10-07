using System.IO;
using ProjectEcho.Core.Diarization;

namespace ProjectEcho.App;

/// <summary>Only bundled paths relocate with the application; deliberately selected external paths keep their meaning.</summary>
internal static class SpeakerUiSettingsPaths
{
    internal static string? DefaultEmbedding(string applicationDirectory)
    {
        string titanet = Path.Combine(applicationDirectory, "Models", "Sherpa", "nemo_en_titanet_large.onnx");
        string eres = Path.Combine(applicationDirectory, "Models", "Sherpa", "3dspeaker-eres2net.onnx");
        return File.Exists(titanet) ? titanet : File.Exists(eres) ? eres : null;
    }

    internal static SherpaDiarizationConfiguration ForStorage(SherpaDiarizationConfiguration value, string applicationDirectory) => value with
    {
        ExecutablePath = Store(value.ExecutablePath, applicationDirectory),
        SegmentationModelPath = Store(value.SegmentationModelPath, applicationDirectory),
        EmbeddingModelPath = Store(value.EmbeddingModelPath, applicationDirectory)
    };

    internal static SherpaDiarizationConfiguration Resolve(SherpaDiarizationConfiguration value, string applicationDirectory) => value with
    {
        ExecutablePath = ResolvePath(value.ExecutablePath, applicationDirectory),
        SegmentationModelPath = ResolvePath(value.SegmentationModelPath, applicationDirectory),
        EmbeddingModelPath = ResolvePath(value.EmbeddingModelPath, applicationDirectory)
    };

    private static string Store(string path, string applicationDirectory)
    {
        string full = ResolvePath(path, applicationDirectory);
        string root = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, full).Replace('\\', '/') : full;
    }

    private static string ResolvePath(string path, string applicationDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new InvalidDataException("说话人设置路径无效。");
        if (Path.IsPathFullyQualified(path))
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
                throw new InvalidDataException("说话人设置只接受本地文件路径。");
            return full;
        }
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Split(['/', '\\']).Any(part => part is "." or ".."))
            throw new InvalidDataException("说话人相对路径不得越过程序目录。");
        string root = Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(root, path));
        if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("说话人相对路径越界。");
        return result;
    }
}
