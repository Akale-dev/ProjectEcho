using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using ProjectEcho.Export;

internal static class TruePeakTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        run("T27-SYNTH ITU true-peak analytical phase references", Analytical);
        run("T27-SYNTH true-peak history, EOF and silence", Streaming);
        run("T27-SYNTH protected WAV independent 32x sinc and original hashes", () => Protection(output));
        run("T27-SYNTH bounded 60-second streaming meter", () => BoundedStreaming(output));
    }

    private static void Analytical()
    {
        // Independently synthesised mono analogues of EBU Tech 3341 signals 15–19.
        // No claim of having run the official downloaded EBU conformance test set.
        foreach (var (frequency, phase, amplitude) in new[]
        {
            (.25, 0d, .5), (.25, Math.PI / 4, .5), (1d/6, Math.PI/3, .5),
            (.125, 3*Math.PI/8, .5), (.25, Math.PI/4, Math.Sqrt(2))
        })
        {
            var signal = Tone(8192, frequency, phase, amplitude);
            var meter = new TruePeakMeter(48000); meter.AppendSamples(signal); var measurement = meter.Complete();
            double expected = 20 * Math.Log10(amplitude);
            Require(measurement.DbTp >= expected - .4 && measurement.DbTp <= expected + .2,
                $"ITU estimate outside EBU minimum phase-tone tolerance: {measurement.DbTp} versus {expected}.");
        }
        var intersample = Enumerable.Range(0, 4096).Select(i => (i % 4 < 2 ? .8f : -.8f)).ToArray();
        var counterexample = new TruePeakMeter(48000); counterexample.AppendSamples(intersample); var measured = counterexample.Complete();
        Require(measured.SamplePeak < .801 && measured.TruePeakLinear > 1.05,
            "[.8,.8,-.8,-.8] intersample overload was hidden by sample-peak detection.");
    }

    private static void Streaming()
    {
        var samples = Tone(1031, .45, .137, .85);
        samples[^1] = -.99f;
        var whole = new TruePeakMeter(48000); whole.AppendSamples(samples); var expected = whole.Complete();
        var chunked = new TruePeakMeter(48000);
        for (int offset = 0; offset < samples.Length; offset += 7) chunked.AppendSamples(samples.AsSpan(offset, Math.Min(7, samples.Length-offset)));
        var actual = chunked.Complete();
        Require(actual == expected && chunked.Complete() == actual, "FIR state/EOF flush changed with read boundaries.");
        Require(actual.FrameCount == samples.Length, "FIR flush padded the content frame count.");
        var silence = new TruePeakMeter(32000); silence.AppendSamples(new float[1024]); var empty = silence.Complete();
        Require(empty.TruePeakLinear == 0 && empty.SamplePeak == 0 && empty.DbTp is null, "Silence produced invalid dB/NaN metadata.");
        Require(!JsonSerializer.Serialize(empty).Contains("Infinity"), "Silence metadata is not JSON-safe.");
        bool invalidRejected = false;
        try { var bad = new TruePeakMeter(48000); bad.AppendSamples([float.NaN]); }
        catch (InvalidDataException) { invalidRejected = true; }
        Require(invalidRejected, "Nonfinite samples were accepted.");
        using var cancellation = new CancellationTokenSource();
        using var cancellableStream = new CancelAfterFirstReadStream(new byte[131072], cancellation);
        bool cancelled = false;
        try { TruePeakProtection.AnalyzePcm16(cancellableStream, 48000, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && cancellableStream.ReadCount == 1, "File scan ignored cancellation between bounded chunks.");
    }

    private static void Protection(string output)
    {
        var root = Path.Combine(output, "truepeak"); Directory.CreateDirectory(root);
        var fixtures = new List<(string Name, float[] Samples)>
        {
            ("intersample", Enumerable.Range(0, 4096).Select(i => (i % 4 < 2 ? .8f : -.8f)).ToArray()),
            ("silence", new float[1024]),
            ("boundary-impulses", Enumerable.Range(0, 1031).Select(i => i is 0 or 1029 ? .99f : i is 1 or 1030 ? -.99f : 0).ToArray()),
            ("near-nyquist", Tone(4096, .49, .173, .98)),
            ("high-frequency", Tone(4096, .45, Math.PI / 8, .98)),
            ("third-band", Tone(4096, 1d/3, Math.PI / 6, .98)),
            ("phase-offset", Tone(4096, .25, Math.PI / 4, 1.12))
        };
        var evidence = new List<object>();
        foreach (var (name, samples) in fixtures)
        {
            var source = Path.Combine(root, name + ".wav"); WriteWave(source, samples);
            var hash = SHA256.HashData(File.ReadAllBytes(source));
            var result = TruePeakProtection.ProtectWave(source, Path.Combine(root, name + "-safe.wav"));
            Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Protection modified source WAV.");
            var decoded = ReadWave(result.Path);
            var reference = ReferencePeak32x(decoded);
            Require(reference <= Math.Pow(10, -1d/20) + 1e-6, $"Independent 32x reconstruction exceeds -1 dBTP: {name}, {20*Math.Log10(reference)}.");
            Require(result.After.FrameCount == samples.Length && decoded.Length == samples.Length, "Protection changed elapsed frames.");
            Require(result.After.TruePeakLinear <= TruePeakProtection.ProcessingCeilingLinear + 1e-7, "Protected meter above processing ceiling.");
            evidence.Add(new { name, result.Before, result.After, result.AppliedGain, result.AttenuationDb,
                reference32xPeak = reference, reference32xDbTp = reference == 0 ? (double?)null : 20*Math.Log10(reference) });
        }
        var cancelledOutput = Path.Combine(root, "cancelled.wav");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        bool rejected = false;
        try { TruePeakProtection.ProtectWave(Path.Combine(root, "silence.wav"), cancelledOutput, cancel.Token); }
        catch (OperationCanceledException) { rejected = true; }
        Require(rejected && !File.Exists(cancelledOutput), "Cancelled protection published output.");
        File.WriteAllText(Path.Combine(root,"truepeak-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void BoundedStreaming(string output)
    {
        // The source synthesises bounded chunks; no minute-sized PCM array or oversampled output is allocated.
        using var input = new GeneratedPcmStream(48_000L * 60);
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = TruePeakProtection.AnalyzePcm16(input, 48000);
        var elapsed = watch.Elapsed.TotalSeconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        Require(result.FrameCount == 2_880_000 && result.TruePeakLinear > 1.05,"Streaming detector missed full generated input.");
        Require(allocated < 2_000_000,"True-peak scanning allocation scales with full timeline length.");
        var root = Path.Combine(output,"truepeak"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"streaming-performance.json"),JsonSerializer.Serialize(new
        {
            syntheticDurationSeconds=60,actualFrames=result.FrameCount,elapsedSeconds=elapsed,allocatedBytes=allocated,
            bufferBytes=32768,firHistorySamples=24,twoHourExecution="NOT_RUN by this benchmark; bounded memory derives from streaming implementation"
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    internal static float[] Tone(int count, double cyclesPerSample, double phase, double amplitude)
    {
        var samples = new float[count];
        for (int index=0; index<count; index++)
        {
            double taper = Math.Min(1, Math.Min(index/480d, (count-1-index)/480d));
            samples[index] = (float)(amplitude * taper * Math.Sin(2*Math.PI*cyclesPerSample*index + phase));
        }
        return samples;
    }

    // Independent reconstruction: 32 phases × 128-sample Blackman-windowed sinc.
    // Deliberately does not use the production FIR coefficients, history ring or meter implementation.
    internal static double ReferencePeak32x(float[] source)
    {
        const int radius=64, phases=32;
        var kernels = new double[phases][];
        for (int phase=0;phase<phases;phase++)
        {
            kernels[phase]=new double[2*radius];
            double sum=0;
            for(int tap=0;tap<2*radius;tap++)
            {
                double distance=(tap-radius+1)-(double)phase/phases;
                double window=.42+.5*Math.Cos(Math.PI*distance/radius)+.08*Math.Cos(2*Math.PI*distance/radius);
                double coefficient=Math.Abs(distance)<1e-14?1:Math.Sin(Math.PI*distance)/(Math.PI*distance)*window;
                if(Math.Abs(distance)>radius) coefficient=0;
                kernels[phase][tap]=coefficient; sum+=coefficient;
            }
            for(int tap=0;tap<2*radius;tap++) kernels[phase][tap]/=sum;
        }
        double peak=0;
        for(int frame=-radius;frame<source.Length+radius;frame++)
        for(int phase=0;phase<phases;phase++)
        {
            double value=0;
            for(int tap=0;tap<2*radius;tap++)
            {
                int index=frame+tap-radius+1;
                if((uint)index<(uint)source.Length) value+=source[index]*kernels[phase][tap];
            }
            peak=Math.Max(peak,Math.Abs(value));
        }
        return peak;
    }

    internal static void WriteWave(string path,float[] samples)
    {
        using var writer = new WaveFileWriter(path,new WaveFormat(48000,16,1));
        var bytes = new byte[samples.Length*2];
        for(int index=0;index<samples.Length;index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index*2), (short)Math.Round(Math.Clamp(samples[index]*32768d,short.MinValue,short.MaxValue)));
        writer.Write(bytes);
    }
    internal static float[] ReadWave(string path)
    {
        using var reader = new WaveFileReader(path);
        var result = new float[reader.Length/2];
        for(int index=0;index<result.Length;index++) result[index]=reader.ReadNextSampleFrame()[0];
        return result;
    }
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }

    private sealed class CancelAfterFirstReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public int ReadCount { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            ReadCount++;
            var count = base.Read(buffer);
            cancellation.Cancel();
            return count;
        }
    }

    private sealed class GeneratedPcmStream(long frames) : Stream
    {
        private long position;
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>frames*2;
        public override long Position {get=>position;set=>throw new NotSupportedException();}
        public override int Read(Span<byte> buffer)
        {
            int count=(int)Math.Min(buffer.Length,Length-position); count-=count%2;
            for(int index=0;index<count;index+=2)
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(index,2),
                    (short)(((position/2+index/2)%4<2)?26214:-26214));
            position+=count;return count;
        }
        public override int Read(byte[] buffer,int offset,int count)=>Read(buffer.AsSpan(offset,count));
        public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
