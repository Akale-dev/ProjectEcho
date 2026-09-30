namespace ProjectEcho.Core;

public enum SessionState
{
    Idle,
    Recording,
    Paused,
    Stopping,
    Saved,
    Faulted,
    SavedIncomplete
}

public static class SessionRules
{
    // A paused capture still owns the session: playback could otherwise be recaptured.
    public static bool CanPlayback(SessionState state) => state is SessionState.Idle or SessionState.Saved or SessionState.SavedIncomplete;

    public static bool CanStart(SessionState state) => state is SessionState.Idle or SessionState.Saved or SessionState.Faulted or SessionState.SavedIncomplete;
}

public static class SessionDefaults
{
    public const string DefaultProject = "未分类";
    public static string Title(DateTimeOffset localTime) => localTime.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
}
