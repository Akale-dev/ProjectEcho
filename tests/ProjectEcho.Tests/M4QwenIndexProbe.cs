using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectEcho.Core.Transcription;

/// <summary>
/// File-layer verification of real Qwen revisions using native SQLite. Only transcript/meeting
/// metadata is copied from the named public/synthetic probe; no source audio or inference is used.
/// </summary>
internal static class M4QwenIndexProbe
{
    internal static int Run(string[] args)
    {
        if (args.Length != 5 || args[0] != "--m4-qwen-index-probe" || args[1] != "--source" || args[3] != "--output")
        {
            Console.Error.WriteLine("Usage: --m4-qwen-index-probe --source <real-short-04 public/synthetic meeting> --output <new project-local directory>");
            return 2;
        }
        string project = FindProject();
        string source = InsideProject(project, args[2]), output = InsideProject(project, args[4]);
        string authorizedSource = Path.Combine(project, ".local", "M4", "real-short-04") + Path.DirectorySeparatorChar;
        if (!source.StartsWith(authorizedSource, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only the explicitly selected real-short-04 public/synthetic transcript evidence may be copied.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("Use a new empty output directory; previous evidence is retained.");
        Directory.CreateDirectory(output);
        var checks = new List<string>(); var errors = new List<string>();
        var sourceHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        object? rebuild = null, negative = null, sourceDescription = null, reviewEvidence = null;
        string copy = Path.Combine(output, "rebuild-copy", Path.GetFileName(source));
        string rejectedCopy = Path.Combine(output, "alignment-tamper-copy", Path.GetFileName(source));
        var watch = Stopwatch.StartNew(); DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            Require(OperatingSystem.IsWindows(), "Actual Windows native SQLite execution");
            var sourceRevisions = TranscriptService.ListRevisions(source);
            var parent = sourceRevisions.FirstOrDefault(revision => revision.Alignment is not null && revision.ParentRevisionId is null)
                ?? throw new InvalidDataException("Selected source has no original aligned Qwen version.");
            Require(parent.IsSynthetic && parent.Provider.Contains("Qwen3-ASR", StringComparison.Ordinal),
                "Selected source has a real-native-Qwen original explicitly marked as a synthetic/public probe");
            Require(sourceRevisions.Any(revision => revision.ParentRevisionId is not null),
                "Selected source also contains an existing reviewed revision");
            var files = MetadataFiles(source, sourceRevisions);
            foreach (string relative in files) sourceHashes.Add(relative, Hash(Path.Combine(source, relative)));
            using var meetingJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "meeting.json")));
            sourceDescription = new
            {
                SourceMeeting = source, SelectedOriginalRevision = parent.RevisionId,
                SourceMeetingSyntheticFlag = meetingJson.RootElement.TryGetProperty("isSynthetic", out var flag) && flag.GetBoolean(),
                TranscriptSyntheticFlag = parent.IsSynthetic,
                Note = "Source meeting.json is preserved byte-for-byte, including its audio-import metadata flag. The real Qwen transcript and this edit are explicitly synthetic probe revisions.",
                Revisions = sourceRevisions.Select(revision => new { revision.RevisionId, revision.ParentRevisionId, revision.Provider,
                    revision.IsSynthetic, HasAlignment = revision.Alignment is not null, revision.OriginalSha256 }).ToArray(),
                CopiedMetadataFiles = files
            };
            CopyMetadata(copy, files);
            Require(!Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories).Any(IsAudioOrModel),
                "Isolated copy contains only meeting/transcript metadata, no audio or model");
            string query = string.Concat(parent.Segments.First().Text.Trim().EnumerateRunes().Take(3).Select(rune => rune.ToString()));
            Require(query.EnumerateRunes().Count() == 3, "Search query exercises SQLite FTS5 trigram path");
            var originalHits = TranscriptService.Search(copy, query);
            Require(originalHits.Any(hit => hit.RevisionId == parent.RevisionId && !hit.IsReviewed),
                "Native SQLite search finds the original Qwen revision with Alignment provenance");
            VerifyHits(copy, query, originalHits, "Original index");

            const string marker = "M4_INDEX_SYNTHETIC_REVIEW_20261004";
            const string note = "SYNTHETIC INDEX PROBE ONLY: edit made solely in the isolated metadata copy; source real-Qwen files and audio were not changed.";
            var reviewed = TranscriptService.CreateRevision(copy, parent.RevisionId,
                parent.Segments.Select((segment, index) => index == 0 ? segment with { Text = marker + " " + segment.Text } : segment).ToArray(), note);
            Require(reviewed.IsSynthetic && reviewed.ParentRevisionId == parent.RevisionId && reviewed.ReviewNote == note &&
                reviewed.Alignment == parent.Alignment && reviewed.OriginalFile == parent.OriginalFile && reviewed.OriginalSha256 == parent.OriginalSha256 &&
                reviewed.Segments.Zip(parent.Segments).All(pair => pair.First with { Text = pair.Second.Text } == pair.Second),
                "Synthetic edit preserves parent, original hash, acoustic provenance, every time and segment identity");
            var editHits = TranscriptService.Search(copy, marker);
            Require(editHits.Count == 1 && editHits[0].IsReviewed && editHits[0].RevisionId == reviewed.RevisionId &&
                editHits[0].StartMs == parent.Segments[0].StartMs && editHits[0].EndMs == parent.Segments[0].EndMs,
                "Review search returns its own version and exact original milliseconds");
            var before = TranscriptService.Search(copy, query);
            VerifyHits(copy, query, before, "Before damaged-cache rebuild");
            var authoritative = HashMetadata(copy);
            string database = Path.Combine(copy, "transcripts", "search.sqlite3");
            Require(File.Exists(database), "Search created a real SQLite database in the isolated copy");
            string cleanDatabaseSha256 = Hash(database);
            byte[] corruption = Encoding.UTF8.GetBytes("M4 SYNTHETIC damaged SQLite cache; authoritative Qwen JSON must rebuild it.\n");
            File.WriteAllBytes(database, corruption); string corruptSha256 = Hash(database);
            var after = TranscriptService.Search(copy, query);
            var editAfter = TranscriptService.Search(copy, marker);
            Require(before.SequenceEqual(after) && editHits.SequenceEqual(editAfter),
                "Rebuilt cache preserves all hit versions, segment IDs, text and start/end milliseconds");
            VerifyHits(copy, query, after, "After damaged-cache rebuild");
            var backups = Directory.EnumerateFiles(Path.GetDirectoryName(database)!, "search.sqlite3.rebuild-*")
                .Where(path => Guid.TryParseExact(Path.GetFileName(path)["search.sqlite3.rebuild-".Length..], "N", out _)).ToArray();
            Require(backups.Length == 1 && Hash(backups[0]) == corruptSha256,
                "Damaged SQLite bytes survive in a unique rebuild backup");
            using (var rebuiltFile = File.OpenRead(database))
            {
                byte[] header = new byte[16]; rebuiltFile.ReadExactly(header);
                Require(header.AsSpan().SequenceEqual("SQLite format 3\0"u8), "Rebuilt database has a real SQLite header");
            }
            Require(Unchanged(copy, authoritative), "All copied original/reviewed text, alignment and meeting hashes remain unchanged during rebuild");
            reviewEvidence = new { reviewed.RevisionId, reviewed.ParentRevisionId, reviewed.IsSynthetic, reviewed.ReviewNote,
                reviewed.OriginalSha256, reviewed.Alignment, Hits = editAfter };
            rebuild = new { MeetingDirectory = copy, Query = query, OriginalHits = originalHits, Before = before, After = after,
                Database = database, CleanDatabaseSha256 = cleanDatabaseSha256, CorruptSha256 = corruptSha256,
                RebuiltSha256 = Hash(database), Backups = backups.Select(path => new { Path = path, Sha256 = Hash(path) }).ToArray(),
                AuthoritativeHashes = authoritative };

            CopyMetadata(rejectedCopy, files);
            var cachedHits = TranscriptService.Search(rejectedCopy, query);
            Require(cachedHits.Any(hit => hit.RevisionId == parent.RevisionId), "Negative fixture first has an existing valid SQLite hit");
            string cachedDatabase = Path.Combine(rejectedCopy, "transcripts", "search.sqlite3");
            string cachedHash = Hash(cachedDatabase);
            string alignmentFile = Path.Combine(rejectedCopy, parent.Alignment!.EvidenceFile);
            string alignmentBefore = Hash(alignmentFile);
            var negativeOriginals = HashMetadata(rejectedCopy);
            File.AppendAllText(alignmentFile, " ", new UTF8Encoding(false)); // JSON remains parseable; only the declared SHA changes.
            string? rejection = null;
            try { _ = TranscriptService.Search(rejectedCopy, query); }
            catch (InvalidDataException ex) when (ex.Message.Contains("SHA-256", StringComparison.Ordinal)) { rejection = ex.Message; }
            Require(rejection is not null, "Existing SQLite cache cannot bypass tampered Alignment evidence SHA-256");
            Require(Hash(cachedDatabase) == cachedHash, "Rejected source is checked before cached SQLite content is reused or rewritten");
            negativeOriginals.Remove(Path.GetRelativePath(rejectedCopy, alignmentFile));
            Require(Unchanged(rejectedCopy, negativeOriginals), "Only the explicitly corrupted negative-fixture alignment file changed");
            negative = new { MeetingDirectory = rejectedCopy, CachedHitCount = cachedHits.Count, CacheSha256Unchanged = cachedHash,
                AlignmentFile = alignmentFile, BeforeSha256 = alignmentBefore, AfterSha256 = Hash(alignmentFile), Rejection = rejection };
            Require(Unchanged(source, sourceHashes), "All selected source Qwen originals, reviews, alignment files and meeting.json remain unchanged");
        }
        catch (Exception ex) { errors.Add(ex.ToString()); Console.Error.WriteLine(ex.Message); }
        var sourceIntegrity = sourceHashes.Select(pair => new { RelativePath = pair.Key, ExpectedSha256 = pair.Value,
            Unchanged = File.Exists(Path.Combine(source, pair.Key)) && Hash(Path.Combine(source, pair.Key)) == pair.Value }).ToArray();
        if (sourceIntegrity.Any(row => !row.Unchanged)) errors.Add("Source metadata changed during isolated file/index probe.");
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic &&
            (assembly.GetName().Name is "ProjectEcho.Core" or "Microsoft.Data.Sqlite" || assembly.GetName().Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true))
            .Select(assembly => new { assembly.FullName, assembly.Location, Sha256 = Hash(assembly.Location) }).ToArray();
        var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(module => module.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => new { Path = module.FileName, Sha256 = Hash(module.FileName) }).ToArray();
        var summary = new
        {
            Status = errors.Count == 0 ? "PASS" : "FAIL", StartedUtc = started, ElapsedMs = watch.ElapsedMilliseconds,
            OS = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(), Source = sourceDescription,
            Checks = checks, Errors = errors, Review = reviewEvidence, Rebuild = rebuild, Negative = negative,
            SourceIntegrity = sourceIntegrity, LoadedManagedAssemblies = loaded, LoadedNativeSqlite = native,
            ModelInferenceExecuted = false, AudioCopied = false, MicrophoneCollected = false, AudioPlayed = false, Uploaded = false,
            Scope = "Native SQLite/file-layer validation against copied real-native-Qwen public/synthetic transcript evidence. No new inference, no model-quality or recording claim."
        };
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("M4 Qwen index probe: " + summary.Status + "; " + Path.Combine(output, "summary.json"));
        return errors.Count == 0 ? 0 : 1;

        void Require(bool condition, string check) { if (!condition) throw new InvalidDataException(check); checks.Add(check); }
        void CopyMetadata(string destination, IReadOnlyList<string> paths)
        {
            foreach (string relative in paths)
            {
                string target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = new FileStream(Path.Combine(source, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var outputFile = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(outputFile); outputFile.Flush(true);
            }
            Require(Unchanged(destination, sourceHashes), "Read-only metadata copy hash check: " + Path.GetFileName(Path.GetDirectoryName(destination)));
        }
        void VerifyHits(string directory, string needle, IReadOnlyList<TranscriptSearchHit> hits, string label)
        {
            var expected = TranscriptService.ListRevisions(directory).SelectMany(revision => revision.Segments
                .Where(segment => segment.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(segment => new TranscriptSearchHit(revision.RevisionId, segment.SegmentId, segment.StartMs, segment.EndMs,
                    segment.Text, segment.SpeakerLabel, revision.ParentRevisionId is not null))).ToArray();
            Require(expected.SequenceEqual(hits), label + ": actual FTS results equal every authoritative revision/segment/time");
        }
    }

    private static IReadOnlyList<string> MetadataFiles(string source, IReadOnlyList<TranscriptRevision> revisions)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "meeting.json" };
        foreach (var revision in revisions)
        {
            paths.Add(Path.Combine("transcripts", revision.ParentRevisionId is null ? "original" : "reviewed", revision.RevisionId, "revision.json"));
            paths.Add(revision.OriginalFile);
            if (revision.Alignment is { } alignment) paths.Add(alignment.EvidenceFile);
            string mapping = Path.Combine(Path.GetDirectoryName(revision.OriginalFile)!, "source-export-manifest.json");
            if (File.Exists(Path.Combine(source, mapping))) paths.Add(mapping);
        }
        foreach (string path in paths)
            if (Path.IsPathRooted(path) || path.Split('/', '\\').Contains("..") || IsAudioOrModel(path) ||
                Path.GetExtension(path).ToLowerInvariant() is not (".json" or ".txt" or ".md" or ".srt" or ".vtt"))
                throw new InvalidDataException("Only authoritative transcript/meeting metadata may be copied.");
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string> HashMetadata(string directory)
    {
        var paths = new List<string> { Path.Combine(directory, "meeting.json") };
        foreach (string kind in new[] { "original", "reviewed" })
        {
            string folder = Path.Combine(directory, "transcripts", kind);
            if (Directory.Exists(folder)) paths.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories));
        }
        return paths.ToDictionary(path => Path.GetRelativePath(directory, path), Hash, StringComparer.OrdinalIgnoreCase);
    }
    private static bool Unchanged(string directory, IReadOnlyDictionary<string, string> hashes) =>
        hashes.All(pair => File.Exists(Path.Combine(directory, pair.Key)) && Hash(Path.Combine(directory, pair.Key)) == pair.Value);
    private static bool IsAudioOrModel(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".gguf" or ".bin" or ".onnx" or ".safetensors";
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static string FindProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the probe inside ProjectEcho.");
    }
    private static string InsideProject(string project, string path)
    {
        string full = Path.GetFullPath(path, project);
        if (!full.StartsWith(project.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Index probe paths must remain inside ProjectEcho.");
        for (string? current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Index probe paths cannot use reparse points.");
        return full;
    }
}
