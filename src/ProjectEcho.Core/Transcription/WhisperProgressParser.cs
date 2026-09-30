using System.Globalization;
using System.Text;

namespace ProjectEcho.Core.Transcription;

/// <summary>Bounded streaming parser for b5130's actual stderr progress callback, never transcript text.</summary>
public sealed class WhisperProgressParser(Action<int> report)
{
    private const string Prefix = "whisper_print_progress_callback: progress =";
    private readonly StringBuilder line = new();
    private bool overlong;
    private int previous = -1;

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is '\n' or '\r') { ReadLine(); continue; }
            if (overlong) continue;
            if (line.Length >= 256) { line.Clear(); overlong = true; }
            else line.Append(character);
        }
    }

    public void Complete() => ReadLine();

    private void ReadLine()
    {
        if (!overlong)
        {
            string value = line.ToString().Trim();
            if (value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith('%') &&
                int.TryParse(value.AsSpan(Prefix.Length, value.Length - Prefix.Length - 1).Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int percent) &&
                percent is >= 0 and <= 100 && percent > previous)
            { previous = percent; report(percent); }
        }
        line.Clear(); overlong = false;
    }
}
