namespace Snitch.Languages.Razor;

/// <summary>
/// What a Razor file is to the application. Inferred from the file name, the path and the
/// directives it declares — the extension alone cannot tell a layout from a partial.
/// </summary>
public enum RazorPageKind
{
    Page,
    View,
    Partial,
    Layout,
    Component,
    Imports,
    ViewStart,
    Other,
}

/// <summary>A Razor directive with its argument, e.g. <c>@model HomeViewModel</c>.</summary>
public sealed record RazorDirective(string Name, string Value, int Line, int Column, int Offset);

/// <summary>The argument of an <c>@inject</c> directive, split into type and property.</summary>
public sealed record RazorInjection(string ServiceType, string PropertyName);

/// <summary>
/// A C# expression written in markup: an implicit expression (<c>@Model.Name</c>), an explicit
/// one (<c>@(x + 1)</c>) or an <c>@await</c> expression.
/// </summary>
public sealed record RazorExpression(
    string Text,
    int Offset,
    int Line,
    int Column,
    bool InsideScript,
    bool IsExplicit);

/// <summary>
/// A contiguous C# block. <c>Kind</c> is <c>block</c> (<c>@{ … }</c>), <c>code</c>/<c>functions</c>
/// (the Blazor and MVC directives), <c>section</c> (<c>@section Name { … }</c> — its body is markup)
/// or <c>control</c> (<c>@if</c>, <c>@foreach</c>, … — only the header is C#; the body is markup).
/// </summary>
public sealed record RazorCodeBlock(
    string Kind,
    string? Name,
    string Text,
    string Header,
    int Offset,
    int EndOffset,
    int Line,
    int EndLine);

/// <summary>One attribute of a start tag, with the position of the attribute name.</summary>
public sealed record RazorAttribute(
    string Name,
    string? Value,
    bool HasValue,
    bool ValueHasRazor,
    int Line,
    int Column,
    int Offset);

/// <summary>A start tag and, when it has been closed, the elements nested inside it.</summary>
public sealed class RazorElement
{
    public required string Name { get; init; }
    public required int Offset { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }

    public bool SelfClosing { get; set; }
    public bool IsVoid { get; set; }
    public int EndOffset { get; set; }
    public int EndLine { get; set; }

    /// <summary>The raw text of a <c>script</c> or <c>style</c> element (never for other tags).</summary>
    public string? RawText { get; set; }

    public List<RazorAttribute> Attributes { get; } = [];
    public List<RazorElement> Children { get; } = [];
    public RazorElement? Parent { get; set; }

    public IEnumerable<RazorElement> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    public RazorAttribute? Attribute(string name) =>
        Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool HasAttribute(string name) => Attribute(name) is not null;

    public bool HasAttributePrefix(string prefix) =>
        Attributes.Any(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The attribute's value when it is plain markup, or null when it is absent or dynamic.</summary>
    public string? Literal(string name) =>
        Attribute(name) is { HasValue: true, ValueHasRazor: false } attribute ? attribute.Value : null;

    public bool IsDynamic(string name) => Attribute(name) is { ValueHasRazor: true };
}

/// <summary>A problem found by the scanner itself, before any rule runs (unbalanced syntax).</summary>
public sealed record RazorSyntaxIssue(string RuleId, string Message, int Line, int Column, int Offset);

/// <summary>The parsed view: directives, C# spans and the HTML element tree, all with positions.</summary>
public sealed class RazorDocument
{
    public required string FilePath { get; init; }
    public required string Text { get; init; }
    public required int[] LineStarts { get; init; }

    public RazorPageKind Kind { get; set; }
    public string? Route { get; set; }
    public string? ModelType { get; set; }
    public string? Inherits { get; set; }
    public string? LayoutDirective { get; set; }
    public string? Namespace { get; set; }
    public bool IgnoresAntiForgery { get; set; }

    public List<RazorDirective> Directives { get; } = [];
    public List<RazorExpression> Expressions { get; } = [];
    public List<RazorCodeBlock> CodeBlocks { get; } = [];
    public List<RazorElement> Elements { get; } = [];
    public List<RazorSyntaxIssue> SyntaxIssues { get; } = [];
    public List<string> Handlers { get; } = [];
    public List<string> Sections { get; } = [];
    public List<RazorInjection> Injections { get; } = [];
    public List<string> Usings { get; } = [];
    public List<string> TagHelpers { get; } = [];
    public HashSet<string> LoopVariables { get; } = new(StringComparer.Ordinal);

    public int LineCount { get; set; }

    public IEnumerable<RazorElement> AllElements() =>
        Elements.SelectMany(element => element.Descendants().Prepend(element));

    public bool HasDirective(string name) =>
        Directives.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public (int Line, int Column) Position(int offset) => RazorText.Position(LineStarts, offset);

    public string LineText(int line) => RazorText.LineText(Text, LineStarts, line);
}

/// <summary>Offset → line/column conversion shared by the scanner and the rules.</summary>
public static class RazorText
{
    public static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return [.. starts];
    }

    /// <summary>1-based line and column for a character offset.</summary>
    public static (int Line, int Column) Position(int[] lineStarts, int offset)
    {
        if (lineStarts.Length == 0) return (1, 1);
        if (offset < 0) offset = 0;

        var low = 0;
        var high = lineStarts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (lineStarts[mid] <= offset) low = mid;
            else high = mid - 1;
        }
        return (low + 1, offset - lineStarts[low] + 1);
    }

    public static string LineText(string text, int[] lineStarts, int line)
    {
        if (line < 1 || line > lineStarts.Length) return string.Empty;

        var start = lineStarts[line - 1];
        var end = line < lineStarts.Length ? lineStarts[line] : text.Length;
        return text[start..end].TrimEnd('\r', '\n');
    }
}
