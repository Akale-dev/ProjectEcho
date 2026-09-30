using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ProjectEcho.Core;

[assembly: InternalsVisibleTo("ProjectEcho.Tests")]

namespace ProjectEcho.Export;

/// <summary>
/// Read-only PCM-relative stream for validated RIFF/WAVE and RF64/WAVE files.
/// RF64 follows EBU Tech 3306-2009 sections 3.4 and A.2:
/// https://tech.ebu.ch/docs/tech/tech3306v1_1.pdf
/// Length/Position exclude all container headers; all file/PCM offsets remain Int64.
/// The M0 reader and its compatibility behavior are deliberately separate.
/// </summary>
public sealed class M2WaveReader : Stream
{
    private const uint Riff = 0x46464952, Rf64 = 0x34364652, Wave = 0x45564157;
    private const uint Ds64 = 0x34367364, Fmt = 0x20746d66, Data = 0x61746164, Fact = 0x74636166;
    private readonly FileStream file;
    private bool disposed;
    private long position;
    public PcmFormat Format { get; private set; } = null!;
    public long FrameCount => DataLength / Format.BlockAlign;
    public long DataOffset { get; private set; }
    public long DataLength { get; private set; }
    public bool IsRf64 { get; private set; }

    public M2WaveReader(string path, PcmFormat? expected = null)
    {
        M2FileSafety.NoReparse(path);
        file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        try { ReadHeader(expected); file.Position = DataOffset; }
        catch { file.Dispose(); throw; }
    }

    private void ReadHeader(PcmFormat? expected)
    {
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, true);
        if (file.Length < 12) throw Invalid("WAV 容器头被截断。");
        uint magic = reader.ReadUInt32(), size32 = reader.ReadUInt32();
        if (magic is not (Riff or Rf64) || reader.ReadUInt32() != Wave) throw Invalid("不是支持的 RIFF/RF64 WAVE。");
        IsRf64 = magic == Rf64;
        ulong rfRiff = 0, rfData = 0, rfFrames = 0;
        var table = new Dictionary<uint, Queue<ulong>>();
        if (IsRf64)
        {
            if (file.Length < 48 || reader.ReadUInt32() != Ds64) throw Invalid("RF64 的首个子块必须是 ds64。");
            uint length = reader.ReadUInt32();
            if (length < 28 || length > 28 + 4096 * 12 || 20L + length > file.Length) throw Invalid("RF64 ds64 长度无效。");
            rfRiff = reader.ReadUInt64(); rfData = reader.ReadUInt64(); rfFrames = reader.ReadUInt64();
            uint entries = reader.ReadUInt32();
            if (entries > 4096 || length != 28UL + entries * 12UL) throw Invalid("RF64 ds64 表长度不一致。");
            for (uint index = 0; index < entries; index++)
            {
                uint id = reader.ReadUInt32(); ulong bytes = reader.ReadUInt64();
                if (id is Ds64 or Data || bytes > long.MaxValue) throw Invalid("RF64 ds64 扩展表条目无效。");
                if (!table.TryGetValue(id, out var sizes)) table.Add(id, sizes = new());
                sizes.Enqueue(bytes);
            }
            if (rfRiff > long.MaxValue - 8L || rfData > long.MaxValue || rfFrames > long.MaxValue)
                throw Invalid("RF64 大小超出受支持的 Int64 文件范围。");
            long riffBytes = size32 == uint.MaxValue ? (long)rfRiff : size32;
            if ((ulong)riffBytes != rfRiff || riffBytes + 8 != file.Length)
                throw Invalid("RF64 RIFF/ds64/实际文件长度不一致。");
        }
        else if ((long)size32 + 8 != file.Length) throw Invalid("RIFF 容器长度不一致。");

