using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Platform;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.Xaml;

/// <summary>Rename one x:Name (markup + code-behind) or one resource key (every file that uses it).</summary>
public sealed record XamlRenameRequest
{
    public required string Path { get; init; }
    public required string OldName { get; init; }
    public required string NewName { get; init; }

    /// <summary><c>auto</c> (default), <c>name</c> or <c>resource</c>.</summary>
    public string Kind { get; init; } = "auto";
    public string? ProjectName { get; init; }
    public bool DryRun { get; init; } = true;
}

/// <summary>Move an element's attributes into a keyed Style and reference it.</summary>
public sealed record XamlExtractStyleRequest
{
    public required string Path { get; init; }

    /// <summary>The line the element's start tag begins on.</summary>
    public int Line { get; init; }
    public required string Key { get; init; }
    public string? TargetType { get; init; }
    public bool DryRun { get; init; } = true;
}

/// <summary>Promote one literal attribute value into a resource and reference it.</summary>
public sealed record XamlPromoteRequest
{
    public required string Path { get; init; }
    public int Line { get; init; }
    public required string Attribute { get; init; }
    public required string Key { get; init; }

    /// <summary><c>auto</c> (infer from the attribute), or <c>double</c>, <c>string</c>, <c>color</c>, <c>brush</c>.</summary>
    public string Kind { get; init; } = "auto";

    /// <summary><c>static</c> (default) or <c>dynamic</c>.</summary>
    public string ResourceKind { get; init; } = "static";
    public bool DryRun { get; init; } = true;
}

/// <summary>Normalise the indentation of a XAML file without touching anything else.</summary>
public sealed record XamlFormatRequest
{
    public required string Path { get; init; }

    /// <summary><c>auto</c> (detect the file's own unit), or the number of spaces.</summary>
    public string Indent { get; init; } = "auto";
    public bool DryRun { get; init; } = true;
}

/// <summary>
/// The editing side of the XAML layer, the counterpart of <see cref="IXamlPagesService"/> and
/// <see cref="IAnalyzeXamlService"/> which only read. Every operation is a set of verified text
/// splices: the file must still parse as XML afterwards, an x:Key/x:Name may not be duplicated by the
/// edit, a C# file must not gain syntax errors — otherwise nothing is written. Each write is recorded
/// in the solution's edit history and is revertible with <c>revert_edit</c>.
/// </summary>
public interface IXamlEditService
{
    Task<object> RenameAsync(XamlRenameRequest request, CancellationToken cancellationToken = default);
    Task<object> ExtractStyleAsync(XamlExtractStyleRequest request, CancellationToken cancellationToken = default);
    Task<object> PromoteResourceAsync(XamlPromoteRequest request, CancellationToken cancellationToken = default);
    Task<object> FormatAsync(XamlFormatRequest request, CancellationToken cancellationToken = default);
}

