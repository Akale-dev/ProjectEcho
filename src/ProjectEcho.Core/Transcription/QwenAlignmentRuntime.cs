using System.Globalization;
using System.Runtime.Intrinsics.X86;

namespace ProjectEcho.Core.Transcription;

/// <summary>Selection is restricted to our bundled executables; custom installations remain explicit.</summary>
public static class QwenAlignmentRuntime
{
    public static bool SupportsOptimizedCpu => Avx2.IsSupported && Fma.IsSupported && Sse42.IsSupported && Bmi2.IsSupported &&
        X86Base.IsSupported && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0; // F16C is separately required by this build.

    public static QwenAlignmentConfiguration EffectiveConfiguration(QwenAlignmentConfiguration configuration)
    {
        if (configuration.Hardware == LocalTranscriptionHardware.Cpu && (configuration.DisableCpuAcceleration || !SupportsOptimizedCpu))
            return configuration with { Engine = QwenAlignmentEngine.Native,
                ExecutablePath = Path.Combine(AppContext.BaseDirectory, "Tools", "qwen-aligner", "audiocpp_cli.exe") };
        return configuration;
    }

    public static string ResolveBundledExecutable(string path, LocalTranscriptionHardware hardware, bool disableCpuAcceleration = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        string tools = Path.Combine(AppContext.BaseDirectory, "Tools");
        string[] names = ["qwen-aligner", "qwen-aligner-avx2", "qwen-aligner-vulkan"];
        if (!names.Any(name => Path.GetFullPath(path).Equals(Path.Combine(tools, name, "audiocpp_cli.exe"), StringComparison.OrdinalIgnoreCase)))
            return path;
        string selected = hardware == LocalTranscriptionHardware.Vulkan ? "qwen-aligner-vulkan" :
            !disableCpuAcceleration && SupportsOptimizedCpu && File.Exists(Path.Combine(tools, "qwen-aligner-avx2", "audiocpp_cli.exe")) ? "qwen-aligner-avx2" : "qwen-aligner";
        return Path.Combine(tools, selected, "audiocpp_cli.exe");
    }
}

internal sealed record QwenAlignmentInput(int Index, string Audio, string Text)
{
    internal string Id => "w" + Index.ToString("D6", CultureInfo.InvariantCulture);
    internal string Language => Text.Any(value => value is >= '\u3400' and <= '\u9fff') ? "Chinese" : "English";
}

internal sealed record QwenAlignmentBatchResult(LocalInferenceProcessResult Process, IReadOnlyDictionary<int, string> Words,
    string ActualBackend, string ActualDevice, string ProcessPrefix, string? Manifest, string? ManifestSha256, string? RequestsSha256);

internal static class QwenNativeAlignment
{
    internal const int MaximumBatchWindows = 16; // CLI materializes input PCM; never pass an unbounded meeting.
    internal const string RawMarker = "projectecho-alignment: raw-model-boundaries-v1; sample_rate=16000; timestamp_quantum_ms=80";
    internal const string BatchMarker = "projectecho-aligner-protocol: batch-v2";

    internal static string Executable(QwenAlignmentConfiguration configuration) => configuration.Engine == QwenAlignmentEngine.OfficialPython
        ? configuration.Python!.ExecutablePath : configuration.ExecutablePath;

    private static string[] Prefix(QwenAlignmentConfiguration configuration) => configuration.Engine == QwenAlignmentEngine.OfficialPython
        ? ["-I", configuration.Python!.BridgePath] : [];

    internal static async Task<LocalInferenceProcessResult> ProbeAsync(QwenAlignmentConfiguration configuration, string attempt, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var result = await LocalInferenceProcess.RunAsync(Executable(configuration), [.. Prefix(configuration), "--version"], attempt, "aligner-version", linked.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidDataException("句子定位程序版本检查失败。");
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidDataException("句子定位程序版本检查超时，已停止该进程。"); }
    }

