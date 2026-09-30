using System.Runtime.CompilerServices;
using NAudio.Wave;

[assembly: InternalsVisibleTo("ProjectEcho.Tests")]

namespace ProjectEcho.Capture.Windows;

/// <summary>The WAV file and PCM offset used by production capture checkpoints.</summary>
internal sealed class CaptureWaveFile : IDisposable
{
    public FileStream Stream { get; }
    public WaveFileWriter Writer { get; }
    public long DataOffset { get; }

    public CaptureWaveFile(string path, WaveFormat format)
    {
        Stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            Writer = new WaveFileWriter(Stream, format);
            // NAudio 3.1 buffers part of the WAV header. Until Flush, Stream.Position
            // can point inside that header (38 instead of 58 for float32), not at PCM.
            Writer.Flush();
            DataOffset = Stream.Position;
        }
        catch
        {
            Stream.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { Writer.Dispose(); } finally { Stream.Dispose(); }
    }
}
