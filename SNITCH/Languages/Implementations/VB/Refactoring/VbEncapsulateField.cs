using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// Rider's "Encapsulate Field" for Visual Basic: a field becomes a private field plus a property with
/// <c>Get</c>/<c>Set</c> accessors, and every reference to the field becomes a reference to the
/// property. The two edits are computed against the original tree and applied as text changes, so an
/// insertion never shifts a later reference.
/// </summary>
internal static class VbEncapsulateField
{
    public static async Task<object> RunAsync(
        IRoslynWorkspace workspace, IFieldSymbol field, string? propertyName, bool dryRun)
    {
        var solution = await workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory. Call load_solution first." };

        var location = field.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location?.SourceTree is null)
            return new { error = "Field has no source location." };

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), Path.GetFullPath(location.SourceTree.FilePath),
                                             StringComparison.OrdinalIgnoreCase));

        if (document is null) return new { error = "Could not locate the field's source document." };

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null) return new { error = "Could not read the field's source file." };

        var declaration = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(candidate =>
                candidate.Parent is TypeBlockSyntax type &&
                type.BlockStatement.Identifier.Text.Equals(field.ContainingType.Name, StringComparison.Ordinal) &&
                candidate.Declarators.Any(declarator => declarator.Names.Any(name => name.Identifier.Text == field.Name)));

        if (declaration is null) return new { error = "Could not locate the field declaration node." };

        var declarator = declaration.Declarators
            .First(candidate => candidate.Names.Any(name => name.Identifier.Text == field.Name));

        if (declarator.Names.Count > 1)
            return new
            {
                error = $"'{field.Name}' shares its declaration with another field; split the declaration first " +
                        "(encapsulating one name would rebuild the others).",
            };

        if (declaration.Modifiers.Any(token => token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.WithEventsKeyword)))
            return new { error = "A WithEvents field cannot be encapsulated: the event wiring depends on the field." };

        propertyName ??= ToPascalCase(field.Name);
        if (string.Equals(propertyName, field.Name, StringComparison.OrdinalIgnoreCase))
            propertyName += "Value";

        var newline = ReadNewline(await document.GetTextAsync());
        var indentation = IndentationOf((await document.GetTextAsync()), declaration.SpanStart);
        var fieldText = declaration.ToString().TrimEnd();
        var typeName = field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var propertyText = BuildProperty(propertyName, typeName, field.Name, field.IsReadOnly, indentation);

        var replacement = fieldText + newline + propertyText;

        var changes = new Dictionary<Document, List<TextChange>>
        {
            [document] = [new TextChange(declaration.Span, replacement)],
        };

        foreach (var reference in await SymbolFinder.FindReferencesAsync(field, solution))
        {
            foreach (var referenceLocation in reference.Locations)
            {
                if (!changes.TryGetValue(referenceLocation.Document, out var list))
                    changes[referenceLocation.Document] = list = [];

                list.Add(new TextChange(referenceLocation.Location.SourceSpan, propertyName));
            }
        }

        var updated = new Dictionary<Document, string>();
        foreach (var (target, edits) in changes)
        {
            var text = await target.GetTextAsync();
            updated[target] = text.WithChanges(edits).ToString();
        }

        var preview = Snitch.Languages.CSharp.Refactoring.RefactoringFileWriter.Diff(
            (await document.GetTextAsync()).ToString(), updated[document]);

        if (!dryRun)
        {
            foreach (var (target, text) in updated)
                await RefactoringFileWriter.WriteAndUpdateAsync(workspace, target, text);
        }

        return new
        {
            @refactoring = "encapsulate_field",
            fieldName = field.Name,
            propertyName,
            containingType = field.ContainingType.Name,
            readOnlyProperty = field.IsReadOnly,
            dryRun,
            applied = !dryRun,
            filesAffected = updated.Count,
            files = updated.Keys.Select(target => target.FilePath).ToList(),
            preview,
            note = dryRun
                ? "Preview only: pass dryRun=false to write the files."
                : "Files written; every edit is recorded in the solution's edit history.",
        };
    }

    private static string BuildProperty(string propertyName, string typeName, string fieldName, bool readOnly, string indent)
    {
        var inner = indent + "    ";
        var lines = new List<string>
        {
            indent + "Public " + (readOnly ? "ReadOnly " : "") + "Property " + propertyName + " As " + typeName,
            inner + "Get",
            inner + "    Return " + fieldName,
            inner + "End Get",
        };

        if (!readOnly)
        {
            lines.Add(inner + "Set(value As " + typeName + ")");
            lines.Add(inner + "    " + fieldName + " = value");
            lines.Add(inner + "End Set");
        }

        lines.Add(indent + "End Property");
        return string.Join(Environment.NewLine, lines);
    }

    private static string IndentationOf(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, position));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }

    private static string ReadNewline(SourceText text) =>
        text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string ToPascalCase(string name)
    {
        var trimmed = name.TrimStart('_');
        if (trimmed.Length == 0) return name;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
