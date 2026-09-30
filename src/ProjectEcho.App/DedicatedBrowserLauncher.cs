using System.Diagnostics;
using System.IO;

namespace ProjectEcho.App;

/// <summary>A launch hint only; it is never proof of the correct audio source.</summary>
public sealed record BrowserLaunchResult(
    int ProcessId,
    DateTimeOffset? StartTimeUtc,
    string ProfileDirectory,
    string Message);

/// <summary>Called only by the user's explicit open-browser action.</summary>
public static class DedicatedBrowserLauncher
{
    public static BrowserLaunchResult LaunchChrome(string projectRoot, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!Path.IsPathFullyQualified(projectRoot))
            throw new ArgumentException(UiText.T("项目目录必须是绝对路径。"), nameof(projectRoot));

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (!Directory.Exists(root) || (!File.Exists(Path.Combine(root, "AGENTS.md")) && !File.Exists(Path.Combine(root, "portable.json"))))
            throw new InvalidOperationException(UiText.T("找不到 ProjectEcho 项目目录。请从项目目录运行样机。"));

        var chromePath = FindInstalledChrome();
        var storage = Path.GetFullPath(dataRoot ?? Path.Combine(root, ".local"));
        for (DirectoryInfo? ancestor = new DirectoryInfo(storage); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.T("数据路径含目录链接；未启动浏览器。"));
        Directory.CreateDirectory(storage);
        var profileBase = Path.Combine(storage, "BrowserProfiles", "Meetings", "Chrome");
        // Each click gets a fresh directory: Chrome must not forward this launch to
        // a previous ProjectEcho browser instance with a reused user-data-dir.
        var profile = Path.Combine(profileBase,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        EnsureProjectLocalDirectory(storage, profile);

        var probeDirectory = Path.Combine(root, "tests");
        var probe = Path.Combine(probeDirectory, "audio_probe.html");
        var canOpenProbe = Directory.Exists(probeDirectory)
            && !IsReparsePoint(probeDirectory)
            && File.Exists(probe)
            && !IsReparsePoint(probe);
        var destination = canOpenProbe ? new Uri(probe).AbsoluteUri : "about:blank";

        var startInfo = new ProcessStartInfo(chromePath)
        {
            UseShellExecute = false,
            WorkingDirectory = root
        };
        startInfo.ArgumentList.Add($"--user-data-dir={profile}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add(destination);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(UiText.T("Chrome 未返回启动结果。请检查组织的应用限制。"));
        DateTimeOffset? startTimeUtc = null;
        try { startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (InvalidOperationException) { /* Startup failure/forwarding must not imply an audio identity. */ }
        catch (System.ComponentModel.Win32Exception) { /* Restricted process information remains unknown. */ }

        var identity = startTimeUtc.HasValue
            ? UiText.F($"启动 PID {process.Id}，UTC {startTimeUtc.Value:O}")
            : UiText.F($"启动 PID {process.Id}（启动时间不可读）");
        return new BrowserLaunchResult(process.Id, startTimeUtc, profile,
            UiText.F($"已请求打开独立 Chrome 实例；{identity}。这只是启动提示，尚未验证音频进程树。")
            + UiText.T("请手动点击合成音播放，再刷新并选择实际音频会话/祖先进程树。")
            + UiText.T("范围是该实例内全部标签页的声音，不是单标签页。未开始录音或启用麦克风。")
            + UiText.T("每次打开使用新的本地资料目录；登录资料留在所选数据目录中，不加入导出。"));
    }

    private static string FindInstalledChrome()
    {
        foreach (var location in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        })
        {
            if (string.IsNullOrWhiteSpace(location)) continue;
            var path = Path.Combine(location, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(UiText.T("未找到已安装的 Chrome。样机不会下载浏览器；可继续测试已选桌面应用音源。"));
    }

    private static void EnsureProjectLocalDirectory(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.T("浏览器资料目录必须位于当前项目内。"));

        // Refuse existing directory links instead of allowing a project path to
        // redirect Chrome profile writes into another project or normal profile.
        for (DirectoryInfo? ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.T("项目路径含目录链接；无法确认浏览器资料写入边界。"));

        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) && IsReparsePoint(current))
                throw new IOException(UiText.T("浏览器资料路径含目录链接；未启动浏览器。"));
            Directory.CreateDirectory(current);
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
