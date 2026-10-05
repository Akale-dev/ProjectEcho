using System.Diagnostics;
using System.Runtime.InteropServices;
using ProjectEcho.Capture.Windows;
using ProjectEcho.Core;

/// <summary>No device access: synthetic unmanaged bytes stand in for an already authorized prefix.</summary>
internal static class CapturePacketScratchTests
{
    internal static void Register(Action<string, Action> run)
    {
        run("T21/T57-SYNTH M3I capture scratch reserves bounded capacity independently of reported metadata", Capacity);
        run("T21/T51/T52-SYNTH M3I legal packet larger than reported buffer preserves all PCM and raw cutoff prefix", LargerPacket);
        run("T51/T52-SYNTH capture scratch copies exact pre-cutoff bytes and never stages a forbidden tail", PrefixAndSilence);
        run("T52-SYNTH capture scratch is cleared after materialization failures disposal and refused copies", FailureCleanup);
        run("T54-SYNTH held-buffer staging and fixed timing counters allocate no managed bytes after warmup", AllocationAndMetrics);
    }

    private static void Capacity()
    {
        using var exact = new CapturePacketScratch(480, 8);
        using var unavailable = new CapturePacketScratch(null, 8);
        using var invalidZero = new CapturePacketScratch(0, 2);
        using var invalidNegative = new CapturePacketScratch(-1, 2);
        using var capped = new CapturePacketScratch(int.MaxValue, 8);
        Need(exact.Capacity == CapturePacketScratch.MaximumBytes, "reported BufferSize仍被误作包大小上限。");
        Need(new[] { unavailable.Capacity, invalidZero.Capacity, invalidNegative.Capacity, capped.Capacity }.All(value => value == 1024 * 1024),
            "元数据不可用的回退或1MiB上限发生变化。");
        Reject<ArgumentOutOfRangeException>(() => new CapturePacketScratch(480, 0));
    }

