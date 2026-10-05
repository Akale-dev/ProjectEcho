using System.Security.Cryptography;
using System.Text.Json;

namespace ProjectEcho.Export;

internal static class M2FileSafety
{
    internal static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("导出需要明确的绝对路径。");
        string result = Path.GetFullPath(path);
        string root = Path.GetPathRoot(result)!;
        if (result.AsSpan(root.Length).Contains(':')) throw new InvalidDataException("拒绝备用数据流路径。");
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("当前导出仅写入本地磁盘；网络路径不属于本地导出。");
        NoReparse(result);
        return result;
    }

    internal static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.TrimEnd(' ', '.') != p || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("拒绝不安全导出相对路径。");
        string absolute = Path.GetFullPath(Path.Combine(FullPath(root), relative));
        if (!IsInside(root, absolute)) throw new InvalidDataException("导出路径越界。");
        NoReparse(absolute);
        return absolute;
    }

    internal static bool IsInside(string root, string candidate) => Path.GetFullPath(candidate).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void NoReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝重解析点导出路径。");
    }

    internal static FileStream LockedRead(string path)
    {
        NoReparse(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
    }

    internal static string Hash(Stream stream, CancellationToken cancellationToken) => Hash(stream, cancellationToken, null);

    internal static string Hash(Stream stream, CancellationToken cancellationToken, Action<long, long>? progress)
    {
        long saved = stream.Position; stream.Position = 0;
        long processed = 0, total = stream.Length;
        void Notify() { try { progress?.Invoke(processed, total); } catch (Exception) { } }
        Notify();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[65536]; int count;
        try
        {
            while ((count = stream.Read(bytes)) != 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(bytes, 0, count); processed += count; Notify(); }
            cancellationToken.ThrowIfCancellationRequested(); Notify();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { stream.Position = saved; }
    }

    internal static string Hash(string path, CancellationToken token)
    { using var file = LockedRead(path); return Hash(file, token); }

    internal static string Hash(string path, CancellationToken token, Action<long, long>? progress)
    { using var file = LockedRead(path); return Hash(file, token, progress); }

    internal static void AtomicJson(string path, object data, CancellationToken token)
    {
        NoReparse(path); string partial = path + ".partial";
        using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, data, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
        token.ThrowIfCancellationRequested(); NoReparse(path); File.Move(partial, path, false);
    }

    internal static void RequireDiskSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < checked(bytes + 16L * 1024 * 1024))
            throw new IOException("可用空间不足以完成混音及临时文件；原轨保持不变。");
    }

    // Only individual files explicitly created by this operation are eligible for cleanup.
    internal static void DeleteGenerated(string root, string path)
    {
        if (!IsInside(root, path)) throw new InvalidDataException("拒绝删除导出工作目录以外文件。");
        NoReparse(path); if (File.Exists(path)) File.Delete(path);
    }
}
