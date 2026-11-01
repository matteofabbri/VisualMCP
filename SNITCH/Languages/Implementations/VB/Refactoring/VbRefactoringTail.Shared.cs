using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.Refactoring;

/// <summary>
/// The syntax vocabulary the declaration-shape operations share: what a member is called, which tokens
/// make it <c>Shared</c>, where a type body ends, how a namespace counts as visible. VB has no single
/// <c>MemberDeclarationSyntax</c>, so every answer here is a switch over the block kinds.
/// </summary>
internal static partial class VbRefactoringTail
{
    internal static TypeBlockSyntax? FindTypeBlock(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<TypeBlockSyntax>()
            .FirstOrDefault(block => string.Equals(block.BlockStatement.Identifier.ValueText, name, StringComparison.Ordinal));

    internal static string? MemberName(StatementSyntax member) => member switch
    {
        MethodBlockSyntax method => method.SubOrFunctionStatement.Identifier.ValueText,
        MethodStatementSyntax method => method.Identifier.ValueText,
        PropertyBlockSyntax property => property.PropertyStatement.Identifier.ValueText,
        PropertyStatementSyntax property => property.Identifier.ValueText,
        FieldDeclarationSyntax field when field.Declarators.Count == 1 && field.Declarators[0].Names.Count == 1
            => field.Declarators[0].Names[0].Identifier.ValueText,
        _ => null,
    };

    /// <summary>The statement that carries a member's modifiers and identifier.</summary>
    internal static StatementSyntax DeclarationOf(StatementSyntax member) => member switch
    {
        MethodBlockSyntax method => method.SubOrFunctionStatement,
        PropertyBlockSyntax property => property.PropertyStatement,
        _ => member,
    };

    internal static SyntaxTokenList ModifiersOf(StatementSyntax member) => DeclarationOf(member) switch
    {
        MethodStatementSyntax statement => statement.Modifiers,
        PropertyStatementSyntax statement => statement.Modifiers,
        FieldDeclarationSyntax statement => statement.Modifiers,
        TypeBlockSyntax type => type.BlockStatement.Modifiers,
        EventStatementSyntax statement => statement.Modifiers,
        _ => default,
    };

    internal static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind) =>
        modifiers.Any(token => token.IsKind(kind));

    internal static string IndentationOf(SourceText text, int position)
    {
        if (text.Length == 0) return string.Empty;

        var line = text.Lines.GetLineFromPosition(Math.Clamp(position, 0, text.Length));
        var lineText = text.ToString(line.Span);
        return lineText[..(lineText.Length - lineText.TrimStart().Length)];
    }

