using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Explicit file import and muted WPF playback; never creates a capture client.</summary>
    internal async Task<object> M3ReplaySmokeAsync(string audioPath, string subtitlePath, string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled || MicEnabled.IsChecked == true)
            throw new InvalidOperationException("回放检查必须在未采集状态运行。");
        string sourceAudio = Path.GetFullPath(audioPath), sourceSubtitles = Path.GetFullPath(subtitlePath);
        var sourceHashes = new[] { sourceAudio, sourceSubtitles }.ToDictionary(path => path, PlaybackFixtureHash);
        string folder = Path.GetFullPath(evidence); RejectReparseAncestors(folder); Directory.CreateDirectory(folder);
        string progress = Path.Combine(folder, "replay-stages.jsonl");
        dataRoot = Path.Combine(folder, "isolated-replay-data"); DataRootText.Text = dataRoot;
        Directory.CreateDirectory(dataRoot);
        player.Volume = 0; player.IsMuted = true;
        var measurements = new List<object>();
        string? meetingDirectory = null;
        try
        {
            AppendPlaybackSmokeStage(progress, "external_audio_import_started");
            var imported = await ImportAudioFileAsync(sourceAudio, token);
            meetingDirectory = imported.MeetingDirectory;
            AppendPlaybackSmokeStage(progress, "external_audio_imported", new { imported.MeetingDirectory, imported.Mix.DurationUs });
            string mixHash = PlaybackFixtureHash(imported.Mix.WavePath);
            if (imported.OriginalSha256 != sourceHashes[sourceAudio]) throw new InvalidDataException("导入原件哈希不符。");
            importFile = sourceSubtitles;
            var view = transcriptionView!;
            view.ProviderBox.Text = "本地回放集成验证";
            StartTranscriptWork(ImportTranscriptAsync); await transcriptWork.WaitAsync(token);
            var revision = visibleRevision ?? throw new InvalidDataException("实际字幕导入未成功：" + view.StatusText.Text);
            var starts = revision.Segments.Where(segment => segment.StartMs is { } start && start < imported.Mix.DurationUs / 1000 - 1100)
                .Select(segment => segment.StartMs!.Value).Distinct().Order().ToArray();
            if (starts.Length < 3) throw new InvalidDataException("集成验证需至少三个不同起点、且各点后仍有 1.1 秒音频的实际字幕。");
            long[] positions = [starts[0], starts[starts.Length / 2], starts[^1]];
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            if (player.Source is not null) throw new InvalidDataException("仅导入/展示字幕便自动播放。");
            foreach (long position in positions)
                measurements.Add(await MeasureTranscriptClickAsync("single_click", position, token));
            measurements.Add(await MeasureTranscriptClickAsync("repeat_same_selected_row", positions[^1], token));

            // Stop during the pending Open, then click the same URI again. Late
            // MediaOpened from the disposed session must not override this request.
            ResetTranscriptPlayback();
            ClickTranscriptRow(positions[0]);
            StopPlayback_Click(this, new RoutedEventArgs());
            await Task.Delay(150, token);
            if (pendingTranscriptSeek is not null || player.Source is not null)
                throw new InvalidDataException("停止回听后旧打开事件重新播放。");
            measurements.Add(await MeasureTranscriptClickAsync("after_stop", positions[1], token));

            ResetTranscriptPlayback();
            foreach (long position in new[] { positions[0], positions[^1], positions[1], positions[0], positions[^1] }) ClickTranscriptRow(position);
            measurements.Add(await ObserveTranscriptPositionAsync("rapid_pending_open_last_click_wins", positions[^1], token));

            // Open two separate imported media paths in one dispatcher turn. This
            // exercises obsolete MediaOpened events after the player is replaced.
            var secondImported = await ImportAudioFileAsync(sourceAudio, token);
            await transcriptWork.WaitAsync(token); // A selected meeting now loads its versions automatically.
            importFile = sourceSubtitles; StartTranscriptWork(ImportTranscriptAsync); await transcriptWork.WaitAsync(token);
            var secondRevision = visibleRevision ?? throw new InvalidDataException("第二场字幕未导入。");
            view.SegmentsBox.ItemsSource = new[]
            {
                new TranscriptRow(imported.MeetingDirectory, revision, revision.Segments.First(s => s.StartMs == positions[0]), "回放检查一"),
                new TranscriptRow(secondImported.MeetingDirectory, secondRevision, secondRevision.Segments.First(s => s.StartMs == positions[^1]), "回放检查二")
            };
            foreach (int index in new[] { 0, 1, 0, 1 }) InvokeTranscriptListItem(index);
            measurements.Add(await ObserveTranscriptPositionAsync("rapid_different_media_last_click_wins", positions[^1], token));
            if (!string.Equals(player.Source?.LocalPath, Path.GetFullPath(secondImported.Mix.WavePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("快速跨会议点击最终播放文件错误。");
            SelectMeeting(imported.MeetingDirectory); await LoadTranscriptVersionsAsync(revision.RevisionId);

            // M3b exercises the dedicated listening tab and its actual controls.
            NavigateTo(5);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            if (MeetingTabs.Items.Count != 4 || RecordingBar.IsVisible || !ReplayPlayerBar.IsVisible || ReplaySegmentsBox.Items.Count != revision.Segments.Count)
                throw new InvalidDataException("M3b 四标签/播放器范围/字幕加载错误。");
            var replayRows = ReplaySegmentsBox.Items.Cast<TranscriptRow>().ToArray();
            int replayIndex = Array.FindIndex(replayRows, row => row.Segment.StartMs == positions[0]);
            ReplaySegmentsBox.ScrollIntoView(ReplaySegmentsBox.Items[replayIndex]); UpdateLayout();
            var replayItem = ReplaySegmentsBox.ItemContainerGenerator.ContainerFromIndex(replayIndex) as ListBoxItem
                ?? throw new InvalidDataException("回听页字幕容器未生成。");
            replayItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = replayItem });
            measurements.Add(await ObserveTranscriptPositionAsync("m3b_replay_tab_subtitle_click", positions[0], token));
            PausePlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double pausedAt = player.Position.TotalMilliseconds;
            await Task.Delay(350, token);
            if (!playbackPaused || Math.Abs(player.Position.TotalMilliseconds - pausedAt) > 80)
                throw new InvalidDataException("回听页暂停后播放时间继续推进。");
            ReplayPositionSlider.Value = positions[1] / 1000d;
            UpdatePlaybackPosition();
            var expectedSubtitle = revision.Segments.First(s => s.StartMs == positions[1]).Text;
            if (Math.Abs(player.Position.TotalMilliseconds - positions[1]) > 100 || !CurrentSubtitleText.Text.Contains(expectedSubtitle) || !playbackPaused)
                throw new InvalidDataException("暂停定位没有更新当前字幕或改变了暂停状态。");
            PausePlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(350, token);
            if (playbackPaused || player.Position.TotalMilliseconds < positions[1] + 100)
                throw new InvalidDataException("回听页继续播放未推进。");
            if (PlaybackButton.Content is not System.Windows.Shapes.Path || PausePlaybackButton.Content is not System.Windows.Shapes.Path
                || StopPlaybackButton.Content is not System.Windows.Shapes.Path || string.IsNullOrWhiteSpace(AutomationProperties.GetName(PausePlaybackButton)))
                throw new InvalidDataException("回听控制未保持无文字矢量图标或缺少无障碍名称。");
            double[] expectedRates = [0.5, 0.75, 1, 1.25, 1.5, 2];
            if (PlaybackSpeedBox.Items.Count != expectedRates.Length) throw new InvalidDataException("倍速选项数量不符。");
            PausePlayback_Click(this, new RoutedEventArgs());
            double pauseBeforeRates = player.Position.TotalMilliseconds;
            for (int index = 0; index < expectedRates.Length; index++)
            {
                PlaybackSpeedBox.SelectedIndex = index;
                if (player.SpeedRatio != expectedRates[index] || !playbackPaused)
                    throw new InvalidDataException("倍速选择未应用，或改变了暂停状态。");
            }
            await Task.Delay(250, token);
            if (Math.Abs(player.Position.TotalMilliseconds - pauseBeforeRates) > 80)
                throw new InvalidDataException("暂停时更改倍速启动了播放。");
            var rateMeasurements = new List<object>();
            foreach (int index in new[] { 0, 5 })
            {
                PlaybackSpeedBox.SelectedIndex = index;
                ResetTranscriptPlayback();
                ClickTranscriptRow(positions[0]);
                var ready = Stopwatch.StartNew();
                while (!playbackOpened || player.Position.TotalMilliseconds < positions[0] + 150)
                {
                    if (ready.Elapsed > TimeSpan.FromSeconds(6)) throw new InvalidDataException("倍速实测播放器未启动。");
                    await Task.Delay(40, token);
                }
                if (player.SpeedRatio != expectedRates[index]) throw new InvalidDataException("新媒体会话丢失倍速选择。");
                double firstPosition = player.Position.TotalMilliseconds;
                var rateTimer = Stopwatch.StartNew();
                await Task.Delay(750, token);
                double elapsed = rateTimer.Elapsed.TotalMilliseconds;
                double advanced = player.Position.TotalMilliseconds - firstPosition;
                double expected = elapsed * expectedRates[index];
                if (Math.Abs(advanced - expected) > Math.Max(180, expected * 0.3))
                    throw new InvalidDataException($"实际播放倍速不符：{expectedRates[index]}x，{elapsed:F0} ms 内推进 {advanced:F0} ms。");
                rateMeasurements.Add(new { Rate = expectedRates[index], ElapsedMs = elapsed, AdvancedMs = advanced, ExpectedMs = expected });
            }
            PlaybackSpeedBox.SelectedIndex = 2;
            foreach (int page in new[] { 0, 1, 2, 3, 4, 5 })
            {
                NavigateTo(page); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                if (RecordingBar.IsVisible != (page == 0) || ReplayPlayerBar.IsVisible != (page == 5))
                    throw new InvalidDataException("录音栏或播放器出现在其他页面。");
            }

            foreach (var locked in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping, SessionState.Faulted })
            {
                ResetTranscriptPlayback();
                state = SessionState.Saved; UpdateControls(); ClickTranscriptRow(positions[0]);
                state = locked; UpdateControls();
                ClickTranscriptRow(positions[1]); SeekTranscript(); Playback_Click(this, new RoutedEventArgs());
                await Task.Delay(180, token);
                if (view.SeekButton.IsEnabled || PlaybackButton.IsEnabled || PausePlaybackButton.IsEnabled || PlaybackSpeedBox.IsEnabled || ReplayPositionSlider.IsEnabled || ReplaySegmentsBox.IsEnabled || pendingTranscriptSeek is not null || player.Source is not null)
                    throw new InvalidDataException("录音边界未阻止字幕/旧打开事件回放：" + locked);
                AppendPlaybackSmokeStage(progress, "locked_state_passed", new { State = locked.ToString(), NoMediaSource = true });
            }
            state = SessionState.Saved; UpdateControls();
            measurements.Add(await MeasureTranscriptClickAsync("after_saved", positions[0], token));

            var exportedSubtitles = new List<string>();
            var selectedReplayRevision = await Task.Run(() => TranscriptService.CreateRevision(imported.MeetingDirectory, revision.RevisionId,
                revision.Segments.Select((segment, index) => index == 0 ? segment with { Text = segment.Text + " [回听页所选版本]" } : segment).ToArray(),
                "合成回听导出版本选择检查", token), token);
            SetReplayRevisions(imported.MeetingDirectory, TranscriptService.ListRevisions(imported.MeetingDirectory), selectedReplayRevision.RevisionId);
            await LoadTranscriptVersionsAsync();
            if (ReplayRevisionBox.SelectedItem is not RevisionRow preserved || preserved.Revision.RevisionId != selectedReplayRevision.RevisionId)
                throw new InvalidDataException("读取转写版本覆盖了用户选择的回听版本。");
            view.RevisionBox.SelectedItem = view.RevisionBox.Items.Cast<RevisionRow>().First(row => row.Revision.RevisionId == revision.RevisionId);
            if (visibleRevision?.RevisionId != revision.RevisionId || !ReplayExportSubtitlesButton.IsEnabled)
                throw new InvalidDataException("回听导出检查未保持与转写页不同的版本。");
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string destination = Path.Combine(folder, "full-timeline-subtitles" + extension);
                StartTranscriptWork(() => ExportReplaySubtitlesAsync(destination)); await transcriptWork.WaitAsync(token);
                if (!File.Exists(destination)) throw new InvalidDataException("界面字幕导出未生成文件：" + view.StatusText.Text);
                var roundTrip = await Task.Run(() => TranscriptService.Import(new(imported.MeetingDirectory,
                    Path.GetFileName(imported.MeetingDirectory), (imported.Mix.DurationUs + 999) / 1000, destination, "导出再导入验证"), token), token);
                if (!selectedReplayRevision.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).SequenceEqual(roundTrip.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))))
                    throw new InvalidDataException(extension + " 字幕导出再导入改变了时间或文字。");
                exportedSubtitles.Add(destination);
            }

            // Untimed text remains editable and must not invent an audio position.
            ResetTranscriptPlayback();
            string untimed = Path.Combine(folder, "untimed-replay-check.txt");
            File.WriteAllText(untimed, "回放检查：这条文字没有时间戳。");
            importFile = untimed; StartTranscriptWork(ImportTranscriptAsync); await transcriptWork.WaitAsync(token);
            view.SegmentsBox.SelectedIndex = 0; await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            InvokeTranscriptListItem(0);
            if (view.SeekButton.IsEnabled || ReplayExportSubtitlesButton.IsEnabled || player.Source is not null || pendingTranscriptSeek is not null)
                throw new InvalidDataException("无时间文字启动了回放。");

            // Simulate a fresh window's empty render cache; the meeting and mix are
            // restored from disk, then the preserved subtitle version is reloaded.
            renderedMeetings.Clear(); SelectMeeting(imported.MeetingDirectory);
            await savedMixWork.WaitAsync(token);
            if (currentMix is null || mixedPath is null) throw new InvalidDataException("重新选择会议没有从磁盘恢复回放文件。");
            await LoadTranscriptVersionsAsync(revision.RevisionId);
            measurements.Add(await MeasureTranscriptClickAsync("reopen_from_disk", positions[1], token));

            // Switching meetings while opening must cancel that meeting's request.
            ResetTranscriptPlayback(); ClickTranscriptRow(positions[0]);
            SelectMeeting(imported.MeetingDirectory);
            await Task.Delay(150, token);
            if (pendingTranscriptSeek is not null || player.Source is not null)
                throw new InvalidDataException("重新选择会议未清除待完成回放。");
            await LoadTranscriptVersionsAsync(revision.RevisionId);

            // Keep a reviewable actual WPF rendering of the imported transcript.
            NavigateTo(5);
            ClickTranscriptRow(positions[1]);
            measurements.Add(await ObserveTranscriptPositionAsync("m3b_current_subtitle_screenshot", positions[1], token));
            PausePlayback_Click(this, new RoutedEventArgs()); UpdatePlaybackPosition();
            var images = new List<string>();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(folder, "imported-subtitles-replay.png", 1, images);
            useDarkTheme = true; ApplyVisualTheme(); UpdateNavigation();
            SaveLayoutImage(folder, "imported-subtitles-replay-dark.png", 1, images);
            useDarkTheme = false; ApplyVisualTheme(); UpdateNavigation();
            double originalWidth = Width, originalHeight = Height;
            Width = MinWidth; Height = MinHeight;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            SaveLayoutImage(folder, "imported-subtitles-replay-minimum.png", 1, images);
            Width = originalWidth; Height = originalHeight;
            await CaptureScrolledPageAsync(folder, "imported-subtitles-replay-bottom.png", images);
            bool sourcesUnchanged = sourceHashes.All(pair => PlaybackFixtureHash(pair.Key) == pair.Value)
                && PlaybackFixtureHash(imported.OriginalPath) == imported.OriginalSha256
                && PlaybackFixtureHash(imported.Mix.WavePath) == mixHash;
            if (!sourcesUnchanged || host is not null || microphoneEnabled) throw new InvalidDataException("原件更改或出现采集状态。");
            AppendPlaybackSmokeStage(progress, "replay_checks_completed", new { Measurements = measurements.Count, SourcesUnchanged = true });
            return new
            {
                Status = "PASS", Os = Environment.OSVersion.Version.ToString(), SourceMode = "EXPLICIT FILE IMPORT / MUTED PLAYBACK / NO CAPTURE",
                Audio = sourceAudio, Subtitles = sourceSubtitles, Meeting = imported.MeetingDirectory,
                RevisionId = revision.RevisionId, DurationSeconds = imported.Mix.DurationUs / 1_000_000d,
                Measurements = measurements, RecordingPausedStoppingFaultedGuard = "PASS",
                UntimedDoesNotPlay = true, ReopenedFromDisk = true, MeetingSelectionCancelsPendingPlayback = true,
                SubtitleExportRoundTrip = "PASS SRT/VTT; exact millisecond times and text", ExportedSubtitles = exportedSubtitles,
                ReplayExportsSelectedRevision = true, PlaybackIconsWithAccessibleNames = true,
                PlaybackRates = expectedRates, PlaybackRateMeasurements = rateMeasurements, RateChangeWhilePausedDoesNotPlay = true,
                M3bDedicatedReplay = "PASS routed click, pause, seek while paused, current subtitle, resume, scoped bars, four tabs, privacy guards",
                SourceHashes = sourceHashes, SourcesUnchanged = true, ActualWpfRoutedMouseEvent = true,
                PhysicalMouseAutomation = "NOT_RUN; real ListBoxItem routed input event exercised in-process",
                PlayerMuted = true, SystemVolumeChanged = false, MicrophoneCollected = false, CaptureHostStarted = false, Uploaded = false,
                Images = images
            };
        }
        catch (Exception ex)
        {
            AppendPlaybackSmokeStage(progress, "replay_failure", new { Error = ex.ToString(), Meeting = meetingDirectory, Measurements = measurements });
            throw;
        }
        finally { ResetTranscriptPlayback(); state = SessionState.Idle; UpdateControls(); }
    }

    private async Task<object> MeasureTranscriptClickAsync(string scenario, long milliseconds, CancellationToken token)
    {
        ClickTranscriptRow(milliseconds);
        return await ObserveTranscriptPositionAsync(scenario, milliseconds, token);
    }

    private async Task<object> ObserveTranscriptPositionAsync(string scenario, long milliseconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (pendingTranscriptSeek is not null || !playbackOpened || Math.Abs(player.Position.TotalMilliseconds - milliseconds) > 500)
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(8))
                throw new InvalidDataException($"{scenario}: 定位未到达 {milliseconds} ms，实际 {player.Position.TotalMilliseconds:F0} ms；{transcriptionView!.StatusText.Text}");
            await Task.Delay(20, token);
        }
        if (!player.HasAudio || player.Volume != 0 || !player.IsMuted || !transcriptOwnsPlayer)
            throw new InvalidDataException(scenario + ": 实际播放器未打开音频或未保持静音。");
        var startupSamples = new List<object>();
        // MediaOpened means the decoder has metadata; Windows may still be
        // starting its audio clock. Require real progress within a bounded wait,
        // then independently prove that playback keeps advancing afterwards.
        var openedTimer = Stopwatch.StartNew();
        while (player.Position.TotalMilliseconds < milliseconds + 150)
        {
            startupSamples.Add(new { AfterOpenedMs = openedTimer.Elapsed.TotalMilliseconds, PositionMs = player.Position.TotalMilliseconds });
            if (openedTimer.Elapsed > TimeSpan.FromSeconds(4))
                throw new InvalidDataException(scenario + ": 打开后音频时钟未启动；" + JsonSerializer.Serialize(new { Samples = startupSamples,
                    Source = player.Source?.LocalPath, Status = transcriptionView!.StatusText.Text, transcriptOwnsPlayer, playbackOpened, Pending = pendingTranscriptSeek }));
            await Task.Delay(50, token);
        }
        double startupWait = openedTimer.Elapsed.TotalMilliseconds;
        double first = player.Position.TotalMilliseconds;
        await Task.Delay(360, token);
        double second = player.Position.TotalMilliseconds;
        if (second - first < 150 || second - first > 1500)
            throw new InvalidDataException($"{scenario}: 定位后没有正常继续播放（{first:F0} → {second:F0} ms）。");
        return new { Scenario = scenario, RequestedMs = milliseconds, FirstPositionMs = first, LaterPositionMs = second,
            AdvancedMs = second - first, StartupWaitMs = startupWait, StartupSamples = startupSamples,
            MediaOpened = playbackOpened, HasAudio = player.HasAudio, NaturalDurationMs = player.NaturalDuration.TimeSpan.TotalMilliseconds };
    }

    private void ClickTranscriptRow(long milliseconds)
    {
        var view = transcriptionView!;
        int index = Enumerable.Range(0, view.SegmentsBox.Items.Count)
            .First(i => view.SegmentsBox.Items[i] is TranscriptRow row && row.Segment.StartMs == milliseconds);
        InvokeTranscriptListItem(index);
    }

    private void InvokeTranscriptListItem(int index)
    {
        var list = transcriptionView!.SegmentsBox;
        list.ScrollIntoView(list.Items[index]); UpdateLayout();
        var item = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem
            ?? throw new InvalidDataException("实际字幕行容器尚未生成。");
        item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = item });
    }
}
