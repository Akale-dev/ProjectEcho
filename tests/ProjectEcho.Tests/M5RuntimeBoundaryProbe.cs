using System.Text.Json;
using ProjectEcho.Core.Diarization;

internal static class M5RuntimeBoundaryProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6) return 2;
        string root = SyntheticAudioProbe.FindProjectRoot(), fixtureRoot = SyntheticAudioProbe.SafeProjectPath(root, args[1]), output = SyntheticAudioProbe.SafeProjectPath(root, args[5]);
        if (Directory.Exists(output)) throw new IOException("Use a fresh boundary evidence directory.");
        Directory.CreateDirectory(output); var runs = new List<object>(); var errors = new List<string>();
        try
        {
            foreach (var item in new[] { (Name: "9.999", Frames: 159984L), (Name: "10", Frames: 160000L), (Name: "10.001", Frames: 160016L) })
            {
                string meeting = Path.Combine(output, "边界-" + item.Name); Directory.CreateDirectory(meeting);
                string id = Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(meeting, "meeting.json"), JsonSerializer.Serialize(new { meetingId = id, isSynthetic = true }));
                string source = Path.Combine(fixtureRoot, "boundary-" + item.Name + ".wav"), wave = Path.Combine(meeting, "输入.wav"); File.Copy(source, wave);
                string hash = M3TranscriptTests.Hash(wave);
                var configuration = new SherpaDiarizationConfiguration(args[2], args[3], args[4], Threads: 2, ModelLicenseAcknowledged: true);
                var result = await SherpaDiarizationRuntime.RunAsync(new(meeting, id, wave, new(hash, null, 16000, item.Frames, "file-v1"), configuration, true));
                using var attempt = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.AttemptDirectory, "attempt.json")));
                long padded = attempt.RootElement.GetProperty("paddedFrames").GetInt64();
                bool pass = result.Status == "Succeeded" && result.Analysis is not null && result.Analysis.Audio.FrameCount == item.Frames &&
                    padded == (item.Frames <= 160000 ? 176000 - item.Frames : 0) && M3TranscriptTests.Hash(wave) == hash && M3TranscriptTests.Hash(source) == hash &&
                    result.Analysis.Intervals.All(interval => interval.StartMs >= 0 && interval.EndMs <= item.Frames * 1000 / 16000);
                runs.Add(new { item.Name, item.Frames, padded, result.Status, result.Message, result.AttemptDirectory, result.Analysis, pass });
                if (!pass) errors.Add("Boundary " + item.Name + " failed; raw evidence retained.");
            }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", sourceMode = "public speech excerpt with constructed 10-second file boundary; no capture", runs, errors }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(errors.Count == 0 ? "M5 runtime boundaries PASS" : string.Join('\n', errors)); return errors.Count == 0 ? 0 : 1;
    }
}
