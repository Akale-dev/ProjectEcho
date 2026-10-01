using System.Text;
using ProjectEcho.Core.Transcription;

internal static class M3cTranscriptTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3c-transcript"); Directory.CreateDirectory(root);
        run("T35-SYNTH M3c Whisper percentages parse real callback format across bounded stream chunks", ProgressParser);
        run("T35-SYNTH large-model loading stays indeterminate and complete percent tokens report before newline", LoadingAndPartialProgress);
        run("T35-WINDOWS SYNTHETIC CLI M3c progress is engine reported with separate validation and saving stages", () => ProgressPipeline(root));
        run("T35-WINDOWS SYNTHETIC CLI loading and live no-newline progress arrive before process completion", () => LiveLoadingPipeline(root));
        run("T59-SYNTH M3c full text export preserves selected revision Unicode and repeated words without timestamps", () => TextExport(root));
        run("T20/T50-SYNTH M3c full text rejects meeting destinations and preserves files on cancellation or conflict", () => TextBoundaries(root));
    }

    private static void LoadingAndPartialProgress()
    {
        var percentages = new List<int>(); var stages = new List<LocalTranscriptionStage>();
        var parser = new WhisperProgressParser(percentages.Add, stages.Add);
        parser.Append("whisper_init_from_file_with_params_no_state: loading model from 'ggml-large-v3.bin'\r");
        parser.Append("whisper_model_load: model size = 3094.36 MB\nmodel says 100%\r");
        Need(percentages.Count == 0 && stages.Count == 0, "模型加载日志被当作识别百分比或完成。");
        foreach (char value in "main: processing '") parser.Append([value]);
        Need(stages.SequenceEqual(new[] { LocalTranscriptionStage.Recognizing }) && percentages.Count == 0,
            "已加载模型/输入后的真实处理标记没有及时切阶段。");
        parser.Append(new string('x', 4096)); parser.Append("' (768000 samples, 48.0 sec), lang = auto\r");
        parser.Append("whisper_full_with_state: auto-detected language: zh (p = 0.99)\n");
        parser.Append("whisper_print_progress_callback: progress =   0%");
        Need(percentages.SequenceEqual(new[] { 0 }), "完整0%仍等待换行或被擅自抹去。");
        parser.Append("\rwhisper_print_progress_callback: prog");
        parser.Append("ress =  63");
        Need(percentages.Count == 1, "百分号尚未到达时提前猜进度。");
        parser.Append("%");
        Need(percentages.SequenceEqual(new[] { 0, 63 }), "无换行63%未立即回报。");
        parser.Append("\nmain: processing 'duplicate'\rwhisper_model_load: loading model\r");
        parser.Append("whisper_print_progress_callback: progress =  45%\rwhisper_print_progress_callback: progress = 100%");
        Need(percentages.SequenceEqual(new[] { 0, 63, 100 }) && stages.Count == 1, "进度倒退或阶段重复/退回。");
        parser.Complete(); Need(percentages.Count == 3, "EOF重复发布已收到百分比。");
    }

    private static void LiveLoadingPipeline(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "live-loading"), wave = WriteProgressWave(meeting);
        string model = M3TranscriptTests.Input(meeting, "fixture.bin", "synthetic-progress-loading");
        var request = new LocalTranscriptionRequest(meeting, "live-loading", 1000, wave, M3TranscriptTests.Hash(wave),
            new("whisper.cpp", Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model, ModelLicenseAcknowledged: true), true);
        var reports = new Reports();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = LocalTranscriptionBackends.Default.RunAsync(request, timeout.Token, reports);
        try
        {
            string loading = WaitFor("fixture-model-loading.txt");
            var initial = reports.Snapshot();
            Need(!task.IsCompleted && initial.Any(x => x.Stage == LocalTranscriptionStage.LoadingModel) &&
                initial.All(x => x.Percent is null) && initial.All(x => x.Stage != LocalTranscriptionStage.Recognizing),
                "模型加载等待被显示成0%识别或提前切到识别。");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(loading)!, "continue-recognition.txt"), "continue");
            string emitted = WaitFor("fixture-progress-emitted.txt");
            while (!reports.Snapshot().Any(x => x.Percent == 63) && !task.IsCompleted && !timeout.IsCancellationRequested) Thread.Sleep(10);
            Need(!task.IsCompleted && reports.Snapshot().Any(x => x.Percent == 63), "管道收到无换行%后仍等待进程退出。");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(emitted)!, "continue-save.txt"), "continue");
            var result = task.GetAwaiter().GetResult();
            Need(result.Status == LocalTranscriptionStatus.Succeeded, "长加载合成夹具失败：" + result.Message);
            var values = reports.Snapshot();
            Need(values.Where(x => x.Percent is not null).Select(x => x.Percent).SequenceEqual(new int?[] { 63, 100 }),
                "管道擅自插入计时器百分比。");
            var phases = values.Select(x => x.Stage).Where((x, i) => i == 0 || values[i - 1].Stage != x).ToArray();
            Need(phases.SequenceEqual(new[] { LocalTranscriptionStage.Validating, LocalTranscriptionStage.LoadingModel,
                LocalTranscriptionStage.Recognizing, LocalTranscriptionStage.Saving }), "阶段乱序或保存前漏阶段。");
        }
        finally { timeout.Cancel(); _ = task.GetAwaiter().GetResult(); }

        string WaitFor(string marker)
        {
            while (!task.IsCompleted && !timeout.IsCancellationRequested)
            {
                string? path = Directory.EnumerateFiles(Path.Combine(meeting, "transcripts", "asr"), marker, SearchOption.AllDirectories).FirstOrDefault();
                if (path is not null) return path;
                Thread.Sleep(10);
            }
            throw new InvalidDataException("合成CLI未及时发布阶段标记：" + marker);
        }
    }

    private static string WriteProgressWave(string meeting)
    {
        string wave = Path.Combine(meeting, "input.wav");
        using var writer = new BinaryWriter(File.Create(wave));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000);
        for (int i = 0; i < 16000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 3000));
        return wave;
    }

    private static void ProgressParser()
    {
        var actual = new List<int>(); var parser = new WhisperProgressParser(actual.Add);
        const string messages = "model load 90%\n[00:01] said progress = 50%\nwhisper_print_progress_callback: progress =   5%\r\n" +
            "whisper_print_progress_callback: progress = 105%\nwhisper_print_progress_callback: progress = -1%\n" +
            "whisper_print_progress_callback: progress =  50%\nwhisper_print_progress_callback: progress =  40%\n" +
            "whisper_print_progress_callback: progress =  50%\nwhisper_print_progress_callback: progress =  75%\n";
        foreach (char value in messages) parser.Append([value]);
        parser.Append(new string('x', 5000)); parser.Append("whisper_print_progress_callback: progress =  99%\n");
        parser.Append("whisper_print_progress_callback: progress = 100%"); parser.Complete();
        Need(actual.SequenceEqual(new[] { 5, 50, 75, 100 }), "解析了非进度内容、倒退、重复、越界或超长行。");
        var empty = new List<int>(); new WhisperProgressParser(empty.Add).Complete();
        Need(empty.Count == 0, "无报告时伪造进度。");
    }

    private static void ProgressPipeline(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "progress"), wave = Path.Combine(meeting, "input.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000);
            for (int i = 0; i < 16000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 3000));
        }
        string model = M3TranscriptTests.Input(meeting, "fixture.bin", "synthetic-progress");
        var request = new LocalTranscriptionRequest(meeting, "progress", 1000, wave, M3TranscriptTests.Hash(wave),
            new("whisper.cpp", Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model, ModelLicenseAcknowledged: true), true);
        var reports = new Reports();
        var result = LocalTranscriptionBackends.Default.RunAsync(request, progress: reports).GetAwaiter().GetResult();
        Need(result.Status == LocalTranscriptionStatus.Succeeded, "进度夹具运行失败：" + result.Message);
        Need(reports.Values.Where(v => v.Percent is not null).Select(v => v.Percent).SequenceEqual(new int?[] { 5, 45, 100 }), "真实stderr管道未透传可靠百分比。");
        Need(reports.Values.First().Stage == LocalTranscriptionStage.Validating && reports.Values.Last() == new LocalTranscriptionProgress(LocalTranscriptionStage.Saving), "校验、识别和落盘未分开。");
        Need(File.ReadAllText(Path.Combine(result.AttemptDirectory, "stderr.txt")).Contains("unrelated model text 100%"), "进度解析改变了原始诊断输出。");
        File.WriteAllText(model, "synthetic-success"); var unknown = new Reports();
        var noProgress = LocalTranscriptionBackends.Default.RunAsync(request, progress: unknown).GetAwaiter().GetResult();
        Need(noProgress.Status == LocalTranscriptionStatus.Succeeded && unknown.Values.All(v => v.Percent is null), "没有引擎进度时伪造百分比。");
    }

    private static void TextExport(string root)
    {
        string folder = Path.Combine(root, "text"); Directory.CreateDirectory(folder);
        string meeting = M3TranscriptTests.Meeting(folder, "meeting");
        var original = TranscriptService.Import(new(meeting, "meeting", 10000,
            M3TranscriptTests.Input(folder, "source.srt", "1\n00:00:02,001 --> 00:00:03,002\n同一句\n\n2\n00:00:00,003 --> 00:00:01,004\n中文 English 🚀\n第二行\n\n3\n00:00:02,001 --> 00:00:03,000\n同一句"), IsSynthetic: true));
        var revised = TranscriptService.CreateRevision(meeting, original.RevisionId,
            original.Segments.Select(s => s.Text == "中文 English 🚀\n第二行" ? s with { Text = "繁體 English 🚀\n第二行" } : s).ToArray());
        string destination = Path.Combine(folder, "full.txt");
        TranscriptSubtitleExporter.ExportText(meeting, revised.RevisionId, destination);
        Need(File.ReadAllText(destination) == "繁體 English 🚀\n第二行\n同一句\n同一句\n", "全文错误版本、含时间或删除重复内容。");
        Need(!File.ReadAllBytes(destination).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "全文不是UTF-8无BOM。");
        Need(TranscriptService.Load(meeting, original.RevisionId).Segments.Any(s => s.Text.StartsWith("中文", StringComparison.Ordinal)), "全文导出改变原稿。");
        var untimed = TranscriptService.Import(new(meeting, "meeting", 10000,
            M3TranscriptTests.Input(folder, "notes.txt", "无时间说明\n正文中的 --> 是原文"), IsSynthetic: true));
        TranscriptSubtitleExporter.ExportText(meeting, untimed.RevisionId, destination, overwrite: true);
        Need(File.ReadAllText(destination).TrimEnd() == "无时间说明\n正文中的 --> 是原文", "无时间全文不能导出或原文被字幕规则改写。");
    }

    private static void TextBoundaries(string root)
    {
        string folder = Path.Combine(root, "boundaries"); Directory.CreateDirectory(folder);
        string meeting = M3TranscriptTests.Meeting(folder, "meeting");
        var revision = TranscriptService.Import(new(meeting, "meeting", 1000, M3TranscriptTests.Input(folder, "source.txt", "会议原文"), IsSynthetic: true));
        string existing = M3TranscriptTests.Input(folder, "existing.txt", "用户文件"), hash = M3TranscriptTests.Hash(existing);
        Reject<IOException>(() => TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId, existing));
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId, Path.Combine(meeting, "new.txt")));
        string another = M3TranscriptTests.Meeting(folder, "another");
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId, Path.Combine(another, "new.txt")));
        Reject<InvalidDataException>(() => TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId, Path.Combine(folder, "new.srt")));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Reject<OperationCanceledException>(() => TranscriptSubtitleExporter.ExportText(meeting, revision.RevisionId, existing, cancel.Token, true));
        Need(M3TranscriptTests.Hash(existing) == hash && !Directory.EnumerateFiles(folder, ".projectecho-subtitle-*.tmp").Any(), "取消/冲突覆盖了文件或泄漏临时文件。");
    }

    private sealed class Reports : IProgress<LocalTranscriptionProgress>
    {
        internal readonly List<LocalTranscriptionProgress> Values = [];
        public void Report(LocalTranscriptionProgress value) { lock (Values) Values.Add(value); }
        internal LocalTranscriptionProgress[] Snapshot() { lock (Values) return Values.ToArray(); }
    }
    private static void Need(bool value, string message) => M3TranscriptTests.Need(value, message);
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
