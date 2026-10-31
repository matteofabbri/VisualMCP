using System.Text.Json;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.VB.Style;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// ReSharper's Code Cleanup as one call: the pieces already existed as separate tools (the style
/// rewrites, using pruning, region removal, member reordering, formatting), and cleanup is the order
/// they belong in. It is deliberately not a new engine — every step is the tool it was, run in the
/// scope the caller asked for, and the report is the concatenation of their reports.
/// <para>
/// The language-neutral steps (usings, regions, format) cover both C# and Visual Basic; the style step
/// runs each language's rule set over its own documents, and member reordering is C# only because the
/// order it enforces is C#'s own convention.
/// </para>
/// </summary>
public interface ICodeCleanupService
{
    Task<object> Cleanup(
        string scope,
        string? filePath,
        string? projectName,
        string? steps,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class CodeCleanupService : ICodeCleanupService
{
    /// <summary>The steps in the order they must run: rewriting first, formatting last.</summary>
    private static readonly string[] Order = ["style", "usings", "regions", "reorder", "format"];

    private readonly ISimplifyCodeService _style;
    private readonly IOptimizeUsingsService _usings;
    private readonly IRemoveRegionsService _regions;
    private readonly IReorderMembersService _reorder;
    private readonly IFormatCodeService _format;
    private readonly IRoslynWorkspace? _workspace;
    private readonly IVisualMcpStore? _store;

    public CodeCleanupService(
        ISimplifyCodeService style,
        IOptimizeUsingsService usings,
        IRemoveRegionsService regions,
        IReorderMembersService reorder,
        IFormatCodeService format,
        IRoslynWorkspace? workspace = null,
        IVisualMcpStore? store = null)
    {
        _style = style;
        _usings = usings;
        _regions = regions;
        _reorder = reorder;
        _format = format;
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> Cleanup(
        string scope,
        string? filePath,
        string? projectName,
        string? steps,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var level = (scope ?? "solution").Trim().ToLowerInvariant();
        if (level is not ("document" or "project" or "solution"))
            return new { error = "scope must be document, project or solution." };

        var requested = string.IsNullOrWhiteSpace(steps)
            ? Order.ToList()
            : steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(step => step.ToLowerInvariant())
                   .Distinct()
                   .ToList();

        var unknown = requested.Except(Order, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            return new
            {
                error = $"Unknown cleanup step(s): {string.Join(", ", unknown)}.",
                supported = Order,
            };

        var ordered = Order.Where(requested.Contains).ToList();
        var path = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);

        var results = new List<object>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countedFiles = 0;

        foreach (var step in ordered)
        {
            object result = step switch
            {
                "style" => await StyleAsync(level, path, projectName, dryRun, cancellationToken),
                "usings" => await _usings.OptimizeUsings(projectName, path, sort: true, dryRun),
                "regions" => await _regions.RemoveRegions(projectName, path, dryRun),
                "reorder" => await _reorder.ReorderMembers(projectName, path, dryRun),
                _ => await _format.Format(level, path, projectName, dryRun, cancellationToken),
            };

            var element = JsonSerializer.SerializeToElement(result);
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var error))
                return new
                {
                    error = $"{step}: {error.GetString()}",
                    completed = results,
                    note = "Cleanup stopped at the first failing step; earlier steps have already run.",
                };

            var stepFiles = FilesOf(element).ToList();
            countedFiles += CountOf(element, "filesModified") + CountOf(element, "filesChanged");
            foreach (var file in stepFiles) files.Add(file);

            results.Add(new
            {
                step,
                filesModified = CountOf(element, "filesModified") + CountOf(element, "filesChanged"),
                result,
            });
        }

        return new
        {
            tool = "code_cleanup",
            scope = level,
            steps = ordered,
            dryRun,
            applied = !dryRun,
            filesModified = files.Count > 0 ? files.Count : countedFiles,
            files = files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList(),
            results,
            note = dryRun
                ? "Preview only: each step reported what it would change; pass dryRun=false to write."
                : "Steps ran in order (rewrite → usings → regions → reorder → format) and the files were written. " +
                  "Style, usings, regions and format cover C# and Visual Basic; reorder applies to C# declarations.",
        };
    }

    /// <summary>
    /// The style step in both languages: each engine reads only the documents of its own language, so
    /// one call over a mixed solution rewrites both. The report keeps the two halves apart.
    /// </summary>
    private async Task<object> StyleAsync(
        string level, string? path, string? projectName, bool dryRun, CancellationToken cancellationToken)
    {
        var csharp = await _style.SimplifyAsync(level, path, projectName, rules: null, dryRun, cancellationToken);
        if (_workspace is null) return csharp;

        var visualBasic = await new VbSimplifyCodeService(_workspace, _store)
            .SimplifyAsync(level, path, projectName, rules: null, dryRun, cancellationToken);

        var csharpJson = JsonSerializer.SerializeToElement(csharp);
        var visualBasicJson = JsonSerializer.SerializeToElement(visualBasic);

        if (ErrorOf(csharpJson) is { } csharpError)
            return new { error = csharpError, csharp, visualBasic };

        if (ErrorOf(visualBasicJson) is { } visualBasicError)
            return new { error = visualBasicError, csharp, visualBasic };

        return new
        {
            csharp,
            visualBasic,
            filesChanged = CountOf(csharpJson, "filesChanged") + CountOf(visualBasicJson, "filesChanged"),
            files = FilesOf(csharpJson).Concat(FilesOf(visualBasicJson))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static string? ErrorOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("error", out var error) &&
        error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;

    /// <summary>The file count a step reported under one of its names, when it reports one.</summary>
    private static int CountOf(JsonElement result, string property) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty(property, out var count) &&
        count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;

    /// <summary>The paths a step reported, whichever shape it used (<c>files</c> of strings or objects).</summary>
    private static IEnumerable<string> FilesOf(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in files.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { Length: > 0 } path) yield return path;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in new[] { "file", "File", "filePath", "FilePath" })
            {
                if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;
                if (value.GetString() is { Length: > 0 } path) yield return path;
                break;
            }
        }
    }
}
