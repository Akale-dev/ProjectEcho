using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProjectEcho.Core.Transcription;

/// <summary>Offline ASR plus separate, acoustic sentence alignment on hash-bound saved PCM windows.</summary>
public sealed class QwenTranscriber : ILocalTranscriber
{
    public const string BackendId = "qwen3-asr";
    public const string Policy = "qwen3-transcribe-raw-forced-alignment-v1";
    public const string PartialAlignmentPolicy = "qwen3-transcribe-verified-text-partial-alignment-v2";
    public const int WindowSeconds = 30;
    public LocalTranscriberDescriptor Descriptor { get; } = new(BackendId, "Qwen3-ASR + 句子定位（本地）",
        new(16000, 1, 16, true, true, true, false, "approximate", "sentence"));

    public LocalTranscriptionReadiness Inspect(LocalTranscriptionConfiguration? configuration)
    {
        if (configuration is null || configuration.BackendId != BackendId)
            return new(false, "尚未选择 Qwen 本地配置；不会替换现有 Whisper 引擎。");
        try
        {
            if (configuration.QwenAlignment is not { } alignment)
                return new(false, "Qwen 需要独立的本地句子定位程序和模型。");
            alignment = QwenAlignmentRuntime.EffectiveConfiguration(alignment);
            var requiredFiles = new List<(string Path, bool Program)> { (configuration.ExecutablePath, true), (configuration.ModelPath, false) };
            if (alignment.Engine == QwenAlignmentEngine.OfficialPython)
            {
                if (alignment.Hardware != LocalTranscriptionHardware.Cpu || alignment.Python is not { } python)
                    return new(false, "官方 Python 句子定位需要独立的 CPU 运行环境和本地模型目录。");
                if (string.IsNullOrWhiteSpace(python.ExecutablePath) || string.IsNullOrWhiteSpace(python.BridgePath) || string.IsNullOrWhiteSpace(python.ModelDirectory))
                    return new(false, "请先配置官方 Python 定位运行环境和完整本地模型目录。");
                requiredFiles.Add((python.ExecutablePath, true)); requiredFiles.Add((python.BridgePath, false));
                if (!Path.GetExtension(python.BridgePath).Equals(".py", StringComparison.OrdinalIgnoreCase))
                    return new(false, "官方 Python 定位桥接脚本无效。");
                QwenOfficialAlignmentModel.Files(python.ModelDirectory);
            }
            else if (alignment.Engine == QwenAlignmentEngine.Native)
            { requiredFiles.Add((alignment.ExecutablePath, true)); requiredFiles.Add((alignment.ModelPath, false)); }
            else return new(false, "句子定位引擎设置无效。");
            foreach (var file in requiredFiles)
            {
                string path = TranscriptFiles.LocalPath(file.Item1);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    return new(false, "Qwen 或句子定位的本地程序/模型不存在；不会自动下载。");
                if (file.Item2 && !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return new(false, "本地推理只运行已选择的 exe；Python 仅通过专用桥接脚本运行，不接受命令模板。");
            }
            if (!configuration.ModelLicenseAcknowledged) return new(false, "请先确认 Qwen 与句子定位模型的来源和许可。");
            if (configuration.Threads is < 1 or > 64 || configuration.GpuDeviceIndex is < 0 or > 63 ||
                alignment.Hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Vulkan) || alignment.GpuDeviceIndex is < 0 or > 63 ||
                configuration.Hardware is not (LocalTranscriptionHardware.Cpu or LocalTranscriptionHardware.Auto or LocalTranscriptionHardware.Vulkan) ||
                configuration.Language is not ("auto" or "zh" or "en") || configuration.ModelName?.Length > 200 ||
                configuration.InitialPrompt?.Length > 4000 || configuration.InitialPrompt?.Contains('\0') == true)
                return new(false, "Qwen 的语言、线程、硬件或提示词设置无效。");
            return new(true, "Qwen 本地文件已配置；识别后以独立模型定位句子，文字与时间仍需核对。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new(false, ex.Message); }
    }

    public async Task<LocalTranscriptionResult> RunAsync(LocalTranscriptionRequest request, CancellationToken cancellationToken = default,
        IProgress<LocalTranscriptionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var readiness = Inspect(request.Configuration);
        if (!readiness.Ready) return new(LocalTranscriptionStatus.NotConfigured, "", 0, null, readiness.Message);
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        TranscriptFiles.ValidateIdentity(request.MeetingId, request.MeetingDurationMs);
        string wave = TranscriptFiles.LocalPath(request.WavePath);
        if (!wave.StartsWith(meeting.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Qwen 仅处理本场目录内的已保存识别副本。");
        string attempts = TranscriptFiles.Inside(meeting, "transcripts/asr"); Directory.CreateDirectory(attempts);
        string attempt = TranscriptFiles.Inside(attempts, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(attempt);
        var watch = Stopwatch.StartNew();
        var configuration = request.Configuration with { QwenAlignment = QwenAlignmentRuntime.EffectiveConfiguration(request.Configuration.QwenAlignment!) };
        var status = LocalTranscriptionStatus.Failed; string message = "", actualBackend = "Unconfirmed";
        TranscriptRevision? revision = null; QwenWindowPlan? plan = null;
        var outcomes = new List<QwenWindowOutcome>(); var windowEvidence = new List<object>();
        var timingWarnings = new List<string>(); int untimedWindowCount = 0;
        bool textCoverageComplete = false, alignmentComplete = false;
        var runs = new List<LocalInferenceProcessResult>(); var hashes = new Dictionary<string, string>();
        var handles = new List<FileStream>();
        var alignmentModelFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        LocalInferenceProcessResult? alignmentVersion = null;
        string alignmentHardware = "NotRun", alignmentDevice = "NotRun"; bool alignmentBatch = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(24));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = linked.Token;
        try
        {
            progress?.Report(new(LocalTranscriptionStage.Validating)); token.ThrowIfCancellationRequested();
            configuration = configuration with
            {
                ExecutablePath = TranscriptFiles.LocalPath(configuration.ExecutablePath),
                ModelPath = TranscriptFiles.LocalPath(configuration.ModelPath),
                QwenAlignment = configuration.QwenAlignment! with
                {
                    ExecutablePath = string.IsNullOrWhiteSpace(configuration.QwenAlignment!.ExecutablePath) ? "" : TranscriptFiles.LocalPath(configuration.QwenAlignment.ExecutablePath),
                    ModelPath = string.IsNullOrWhiteSpace(configuration.QwenAlignment.ModelPath) ? "" : TranscriptFiles.LocalPath(configuration.QwenAlignment.ModelPath),
                    Python = configuration.QwenAlignment.Engine == QwenAlignmentEngine.OfficialPython && configuration.QwenAlignment.Python is { } python ? python with
                    {
                        ExecutablePath = TranscriptFiles.LocalPath(python.ExecutablePath), ModelDirectory = TranscriptFiles.LocalPath(python.ModelDirectory),
                        BridgePath = TranscriptFiles.LocalPath(python.BridgePath)
                    } : configuration.QwenAlignment.Python
                }
            };
            foreach (var file in new[] { ("executable", configuration.ExecutablePath), ("model", configuration.ModelPath),
                ("alignerExecutable", QwenNativeAlignment.Executable(configuration.QwenAlignment!)) })
            {
                var handle = TranscriptFiles.ReadLock(file.Item2); handles.Add(handle);
                hashes[file.Item1] = TranscriptFiles.Hash(handle, token);
            }
            if (configuration.QwenAlignment!.Engine == QwenAlignmentEngine.OfficialPython)
            {
                var official = configuration.QwenAlignment.Python!;
                var bridge = TranscriptFiles.ReadLock(official.BridgePath); handles.Add(bridge); hashes["alignerBridge"] = TranscriptFiles.Hash(bridge, token);
                foreach (string file in QwenOfficialAlignmentModel.Files(official.ModelDirectory))
                {
                    var handle = TranscriptFiles.ReadLock(file); handles.Add(handle);
                    alignmentModelFiles.Add(Path.GetFileName(file), TranscriptFiles.Hash(handle, token));
                }
                hashes["alignerModel"] = TranscriptFiles.Hash(Encoding.UTF8.GetBytes(string.Join('\n', alignmentModelFiles.Select(pair => pair.Key + "\0" + pair.Value))));
            }
            else
            {
                var handle = TranscriptFiles.ReadLock(configuration.QwenAlignment.ModelPath); handles.Add(handle);
                hashes["alignerModel"] = TranscriptFiles.Hash(handle, token);
            }
            using var input = new QwenWindowAudio(wave, request.ExpectedWaveSha256, token);
            if (Math.Abs(input.FrameCount * 1000m / 16000 - request.MeetingDurationMs) > 1)
                throw new InvalidDataException("Qwen 输入帧数与本场完整音频时长不符。");
            long windowCount = (input.FrameCount - 1) / (WindowSeconds * 16000) + 1;
            int balancedFrames = checked((int)((input.FrameCount + windowCount - 1) / windowCount));
            plan = QwenWindowPlan.Create(input.FrameCount, balancedFrames, timelinePolicy: request.TimelinePolicy);
            TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, "windows.json"), plan);
            var files = new Dictionary<int, QwenWindowAudioFile>();
            foreach (var window in plan.Windows)
            {
                var file = input.WriteWindow(plan, window, attempt, token); files.Add(window.Index, file);
                var windowLock = TranscriptFiles.ReadLock(file.Path); handles.Add(windowLock);
                if (TranscriptFiles.Hash(windowLock, token) != file.Sha256)
                    throw new InvalidDataException("识别窗口在写入和锁定之间发生变化，未启动推理。");
                if (file.IsDigitalSilence) outcomes.Add(new(window.Index, file.Sha256, QwenWindowStatus.DigitalSilence));
            }
            var spoken = plan.Windows.Where(window => !files[window.Index].IsDigitalSilence).ToArray();
            var recognized = new Dictionary<int, string>();
            if (spoken.Length > 0)
            {
                string list = TranscriptFiles.Inside(attempt, "input-list.txt");
                TranscriptFiles.WriteNew(list, new UTF8Encoding(false).GetBytes(string.Join('\n', spoken.Select(window => files[window.Index].Path)) + "\n"));
                var arguments = new List<string> { "--model", configuration.ModelPath, "--backend", configuration.Hardware switch
                    { LocalTranscriptionHardware.Cpu => "cpu", LocalTranscriptionHardware.Vulkan => "vulkan", _ => "auto" },
                    "--threads", configuration.Threads.ToString(CultureInfo.InvariantCulture), "--n-ctx", "2048", "--timestamps", "none",
                    "--batch", list, "--batch-jsonl" };
                if (configuration.Hardware != LocalTranscriptionHardware.Cpu)
                    arguments.AddRange(["--device", configuration.GpuDeviceIndex.ToString(CultureInfo.InvariantCulture)]);
                if (configuration.Language != "auto") arguments.AddRange(["--language", configuration.Language]);
                if (!string.IsNullOrWhiteSpace(configuration.InitialPrompt)) arguments.AddRange(["--prompt", configuration.InitialPrompt]);
                TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, "asr-arguments.json"), arguments);
                progress?.Report(new(LocalTranscriptionStage.LoadingModel));
                int completed = 0;
                var byPath = spoken.ToDictionary(window => files[window.Index].Path, window => window.Index, StringComparer.OrdinalIgnoreCase);
                void Receive(string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    using var document = TranscriptFiles.ParseJson(Encoding.UTF8.GetBytes(line)); var root = document.RootElement;
                    if (root.TryGetProperty("type", out var kind) && kind.GetString() == "batch_header") return;
                    if (!root.TryGetProperty("file", out var path) || !byPath.TryGetValue(path.GetString() ?? "", out int index) ||
                        recognized.ContainsKey(index) || root.TryGetProperty("error", out _))
                        throw new InvalidDataException("Qwen 返回失败、截断、重复或非本次窗口输出；原始输出保留。");
                    string text = root.GetProperty("text").GetString() ?? throw new InvalidDataException("Qwen 文字为空值。");
                    if (text.Length > TranscriptFiles.MaximumSegmentCharacters || text.Contains('\0'))
                        throw new InvalidDataException("Qwen 窗口文字超限或含无效字符。");
                    recognized.Add(index, text); completed++;
                    progress?.Report(new(LocalTranscriptionStage.Recognizing, completed * 100 / spoken.Length));
                }
                var run = await LocalInferenceProcess.RunAsync(configuration.ExecutablePath, arguments, attempt, "asr", token, Receive).ConfigureAwait(false);
                runs.Add(run);
                if (run.ExitCode != 0 || recognized.Count != spoken.Length)
                    throw new InvalidDataException("Qwen 未成功处理全部音频窗口，不能保存整场完成稿。");
                actualBackend = VerifyBackend(run.StandardError, configuration.Hardware, configuration.GpuDeviceIndex);
            }
            var segments = new List<TranscriptSegment>(); var originals = new List<string>();
            foreach (var window in spoken)
            {
                token.ThrowIfCancellationRequested();
                string text = recognized[window.Index]; originals.Add(text);
                TranscriptFiles.WriteNew(TranscriptFiles.Inside(attempt, $"window-{window.Index:D6}.txt"), Encoding.UTF8.GetBytes(text));
            }
            // All requests retain their own frame/text binding. Reuse one model session for at most 16 windows.
            var alignmentInputs = spoken.Where(window => recognized[window.Index].Length > 0)
                .Select(window => new QwenAlignmentInput(window.Index, files[window.Index].Path, recognized[window.Index])).ToArray();
            var aligned = new Dictionary<int, QwenAlignmentBatchResult>();
            if (alignmentInputs.Length > 0)
            {
                progress?.Report(new(LocalTranscriptionStage.Aligning, 0));
                alignmentVersion = await QwenNativeAlignment.ProbeAsync(configuration.QwenAlignment!, attempt, token).ConfigureAwait(false);
                alignmentBatch = alignmentVersion.StandardOutput.Split('\n').Any(line => line.Trim() == QwenNativeAlignment.BatchMarker);
                int completedAlignment = 0;
                foreach (var batch in alignmentInputs.Chunk(alignmentBatch ? QwenNativeAlignment.MaximumBatchWindows : 1))
                {
                    token.ThrowIfCancellationRequested();
                    var result = await QwenNativeAlignment.RunAsync(configuration.QwenAlignment, configuration.Threads, batch, alignmentBatch, attempt, token,
                        () => progress?.Report(new(LocalTranscriptionStage.Aligning, ++completedAlignment * 100 / alignmentInputs.Length))).ConfigureAwait(false);
                    runs.Add(result.Process);
                    if (alignmentHardware != "NotRun" && (alignmentHardware != result.ActualBackend || alignmentDevice != result.ActualDevice))
                        throw new InvalidDataException("句子定位批次实际设备不一致，未保存完成稿。");
                    alignmentHardware = result.ActualBackend; alignmentDevice = result.ActualDevice;
                    foreach (var item in batch) aligned.Add(item.Index, result);
                }
            }
            foreach (var window in spoken)
            {
                token.ThrowIfCancellationRequested(); var file = files[window.Index]; string text = recognized[window.Index];
                if (text.Length == 0)
                {
                    outcomes.Add(new(window.Index, file.Sha256, QwenWindowStatus.ModelNoText, QwenWindowAlignment.TextSha256(text)));
                    continue;
                }
                var batchResult = aligned[window.Index]; string words = batchResult.Words[window.Index];
                // Keep this exact raw file immutable through inspection and revision publication.
                var wordsLock = TranscriptFiles.ReadLock(words); handles.Add(wordsLock);
                byte[] rawWords = TranscriptFiles.ReadBounded(words, token);
                string rawWordsHash = TranscriptFiles.Hash(rawWords);
                var inspected = QwenAlignmentSentenceMapper.Inspect(text, rawWords, window.InputFrameCount);
                var evidence = new QwenWindowAlignmentEvidence("forced-alignment", Policy, hashes["alignerExecutable"], hashes["alignerModel"],
                    file.Sha256, QwenWindowAlignment.TextSha256(text));
                var ids = new List<string>(); IReadOnlyList<QwenMappedSentence> mapped = [];
                string alignmentStatus;
                if (!inspected.TimingComplete)
                {
                    // Context could duplicate words across windows; it cannot be silently merged
                    // into an untimed paragraph. The production balanced plan has no context.
                    if (window.InputStartFrame != window.OwnedStartFrame || window.InputFrameCount != window.OwnedFrameCount)
                        throw new InvalidDataException("待定位窗口含未解决上下文，不能猜测文字归属。");
                    string id = $"segment-{segments.Count + 1:D6}"; ids.Add(id);
                    segments.Add(new(id, null, null, null, "mixed", text, "untimed"));
                    outcomes.Add(new(window.Index, file.Sha256, QwenWindowStatus.TranscribedUntimed, evidence.OriginalTextSha256, 1));
                    alignmentStatus = "untimed"; untimedWindowCount++;
                    if (timingWarnings.Count < 100)
                    {
                        string details = string.Join("；", inspected.TimingIssues.Take(3).Select(issue =>
                            $"{issue.Kind}：词 {issue.WordNumber}，{issue.StartSample}–{issue.EndSample} 帧，窗口 {issue.InputFrames} 帧"));
                        timingWarnings.Add($"Qwen 窗口 {window.Index + 1} 的完整字文已核对，但声学时间有 {inspected.TimingIssueCount} 项异常（{details}）；保留全文为无时间段，未修补时码。");
                    }
                }
                else
                {
                    mapped = QwenWindowAlignment.Map(plan, window, text, file.Sha256, evidence, inspected.Sentences);
                    foreach (var sentence in mapped)
                    {
                        if (sentence.Ownership != QwenWindowSentenceOwnership.Inside) throw new InvalidDataException("窗口上下文尚未解决，不能静默删除原文。");
                        long endMs = sentence.EndMs;
                        if (endMs > request.MeetingDurationMs && sentence.EndFrame == input.FrameCount && endMs - request.MeetingDurationMs <= 1)
                            endMs = request.MeetingDurationMs;
                        string id = $"segment-{segments.Count + 1:D6}"; ids.Add(id);
                        segments.Add(new(id, sentence.StartMs, endMs, null, "mixed", sentence.Text, "approximate"));
                    }
                    outcomes.Add(new(window.Index, file.Sha256, QwenWindowStatus.Aligned, evidence.OriginalTextSha256, mapped.Count));
                    alignmentStatus = "aligned";
                }
                windowEvidence.Add(new { window, evidence, alignmentStatus, transcriptSegmentIds = ids,
                    sentences = mapped, untimedText = alignmentStatus == "untimed" ? text : null,
                    timingIssueCount = inspected.TimingIssueCount, timingIssues = inspected.TimingIssues,
                    rawWords = Path.GetFileName(words), rawWordsSha256 = rawWordsHash,
                    alignmentHardware = batchResult.ActualBackend, alignmentDevice = batchResult.ActualDevice, process = batchResult.ProcessPrefix,
                    batchManifest = batchResult.Manifest is null ? null : Path.GetFileName(batchResult.Manifest), batchResult.ManifestSha256,
                    batchResult.RequestsSha256 });
            }
            var coverage = QwenWindowCoverage.Evaluate(plan, outcomes);
            textCoverageComplete = coverage.TextComplete; alignmentComplete = coverage.Complete;
            if (!textCoverageComplete) throw new InvalidDataException("本场音频的识字覆盖未完成，原始输出保留供检查。");
            if (untimedWindowCount > 100)
                timingWarnings.Add($"共有 {untimedWindowCount} 个窗口定位未完成；仅列前100条摘要，完整数值诊断和全部原文保存在本版证据中。");
            if (segments.Count == 0)
            {
                status = LocalTranscriptionStatus.NoSpeech;
                message = spoken.Length == 0 ? "输入全部为数字零，未调用模型、未生成文字。" : "模型已处理全部窗口但未返回文字；不能据此保证无人讲话。";
            }
            else
            {
                progress?.Report(new(LocalTranscriptionStage.Saving));
                string originalText = string.Join("\n\n", originals);
                byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = alignmentComplete ? "m4-qwen-alignment-1" : "m4-qwen-alignment-2",
                    policy = alignmentComplete ? Policy : PartialAlignmentPolicy,
                    textCoverageComplete, alignmentComplete, originalTextSha256 = QwenWindowAlignment.TextSha256(originalText),
                    request.MeetingId, request.ExpectedWaveSha256,
                    request.SourceMixSha256, request.TimelinePolicy, input.FrameCount, plan, coverage, hashes, actualBackend,
                    alignmentHardware, alignmentDevice, alignmentBatch, alignmentEngine = configuration.QwenAlignment.Engine.ToString(), alignmentModelFiles,
                    attempt = Path.GetFileName(attempt), windowEvidence, outcomes,
                    warning = "Acoustic model estimates; no interpolation, no Whisper transcript, no claim of transcription accuracy."
                }, TranscriptFiles.Json);
                revision = alignmentComplete
                    ? TranscriptService.ImportAligned(request, originalText, segments, evidence, input.FrameCount, Policy, token)
                    : TranscriptService.ImportQwenUnaligned(request, originalText, segments, evidence, input.FrameCount, timingWarnings, token);
                status = LocalTranscriptionStatus.Succeeded;
                message = alignmentComplete
                    ? $"Qwen 原稿与句子定位已保存为独立版本；识别 {actualBackend}，定位 {alignmentHardware}。点击字幕可回听，文字及近似时码请核对。"
                    : $"Qwen 全文已完整保存；{untimedWindowCount} 个窗口的声学时间未通过校验，已保留为无时间段，定位尚未完成。" +
                        "正常窗口可回听；全文可搜索、校订和导出 TXT，含待定位段时不能导出 SRT/VTT。未插值或切换硬件重试。" +
                        $"识别 {actualBackend}，定位 {alignmentHardware}。";
                if (!alignmentComplete) message += "\n" + string.Join("\n", timingWarnings.Take(3));
            }
        }
        catch (OperationCanceledException)
        {
            status = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? LocalTranscriptionStatus.Failed : LocalTranscriptionStatus.Cancelled;
            message = status == LocalTranscriptionStatus.Cancelled ? "本地识别/定位已取消，原音频、原稿、人工稿和本次输出保留。" : "识别/定位超过 24 小时，已停止本次进程。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception or TimeoutException)
        { status = LocalTranscriptionStatus.Failed; message = ex.Message; }
        finally
        {
            foreach (var handle in handles) handle.Dispose(); watch.Stop();
            TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, "attempt.json"), new
            {
                schemaVersion = "m4-qwen-attempt-1", policy = untimedWindowCount > 0 ? PartialAlignmentPolicy : Policy,
                textCoverageComplete, alignmentComplete, untimedWindowCount,
                provider = BackendId, request.MeetingId, request.IsSynthetic,
                request.MeetingDurationMs, request.ExpectedWaveSha256, request.SourceMixSha256, request.TimelinePolicy,
                requestedHardware = configuration.Hardware.ToString(), configuration.GpuDeviceIndex, configuration.Threads,
                configuration.Language, configuration.InitialPrompt, configuration.ModelName, configuration.ModelLicenseAcknowledged,
                hashes, actualBackend, alignmentHardware, alignmentDevice, alignmentBatch,
                alignmentEngine = configuration.QwenAlignment?.Engine.ToString(), alignmentModelFiles,
                requestedAlignmentEngine = request.Configuration.QwenAlignment?.Engine.ToString(),
                disableCpuAcceleration = request.Configuration.QwenAlignment?.DisableCpuAcceleration,
                cpuSupportsOptimizedAlignment = QwenAlignmentRuntime.SupportsOptimizedCpu,
                requestedAlignmentHardware = configuration.QwenAlignment?.Hardware.ToString(), alignmentGpuDeviceIndex = configuration.QwenAlignment?.GpuDeviceIndex,
                alignmentVersion = alignmentVersion is null ? null : new { alignmentVersion.StandardOutput, alignmentVersion.StandardError, alignmentVersion.ElapsedMilliseconds },
                timestampQuality = revision is null ? "not-saved" : revision.Segments.All(segment => segment.StartMs is null) ? "untimed" :
                    revision.Segments.Any(segment => segment.StartMs is null) ? "mixed" : "approximate", outcomes,
                coverage = plan is null ? null : QwenWindowCoverage.Evaluate(plan, outcomes),
                processMetricsScope = "Launched PID only; a Python venv launcher may spawn a separate worker, whose memory is not included. The portable base python.exe avoids that launcher.",
                processMetrics = runs.Select(run => new { run.ExitCode, run.ElapsedMilliseconds, run.PeakWorkingSetBytes, run.PeakPrivateBytes, run.CpuMilliseconds, run.Priority }),
                elapsedMilliseconds = watch.ElapsedMilliseconds, status = status.ToString(), revisionId = revision?.RevisionId, message
            });
        }
        return new(status, attempt, watch.ElapsedMilliseconds, status == LocalTranscriptionStatus.Succeeded ? revision : null, message);
    }

    internal static string VerifyBackend(string diagnostics, LocalTranscriptionHardware requested, int deviceIndex)
    {
        if (!diagnostics.Split('\n').Any(line => line.TrimStart().StartsWith("[info] qwen3_asr: using ", StringComparison.Ordinal)))
            throw new InvalidDataException("所选程序没有确认 Qwen3-ASR 模型架构，不能把其他模型输出标记成 Qwen。");
        string[] values = diagnostics.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("projectecho-backend: ", StringComparison.Ordinal))
            .Select(line => line["projectecho-backend: ".Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != 1) throw new InvalidDataException("Qwen 缺少唯一实际后端证据，未假定 GPU 成功。");
        string actual = values[0];
        if (requested == LocalTranscriptionHardware.Vulkan && actual != "Vulkan" + deviceIndex.ToString(CultureInfo.InvariantCulture) ||
            requested == LocalTranscriptionHardware.Cpu && !actual.Equals("CPU", StringComparison.OrdinalIgnoreCase) ||
            requested == LocalTranscriptionHardware.Auto && !actual.Equals("CPU", StringComparison.OrdinalIgnoreCase) && !actual.StartsWith("Vulkan", StringComparison.Ordinal))
            throw new InvalidDataException($"Qwen 实际后端 {actual} 不符合所选 {requested}，不把回退记为成功。");
        return actual;
    }
}
