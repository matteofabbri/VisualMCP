using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// ReSharper's "Implement missing members": a type that declares an interface or derives from an
/// abstract class but does not implement everything gets one stub per missing member, with the real
/// signature taken from the symbol and a body that throws. The compiler gate then proves the
/// CS0535/CS0534 (C#) or BC30149 (VB) errors are gone and nothing new appeared.
/// <para>
/// Visual Basic stubs carry the <c>Implements I.Member</c> clause the language requires; the check at
/// the end is the missing-member count of the type, which is the same question in either language and
/// does not depend on diagnostic messages (they are localized).
/// </para>
/// </summary>
public interface IImplementMembersService
{
    Task<object> ImplementMembers(
        string filePath,
        string typeName,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed partial class ImplementMembersService : IImplementMembersService
{
    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public ImplementMembersService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> ImplementMembers(
        string filePath,
        string typeName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to complete is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var document = FindDocument(solution, filePath);
        if (document is null) return new { error = $"{filePath} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var declaration = root.DescendantNodes().Where(IsTypeDeclaration)
            .FirstOrDefault(candidate => NameOf(candidate) == typeName);
        if (declaration is null) return new { error = $"No type '{typeName}' in {Path.GetFileName(document.FilePath)}." };

        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type)
            return new { error = $"'{typeName}' did not resolve to a type." };

        if (type.TypeKind == TypeKind.Interface)
            return new { error = $"'{typeName}' is an interface; it has no members to implement." };

        var missing = MissingMembers(type);
        if (missing.Count == 0)
            return new
            {
                tool = "implement_members",
                type = typeName,
                implemented = Array.Empty<string>(),
                note = "Nothing is missing: every interface and abstract member is already implemented.",
            };

        var visualBasic = declaration.SyntaxTree.Options.Language == LanguageNames.VisualBasic;
        var stubs = missing.Select(member => Render(member, type, model, declaration.SpanStart, visualBasic)).ToList();
        var newText = InsertMembers(text, declaration, stubs);

        var afterSolution = solution.WithDocumentText(document.Id, SourceText.From(newText));
        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, afterSolution, new[] { document.Project.Id }, cancellationToken);

        // A gate over the total alone would accept a stub that trades one error for another: require
        // that the type's missing-member count actually went down.
        var before = missing.Count;
        var after = await MissingMemberCountAsync(afterSolution, document, typeName, cancellationToken);

        if (!gate.Passed || after >= before)
            return new
            {
                error = after >= before
                    ? $"Refused: the generated stubs would not resolve the missing-member errors of '{typeName}'."
                    : $"Refused: completing '{typeName}' would add compiler errors ({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors.Count > 0 ? gate.NewErrors : gate.Errors,
                members = missing.Select(member => member.ToDisplayString()).ToList(),
            };

        if (!dryRun)
        {
            _store?.RecordEdit("implement_members", document.FilePath!, typeName, text.ToString(), newText);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, newText, cancellationToken);
        }

        return new
        {
            tool = "implement_members",
            type = typeName,
            implemented = missing.Select(member => member.ToDisplayString()).ToList(),
            stubCount = missing.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            missingMemberErrors = new { before, after },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(text.ToString(), newText),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    /// <summary>
    /// How many interface and abstract members the type still owes after the rewrite. It is the same
    /// question the compiler asks, asked through the symbol model, so it works in both languages and
    /// does not read a diagnostic message (which is localized).
    /// </summary>
    private static async Task<int> MissingMemberCountAsync(
        Solution solution, Document original, string typeName, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(original.Id);
        if (document is null) return int.MaxValue;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return int.MaxValue;

        var declaration = root.DescendantNodes().Where(IsTypeDeclaration)
            .FirstOrDefault(candidate => NameOf(candidate) == typeName);
        if (declaration is null) return int.MaxValue;

        return model.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol type
            ? MissingMembers(type).Count
            : int.MaxValue;
    }

    /// <summary>Every interface or abstract member the type does not implement yet.</summary>
    internal static List<ISymbol> MissingMembers(INamedTypeSymbol type)
    {
        var missing = new List<ISymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var @interface in type.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (member.IsStatic || member.IsImplicitlyDeclared) continue;
                if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                if (member is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;
                if (type.FindImplementationForInterfaceMember(member) is not null) continue;

                var key = Signature(member);
                if (seen.Add(key)) missing.Add(member);
            }
        }

        for (var baseType = type.BaseType;
             baseType is not null && baseType.SpecialType != SpecialType.System_Object;
             baseType = baseType.BaseType)
        {
            foreach (var member in baseType.GetMembers())
            {
                if (!member.IsAbstract || member.IsStatic) continue;
                if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary }) continue;
                if (member is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) continue;
                if (HasOverride(type, member)) continue;

                var key = Signature(member);
                if (seen.Add(key)) missing.Add(member);
            }
        }

