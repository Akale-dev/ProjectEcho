using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

/// <summary>
/// Explicit local recovery of an already completed Whisper output. Never invokes an engine, opens a
/// device, plays audio, copies missing original tracks, or writes the supplied source directory.
/// Private transcript artifacts remain in the caller's new project-local output directory.
/// </summary>
internal static class M3iAsrSavedOutputProbe
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record WaveInfo(int Rate, long Frames, long FileBytes, long DataBytes);
    private sealed record SourceFile(string RelativePath, long Bytes, string Sha256);

    internal static Task<int> RunAsync(string[] args) => Task.FromResult(Run(args));

    private static int Run(string[] args)
    {
        if (args.Length != 3 || args[0] != "--m3i-asr-saved-output-probe")
        {
            Console.Error.WriteLine("Usage: --m3i-asr-saved-output-probe <project-local-source-meeting> <new-project-local-output>");
            return 2;
        }
        string phase = "scope";
        string? output = null;
        SourceSnapshot? snapshot = null;
        bool unchanged = false, outputCreated = false;
        try
        {
            string root = FindAuthorizedRoot();
            string source = SafePath(root, args[1]);
            output = SafePath(root, args[2]);
            Require(Directory.Exists(source) && !Directory.Exists(output) && !File.Exists(output), "fresh-output-required");
            Require(!Inside(source, output) && !Inside(output, source) && !string.Equals(source, output, StringComparison.OrdinalIgnoreCase),
                "source-and-output-overlap");
            string id = Path.GetFileName(source);
            Require(id.Length is > 0 and <= 200, "meeting-id-invalid");
            phase = "source-snapshot";
            var supplied = EnumerateSafeFiles(source).ToList();
            // The separately saved private audit also protects the supplied diagnostic Log. Its
            // paths are data, accepted only beneath this project's DEBUG directory.
            string auditManifest = Path.Combine(root, ".local", "M3I-ASRFix", "source-manifest.private.json");
            var auditFiles = ReadPrivateAudit(root, auditManifest);
            supplied.AddRange(auditFiles.Keys);
            snapshot = new SourceSnapshot(root, supplied.Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var pair in auditFiles) Require(snapshot.Hash(pair.Key) == pair.Value, "provided-source-changed-since-audit");

            phase = "attempt-and-identity";
            string attemptPath = SingleFile(source, "transcripts/asr", "attempt.json");
            using var attemptDoc = JsonDocument.Parse(snapshot.Read(attemptPath, 1024 * 1024));
            var attempt = attemptDoc.RootElement;
            Require(attempt.GetProperty("schemaVersion").GetString() == "m3-whisper-attempt-2" &&
                attempt.GetProperty("status").GetString() == "Failed" &&
                attempt.GetProperty("exitCode").GetInt32() == 0 &&
                attempt.GetProperty("provider").GetString() == "whisper.cpp" &&
                attempt.GetProperty("timestampBoundaryPolicy").GetString() == "whisper-final-cue-overrun-at-most-250ms-v1" &&
                attempt.GetProperty("zeroDurationPolicy").GetString() == "whisper-in-range-zero-duration-own-tokens-or-untimed-v1" &&
                !attempt.GetProperty("isSynthetic").GetBoolean() && attempt.GetProperty("meetingId").GetString() == id,
                "attempt-not-an-eligible-completed-engine-run");
            long durationMs = attempt.GetProperty("meetingDurationMs").GetInt64();
            string manifestPath = Path.Combine(source, "meeting.json"), indexPath = Path.Combine(source, "journal", "capture-index.jsonl");
            using var manifestDoc = JsonDocument.Parse(snapshot.Read(manifestPath, 8 * 1024 * 1024));
            var manifest = manifestDoc.RootElement;
            Require(manifest.GetProperty("schemaVersion").GetString() == "m1-recording-1" &&
                !manifest.GetProperty("isSynthetic").GetBoolean() && manifest.GetProperty("storageComplete").GetBoolean(),
                "unsupported-source-manifest");
            using var sessionDoc = JsonDocument.Parse(snapshot.FirstLine(indexPath, 1024 * 1024));
            var session = sessionDoc.RootElement;
            Require(session.GetProperty("schemaVersion").GetInt32() == 1 && session.GetProperty("sequence").GetInt64() == 0 &&
                !session.GetProperty("isSynthetic").GetBoolean() &&
                session.GetProperty("data").GetProperty("type").GetString() == "session" &&
                session.GetProperty("data").GetProperty("meetingId").GetString() == id &&
                HashText(session.GetProperty("data").GetRawText()) == session.GetProperty("sha256").GetString(),
                "source-session-identity-mismatch");

            phase = "audio-and-render-binding";
            string input = SingleFile(source, "transcripts/asr-inputs", "audio-16k.wav");
            string renderPath = SingleFile(source, "derived", "render.json");
            using var renderDoc = JsonDocument.Parse(snapshot.Read(renderPath, 8 * 1024 * 1024));
            var render = renderDoc.RootElement;
            Require(render.GetProperty("schemaVersion").GetString() == "m2-render-1" &&
                render.GetProperty("wave").GetProperty("file").GetString() == "mixed_full.wav" &&
                render.GetProperty("sourceStorageComplete").GetBoolean() &&
                render.GetProperty("sourceManifestSha256").GetString() == snapshot.Hash(manifestPath) &&
                render.GetProperty("sourceIndexSha256").GetString() == snapshot.Hash(indexPath), "render-source-binding-mismatch");
            string mix = Path.Combine(Path.GetDirectoryName(renderPath)!, "mixed_full.wav");
            WaveInfo wave = ReadWave(snapshot.Stream(input)), mixed = ReadWave(snapshot.Stream(mix));
            Require(wave.Rate == 16000 && mixed.Rate == 48000 && wave.Frames > 0 &&
                durationMs == checked((wave.Frames * 1000 + wave.Rate - 1) / wave.Rate) &&
                snapshot.Hash(input) == attempt.GetProperty("inputWaveSha256").GetString() &&
                snapshot.Hash(mix) == render.GetProperty("wave").GetProperty("sha256").GetString() &&
                mixed.Frames == render.GetProperty("frameCount").GetInt64() &&
                mixed.Rate == render.GetProperty("sampleRate").GetInt32(), "wave-hash-frame-or-duration-mismatch");
            long duration100ns = manifest.GetProperty("duration100ns").GetInt64();
            Require(duration100ns == render.GetProperty("duration100ns").GetInt64() &&
                durationMs == checked((duration100ns + 9999) / 10000) &&
                mixed.Frames == checked((long)(((Int128)duration100ns * mixed.Rate + 9999999) / 10000000)) &&
                Math.Abs(wave.Frames - mixed.Frames * (decimal)wave.Rate / mixed.Rate) <= 1 &&
                manifest.GetProperty("timelinePolicy").GetString() == render.GetProperty("timelinePolicy").GetString(),
                "recording-timeline-binding-mismatch");

            phase = "saved-output-pairing";
            string attemptDirectory = Path.GetDirectoryName(attemptPath)!;
            string srtPath = Path.Combine(attemptDirectory, "whisper-output.srt");
            string tokenPath = Path.Combine(attemptDirectory, "whisper-output.json");
            byte[] rawSrt = snapshot.Read(srtPath, 8 * 1024 * 1024), rawTokens = snapshot.Read(tokenPath, 64 * 1024 * 1024);
            var rawSegments = ParseProductionSrt(rawSrt, id);
            using var tokensDoc = JsonDocument.Parse(rawTokens);
            var entries = tokensDoc.RootElement.GetProperty("transcription");
            Require(entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == rawSegments.Count,
                "srt-json-count-mismatch");
            int index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var cue = rawSegments[index++];
                Require(entry.GetProperty("text").GetString()!.Trim() == cue.Text &&
                    entry.GetProperty("offsets").GetProperty("from").GetInt64() == cue.StartMs &&
                    entry.GetProperty("offsets").GetProperty("to").GetInt64() == cue.EndMs, "srt-json-content-or-time-mismatch");
            }
            string sourceText = CanonicalText(rawSegments);
            Require(sourceText.Length > 0, "source-text-empty");

            phase = "new-recovery-workspace";
            Directory.CreateDirectory(output); outputCreated = true;
            string workspace = Path.Combine(output, "recovery-workspace", id);
            Directory.CreateDirectory(workspace);
            CopyNew(snapshot, manifestPath, Path.Combine(workspace, "meeting.json"));
            string evidence = Path.Combine(workspace, "saved-whisper-output");
            Directory.CreateDirectory(evidence);
            string copiedSrt = Path.Combine(evidence, "whisper-output.srt"), copiedTokens = Path.Combine(evidence, "whisper-output.json");
            WriteNew(copiedSrt, rawSrt); WriteNew(copiedTokens, rawTokens);
            Require(HashFile(copiedSrt) == snapshot.Hash(srtPath) && HashFile(copiedTokens) == snapshot.Hash(tokenPath),
                "copied-evidence-hash-mismatch");
            WriteJson(Path.Combine(output, "source-files.private.json"), new
            {
                PrivateLocalOnly = true, SourceAudioFilesNotCopied = true,
                Files = snapshot.Manifest, InputWaveSha256 = snapshot.Hash(input), MixedWaveSha256 = snapshot.Hash(mix),
                RawSrtSha256 = snapshot.Hash(srtPath), RawTokenJsonSha256 = snapshot.Hash(tokenPath)
            });

            phase = "production-whisper-import";
            var request = new TranscriptImportRequest(workspace, id, durationMs, copiedSrt,
                "whisper.cpp（本地已完成输出恢复，待人工校对）",
                attempt.TryGetProperty("modelName", out var name) ? name.GetString() : null, IsSynthetic: false);
            MethodInfo method = typeof(TranscriptService).GetMethod("ImportWhisper", BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(TranscriptImportRequest), typeof(string), typeof(CancellationToken)], null)
                ?? throw new MissingMethodException("Expected production Whisper import entry point is unavailable.");
            var revision = (TranscriptRevision)(method.Invoke(null, [request, copiedTokens, CancellationToken.None])
                ?? throw new InvalidDataException("Production import returned no revision."));
            revision = TranscriptService.Load(workspace, revision.RevisionId);
            Require(!revision.IsSynthetic && revision.MeetingId == id && revision.MeetingDurationMs == durationMs &&
                CanonicalText(revision.Segments) == sourceText && revision.OriginalSha256 == snapshot.Hash(srtPath),
                "recovered-text-or-identity-changed");

            phase = "complete-user-exports";
            string textPath = Path.Combine(output, "RecoveredTranscript.txt"), jsonPath = Path.Combine(output, "RecoveredTranscript.json");
            TranscriptSubtitleExporter.ExportText(workspace, revision.RevisionId, textPath);
            string revisionPath = Path.Combine(workspace, "transcripts", "original", revision.RevisionId, "revision.json");
            WriteNew(jsonPath, File.ReadAllBytes(revisionPath));
            Require(new FileInfo(jsonPath).Length <= 8 * 1024 * 1024, "reimport-json-exceeds-product-limit");
            Require(CanonicalText(File.ReadAllText(textPath, Utf8)) == sourceText, "txt-export-lost-text");
            string roundTrip = Path.Combine(output, "roundtrip-validation", id);
            Directory.CreateDirectory(roundTrip); CopyNew(snapshot, manifestPath, Path.Combine(roundTrip, "meeting.json"));
            var reimported = TranscriptService.Import(new(roundTrip, id, durationMs, jsonPath,
                "恢复JSON往返检查", IsSynthetic: false));
            Require(!reimported.IsSynthetic && reimported.MeetingId == id && reimported.Segments.SequenceEqual(revision.Segments),
                "exported-project-json-cannot-roundtrip");
            int untimed = revision.Segments.Count(segment => segment.StartMs is null);
            string subtitleStatus = "NotRequested";
            if (untimed > 0)
            {
                string forbidden = Path.Combine(output, "must-not-drop-untimed.srt");
                bool rejected = false;
                try { TranscriptSubtitleExporter.Export(workspace, revision.RevisionId, forbidden); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected && !File.Exists(forbidden), "untimed-srt-export-did-not-refuse");
                subtitleStatus = "BlockedBecauseUntimedTextIsPreserved";
            }
            string instructions = "# 已完成识别结果的本地恢复\n\n" +
                "本目录复用本场已完成的 Whisper 原始输出，没有重新识别、播放或录音。原文件未修改。\n\n" +
                "1. 打开 M3J（或现有支持无时间 JSON 导入的 M3I Lite），在原来的这场会议中进入“转写与字幕”。\n" +
                "2. 使用导入转写功能，选择本目录的 RecoveredTranscript.json。不要选择创建新会议或导入音频。\n" +
                "3. 这是独立新版本，保留原会议身份及有效时间；已有版本和人工修改不覆盖。\n" +
                "4. RecoveredTranscript.txt 包含全文，方便阅读或复制，无需重跑识别。\n\n" +
                (untimed > 0 ? $"有 {untimed} 段时间待人工校对，文字已完整保留；这些段不能定位回听。本次不生成 SRT，避免静默漏字。\n\n" : "") +
                "recovery-workspace 仅用于恢复文字，包含原清单和原始字幕证据；未复制缺失的 remote 原轨，不是完整会议音频备份。\n";
            WriteNew(Path.Combine(output, "README-导入说明.md"), Utf8.GetBytes(instructions));

            phase = "originals-unchanged";
            unchanged = snapshot.VerifyUnchanged();
            Require(unchanged && EnumerateSafeFiles(source).Count() == supplied.Distinct(StringComparer.OrdinalIgnoreCase).Count(path => Inside(source, path)),
                "source-files-changed");
            WriteJson(Path.Combine(output, "report.json"), new
            {
                Status = "PASS", IsSynthetic = false, EngineInvoked = false, AudioPlayed = false, CaptureStarted = false,
                SourceFilesUnchanged = unchanged, OriginalAudioNotProvidedOrReconstructed = true,
                InputFrames = wave.Frames, InputSampleRate = wave.Rate, InputDurationMs = wave.Frames * 1000m / wave.Rate,
                MeetingDurationMs = durationMs, MixedFrames = mixed.Frames, AllAudioAndRenderBindingsVerified = true,
                RawSegmentCount = rawSegments.Count, SegmentCount = revision.Segments.Count, UntimedCount = untimed,
                RawTextCharactersIgnoringWhitespace = sourceText.Length, RecoveredTextCharactersIgnoringWhitespace = CanonicalText(revision.Segments).Length,
                TextSha256IgnoringWhitespace = HashText(sourceText), NonWhitespaceTextPreserved = true,
                WhitespaceMayReflectSentenceSegmentation = true,
                FirstTimedStartMs = revision.Segments.Where(segment => segment.StartMs is not null).Select(segment => segment.StartMs).Min(),
                LastTimedEndMs = revision.Segments.Where(segment => segment.EndMs is not null).Select(segment => segment.EndMs).Max(),
                WarningCount = revision.Warnings.Count, JsonRoundTripVerified = true, CompleteTxtVerified = true,
                SubtitleStatus = subtitleStatus, RevisionIsSynthetic = revision.IsSynthetic
            });
            Console.WriteLine($"Saved-output recovery PASS; segments={revision.Segments.Count}; untimed={untimed}; source files unchanged.");
            return 0;
        }
        catch (Exception error)
        {
            if (snapshot is not null) try { unchanged = snapshot.VerifyUnchanged(); } catch { unchanged = false; }
            string errorType = error is TargetInvocationException { InnerException: { } inner } ? inner.GetType().Name : error.GetType().Name;
            if (outputCreated && output is not null && Directory.Exists(output) && !File.Exists(Path.Combine(output, "report.json")))
                WriteJson(Path.Combine(output, "report.json"), new { Status = "FAIL", Phase = phase, ErrorType = errorType, SourceFilesUnchanged = unchanged,
                    PrivateErrorMessageOmitted = true, EngineInvoked = false, AudioPlayed = false, CaptureStarted = false });
            Console.Error.WriteLine($"Saved-output recovery FAIL; phase={phase}; type={errorType}; private details omitted.");
            return 1;
        }
        finally { snapshot?.Dispose(); }
    }

    private static List<TranscriptSegment> ParseProductionSrt(byte[] bytes, string id)
    {
        var parser = typeof(TranscriptService).Assembly.GetType("ProjectEcho.Core.Transcription.TranscriptParser", throwOnError: true)!;
        var method = parser.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Expected production transcript parser is unavailable.");
        return (List<TranscriptSegment>)(method.Invoke(null, [bytes, ".srt", id, new List<string>()])
            ?? throw new InvalidDataException("Production subtitle parser returned no segments."));
    }

    private static WaveInfo ReadWave(FileStream stream)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Require(reader.ReadUInt32() == 0x46464952, "wave-riff-required");
        Require((long)reader.ReadUInt32() + 8 == stream.Length && reader.ReadUInt32() == 0x45564157, "wave-riff-length-mismatch");
        int rate = 0, align = 0; bool format = false;
        while (stream.Position + 8 <= stream.Length)
        {
            uint tag = reader.ReadUInt32(), bytes = reader.ReadUInt32(); long start = stream.Position;
            Require(start + bytes <= stream.Length, "wave-chunk-truncated");
            if (tag == 0x20746d66)
            {
                Require(!format && bytes >= 16 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1, "pcm16-mono-required");
                rate = reader.ReadInt32(); int byteRate = reader.ReadInt32(); align = reader.ReadUInt16(); int bits = reader.ReadUInt16();
                Require(rate is 16000 or 48000 && align == 2 && bits == 16 && byteRate == rate * align, "wave-format-mismatch");
                format = true;
            }
            if (tag == 0x61746164)
            {
                Require(format && bytes > 0 && bytes % align == 0 && start + bytes == stream.Length, "wave-data-eof-mismatch");
                return new(rate, bytes / align, stream.Length, bytes);
            }
            stream.Position = start + bytes + (bytes & 1);
        }
        throw new InvalidDataException("Wave data chunk missing.");
    }

    private static Dictionary<string, string> ReadPrivateAudit(string root, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        NoReparse(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Require(document.RootElement.GetProperty("Schema").GetString() == "m3i-asr-private-source-manifest-1", "private-audit-schema-mismatch");
        string debug = Path.Combine(root, "DEBUG");
        foreach (var entry in document.RootElement.GetProperty("Files").EnumerateArray())
        {
            string file = SafePath(root, Path.Combine(root, entry.GetProperty("ProjectRelativePath").GetString()!));
            Require(Inside(debug, file), "private-audit-outside-debug");
            result.Add(file, entry.GetProperty("Sha256").GetString()!);
        }
        return result;
    }

    private sealed class SourceSnapshot : IDisposable
    {
        private readonly Dictionary<string, FileStream> streams = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly string root;
        internal SourceSnapshot(string root, IEnumerable<string> paths)
        {
            this.root = root;
            try
            {
                foreach (string path in paths.Select(value => Path.GetFullPath(value)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    NoReparse(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                    streams.Add(path, stream); hashes.Add(path, HashStream(stream));
                }
            }
            catch { Dispose(); throw; }
        }
        internal IReadOnlyList<SourceFile> Manifest => streams.Select(pair =>
            new SourceFile(Path.GetRelativePath(root, pair.Key).Replace('\\', '/'), pair.Value.Length, hashes[pair.Key])).ToArray();
        internal FileStream Stream(string path) => streams.TryGetValue(Path.GetFullPath(path), out var stream) ? stream
            : throw new InvalidDataException("Input was not in the locked source snapshot.");
        internal string Hash(string path) => hashes[Path.GetFullPath(path)];
        internal byte[] Read(string path, int limit)
        {
            var stream = Stream(path); Require(stream.Length <= limit, "bounded-file-limit");
            stream.Position = 0; var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
        }
        internal byte[] FirstLine(string path, int limit)
        {
            var stream = Stream(path); stream.Position = 0;
            using var result = new MemoryStream(); int value;
            while ((value = stream.ReadByte()) >= 0 && value != 10)
            { Require(result.Length < limit, "index-line-limit"); result.WriteByte((byte)value); }
            Require(value == 10, "index-first-line-truncated"); return result.ToArray();
        }
        internal bool VerifyUnchanged() => streams.All(pair => File.Exists(pair.Key) && HashStream(pair.Value) == hashes[pair.Key]);
        public void Dispose() { foreach (var stream in streams.Values) stream.Dispose(); }
    }

    private static string FindAuthorizedRoot()
    {
        string root = SyntheticAudioProbe.FindProjectRoot();
        string? parent = Path.GetDirectoryName(root);
        if (Path.GetFileName(root).Equals("lite", StringComparison.OrdinalIgnoreCase) && parent is not null &&
            File.Exists(Path.Combine(root, ".git")) && File.Exists(Path.Combine(parent, "AGENTS.md")) && File.Exists(Path.Combine(parent, "global.json")))
            root = parent;
        return root;
    }
    private static string SafePath(string root, string path)
    {
        string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        Require(Inside(root, full) && !full.StartsWith("\\\\", StringComparison.Ordinal) &&
            !full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':') &&
            new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Network, "project-local-path-required");
        NoReparse(full); return full;
    }
    private static bool Inside(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void NoReparse(string path)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse points are not allowed.");
    }
    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        NoReparse(root);
        foreach (string file in Directory.EnumerateFiles(root)) { NoReparse(file); yield return file; }
        foreach (string directory in Directory.EnumerateDirectories(root))
        { NoReparse(directory); foreach (string file in EnumerateSafeFiles(directory)) yield return file; }
    }
    private static string SingleFile(string source, string folder, string name)
    {
        var files = EnumerateSafeFiles(Path.Combine(source, folder)).Where(file => Path.GetFileName(file) == name).Take(2).ToArray();
        Require(files.Length == 1, "ambiguous-or-missing-required-input"); return Path.GetFullPath(files[0]);
    }
    private static string CanonicalText(IEnumerable<TranscriptSegment> segments) => CanonicalText(string.Concat(segments.Select(segment => segment.Text)));
    private static string CanonicalText(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    private static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return HashStream(stream); }
    private static string HashStream(Stream stream) { stream.Position = 0; return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static void CopyNew(SourceSnapshot source, string from, string to) => WriteNew(to, source.Read(from, 8 * 1024 * 1024));
    private static void WriteNew(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true); }
    private static void WriteJson(string path, object value) => WriteNew(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    private static void Require(bool valid, string code) { if (!valid) throw new InvalidDataException(code); }
}
