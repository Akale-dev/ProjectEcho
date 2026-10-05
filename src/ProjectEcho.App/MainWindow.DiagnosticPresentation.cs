using ProjectEcho.Core.Diagnostics;

namespace ProjectEcho.App;

public partial class MainWindow
{
    // Run before translating individual diagnostics. This is only a display/new-log projection;
    // saved capture/manifests and preexisting operational Log entries remain unchanged.
    private static string PresentRecordingDiagnostic(string text) =>
        string.Join("\n", RecordingDiagnosticPresentation.Project(text).Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n').Select(UiText.T));

    private static string PresentRecordingWarnings(IEnumerable<string> warnings) =>
        PresentRecordingDiagnostic(string.Join("\n", warnings));
}