public sealed class XamlEditService : IXamlEditService
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, (string Kind, string? Dialect)> PromotableAttributes = new(StringComparer.Ordinal)
    {
        ["Width"] = ("double", null), ["Height"] = ("double", null),
        ["MinWidth"] = ("double", null), ["MaxWidth"] = ("double", null),
        ["MinHeight"] = ("double", null), ["MaxHeight"] = ("double", null),
        ["FontSize"] = ("double", null), ["Opacity"] = ("double", null),
        ["StrokeThickness"] = ("double", null), ["Spacing"] = ("double", null),
        ["CornerRadius"] = ("string", null), ["Padding"] = ("string", null), ["Margin"] = ("string", null),
        ["Foreground"] = ("brush", null), ["Background"] = ("brush", null),
        ["BorderBrush"] = ("brush", null), ["Fill"] = ("brush", null), ["Stroke"] = ("brush", null),
        ["Color"] = ("color", null), ["TextColor"] = ("color", null),
        ["Text"] = ("string", null), ["Content"] = ("string", null), ["Header"] = ("string", null),
        ["Title"] = ("string", null), ["Placeholder"] = ("string", null), ["PlaceholderText"] = ("string", null),
        ["ToolTip"] = ("string", null),
    };

    private readonly IVisualMcpStore? _store;
    private readonly IRoslynWorkspace? _workspace;

    /// <summary>The store records the write; the workspace is what the C# half of a rename needs.</summary>
    public XamlEditService(IVisualMcpStore? store = null, IRoslynWorkspace? workspace = null)
    {
        _store = store;
        _workspace = workspace;
    }

    // ── rename ──────────────────────────────────────────────────────────────
    public async Task<object> RenameAsync(XamlRenameRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFile(request.Path, out var path, out var problem))
            return new { error = problem![0] };

        if (string.IsNullOrWhiteSpace(request.OldName) || string.IsNullOrWhiteSpace(request.NewName))
            return new { error = "Both oldName and newName are required." };

        var oldName = request.OldName.Trim();
        var newName = request.NewName.Trim();
        if (!Identifier.IsMatch(newName))
            return new { error = $"'{newName}' is not a valid XAML name: use letters, digits and underscores, starting with a letter or an underscore." };

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var tags = XamlTextIndex.Scan(text);
        var kind = (request.Kind ?? "auto").Trim().ToLowerInvariant();
        if (kind is not ("auto" or "name" or "resource"))
            return new { error = "kind must be auto, name or resource." };

        var declaresName = tags.Any(tag => Attributes(text, tags, tag, "Name", oldName).Count > 0);
        var declaresKey = tags.Any(tag => Attributes(text, tags, tag, "Key", oldName).Count > 0);

        if (kind == "auto") kind = declaresName ? "name" : declaresKey ? "resource" : "unknown";
        if (kind == "unknown")
            return new
            {
                error = $"No x:Name or x:Key '{oldName}' is declared in {Path.GetFileName(path)}.",
                names = Declared(text, tags, "Name"),
                keys = Declared(text, tags, "Key"),
            };

        if (kind == "name" ? !declaresName : !declaresKey)
            return new { error = $"'{oldName}' is not declared as an x:{(kind == "name" ? "Name" : "Key")} in {Path.GetFileName(path)}." };

        if (kind == "name" && Declared(text, tags, "Name").Contains(newName, StringComparer.Ordinal))
            return new { error = $"x:Name '{newName}' already exists in this file." };

        // Collect the whole project (or, without one, the file's own folder) so a resource key can be
        // followed to the other files that reference it.
        var collection = await CollectAsync(request.ProjectName, request.ProjectName is not null ? null : Path.GetDirectoryName(path));
        var projectFiles = collection.Files
            .Where(file => !string.Equals(file.FilePath, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var plans = new List<PlannedFile>();
        var notes = new List<string>();
        var skipped = new List<string>();

        if (kind == "name")
        {
            // An x:Name namescope is the file: the declaration and every reference to it are here.
            var splices = NameSplices(text, tags, oldName, newName, skipped);
            if (splices.Count == 0)
                return new { error = $"No declaration of x:Name '{oldName}' was found to rewrite." };

            var after = Apply(text, splices);
            if (ParseProblem(after) is { } parseProblem)
                return new { error = $"Refused: the rename would produce invalid XML ({parseProblem}). Nothing was written." };

            plans.Add(new PlannedFile(path, text, after, splices.Count));

            var otherFiles = new List<string>();
            foreach (var file in projectFiles)
            {
                var other = await File.ReadAllTextAsync(file.FilePath, cancellationToken);
                var otherTags = XamlTextIndex.Scan(other);
                if (Declared(other, otherTags, "Name").Contains(oldName, StringComparer.Ordinal))
                    otherFiles.Add(file.RelativePath);
            }

            if (otherFiles.Count > 0)
                notes.Add($"x:Name '{oldName}' is also declared in {string.Join(", ", otherFiles)}; those are separate namescopes and were left alone.");

            var codeBehind = await RenameInCodeBehindAsync(path, request.ProjectName, oldName, newName, renameStringLiterals: false, skipped, cancellationToken);
            plans.AddRange(codeBehind);
        }
        else
        {
            var keySplices = KeySplices(text, tags, oldName, newName);
            var after = Apply(text, keySplices);
            if (ParseProblem(after) is { } parseProblem)
                return new { error = $"Refused: the rename would produce invalid XML ({parseProblem}). Nothing was written." };

            plans.Add(new PlannedFile(path, text, after, keySplices.Count));

            foreach (var file in projectFiles)
            {
                var other = await File.ReadAllTextAsync(file.FilePath, cancellationToken);
                var otherTags = XamlTextIndex.Scan(other);
                var splices = KeySplices(other, otherTags, oldName, newName);
                if (splices.Count == 0) continue;

                var updated = Apply(other, splices);
                if (ParseProblem(updated) is not null) continue;

                plans.Add(new PlannedFile(file.FilePath, other, updated, splices.Count));
            }

            var codeBehind = await RenameInCodeBehindAsync(path, request.ProjectName, oldName, newName, renameStringLiterals: true, skipped, cancellationToken);
            plans.AddRange(codeBehind);
            notes.Add("Resource keys are matched across the project's XAML files; a key reached through a binding written in code " +
                      "with a computed string cannot be found.");
        }

        if (plans.All(plan => plan.Changed == false))
            return new { file = path, kind, changed = false, applied = false, note = "The files already hold the new name." };

        var diff = plans
            .Where(plan => plan.Before != plan.After)
            .Select(plan => new
            {
                file = plan.File,
                replacements = plan.Replacements,
                lineDiff = TextDiff.Lines(plan.Before, plan.After),
            })
            .ToList();

        if (request.DryRun)
            return new
            {
                file = path,
                kind,
                oldName,
                newName,
                dryRun = true,
                applied = false,
                filesChanged = diff.Count,
                files = diff,
                skipped,
                notes,
                note = "Preview only: pass dryRun=false to write the files.",
            };

        foreach (var plan in plans.Where(plan => plan.Before != plan.After))
        {
            await FileText.WriteAsync(plan.File, plan.After, cancellationToken);
            Record(kind == "name" ? "xaml_rename(x:Name)" : "xaml_rename(x:Key)", plan.File, plan.Before, plan.After);
        }

        return new
        {
            file = path,
            kind,
            oldName,
            newName,
            applied = true,
            filesChanged = diff.Count,
            files = diff,
            skipped,
            notes,
            history = _store is null ? "unavailable (no solution store)" : "recorded (revert with revert_edit)",
        };
    }

    /// <summary>Every place a name is written: the declaration, and the references that resolve it.</summary>
    private static List<Splice> NameSplices(string text, List<XamlTextTag> tags, string oldName, string newName,
        List<string> skipped)
    {
        var splices = new List<Splice>();

        foreach (var tag in tags)
        {
            if (tag.IsClosing) continue;

            foreach (var attribute in XamlTextIndex.Attributes(text, tag))
            {
                var value = attribute.Value(text);

                if (IsXamlAttribute(attribute.Name, "Name"))
                {
                    if (value == oldName) splices.Add(new Splice(attribute.ValueStart, value.Length, newName));
                    continue;
                }

                if (attribute.Name is "ElementName" or "TargetName")
                {
                    if (value == oldName) splices.Add(new Splice(attribute.ValueStart, value.Length, newName));
                    continue;
                }

                if (!XamlMarkup.TryParse(value, out var extension)) continue;

                if (extension.Type == "x:Reference")
                {
                    foreach (var span in NameSpansInExtension(value, "x:Reference", oldName))
                        splices.Add(new Splice(attribute.ValueStart + span.Start, span.Length, newName));
                    continue;
                }

                if (extension.IsBinding && extension.Get("ElementName") == oldName)
                {
                    foreach (Match match in Regex.Matches(value,
                                 @"(?<=ElementName=)\s*(['""]?)(" + Regex.Escape(oldName) + @")\1(?![\w.])"))
                    {
                        var group = match.Groups[2];
                        splices.Add(new Splice(attribute.ValueStart + group.Index, group.Length, newName));
                    }
                }
            }
        }

        return splices;
    }

    /// <summary>The spans of the referenced name inside an <c>{x:Reference Name}</c> value.</summary>
    private static List<(int Start, int Length)> NameSpansInExtension(string value, string type, string name)
    {
        var spans = new List<(int Start, int Length)>();
        var pattern = @"(?<=\{" + Regex.Escape(type) + @"\s+(?:Name=)?)(['""]?)(" + Regex.Escape(name) + @")\1(?![\w.])";

        foreach (Match match in Regex.Matches(value, pattern))
        {
            var group = match.Groups[2];
            spans.Add((group.Index, group.Length));
        }

        return spans;
    }

    /// <summary>Every place a resource key is written: definitions, StaticResource/DynamicResource uses and BasedOn.</summary>
    private static List<Splice> KeySplices(string text, List<XamlTextTag> tags, string oldName, string newName)
    {
        var splices = new List<Splice>();

        foreach (var tag in tags)
        {
            if (tag.IsClosing) continue;

            foreach (var attribute in XamlTextIndex.Attributes(text, tag))
            {
                var value = attribute.Value(text);

                if (IsXamlAttribute(attribute.Name, "Key"))
                {
                    if (value == oldName) splices.Add(new Splice(attribute.ValueStart, value.Length, newName));
                    continue;
                }

                foreach (Match match in Regex.Matches(value, @"\{(?:StaticResource|DynamicResource|ThemeResource)\s+(?:ResourceKey=)?([A-Za-z0-9_.]+)\s*\}"))
                {
                    if (!string.Equals(match.Groups[1].Value, oldName, StringComparison.Ordinal)) continue;

                    var keyGroup = match.Groups[1];
                    splices.Add(new Splice(attribute.ValueStart + keyGroup.Index, keyGroup.Length, newName));
                }
            }
        }

        return splices;
    }

    private static bool IsXamlAttribute(string name, string local) =>
        name.Equals("x:" + local, StringComparison.Ordinal);

    private static HashSet<string> Declared(string text, List<XamlTextTag> tags, string local)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (tag.IsClosing) continue;
            foreach (var attribute in XamlTextIndex.Attributes(text, tag))
                if (IsXamlAttribute(attribute.Name, local))
                    declared.Add(attribute.Value(text));
        }

        return declared;
    }

    private static List<XamlTextAttribute> Attributes(string text, List<XamlTextTag> tags, XamlTextTag tag,
        string local, string value) =>
        tag.IsClosing
            ? []
            : XamlTextIndex.Attributes(text, tag)
                .Where(attribute => IsXamlAttribute(attribute.Name, local) && attribute.Value(text) == value)
                .ToList();

    /// <summary>
    /// The C# half: identifiers the code-behind uses that resolve to nothing are the generated fields
    /// x:Name produces, so they are renamed. An identifier that resolves to a real symbol is somebody
    /// else's and is reported as skipped instead of being renamed.
    /// </summary>
    private async Task<List<PlannedFile>> RenameInCodeBehindAsync(string xamlPath, string? projectName,
        string oldName, string newName, bool renameStringLiterals, List<string> skipped, CancellationToken cancellationToken)
    {
        var plans = new List<PlannedFile>();
        if (_workspace is null) return plans;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null) return plans;

        var document = XamlAnalysis.TryRead(new XamlFileEntry(xamlPath, Path.GetFileName(xamlPath), projectName),
            new XamlCollection(), []);
        if (document?.ClassName is null) return plans;

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation?.GetTypeByMetadataName(document.ClassName) is not INamedTypeSymbol type) continue;

            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var tree = reference.SyntaxTree;
                var model = compilation.GetSemanticModel(tree);
                var source = await tree.GetTextAsync(cancellationToken);
                var text = source.ToString();
                var splices = new List<Splice>();

                foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.Text != oldName) continue;

                    var symbol = model.GetSymbolInfo(identifier).Symbol;
                    if (symbol is not null)
                    {
                        skipped.Add($"{Path.GetFileName(tree.FilePath)}:{identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: " +
                                    $"'{oldName}' resolves to {symbol.Kind} '{symbol.Name}' and was left alone");
                        continue;
                    }

                    splices.Add(new Splice(identifier.Identifier.SpanStart, identifier.Identifier.Span.Length, newName));
                }

                if (renameStringLiterals)
                {
                    foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>())
                    {
                        if (!literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)) continue;
                        if (literal.Token.ValueText != oldName) continue;

                        // Only the quotes' content: "Key" → "New".
                        var start = literal.Token.SpanStart + 1;
                        splices.Add(new Splice(start, literal.Token.Span.Length - 2, newName));
                    }
                }

                if (splices.Count == 0) continue;

                var after = Apply(text, splices);
                var before = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text);
                var updated = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(after);
                if (updated.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) >
                    before.GetDiagnostics().Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    skipped.Add($"{Path.GetFileName(tree.FilePath)}: the rename would add C# syntax errors, so that file was left alone");
                    continue;
                }

                plans.Add(new PlannedFile(tree.FilePath, text, after, splices.Count));
            }
        }

        return plans;
    }

    // ── extract a style ─────────────────────────────────────────────────────
    public async Task<object> ExtractStyleAsync(XamlExtractStyleRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFile(request.Path, out var path, out var problem))
            return new { error = problem![0] };

        if (string.IsNullOrWhiteSpace(request.Key) || !Identifier.IsMatch(request.Key))
            return new { error = $"'{request.Key}' is not a valid resource key: use letters, digits and underscores." };

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var xml = TryParse(text, out var parseProblem);
        if (xml is null) return new { error = $"The file is not well-formed XML: {parseProblem}" };

        if (request.Line <= 0)
            return new { error = "A line is required: the line the element's start tag begins on (xaml_tree gives the lines)." };

        var element = ElementAt(xml, request.Line);
        if (element is null)
            return new
            {
                error = $"No element starts on line {request.Line}.",
                elements = xml.Descendants().Take(20).Select(candidate => new
                {
                    line = XamlAnalysis.LineOf(candidate),
                    element = candidate.Name.LocalName,
                }).ToList(),
            };

        var tags = XamlTextIndex.Scan(text);
        var tag = LocateTag(text, tags, element);
        if (tag is null)
            return new
            {
                error = "The element's start tag could not be located in the text.",
                element = element.Name.LocalName,
                line = XamlAnalysis.LineOf(element),
                column = XamlAnalysis.ColumnOf(element),
                tags = tags.Take(8).Select(candidate => new { candidate.Name, candidate.Start, candidate.End }).ToList(),
            };

        if (XamlTextIndex.Attributes(text, tag).Any(attribute => attribute.Name == "Style"))
            return new { error = "The element already sets Style; merging two styles is not something this tool invents." };

        var targetType = string.IsNullOrWhiteSpace(request.TargetType) ? element.Name.LocalName : request.TargetType.Trim();

        var setters = new List<XamlTextAttribute>();
        foreach (var attribute in XamlTextIndex.Attributes(text, tag))
        {
            if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
            if (attribute.Name.StartsWith("x:", StringComparison.Ordinal) || attribute.Name.StartsWith("d:", StringComparison.Ordinal)) continue;
            if (attribute.Name is "Name" or "Class" or "Style") continue;
            if (attribute.Quote == '\0') continue; // a valueless attribute has no value to put in a Setter
            if (XamlAnalysis.IsEventHandler(attribute.Name, attribute.Value(text))) continue;

            setters.Add(attribute);
        }

        if (setters.Count == 0)
            return new
            {
                error = "The element has no attribute a Setter could take: only x:*, Name, Style and event handlers were found.",
                element = element.Name.LocalName,
                line = XamlAnalysis.LineOf(element),
            };

        if (Declared(text, tags, "Key").Contains(request.Key, StringComparer.Ordinal))
            return new { error = $"x:Key '{request.Key}' already exists in this file." };

        var newLine = XamlTextIndex.NewLine(text);
        var unit = XamlTextIndex.IndentUnit(text);
        var resources = FindResourcesHost(text, tags, xml, element, out var resourcesProblem);
        if (resources is null)
            return new { error = resourcesProblem ?? "No place to put the resource was found in this file." };

        var styleIndent = resources.StyleIndent;
        var setterIndent = styleIndent + unit;

        var block = new StringBuilder();
        block.Append(resources.Prefix);
        block.Append("<Style x:Key=\"").Append(request.Key).Append("\" TargetType=\"").Append(targetType).Append("\">").Append(newLine);
        foreach (var setter in setters)
            block.Append(setterIndent).Append("<Setter Property=\"").Append(setter.Name)
                .Append("\" Value=\"").Append(setter.Value(text)).Append("\" />").Append(newLine);
        block.Append(styleIndent).Append("</Style>");
        block.Append(resources.Suffix);

        var splices = new List<Splice>
        {
            new(resources.InsertOffset, 0, block.ToString()),
            new(tag.AttributeInsertionOffset, 0, " Style=\"{StaticResource " + request.Key + "}\""),
        };

        var after = Apply(text, splices);
        if (TryParse(after, out var problem2) is null)
            return new
            {
                error = $"Refused: the edit would produce invalid XML ({problem2}). Nothing was written.",
                preview = Preview(after),
            };

        return await WriteAsync("xaml_extract_style", path, text, after, request.DryRun, cancellationToken, new
        {
            element = element.Name.LocalName,
            line = XamlAnalysis.LineOf(element),
            key = request.Key,
            targetType,
            setters = setters.Select(setter => new { property = setter.Name, value = setter.Value(text) }).ToList(),
            resources = resources.Description,
        });
    }

    // ── promote a literal to a resource ─────────────────────────────────────
    public async Task<object> PromoteResourceAsync(XamlPromoteRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFile(request.Path, out var path, out var problem))
            return new { error = problem![0] };

        if (string.IsNullOrWhiteSpace(request.Key) || !Identifier.IsMatch(request.Key))
            return new { error = $"'{request.Key}' is not a valid resource key: use letters, digits and underscores." };

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var xml = TryParse(text, out var parseProblem);
        if (xml is null) return new { error = $"The file is not well-formed XML: {parseProblem}" };

        var element = ElementAt(xml, request.Line);
        if (element is null)
            return new { error = $"No element starts on line {request.Line}.", hint = "xaml_tree reports the line of every element." };

        var tags = XamlTextIndex.Scan(text);
        var tag = LocateTag(text, tags, element);
        if (tag is null) return new { error = "The element's start tag could not be located in the text." };

        var attribute = XamlTextIndex.Attributes(text, tag)
            .FirstOrDefault(candidate => candidate.Name.Equals(request.Attribute, StringComparison.Ordinal));
        if (attribute is null)
            return new
            {
                error = $"The element on line {request.Line} declares no '{request.Attribute}'.",
                attributes = XamlTextIndex.Attributes(text, tag).Select(candidate => candidate.Name).ToList(),
            };

        var value = attribute.Value(text);
        if (value.StartsWith('{'))
            return new { error = $"'{request.Attribute}' is already a markup extension ('{value}'), not a literal to promote." };
        if (value.Length == 0)
            return new { error = $"'{request.Attribute}' is empty: there is nothing to promote." };

        var kind = (request.Kind ?? "auto").Trim().ToLowerInvariant();
        if (kind == "auto")
        {
            if (!PromotableAttributes.TryGetValue(attribute.Name, out var known))
                return new
                {
                    error = $"The kind of '{attribute.Name}' is not known, so the resource element it would need is not either.",
                    hint = "Pass kind=double|string|color|brush explicitly.",
                    known = PromotableAttributes.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                };

            kind = known.Kind;
        }

        if (kind is not ("double" or "string" or "color" or "brush"))
            return new { error = "kind must be auto, double, string, color or brush." };

        if (Declared(text, tags, "Key").Contains(request.Key, StringComparer.Ordinal))
            return new { error = $"x:Key '{request.Key}' already exists in this file." };

        var collection = await CollectAsync(null, Path.GetDirectoryName(path));
        var facts = collection.Files
            .Where(file => string.Equals(file.FilePath, path, StringComparison.OrdinalIgnoreCase))
            .Select(collection.FactsFor)
            .FirstOrDefault();
        var dialect = facts?.Dialect ?? XamlDialect.Unknown;

        var resources = FindResourcesHost(text, tags, xml, element, out var resourcesProblem);
        if (resources is null)
            return new { error = resourcesProblem ?? "No place to put the resource was found in this file." };

        var resource = ResourceElement(request.Key, kind, value, dialect);
        var reference = request.ResourceKind.Equals("dynamic", StringComparison.OrdinalIgnoreCase)
            ? "{DynamicResource " + request.Key + "}"
            : "{StaticResource " + request.Key + "}";

        var splices = new List<Splice>
        {
            new(resources.InsertOffset, 0, resources.Prefix + resource + resources.Suffix),
            new(attribute.ValueStart, attribute.ValueLength, reference),
        };

        var namespacesAdded = new List<string>();
        if (resource.Contains("<sys:", StringComparison.Ordinal) &&
            !xml.Root!.Attributes().Any(candidate => candidate.IsNamespaceDeclaration &&
                                                     candidate.Value.Contains("clr-namespace:System", StringComparison.Ordinal)))
        {
            var rootTag = LocateTag(text, tags, xml.Root);
            if (rootTag is not null)
            {
                splices.Add(new Splice(rootTag.AttributeInsertionOffset, 0,
                    " xmlns:sys=\"clr-namespace:System;assembly=mscorlib\""));
                namespacesAdded.Add("sys");
            }
        }

        var after = Apply(text, splices);
        if (TryParse(after, out var problem2) is null)
            return new
            {
                error = $"Refused: the edit would produce invalid XML ({problem2}). Nothing was written.",
                preview = Preview(after),
            };

        return await WriteAsync("xaml_promote_resource", path, text, after, request.DryRun, cancellationToken, new
        {
            element = element.Name.LocalName,
            line = XamlAnalysis.LineOf(element),
            attribute = attribute.Name,
            literal = value,
            key = request.Key,
            kind,
            dialect = facts?.DialectName ?? "unknown",
            resource,
            reference,
            namespacesAdded = namespacesAdded.Count > 0 ? namespacesAdded : null,
            resources = resources.Description,
        });
    }

    /// <summary>The resource element in the dialect's own syntax: WPF needs <c>sys:</c>, the rest have x:.</summary>
    private static string ResourceElement(string key, string kind, string value, XamlDialect dialect)
    {
        var scalarPrefix = dialect is XamlDialect.Wpf or XamlDialect.Unknown ? "sys:" : "x:";

        return kind switch
        {
            "double" => $"<{scalarPrefix}Double x:Key=\"{key}\">{value}</{scalarPrefix}Double>",
            "string" => $"<{scalarPrefix}String x:Key=\"{key}\">{value}</{scalarPrefix}String>",
            "color" => $"<Color x:Key=\"{key}\">{value}</Color>",
            _ => $"<SolidColorBrush x:Key=\"{key}\" Color=\"{value}\" />",
        };
    }

    // ── format ──────────────────────────────────────────────────────────────
    public async Task<object> FormatAsync(XamlFormatRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryResolveFile(request.Path, out var path, out var problem))
            return new { error = problem![0] };

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var xml = TryParse(text, out var parseProblem);
        if (xml is null) return new { error = $"The file is not well-formed XML, so it cannot be formatted: {parseProblem}" };

        var unit = (request.Indent ?? "auto").Trim().ToLowerInvariant() switch
        {
            "auto" => XamlTextIndex.IndentUnit(text),
            "2" => "  ",
            "4" => "    ",
            "tab" or "tabs" => "\t",
            var other when int.TryParse(other, out var width) && width is > 0 and <= 12 => new string(' ', width),
            _ => null,
        };

        if (unit is null) return new { error = "indent must be auto, 2, 4, tab or a number of spaces (1-12)." };

        var tags = XamlTextIndex.Scan(text);

        // The depth of every tag, keyed on where the scanner found it: the element's ancestors give a
        // start tag its depth, and a close tag has the depth of the tag it closes.
        var elements = new List<XElement>();
        if (xml.Root is not null) elements.Add(xml.Root);
        elements.AddRange(xml.Descendants());

        var depthByTagStart = new Dictionary<int, int>();
        foreach (var element in elements)
        {
            var tag = LocateTag(text, tags, element);
            if (tag is null) continue;

            var depth = element.Ancestors().Count();
            depthByTagStart[tag.Start] = depth;

            var close = ClosingTag(tags, tag);
            if (close is not null) depthByTagStart[close.Start] = depth;
        }

        var lines = text.Split('\n');
        var offsets = new List<(int Offset, int Length)>();
        var running = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            offsets.Add((running, lines[index].Length));
            running += lines[index].Length + 1;
        }

        var builder = new StringBuilder(text.Length);
        var changedLines = 0;

        // Attributes written one per line are aligned under the first attribute, which is what an IDE
        // does and what keeps a long start tag readable.
        var continuation = new Dictionary<int, string>();
        foreach (var tag in tags.Where(candidate => !candidate.IsClosing))
        {
            var alignment = new string(' ', tag.Start + 1 + tag.Name.Length + 1);
            foreach (var (offset, _) in offsets)
                if (offset > tag.Start && offset <= tag.AttributeInsertionOffset)
                    continuation[offset] = alignment;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var content = raw.TrimEnd('\r');
            var trimmed = content.TrimEnd();
            var leading = trimmed.Length - trimmed.TrimStart().Length;
            var replacement = trimmed;

            if (continuation.TryGetValue(offsets[index].Offset, out var alignment))
                replacement = alignment + trimmed[leading..];
            else if (trimmed.Length > leading && trimmed[leading] == '<' &&
                     depthByTagStart.TryGetValue(offsets[index].Offset + leading, out var depth))
                replacement = Repeat(unit, depth) + trimmed[leading..];

            if (replacement != content) changedLines++;
            builder.Append(replacement);
            if (raw.EndsWith('\r')) builder.Append('\r');
            if (index < lines.Length - 1) builder.Append('\n');
        }

        var after = builder.ToString();
        if (string.Equals(after, text, StringComparison.Ordinal))
            return new { file = path, changed = false, applied = false, note = "The file is already indented with this style." };

        // The gate: indentation is whitespace between tags, so the document's shape must be identical.
        if (TryParse(after, out var problem2) is null ||
            !string.Equals(XamlTextIndex.Signature(text), XamlTextIndex.Signature(after), StringComparison.Ordinal))
            return new
            {
                error = $"Refused: the rewrite would change the document's structure, not only its indentation{(problem2 is null ? string.Empty : $" ({problem2})")}. Nothing was written.",
            };

        return await WriteAsync("format_xaml", path, text, after, request.DryRun, cancellationToken, new
        {
            indent = unit == "\t" ? "tab" : unit.Length.ToString(),
            linesChanged = changedLines,
        });
    }

    // ── shared plumbing ─────────────────────────────────────────────────────
    private sealed record Splice(int Offset, int Length, string Replacement);

    private sealed record PlannedFile(string File, string Before, string After, int Replacements)
    {
        public bool Changed => !string.Equals(Before, After, StringComparison.Ordinal);
    }

    private sealed record ResourcesHost(int InsertOffset, string StyleIndent, string Prefix, string Suffix, string Description);

    /// <summary>
    /// Where a new resource goes: inside the nearest <c>&lt;X.Resources&gt;</c> of the element's own tree,
    /// or a block created right after the root's start tag when the file has none. The prefix/suffix
    /// carry the wrapper and the indentation, so the caller only has to build the resource itself.
    /// </summary>
    private static ResourcesHost? FindResourcesHost(string text, List<XamlTextTag> tags, XDocument xml, XElement element,
        out string? problem)
    {
        problem = null;
        var newLine = XamlTextIndex.NewLine(text);
        var unit = XamlTextIndex.IndentUnit(text);

        var holder = element.AncestorsAndSelf()
            .Select(ancestor => ancestor.Elements()
                .FirstOrDefault(child => child.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
            .FirstOrDefault(resources => resources is not null);

        if (holder is not null)
        {
            var open = LocateTag(text, tags, holder);
            if (open is null) return null;

            var close = ClosingTag(tags, open);
            if (close is null) return null;

            var closeIndent = XamlTextIndex.IndentAt(text, close.Start);
            return new ResourcesHost(close.Start, closeIndent + unit, string.Empty, newLine + closeIndent,
                $"the {holder.Name.LocalName} block on line {XamlAnalysis.LineOf(holder)}");
        }

        var root = xml.Root;
        if (root is null) return null;

        var rootTag = LocateTag(text, tags, root);
        if (rootTag is null) return null;

        if (rootTag.IsSelfClosing)
        {
            problem = $"The root element is self-closing ('<{root.Name.LocalName} />'), so it has no content to hold a Resources block.";
            return null;
        }

        var rootIndent = XamlTextIndex.IndentAt(text, rootTag.Start);
        var resourcesIndent = rootIndent + unit;
        var wrapper = root.Name.LocalName;

        // After the start tag's '>': an element goes inside the element, not inside its tag.
        return new ResourcesHost(
            rootTag.End + 1,
            resourcesIndent + unit,
            newLine + resourcesIndent + "<" + wrapper + ".Resources>" + newLine + resourcesIndent + unit,
            newLine + resourcesIndent + "</" + wrapper + ".Resources>",
            $"a new {wrapper}.Resources block after the root element's start tag");
    }

    /// <summary>The close tag that matches a start tag, by name and nesting depth.</summary>
    private static XamlTextTag? ClosingTag(List<XamlTextTag> tags, XamlTextTag open)
    {
        if (open.IsSelfClosing) return null;

        var depth = 1;
        var index = tags.IndexOf(open);
        for (var candidate = index + 1; candidate < tags.Count; candidate++)
        {
            var tag = tags[candidate];
            if (!tag.Name.Equals(open.Name, StringComparison.Ordinal)) continue;

            if (tag.IsClosing)
            {
                depth--;
                if (depth == 0) return tag;
            }
            else if (!tag.IsSelfClosing)
            {
                depth++;
            }
        }

        return null;
    }

    private static string Repeat(string unit, int times)
    {
        if (times <= 0) return string.Empty;
        var builder = new StringBuilder(unit.Length * times);
        for (var index = 0; index < times; index++) builder.Append(unit);
        return builder.ToString();
    }

    private static XElement? ElementAt(XDocument xml, int line) => xml.Descendants()
        .Where(element => XamlAnalysis.LineOf(element) == line)
        .OrderBy(element => XamlAnalysis.ColumnOf(element))
        .FirstOrDefault();

    /// <summary>
    /// The tag span of an element. The exact offset comes from the XML reader's line info; a tag that
    /// starts elsewhere on that line (the reader and the scanner disagreeing by a column) is still
    /// found by name, so a file with an unusual declaration does not make every edit fail.
    /// </summary>
    private static XamlTextTag? LocateTag(string text, List<XamlTextTag> tags, XElement element)
    {
        var offset = XamlTextIndex.OffsetOf(text, XamlAnalysis.LineOf(element), XamlAnalysis.ColumnOf(element));
        var exact = XamlTextIndex.TagAt(tags, offset);
        if (exact is not null) return exact;

        var lineStart = offset - Math.Max(0, XamlAnalysis.ColumnOf(element) - 1);
        var lineEnd = text.IndexOf('\n', Math.Max(0, lineStart));
        if (lineEnd < 0) lineEnd = text.Length;

        return tags.FirstOrDefault(tag =>
            !tag.IsClosing &&
            tag.Name == element.Name.LocalName &&
            tag.Start >= lineStart && tag.Start <= lineEnd);
    }

    private static XDocument? TryParse(string text, out string? problem)
    {
        try
        {
            problem = null;
            return XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            problem = $"line {exception.LineNumber}: {exception.Message}";
            return null;
        }
    }

    private static string? ParseProblem(string text) => TryParse(text, out var problem) is null ? problem : null;

    /// <summary>The first lines of a refused edit, so the caller can see what it would have produced.</summary>
    private static string Preview(string text) =>
        string.Join("\n", text.Split('\n').Take(10)).TrimEnd();

    private static string Apply(string text, List<Splice> splices)
    {
        var builder = new StringBuilder(text);
        foreach (var splice in splices.OrderByDescending(candidate => candidate.Offset))
            builder.Remove(splice.Offset, splice.Length).Insert(splice.Offset, splice.Replacement);
        return builder.ToString();
    }

    /// <summary>The XAML files a call scans; without a workspace only the given path can be read.</summary>
    private async Task<XamlCollection> CollectAsync(string? projectName, string? path)
    {
        if (_workspace is null)
            return new XamlCollection { Error = null };

        return await XamlAnalysis.CollectAsync(_workspace, projectName, path, 5000);
    }

    private static bool TryResolveFile(string? path, out string fullPath, out string[]? problem)
    {
        fullPath = string.Empty;
        problem = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = ["A path is required: point it at one .xaml/.axaml file."];
            return false;
        }

        fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            problem = [$"File not found: {fullPath}"];
            return false;
        }

        if (!fullPath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) &&
            !fullPath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            problem = [$"'{fullPath}' is not a .xaml or .axaml file."];
            return false;
        }

        return true;
    }

    private async Task<object> WriteAsync(string tool, string path, string before, string after, bool dryRun,
        CancellationToken cancellationToken, object? details)
    {
        var diff = TextDiff.Lines(before, after);

        if (dryRun)
            return new
            {
                file = path,
                dryRun = true,
                applied = false,
                changed = true,
                details,
                diff,
                note = "Preview only: pass dryRun=false to write the file.",
            };

        await FileText.WriteAsync(path, after, cancellationToken);
        Record(tool, path, before, after);

        return new
        {
            file = path,
            applied = true,
            changed = true,
            details,
            diff,
            history = _store is null ? "unavailable (no solution store)" : "recorded (revert with revert_edit)",
        };
    }

    private void Record(string tool, string file, string before, string after)
    {
        if (_store is null) return;

        try { _store.RecordEdit(tool, file, null, before, after); }
        catch (Exception) { /* the history is an audit trail, not a precondition for the write */ }
    }
}
