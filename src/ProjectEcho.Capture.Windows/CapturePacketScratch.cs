using System.Runtime.InteropServices;

namespace ProjectEcho.Capture.Windows;

internal enum ScratchCopyResult { Success, ExceedsPacketLimit, ExceedsScratchCapacity, MissingPointer }

/// <summary>
/// A source-private, bounded staging buffer allocated before Start. CopyPrefix
/// allocates nothing on its normal path; callers materialize only after ReleaseBuffer.
/// This is deliberately not a shared pool and never copies a forbidden packet tail.
/// </summary>
internal sealed class CapturePacketScratch : IDisposable
{
    internal const int MaximumBytes = 1024 * 1024;
    private readonly byte[] bytes;
    private int used;
    private bool disposed;

    internal CapturePacketScratch(int? bufferFrames, int blockAlign)
    {
        if (blockAlign <= 0) throw new ArgumentOutOfRangeException(nameof(blockAlign));
        long requested = bufferFrames is > 0 ? (long)bufferFrames.Value * blockAlign : MaximumBytes;
        bytes = new byte[(int)Math.Min(MaximumBytes, requested)];
    }

    internal int Capacity => bytes.Length;
    internal int UsedBytes => used;
    // Synthetic tests inspect only their own known bytes, never a live capture buffer.
    internal ReadOnlySpan<byte> DiagnosticBytes => bytes;

    internal ScratchCopyResult CopyPrefix(IntPtr source, long length, bool silent)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CapturePacketScratch));
        Clear();
        if (length < 0 || length > MaximumBytes) return ScratchCopyResult.ExceedsPacketLimit;
        if (length > bytes.Length) return ScratchCopyResult.ExceedsScratchCapacity;
        if (length == 0) return ScratchCopyResult.Success;
        if (!silent && source == IntPtr.Zero) return ScratchCopyResult.MissingPointer;
        // Set the full attempted range before copying, so any partial-copy failure
        // is cleared by the source's outer finally as well.
        used = (int)length;
        try
        {
            if (silent) bytes.AsSpan(0, used).Clear();
            else Marshal.Copy(source, bytes, 0, used);
        }
        catch { Clear(); throw; }
        return ScratchCopyResult.Success;
    }

    internal byte[] MaterializeAndClear() => MaterializeAndClear(null);

    // The optional allocator is a deterministic failure seam for synthetic tests.
    // This method is called only after the native buffer has been released.
    internal byte[] MaterializeAndClear(Func<int, byte[]>? allocator)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CapturePacketScratch));
        try
        {
            var result = allocator is null ? used == 0 ? Array.Empty<byte>() : new byte[used] : allocator(used);
            if (result.Length != used) throw new InvalidDataException("采集副本长度与获准前缀不一致。");
            bytes.AsSpan(0, used).CopyTo(result);
            return result;
        }
        finally { Clear(); }
    }

    internal void Clear()
    {
        if (used > 0) bytes.AsSpan(0, used).Clear();
        used = 0;
    }

    public void Dispose()
    {
        bytes.AsSpan().Clear(); used = 0; disposed = true;
    }
}
