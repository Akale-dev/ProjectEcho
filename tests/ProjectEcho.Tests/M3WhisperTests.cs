using System.Diagnostics;
using System.Text;
using ProjectEcho.Core.Transcription;

internal static class M3WhisperTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3-whisper"); Directory.CreateDirectory(root);
        run("T35/T37-SYNTH M3 no model state never invokes executable or capture", () => Missing(root));
        run("T35-SYNTH M3 zero PCM suppresses local ASR hallucination path and legacy model hash is ignored", () => Silence(root));
        run("T35/T37-WINDOWS SYNTHETIC CLI fixture M3 failure cancellation retry output retention", () => Processes(root));
    }

    private static void Missing(string root)
    {
        var config = new WhisperConfiguration("", ""); var ready = WhisperTranscriber.Inspect(config);
        M3TranscriptTests.Need(!ready.Ready && ready.Message.Contains("正常录音"), "无模型不是明确安全状态。");
        string meeting = M3TranscriptTests.Meeting(root, "missing");
        var result = WhisperTranscriber.RunAsync(new(meeting, "missing", 1000, "", "", config, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(result.Status == "NotConfigured" && !Directory.Exists(Path.Combine(meeting, "transcripts")), "缺模型时启动了任务或产生转写。");
    }

    private static void Silence(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "silence"); string wave = WriteWave(meeting, true);
        string model = M3TranscriptTests.Input(meeting, "fixture-model.bin", "synthetic-success");
        var config = Config(model);
        var result = WhisperTranscriber.RunAsync(new(meeting, "silence", 1000, wave, M3TranscriptTests.Hash(wave), config, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(result.Status == "NoSpeech" && result.Revision is null && !File.Exists(Path.Combine(result.AttemptDirectory, "stdout.txt")), "纯数字静音调用了模型或产生文字。");
        var bad = WhisperTranscriber.RunAsync(new(meeting, "silence", 1000, wave, M3TranscriptTests.Hash(wave), config with { ExpectedModelSha256 = new string('f', 64) }, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(bad.Status == "NoSpeech" && File.Exists(Path.Combine(bad.AttemptDirectory, "attempt.json")), "旧模型hash仍然阻塞识别。");
        var rate = WriteWave(meeting, false, 48000, "bad-rate.wav");
        var wrongRate = WhisperTranscriber.RunAsync(new(meeting, "silence", 1000, rate, M3TranscriptTests.Hash(rate), config, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(wrongRate.Status == "Failed" && wrongRate.Message.Contains("16 kHz"), "盲喂48k混音。");
    }

    private static void Processes(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "processes"), wave = WriteWave(meeting, false), waveHash = M3TranscriptTests.Hash(wave);
        string model = M3TranscriptTests.Input(meeting, "fixture-model.bin", "synthetic-fail"); var config = Config(model);
        var failure = WhisperTranscriber.RunAsync(new(meeting, "processes", 1000, wave, waveHash, config, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(failure.Status == "Failed" && File.ReadAllText(Path.Combine(failure.AttemptDirectory, "stderr.txt")).Contains("synthetic failure"), "合成CLI故障未隔离/没有保留输出。");
        File.WriteAllText(model, "synthetic-wait"); using var cancellation = new CancellationTokenSource();
        var timer = Stopwatch.StartNew();
        var task = WhisperTranscriber.RunAsync(new(meeting, "processes", 1000, wave, waveHash, config, true), cancellation.Token);
        // Wait for this synthetic child to acknowledge it started, then cancel the owned process only.
        while (!Directory.EnumerateFiles(Path.Combine(meeting, "transcripts", "asr"), "fixture-ready.txt", SearchOption.AllDirectories).Any() && !task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(20);
        cancellation.Cancel(); var cancelled = task.GetAwaiter().GetResult();
        M3TranscriptTests.Need(cancelled.Status == "Cancelled" && timer.Elapsed < TimeSpan.FromSeconds(15), "取消未停止合成子进程。");
        File.WriteAllText(model, "synthetic-success");
        var success = WhisperTranscriber.RunAsync(new(meeting, "processes", 1000, wave, waveHash, config, true)).GetAwaiter().GetResult();
        M3TranscriptTests.Need(success.Status == "Succeeded" && success.Revision?.Segments.Single().Text == "合成夹具结果，非真实 ASR", "失败/取消后重试不可用。");
        M3TranscriptTests.Need(success.AttemptDirectory != failure.AttemptDirectory && success.AttemptDirectory != cancelled.AttemptDirectory &&
            M3TranscriptTests.Hash(wave) == waveHash && File.Exists(Path.Combine(success.AttemptDirectory, "whisper-output.json")), "重试覆盖原音频或原始输出丢失。");
        File.WriteAllText(Path.Combine(root, "ASR_NOT_REAL.txt"), "这些结果仅验证Windows本地进程合成夹具。没有whisper.cpp二进制/真实模型，没有真实识别速度、准确率、中英混合或专业词验收。\n");
    }

    internal static async Task<int> RunFixtureAsync(string[] args)
    {
        string Value(string option) { int i = Array.IndexOf(args, option); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("fixture argument missing"); return args[i + 1]; }
        string output = Value("-of"), model = Value("-m");
        if (!Path.GetFullPath(output).StartsWith(Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 90;
        string behavior = File.ReadAllText(model);
        if ((!behavior.StartsWith("synthetic-hardware-", StringComparison.Ordinal) && !args.Contains("-ng")) ||
            !args.Contains("-osrt") || !args.Contains("-otxt") || !args.Contains("-ojf")) return 91;
        File.WriteAllText(output + "-arguments.json", System.Text.Json.JsonSerializer.Serialize(args));
        Console.WriteLine("ProjectEcho synthetic CLI fixture, never actual ASR.");
        if (behavior.StartsWith("synthetic-hardware-", StringComparison.Ordinal))
        {
            string selected = behavior["synthetic-hardware-".Length..];
            if (selected == "none") await Console.Error.WriteAsync("whisper_backend_init_gpu: no GPU found\r\n");
            else if (selected == "unreported") await Console.Error.WriteAsync("GPU DLL is loaded but does not prove GPU selection\n");
            else
            {
                // Split the diagnostic across writes so the product must parse the stream.
                await Console.Error.WriteAsync("whisper_backend_init_gpu: us");
                await Console.Error.FlushAsync();
                await Task.Delay(10);
                await Console.Error.WriteAsync("ing " + (selected == "failed" ? "Vulkan0" : selected) + " backend\r\n");
                if (selected == "failed") await Console.Error.WriteAsync("whisper_backend_init_gpu: failed to initialize Vulkan0 backend\n");
            }
            await Console.Error.FlushAsync();
        }
        if (behavior == "synthetic-fail") { Console.Error.WriteLine("synthetic failure"); return 7; }
        if (behavior == "synthetic-wait")
        { File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "fixture-ready.txt"), "ready"); await Task.Delay(TimeSpan.FromMinutes(5)); return 8; }
        if (behavior == "synthetic-progress")
        {
            if (!args.Contains("-pp")) return 92;
            await Console.Error.WriteAsync("whisper_print_progress_callback: prog");
            await Console.Error.FlushAsync();
            await Task.Delay(30);
            await Console.Error.WriteAsync("ress =   5%\r\n");
            await Console.Error.WriteAsync("whisper_print_progress_callback: progress =  45%\n");
            await Console.Error.WriteAsync("unrelated model text 100%\n");
            await Console.Error.WriteAsync("whisper_print_progress_callback: progress = 100%");
        }
        if (behavior == "synthetic-progress-loading")
        {
            if (!args.Contains("-pp") || args.Contains("-p")) return 92;
            string attempt = Path.GetDirectoryName(output)!;
            await Console.Error.WriteAsync("whisper_init_from_file_with_params_no_state: loading model from 'synthetic-large.bin'\r\n");
            await Console.Error.FlushAsync();
            File.WriteAllText(Path.Combine(attempt, "fixture-model-loading.txt"), "synthetic-model-loading-not-real-ASR");
            if (!await WaitForMarker("continue-recognition.txt")) return 93;
            await Console.Error.WriteAsync("main: processing '" + new string('x', 400) + "' (16000 samples, 1.0 sec), lang = auto\r\n");
            await Console.Error.WriteAsync("whisper_print_progress_callback: progress =  63%");
            await Console.Error.FlushAsync();
            File.WriteAllText(Path.Combine(attempt, "fixture-progress-emitted.txt"), "synthetic-engine-percent-token");
            if (!await WaitForMarker("continue-save.txt")) return 94;
            await Console.Error.WriteAsync("\rwhisper_print_progress_callback: progress = 100%\n");
            await Console.Error.FlushAsync();

            async Task<bool> WaitForMarker(string name)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    if (File.Exists(Path.Combine(attempt, name))) return true;
                    await Task.Delay(10);
                }
                return false;
            }
        }
        if (behavior.StartsWith("synthetic-sentence-", StringComparison.Ordinal))
            return M3SentenceSegmentationTests.WriteFixture(output, behavior);
        if (behavior.StartsWith("synthetic-m3e-timestamp-", StringComparison.Ordinal))
            return M3eWhisperTimestampTests.WriteFixture(output, behavior);
        File.WriteAllText(output + ".srt", "1\n00:00:00,100 --> 00:00:00,900\n合成夹具结果，非真实 ASR\n", new UTF8Encoding(false));
        File.WriteAllText(output + ".txt", "合成夹具结果，非真实 ASR\n", new UTF8Encoding(false));
        File.WriteAllText(output + ".json", "{\"isSynthetic\":true}", new UTF8Encoding(false)); return 0;
    }

    private static WhisperConfiguration Config(string model) => new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model,
        ModelName: "synthetic-process-fixture-not-whisper", ModelLicenseAcknowledged: true);

    private static string WriteWave(string meeting, bool silence, int rate = 16000, string name = "input.wav")
    {
        string file = Path.Combine(meeting, name); using var stream = File.Create(file); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(rate * 2 + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(rate * 2);
        for (int i = 0; i < rate; i++) writer.Write(silence ? (short)0 : (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 3000));
        return file;
    }
}
