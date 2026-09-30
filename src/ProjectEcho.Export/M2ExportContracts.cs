namespace ProjectEcho.Export;

public enum M2ExportFormat { Wave, M4a, Mp3 }

public sealed record FullMixResult(string WavePath, string? CompressedPath, string Directory,
    int SampleRate, long FrameCount, long DurationUs, IReadOnlyList<string> Warnings)
{
    public string WaveSha256 { get; init; } = "";
    public string? CompressedSha256 { get; init; }
    public bool SourceStorageComplete { get; init; }
    public TruePeakMeasurement? WaveTruePeak { get; init; }
    public double WaveAppliedGain { get; init; } = 1;
}

public sealed record ExportOptions(M2ExportFormat Format = M2ExportFormat.M4a,
    long MaxFileBytes = 20_000_000, double MaxDurationSeconds = 1800,
    double OverlapSeconds = 5, bool IncludeManifest = false);

public sealed record ExportPartResult(string Path, long ByteLength, long StartUs, long EndUs,
    long OverlapWithPreviousUs, string Sha256, double DecodedDurationSeconds)
{
    public long StartFrame { get; init; }
    public long EndFrame { get; init; }
    public long EncoderDelayFrames { get; init; }
    public long TrailingPaddingFrames { get; init; }
    public string? TimingWarning { get; init; }
    public TruePeakMeasurement? TruePeak { get; init; }
    public double AppliedGain { get; init; } = 1;
    public int EncodingAttempts { get; init; } = 1;
    public M4aContainerTimeline? ContainerTimeline { get; init; }
    public Mp3FrameTimeline? Mp3Timeline { get; init; }
}

public sealed record ExportResult(string Directory, IReadOnlyList<ExportPartResult> Parts,
    string? ManifestPath, long TotalBytes)
{
    public string? LocalReceiptPath { get; init; }
}
