using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProjectEcho.Core;
using ProjectEcho.Export;

namespace ProjectEcho.App;

public partial class MainWindow
{
    private bool selectedMeetingImported;

    private void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (!ImportAudioButton.IsEnabled) return;
        NavigateTo(1);
        var picker = new OpenFileDialog
        {
            Title = "导入本地录音（复制原件，不修改源文件）",
            Filter = "音频录音|*.wav;*.mp3;*.m4a", CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        backgroundWork = ImportAudioFromPickerAsync(picker.FileName);
    }

    private async Task ImportAudioFromPickerAsync(string sourcePath)
    {
        try { await ImportAudioFileAsync(sourcePath); }
        catch (OperationCanceledException) { RecoveryStatus.Text = "录音导入已取消；没有发布完成的会议，源文件保留。"; }
        catch (Exception ex) { RecoveryStatus.Text = "录音导入未完成；源文件保留：" + ex.Message; }
    }

    // Shared by the user action and the real WPF integration test; no synthetic meeting metadata.
    private async Task<ImportedAudioMeeting> ImportAudioFileAsync(string sourcePath, CancellationToken token = default)
    {
        if (!SessionRules.CanStart(state) || WindowCloseRequested || exportBusy || transcriptBusy || recoveryBusy)
            throw new InvalidOperationException("请等待当前录音保存或文件任务结束后再导入。");
        exportBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        exportCancellation = cancellation;
        string targetRoot = RecordingsRoot;
        RecoveryStatus.Text = "正在复制原件、读取至音频末尾并生成完整回放文件…可取消。";
        ExportStatus.Text = "正在导入本地录音；不会启动采集或上传。";
        UpdateControls();
        try
        {
            var result = await Task.Run(() => AudioImportService.Import(sourcePath, targetRoot, cancellation.Token), cancellation.Token);
            renderedMeetings[result.MeetingDirectory] = result.Mix;
            if (!WindowCloseRequested && SessionRules.CanStart(state))
            {
                RefreshRecovery_Click(this, new RoutedEventArgs());
                SelectMeeting(result.MeetingDirectory);
                if (RecoveryBox.ItemsSource is IEnumerable<RecordingDirectory> entries)
                    RecoveryBox.SelectedItem = entries.FirstOrDefault(x => x.Path == result.MeetingDirectory);
            }
            RecoveryStatus.Text = $"录音导入完成：{result.Mix.DurationUs / 1_000_000d:F3} 秒。原件副本和完整回放已校验。\n可前往“转写与搜索”导入字幕或运行本地识别。";
            ExportStatus.Text = "外部录音已导入；回放时间从文件 0 秒开始。\n" + string.Join(Environment.NewLine, result.Mix.Warnings);
            return result;
        }
        finally
        {
            exportCancellation = null; exportBusy = false; UpdateControls();
            if (!WindowCloseRequested) await DrainMixQueueAsync();
        }
    }
}
