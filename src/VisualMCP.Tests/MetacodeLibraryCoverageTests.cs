using System.Text;
using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The library's own source is the model's first client: every file of Snitch.Metacode must translate
/// without a diagnostic. A C# feature the frontend cannot place fails here, before it becomes a RAW
/// node in the solution's index — which is how the five expression nodes this pass added were found.
/// </summary>
public sealed class MetacodeLibraryCoverageTests
{
    [Fact]
    public void Every_file_of_the_library_translates_without_diagnostics()
    {
        var root = FindLibrary();
        if (root is null)
            return; // the test project is running outside the repository.

        var translator = new MetacodeTranslator([new CSharpFrontend()]);
        var failures = new StringBuilder();
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var document = translator.Translate(MetacodeSource.FromFile(file));
            if (document.Diagnostics.Count == 0)
                continue;

            failures.AppendLine(Path.GetRelativePath(root, file));
            foreach (var diagnostic in document.Diagnostics)
                failures.Append("  ").Append(diagnostic).AppendLine();
        }

        Assert.True(
            failures.Length == 0,
            "The metacode model cannot place part of its own library:\n" + failures);
    }

    private static string? FindLibrary()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "SNITCH", "Libraries", "Snitch.Metacode");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
