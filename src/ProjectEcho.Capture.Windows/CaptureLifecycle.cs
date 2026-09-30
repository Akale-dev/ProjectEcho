namespace ProjectEcho.Capture.Windows;

/// <summary>Capture-thread ordering rules, also exercised without opening audio devices.</summary>
internal static class CaptureLifecycle
{
    internal static void StartAfterWriterReady(Task writerReady, IEnumerable<Action> starts, Func<bool> stopping)
    {
        writerReady.GetAwaiter().GetResult();
        foreach (var start in starts)
        {
            if (stopping()) throw new OperationCanceledException("启动已取消，未开始新的采集流。");
            start();
        }
    }

    internal static void StopAllBeforeDrain(IReadOnlyList<CaptureStopActions> sources)
    {
        // One slow drain/release must not leave another source (especially mic) collecting.
        foreach (var source in sources) Attempt(source.Stop, source, "停止");
        foreach (var source in sources) Attempt(source.Drain, source, "尾包处理");
        foreach (var source in sources) Attempt(source.Release, source, "释放");
    }

    private static void Attempt(Action action, CaptureStopActions source, string operation)
    {
        try { action(); }
        catch (Exception ex) { source.Failed(operation, ex); }
    }
}

internal sealed record CaptureStopActions(Action Stop, Action Drain, Action Release, Action<string, Exception> Failed);
