namespace ProjectEcho.Core.Transcription;

internal static class QwenOfficialAlignmentModel
{
    // This route supports the separately configured official 0.6B checkpoint, not arbitrary Python code/model repositories.
    private static readonly string[] Required = ["model.safetensors", "config.json", "preprocessor_config.json", "tokenizer_config.json", "vocab.json", "merges.txt"];

    internal static string[] Files(string directory)
    {
        directory = TranscriptFiles.LocalPath(directory);
        if (!Directory.Exists(directory)) throw new InvalidDataException("官方 Python 定位模型目录不存在；程序不会自动下载。");
        TranscriptFiles.NoReparse(directory);
        if (Required.Any(name => !File.Exists(Path.Combine(directory, name))))
            throw new InvalidDataException("官方 Python 定位需要完整的 Qwen3-ForcedAligner-0.6B 目录，不能选择 GGUF 或仅有权重的目录。");
        string[] files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (files.Length > 64) throw new InvalidDataException("定位模型目录文件过多，请选择单独的官方模型目录。");
        long total = 0;
        foreach (string file in files)
        {
            TranscriptFiles.NoReparse(file); long length = new FileInfo(file).Length;
            if (length <= 0 || (total += length) > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("官方定位模型目录存在空文件或超过 8 GiB 限制。");
        }
        return files;
    }
}