    internal static async Task<QwenAlignmentBatchResult> RunAsync(QwenAlignmentConfiguration configuration, int threads,
        IReadOnlyList<QwenAlignmentInput> inputs, bool batch, string attempt, CancellationToken token, Action? completed)
    {
        if (inputs.Count < 1 || inputs.Count > (batch ? MaximumBatchWindows : 1)) throw new ArgumentException("定位批次窗口数无效。");
        if (!batch && (configuration.Hardware != LocalTranscriptionHardware.Cpu || configuration.Engine == QwenAlignmentEngine.OfficialPython))
            throw new InvalidDataException("所选定位程序不支持 batch-v2 实际设备验证，请选择新版 Vulkan 定位程序。");
        string prefix = (batch ? "align-batch-" : "align-") + inputs[0].Index.ToString("D6", CultureInfo.InvariantCulture);
        string output = TranscriptFiles.Inside(attempt, batch ? prefix + ".json" : $"window-{inputs[0].Index:D6}.words.json");
        string? manifest = batch ? TranscriptFiles.Inside(attempt, prefix + ".manifest.json") : null;
        var arguments = new List<string>(Prefix(configuration));
        arguments.AddRange(["--task", "align", "--family", "qwen3_forced_aligner", "--model",
            configuration.Engine == QwenAlignmentEngine.OfficialPython ? configuration.Python!.ModelDirectory : configuration.ModelPath,
            "--backend", configuration.Hardware == LocalTranscriptionHardware.Vulkan ? "vulkan" : "cpu",
            "--words-out", output, "--threads", threads.ToString(CultureInfo.InvariantCulture), "--metrics"]);
        if (configuration.Hardware == LocalTranscriptionHardware.Vulkan)
            arguments.AddRange(["--device", configuration.GpuDeviceIndex.ToString(CultureInfo.InvariantCulture)]);
        var expected = inputs.ToDictionary(input => input.Id, input => batch ?
            TranscriptFiles.Inside(attempt, prefix + "_" + input.Id + ".json") : output, StringComparer.Ordinal);
        string? requests = null; byte[]? requestBytes = null;
        if (batch)
        {
            requests = TranscriptFiles.Inside(attempt, prefix + ".requests.json");
            requestBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(inputs.Select(input =>
                new { id = input.Id, audio = input.Audio, text = input.Text, language = input.Language }), TranscriptFiles.Json);
            TranscriptFiles.WriteNew(requests, requestBytes);
            arguments.AddRange(["--request-sequence", requests]);
            arguments.AddRange(["--batch-manifest-out", manifest!]);
        }
        else arguments.AddRange(["--audio", inputs[0].Audio, "--text", inputs[0].Text, "--language", inputs[0].Language]);
        using var requestLock = requests is null ? null : TranscriptFiles.ReadLock(requests);
        string? requestHash = requestLock is null ? null : TranscriptFiles.Hash(requestLock, token);
        if (requestBytes is not null && requestHash != TranscriptFiles.Hash(requestBytes))
            throw new InvalidDataException("定位请求列表在写入和锁定之间发生变化，未启动推理。");
        TranscriptFiles.WriteJsonNew(TranscriptFiles.Inside(attempt, prefix + ".arguments.json"), arguments);
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Receive(string line)
        {
            const string marker = "projectecho-aligner-complete: ";
            if (!line.StartsWith(marker, StringComparison.Ordinal)) return;
            string id = line[marker.Length..];
            if (!expected.TryGetValue(id, out string? file) || !done.Add(id) || !File.Exists(file))
                throw new InvalidDataException("句子定位返回未知、重复或尚未落盘的窗口，未保存完成稿。");
            completed?.Invoke();
        }
        var run = await LocalInferenceProcess.RunAsync(Executable(configuration), arguments, attempt, prefix, token,
            errorLine: batch ? Receive : null).ConfigureAwait(false);
        if (run.ExitCode != 0 || expected.Values.Any(path => !File.Exists(path)) || batch && done.Count != inputs.Count)
            throw new InvalidDataException("句子定位进程未完成或缺少输出；不会用窗口起点代替。");
        if (!run.StandardError.Contains(RawMarker, StringComparison.Ordinal))
            throw new InvalidDataException("句子定位程序未确认原始声学边界，拒绝可能含插值修补的时码。");
        string actual = "CPU", device = "legacy-cpu-only; device not reported";
        if (batch)
        {
            actual = UniqueDiagnostic(run.StandardError, "projectecho-aligner-backend: ");
            device = UniqueDiagnostic(run.StandardError, "projectecho-aligner-device: ");
            string required = configuration.Hardware == LocalTranscriptionHardware.Cpu ? "CPU" : "Vulkan" + configuration.GpuDeviceIndex.ToString(CultureInfo.InvariantCulture);
            if (!actual.Equals(required, StringComparison.Ordinal))
                throw new InvalidDataException($"句子定位实际后端 {actual} 不符合所选 {required}；不会把回退记为成功。");
        }
        else completed?.Invoke();
        string? manifestHash = null;
        if (manifest is not null)
        {
            byte[] bytes = TranscriptFiles.ReadBounded(manifest, token);
            using var parsed = TranscriptFiles.ParseJson(bytes);
            if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("定位批次运行清单无效。");
            manifestHash = TranscriptFiles.Hash(bytes);
        }
        var words = new Dictionary<int, string>();
        foreach (var input in inputs)
        {
            string canonical = TranscriptFiles.Inside(attempt, $"window-{input.Index:D6}.words.json");
            if (batch) File.Move(expected[input.Id], canonical, false);
            words.Add(input.Index, canonical);
        }
        return new(run, words, actual, device, prefix, manifest, manifestHash, requestHash);
    }

    private static string UniqueDiagnostic(string diagnostics, string prefix)
    {
        string[] values = diagnostics.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..]).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0])) throw new InvalidDataException("句子定位缺少唯一实际设备证据。");
        return values[0];
    }
}
