namespace ProjectEcho.Core.Transcription;

/// <summary>All ranges are half-open 16 kHz PCM frame ranges on the already prepared recording timeline.</summary>
public sealed record QwenWindow(int Index, long InputStartFrame, int InputFrameCount,
    long OwnedStartFrame, int OwnedFrameCount)
{
    public long InputEndFrame => checked(InputStartFrame + InputFrameCount);
    public long OwnedEndFrame => checked(OwnedStartFrame + OwnedFrameCount);
}

/// <summary>
/// A bounded input plan, not sentence timing or a VAD decision. Ownership covers every input frame
/// exactly once; optional context is separately recorded and must not be silently deduplicated as text.
/// Pauses are already represented by the selected input timeline; this class never adds or removes them.
/// </summary>
public sealed class QwenWindowPlan
{
    public const int SampleRate = 16000;
    public const string MappingVersion = "qwen-pcm16-contiguous-ownership-v1";
    public long TotalFrames { get; }
    public string TimelinePolicy { get; }
    public IReadOnlyList<QwenWindow> Windows { get; }

    private QwenWindowPlan(long totalFrames, string timelinePolicy, QwenWindow[] windows)
    {
        TotalFrames = totalFrames;
        TimelinePolicy = timelinePolicy;
        Windows = Array.AsReadOnly(windows);
    }

    public static QwenWindowPlan Create(long totalFrames, int windowFrames, int contextFrames = 0,
        string timelinePolicy = "elapsed-v1")
    {
        if (totalFrames < 1 || totalFrames > 7L * 24 * 60 * 60 * SampleRate ||
            windowFrames < 1 || windowFrames > 600 * SampleRate || contextFrames < 0 ||
            contextFrames > windowFrames || (long)windowFrames + 2L * contextFrames > 600 * SampleRate)
            throw new InvalidDataException("Qwen 分窗帧数无效（整场至多七天，每次含上下文至多十分钟）。");
        if (timelinePolicy is not ("elapsed-v1" or "pause-excluded-v1" or "file-v1"))
            throw new InvalidDataException("Qwen 分窗缺少可识别的音频时间轴策略。");
        long count = (totalFrames - 1) / windowFrames + 1;
        if (count > 50000) throw new InvalidDataException("Qwen 分窗数量超过五万，拒绝无界任务。");
        var windows = new QwenWindow[checked((int)count)];
        for (int index = 0; index < windows.Length; index++)
        {
            long ownedStart = (long)index * windowFrames;
            int ownedCount = checked((int)Math.Min(windowFrames, totalFrames - ownedStart));
            long inputStart = Math.Max(0, ownedStart - contextFrames);
            long inputEnd = Math.Min(totalFrames, ownedStart + ownedCount + contextFrames);
            windows[index] = new(index, inputStart, checked((int)(inputEnd - inputStart)), ownedStart, ownedCount);
        }
        return new(totalFrames, timelinePolicy, windows);
    }

    public void ValidateWindow(QwenWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Index < 0 || window.Index >= Windows.Count || Windows[window.Index] != window)
            throw new InvalidDataException("窗口与本次完整输入计划不一致。");
    }
}

public enum QwenWindowStatus { Pending, Aligned, DigitalSilence, AlignmentPending, Failed, Cancelled, ModelNoText, TranscribedUntimed }

/// <summary>One immutable attempt result per window. Failed/cancelled windows remain explicit coverage gaps.</summary>
public sealed record QwenWindowOutcome(int WindowIndex, string InputWaveSha256, QwenWindowStatus Status,
    string? OriginalTextSha256 = null, int SentenceCount = 0, bool HasUnresolvedContext = false);

public sealed record QwenWindowCoverageResult(bool Complete, long CoveredFrames, long TotalFrames,
    IReadOnlyList<int> IncompleteWindowIndexes)
{
    // Complete/CoveredFrames retain their original fully aligned meaning.
    public bool TextComplete { get; init; }
    public long TextCoveredFrames { get; init; }
    public IReadOnlyList<int> IncompleteTextWindowIndexes { get; init; } = [];
    public IReadOnlyList<int> UntimedWindowIndexes { get; init; } = [];
}

public static class QwenWindowCoverage
{
    public static QwenWindowCoverageResult Evaluate(QwenWindowPlan plan, IReadOnlyList<QwenWindowOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count > plan.Windows.Count) throw new InvalidDataException("窗口结果超过计划，不能重复计入覆盖。");
        var byIndex = new Dictionary<int, QwenWindowOutcome>();
        foreach (var outcome in outcomes)
        {
            if (outcome is null || outcome.WindowIndex < 0 || outcome.WindowIndex >= plan.Windows.Count ||
                !byIndex.TryAdd(outcome.WindowIndex, outcome) || !TranscriptFiles.IsHash(outcome.InputWaveSha256) ||
                !Enum.IsDefined(outcome.Status) || outcome.SentenceCount < 0 || outcome.SentenceCount > 50000)
                throw new InvalidDataException("窗口结果标识、哈希或数量无效。");
            if (outcome.Status is QwenWindowStatus.Aligned or QwenWindowStatus.TranscribedUntimed &&
                (!TranscriptFiles.IsHash(outcome.OriginalTextSha256) || outcome.SentenceCount == 0))
                throw new InvalidDataException("已定位窗口缺少原文哈希或句子，不能作为成功覆盖。");
            if (outcome.Status == QwenWindowStatus.DigitalSilence &&
                (outcome.OriginalTextSha256 is not null || outcome.SentenceCount != 0 || outcome.HasUnresolvedContext))
                throw new InvalidDataException("数字零窗口不能带识别文字或未解决的上下文。");
            if (outcome.Status == QwenWindowStatus.ModelNoText &&
                (!string.Equals(outcome.OriginalTextSha256, QwenWindowAlignment.TextSha256(""), StringComparison.OrdinalIgnoreCase) ||
                    outcome.SentenceCount != 0 || outcome.HasUnresolvedContext))
                throw new InvalidDataException("模型无文字窗口须保留空原文哈希；这不证明没有语音。");
        }
        long covered = 0, textCovered = 0;
        var incomplete = new List<int>(); var incompleteText = new List<int>(); var untimed = new List<int>();
        foreach (var window in plan.Windows)
        {
            if (byIndex.TryGetValue(window.Index, out var outcome) && !outcome.HasUnresolvedContext &&
                outcome.Status is QwenWindowStatus.Aligned or QwenWindowStatus.DigitalSilence or QwenWindowStatus.ModelNoText)
                covered += window.OwnedFrameCount;
            else incomplete.Add(window.Index);
            if (byIndex.TryGetValue(window.Index, out var textOutcome) && !textOutcome.HasUnresolvedContext &&
                textOutcome.Status is QwenWindowStatus.Aligned or QwenWindowStatus.DigitalSilence or QwenWindowStatus.ModelNoText or QwenWindowStatus.TranscribedUntimed)
            {
                textCovered += window.OwnedFrameCount;
                if (textOutcome.Status == QwenWindowStatus.TranscribedUntimed) untimed.Add(window.Index);
            }
            else incompleteText.Add(window.Index);
        }
        return new(incomplete.Count == 0 && covered == plan.TotalFrames, covered, plan.TotalFrames,
            incomplete.AsReadOnly())
        {
            TextComplete = incompleteText.Count == 0 && textCovered == plan.TotalFrames,
            TextCoveredFrames = textCovered, IncompleteTextWindowIndexes = incompleteText.AsReadOnly(),
            UntimedWindowIndexes = untimed.AsReadOnly()
        };
    }
}
