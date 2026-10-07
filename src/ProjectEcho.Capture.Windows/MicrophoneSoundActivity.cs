using System.Buffers.Binary;
using ProjectEcho.Core;

namespace ProjectEcho.Capture.Windows;

/// <summary>Activity evidence only. Never changes samples, recording health, or source consent.</summary>
internal sealed class MicrophoneSoundActivity(PcmFormat format)
{
    internal const double RmsThreshold = 0.0031622776601683794; // -50 dBFS, after per-channel DC removal.
    private readonly double[] sums = new double[format.Channels], squares = new double[format.Channels];
    private readonly int windowFrames = Math.Max(1, format.SampleRate / 100); // 10 ms, independent of packet size.
    private readonly int minimumFrames = Math.Max(2, format.SampleRate / 1000);
    private int count;
    private ulong? previousEnd;

    // Returns the last active frame end in this new, already elapsed slice, or zero.
    internal int Observe(ReadOnlySpan<byte> pcm, ulong startQpc100ns)
    {
        int frames = pcm.Length / format.BlockAlign, lastActive = 0, width = format.BitsPerSample / 8;
        // Never join energy windows across an actual packet gap or timestamp reversal.
        if (previousEnd is { } previous && (startQpc100ns >= previous ? startQpc100ns - previous : previous - startQpc100ns) > 2) Reset();
        for (int frame = 0; frame < frames; frame++)
        {
            for (int channel = 0; channel < format.Channels; channel++)
            {
                var sample = pcm.Slice(frame * format.BlockAlign + channel * width, width);
                double value = format.IsFloat ? BitConverter.ToSingle(sample) : width switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                    3 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608d,
                    4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                    _ => 0
                };
                if (!double.IsFinite(value)) value = 0;
                value = Math.Clamp(value, -1, 1);
                sums[channel] += value; squares[channel] += value * value;
            }
            if (++count == windowFrames)
            {
                if (Active()) lastActive = frame + 1;
                Reset();
            }
        }
        // A just-arrived voice before the stop boundary counts even before the 10 ms window fills.
        if (frames > 0 && count >= minimumFrames && Active()) lastActive = frames;
        previousEnd = checked(startQpc100ns + (ulong)((UInt128)(uint)frames * 10_000_000 / (uint)format.SampleRate));
        return lastActive;
    }

    private bool Active()
    {
        for (int channel = 0; channel < format.Channels; channel++)
        {
            double mean = sums[channel] / count;
            if (squares[channel] / count - mean * mean >= RmsThreshold * RmsThreshold) return true;
        }
        return false;
    }
    private void Reset() { count = 0; Array.Clear(sums); Array.Clear(squares); }
}
