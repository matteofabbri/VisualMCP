using System.Text;
using System.Xml.Linq;
using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>
/// The element tree a XAML file declares: every element, its x:Name, the attributes it sets and its
/// children, as data and as an indented text tree. It is the static half of a visual tree — the markup
/// the framework will instantiate — not a rendered preview: nothing is measured, laid out or drawn.
/// </summary>
public interface IXamlTreeService
{
    Task<object> TreeAsync(string path, string? projectName, int maxDepth, CancellationToken cancellationToken = default);
}

public sealed class XamlTreeService : IXamlTreeService
{
    private readonly IRoslynWorkspace _workspace;
    public XamlTreeService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> TreeAsync(string path, string? projectName = null, int maxDepth = 12,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new { error = "A path is required: point it at one .xaml/.axaml file." };

        maxDepth = Math.Clamp(maxDepth, 1, 64);

        var collection = await XamlAnalysis.CollectAsync(_workspace, projectName, path, 2);
        if (collection.Error is not null) return new { error = collection.Error };
        if (collection.Files.Count != 1)
            return new
            {
                error = $"'{path}' resolves to {collection.Files.Count} XAML files; point at one file.",
                files = collection.Files.Take(10).Select(file => file.RelativePath).ToList(),
            };

        var file = collection.Files[0];
        var document = XamlAnalysis.TryRead(file, collection, []);
        if (document is null)
            return new { error = $"The file could not be read or is not well-formed XML: {file.FilePath}" };

        var facts = collection.FactsFor(file);
        var text = new StringBuilder();
        var counts = new TreeCounts();

        var root = document.Xml.Root is null ? null : Build(document.Xml.Root, 0, maxDepth, text, counts);
        var codeBehind = document.ClassName is null
            ? null
            : await XamlAnalysis.ResolveTypeAsync(await _workspace.EnsureSolutionLoadedAsync(cancellationToken),
                file.Project, document.ClassName);

        return new
        {
            file = file.FilePath,
            relativePath = file.RelativePath,
            project = file.Project,
            dialect = facts.DialectName,
            dialectSignals = facts.Signals.Count > 0 ? facts.Signals : null,
            targetFrameworks = facts.TargetFrameworks.Count > 0 ? facts.TargetFrameworks : null,
            rootElement = document.RootName,
            className = document.ClassName,
            codeBehind = codeBehind is null
                ? null
                : new { fullName = codeBehind.FullName, file = codeBehind.FilePath, line = codeBehind.Line },
            counts = new
            {
                elements = counts.Elements,
                named = counts.Named,
                attributes = counts.Attributes,
                bindings = document.Bindings.Count,
                keys = document.Keys.Count,
                maxDepth = counts.Depth,
            },
            truncated = counts.Truncated || maxDepth < counts.Depth,
            tree = root,
            text = text.ToString().TrimEnd(),
            notes = new[]
            {
                "The element tree the markup declares. It is the static half of a visual tree: no layout, " +
                "no styles applied, no templates expanded — a DataTemplate's content appears under the " +
                "template that declares it, not under the control that would use it.",
            },
        };
    }

    private static object Build(XElement element, int depth, int maxDepth, StringBuilder text, TreeCounts counts)
    {
        counts.Elements++;
        counts.Depth = Math.Max(counts.Depth, depth);

        var name = AttributeInXamlNamespace(element, "Name");
        if (name is not null) counts.Named++;

        var attributes = element.Attributes().ToList();
        counts.Attributes += attributes.Count;

        var indent = new string(' ', depth * 2);

        // An XName carries the namespace, not the prefix: the prefix is whatever this element declares
        // for that namespace, and asking the element is the only way to read it back.
        var prefix = string.IsNullOrEmpty(element.Name.NamespaceName)
            ? string.Empty
            : element.GetPrefixOfNamespace(element.Name.Namespace) ?? string.Empty;

        text.Append(indent).Append(element.Name.LocalName);
        if (prefix.Length > 0)
            text.Append(" (").Append(prefix).Append(')');
        if (name is not null) text.Append(" x:Name=").Append(name);
        text.Append("  ·  line ").Append(XamlAnalysis.LineOf(element));
        text.AppendLine();

        var children = new List<object>();
        var truncated = false;

        foreach (var child in element.Elements())
        {
            if (depth + 1 > maxDepth)
            {
                truncated = true;
                break;
            }

            children.Add(Build(child, depth + 1, maxDepth, text, counts));
        }

        if (truncated) counts.Truncated = true;

        return new
        {
            element = element.Name.LocalName,
            namespacePrefix = prefix.Length > 0 ? prefix : null,
            name,
            line = XamlAnalysis.LineOf(element),
            attributes = attributes
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute => new { name = attribute.Name.LocalName, value = attribute.Value })
                .ToList(),
            children = children.Count > 0 ? children : null,
            childCount = element.Elements().Count(),
            truncated = truncated ? true : (bool?)null,
        };
    }

    private static string? AttributeInXamlNamespace(XElement element, string localName) =>
        element.Attributes()
            .Where(attribute => attribute.Name.LocalName == localName &&
                                attribute.Name.NamespaceName.Contains("xaml", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => attribute.Value)
            .FirstOrDefault();

    private sealed class TreeCounts
    {
        public int Elements { get; set; }
        public int Named { get; set; }
        public int Attributes { get; set; }
        public int Depth { get; set; }
        public bool Truncated { get; set; }
    }
}
