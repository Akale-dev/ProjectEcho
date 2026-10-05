using System.Text;
using System.Text.RegularExpressions;

namespace ProjectEcho.Core.Diagnostics;

/// <summary>
/// Display-only projection of exact historical diagnostic templates. Never changes capture results,
/// original warning arrays, timeline metadata, files, or the meaning of Complete/StorageComplete.
/// Unknown and augmented messages pass through verbatim, including their original line endings.
/// </summary>
public static class RecordingDiagnosticPresentation
{
    private const string ProcessClockNote =
        "remote 设备帧计数不可依赖；保留原值，以实际API QPC标记时间。长时/双轨同步未验收。";
    private const string LegacyCompletenessNote =
        "原件及索引已完成验证；旧式完整捕获/设备时钟条件未通过，Complete仍为false。";
    private const string MixAcceptanceNote =
        "混音依据原始 API QPC 和设备帧重建时间；连续设备帧采用有界平稳映射，保留断续与真实缺口。真实两小时双轨同步尚待验收。";

    public static string Project(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Delimiters are retained so unrecognized diagnostics are not reformatted or shortened.
        string[] parts = Regex.Split(message, "(\\r\\n|\\r|\\n)");
        bool hasKnownProcessClock = Enumerable.Range(0, (parts.Length + 1) / 2)
            .Any(index => parts[index * 2] == ProcessClockNote ||
                parts[index * 2] == "远端音频使用系统 API 时间戳计时；原始时钟已保留。");
        var result = new StringBuilder(message.Length);
        for (int index = 0; index < parts.Length; index += 2)
        {
            string line = parts[index];
            if (line == MixAcceptanceNote ||
                line == "The mix reconstructs timing from original API QPC timestamps and device frames. Continuous device frames use a bounded, steady mapping while preserving discontinuities and actual gaps. Two-hour synchronization between real tracks remains unverified." ||
                line == "混音依據原始 API QPC 和裝置影格重建時間；連續裝置影格採用有界平穩映射，保留斷續與真實缺口。真實兩小時雙軌同步尚待驗收。") continue;
            if (line == LegacyCompletenessNote)
            {
                // Without the independently reported, known process-loopback limitation this
                // may be the only visible indication of an unverified clock: retain that fact.
                if (hasKnownProcessClock) continue;
                line = "原件和索引已校验；音频时钟完整性仍需检查。";
            }
            else if (line == ProcessClockNote)
                line = "远端音频使用系统 API 时间戳计时；原始时钟已保留。";
            result.Append(line);
            if (index + 1 < parts.Length) result.Append(parts[index + 1]);
        }
        return result.ToString();
    }
}