    private static void LargerPacket()
    {
        byte[] source = Enumerable.Range(0, CapturePacketScratch.MaximumBytes).Select(i => (byte)(i % 251 + 1)).ToArray();
        IntPtr native = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, native, source.Length);
            using var scratch = new CapturePacketScratch(1, 8);
            foreach (int count in new[] { 16, 48000 * 8, CapturePacketScratch.MaximumBytes })
            {
                Need(scratch.CopyPrefix(native, count, false) == ScratchCopyResult.Success, "合法大包因reported容量误拒绝。");
                Need(scratch.MaterializeAndClear().AsSpan().SequenceEqual(source.AsSpan(0, count)), "大包音频被截断或损坏。");
                Cleared(scratch);
            }
            const ulong qpc = 9000000; const int frames = 48000;
            int keep = AudioClock.CompleteFramesBefore(qpc, qpc + 10000, 48000, frames);
            Need(keep == 48 && scratch.CopyPrefix(native, keep * 8L, false) == ScratchCopyResult.Success, "截止前完整帧错误。");
            Need(scratch.UsedBytes == keep * 8 && scratch.DiagnosticBytes[(keep * 8)..].IndexOfAnyExcept((byte)0) < 0, "截止后的PCM被暂存。");
            Need(scratch.MaterializeAndClear().AsSpan().SequenceEqual(source.AsSpan(0, keep * 8)), "截止前大包尾部不完整。");
            Need(scratch.CopyPrefix(IntPtr.Zero, CapturePacketScratch.MaximumBytes, true) == ScratchCopyResult.Success &&
                scratch.MaterializeAndClear().All(value => value == 0), "大静音包泄露旧PCM。");
            Need(scratch.CopyPrefix(native, (long)CapturePacketScratch.MaximumBytes + 1, false) == ScratchCopyResult.ExceedsPacketLimit,
                "大于1MiB未在访问指针前拒绝。");
            Cleared(scratch);
        }
        finally { Marshal.FreeHGlobal(native); }
    }
    private static void PrefixAndSilence()
    {
        byte[] source = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        IntPtr native = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, native, source.Length);
            using var scratch = new CapturePacketScratch(16, 2);
            const ulong epoch = 1000000;
            foreach (ulong cutoff in new ulong[] { epoch, epoch + 625, epoch + 6 * 625, epoch + 6 * 625 + 624 })
            {
                int frames = AudioClock.CompleteFramesBefore(epoch, cutoff, 16000, 16);
                int bytes = frames * 2;
                Need(scratch.CopyPrefix(native, bytes, false) == ScratchCopyResult.Success && scratch.UsedBytes == bytes,
                    "合法前缀未正确暂存。");
                Need(scratch.DiagnosticBytes[..bytes].SequenceEqual(source.AsSpan(0, bytes)) &&
                    scratch.DiagnosticBytes[bytes..].IndexOfAnyExcept((byte)0) < 0,
                    "scratch复制了截止之后的PCM尾部。");
                byte[] copy = scratch.MaterializeAndClear();
                Need(copy.Length == bytes && copy.AsSpan().SequenceEqual(source.AsSpan(0, bytes)), "输出不是精确前缀。");
                Cleared(scratch);
            }
            // Reusing a private scratch after a non-silent packet must not leak
            // either its prior bytes or the native SILENT pointer's contents.
            scratch.CopyPrefix(native, 32, false);
            Need(scratch.CopyPrefix(IntPtr.Zero, 12, true) == ScratchCopyResult.Success, "SILENT空指针被拒绝。");
            byte[] silent = scratch.MaterializeAndClear();
            Need(silent.Length == 12 && silent.All(value => value == 0), "SILENT重用了旧PCM。");
            Cleared(scratch);
            Need(scratch.CopyPrefix(IntPtr.Zero, 0, false) == ScratchCopyResult.Success && scratch.MaterializeAndClear().Length == 0,
                "全在cutoff后的包产生了PCM。");
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    private static void FailureCleanup()
    {
        byte[] source = Enumerable.Repeat((byte)0x7f, 32).ToArray();
        IntPtr native = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, native, source.Length);
            using var scratch = new CapturePacketScratch(16, 2);
            var injected = new IOException("synthetic post-release allocation failure");
            scratch.CopyPrefix(native, 20, false);
            try { scratch.MaterializeAndClear(_ => throw injected); throw new InvalidOperationException("未触发模拟失败。"); }
            catch (IOException error) { Need(ReferenceEquals(error, injected), "清理替换了原异常。"); }
            Cleared(scratch);
            scratch.CopyPrefix(native, 20, false);
            Reject<InvalidDataException>(() => scratch.MaterializeAndClear(_ => new byte[1]));
            Cleared(scratch);
            foreach (var (length, expected) in new[] {
                ((long)CapturePacketScratch.MaximumBytes + 1, ScratchCopyResult.ExceedsPacketLimit), (-1L, ScratchCopyResult.ExceedsPacketLimit) })
            {
                scratch.CopyPrefix(native, 16, false);
                Need(scratch.CopyPrefix(native, length, false) == expected, "超限复制未安全拒绝。");
                Cleared(scratch);
            }
            scratch.CopyPrefix(native, 16, false);
            Need(scratch.CopyPrefix(IntPtr.Zero, 16, false) == ScratchCopyResult.MissingPointer, "非静音空指针未拒绝。");
            Cleared(scratch);
            scratch.CopyPrefix(native, 32, false); scratch.Dispose(); Cleared(scratch);
            Reject<ObjectDisposedException>(() => scratch.MaterializeAndClear());
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    private static void AllocationAndMetrics()
    {
        byte[] source = Enumerable.Repeat((byte)0x55, 320).ToArray();
        IntPtr native = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, native, source.Length);
            using var scratch = new CapturePacketScratch(160, 2);
            var metrics = new CaptureReadMetrics();
            for (int i = 0; i < 200; i++)
            {
                scratch.CopyPrefix(native, 320, false); scratch.Clear();
                metrics.Poll(i + 1); metrics.Released(i + 1, i + 2);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++)
            {
                scratch.CopyPrefix(native, 320, false); scratch.Clear();
                metrics.Poll(i + 1000); metrics.Released(i + 1000, i + 1001);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Need(allocated == 0, $"持包区helper/计数器分配了{allocated}托管字节。");
            var measured = new CaptureReadMetrics(); long frequency = Stopwatch.Frequency;
            measured.Poll(0); measured.Poll(frequency / 100); measured.Poll(frequency / 20);
            measured.Released(0, frequency / 1000); measured.Released(0, frequency / 100);
            Need(measured.PollCount == 3 && measured.PacketCount == 2 && measured.PollIntervalsOver20Ms == 1 && measured.BufferHoldsOver2Ms == 1 &&
                Math.Abs(measured.MaximumPollInterval100ns - 400000) <= 1 && Math.Abs(measured.MaximumBufferHold100ns - 100000) <= 1,
                "固定计数的100ns换算、最大值或阈值计数错误。");
            Cleared(scratch);
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    private static void Cleared(CapturePacketScratch scratch) => Need(scratch.UsedBytes == 0 && scratch.DiagnosticBytes.IndexOfAnyExcept((byte)0) < 0,
        "scratch仍残留已用PCM或旧尾部。");
    private static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
