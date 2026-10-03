using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3dWhisperTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3d-whisper"); Directory.CreateDirectory(root);
        run("T35/T62-SYNTH M3D hardware and prompt configuration preserve legacy defaults and bounds", () => Configuration(root));
        run("T35/T62-WINDOWS SYNTHETIC CLI M3D CPU Auto Vulkan CUDA arguments and literal initial prompt", () => Arguments(root));
        run("T35/T37-WINDOWS SYNTHETIC CLI M3D explicit GPU rejects absent wrong or failed backend", () => RejectFallback(root));
        run("T35/T38-WINDOWS SYNTHETIC CLI M3D models keep separate originals and reviewed versions", () => Versions(root));
    }

    private static void Configuration(string root)
    {
        var request = Request(root, "configuration", "none");
        var legacy = JsonSerializer.Deserialize<WhisperConfiguration>("{\"ExecutablePath\":\"local.exe\",\"ModelPath\":\"local.bin\",\"ExpectedModelSha256\":\"obsolete-invalid-digest\",\"ModelLicenseAcknowledged\":true}")!;
        Need(legacy.Hardware == LocalTranscriptionHardware.Cpu && legacy.GpuDeviceIndex == 0 && legacy.InitialPrompt is null,
            "旧配置未保持CPU默认行为。");
        var settings = request.Configuration with { Hardware = LocalTranscriptionHardware.Vulkan, GpuDeviceIndex = 3,
            InitialPrompt = "腾讯 TCEHY, EPS, EBITDA, 张先生", ExpectedModelSha256 = "obsolete-invalid-digest" };
        var copied = LocalTranscriptionConfiguration.FromWhisper(settings);
        Need(copied.Hardware == settings.Hardware && copied.GpuDeviceIndex == 3 && copied.InitialPrompt == settings.InitialPrompt,
            "通用适配器丢失硬件或initial prompt。");
        Need(WhisperTranscriber.Inspect(settings).Ready && LocalTranscriptionBackends.Default.Inspect(copied).Ready,
            "旧模型hash仍参与校验或新配置无法通过静态检查。");
        Need(JsonSerializer.Deserialize<WhisperConfiguration>(JsonSerializer.Serialize(settings)) == settings,
            "配置JSON往返改变硬件或提示词。");
        foreach (var invalid in new[] { settings with { Hardware = (LocalTranscriptionHardware)99 }, settings with { GpuDeviceIndex = -1 },
            settings with { GpuDeviceIndex = 64 }, settings with { InitialPrompt = new string('a', 4001) }, settings with { InitialPrompt = "a\0b" } })
            Need(!WhisperTranscriber.Inspect(invalid).Ready, "无效硬件/设备/提示词未拒绝。");
    }

    private static void Arguments(string root)
    {
        const string prompt = "公司 \"ACME\"；ticker $ABC；EBITDA\n张先生 & Co; --no-gpu `literal` $(literal)";
        foreach (var hardware in Enum.GetValues<LocalTranscriptionHardware>())
        {
            string reported = hardware switch { LocalTranscriptionHardware.Vulkan => "Vulkan3", LocalTranscriptionHardware.Cuda => "CUDA3", _ => "none" };
            var request = Request(root, "arguments-" + hardware, reported);
            request = request with { Configuration = request.Configuration with { Hardware = hardware, GpuDeviceIndex = 3,
                InitialPrompt = prompt, ExpectedModelSha256 = "this-is-intentionally-not-a-hash" } };
            var result = LocalTranscriptionBackends.Default.RunAsync(new(request.MeetingDirectory, request.MeetingId, request.MeetingDurationMs,
                request.WavePath, request.ExpectedWaveSha256, LocalTranscriptionConfiguration.FromWhisper(request.Configuration), true)).GetAwaiter().GetResult();
            Need(result.Status == LocalTranscriptionStatus.Succeeded, hardware + "合成参数流程失败：" + result.Message);
            var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(result.AttemptDirectory, "whisper-output-arguments.json")))!;
            Need(args.Contains("-ng") == (hardware == LocalTranscriptionHardware.Cpu) && args.Contains("-dev") == (hardware != LocalTranscriptionHardware.Cpu),
                "GPU模式仍被-ng强制CPU，或CPU未显式禁GPU。");
            if (hardware != LocalTranscriptionHardware.Cpu) Need(args[Array.IndexOf(args, "-dev") + 1] == "3", "设备编号未传给CLI。");
            Need(args.Count(a => a == "--prompt") == 1 && args[Array.IndexOf(args, "--prompt") + 1] == prompt,
                "提示词被拆成CLI选项或被shell解释。");
            using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
            var data = attempt.RootElement;
            Need(data.GetProperty("requestedHardware").GetString() == hardware.ToString() && data.GetProperty("initialPrompt").GetString() == prompt &&
                data.GetProperty("gpuDeviceIndex").GetInt32() == 3 && data.GetProperty("modelHashPolicy").GetString() == "audit-only-no-expected-comparison" &&
                data.GetProperty("modelSha256").GetString() == M3TranscriptTests.Hash(request.Configuration.ModelPath), "尝试证据缺硬件/提示/审计hash。");
            var contracts = data.GetProperty("cliArgumentContractSources").EnumerateArray().Select(item => item.GetString()).ToArray();
            Need(contracts.Contains(WhisperTranscriber.VerifiedCliSource) && contracts.Contains(WhisperTranscriber.VerifiedForkCliSource) &&
                data.GetProperty("executableVersion").GetString() == "NotProbed" && data.GetProperty("executableBuilder").GetString() == "NotProbed" &&
                data.GetProperty("executableSha256").GetString() == M3TranscriptTests.Hash(request.Configuration.ExecutablePath) &&
                !data.TryGetProperty("cliSource", out _), "把适配器参数合同来源冒充成所选程序的版本或构建者。");
            if (hardware is LocalTranscriptionHardware.Vulkan or LocalTranscriptionHardware.Cuda)
                Need(data.GetProperty("actualGpuBackend").GetString() == reported && data.GetProperty("hardwareEvidence").GetString() == "SelectedGpuBackend",
                    "GPU选择不是来自stderr实际后端证据。");
            if (hardware == LocalTranscriptionHardware.Auto) Need(result.Message.Contains("CPU"), "自动回退未明确显示CPU。");
            Need(M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "ASR改变输入音频。");
        }
    }

    private static void RejectFallback(string root)
    {
        foreach (string reported in new[] { "none", "unreported", "CUDA0", "failed", "VulkanX" })
        {
            var request = Request(root, "reject-" + reported, reported);
            request = request with { Configuration = request.Configuration with { Hardware = LocalTranscriptionHardware.Vulkan } };
            var result = WhisperTranscriber.RunAsync(request).GetAwaiter().GetResult();
            Need(result.Status == "Failed" && result.Revision is null && result.Message.Contains("Vulkan") &&
                TranscriptService.ListRevisions(request.MeetingDirectory).Count == 0,
                "缺失/错误/初始化失败GPU被算作成功：" + reported + "/" + result.Message);
            Need(File.Exists(Path.Combine(result.AttemptDirectory, "attempt.json")) &&
                M3TranscriptTests.Hash(request.WavePath) == request.ExpectedWaveSha256, "GPU失败未保留证据或修改音频。");
        }
    }

    private static void Versions(string root)
    {
        var firstRequest = Request(root, "versions", "none");
        var first = WhisperTranscriber.RunAsync(firstRequest).GetAwaiter().GetResult();
        Need(first.Status == "Succeeded", "第一模型失败。");
        string original = Path.Combine(firstRequest.MeetingDirectory, first.Revision!.OriginalFile);
        string originalHash = M3TranscriptTests.Hash(original);
        var reviewed = TranscriptService.CreateRevision(firstRequest.MeetingDirectory, first.Revision.RevisionId,
            first.Revision.Segments.Select(s => s with { Text = "第一模型的人工修订" }).ToArray(), "M3D synthetic review");
        string secondModel = M3TranscriptTests.Input(firstRequest.MeetingDirectory, "second-model.bin", "synthetic-hardware-Vulkan0");
        var second = WhisperTranscriber.RunAsync(firstRequest with { Configuration = firstRequest.Configuration with {
            ModelPath = secondModel, ModelName = "synthetic-model-two", Hardware = LocalTranscriptionHardware.Vulkan, InitialPrompt = "synthetic financial ticker" } }).GetAwaiter().GetResult();
        var revisions = TranscriptService.ListRevisions(firstRequest.MeetingDirectory);
        Need(second.Status == "Succeeded" && second.Revision!.RevisionId != first.Revision.RevisionId &&
            second.AttemptDirectory != first.AttemptDirectory && revisions.Count == 3 && M3TranscriptTests.Hash(original) == originalHash &&
            TranscriptService.Load(firstRequest.MeetingDirectory, reviewed.RevisionId).Segments.Single().Text == "第一模型的人工修订",
            "后续模型覆盖原稿/人工校订或尝试目录。");
        Need(revisions.Select(r => r.RevisionId).Distinct().Count() == 3 && second.Revision!.ModelName == "synthetic-model-two",
            "模型版本标识或显示名称混淆。");
    }

    private static WhisperRequest Request(string root, string id, string reported)
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
        string model = M3TranscriptTests.Input(meeting, "model.bin", "synthetic-hardware-" + reported);
        return new(meeting, id, 1000, wave, M3TranscriptTests.Hash(wave), new(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"),
            model, ModelName: "synthetic-model-one", ModelLicenseAcknowledged: true), true);
    }

    private static void Need(bool condition, string message) => M3TranscriptTests.Need(condition, message);
}
