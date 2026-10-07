using System.Text.Json;
using ProjectEcho.Core.Diarization;
using ProjectEcho.Core.Transcription;
using ProjectEcho.Export;

/// <summary>Opt-in: real saved Qwen raw alignment plus new native Whisper on an existing public audio file.</summary>
internal static class M5SpeakerMappingProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6)
        { Console.Error.WriteLine("--m5-speaker-mapping-probe <saved-qwen-meeting> <whisper.exe> <model.bin> <public.wav> <output>"); return 2; }
        string root = SyntheticAudioProbe.FindProjectRoot();
        string output = SyntheticAudioProbe.SafeProjectPath(root, args[5]); Directory.CreateDirectory(output);
        string report = Path.Combine(output, "report.json"); if (File.Exists(report)) throw new IOException("Evidence already exists.");
        var checks = new List<object>(); var errors = new List<string>(); object? qwen = null, whisper = null;
        try
        {
            string meeting = Path.GetFullPath(args[1]);
            var originals = TranscriptService.ListRevisions(meeting).Where(revision => revision.Alignment?.Policy == QwenTranscriber.Policy).ToArray();
            if (originals.Length == 0) throw new InvalidDataException("No real Qwen aligned revision.");
            var results = new List<object>();
            foreach (var revision in originals)
            {
                var alignment = revision.Alignment!; var audio = new SpeakerAudioBinding(alignment.InputWaveSha256, alignment.SourceMixSha256,
                    alignment.SampleRate, alignment.FrameCount, alignment.TimelinePolicy);
                var mapping = SpeakerAlignmentReader.Read(meeting, revision, audio);
                Need(mapping.Boundaries.Count > 0, "Qwen original acoustic words actually reused");
                Need(mapping.Boundaries.All(boundary => revision.Segments.Any(segment => segment.SegmentId == boundary.SegmentId &&
                    boundary.TextOffset > 0 && boundary.TextOffset < segment.Text.Length && boundary.LeftEndMs >= segment.StartMs && boundary.RightStartMs <= segment.EndMs)), "Qwen bounds remain on selected timeline");
                Need(TranscriptService.Load(meeting, revision.RevisionId) == revision ||
                    SpeakerService.TranscriptFingerprint(TranscriptService.Load(meeting, revision.RevisionId)) == SpeakerService.TranscriptFingerprint(revision), "Qwen authority unchanged");
                results.Add(new { revision.RevisionId, revision.MeetingDurationMs, segments = revision.Segments.Count,
                    boundaries = mapping.Boundaries.Count, mappedSegments = mapping.Boundaries.Select(boundary => boundary.SegmentId).Distinct().Count(),
                    inputWaveSha256 = audio.InputWaveSha256, timelinePolicy = audio.TimelinePolicy, warnings = mapping.Warnings });
            }
            qwen = results;
            var imported = AudioImportService.Import(SyntheticAudioProbe.SafeProjectPath(root, args[4]), Path.Combine(output, "recordings"));
            var prepared = AsrAudioPreparation.Prepare(imported.Mix.WavePath, imported.Mix.WaveSha256, Path.Combine(imported.MeetingDirectory, "transcripts", "inputs"));
            long duration = (long)Math.Round(prepared.Frames * 1000d / 16000);
            var request = new LocalTranscriptionRequest(imported.MeetingDirectory, Path.GetFileName(imported.MeetingDirectory), duration,
                prepared.Path, prepared.Sha256, new("whisper.cpp", args[2], args[3], "en", 4, "public-fixture-base", ModelLicenseAcknowledged: true),
                SourceMixSha256: imported.Mix.WaveSha256, TimelinePolicy: "file-v1");
            var result = await new WhisperCppLocalTranscriber().RunAsync(request);
            Need(result.Status == LocalTranscriptionStatus.Succeeded && result.Revision?.Alignment?.Policy == "whisper-exact-token-boundaries-v1", "Native Whisper persisted exact audio-bound token evidence");
            var transcript = result.Revision!;
            var wave = new SpeakerAudioBinding(prepared.Sha256, imported.Mix.WaveSha256, 16000, prepared.Frames, "file-v1");
            var reader = SpeakerAlignmentReader.Read(imported.MeetingDirectory, transcript, wave);
            Need(reader.Boundaries.Count > 0, "Native Whisper real token boundaries actually reused");
            string changedId = reader.Boundaries[0].SegmentId;
            var revised = TranscriptService.CreateRevision(imported.MeetingDirectory, transcript.RevisionId,
                transcript.Segments.Select(segment => segment.SegmentId == changedId ? segment with { Text = "用户已校订，原词界不适用。" } : segment).ToArray());
            var revisedMap = SpeakerAlignmentReader.Read(imported.MeetingDirectory, revised, wave);
            Need(revisedMap.Boundaries.All(boundary => boundary.SegmentId != changedId), "Human edits never borrowed old words or overwritten text");
            Need(M3TranscriptTests.Hash(prepared.Path) == prepared.Sha256 && M3TranscriptTests.Hash(imported.Mix.WavePath) == imported.Mix.WaveSha256,
                "Audio remains unchanged");
            whisper = new { result.Status, result.ElapsedMilliseconds, transcript.RevisionId, segments = transcript.Segments.Count,
                boundaries = reader.Boundaries.Count, inputWaveSha256 = prepared.Sha256, timelinePolicy = transcript.Alignment!.TimelinePolicy,
                mappedSegments = reader.Boundaries.Select(boundary => boundary.SegmentId).Distinct().Count(), warnings = reader.Warnings,
                humanEditedSegmentsWithOldBoundaries = revisedMap.Boundaries.Count(boundary => boundary.SegmentId == changedId) };
        }
        catch (Exception exception) { errors.Add(exception.ToString()); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { status = errors.Count == 0 ? "PASS" : "FAIL", qwen, whisper, checks, errors,
            scope = "real local ASR/alignment reuse; speaker grouping quality not claimed; no recording microphone or network" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report); return errors.Count == 0 ? 0 : 1;
        void Need(bool condition, string name) { checks.Add(new { name, passed = condition }); if (!condition) throw new InvalidDataException(name); }
    }
}
