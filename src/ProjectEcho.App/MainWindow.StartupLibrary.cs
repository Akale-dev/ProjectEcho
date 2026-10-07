using System.Windows;
using ProjectEcho.Core;
using ProjectEcho.Core.Library;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private Task startupLibraryWork = Task.CompletedTask;
    private CancellationTokenSource? startupLibraryCancellation;
    private int libraryRefreshGeneration;
    internal Task WaitForStartupLibraryAsync() => startupLibraryWork;

    private void CancelStartupLibraryRefresh()
    {
        libraryRefreshGeneration++;
        startupLibraryCancellation?.Cancel();
    }

    private async Task RefreshStartupLibraryAsync()
    {
        if (!DataRootAvailable || WindowCloseRequested) return;
        CancelStartupLibraryRefresh();
        int generation = libraryRefreshGeneration;
        string directory = dataRoot;
        var query = libraryQuery;
        using var cancellation = new CancellationTokenSource();
        startupLibraryCancellation = cancellation;
        MeetingCountText.Text = UiText.T("正在读取会议库…");
        try
        {
            var entries = await Task.Run(() =>
            {
                var index = new MeetingLibraryIndex(directory);
                index.Refresh(cancellation.Token);
                return index.Query(query, cancellation.Token);
            }, cancellation.Token);
            if (WindowCloseRequested || generation != libraryRefreshGeneration || dataRoot != directory || query != libraryQuery) return;
            ApplyLibraryEntries(entries);
            UpdateControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (WindowCloseRequested || generation != libraryRefreshGeneration || dataRoot != directory) return;
            MeetingCountText.Text = UiText.T("会议库读取失败，可点击刷新重试");
            RecoveryStatus.Text = UiText.T("未能列出录音目录：") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(startupLibraryCancellation, cancellation)) startupLibraryCancellation = null;
        }
    }

    private void ApplyLibraryEntries(IReadOnlyList<MeetingLibraryEntry> entries)
    {
        var directories = entries.Select(item =>
            new RecordingDirectory(item.Directory, item.Title, item.Date, item.Imported, item.Project, item.DurationMs, item.Status)).ToArray();
        foreach (var entry in directories) entry.IsRecognizing = IsRecognitionMeeting(entry.Path);
        string? previouslySelected = selectedMeetingDirectory;
        synchronizingMeetingSelection = true;
        try
        {
            RecoveryBox.ItemsSource = directories;
            RecoveryBox.SelectedItem = directories.FirstOrDefault(item => string.Equals(item.Path, previouslySelected, StringComparison.OrdinalIgnoreCase));
        }
        finally { synchronizingMeetingSelection = false; }
        libraryRead = true;
        MeetingCountText.Text = UiText.F($"{directories.Length} 场会议") + (LibraryFilterActive ? UiText.T(" · 已筛选") : "");
        LibraryFilterButton.ToolTip = UiText.T(LibraryFilterActive ? "筛选和排序会议（已筛选）" : "筛选和排序会议");
        RecoveryStatus.Text = UiText.F($"已通过 SQLite 列出 {directories.Length} 场会议；未读取音频。请选择要检查的一场。");
    }
}
