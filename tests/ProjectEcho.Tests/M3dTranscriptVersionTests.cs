using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

internal static class M3dTranscriptVersionTests
{
    internal static void Register(Action<string, Action> run, string output)
    {
        string root = Path.Combine(output, "m3d-transcript-versions"); Directory.CreateDirectory(root);
        run("T38-WINDOWS SYNTHETIC CLI M3D model reruns keep independent originals reviews and old-version exports after reopen",
            () => IndependentRuns(root));
    }

    private static void IndependentRuns(string root)
    {
        string meeting = M3TranscriptTests.Meeting(root, "independent-model-runs");
        string wave = Path.Combine(meeting, "synthetic.wav");
        using (var writer = new BinaryWriter(File.Create(wave)))
        {
            const int rate = 16000;
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(rate * 2 + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(rate * 2);
            for (int i = 0; i < rate; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 1000));
        }
        string waveHash = M3TranscriptTests.Hash(wave);
        string modelA = M3TranscriptTests.Input(meeting, "synthetic-model-a.bin", "synthetic-success");
        string modelB = M3TranscriptTests.Input(meeting, "synthetic-model-b.bin", "synthetic-success");
        var first = Run(modelA, "synthetic-model-A");
        var firstRevision = first.Revision!;
        var corrected = TranscriptService.CreateRevision(meeting, firstRevision.RevisionId,
            firstRevision.Segments.Select(s => s with { Text = "synthetic A reviewed: ticker TEST, revenue 123" }).ToArray(), "synthetic review before a new model");
        var originalSnapshot = Snapshot();
        var second = Run(modelB, "synthetic-model-B");
        var repeated = Run(modelA, "synthetic-model-A");
        M3TranscriptTests.Need(originalSnapshot.All(file => M3TranscriptTests.Hash(file.Key) == file.Value), "后续模型识别覆盖旧模型输出、原稿或人工修订。");
        M3TranscriptTests.Need(new[] { first, second, repeated }.Select(run => run.AttemptDirectory).Distinct().Count() == 3,
            "多次识别复用同一个尝试目录。");
        var completedSnapshot = Snapshot();

        // There is no in-memory transcript store: reconstruct every object from
        // finalized disk folders, as a new process does after the application exits.
        var reopened = TranscriptService.ListRevisions(meeting);
        M3TranscriptTests.Need(reopened.Count == 4 && reopened.Select(r => r.RevisionId).Distinct().Count() == 4,
            "从磁盘重开丢失独立原稿/修订，或同模型重跑覆盖旧版。");
        M3TranscriptTests.Need(reopened.Count(r => r.ModelName == "synthetic-model-A" && r.ParentRevisionId is null) == 2 &&
            reopened.Single(r => r.ModelName == "synthetic-model-B").RevisionId == second.Revision!.RevisionId,
            "模型名称未随原稿保存。");
        var oldOriginal = TranscriptService.Load(meeting, firstRevision.RevisionId);
        var oldReview = TranscriptService.Load(meeting, corrected.RevisionId);
        M3TranscriptTests.Need(oldReview.ParentRevisionId == oldOriginal.RevisionId && oldReview.ModelName == oldOriginal.ModelName &&
            oldReview.OriginalFile == oldOriginal.OriginalFile && oldReview.OriginalSha256 == oldOriginal.OriginalSha256,
            "重开后旧人工修订丢失原稿/模型来源。");
        M3TranscriptTests.Need(oldOriginal.Segments.Single().Text == "合成夹具结果，非真实 ASR" &&
            oldReview.Segments.Single().Text == "synthetic A reviewed: ticker TEST, revenue 123", "原稿与人工修订混淆。");

        foreach (var selected in new[] { oldOriginal, oldReview, second.Revision!, repeated.Revision! })
        {
            string prefix = Path.Combine(root, selected.RevisionId);
            TranscriptSubtitleExporter.ExportText(meeting, selected.RevisionId, prefix + ".txt");
            M3TranscriptTests.Need(File.ReadAllText(prefix + ".txt").TrimEnd('\r', '\n') == selected.Segments.Single().Text,
                "选旧版本导出全文错误地使用最新结果。");
            foreach (string extension in new[] { ".srt", ".vtt" })
            {
                string destination = TranscriptSubtitleExporter.Export(meeting, selected.RevisionId, prefix + extension);
                string roundTripMeeting = M3TranscriptTests.Meeting(root, "roundtrip-" + selected.RevisionId + extension);
                var roundTrip = TranscriptService.Import(new(roundTripMeeting, Path.GetFileName(roundTripMeeting), 1000,
                    destination, "synthetic selected-version export verification", IsSynthetic: true));
                M3TranscriptTests.Need(selected.Segments.Select(s => (s.StartMs, s.EndMs, s.Text)).SequenceEqual(
                    roundTrip.Segments.Select(s => (s.StartMs, s.EndMs, s.Text))), "选版字幕导出丢失完整时间或文字。");
            }
        }
        M3TranscriptTests.Need(TranscriptService.ListRevisions(meeting).Count == 4 && M3TranscriptTests.Hash(wave) == waveHash &&
            completedSnapshot.All(file => M3TranscriptTests.Hash(file.Key) == file.Value), "重开/选择/导出更改原录音或任一旧版文件。");
        File.WriteAllText(Path.Combine(root, "version-lifecycle-evidence.json"), JsonSerializer.Serialize(new
        {
            Status = "PASS", SourceMode = "SYNTHETIC CLI AND GENERATED WAV / NO CAPTURE",
            Attempts = new[] { first, second, repeated }.Select(run => run.AttemptDirectory).ToArray(),
            Versions = reopened.Select(r => new { r.RevisionId, r.ParentRevisionId, r.ModelName, r.ImportedAt }).ToArray(),
            OriginalAndReviewPreserved = true, SameModelRerunPreserved = true, ReopenedFromDisk = true,
            OldVersionTxtSrtVttExports = true, SourceAndPriorFilesUnchanged = true,
            ActualModelInference = "NOT_RUN; the executable is a synthetic CLI fixture"
        }, new JsonSerializerOptions { WriteIndented = true }));

        WhisperRunResult Run(string model, string name)
        {
            var configuration = new WhisperConfiguration(Path.Combine(AppContext.BaseDirectory, "ProjectEcho.Tests.exe"), model,
                ModelName: name, ModelLicenseAcknowledged: true);
            var result = WhisperTranscriber.RunAsync(new(meeting, "independent-model-runs", 1000, wave, waveHash, configuration, true)).GetAwaiter().GetResult();
            M3TranscriptTests.Need(result.Status == "Succeeded" && result.Revision is not null, "合成模型执行失败：" + result.Message);
            using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
            M3TranscriptTests.Need(attempt.RootElement.GetProperty("revisionId").GetString() == result.Revision!.RevisionId,
                "尝试记录未关联对应独立原稿。");
            return result;
        }

        Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(Path.Combine(meeting, "transcripts"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, M3TranscriptTests.Hash, StringComparer.OrdinalIgnoreCase);
    }
}
