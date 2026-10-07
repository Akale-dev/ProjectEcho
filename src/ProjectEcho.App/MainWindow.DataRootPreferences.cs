using System.IO;
using System.Windows;
using ProjectEcho.Core;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private string? dataRootPreferencePath;
    private string? dataRootUnavailableMessage;
    private string? dataRootPreferenceApplicationDirectory;
    private bool DataRootAvailable => dataRootUnavailableMessage is null;

    // Smoke windows must not read or overwrite a real portable installation's last selection.
    private string InitializeDataRoot(bool loadPreferences, string? preferencePathOverride)
    {
        bool portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.json"));
        string defaultRoot = ValidateLocalDataRoot(Path.Combine(projectRoot, portable ? "Data" : ".local"));
        dataRootPreferenceApplicationDirectory = projectRoot;
        dataRootPreferencePath = preferencePathOverride ?? (loadPreferences
            ? Path.Combine(projectRoot, portable ? "settings" : ".local/settings", "data-root.json") : null);
        if (dataRootPreferencePath is null) return defaultRoot;
        string? remembered = null;
        try
        {
            remembered = PortableDataRootPreferences.Load(dataRootPreferencePath, dataRootPreferenceApplicationDirectory);
            if (remembered is null) return defaultRoot;
            ProbeDataRoot(remembered, create: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            dataRootUnavailableMessage = remembered is null
                ? UiText.T("上次数据目录配置无法读取；请重新选择数据目录。未切换到默认目录。") + "\n" + ex.Message
                : UiText.T("上次数据目录暂不可用；已保留原路径，请连接磁盘后重新选择。未创建或切换到其他目录。") + "\n" + ex.Message;
            return remembered ?? "";
        }
        return remembered;
    }

    private void ShowDataRootState()
    {
        DataRootText.Text = dataRootUnavailableMessage is null ? dataRoot : dataRoot + "\n" + dataRootUnavailableMessage;
        if (dataRootUnavailableMessage is null) return;
        SetValidationIssue(DataRootButton, dataRootUnavailableMessage);
        RecordingValidationText.Text = dataRootUnavailableMessage;
        RecordingValidationText.Visibility = Visibility.Visible;
        RecoveryStatus.Text = dataRootUnavailableMessage;
        SetStatus(dataRootUnavailableMessage);
    }

    private void EnsureDataRootAvailable()
    {
        if (!DataRootAvailable) throw new InvalidOperationException(dataRootUnavailableMessage);
    }

    private static string ProbeDataRoot(string path, bool create)
    {
        string chosen = ValidateLocalDataRoot(path);
        if (create) Directory.CreateDirectory(chosen);
        if (!Directory.Exists(chosen)) throw new DirectoryNotFoundException(UiText.T("所选数据目录已不存在。"));
        // Enumerate only to confirm access; no filenames/content are retained.
        using (var entries = Directory.EnumerateFileSystemEntries(chosen).GetEnumerator()) _ = entries.MoveNext();
        string probe = Path.Combine(chosen, ".projectecho-write-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
        return chosen;
    }

    private void ApplySelectedDataRoot(string path)
    {
        string chosen = ProbeDataRoot(path, create: true);
        // Persist before switching. A save failure leaves the current location and its settings intact.
        if (dataRootPreferencePath is not null)
            PortableDataRootPreferences.Save(dataRootPreferencePath, dataRootPreferenceApplicationDirectory!, chosen);
        CancelStartupLibraryRefresh();
        ResetTranscriptPlayback();
        ClearSavedMixPlaybackRequest();
        savedMixCancellation?.Cancel();
        ClearReplaySubtitles();
        RememberFilingDraft();
        selectedFilingBaseline = null;
        FlushMeetingWhisperPrompt();
        dataRoot = chosen;
        dataRootUnavailableMessage = null;
        ClearRecordingProcessing();
        libraryQuery = new();
        LoadWhisperConfiguration();
        ShowDataRootState();
        selectedMeetingDirectory = null; currentMix = null; mixedPath = null; lastExportDirectory = null;
        selectedDurationDirectory = null; selectedDurationMs = null;
        RefreshSelectedExportStatus();
        RefreshMeetingLog();
        RecoveryBox.ItemsSource = null;
        RecoveryStatus.Text = "";
        libraryRead = false; MeetingCountText.Text = UiText.T("点击会议库读取历史");
        SelectedMeetingText.Text = UiText.T("数据目录已更改；原有文件保留在原位置。");
        SetStatus(UiText.T("已记住所选数据目录；下次启动继续使用。没有移动或删除历史原件。"));
        ClearValidationIssues(DataRootButton, SelectionPanel);
        RefreshRecordingValidationMessage();
        UpdateControls();
    }
}
