using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ProjectEcho.Export;

public sealed record AsrAudioInput(string Path, string Sha256, long Frames, long SourceFrames, int SourceSampleRate);

/// <summary>File-only ASR input preparation. The complete elapsed timeline is retained; no capture dependency.</summary>
public static class AsrAudioPreparation
{
    public static AsrAudioInput Prepare(string sourceWave, string expectedSha256, string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = M2FileSafety.FullPath(sourceWave), root = M2FileSafety.FullPath(destinationRoot);
        if (expectedSha256.Length != 64) throw new InvalidDataException("缺少已校验混音的哈希。");
        using var reader = new M2WaveReader(source);
        if (reader.Format.Channels != 1 || reader.Format.BitsPerSample != 16 || reader.Format.IsFloat || reader.FrameCount <= 0)
            throw new InvalidDataException("识别输入须来自完整的单声道 PCM16 混音。");
        if (!string.Equals(M2FileSafety.Hash(source, cancellationToken), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("混音文件已改变，请重新生成后再识别。");
        long expectedFrames = checked((long)Math.Round((double)reader.FrameCount * 16000 / reader.Format.SampleRate));
        if (expectedFrames < 1 || expectedFrames > (uint.MaxValue - 36L) / 2)
            throw new InvalidDataException("识别副本超出 WAV 支持长度。");
        M2FileSafety.RequireDiskSpace(root, checked(expectedFrames * 2 + 4096));
        Directory.CreateDirectory(root);
        string folder = M2FileSafety.Inside(root, "input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = M2FileSafety.Inside(folder, "audio-16k.wav"), partial = path + ".partial";
        try
        {
            using (var input = new RawSourceWaveStream(reader, new WaveFormat(reader.Format.SampleRate, 16, 1)))
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, true))
            {
                IWaveProvider converted = reader.Format.SampleRate == 16000 ? input
                    : new SampleToWaveProvider16(new WdlResamplingSampleProvider(input.ToSampleProvider(), 16000));
                writer.Write(0x46464952u); writer.Write(0u); writer.Write(0x45564157u);
                writer.Write(0x20746d66u); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(16000); writer.Write(32000); writer.Write((ushort)2); writer.Write((ushort)16);
                writer.Write(0x61746164u); writer.Write(0u);
                byte[] buffer = new byte[32000]; long bytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = converted.Read(buffer);
                    if (count == 0) break;
                    if (count % 2 != 0 || bytes + count > (expectedFrames + 1) * 2)
                        throw new InvalidDataException("识别重采样输出帧数异常。");
                    writer.Write(buffer, 0, count); bytes += count;
                }
                if (Math.Abs(bytes / 2 - expectedFrames) > 1 || bytes == 0)
                    throw new InvalidDataException("识别副本未覆盖完整时间轴。");
                file.Position = 4; writer.Write(checked((uint)(36 + bytes)));
                file.Position = 40; writer.Write(checked((uint)bytes)); writer.Flush(); file.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var verification = new M2WaveReader(partial))
                if (verification.Format.SampleRate != 16000 || Math.Abs(verification.FrameCount - expectedFrames) > 1)
                    throw new InvalidDataException("识别副本未通过格式和时长校验。");
            File.Move(partial, path, false);
            string hash = M2FileSafety.Hash(path, cancellationToken);
            using var final = new M2WaveReader(path);
            return new(path, hash, final.FrameCount, reader.FrameCount, reader.Format.SampleRate);
        }
        catch
        {
            M2FileSafety.DeleteGenerated(folder, partial);
            M2FileSafety.DeleteGenerated(folder, path);
            throw;
        }
    }
}
