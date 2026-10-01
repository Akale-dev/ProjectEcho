using System.Globalization;
using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>Bounded streaming parser for b5130's actual stderr progress callback, never transcript text.</summary>
public sealed class WhisperProgressParser(Action<int> report, Action<LocalTranscriptionStage>? reportStage = null)
{
    public const string Policy = "b5130-stderr-window-percent-at-token-and-loading-stage-v2";
    private const string Prefix = "whisper_print_progress_callback: progress =";
    private const string ProcessingPrefix = "main: processing '";
    private readonly StringBuilder line = new();
    private bool overlong;
    private int previous = -1;
    private bool recognizing;

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is '\n' or '\r') { ReadLine(); continue; }
            if (overlong) continue;
            if (line.Length >= 256) { line.Clear(); overlong = true; }
            else
            {
                line.Append(character);
                // cli.cpp writes this exact prefix only after loading the model and input.
                // Recognize it before a potentially long local pathname exceeds the line bound.
                if (!recognizing && line.Length == ProcessingPrefix.Length &&
                    line.ToString().Equals(ProcessingPrefix, StringComparison.Ordinal)) Recognizing();
                // A complete callback percentage must not wait for newline, another encoder
                // window, or process exit. CR/LF and EOF still work, and duplicates are suppressed.
                if (character == '%') ReadProgress();
            }
        }
    }

    public void Complete() => ReadLine();

    private void ReadLine()
    {
        if (!overlong)
        {
            if (line.ToString().Trim().StartsWith(ProcessingPrefix, StringComparison.Ordinal)) Recognizing();
            ReadProgress();
        }
        line.Clear(); overlong = false;
    }

    private void ReadProgress()
    {
        string value = line.ToString().Trim();
        if (value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith('%') &&
            int.TryParse(value.AsSpan(Prefix.Length, value.Length - Prefix.Length - 1).Trim(),
                NumberStyles.None, CultureInfo.InvariantCulture, out int percent) &&
            percent is >= 0 and <= 100 && percent > previous)
        {
            Recognizing();
            previous = percent; report(percent);
        }
    }

    private void Recognizing()
    {
        if (recognizing) return;
        recognizing = true; reportStage?.Invoke(LocalTranscriptionStage.Recognizing);
    }
}
