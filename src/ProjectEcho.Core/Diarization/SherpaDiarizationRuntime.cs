using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProjectEcho.Core.Transcription;

namespace ProjectEcho.Core.Diarization;

public sealed record SherpaDiarizationConfiguration(string ExecutablePath, string SegmentationModelPath,
    string EmbeddingModelPath, int? KnownSpeakerCount = null, int Threads = 2,
    double ClusteringThreshold = 0.9, bool ModelLicenseAcknowledged = false);
public sealed record SherpaDiarizationRequest(string MeetingDirectory, string MeetingId, string WavePath,
    SpeakerAudioBinding Audio, SherpaDiarizationConfiguration Configuration, bool IsSynthetic = false);
public sealed record SherpaDiarizationRunResult(string Status, string AttemptDirectory,
    SpeakerAnalysis? Analysis, string Message);

/// <summary>One owned native CPU process for the whole meeting; no per-window identity reset or capture access.</summary>
public static partial class SherpaDiarizationRuntime
{
    public const string ContractVersion = "sherpa-onnx-v1.13.8";
    public const string ClusteringPolicy = "whole-file-overlapping-10s-segmentation-global-complete-link-cosine-v1";
    public const string SphericalClusteringPolicy = "whole-file-overlapping-10s-segmentation-global-spherical-kmeans-unweighted-16x80-v1";
    public const string Source = "https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/offline-speaker-diarization-pyannote-impl.h";
    // Downloaded official MT asset and the same PE with only our audited UTF-8 manifest.
    private const string OfficialExecutableHash = "acc7dd36a3a3e14d396b54a41192f4a7db4df3883d0bc5fa68f7d2851ac83066";
    private const string PortableExecutableHash = "0843e7b90ed405aa7a5aa3a8ab0357ddca9b937ff956433bb88ff25044121698";
    private const string SphericalWorkerHash = "70587ae81a60bcca819c5c5409e723ea20a061b7f5057e88e6774c3279f7677d";
    public static WhisperReadiness Inspect(SherpaDiarizationConfiguration? configuration)
    {
        if (configuration is null) return new(false, "尚未配置本地说话人程序和两个模型。");
        try
        {
            foreach (string path in new[] { configuration.ExecutablePath, configuration.SegmentationModelPath, configuration.EmbeddingModelPath })
                if (!File.Exists(TranscriptFiles.LocalPath(path)) || new FileInfo(path).Length == 0)
                    return new(false, "说话人程序或模型不存在；不会自动下载。");
            if (!string.Equals(Path.GetExtension(configuration.ExecutablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                return new(false, "请选择受信任的 sherpa-onnx-offline-speaker-diarization.exe。");
            if (configuration.KnownSpeakerCount is < 1 or > 64 || configuration.Threads is < 1 or > 16 ||
                !double.IsFinite(configuration.ClusteringThreshold) || configuration.ClusteringThreshold is <= 0 or > 2)
                return new(false, "人数提示须为 1–64（留空为自动）；线程须为 1–16，阈值须大于 0 且不超过 2。");
            if (!configuration.ModelLicenseAcknowledged) return new(false, "请确认说话人模型来源与许可。");
            return new(true, "本地 CPU 说话人程序已配置；整场聚类，结果需人工核对。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new(false, ex.Message); }
    }

    public static async Task<SherpaDiarizationRunResult> RunAsync(SherpaDiarizationRequest request,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        string meeting = TranscriptFiles.Meeting(request.MeetingDirectory);
        var ready = Inspect(request.Configuration);
        if (!ready.Ready) return new("NotConfigured", "", null, ready.Message);
        if (request.Audio.SampleRate != 16000 || request.Audio.FrameCount <= 0 || request.Audio.FrameCount > 16000L * 3 * 3600 ||
            !TranscriptFiles.IsHash(request.Audio.InputWaveSha256) || request.Audio.TimelinePolicy is not ("pause-excluded-v1" or "elapsed-v1" or "file-v1"))
            throw new InvalidDataException("说话人输入须是已校验的完整 16kHz mono PCM16、实际时间轴；首版上限为三小时。");
        string wavePath = TranscriptFiles.LocalPath(request.WavePath);
        if (!wavePath.StartsWith(meeting.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("说话人输入须位于当前会议目录中。");
        string attempts = TranscriptFiles.Inside(meeting, "speakers/attempts"); Directory.CreateDirectory(attempts);
        string attempt = TranscriptFiles.Inside(attempts, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(attempt);
        string status = "Failed", message = "", waveHash = "", exeHash = "", segHash = "", embHash = "", executionHash = "";
        long paddedFrames = 0; SpeakerAnalysis? analysis = null; SherpaTimestampResult? timestamps = null;
        LocalInferenceProcessResult? run = null; var watch = Stopwatch.StartNew();
        var closureLocks = new List<FileStream>(); var closureHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawSpeakerMap = new Dictionary<string, string>();
        string clusteringPolicy = ClusteringPolicy, engineVersion = "1.13.8 / 11afbd00 (verified binary SHA-256)";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var wave = TranscriptFiles.ReadLock(wavePath);
            using var exe = TranscriptFiles.ReadLock(request.Configuration.ExecutablePath);
            using var segmentation = TranscriptFiles.ReadLock(request.Configuration.SegmentationModelPath);
            using var embedding = TranscriptFiles.ReadLock(request.Configuration.EmbeddingModelPath);
            waveHash = TranscriptFiles.Hash(wave, cancellationToken);
            if (!waveHash.Equals(request.Audio.InputWaveSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("说话人音频哈希改变。");
            (long offset, long frames) = ValidateWave(wave);
            if (frames != request.Audio.FrameCount) throw new InvalidDataException("说话人音频帧数与绑定不符。");
            exeHash = TranscriptFiles.Hash(exe, cancellationToken); segHash = TranscriptFiles.Hash(segmentation, cancellationToken);
            if (exeHash is not (OfficialExecutableHash or PortableExecutableHash or SphericalWorkerHash))
                throw new InvalidDataException("说话人程序不是已验证的本版原生程序或兼容 sherpa-onnx 版本；未运行未知输出合同。");
            if (exeHash == SphericalWorkerHash)
            {
                engineVersion = "ProjectEcho spherical-v1 / sherpa-onnx 1.13.8 / 11afbd00 (verified binary SHA-256)";
                if (request.Configuration.KnownSpeakerCount is > 0) clusteringPolicy = SphericalClusteringPolicy;
            }
            embHash = TranscriptFiles.Hash(embedding, cancellationToken);
            foreach (string dll in Directory.EnumerateFiles(Path.GetDirectoryName(request.Configuration.ExecutablePath)!, "*.dll"))
            {
                var held = TranscriptFiles.ReadLock(dll); closureLocks.Add(held);
                closureHashes.Add(Path.GetFileName(dll), TranscriptFiles.Hash(held, cancellationToken));
            }
            if (exeHash == SphericalWorkerHash)
            {
                var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sherpa-onnx-c-api.dll"] = "86c7807d12982aa31c5cae57c686f30df9e5d5db4309b314d1efd5c873a8333c",
                    ["onnxruntime.dll"] = "7f66f939a881baf4f46a2216496798edf4a1429878b646d12674aa62f27d8a25",
                    ["onnxruntime_providers_shared.dll"] = "551d0e1fe4c227d8542314ba718d52f4379e0c7bfe729a37c59833a884e27b4d"
                };
                foreach (var dependency in required)
                    if (!closureHashes.TryGetValue(dependency.Key, out string? hash) || hash != dependency.Value)
                        throw new InvalidDataException("原生说话人依赖缺失或与已验证版本不符：" + dependency.Key + "。请完整解压本版程序；没有启动分析进程。");
            }
            string executionWave = wavePath;
            // The upstream <=10s special path never invokes embedding clustering. Padding only this
            // independent inference copy forces the verified global path without altering source time.
            if (frames <= 160000)
            {
                executionWave = TranscriptFiles.Inside(attempt, "short-input-padded.wav"); paddedFrames = 176000 - frames;
                using var copy = new FileStream(executionWave, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new BinaryWriter(copy, Encoding.ASCII, leaveOpen: true);
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 176000 * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(176000 * 2);
                wave.Position = offset; var buffer = new byte[65536]; long remaining = frames * 2;
                while (remaining > 0) { cancellationToken.ThrowIfCancellationRequested(); int n = wave.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (n == 0) throw new EndOfStreamException(); copy.Write(buffer, 0, n); remaining -= n; }
                copy.SetLength(44L + 176000 * 2); copy.Flush(true);
            }
            using var executionLock = TranscriptFiles.ReadLock(executionWave);
            executionHash = TranscriptFiles.Hash(executionLock, cancellationToken);
            var configuration = request.Configuration;
            string[] args = [
                "--segmentation.pyannote-model=" + TranscriptFiles.LocalPath(configuration.SegmentationModelPath),
                "--embedding.model=" + TranscriptFiles.LocalPath(configuration.EmbeddingModelPath),
                "--segmentation.provider=cpu", "--embedding.provider=cpu",
                "--segmentation.num-threads=" + configuration.Threads.ToString(CultureInfo.InvariantCulture),
                "--embedding.num-threads=" + configuration.Threads.ToString(CultureInfo.InvariantCulture),
                "--segmentation.pyannote-window-shift-ratio=0.1", "--min-duration-on=0", "--min-duration-off=0",
                "--clustering.compute-confidence=false",
                "--clustering.cluster-threshold=" + configuration.ClusteringThreshold.ToString("R", CultureInfo.InvariantCulture),
                "--clustering.num-clusters=" + (configuration.KnownSpeakerCount ?? -1).ToString(CultureInfo.InvariantCulture), executionWave];
            using var timeout = new CancellationTokenSource(TimeSpan.FromHours(24));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            run = await LocalInferenceProcess.RunAsync(configuration.ExecutablePath, args, attempt, "native", stop.Token,
                errorLine: line => { var match = ProgressPattern().Match(line); if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 100) progress?.Report(value); }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (run.ExitCode != 0) throw new InvalidDataException($"说话人原生进程返回 {run.ExitCode}；原始输出已保留。");
            if (Regex.Matches(run.StandardOutput, "provider=\"cpu\"").Count != 2 || !run.StandardOutput.Contains("Started", StringComparison.Ordinal))
                throw new InvalidDataException("说话人原生输出未确认两模型均使用 CPU。");
            if (exeHash == SphericalWorkerHash)
            {
                string expected = request.Configuration.KnownSpeakerCount is > 0 ? "spherical-kmeans-global-v1" : "sherpa-complete-link-global-v1";
                if (!run.StandardOutput.Split('\n').Any(line => line.TrimEnd('\r') == "projectecho-clustering=" + expected))
                    throw new InvalidDataException("原生程序没有确认所选人数模式的全场聚类算法，未发布分析。");
            }
            timestamps = SherpaDiarizationTimestamps.Normalize(run.StandardOutput, frames, segHash, paddedFrames, cancellationToken);
            var raw = timestamps.Intervals;
            bool[] overlaps = DetectOverlaps(raw, cancellationToken);
            string[] ordered = OrderSpeakers(raw, overlaps).ToArray();
            var speakers = ordered.Select((number, index) => new MeetingSpeaker(Guid.NewGuid().ToString("N"), Code(index))).ToArray();
            var ids = ordered.Select((number, index) => (number, speakers[index].SpeakerId)).ToDictionary(x => x.number, x => x.SpeakerId);
            rawSpeakerMap = ids;
            var usableGroups = FirstUsableObservations(raw, cancellationToken).Keys.ToHashSet(StringComparer.Ordinal);
            var intervals = raw.Select((x, index) => new SpeakerInterval(x.StartMs, x.EndMs, ids[x.Speaker],
                x.EndMs - x.StartMs < 300 || !usableGroups.Contains(x.Speaker) ? "uncertain" : "estimated", overlaps[index])).ToArray();
            var warnings = new List<string> { "声音分组与换人时间均为模型估计；未推断姓名，未将音源当人物。", "不足 300ms 的短声音标待确认；缺失区间不代表没有发言。", "阈值 0.9 仅在固定公开及构造样本验证，不承诺自然会议准确率。" };
            string? timestampWarning = timestamps.Adjustments.Count == 0 ? null :
                $"已将 {timestamps.Adjustments.Count} 条经验证的模型末帧中心结束时码归到音频末端（最大调整 {timestamps.Adjustments.Max(item => item.RawEndMs - item.NormalizedEndMs)} ms）；没有裁剪音频或文字，原始时码及依据保留在分析尝试记录。";
            if (timestampWarning is not null) warnings.Add(timestampWarning);
            if (configuration.KnownSpeakerCount is { } known && known != speakers.Length) warnings.Add($"已知人数提示 {known} 与模型返回 {speakers.Length} 组不同，请核对。");
            string? coverageWarning = configuration.KnownSpeakerCount is > 1 && usableGroups.Count < configuration.KnownSpeakerCount
                ? $"已知人数为 {configuration.KnownSpeakerCount}，但只有 {usableGroups.Count} 组包含至少300ms连续非重叠声音；人数提示没有成功分开主要发言，请回听核对或另选声音模型。" : null;
            if (coverageWarning is not null) warnings.Add(coverageWarning);
            if (paddedFrames > 0) warnings.Add("不足或等于 10 秒的输入只在推理副本尾部补零，以运行全场聚类；原时间轴未改变。");
            analysis = new(1, request.MeetingId, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, request.Audio,
                new("sherpa-onnx", engineVersion, exeHash, segHash, embHash,
                    configuration.KnownSpeakerCount, configuration.ClusteringThreshold, "CPU", clusteringPolicy,
                    run.ElapsedMilliseconds, run.PeakWorkingSetBytes), speakers, intervals, warnings, request.IsSynthetic);
            cancellationToken.ThrowIfCancellationRequested();
            analysis = SpeakerService.SaveAnalysis(meeting, analysis, cancellationToken);
            status = "Succeeded"; message = speakers.Length == 0 ? "模型没有返回说话人；完整音频及原字幕保留，不能据此认定没有发言。" : $"已保存独立说话人分析，共 {speakers.Length} 组；请核对后应用字幕。";
            if (timestampWarning is not null) message += "\n" + timestampWarning;
            if (coverageWarning is not null) message += "\n" + coverageWarning;
            message += exeHash == SphericalWorkerHash && configuration.KnownSpeakerCount is > 0
                ? "\n本次使用新版已知人数全场聚类；已有人工版本保持独立。"
                : "\n本次使用兼容的自动/旧版全场聚类；该模式的既有质量限制仍需核对。";
        }
        catch (OperationCanceledException) { status = "Cancelled"; message = "说话人任务已取消；原音频和已有版本保留。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException or OverflowException)
        { status = "Failed"; message = ex.Message; }
        finally
        {
            foreach (var held in closureLocks) held.Dispose();
            TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, "attempt.json"), new
            {
                schemaVersion = "m5-sherpa-attempt-1", request.MeetingId, request.IsSynthetic, status, message,
                createdAt = DateTimeOffset.UtcNow, elapsedMilliseconds = watch.ElapsedMilliseconds,
                inputWaveSha256 = waveHash, request.Audio, originalFrameCount = request.Audio.FrameCount, paddedFrames,
                executionWaveSha256 = executionHash, executableSha256 = exeHash, segmentationSha256 = segHash, embeddingSha256 = embHash,
                timestampNormalization = timestamps is null ? null : new { timestamps.Policy, originalFrameCount = request.Audio.FrameCount,
                    sourceEndMs = (request.Audio.FrameCount * 1000 + 15999) / 16000, timestamps.Adjustments },
                nativeDependencies = closureHashes, rawClusterToStableSpeakerId = rawSpeakerMap, request.Configuration.KnownSpeakerCount, request.Configuration.Threads,
                request.Configuration.ClusteringThreshold, request.Configuration.ModelLicenseAcknowledged,
                actualDevice = run is not null ? "CPU-requested-see-raw-config" : "NotStarted", adapterContract = ContractVersion,
                clusteringPolicy, engineVersion, contractSource = Source,
                clusteringSource = exeHash == SphericalWorkerHash ? "src/ProjectEcho.Diarization.Native/main.cpp" : Source,
                workerBuildEvidence = exeHash == SphericalWorkerHash ? "LICENSES/Sherpa/ProjectEcho-native-worker.json" : null,
                minDurationOn = 0, minDurationOff = 0,
                progressMeaning = "native-embedding-chunks-not-total-task", resources = run is null ? null : new
                { run.ExitCode, run.ElapsedMilliseconds, run.PeakWorkingSetBytes, run.PeakPrivateBytes, run.CpuMilliseconds, run.Priority }, analysisId = status == "Succeeded" ? analysis?.AnalysisId : null
            });
        }
        return new(status, attempt, status == "Succeeded" ? analysis : null, message);
    }

    public sealed record NativeInterval(long StartMs, long EndMs, string Speaker);
    public static IReadOnlyList<string> OrderSpeakers(IReadOnlyList<NativeInterval> intervals, IReadOnlyList<bool> overlaps)
    {
        if (overlaps.Count != intervals.Count) throw new ArgumentException("Overlap vector length differs.");
        var usable = FirstUsableObservations(intervals);
        return intervals.Select((row, index) => (row, index)).GroupBy(x => x.row.Speaker, StringComparer.Ordinal)
            .OrderBy(group => usable.GetValueOrDefault(group.Key, long.MaxValue))
            .ThenBy(group => group.Min(x => x.row.StartMs)).Select(group => group.Key).ToArray();
    }

    /// <summary>Whole-interval overlap flags must not erase its actually unshared portions.</summary>
    public static IReadOnlyDictionary<string, long> FirstUsableObservations(IReadOnlyList<NativeInterval> intervals,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var events = intervals.SelectMany(row => new[] { (Time: row.StartMs, row.Speaker, Delta: 1), (Time: row.EndMs, row.Speaker, Delta: -1) })
            .OrderBy(item => item.Time).ToArray();
        var active = new Dictionary<string, int>(StringComparer.Ordinal); var result = new Dictionary<string, long>(StringComparer.Ordinal);
        string? currentSpeaker = null; long previous = 0, runStart = 0;
        for (int i = 0; i < events.Length;)
        {
            token.ThrowIfCancellationRequested(); long time = events[i].Time;
            if (time > previous)
            {
                string? only = active.Count == 1 ? active.Keys.First() : null;
                if (only != currentSpeaker) { currentSpeaker = only; runStart = previous; }
                if (only is not null && time - runStart >= 300) result.TryAdd(only, runStart);
            }
            do
            {
                var item = events[i++]; int count = active.GetValueOrDefault(item.Speaker) + item.Delta;
                if (count == 0) active.Remove(item.Speaker); else active[item.Speaker] = count;
            } while (i < events.Length && events[i].Time == time);
            previous = time;
        }
        return result;
    }
    /// <summary>Linear sweeps: largest preceding end and nearest following start from a different cluster.</summary>
    public static bool[] DetectOverlaps(IReadOnlyList<NativeInterval> intervals, CancellationToken token = default)
    {
        var result = new bool[intervals.Count];
        string? first = null, second = null; long maximum = long.MinValue, runnerUp = long.MinValue;
        void Add(string speaker, long value)
        {
            if (speaker == first) { maximum = Math.Max(maximum, value); return; }
            if (speaker == second) { runnerUp = Math.Max(runnerUp, value); if (runnerUp > maximum) (first, second, maximum, runnerUp) = (second, first, runnerUp, maximum); return; }
            if (value > maximum) { second = first; runnerUp = maximum; first = speaker; maximum = value; }
            else if (value > runnerUp) { second = speaker; runnerUp = value; }
        }
        for (int index = 0; index < intervals.Count; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var item = intervals[index];
            result[index] = (item.Speaker == first ? runnerUp : maximum) > item.StartMs;
            Add(item.Speaker, item.EndMs);
        }
        first = second = null; maximum = runnerUp = long.MinValue;
        for (int index = intervals.Count - 1; index >= 0; index--)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            var item = intervals[index];
            long nextNegativeStart = item.Speaker == first ? runnerUp : maximum;
            result[index] |= nextNegativeStart != long.MinValue && -nextNegativeStart < item.EndMs;
            Add(item.Speaker, -item.StartMs);
        }
        return result;
    }
    public static IReadOnlyList<NativeInterval> ParseOutput(string output, long durationMs)
    {
        var result = new List<NativeInterval>(); long lastStart = -1;
        foreach (string line in output.Split('\n'))
        {
            var match = IntervalPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) { if (!line.StartsWith("OfflineSpeakerDiarizationConfig(", StringComparison.Ordinal) && line.Contains("speaker_", StringComparison.Ordinal)) throw new InvalidDataException("说话人原始区间格式无效。"); continue; }
            long start = checked((long)decimal.Round(decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1000)),
                end = checked((long)decimal.Round(decimal.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 1000));
            if (start < lastStart || start < 0 || end <= start || end > durationMs || result.Count >= 50000)
                throw new InvalidDataException($"说话人第{result.Count + 1}条区间 {start}–{end} ms 无效（前起点 {lastStart} ms，有效时间上界 {durationMs} ms，上限50000条）；反序、越界或零长区间不静默裁切。");
            lastStart = start; result.Add(new(start, end, match.Groups[3].Value));
        }
        return result;
    }
    private static string Code(int index) { string result = ""; do { result = (char)('A' + index % 26) + result; index = index / 26 - 1; } while (index >= 0); return result; }
    private static (long Offset, long Frames) ValidateWave(Stream stream)
    {
        stream.Position = 0; using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF" || reader.ReadUInt32() + 8L != stream.Length || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidDataException("说话人输入必须是完整 RIFF WAV。");
        bool format = false; long offset = -1, frames = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string kind = Encoding.ASCII.GetString(reader.ReadBytes(4)); uint length = reader.ReadUInt32(); long next = checked(stream.Position + length + (length & 1));
            if (next > stream.Length) throw new InvalidDataException("WAV 块越界。");
            if (kind == "fmt ") { if (length < 16 || reader.ReadUInt16() != 1 || reader.ReadUInt16() != 1 || reader.ReadUInt32() != 16000 || reader.ReadUInt32() != 32000 || reader.ReadUInt16() != 2 || reader.ReadUInt16() != 16) throw new InvalidDataException("说话人 WAV 必须是 16kHz mono PCM16。"); format = true; }
            if (kind == "data") { if (offset >= 0 || length % 2 != 0) throw new InvalidDataException("WAV data 块重复或帧数错误。"); offset = stream.Position; frames = length / 2; }
            stream.Position = next;
        }
        if (!format || offset < 0 || frames <= 0 || stream.Position != stream.Length) throw new InvalidDataException("WAV 缺少格式或完整 PCM。");
        return (offset, frames);
    }
    [GeneratedRegex(@"^(\d+\.\d{3}) -- (\d+\.\d{3}) speaker_(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IntervalPattern();
    [GeneratedRegex(@"^progress (\d+(?:\.\d+)?)%$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();
}

