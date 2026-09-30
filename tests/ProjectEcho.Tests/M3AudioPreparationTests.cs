using System.Security.Cryptography;
using NAudio.Wave;
using ProjectEcho.Export;

internal static class M3AudioPreparationTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("M3-SYNTH ASR resampling keeps elapsed timeline, silence and source hash", () => Run(output));
    }

    private static void Run(string output)
    {
        string root = Path.Combine(output, "asr-preparation"); Directory.CreateDirectory(root);
        string source = Path.Combine(root, "synthetic-48k.wav");
        const int rate = 48000, frames = rate * 3 + 1;
        using (var writer = new WaveFileWriter(source, new WaveFormat(rate, 16, 1)))
            for (int i = 0; i < frames; i++)
            {
                double time = (double)i / rate;
                bool audible = time is >= .1 and < .3 or >= 2.6 and < 2.9;
                writer.WriteSample(audible ? (float)(.2 * Math.Sin(2 * Math.PI * 660 * time)) : 0);
            }
        string hash = Hash(source);
        var prepared = AsrAudioPreparation.Prepare(source, hash, Path.Combine(root, "inputs"));
        using var reader = new M2WaveReader(prepared.Path);
        if (reader.Format.SampleRate != 16000 || reader.Format.Channels != 1 || reader.Format.BitsPerSample != 16 ||
            Math.Abs(reader.FrameCount / 16000d - frames / (double)rate) > .0001)
            throw new Exception("ASR input format or full timeline changed.");
        var samples = reader.ReadFrames(0, (int)reader.FrameCount);
        static double Rms(float[] values, int start, int count) => Math.Sqrt(values.Skip(start).Take(count).Select(x => (double)x * x).Average());
        if (Rms(samples, 2400, 1600) < .1 || Rms(samples, 42400, 2400) < .1 || Rms(samples, 16000, 16000) != 0)
            throw new Exception("ASR input lost head/tail or filled the silent pause.");
        if (Hash(source) != hash || Hash(prepared.Path) != prepared.Sha256) throw new Exception("Input provenance mismatch.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { AsrAudioPreparation.Prepare(source, hash, Path.Combine(root, "cancelled"), cancelled.Token); throw new Exception("Cancelled task ran."); }
        catch (OperationCanceledException) { }
        try { AsrAudioPreparation.Prepare(source, new string('0', 64), Path.Combine(root, "tampered")); throw new Exception("Incorrect hash accepted."); }
        catch (InvalidDataException) { }
        string silent = Path.Combine(root, "silence.wav");
        using (var writer = new WaveFileWriter(silent, new WaveFormat(rate, 16, 1)))
            writer.Write(new byte[rate * 2]);
        var silence = AsrAudioPreparation.Prepare(silent, Hash(silent), Path.Combine(root, "silence-inputs"));
        using var silentReader = new M2WaveReader(silence.Path);
        if (silentReader.ReadFrames(0, (int)silentReader.FrameCount).Any(x => x != 0)) throw new Exception("Silence became speech-like PCM.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
