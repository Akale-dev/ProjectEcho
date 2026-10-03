using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>
/// Reads the selected backend from the independently checked ggml-org b5130 and Lemonade
/// whisper.cpp-rocm v1.8.4 stderr contracts. A loaded DLL or an enumerated GPU is not
/// proof that whisper selected it. Explicit modes reject fallback before importing a revision.
/// </summary>
internal sealed class WhisperHardwareParser(LocalTranscriptionHardware requested)
{
    public const string Policy = "whisper-selected-gpu-backend-stderr-v2";
    private const string Prefix = "whisper_backend_init_gpu: ";
    private readonly StringBuilder line = new();
    private bool overlong;
    private bool noGpu;
    private bool failed;
    public string? ActualGpuBackend { get; private set; }
    public string Evidence => failed ? "GpuInitializationFailed" : noGpu ? "NoGpuSelected" :
        ActualGpuBackend is not null ? "SelectedGpuBackend" : "NotReported";
    private bool RequiresGpu => requested is LocalTranscriptionHardware.Vulkan or LocalTranscriptionHardware.Cuda;

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is '\r' or '\n') { ReadLine(); continue; }
            if (overlong) continue;
            if (line.Length >= 1024) { line.Clear(); overlong = true; }
            else line.Append(character);
        }
    }

    public void Complete() => ReadLine();

    public void EnsureRequestedBackend()
    {
        if (!RequiresGpu) return;
        string expected = requested == LocalTranscriptionHardware.Vulkan ? "Vulkan" : "CUDA";
        if (failed || noGpu || ActualGpuBackend is null || !IsBackend(ActualGpuBackend, expected))
            throw new InvalidDataException($"已选择 {expected}，但本次引擎未确认使用该后端（{(failed ? "GPU 初始化失败" : noGpu ? "引擎未找到可用 GPU" : ActualGpuBackend ?? "未报告GPU")}）。请检查所选 whisper.cpp 程序包含该后端、显卡驱动和设备编号；随包 CPU 程序不能用于 Vulkan。没有将 CPU 回退记为成功。");
    }

    public string Describe() => ActualGpuBackend is not null && !failed && !noGpu ? $"引擎报告使用 {ActualGpuBackend}。" :
        requested == LocalTranscriptionHardware.Cpu ? "已请求 CPU 模式。" :
        noGpu || failed ? "自动模式：引擎未使用 GPU，按 CPU 路径完成。" : "自动模式：引擎未报告实际硬件后端。";

    private void ReadLine()
    {
        if (!overlong)
        {
            string value = line.ToString().Trim();
            line.Clear();
            if (value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                string detail = value[Prefix.Length..];
                if (detail == "no GPU found") { noGpu = true; EnsureRequestedBackend(); }
                else if (detail.StartsWith("using ", StringComparison.Ordinal) && detail.EndsWith(" backend", StringComparison.Ordinal))
                {
                    ActualGpuBackend = detail[6..^8];
                    EnsureRequestedBackend();
                    if (requested == LocalTranscriptionHardware.Cpu)
                        throw new InvalidDataException("引擎忽略了 CPU 选项并选择 GPU；未将硬件不符的尝试保存为成功版本。");
                }
                else if (detail.StartsWith("failed to initialize ", StringComparison.Ordinal))
                { failed = true; EnsureRequestedBackend(); }
            }
            // This diagnostic occurs after backend setup; do not run a full CPU fallback when
            // an explicit backend was requested but this CLI never reports the selected GPU.
            else if (value.StartsWith("main: processing '", StringComparison.Ordinal)) EnsureRequestedBackend();
        }
        line.Clear(); overlong = false;
    }

    private static bool IsBackend(string name, string expected) => name.StartsWith(expected, StringComparison.Ordinal) &&
        name.AsSpan(expected.Length).IndexOfAnyExceptInRange('0', '9') < 0;
}
