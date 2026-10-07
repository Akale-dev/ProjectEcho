using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3LocalTranscriberTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3b-local-asr"); Directory.CreateDirectory(root);
        run("T35/T37-SYNTH M3b ASR interface preserves local configuration and rejects wrong backend", () => Configuration(root));
        run("T35/T38-SYNTH M3b common ASR result retains original subtitle timeline and revision", () => Normalize(root));
        run("T35-SYNTH M3b common ASR digital silence and input hash boundaries", () => SilenceAndHashes(root));
        run("T37-WINDOWS SYNTHETIC CLI fixture M3b common ASR cancellation failure and retry", () => CancelRetry(root));
    }

    private static void Configuration(string root)
    {
        ILocalTranscriber engine = LocalTranscriptionBackends.Default;
        var capability = engine.Descriptor.Capabilities;
        Need(engine.Descriptor.Id == "whisper.cpp" && capability.InputSampleRate == 16000 && capability.InputChannels == 1 &&
            capability.InputBitsPerSample == 16 && capability.CanCancel && capability.CanAutoDetectLanguage && capability.CanSelectLanguage &&
            !capability.CanIdentifySpeakers && capability.TimestampQuality == "approximate" && capability.TimestampGranularity == "segment",
            "当前适配器能力错误或把字幕近似时间称作说话人/逐字对齐。");
        Need(LocalTranscriptionBackends.Qwen3Asr.Descriptor.Id == "qwen3-asr" &&
            !LocalTranscriptionBackends.Qwen3Asr.Inspect(null).Ready, "M4 Qwen 必须独立配置，不能无模型自动启动。");
        var legacy = new WhisperConfiguration("local.exe", "local.bin", "zh", 3, "fixture", new string('a', 64), true);
        var converted = LocalTranscriptionConfiguration.FromWhisper(legacy);
        Need(converted.BackendId == "whisper.cpp" && converted.ExecutablePath == legacy.ExecutablePath && converted.ModelPath == legacy.ModelPath &&
            converted.Language == "zh" && converted.Threads == 3 && converted.ModelName == legacy.ModelName &&
            converted.ExpectedModelSha256 == legacy.ExpectedModelSha256 && converted.ModelLicenseAcknowledged, "旧配置丢失。");
        Need(!engine.Inspect(null).Ready && !engine.Inspect(converted with { BackendId = "qwen3-asr" }).Ready, "不支持的后端被自动回退。");
        string meeting = M3TranscriptTests.Meeting(root, "config");
        var missing = engine.RunAsync(new(meeting, "config", 1000, "", "", converted with { BackendId = "qwen3-asr" }, true)).GetAwaiter().GetResult();
        Need(missing.Status == LocalTranscriptionStatus.NotConfigured && missing.Revision is null && !Directory.Exists(Path.Combine(meeting, "transcripts")),
            "错误后端产生识别任务或转写。");
    }

    private static void Normalize(string root)
    {
        var request = Request(root, "normalize", "synthetic-success"); string waveHash = request.ExpectedWaveSha256;
        ILocalTranscriber engine = LocalTranscriptionBackends.Default;
        Need(!engine.Inspect(request.Configuration with { Language = "zh;bad" }).Ready && !engine.Inspect(request.Configuration with { Threads = 0 }).Ready,
            "引擎选项未按适配器约束验证。");
        var result = engine.RunAsync(request).GetAwaiter().GetResult();
        Need(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision is not null, "接口未产生规范化结果。");
        var revision = result.Revision!; var segment = revision.Segments.Single();
        Need(segment.StartMs == 100 && segment.EndMs == 900 && segment.TimestampQuality == "approximate" && segment.SpeakerLabel is null &&
            segment.Text == "合成夹具结果，非真实 ASR" && revision.ModelName == "synthetic-interface-fixture" && revision.IsSynthetic,
            "规范化过程改写时间或伪造说话人。");
        string original = Path.Combine(request.MeetingDirectory, revision.OriginalFile);
        string raw = Path.Combine(result.AttemptDirectory, "whisper-output.srt");
        Need(M3TranscriptTests.Hash(raw) == revision.OriginalSha256 && M3TranscriptTests.Hash(original) == revision.OriginalSha256 &&
            M3TranscriptTests.Hash(request.WavePath) == waveHash, "原始模型输出/音频未保留。");
        var reloaded = TranscriptService.Load(request.MeetingDirectory, revision.RevisionId);
        Need(reloaded.Segments.Single() == segment, "磁盘重开改变规范化结果。");
        using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
        Need(attempt.RootElement.GetProperty("language").GetString() == "zh" && attempt.RootElement.GetProperty("threads").GetInt32() == 3 &&
            attempt.RootElement.GetProperty("maxSegmentLength").GetInt32() == 0 &&
            attempt.RootElement.GetProperty("fullTokenJson").GetBoolean(), "语言/线程/句子字幕分段参数记录丢失。");
        var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(result.AttemptDirectory, "whisper-output-arguments.json")))!;
        Need(arguments[Array.IndexOf(arguments, "-l") + 1] == "zh" && arguments[Array.IndexOf(arguments, "-t") + 1] == "3" &&
            !arguments.Contains("-ml") && arguments.Contains("-ojf"), "通用配置未传至真实的独立CLI参数，或仍按48字符硬切。");
    }

    private static void SilenceAndHashes(string root)
    {
        var request = Request(root, "silence", "synthetic-success");
        using (var stream = new FileStream(request.WavePath, FileMode.Open, FileAccess.Write))
        { stream.Position = 44; stream.Write(new byte[32000]); }
        request = request with { ExpectedWaveSha256 = M3TranscriptTests.Hash(request.WavePath) };
        var engine = LocalTranscriptionBackends.Default;
        var silence = engine.RunAsync(request).GetAwaiter().GetResult();
        Need(silence.Status == LocalTranscriptionStatus.NoSpeech && silence.Revision is null &&
            !File.Exists(Path.Combine(silence.AttemptDirectory, "stdout.txt")), "统一接口对数字零启动了模型或伪造文字。");
        var badHash = engine.RunAsync(request with { ExpectedWaveSha256 = new string('f', 64) }).GetAwaiter().GetResult();
        Need(badHash.Status == LocalTranscriptionStatus.Failed && badHash.Revision is null &&
            !File.Exists(Path.Combine(badHash.AttemptDirectory, "stdout.txt")), "统一接口跳过了输入完整性验证。");
    }

    private static void CancelRetry(string root)
    {
        var request = Request(root, "lifecycle", "synthetic-fail");
        ILocalTranscriber engine = LocalTranscriptionBackends.Default;
        var failed = engine.RunAsync(request).GetAwaiter().GetResult();
        Need(failed.Status == LocalTranscriptionStatus.Failed && failed.Revision is null, "失败被规范化为成功。");
        File.WriteAllText(request.Configuration.ModelPath, "synthetic-wait");
        using var cancellation = new CancellationTokenSource(); var timer = Stopwatch.StartNew();
        var task = engine.RunAsync(request, cancellation.Token);
        while (!Directory.EnumerateFiles(Path.Combine(request.MeetingDirectory, "transcripts", "asr"), "fixture-ready.txt", SearchOption.AllDirectories).Any() &&
            !task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        cancellation.Cancel(); var cancelled = task.GetAwaiter().GetResult();
        Need(cancelled.Status == LocalTranscriptionStatus.Cancelled && cancelled.Revision is null && timer.Elapsed < TimeSpan.FromSeconds(15),
            "统一接口未完成取消或留下伪成功修订。");
        File.WriteAllText(request.Configuration.ModelPath, "synthetic-success");
        var first = engine.RunAsync(request).GetAwaiter().GetResult();
        var second = engine.RunAsync(request).GetAwaiter().GetResult();
        Need(first.Status == LocalTranscriptionStatus.Succeeded && second.Status == LocalTranscriptionStatus.Succeeded &&
            first.Revision!.RevisionId != second.Revision!.RevisionId &&
            new[] { failed.AttemptDirectory, cancelled.AttemptDirectory, first.AttemptDirectory, second.AttemptDirectory }.Distinct().Count() == 4 &&
            TranscriptService.ListRevisions(request.MeetingDirectory).Count == 2 && M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256,
            "失败/取消/重跑覆盖了原稿或音频。");
    }

    private static LocalTranscriptionRequest Request(string root, string id, string behavior)
    {
        string meeting = M3TranscriptTests.Meeting(root, id), wave = Path.Combine(meeting, "input.wav");
        using (var stream = File.Create(wave))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(32036); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(32000);
            for (int i = 0; i < 16000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 3000));
        }
        string model = M3TranscriptTests.Input(meeting, "fixture.bin", behavior);
        return new(meeting, id, 1000, wave, M3TranscriptTests.Hash(wave), new("whisper.cpp",
            Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model, "zh", 3, "synthetic-interface-fixture",
            ModelLicenseAcknowledged: true), true);
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
