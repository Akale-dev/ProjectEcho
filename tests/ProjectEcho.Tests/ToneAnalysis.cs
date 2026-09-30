using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;

/// <summary>Offline measurements only; never activates audio or rewrites a WAV.</summary>
internal static class ToneAnalysis
{
    public static int Run(string[] args)
    {
        try
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
            if (root is null || args.Length != 2) throw new ArgumentException("Usage: --tone-report <project-local WAV>");
            var input = SafePath(root.FullName, args[1]);
            if (!string.Equals(Path.GetExtension(input), ".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            var diagnostics = SafePath(root.FullName, Path.Combine(root.FullName, ".local", "diagnostics"));
            using var reader = new WaveFileReader(input);
            var format = reader.WaveFormat;
            var tag = format is WaveFormatExtensible ext ? ext.SubFormat : new Guid((int)format.Encoding, 0, 0x0010, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
            var isFloat = tag == new Guid("00000003-0000-0010-8000-00aa00389b71");
            var width = format.BitsPerSample / 8;
            if ((!isFloat && tag != new Guid("00000001-0000-0010-8000-00aa00389b71")) ||
                (isFloat ? format.BitsPerSample != 32 : format.BitsPerSample is not (8 or 16 or 24 or 32)) ||
                format.BlockAlign != format.Channels * width) throw new InvalidDataException("Unsupported PCM/float format.");
            if (format.SampleRate is < 2000 or > 192000 || format.Channels is < 1 or > 8 ||
                reader.Length <= 0 || reader.Length % format.BlockAlign != 0 || reader.TotalTime.TotalSeconds > 300)
                throw new InvalidDataException("Expected a nonempty PCM/float M0 WAV, at most 300 seconds.");
            var mono = new double[format.SampleRate];
            var seconds = new List<SecondResult>();
            long frames = 0, nonzeroFrames = 0;
            double peak = 0, sumSquares = 0, channelPeak = 0;
            var used = 0;
            var buffer = new byte[format.SampleRate * format.BlockAlign];
            int bytes;
            while ((bytes = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes % format.BlockAlign != 0) throw new InvalidDataException();
                for (var offset = 0; offset < bytes; offset += format.BlockAlign)
                {
                double value = 0;
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sample = Sample(buffer.AsSpan(offset + channel * width, width), isFloat);
                    if (!double.IsFinite(sample)) throw new InvalidDataException();
                    value += sample / format.Channels;
                    channelPeak = Math.Max(channelPeak, Math.Abs(sample));
                }
                mono[used++] = value;
                frames++;
                if (value != 0) nonzeroFrames++;
                peak = Math.Max(peak, Math.Abs(value));
                sumSquares += value * value;
                if (used == mono.Length)
                {
                    seconds.Add(Measure(seconds.Count, mono, used, format.SampleRate));
                    used = 0;
                }
                }
            }
            if (used > 0) seconds.Add(Measure(seconds.Count, mono, used, format.SampleRate));
            if (frames != reader.Length / format.BlockAlign) throw new InvalidDataException("Incomplete WAV read.");
            using var original = File.OpenRead(input);
            var report = new
            {
                checkedAtUtc = DateTimeOffset.UtcNow,
                sourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
                sampleRate = format.SampleRate, channels = format.Channels, bitsPerSample = format.BitsPerSample,
                format = format.Encoding.ToString(), frames, durationSeconds = frames / (double)format.SampleRate,
                monoMethod = "Arithmetic channel mean; opposite channel phases may cancel.",
                monoPeak = peak, monoPeakDbfs = Db(peak), monoRms = Math.Sqrt(sumSquares / frames),
                monoRmsDbfs = Db(Math.Sqrt(sumSquares / frames)), originalChannelPeak = channelPeak, nonzeroFrames,
                nonSilentThresholdRmsDbfs = -60, nonSilentSeconds = seconds.Count(s => s.NonSilent),
                window = "Non-overlapping one-second Hann; final partial window uses its actual length.",
                toneAmplitude = "Goertzel coherent-gain-corrected sinusoidal peak amplitude; dBFS uses peak=1. Null dBFS means exactly zero.",
                audiblePlayback = "NOT_ESTABLISHED", platformAcceptance = "NOT_ESTABLISHED",
                limitations = "Offline signal evidence only; does not prove listening, source attribution, browser isolation, microphone privacy or absence of other frequencies.",
                seconds
            };
            Directory.CreateDirectory(diagnostics);
            var path = Path.Combine(diagnostics, $"tone-report-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"Tone report: {path}");
            Console.WriteLine($"Frames={frames}; seconds={report.durationSeconds:F3}; mono peak={peak:F6}; mono RMS={report.monoRms:F6}; non-silent windows={report.nonSilentSeconds}");
            Console.WriteLine("Offline measurement only; actual listening and platform acceptance are not established.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tone report failed: {ex.GetType().Name}. Use --tone-report <project-local PCM/float WAV>; no original was written.");
            return 1;
        }
    }

    private static SecondResult Measure(int second, double[] samples, int count, int rate)
    {
        double square = 0, peak = 0, weightSum = 0;
        var windowed = new double[count];
        for (var i = 0; i < count; i++)
        {
            var weight = count <= 2 ? 1 : .5 * (1 - Math.Cos(2 * Math.PI * i / (count - 1)));
            windowed[i] = samples[i] * weight;
            weightSum += weight;
            square += samples[i] * samples[i];
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }
        var tones = new[] { 440, 660, 880 }.Select(frequency =>
        {
            var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / rate);
            double previous = 0, beforePrevious = 0;
            foreach (var sample in windowed)
            {
                var current = sample + coefficient * previous - beforePrevious;
                beforePrevious = previous;
                previous = current;
            }
            var power = previous * previous + beforePrevious * beforePrevious - coefficient * previous * beforePrevious;
            var amplitude = 2 * Math.Sqrt(Math.Max(0, power)) / weightSum;
            return new ToneResult(frequency, amplitude, Db(amplitude));
        }).ToArray();
        var rms = Math.Sqrt(square / count);
        return new(second, count / (double)rate, peak, rms, Db(rms), rms >= .001, tones);
    }

    private static double? Db(double value) => value > 0 ? 20 * Math.Log10(value) : null;
    private static double Sample(ReadOnlySpan<byte> value, bool isFloat) => isFloat
        ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
        : value.Length switch
        {
            1 => (value[0] - 128) / 128d,
            2 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768d,
            3 => ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608d,
            4 => BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648d,
            _ => throw new InvalidDataException()
        };
    private static string SafePath(string root, string supplied)
    {
        var full = Path.GetFullPath(supplied);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        for (FileSystemInfo? current = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full); current is not null;)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return full;
    }
    private sealed record ToneResult(int FrequencyHz, double PeakAmplitude, double? PeakDbfs);
    private sealed record SecondResult(int StartSecond, double DurationSeconds, double Peak, double Rms, double? RmsDbfs, bool NonSilent, ToneResult[] Tones);
}
