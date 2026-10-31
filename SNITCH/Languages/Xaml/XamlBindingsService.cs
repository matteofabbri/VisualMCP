using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>
/// The binding inspector: every binding a XAML file declares, what it binds, which type its path is
/// checked against and whether every step of the path exists. This is the static half of a binding
/// inspector — there is no running application and no live DataContext, so a path is checked against
/// the type the markup names, not against an object.
/// </summary>
public interface IXamlBindingsService
{
    Task<object> ListAsync(string? projectName, string? path, bool validate, int maxBindings,
        CancellationToken cancellationToken = default);
}

public sealed class XamlBindingsService : IXamlBindingsService
{
    private readonly IRoslynWorkspace _workspace;
    public XamlBindingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ListAsync(string? projectName = null, string? path = null, bool validate = true,
        int maxBindings = 500, CancellationToken cancellationToken = default)
    {
        maxBindings = XamlAnalysis.Clamp(maxBindings, 500, 5000);

        var collection = await XamlAnalysis.CollectAsync(_workspace, projectName, path, 5000);
        if (collection.Error is not null) return new { error = collection.Error };

        var solution = validate ? await _workspace.EnsureSolutionLoadedAsync(cancellationToken) : null;
        var resolvers = new Dictionary<string, XamlTypeResolver?>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();
        var byKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        var total = 0;

        foreach (var file in collection.Files)
        {
            var document = XamlAnalysis.TryRead(file, collection, []);
            if (document is null) continue;
            fileCount++;

            var facts = collection.FactsFor(file);

            XamlTypeResolver? resolver = null;
            if (validate && solution is not null)
            {
                var key = file.Project ?? string.Empty;
                if (!resolvers.TryGetValue(key, out resolver))
                {
                    resolver = XamlTypeResolver.Create(solution, file.Project, facts);
                    resolvers[key] = resolver;
                }
            }

            var validation = await XamlBindingValidator.ValidateAsync(document, resolver, facts.Dialect);

            foreach (var check in validation.Bindings)
            {
                total++;
                Increment(byKind, check.Binding.Kind);
                Increment(byStatus, check.Status);

                if (rows.Count >= maxBindings) continue;

                rows.Add(new
                {
                    file = file.FilePath,
                    relativePath = file.RelativePath,
                    project = file.Project,
                    dialect = facts.DialectName,
                    line = check.Binding.Line,
                    column = check.Binding.Column,
                    element = check.Binding.Element,
                    elementName = check.Binding.ElementName,
                    property = check.Binding.Property,
                    kind = check.Binding.Kind,
                    path = check.Binding.PathText,
                    mode = check.Binding.Mode,
                    source = SourceOf(check.Binding),
                    dataType = check.DataType,
                    dataTypeSource = check.DataTypeSource,
                    status = check.Status,
                    missingMember = check.MissingMember,
                    message = check.Message,
                    steps = check.Steps.Select(step => new
                    {
                        name = step.Name,
                        type = step.TypeName,
                        found = step.Found,
                    }).ToList(),
                });
            }
        }

        return new
        {
            root = collection.Root,
            projectFilter = projectName ?? "all",
            pathFilter = path,
            validated = validate && solution is not null,
            fileCount,
            bindingCount = total,
            returned = rows.Count,
            truncated = total > rows.Count,
            byKind,
            byStatus,
            invalid = byStatus.GetValueOrDefault("missing-member"),
            notChecked = byStatus.GetValueOrDefault("not-checked"),
            projects = collection.Projects.Select(facts => facts.ToJson()).ToList(),
            bindings = rows,
            notes = new[]
            {
                "The data type comes from x:DataType/DataType in scope, or from a DataContext the code-behind " +
                "assigns (DataContext = new Foo(), BindingContext in MAUI). A binding that names an ElementName, " +
                "a RelativeSource or an explicit Source reads another object and is reported as not-checked.",
                validate && solution is null
                    ? "No solution is loaded, so nothing was type-checked: the bindings are listed with their kinds and paths only."
                    : "A path segment is checked only while its type is known; the first unknown step stops the check.",
            },
        };
    }

    private static string? SourceOf(XamlBinding binding) =>
        binding.SourceElementName is { } elementName
            ? $"ElementName={elementName}"
            : binding.RelativeSource is { } relative
                ? $"RelativeSource={relative}"
                : binding.SourceType;

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var value) ? value + 1 : 1;
}
