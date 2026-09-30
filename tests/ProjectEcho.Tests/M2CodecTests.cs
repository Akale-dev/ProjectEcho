using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using NAudio.Wave;
using ProjectEcho.Export;

/// <summary>Real Windows codecs with generated PCM only. No capture, playback, devices or network.</summary>
internal static class M2CodecTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("T28/T29-CODEC M4A EOF/first-last samples and padding", () => RoundTrip(output));
        run("T28-CODEC M4A cancellation and overwrite protection", () => Boundaries(output));
        run("T27-CODEC AAC float decode true-peak protection and independent reconstruction", () => TruePeakRoundTrip(output));
        run("T27/T35-CODEC small RF64 true-peak copy and actual AAC encode/decode", () => Rf64RoundTrip(output));
    }

    private static void RoundTrip(string output)
    {
        var root = Path.Combine(output, "m2-codec");
        Directory.CreateDirectory(root);
        var evidence = new List<object>();
        foreach (var rate in new[] { 48_000, 32_000 })
        foreach (var frames in new[] { 1, rate / 50, (int)(rate * .137), (int)(rate * 3.217), (int)(rate * 30.125) })
        {
            var path = Path.Combine(root, $"noise-{rate}-{frames}.wav");
            var source = WriteSignal(path, rate, frames);
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var encoded = new MediaFoundationM4aEncoder(rate, rate == 48_000 ? 96_000 : 64_000)
                .Encode(path, Path.ChangeExtension(path, ".m4a"));
            Require(encoded.SourceFrames == frames, "Content frame count changed.");
            Require(encoded.DecodedFrames == encoded.SourceFrames + encoded.TrailingPaddingFrames, "Padding accounting failed.");
            Require(encoded.TrailingPaddingFrames is >= 2048 and < 3072, "Unexpected AAC flush padding.");
            Require(encoded.ByteLength == new FileInfo(encoded.Path).Length, "Actual byte length mismatch.");
            Require(encoded.TruePeak is not null && encoded.TruePeak.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear,
                "AAC output lacks complete decoded true-peak verification.");
            Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Codec modified its input.");
            var decoded = ReadPcm(encoded.Path);
            double? firstCorrelation = null, tailCorrelation = null;
            int? bestDelay = null;
            // A one-frame file proves container completeness, not perceptual fidelity or a measurable marker.
            if (frames >= rate / 10)
            {
                var count = Math.Min(4096, frames);
                firstCorrelation = Correlation(source, decoded, 0, count, 0);
                tailCorrelation = Correlation(source, decoded, frames - count, count, 0);
                Require(firstCorrelation > .8 && tailCorrelation > .8, "First/tail content missing or shifted after AAC decode.");
                bestDelay = Enumerable.Range(0, 1537).MaxBy(delay => Correlation(source, decoded, 0, count, delay));
                Require(bestDelay == 0, "AAC priming changed local/global alignment.");
            }
            evidence.Add(new { rate, frames, encoded.ByteLength, encoded.DecodedFrames, encoded.TrailingPaddingFrames,
                firstCorrelation, tailCorrelation, bestDelay, status = "PASS" });
        }
        var resample = Path.Combine(root, "resample.wav");
        WriteSignal(resample, 48_000, 154_416);
        var converted = new MediaFoundationM4aEncoder(32_000, 64_000).Encode(resample, Path.ChangeExtension(resample, ".m4a"));
        Require(Math.Abs(converted.SourceFrames - 102_944) <= 1, "Resampling shortened source duration.");
        evidence.Add(new { kind = "48000-to-32000", converted.SourceFrames, converted.DecodedFrames, status = "PASS" });
        File.WriteAllText(Path.Combine(root, "codec-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TruePeakRoundTrip(string output)
    {
        var root = Path.Combine(output, "m2-codec-truepeak"); Directory.CreateDirectory(root);
        var fixtures = new[]
        {
            ("intersample", Enumerable.Range(0, 8192).Select(index => index % 4 < 2 ? .8f : -.8f).ToArray()),
            ("phase-tone", TruePeakTests.Tone(8192,.25,Math.PI/4,1.2)),
            ("high-tone", TruePeakTests.Tone(8192,.45,.137,.99)),
            ("impulses", Enumerable.Range(0,4096).Select(index=>index%127==0?.99f:index%127==1?-.99f:0).ToArray()),
            ("silence", new float[1024])
        };
        var evidence = new List<object>();
        foreach(var (name,samples) in fixtures)
        {
            var source = Path.Combine(root,name+".wav"); TruePeakTests.WriteWave(source,samples);
            var hash = SHA256.HashData(File.ReadAllBytes(source));
            var result = new MediaFoundationM4aEncoder().Encode(source,Path.ChangeExtension(source,".m4a"));
            Require(result.TruePeak is not null && result.TruePeak.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear,
                "Compressed output exceeded true-peak processing ceiling.");
            Require(result.EncodingAttempts is >= 1 and <= MediaFoundationM4aEncoder.MaximumEncodingAttempts,
                "Unbounded re-encode or invalid attempt metadata.");
            if(name=="intersample") Require(result.EncodingAttempts>1 && result.AppliedGain<1,
                "Pathological AAC overload was not attenuated/re-encoded.");
            using var reader = new MediaFoundationReader(result.Path,
                new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            Require(reader.WaveFormat.Encoding==WaveFormatEncoding.IeeeFloat,"Reference decode clipped to PCM16 before measurement.");
            using var memory = new MemoryStream();
            var bytes = new byte[16384]; int count;
            while((count=reader.Read(bytes,0,bytes.Length))>0) memory.Write(bytes,0,count);
            var decoded = MemoryMarshal.Cast<byte,float>(memory.ToArray()).ToArray();
            var independent = TruePeakTests.ReferencePeak32x(decoded);
            Require(independent<=Math.Pow(10,-1d/20)+1e-6,$"AAC independent 32x true peak exceeded -1 dBTP: {name}.");
            Require(decoded.LongLength==result.DecodedFrames && result.SourceFrames==samples.Length,"True-peak retry changed frame coverage.");
            Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"True-peak retry modified original WAV.");
            evidence.Add(new {name,result.TruePeak,result.EncodingAttempts,result.AppliedGain,result.DecodedFrames,
                reference32xPeak=independent,reference32xDbTp=independent==0?(double?)null:20*Math.Log10(independent)});
        }
        File.WriteAllText(Path.Combine(root,"aac-truepeak-evidence.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void Rf64RoundTrip(string output)
    {
        var root = Path.Combine(output,"m2-codec-rf64"); Directory.CreateDirectory(root);
        var pcm = new byte[8192];
        for(int index=0;index<pcm.Length;index+=2)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index,2),
                (short)(index/2%4<2?26214:-26214));
        var source = Path.Combine(root,"synthetic-rf64.wav");
        File.WriteAllBytes(source,M2LargeWaveTests.CreateRf64Bytes(pcm));
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var protectedWave = TruePeakProtection.ProtectWave(source,Path.Combine(root,"safe-rf64.wav"));
        using(var check = new M2WaveReader(protectedWave.Path))
            Require(check.IsRf64 && check.FrameCount==4096 && check.DataOffset==80,"True-peak copy corrupted RF64 container/header.");
        Require(File.ReadAllBytes(source).AsSpan(0,80).SequenceEqual(File.ReadAllBytes(protectedWave.Path).AsSpan(0,80)),
            "Fixed-gain protection altered RF64 ds64/header metadata.");
        var encoded = new MediaFoundationM4aEncoder().Encode(source,Path.Combine(root,"from-rf64.m4a"));
        Require(encoded.SourceFrames==4096 && encoded.TruePeak is not null &&
            encoded.TruePeak.TruePeakLinear<=TruePeakProtection.ProcessingCeilingLinear,"RF64 wrapping shortened PCM or missed AAC peaks.");
        Require(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"RF64 codec/protection changed original.");
        File.WriteAllText(Path.Combine(root,"rf64-codec-evidence.json"),JsonSerializer.Serialize(new
        {
            inputContainer="RF64",inputFrames=4096,headerPreserved=true,sourceHashPreserved=true,
            protectedWave.After,encoded.SourceFrames,encoded.DecodedFrames,encoded.EncodingAttempts,encoded.AppliedGain,encoded.TruePeak,
            largeRf64WholeFileEncoding="NOT_RUN; large cross-4GiB WAV extraction has separate evidence"
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void Boundaries(string output)
    {
        var root = Path.Combine(output, "m2-codec-boundaries");
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.wav");
        WriteSignal(input, 48_000, 48_000 * 30);
        var encoder = new MediaFoundationM4aEncoder();
        var target = Path.Combine(root, "cancelled.m4a");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Reject<OperationCanceledException>(() => encoder.Encode(input, target, cancelled.Token));
        Require(!File.Exists(target), "Cancelled encode published a file.");
        using var midflight = new CancellationTokenSource();
        midflight.CancelAfter(5);
        Reject<OperationCanceledException>(() => encoder.Encode(input, target, midflight.Token));
        Require(!File.Exists(target) && !Directory.EnumerateFiles(root, ".encoding-*").Any(), "Cancelled encode left temporary output.");
        File.WriteAllText(target, "protected-existing-file");
        Reject<IOException>(() => encoder.Encode(input, target));
        Require(File.ReadAllText(target) == "protected-existing-file", "Existing destination overwritten.");
        Reject<IOException>(() => encoder.Encode(input, input));
        Reject<ArgumentOutOfRangeException>(() => new MediaFoundationM4aEncoder(44_100));
    }

    private static short[] WriteSignal(string path, int rate, int frames)
    {
        var result = new short[frames];
        var random = new Random(73);
        double filtered = 0;
        var bytes = new byte[frames * 2];
        for (var index = 0; index < frames; index++)
        {
            filtered = filtered * .6 + (random.NextDouble() - .5) * .4;
            result[index] = (short)(filtered * 15_000);
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 2), result[index]);
        }
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        writer.Write(bytes);
        return result;
    }

    private static short[] ReadPcm(string path)
    {
        using var reader = new MediaFoundationReader(path);
        using var pcm = new MemoryStream();
        var buffer = new byte[32_768];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, read);
        var bytes = pcm.ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return samples;
    }

    private static double Correlation(short[] original, short[] decoded, int start, int count, int delay)
    {
        double cross = 0, originalEnergy = 0, decodedEnergy = 0;
        for (var index = start; index < start + count; index++)
        {
            if (index + delay >= decoded.Length) return -1;
            double left = original[index], right = decoded[index + delay];
            cross += left * right;
            originalEnergy += left * left;
            decodedEnergy += right * right;
        }
        return cross / Math.Sqrt(originalEnergy * decodedEnergy);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
