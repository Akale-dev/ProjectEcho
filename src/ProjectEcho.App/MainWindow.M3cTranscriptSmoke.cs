using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ProjectEcho.Core;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.App;

public partial class MainWindow
{
    /// <summary>Actual WPF synthetic import, muted playback and clipboard checks; never starts capture.</summary>
    internal async Task<object> M3cTranscriptSmokeAsync(string evidence, CancellationToken token)
    {
        if (host is not null || microphoneEnabled) throw new InvalidOperationException("测试必须在无采集状态运行。");
        string folder = Path.GetFullPath(evidence); Directory.CreateDirectory(folder);
        string wave = Path.Combine(folder, "m3c-synthetic.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            const int samples = 16000 * 30;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(samples * 2 + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 1000));
        }
        string captions = Path.Combine(folder, "m3c-synthetic.srt"); var text = new StringBuilder();
        for (int i = 0; i < 100; i++)
            text.Append(i + 1).Append('\n').Append(TimeSpan.FromMilliseconds(i * 250).ToString(@"hh\:mm\:ss\,fff"))
                .Append(" --> ").Append(TimeSpan.FromMilliseconds(i * 250 + 200).ToString(@"hh\:mm\:ss\,fff"))
                .Append('\n').Append($"合成句子 {i + 1} · English / 繁體").Append("\n\n");
        File.WriteAllText(captions, text.ToString());
        string waveHash = PlaybackFixtureHash(wave);
        dataRoot = Path.Combine(folder, "isolated-data"); Directory.CreateDirectory(dataRoot); DataRootText.Text = dataRoot;
        player.Volume = 0; player.IsMuted = true;
        var images = new List<string>();
        IDataObject? previousClipboard = Clipboard.GetDataObject();
        try
        {
            var imported = await ImportAudioFileAsync(wave, token);
            var view = transcriptionView!;
            await transcriptWork.WaitAsync(token);
            importFile = captions; StartTranscriptWork(ImportTranscriptAsync); await transcriptWork.WaitAsync(token);
            var revision = visibleRevision ?? throw new InvalidDataException(view.StatusText.Text);
            NavigateTo(5); await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var automaticSubtitleStarts = new HashSet<long>();
            void ObserveAutomaticSubtitle(object sender, SelectionChangedEventArgs args)
            {
                if (playbackOpened && !playbackPaused && ReplaySegmentsBox.SelectedItem is TranscriptRow { Segment.StartMs: { } start })
                    automaticSubtitleStarts.Add(start);
            }
            ReplaySegmentsBox.SelectionChanged += ObserveAutomaticSubtitle;
            PlayListeningAndWait();
            var ready = Stopwatch.StartNew();
            while (!playbackOpened || player.Position.TotalMilliseconds < 100)
            { M3cNeed(ready.Elapsed < TimeSpan.FromSeconds(8), "播放器没有开始推进。"); await Task.Delay(30, token); }
            await Task.Delay(250, token);
            var sampleTimer = Stopwatch.StartNew(); var samples = new List<(double At, double Slider, double Media)>();
            void Sample(object sender, RoutedPropertyChangedEventArgs<double> args)
            { if (Math.Abs(args.NewValue - args.OldValue) > 0.00001) samples.Add((sampleTimer.Elapsed.TotalMilliseconds, args.NewValue * 1000, player.Position.TotalMilliseconds)); }
            ReplayPositionSlider.ValueChanged += Sample;
            await Task.Delay(2500, token);
            ReplayPositionSlider.ValueChanged -= Sample;
            ReplaySegmentsBox.SelectionChanged -= ObserveAutomaticSubtitle;
            double measuredSeconds = sampleTimer.Elapsed.TotalSeconds;
            double distinctChangesPerSecond = samples.Count / measuredSeconds;
            File.WriteAllText(Path.Combine(folder, "slider-samples.json"), JsonSerializer.Serialize(new
            { SampleSeconds = measuredSeconds, DistinctChangesPerSecond = distinctChangesPerSecond,
                Samples = samples.Select(s => new { s.At, s.Slider, s.Media }).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            M3cNeed(distinctChangesPerSecond >= 24, $"播放条有效位置变化仍不足24次/秒：{distinctChangesPerSecond:F2}。");
            M3cNeed(samples.Zip(samples.Skip(1)).All(pair => pair.Second.Slider >= pair.First.Slider - 2), "连续播放进度倒退。");
            M3cNeed(automaticSubtitleStarts.Count >= 6, "自然播放未自动选中后续字幕句子。");
            var snapshot = new { SampleSeconds = measuredSeconds, DistinctSliderChanges = samples.Count, DistinctChangesPerSecond = distinctChangesPerSecond,
                MaximumSliderMediaDifferenceMs = samples.Max(sample => Math.Abs(sample.Slider - sample.Media)), Samples = samples.Select(s => new { s.At, s.Slider, s.Media }).ToArray() };

            PausePlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            double paused = ReplayPositionSlider.Value;
            await Task.Delay(250, token); M3cNeed(playbackPaused && Math.Abs(ReplayPositionSlider.Value - paused) < 0.08, "暂停后滑块继续推进。");
            var subtitleFollowing = await ReplayFollowingSmokeAsync(imported.MeetingDirectory, revision, token);
            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            view.PlaybackPositionSlider.Value = 6;
            M3cNeed(playbackPaused && Math.Abs(player.Position.TotalSeconds - 6) < 0.1 && Math.Abs(ReplayPositionSlider.Value - 6) < 0.1, "校订播放条定位没有共享媒体时钟。");
            view.PlaybackSpeedBox.SelectedIndex = 5;
            M3cNeed(player.SpeedRatio == 2 && PlaybackSpeedBox.SelectedIndex == 5 && playbackPaused, "校订倍速没有同步或误启动。");
            view.PauseListeningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var resumeReady = Stopwatch.StartNew();
            while (player.Position.TotalMilliseconds < 6150)
            { M3cNeed(resumeReady.Elapsed < TimeSpan.FromSeconds(5), "校订2倍速定位后没有开始播放。"); await Task.Delay(30, token); }
            double beforeRate = player.Position.TotalMilliseconds; var speedTimer = Stopwatch.StartNew(); await Task.Delay(750, token);
            double rateAdvance = player.Position.TotalMilliseconds - beforeRate;
            M3cNeed(!playbackPaused && Math.Abs(rateAdvance - speedTimer.Elapsed.TotalMilliseconds * 2) < Math.Max(180, speedTimer.Elapsed.TotalMilliseconds * 0.6),
                $"校订2倍速没有真实推进：{speedTimer.Elapsed.TotalMilliseconds:F1}ms 内推进 {rateAdvance:F1}ms。");
            view.PauseListeningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.PlaybackSpeedBox.SelectedIndex = 2;

            view.SegmentsBox.SelectedIndex = 3;
            string selectedText = ((TranscriptRow)view.SegmentsBox.SelectedItem).Segment.Text;
            view.CopySegmentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            M3cNeed(Clipboard.GetText() == selectedText, "复制按钮包含时间戳或其他行。");
            NavigateTo(5); ReplaySegmentsBox.SelectedIndex = 5; CopyReplaySegment_Click(this, new RoutedEventArgs());
            M3cNeed(Clipboard.GetText() == ((TranscriptRow)ReplaySegmentsBox.SelectedItem).Segment.Text, "回听复制错误。");
            string reviewExport = Path.Combine(folder, "review-full.txt"), replayExport = Path.Combine(folder, "replay-full.txt");
            StartTranscriptWork(() => ExportTranscriptTextAsync(reviewExport)); await transcriptWork.WaitAsync(token);
            StartTranscriptWork(() => ExportReplayTextAsync(replayExport)); await transcriptWork.WaitAsync(token);
            M3cNeed(File.ReadAllText(reviewExport) == File.ReadAllText(replayExport) && !File.ReadAllText(reviewExport).Contains(" --> "), "全文导出两处不一致或带时间。");

            NavigateTo(3); view.WorkspaceTabs.SelectedIndex = 1; Width = MinWidth; Height = MinHeight;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(80, token); // Let the constrained list settle after the window's native resize.
            Point statusBefore = view.StatusText.TranslatePoint(new Point(), view), playerBefore = view.PlaybackPositionSlider.TranslatePoint(new Point(), view);
            view.SegmentsBox.ScrollIntoView(view.SegmentsBox.Items[^1]); view.ReviewBodyScroller.ScrollToEnd();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            await Task.Delay(80, token); view.ReviewBodyScroller.ScrollToEnd();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            Point statusAfter = view.StatusText.TranslatePoint(new Point(), view), playerAfter = view.PlaybackPositionSlider.TranslatePoint(new Point(), view);
            File.WriteAllText(Path.Combine(folder, "footer-layout.json"), JsonSerializer.Serialize(new
            { StatusBefore = statusBefore.ToString(), StatusAfter = statusAfter.ToString(), PlayerBefore = playerBefore.ToString(), PlayerAfter = playerAfter.ToString(),
                ViewHeight = view.ActualHeight, StatusHeight = view.StatusText.ActualHeight, ListHeight = view.SegmentsBox.ActualHeight }, new JsonSerializerOptions { WriteIndented = true }));
            SaveLayoutImage(folder, "m3c-review-minimum.png", 1, images);
            M3cNeed(statusBefore == statusAfter && playerBefore == playerAfter && playerAfter.Y < statusAfter.Y &&
                statusAfter.Y + view.StatusText.ActualHeight <= view.ActualHeight + 1 && view.SegmentsBox.ActualHeight >= 70,
                "最小窗口字幕滚动改变了固定状态/播放器，或内容超出视口。");
            Point exportPosition = view.ExportTextButton.TranslatePoint(new Point(), view.ReviewBodyScroller);
            M3cNeed(exportPosition.Y >= -1 && exportPosition.Y + view.ExportTextButton.ActualHeight <= view.ReviewBodyScroller.ActualHeight + 1,
                "最小窗口滚动到底后仍无法触达校订导出按钮。");
            M3cNeed(((TabItem)view.WorkspaceTabs.Items[^1]).Header?.ToString()?.Contains("导入") == true, "导入文字不是末页。");
            foreach (var locked in new[] { SessionState.Recording, SessionState.Paused, SessionState.Stopping, SessionState.Faulted })
            {
                state = locked; UpdateControls();
                view.PlayListeningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.PauseListeningButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                M3cNeed(!view.PlayListeningButton.IsEnabled && !view.PauseListeningButton.IsEnabled && !view.PlaybackPositionSlider.IsEnabled &&
                    !view.PlaybackSpeedBox.IsEnabled && player.Source is null && pendingTranscriptSeek is null, "校订页录音状态未阻止回放：" + locked);
            }
            M3cNeed(PlaybackFixtureHash(wave) == waveHash && host is null && !microphoneEnabled, "音频原件改变或意外采集。");
            return new { Status = "PASS", SourceMode = "SYNTHETIC FILE IMPORT / MUTED WPF PLAYBACK / NO CAPTURE", PlaybackSmoothness = snapshot,
                PlaybackRateAdvanceMs = rateAdvance, SharedSeekPauseSpeed = true, FixedFooterAtMinimumWindow = true, CopySelectedText = true,
                AutomaticSubtitleStartsDuringPlayback = automaticSubtitleStarts.Order().ToArray(), SubtitleFollowing = subtitleFollowing,
                PlainTextBothViews = true, ImportTabLast = true, RecordingPrivacyGuard = true, Images = images, ClipboardRestored = true,
                PhysicalKeyboard = "NOT_RUN; copy button routed event and shared copy implementation tested" };
        }
        finally
        {
            ResetTranscriptPlayback(); state = SessionState.Idle; UpdateControls();
            if (previousClipboard is not null) Clipboard.SetDataObject(previousClipboard, true); else Clipboard.Clear();
        }

        void PlayListeningAndWait() => PlaybackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void M3cNeed(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
