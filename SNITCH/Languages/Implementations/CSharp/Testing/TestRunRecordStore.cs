using System.Text.Json;

namespace Snitch.Languages.CSharp.Testing;

/// <summary>One test project and the tests that failed in it during the last run.</summary>
public sealed record FailedTestGroup(string Project, List<string> Failed);

/// <summary>
/// The failures of the last test run — written by <c>run_test</c> and <c>run_tests</c>, read by
/// <c>rerun_failed</c> — so "re-run the failures" needs no argument.
/// </summary>
public sealed record TestRunRecord(DateTimeOffset At, List<FailedTestGroup> Projects)
{
    /// <summary>Every recorded failure, with the project it belongs to.</summary>
    public IEnumerable<(string Project, string Test)> AllFailures() =>
        Projects.SelectMany(group => group.Failed.Select(test => (group.Project, test)));
}

/// <summary>
/// Reads and writes <c>.visualMCP/last-test-run.json</c>. One record at a time: a green run writes an
/// empty one, so a stale failure cannot be re-run by accident.
/// </summary>
public static class TestRunRecordStore
{
    public const string RelativePath = ".visualMCP/last-test-run.json";

    public static string PathFor(string? solutionDirectory) =>
        Path.Combine(solutionDirectory ?? Path.GetTempPath(), ".visualMCP", "last-test-run.json");

    public static TestRunRecord? Read(string? solutionDirectory)
    {
        try
        {
            var path = PathFor(solutionDirectory);
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var at = root.TryGetProperty("At", out var atElement) && atElement.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            var projects = new List<FailedTestGroup>();
            if (root.TryGetProperty("Projects", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                    projects.Add(new FailedTestGroup(NameOf(group), FailedOf(group)));
            }
            else if (root.TryGetProperty("Project", out _))
            {
                // The first shape of the file: one project, one list.
                projects.Add(new FailedTestGroup(NameOf(root), FailedOf(root)));
            }

            return new TestRunRecord(at, projects);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the record, dropping the projects that had no failure; an empty list clears it.</summary>
    public static void Write(string? solutionDirectory, IEnumerable<FailedTestGroup> groups)
    {
        try
        {
            var path = PathFor(solutionDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var failures = groups.Where(group => group.Failed.Count > 0).ToList();
            var record = new TestRunRecord(DateTimeOffset.UtcNow, failures);
            File.WriteAllText(path, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The record is a convenience: a read-only folder must not fail the run.
        }
    }

    private static string NameOf(JsonElement element) =>
        element.TryGetProperty("Project", out var project) ? project.GetString() ?? "(unknown)" : "(unknown)";

    private static List<string> FailedOf(JsonElement element)
    {
        if (!element.TryGetProperty("Failed", out var failed) || failed.ValueKind != JsonValueKind.Array)
            return [];

        return failed.EnumerateArray()
            .Select(test => test.GetString())
            .Where(test => !string.IsNullOrWhiteSpace(test))
            .Select(test => test!)
            .ToList();
    }
}
