using System.Globalization;
using System.Text;

namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// Writes the whole catalogue read from the installation as one Markdown page — every id, in every language
/// group, with the capability that answers it or a dash — so the gap inventory is diffable instead of a
/// truncated answer.
/// </summary>
public static class ReSharperToCoverExport
{
    /// <summary>Renders and writes the page; returns the full path it was written to.</summary>
    /// <param name="catalogue">The catalogue read from the installation.</param>
    /// <param name="answered">The ids a capability here answers, in any spelling; a dot-less id is matched loosely.</param>
    /// <param name="path">Where the Markdown page goes; its directory is created when missing.</param>
    public static string Write(ReSharperCatalogue catalogue, IReadOnlyCollection<string> answered, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(full, Render(catalogue, answered), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    private static string Render(ReSharperCatalogue catalogue, IReadOnlyCollection<string> answered)
    {
        var known = answered.ToHashSet(StringComparer.Ordinal);
        var uncovered = catalogue.Ids.Where(id => !known.Contains(id.Id)).ToList();

        var groups = catalogue.Ids
            .GroupBy(GroupOf, StringComparer.Ordinal)
            .Select(group => new
            {
                Name = group.Key,
                Ids = group.OrderBy(id => id.Id, StringComparer.Ordinal).ToList(),
                Answered = group.Count(id => known.Contains(id.Id)),
            })
            .OrderByDescending(group => group.Ids.Count)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append("# ReSharper — the whole inspection catalogue\n\n");
        builder.Append("Every configurable inspection id the local Rider installation declares, grouped by the languages the\n");
        builder.Append("rule applies to — including the groups that never apply to C# — each with the capability here that\n");
        builder.Append("answers it, or `—` when there is none. This is the full inventory behind `resharper_inspection_map`:\n");
        builder.Append("the `—` rows are the rules that would have to be reimplemented over Roslyn, the `✅` rows are the part\n");
        builder.Append("the curated map already answers.\n");
        builder.Append("The ✅ side of the same comparison is [ReSharper & CodeMaid — Feature Coverage](resharper-codemaid-coverage.md);\n");
        builder.Append("the gaps that are not catalogue ids are in [What is missing](gaps.md).\n\n");

        builder.Append("| | |\n|---|---|\n");
        builder.Append(CultureInfo.InvariantCulture, $"| Installation | {Installation(catalogue)} |\n");
        builder.Append(CultureInfo.InvariantCulture, $"| Read on | {DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} |\n");
        builder.Append(CultureInfo.InvariantCulture, $"| Ids declared | {catalogue.Ids.Count:N0} |\n");
        builder.Append(CultureInfo.InvariantCulture, $"| Answered by a capability here | {catalogue.Ids.Count - uncovered.Count:N0} |\n");
        builder.Append(CultureInfo.InvariantCulture, $"| **With no capability** | **{uncovered.Count:N0}** |\n\n");

        builder.Append("The ids are read from the installation's own metadata — each `ConfigurableSeverityHighlighting`\n");
        builder.Append("argument, linked to the `ElementProblemAnalyzer` that produces it — so the denominator is the engine's\n");
        builder.Append("catalogue, not an estimate. The ~2,500 inspections the coverage pages quote is JetBrains' own count\n");
        builder.Append("across the product's languages and releases; what one installation declares in its metadata today is\n");
        builder.Append("the number above. An id with `—` is a rule that would have to be reimplemented over Roslyn, not\n");
        builder.Append("registered. Groups that do not apply to C# are listed too: the catalogue is the catalogue.\n\n");
        builder.Append("A row whose description is `—` carries none in this installation's resources; the analyzer that\n");
        builder.Append("produces it is reported by `resharper_inspection_map`.\n\n");

        builder.Append("| Language group | Ids | Answered | With no capability |\n|---|---:|---:|---:|\n");
        foreach (var group in groups)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"| `{group.Name}` | {group.Ids.Count:N0} | {group.Answered:N0} | {group.Ids.Count - group.Answered:N0} |\n");
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"| **Total** | **{catalogue.Ids.Count:N0}** | **{catalogue.Ids.Count - uncovered.Count:N0}** | **{uncovered.Count:N0}** |\n\n");

        foreach (var group in groups)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"## {Title(group.Name)} — {group.Ids.Count:N0} {(group.Ids.Count == 1 ? "id" : "ids")}\n\n");
            builder.Append("| Id | What the rule reports | Coverage |\n|---|---|---|\n");
            foreach (var id in group.Ids)
            {
                var description = string.IsNullOrWhiteSpace(id.Description) ? "—" : Cell(id.Description);
                var coverage = known.Contains(id.Id) ? "✅" : "—";
                builder.Append(CultureInfo.InvariantCulture, $"| `{id.Id}` | {description} | {coverage} |\n");
            }

            builder.Append('\n');
        }

        var absent = answered.Where(id => catalogue.Ids.All(entry => !string.Equals(entry.Id, id, StringComparison.Ordinal))).ToList();
        if (absent.Count > 0)
        {
            builder.Append("## The curated ids this installation no longer declares\n\n");
            builder.Append("The coverage map names these, and this Rider version has dropped them (a rename or a merged rule):\n");
            builder.Append(string.Join(", ", absent.Select(id => "`" + id + "`")));
            builder.Append(".\n");
        }

        return builder.ToString();
    }

    /// <summary>The key the summary groups on: the language group the attribute declared.</summary>
    private static string GroupOf(ReSharperInspectionId id) =>
        string.IsNullOrWhiteSpace(id.Languages) ? "(none declared)" : id.Languages;

    private static string Installation(ReSharperCatalogue catalogue)
    {
        if (catalogue.Path is not { Length: > 0 } path) return "not read";
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return (name.Length > 0 ? name : path).Replace("|", "\\|", StringComparison.Ordinal);
    }

    /// <summary>A Markdown heading title from the language group, so <c>CSHARP,VBASIC</c> reads as both.</summary>
    private static string Title(string group) => group switch    {
        "CSHARP" => "C#",
        "VBASIC" => "Visual Basic",
        "CSHARP,VBASIC" => "C# and Visual Basic",
        "JAVA_SCRIPT" => "JavaScript",
        "TYPE_SCRIPT" => "TypeScript",
        _ => group,
    };

    /// <summary>One table cell: a pipe would end the column and a newline would end the row.</summary>
    private static string Cell(string text) => text
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();
}
