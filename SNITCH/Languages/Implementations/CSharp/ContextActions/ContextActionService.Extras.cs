using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The tail of the context-action catalogue: the four actions whose condition is a type-system fact or a
/// single unambiguous literal shape, so a headless engine can decide them without a project model.
/// </summary>
public sealed partial class ContextActionService
{
    /// <summary><c>""</c> becomes <c>string.Empty</c> on the selected line.</summary>
    private Outcome EmptyStringToStringEmpty(Target target)
    {
        var literal = target.Root.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(node => node.IsKind(SyntaxKind.StringLiteralExpression)
                                    && node.Token.ValueText.Length == 0
                                    && OnSelectedLine(target, node));

        if (literal is null) return Outcome.Fail("No empty string literal (\"\") on this line.");

        var replacement = SyntaxFactory.ParseExpression("string.Empty").WithTriviaFrom(literal);
        var newRoot = target.Root.ReplaceNode(literal, replacement);

        return new Outcome(newRoot.ToFullString(), ["\"\" becomes string.Empty"]);
    }

    /// <summary><c>string.Empty</c> (or <c>String.Empty</c>) becomes <c>""</c> on the selected line.</summary>
    private Outcome StringEmptyToLiteral(Target target)
    {
        var access = target.Root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault(node => node.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                                    && node.Name.Identifier.Text == "Empty"
                                    && node.Expression is PredefinedTypeSyntax type
                                    && type.Keyword.Text is "string" or "String"
                                    && OnSelectedLine(target, node));

        if (access is null) return Outcome.Fail("No 'string.Empty' on this line.");

        var replacement = SyntaxFactory.ParseExpression("\"\"").WithTriviaFrom(access);
        var newRoot = target.Root.ReplaceNode(access, replacement);

        return new Outcome(newRoot.ToFullString(), ["string.Empty becomes \"\""]);
    }

    /// <summary>
    /// An auto-property becomes an explicit one over a private backing field, so a body can be added:
    /// <c>public int Value { get; set; }</c> → <c>private int _value;</c> plus a property whose accessors
    /// read and write it. Refused on an interface member, on an accessor that already has a body, and when
    /// the backing field's name is taken.
    /// </summary>
    private Outcome AutoPropertyToExplicit(Target target)
    {
        var property = target.Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(candidate =>
                candidate.AccessorList is not null
                && candidate.ExpressionBody is null
                && candidate.AccessorList.Accessors.Count > 0
                && candidate.AccessorList.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null)
                && OnSelectedLine(target, candidate));

        if (property is null) return Outcome.Fail("No auto-property on this line (a property whose accessors have no body).");
        if (property.Parent is InterfaceDeclarationSyntax)
            return Outcome.Fail("The property is an interface member: an interface has no field to back it.");

        var name = property.Identifier.Text;
        var fieldName = "_" + char.ToLowerInvariant(name[0]) + name[1..];

        var declared = property.Parent?.DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Select(member => member switch
            {
                FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
                PropertyDeclarationSyntax sibling => sibling.Identifier.Text,
                MethodDeclarationSyntax method => method.Identifier.Text,
                EventDeclarationSyntax @event => @event.Identifier.Text,
                BaseTypeDeclarationSyntax type => type.Identifier.Text,
                _ => null,
            })
            .Where(memberName => memberName is not null)
            .ToList() ?? [];

        if (declared.Contains(fieldName))
            return Outcome.Fail($"A member named '{fieldName}' already exists; the backing field would collide.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(property));

        var modifiers = property.Modifiers.ToFullString();
        var isStatic = property.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword));
        var type = property.Type.ToString();
        var initializer = property.Initializer is null ? string.Empty : " = " + property.Initializer.Value;

        var accessors = new List<string>();
        foreach (var accessor in property.AccessorList!.Accessors)
        {
            if (accessor.Keyword.IsKind(SyntaxKind.GetKeyword))
            {
                accessors.Add($"{indentation}    get => {fieldName};");
            }
            else
            {
                var keyword = accessor.Keyword.IsKind(SyntaxKind.InitKeyword) ? "init" : "set";
                accessors.Add($"{indentation}    {keyword} => {fieldName} = value;");
            }
        }

        var replacement =
            $"{(isStatic ? "private static " : "private ")}{type} {fieldName}{initializer};{newLine}" +
            $"{indentation}{modifiers}{type} {name}{newLine}" +
            $"{indentation}{{{newLine}" +
            string.Join(newLine, accessors) + newLine +
            $"{indentation}}}";

        // Splice the replacement over the property's own span: the text before it keeps the line's
        // indentation, so the field starts where the property did and the property gets its own line.
        var text = target.Text.ToString();
        var updated = text[..property.Span.Start] + replacement + text[property.Span.End..];

        return new Outcome(updated, [$"auto-property '{name}' becomes an explicit property over '{fieldName}'"]);
    }

    /// <summary>
    /// Inserts the repository's <c>file_header_template</c> (the .editorconfig option IDE0073 reads) at the
    /// top of the document, with <c>{fileName}</c> substituted.
    /// </summary>
    private Outcome InsertFileHeader(Target target)
    {
        if (target.Document.FilePath is null) return Outcome.Fail("The document has no path on disk.");

        var options = target.Document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider
            .GetOptions(target.Tree);

        if (!options.TryGetValue("file_header_template", out var template) || string.IsNullOrWhiteSpace(template))
        {
            return Outcome.Fail(
                "No 'file_header_template' in the project's .editorconfig chain. Set it first " +
                "(configure_formatting option=file_header_template), then run this action again.");
        }

        var header = template.Replace("{fileName}", Path.GetFileName(target.Document.FilePath), StringComparison.Ordinal);
        var newLine = NewLineOf(target.Text.ToString());
        var text = target.Text.ToString();

        if (text.TrimStart('\uFEFF').StartsWith(header.Trim(), StringComparison.Ordinal))
            return Outcome.Fail("The file already starts with the header.");

        var normalized = header.Replace("\r\n", "\n").Replace("\n", newLine);
        var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : string.Empty;
        var body = text.StartsWith('\uFEFF') ? text[1..] : text;

        return new Outcome(bom + normalized + newLine + newLine + body, ["inserts the file header template"]);
    }

    /// <summary>Whether the node starts on one of the selected lines.</summary>
    private static bool OnSelectedLine(Target target, SyntaxNode node)
    {
        var line = LineOf(node);
        return line >= target.Line && line <= target.ToLine;
    }
}
