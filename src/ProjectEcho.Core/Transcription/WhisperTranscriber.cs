using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>Optional, user-configured whisper.cpp CLI. Never downloads a model or executable.</summary>
public static class WhisperTranscriber
{
    public const string VerifiedCliSource = "https://github.com/ggml-org/whisper.cpp/blob/b5130/examples/cli/cli.cpp";

    public static WhisperReadiness Inspect(WhisperConfiguration? configuration)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.ExecutablePath) || string.IsNullOrWhiteSpace(configuration.ModelPath))
            return new(false, "尚未配置本地 whisper.cpp 或模型；可正常录音、导出及导入转写。");
        try
        {
            string executable = TranscriptFiles.LocalPath(configuration.ExecutablePath), model = TranscriptFiles.LocalPath(configuration.ModelPath);
            if (!File.Exists(executable) || !File.Exists(model)) return new(false, "本地程序或模型文件不存在；不会自动下载。");
            if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase)) return new(false, "请选择已信任的 whisper-cli.exe，不能运行脚本或命令行包装器。");
            if (new FileInfo(executable).Length == 0 || new FileInfo(model).Length == 0) return new(false, "程序或模型文件为空。");
            if (!configuration.ModelLicenseAcknowledged) return new(false, "请先确认所选本地模型的来源与许可。");
            if (configuration.Threads is < 1 or > 64 || configuration.Language is null || configuration.Language.Length is < 2 or > 16 ||
                configuration.Language.Any(c => c is not (>= 'a' and <= 'z') and not '-')) return new(false, "语言或 CPU 线程设置无效。");
            if (configuration.ExpectedModelSha256 is not null && !TranscriptFiles.IsHash(configuration.ExpectedModelSha256)) return new(false, "预期模型 SHA-256 必须是 64 位十六进制。");
            if (configuration.ModelName?.Length > 200) return new(false, "模型显示名称不能超过 200 字符。");
            return new(true, "本地文件已配置；开始后校验哈希并以独立 CPU 进程运行，模型兼容性与准确率待实际验证。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new(false, ex.Message); }
    }

    public static async Task<WhisperRunResult> RunAsync(WhisperRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        TranscriptFiles.ValidateIdentity(request.MeetingId, request.MeetingDurationMs);
        var readiness = Inspect(request.Configuration);
        if (!readiness.Ready) return new("NotConfigured", "", 0, null, readiness.Message);
        string wavePath = TranscriptFiles.LocalPath(request.WavePath);
        if (!wavePath.StartsWith(meeting.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !TranscriptFiles.IsHash(request.ExpectedWaveSha256))
            throw new InvalidDataException("ASR 仅接受会议目录内、带预期 SHA-256 的已保存 16 kHz mono PCM16 派生 WAV。");
        string attempts = TranscriptFiles.Inside(meeting, "transcripts/asr"); Directory.CreateDirectory(attempts);
        string attempt = TranscriptFiles.Inside(attempts, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(attempt);
        var timer = Stopwatch.StartNew(); string status = "Failed", message = "", modelHash = "", executableHash = "", waveHash = "", priorityNote = "";
        int? exitCode = null; TranscriptRevision? revision = null; Process? process = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var model = TranscriptFiles.ReadLock(request.Configuration.ModelPath);
            using var executable = TranscriptFiles.ReadLock(request.Configuration.ExecutablePath);
            using var wave = TranscriptFiles.ReadLock(wavePath);
            modelHash = TranscriptFiles.Hash(model, cancellationToken);
            if (request.Configuration.ExpectedModelSha256 is not null && !string.Equals(modelHash, request.Configuration.ExpectedModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模型 SHA-256 与配置不符，未启动转写。");
            executableHash = TranscriptFiles.Hash(executable, cancellationToken); waveHash = TranscriptFiles.Hash(wave, cancellationToken);
            if (!string.Equals(waveHash, request.ExpectedWaveSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ASR 输入 WAV 哈希改变。");
            bool digitalSilence = ValidateWave(wave, request.MeetingDurationMs, cancellationToken);
            if (digitalSilence)
            {
                status = "NoSpeech"; message = "输入 PCM 全为数字零，未调用模型且未生成文字。该检查不代表其他安静片段已通过语音识别验收。";
            }
            else
            {
                string outputBase = TranscriptFiles.Inside(attempt, "whisper-output");
                var start = new ProcessStartInfo(TranscriptFiles.LocalPath(request.Configuration.ExecutablePath))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = attempt,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                // Verified against upstream examples/cli/cli.cpp on 2026-09-30. No prompt, network URL, shell or arbitrary arguments.
                foreach (string argument in new[] { "-m", TranscriptFiles.LocalPath(request.Configuration.ModelPath), "-f", wavePath,
                    "-l", request.Configuration.Language, "-t", request.Configuration.Threads.ToString(CultureInfo.InvariantCulture),
                    "-ng", "-osrt", "-otxt", "-ojf", "-of", outputBase }) start.ArgumentList.Add(argument);
                process = new Process { StartInfo = start };
                if (!process.Start()) throw new IOException("本地转写进程未启动。");
                try { process.PriorityClass = ProcessPriorityClass.BelowNormal; priorityNote = "BelowNormal"; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { priorityNote = "无法设置较低优先级：" + ex.Message; }
                using var timeout = new CancellationTokenSource(TimeSpan.FromHours(24));
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                using var stopRegistration = stop.Token.Register(() => Kill(process));
                Task stdout = CaptureOutput(process.StandardOutput, TranscriptFiles.Inside(attempt, "stdout.txt"), process, stop.Token);
                Task stderr = CaptureOutput(process.StandardError, TranscriptFiles.Inside(attempt, "stderr.txt"), process, stop.Token);
                try { await Task.WhenAll(process.WaitForExitAsync(stop.Token), stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                { throw new TimeoutException("本地转写超过 24 小时，已停止独立进程。可调整配置后重试。"); }
                cancellationToken.ThrowIfCancellationRequested(); exitCode = process.ExitCode;
                if (exitCode != 0) throw new InvalidDataException($"whisper.cpp 返回错误码 {exitCode}，原始输出保留；录音与导出不受影响。");
                string srt = outputBase + ".srt";
                if (!File.Exists(srt)) throw new InvalidDataException("转写进程成功退出但缺少 SRT 输出，未伪造转写。");
                byte[] resultBytes = TranscriptFiles.ReadBounded(srt, cancellationToken);
                if (string.IsNullOrWhiteSpace(TranscriptFiles.Decode(resultBytes)))
                { status = "NoSpeech"; message = "模型未返回字幕；原始输出保留，不能据此保证录音没有讲话。"; }
                else
                {
                    revision = TranscriptService.ImportWhisper(new(meeting, request.MeetingId, request.MeetingDurationMs, srt,
                        "whisper.cpp（本地，待人工校对）", request.Configuration.ModelName ?? Path.GetFileName(request.Configuration.ModelPath),
                        IsSynthetic: request.IsSynthetic), outputBase + ".json", cancellationToken);
                    status = "Succeeded"; message = "本地转写已保存独立原稿；按句末标点及可用 token 时间分句，时间仍为 approximate，文字需人工核对。";
                }
            }
        }
        catch (OperationCanceledException) { Kill(process); status = "Cancelled"; message = "本地转写已取消；原始音频、已有转写和本次原始输出保留，可重试。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException)
        { Kill(process); status = "Failed"; message = ex.Message; }
        finally
        {
            if (process is not null)
            {
                Kill(process);
                try { if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { message += "；进程退出确认失败：" + ex.Message; status = "Failed"; }
                process.Dispose();
            }
            timer.Stop();
            TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, "attempt.json"), new
            {
                schemaVersion = "m3-whisper-attempt-1", request.IsSynthetic, request.MeetingId,
                importedAt = DateTimeOffset.UtcNow, status, elapsedMilliseconds = timer.ElapsedMilliseconds,
                provider = "whisper.cpp", modelName = request.Configuration.ModelName ?? Path.GetFileName(request.Configuration.ModelPath),
                modelSha256 = modelHash, executableSha256 = executableHash, inputWaveSha256 = waveHash,
                request.Configuration.Language, request.Configuration.Threads, cpuOnly = true, maxSegmentLength = 0,
                fullTokenJson = true, subtitleSegmentation = WhisperSentenceSegmenter.Policy, priorityNote,
                modelLicenseAcknowledged = request.Configuration.ModelLicenseAcknowledged,
                cliSource = VerifiedCliSource, cliSourceCheckedAt = "2026-09-30", exitCode,
                revisionId = revision?.RevisionId, message
            });
        }
        return new(status, attempt, timer.ElapsedMilliseconds, revision, message);
    }

    private static async Task CaptureOutput(StreamReader reader, string outputPath, Process process, CancellationToken token)
    {
        try
        {
            using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            var buffer = new char[4096]; long count = 0; int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                count += read;
                if (count > 2 * 1024 * 1024) throw new InvalidDataException("转写进程诊断输出超过 2 Mi 字符，已停止进程并保留此前输出。");
                await writer.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        catch { Kill(process); throw; }
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool ValidateWave(Stream stream, long durationMs, CancellationToken token)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        string kind = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint riffLength = reader.ReadUInt32();
        if (kind is not ("RIFF" or "RF64") || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("ASR 输入不是 WAV/RF64。");
        if (kind == "RIFF" && riffLength + 8L != stream.Length) throw new InvalidDataException("WAV 容器长度不完整。");
        long? rfDataLength = null; long dataOffset = -1, dataLength = 0; bool format = false;
        while (stream.Position + 8 <= stream.Length)
        {
            token.ThrowIfCancellationRequested(); string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4)); long size = reader.ReadUInt32(); long body = stream.Position;
            if (chunk == "ds64" && kind == "RF64")
            {
                if (size < 28 || size > stream.Length - body) throw new InvalidDataException("RF64 ds64 无效。");
                ulong total = reader.ReadUInt64(), bytes = reader.ReadUInt64();
                if (total != (ulong)(stream.Length - 8) || bytes > long.MaxValue) throw new InvalidDataException("RF64 长度无效。");
                rfDataLength = (long)bytes;
            }
            if (chunk == "data" && size == uint.MaxValue) size = rfDataLength ?? throw new InvalidDataException("RF64 缺少 ds64。");
            if (size < 0 || size > stream.Length - body) throw new InvalidDataException("WAV 块长度越界。");
            if (chunk == "fmt ")
            {
                if (format || size < 16) throw new InvalidDataException("WAV fmt 不完整或重复。");
                ushort tag = reader.ReadUInt16(), channels = reader.ReadUInt16(); uint rate = reader.ReadUInt32(), byteRate = reader.ReadUInt32();
                ushort align = reader.ReadUInt16(), bits = reader.ReadUInt16();
                if (tag != 1 || channels != 1 || rate != 16000 || byteRate != 32000 || align != 2 || bits != 16)
                    throw new InvalidDataException("whisper.cpp 适配器要求预先生成 16 kHz、mono、PCM16 WAV；不得直接输入 48 kHz 混音。");
                format = true;
            }
            if (chunk == "data")
            {
                if (dataOffset >= 0 || size == 0 || (size & 1) != 0) throw new InvalidDataException("WAV data 无效或重复。");
                dataOffset = body; dataLength = size;
            }
            long next = checked(body + size + (size & 1));
            if (next > stream.Length) throw new InvalidDataException("WAV 块缺少必要对齐字节。");
            stream.Position = next;
        }
        if (stream.Position != stream.Length || !format || dataOffset < 0 || Math.Abs(dataLength * 1000m / 32000 - durationMs) > 1)
            throw new InvalidDataException("ASR WAV 时长与完整会话不符或缺少必要块。");
        bool allZero = true; stream.Position = dataOffset; var buffer = new byte[65536]; long remaining = dataLength;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested(); int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("ASR WAV 被截断。");
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0) allZero = false;
            remaining -= read;
        }
        stream.Position = 0; return allZero;
    }
}