        return missing;
    }

    private static bool HasOverride(INamedTypeSymbol type, ISymbol abstractMember)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(abstractMember.Name))
            {
                var overrides = (candidate, abstractMember) switch
                {
                    (IMethodSymbol method, IMethodSymbol target) =>
                        SymbolEqualityComparer.Default.Equals(method.OverriddenMethod, target),
                    (IPropertySymbol property, IPropertySymbol target) =>
                        SymbolEqualityComparer.Default.Equals(property.OverriddenProperty, target),
                    (IEventSymbol @event, IEventSymbol target) =>
                        SymbolEqualityComparer.Default.Equals(@event.OverriddenEvent, target),
                    _ => false,
                };

                if (overrides) return true;
            }
        }

        return false;
    }

    /// <summary>A type signature used to avoid generating the same member twice for two interfaces.</summary>
    private static string Signature(ISymbol member) => member switch
    {
        IMethodSymbol method =>
            method.ReturnType.ToDisplayString() + " " + method.Name + "(" +
            string.Join(",", method.Parameters.Select(parameter => parameter.Type.ToDisplayString())) + ")",
        IPropertySymbol property => property.Type.ToDisplayString() + " " + property.Name + "[" +
                                    string.Join(",", property.Parameters.Select(parameter => parameter.Type.ToDisplayString())) + "]",
        IEventSymbol @event => @event.Type.ToDisplayString() + " " + @event.Name,
        _ => member.ToDisplayString(),
    };

    /// <summary>
    /// The stub text for one member. Types are printed with <c>ToMinimalDisplayString</c> against the
    /// insertion point, so they come out as <c>string</c>/<c>List&lt;int&gt;</c> and reuse the file's usings.
    /// <c>NotImplementedException</c> is fully qualified: no using to add, no implicit-usings dependency.
    /// </summary>
    private static string Render(
        ISymbol member, INamedTypeSymbol type, SemanticModel model, int position, bool visualBasic) =>
        visualBasic ? RenderVisualBasic(member, type, model, position) : RenderCSharp(member, model, position);

    private static string RenderCSharp(ISymbol member, SemanticModel model, int position)
    {
        const string Throws = "throw new System.NotImplementedException();";

        string Type(ITypeSymbol symbol) => symbol.ToMinimalDisplayString(model, position);

        switch (member)
        {
            case IMethodSymbol method:
            {
                var parameters = string.Join(", ", method.Parameters.Select(parameter =>
                {
                    var modifier = parameter.RefKind switch
                    {
                        RefKind.Ref => "ref ",
                        RefKind.Out => "out ",
                        RefKind.In => "in ",
                        _ => string.Empty,
                    };
                    var optional = parameter.IsOptional ? " = default" : string.Empty;
                    return $"{modifier}{Type(parameter.Type)} {parameter.Name}{optional}";
                }));

                var typeParameters = method.TypeParameters.Length == 0
                    ? string.Empty
                    : "<" + string.Join(", ", method.TypeParameters.Select(parameter => parameter.Name)) + ">";

                return $"public {Type(method.ReturnType)} {method.Name}{typeParameters}({parameters}) {{ {Throws} }}";
            }

            case IPropertySymbol property:
            {
                var accessors = new List<string>();
                if (property.GetMethod is not null) accessors.Add($"get => {Throws}");
                if (property.SetMethod is not null) accessors.Add($"set => {Throws}");

                var indexParameters = property.Parameters.Length == 0
                    ? string.Empty
                    : "[" + string.Join(", ", property.Parameters.Select(parameter =>
                        $"{Type(parameter.Type)} {parameter.Name}")) + "]";

                var name = property.IsIndexer ? "this" : property.Name;
                return $"public {Type(property.Type)} {name}{indexParameters} {{ {string.Join(" ", accessors)} }}";
            }

            case IEventSymbol @event:
                return $"public event {Type(@event.Type)} {@event.Name} {{ add {{ {Throws} }} remove {{ {Throws} }} }}";

            default:
                return $"public object {member.Name} => {Throws}";
        }
    }

    /// <summary>Every type declaration a stub can be added to: a C# type, or a VB class, structure or module.</summary>
    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? IsVisualBasicTypeDeclaration(node)
            : node is TypeDeclarationSyntax;

    private static string? NameOf(SyntaxNode node) =>
        node.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicTypeNameOf(node)
            : ((TypeDeclarationSyntax)node).Identifier.ValueText;

    // ── text insertion ───────────────────────────────────────────────────────

    /// <summary>
    /// Inserts the stubs on their own lines before the type's closing brace (C#) or <c>End Class</c>
    /// (VB), indented like the members already there. A one-line C# type has no such line, so the text
    /// between its braces is replaced.
    /// </summary>
    internal static string InsertMembers(SourceText text, SyntaxNode type, IReadOnlyList<string> members)
    {
        if (members.Count == 0) return text.ToString();

        var source = text.ToString();
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var indentation = MemberIndentation(text, type);

        var block = new StringBuilder();
        foreach (var member in members)
        {
            foreach (var line in member.Replace("\r\n", "\n").Split('\n'))
            {
                block.Append(indentation).Append(line).Append(newline);
            }
        }

        var closing = ClosingPosition(type);
        var closingLine = text.Lines.GetLineFromPosition(closing);
        var beforeClosing = source[closingLine.Start..closing];

        // The usual shape: the closing token alone on its line — insert at the start of that line.
        if (beforeClosing.Trim().Length == 0 || type.SyntaxTree.Options.Language == LanguageNames.VisualBasic)
            return source[..closingLine.Start] + block + source[closingLine.Start..];

        // `public class C { }`: replace the whitespace between the braces with the members.
        var open = ((TypeDeclarationSyntax)type).OpenBraceToken.Span.End;
        var closeIndentation = TypeIndentation(text, type);
        return source[..open] + newline + block.ToString().TrimEnd('\r', '\n') + newline +
               closeIndentation + source[closing..];
    }

    /// <summary>Where a type body ends: the closing brace in C#, the first token of <c>End Class</c> in VB.</summary>
    private static int ClosingPosition(SyntaxNode type) =>
        type.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicEndBlockPosition(type)
            : ((TypeDeclarationSyntax)type).CloseBraceToken.SpanStart;

    /// <summary>The indentation of the line the type declaration sits on.</summary>
    internal static string TypeIndentation(SourceText text, SyntaxNode type)
    {
        var line = text.Lines.GetLineFromPosition(type.SpanStart);
        var prefix = text.ToString(TextSpan.FromBounds(line.Start, type.SpanStart));
        return prefix.All(character => character is ' ' or '\t') ? prefix : string.Empty;
    }

    /// <summary>The indentation of the first existing member, or four spaces past the type's own line.</summary>
    internal static string MemberIndentation(SourceText text, SyntaxNode type)
    {
        var member = FirstMember(type);
        if (member is not null)
        {
            var line = text.Lines.GetLineFromPosition(member.SpanStart);
            var prefix = text.ToString(TextSpan.FromBounds(line.Start, member.SpanStart));
            if (prefix.Length > 0 && prefix.All(character => character is ' ' or '\t')) return prefix;
        }

        return TypeIndentation(text, type) + "    ";
    }

    private static SyntaxNode? FirstMember(SyntaxNode type) =>
        type.SyntaxTree.Options.Language == LanguageNames.VisualBasic
            ? VisualBasicFirstMember(type)
            : ((TypeDeclarationSyntax)type).Members.FirstOrDefault();

    private static Document? FindDocument(Solution solution, string filePath)
    {
        var path = Path.GetFullPath(filePath);
        return solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path,
                                                       StringComparison.OrdinalIgnoreCase));
    }
}