    /// <summary>Every line gets the indentation; line breaks follow the document.</summary>
    internal static string Indent(string text, string indentation, string newLine)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(newLine, lines.Select(line => line.Length == 0 ? line : indentation + line));
    }

    /// <summary>
    /// Moves a declaration from one nesting level to another: the indentation it had inside its own
    /// type is replaced by the target's, so its body keeps its relative depth.
    /// </summary>
    internal static string Reindent(string text, string from, string to, string newLine)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(newLine, lines.Select(line =>
        {
            if (line.Length == 0) return line;

            var body = line.StartsWith(from, StringComparison.Ordinal) ? line[from.Length..] : line.TrimStart();
            return to + body;
        }));
    }

    /// <summary>A Visual Basic reserved word used as a name has to be bracketed: <c>[Get]</c>.</summary>
    internal static string EscapeIdentifier(string name) => VbKeywords.Contains(name) ? $"[{name}]" : name;

    private static readonly HashSet<string> VbKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "AddHandler", "AddressOf", "And", "AndAlso", "As", "Boolean", "ByRef", "ByVal", "Call", "Case",
        "Catch", "CBool", "CByte", "CChar", "CDate", "CDbl", "CDec", "Char", "CInt", "Class", "CLng",
        "CObj", "Const", "Continue", "CSByte", "CShort", "CSng", "CStr", "CType", "CUInt", "CULng",
        "CUShort", "Date", "Decimal", "Declare", "Default", "Delegate", "Dim", "DirectCast", "Do",
        "Double", "Each", "Else", "ElseIf", "End", "EndIf", "Enum", "Erase", "Error", "Event", "Exit",
        "False", "Finally", "For", "Friend", "Function", "Get", "GetType", "GetXmlNamespace", "Global",
        "GoSub", "GoTo", "Handles", "If", "Implements", "Imports", "In", "Inherits", "Integer",
        "Interface", "Is", "IsNot", "Let", "Lib", "Like", "Long", "Loop", "Me", "Mod", "Module",
        "MustInherit", "MustOverride", "MyBase", "MyClass", "NameOf", "Namespace", "Narrowing", "New",
        "Next", "Not", "Nothing", "NotInheritable", "NotOverridable", "Object", "Of", "On", "Operator",
        "Option", "Optional", "Or", "OrElse", "Out", "Overloads", "Overridable", "Overrides",
        "ParamArray", "Partial", "Private", "Property", "Protected", "Public", "RaiseEvent", "ReadOnly",
        "ReDim", "REM", "RemoveHandler", "Resume", "Return", "SByte", "Select", "Set", "Shadows",
        "Shared", "Short", "Single", "Static", "Step", "Stop", "String", "Structure", "Sub",
        "SyncLock", "Then", "Throw", "To", "True", "Try", "TryCast", "TypeOf", "UInteger", "ULong",
        "UShort", "Using", "Variant", "Wend", "When", "While", "Widening", "With", "WithEvents",
        "WriteOnly", "Xor",
    };

    /// <summary>Where a new member goes: the start of the line that closes the type body.</summary>
    internal static int BeforeEndOfType(SourceText text, TypeBlockSyntax type)
    {
        var position = Math.Clamp(type.EndBlockStatement.SpanStart, 0, text.Length);
        return text.Lines.GetLineFromPosition(position).Start;
    }

    /// <summary>Right after the line the type's opening statement ends on.</summary>
    internal static int AfterTypeHeader(SourceText text, TypeBlockSyntax type)
    {
        var end = Math.Clamp(type.BlockStatement.Span.End, 0, text.Length);
        return text.Lines.GetLineFromPosition(end).EndIncludingLineBreak;
    }

    /// <summary>Right after the line that closes the type: where a sibling declaration starts.</summary>
    internal static int AfterEndOfType(SourceText text, TypeBlockSyntax type)
    {
        var end = Math.Clamp(type.EndBlockStatement.Span.End, 0, text.Length);
        return text.Lines.GetLineFromPosition(end).EndIncludingLineBreak;
    }

    /// <summary>Where a top-level <c>Imports</c> goes: after the last one, after the options, or at the top.</summary>
    internal static int TopLevelImportInsertion(CompilationUnitSyntax unit, SourceText text)
    {
        var lastImport = unit.Imports.LastOrDefault();
        if (lastImport is not null)
            return text.Lines.GetLineFromPosition(Math.Clamp(lastImport.Span.End, 0, text.Length)).EndIncludingLineBreak;

        var lastOption = unit.ChildNodes().OfType<OptionStatementSyntax>().LastOrDefault();
        if (lastOption is not null)
            return text.Lines.GetLineFromPosition(Math.Clamp(lastOption.Span.End, 0, text.Length)).EndIncludingLineBreak;

        return 0;
    }

    /// <summary>
    /// Whether a file can name a type of <paramref name="namespaceText"/> without qualification: it declares
    /// that namespace (or one below it), or imports it. VB also searches the enclosing namespaces, so a
    /// parent of the declared namespace sees the child.
    /// </summary>
    internal static bool NamespaceVisible(SyntaxNode root, string namespaceText)
    {
        if (string.IsNullOrWhiteSpace(namespaceText)) return true;

        foreach (var block in root.DescendantNodes().OfType<NamespaceBlockSyntax>())
        {
            var declared = block.NamespaceStatement.Name.ToString();
            if (string.Equals(declared, namespaceText, StringComparison.OrdinalIgnoreCase) ||
                declared.StartsWith(namespaceText + ".", StringComparison.OrdinalIgnoreCase) ||
                namespaceText.StartsWith(declared + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var import in root.DescendantNodes().OfType<ImportsStatementSyntax>())
            foreach (var clause in import.ImportsClauses.OfType<SimpleImportsClauseSyntax>())
            {
                var imported = clause.Name.ToString();
                if (string.Equals(imported, namespaceText, StringComparison.OrdinalIgnoreCase) ||
                    imported.StartsWith(namespaceText + ".", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

        return false;
    }

    /// <summary>The namespace a type's declaration sits in, or null for the global namespace.</summary>
    internal static string? NamespaceOf(INamedTypeSymbol symbol) =>
        symbol.ContainingNamespace is { IsGlobalNamespace: false } container ? container.ToDisplayString() : null;

    /// <summary>The name a file can use for a type: the shortest chain, then the namespace when not visible.</summary>
    internal static string SpellingOf(INamedTypeSymbol symbol, SyntaxNode anchor)
    {
        var chain = new List<string>();
        for (var type = symbol; type is not null; type = type.ContainingType)
            chain.Insert(0, type.Name);

        var name = string.Join(".", chain);
        var container = NamespaceOf(symbol);
        if (container is null || NamespaceVisible(anchor, container)) return name;

        return $"{container}.{name}";
    }

    /// <summary>The expression of a plain argument; a range argument (<c>0 To 10</c>) has none.</summary>
    internal static ExpressionSyntax? ExpressionOf(ArgumentSyntax argument) =>
        argument is SimpleArgumentSyntax simple ? simple.Expression : null;

    internal static TextSpan ArgumentRemoval(InvocationExpressionSyntax invocation, int index)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (index < arguments.Count - 1)
            return TextSpan.FromBounds(arguments[index].Span.Start, arguments[index + 1].Span.Start);

        if (index > 0)
            return TextSpan.FromBounds(arguments[index - 1].Span.End, arguments[index].Span.End);

        return arguments[index].Span;
    }

    internal static TextSpan ParameterRemoval(SeparatedSyntaxList<ParameterSyntax> parameters, int index)
    {
        if (index < parameters.Count - 1)
            return TextSpan.FromBounds(parameters[index].Span.Start, parameters[index + 1].Span.Start);

        if (index > 0)
            return TextSpan.FromBounds(parameters[index - 1].Span.End, parameters[index].Span.End);

        return parameters[index].Span;
    }

    /// <summary>Whether an identifier is written: the target of an assignment or the control variable of a loop.</summary>
    internal static bool IsWritten(IdentifierNameSyntax identifier)
    {
        foreach (var ancestor in identifier.Ancestors())
        {
            if (ancestor is AssignmentStatementSyntax assignment && assignment.Left.Span.Contains(identifier.Span))
                return true;

            if (ancestor is ForStatementSyntax control && control.ControlVariable is not null &&
                control.ControlVariable.Span.Contains(identifier.Span))
                return true;

            if (ancestor is AssignmentStatementSyntax or MethodBlockBaseSyntax) break;
        }

        return false;
    }

    /// <summary>Whether the value is handed to a <c>ByRef</c> parameter, which can write it back.</summary>
    internal static bool PassedByRef(IdentifierNameSyntax identifier, SemanticModel model, CancellationToken cancellationToken)
    {
        var argument = identifier.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault();
        var value = argument is null ? null : ExpressionOf(argument);
        if (argument is null || value is null || !value.Span.Contains(identifier.Span)) return false;

        var invocation = argument.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null) return false;

        var arguments = invocation.ArgumentList.Arguments;
        var index = arguments.IndexOf(argument);
        if (index < 0) return false;

        return model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method &&
               index < method.Parameters.Length &&
               method.Parameters[index].RefKind != RefKind.None;
    }

    /// <summary>Reads the type a member's <c>As</c> clause names, when it has one.</summary>
    internal static string? TypeTextOf(SyntaxNode? asClause) => asClause switch
    {
        SimpleAsClauseSyntax simple => simple.Type.ToString(),
        AsNewClauseSyntax fresh => (fresh.NewExpression as ObjectCreationExpressionSyntax)?.Type.ToString(),
        _ => null,
    };
}
