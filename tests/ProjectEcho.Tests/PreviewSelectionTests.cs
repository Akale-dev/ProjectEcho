/// <summary>Filename-selection fixtures only; these placeholder files are not recorded audio.</summary>
internal static class PreviewSelectionTests
{
    public static void Run(string outputDirectory)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AGENTS.md"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Project root not found for synthetic fixtures.");
        var output = Path.GetFullPath(outputDirectory);
        if (!output.StartsWith(Path.TrimEndingDirectorySeparator(root.FullName) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixtures must stay in the project.");
        for (var current = new DirectoryInfo(output); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixture output cannot traverse directory links.");

        var fixtures = Path.Combine(output, "preview-selection-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "TEST_FIXTURE"),
            "TEST_FIXTURE_NOT_CAPTURE: filename-selection tests only; placeholder WAVs are not audio.");
        const string legacy = "mixed-preview.wav";
        const string degraded = "mixed-preview-incomplete-11111111111111111111111111111111.wav";
        const string otherDegraded = "mixed-preview-incomplete-22222222222222222222222222222222.wav";

        var unique = Fixture("unique-degraded", degraded);
        Expect(unique, false, degraded, null);
        Expect(Fixture("legacy-incomplete", legacy), false, legacy, null);
        Expect(Fixture("legacy-complete", legacy), true, legacy, null);
        Expect(Fixture("complete-rejects-degraded", degraded), true, null, "mixed:missing_wave");
        Expect(Fixture("two-degraded", degraded, otherDegraded), false, null, "mixed:ambiguous_preview");
        Expect(Fixture("legacy-and-degraded", legacy, degraded), false, null, "mixed:ambiguous_preview");
        Expect(Fixture("no-candidate"), false, null, "mixed:missing_wave");
        Expect(Fixture("degraded-with-partial", degraded, degraded + ".partial"),
            false, null, "mixed:preview_not_finalized");
        Expect(Fixture("legacy-with-partial", legacy, legacy + ".partial"),
            true, null, "mixed:preview_not_finalized");
        Expect(Fixture("malformed-name", "mixed-preview-incomplete-not-a-guid.wav"),
            false, null, "mixed:invalid_preview_name");

        var nested = Fixture("no-recursive-selection");
        var nestedChild = Path.Combine(nested, "child");
        Directory.CreateDirectory(nestedChild);
        File.WriteAllText(Path.Combine(nestedChild, degraded), "TEST_FIXTURE_NOT_CAPTURE");
        Expect(nested, false, null, "mixed:missing_wave");

        var existingFailures = new List<string> { "capture_incomplete", "remote:device_overlap:1" };
        var selected = RecordingAudit.SelectPreviewPath(unique, false, existingFailures);
        Require(Path.GetFileName(selected) == degraded && existingFailures.SequenceEqual(
            new[] { "capture_incomplete", "remote:device_overlap:1" }),
            "Selecting a degraded preview must preserve prior capture and device failures.");

        string Fixture(string name, params string[] filenames)
        {
            var directory = Path.Combine(fixtures, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "TEST_FIXTURE"), "TEST_FIXTURE_NOT_CAPTURE");
            foreach (var filename in filenames)
                File.WriteAllText(Path.Combine(directory, filename), "TEST_FIXTURE_NOT_CAPTURE: not a WAV payload.");
            return directory;
        }
    }

    private static void Expect(string directory, bool complete, string? expectedFile, string? expectedError)
    {
        var errors = new List<string>();
        var selected = RecordingAudit.SelectPreviewPath(directory, complete, errors);
        Require(Path.GetFileName(selected) == expectedFile,
            $"Unexpected selected preview in fixture {Path.GetFileName(directory)}.");
        Require(expectedError is null ? errors.Count == 0 : errors.Count == 1 && errors[0] == expectedError,
            $"Unexpected preview-selection errors in fixture {Path.GetFileName(directory)}: {string.Join(",", errors)}.");
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