        bool haveFormat = false, haveData = false;
        ulong? factFrames = null;
        int chunks = 0;
        while (file.Position < file.Length)
        {
            if (++chunks > 100000 || file.Length - file.Position < 8) throw Invalid("WAV 子块数量过多或末尾头被截断。");
            uint id = reader.ReadUInt32(), chunk32 = reader.ReadUInt32();
            ulong size = chunk32;
            if (id == Ds64) throw Invalid("拒绝重复或错误位置的 ds64。");
            if (IsRf64 && chunk32 == uint.MaxValue)
            {
                if (id == Data) size = rfData;
                else if (table.TryGetValue(id, out var sizes) && sizes.TryDequeue(out ulong extended)) size = extended;
                else throw Invalid("RF64 大子块缺少 ds64 长度。");
            }
            long payload = file.Position;
            if (size > (ulong)(file.Length - payload)) throw Invalid("WAV 子块超出文件末尾。");
            long end = payload + (long)size;
            if ((size & 1) != 0 && end == file.Length) throw Invalid("奇数长度子块缺少填充字节。");
            if (id == Fmt)
            {
                if (haveFormat || size < 16 || size > 1024) throw Invalid("WAV fmt 子块无效或重复。");
                Format = ParseFormat(reader.ReadBytes((int)size)); haveFormat = true;
                if (expected is not null && Format != expected) throw Invalid("WAV 格式与预期的已记录格式不同。");
            }
            else if (id == Data)
            {
                if (haveData) throw Invalid("不接受重复 data 子块。");
                if (IsRf64 && size != rfData) throw Invalid("RF64 data 与 ds64 声明不同。");
                DataOffset = payload; DataLength = (long)size; haveData = true;
            }
            else if (id == Fact)
            {
                if (factFrames is not null || size < 4) throw Invalid("fact 子块无效或重复。");
                uint fact32 = reader.ReadUInt32(); factFrames = IsRf64 && fact32 == uint.MaxValue ? rfFrames : fact32;
            }
            file.Position = end + (long)(size & 1);
        }
        if (!haveFormat || !haveData || DataLength % Format.BlockAlign != 0) throw Invalid("WAV 缺少格式/数据，或数据不是完整音频帧。");
        if (IsRf64 && (rfFrames != (ulong)FrameCount || table.Values.Any(sizes => sizes.Count != 0)))
            throw Invalid("RF64 sampleCount 或未消费 ds64 表条目不一致。");
        if (factFrames is not null && factFrames != (ulong)FrameCount) throw Invalid("fact 样本数与 PCM 不一致。");
    }

    private static PcmFormat ParseFormat(byte[] bytes)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        int align = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
        uint mask = 0;
        if (bytes.Length != 16 && (bytes.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)) > bytes.Length - 18))
            throw Invalid("fmt 扩展长度不一致。");
        if (tag == 0xfffe)
        {
            if (bytes.Length < 40 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(16)) < 22)
                throw Invalid("WAVEFORMATEXTENSIBLE 被截断。");
            int validBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18));
            if (validBits < 1 || validBits > bits) throw Invalid("WAVEFORMATEXTENSIBLE 有效位数无效。");
            mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20));
            var subtype = new Guid(bytes.AsSpan(24, 16));
            tag = subtype == new Guid("00000001-0000-0010-8000-00aa00389b71") ? (ushort)1
                : subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") ? (ushort)3 : (ushort)0;
        }
        if (tag is not (1 or 3)) throw new NotSupportedException("仅支持 PCM 或 Float32 WAV 原轨。");
        var format = new PcmFormat(rate, channels, bits, tag == 3, align, mask); format.Validate();
        if (byteRate != (long)rate * align) throw Invalid("fmt bytesPerSecond 与音频格式不同。");
        return format;
    }

    public float[] ReadFrames(long frameOffset, int frameCount)
    {
        if (frameOffset < 0 || frameCount < 0 || frameOffset > FrameCount - frameCount) throw Invalid("读取帧范围超出 WAV data。");
        Position = checked(frameOffset * Format.BlockAlign);
        byte[] bytes = new byte[checked(frameCount * Format.BlockAlign)]; this.ReadExactly(bytes);
        var samples = new float[checked(frameCount * Format.Channels)]; int width = Format.BitsPerSample / 8;
        for (int index = 0; index < samples.Length; index++)
        {
            var value = bytes.AsSpan(index * width, width);
            samples[index] = Format.IsFloat ? BinaryPrimitives.ReadSingleLittleEndian(value)
                : width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f
                : width == 3 ? ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608f
                : BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648f;
            if (!float.IsFinite(samples[index])) throw Invalid("WAV 含无效浮点样本。");
        }
        return samples;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return DataLength; } }
    public override long Position
    {
        get { ThrowIfDisposed(); return position; }
        set
        {
            ThrowIfDisposed();
            if (value < 0 || value > DataLength) throw new ArgumentOutOfRangeException(nameof(value));
            file.Position = checked(DataOffset + value); position = value;
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { ArgumentNullException.ThrowIfNull(buffer); return Read(buffer.AsSpan(offset, count)); }
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed(); int count = (int)Math.Min(buffer.Length, DataLength - position);
        if (count == 0) return 0;
        file.ReadExactly(buffer[..count]); position += count; return count;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return Position;
    }
    public override void Flush() { ThrowIfDisposed(); }
    public override void SetLength(long value) => throw new NotSupportedException("WAV reader 为只读。");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("WAV reader 为只读。");
    protected override void Dispose(bool disposing)
    { if (!disposed && disposing) file.Dispose(); disposed = true; base.Dispose(disposing); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private static InvalidDataException Invalid(string message) => new(message);
}
