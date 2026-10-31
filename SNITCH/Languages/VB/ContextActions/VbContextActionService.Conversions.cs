using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    /// <summary>
    /// ReSharper's "Import type" for VB: adds the <c>Imports</c> statement that brings the type named on
    /// the line into scope. The namespace comes from the compilation, so an ambiguous simple name is
    /// refused with the candidates instead of guessing one.
    /// </summary>
    private Outcome ImportType(Target target, string? text)
    {
        var requested = text?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            var onLine = target.Root.DescendantNodes()
                .Where(node => LineOf(node) == target.Line)
                .OfType<TypeSyntax>()
                .OrderBy(node => node.Span.Length)
                .FirstOrDefault();

            if (onLine is null)
                return Outcome.Fail($"No type on line {target.Line}: pass the type name in text.");

            requested = onLine.ToString();
        }

        var candidates = ResolveNamespaces(target.Compilation, requested);
        if (candidates.Count == 0)
            return Outcome.Fail($"'{requested}' is not declared in the source or in any referenced assembly.");
        if (candidates.Count > 1)
            return Outcome.Fail(
                $"'{requested}' is declared in {candidates.Count} namespaces: {string.Join(", ", candidates)}. " +
                "Pass the fully qualified name in text to choose one.");

        var ns = candidates[0];
        if (string.IsNullOrEmpty(ns))
            return Outcome.Fail($"'{requested}' is in the global namespace: no Imports statement can bring it into scope.");

        if (target.Root is not CompilationUnitSyntax unit)
            return Outcome.Fail("The document has no compilation unit to add an Imports statement to.");

        var position = target.Root.DescendantNodes().FirstOrDefault(node => LineOf(node) == target.Line)?.SpanStart ?? 0;
        var simpleName = requested[(requested.LastIndexOf('.') + 1)..];
        if (IsSymbolVisible(target.Model, position, simpleName))
            return Outcome.Fail($"'{simpleName}' is already in scope on line {target.Line}: nothing to import.");

        return new Outcome(AddImports(target.Text.ToString(), unit, ns), [$"added `Imports {ns}`"]);
    }

    /// <summary>
    /// "Convert to interpolated string": an <c>&amp;</c> chain with at least one non-literal operand
    /// becomes <c>$"a{x}b"</c>. Text pieces have their quotes and braces escaped the way VB requires.
    /// </summary>
    private Outcome ConcatToInterpolated(Target target)
    {
        var concatenation = target.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(candidate => candidate.IsKind(SyntaxKind.ConcatenateExpression) && LineOf(candidate) == target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (concatenation is null)
            return Outcome.Fail($"No '&' concatenation on line {target.Line}.");

        var parts = new List<ExpressionSyntax>();
        Flatten(concatenation, parts);

        var pieces = new List<string>(parts.Count);
        var holes = 0;
        foreach (var part in parts)
        {
            if (part is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                pieces.Add(EscapeInterpolatedText(literal.Token.ValueText));
                continue;
            }

            pieces.Add("{" + part.ToString() + "}");
            holes++;
        }

        if (holes == 0)
            return Outcome.Fail("Every operand is a literal: there is nothing to interpolate.");

        return new Outcome(ReplaceNode(target, concatenation, "$\"" + string.Concat(pieces) + "\""),
            [$"concatenation of {parts.Count} piece(s) → interpolated string"]);

        static void Flatten(ExpressionSyntax expression, List<ExpressionSyntax> into)
        {
            if (expression is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.ConcatenateExpression))
            {
                Flatten(binary.Left, into);
                Flatten(binary.Right, into);
                return;
            }

            into.Add(expression);
        }
    }

    /// <summary>"Convert to string concatenation": an interpolated string with a hole becomes an <c>&amp;</c> chain.</summary>
    private Outcome InterpolatedToConcat(Target target)
    {
        var interpolated = target.Root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (interpolated is null) return Outcome.Fail($"No interpolated string on line {target.Line}.");

        var pieces = new List<string>();
        foreach (var content in interpolated.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax text:
                {
                    var value = text.TextToken.ValueText;
                    if (value.Length == 0) break;
                    pieces.Add("\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                    break;
                }

                case InterpolationSyntax hole:
                    if (hole.AlignmentClause is not null || hole.FormatClause is not null)
                        return Outcome.Fail($"The hole {{{hole.Expression}}} has an alignment or a format specifier: it has no concatenation form.");
                    pieces.Add(NeedsParentheses(hole.Expression) ? "(" + hole.Expression + ")" : hole.Expression.ToString());
                    break;
            }
        }

        if (pieces.Count < 2 || !interpolated.Contents.OfType<InterpolationSyntax>().Any())
            return Outcome.Fail("The interpolated string has no hole: there is nothing to concatenate.");

        return new Outcome(ReplaceNode(target, interpolated, string.Join(" & ", pieces)),
            [$"interpolated string of {pieces.Count} piece(s) → concatenation"]);
    }

    /// <summary>"Add digit separators": the decimal integer literal on the line is grouped every three digits.</summary>
    private Outcome DigitSeparators(Target target)
    {
        var literal = target.Root.DescendantTokens()
            .Where(token => IsNumericLiteral(token) && LineOf(token) == target.Line)
            .FirstOrDefault(token => token.Text.Any(char.IsDigit));

        if (string.IsNullOrEmpty(literal.Text))
            return Outcome.Fail($"No numeric literal on line {target.Line}.");

        var raw = literal.Text;
        if (raw.Contains('_', StringComparison.Ordinal))
            return Outcome.Fail($"'{raw}' already carries digit separators.");
        if (raw.StartsWith('&'))
            return Outcome.Fail($"'{raw}' is hexadecimal, octal or binary: grouping it needs a different convention.");
        if (raw.Contains('.', StringComparison.Ordinal) || raw.Contains('e', StringComparison.OrdinalIgnoreCase))
            return Outcome.Fail($"'{raw}' is not an integer literal.");

        var suffixStart = raw.Length;
        while (suffixStart > 0 && char.IsLetter(raw[suffixStart - 1]))
            suffixStart--;

        var digits = raw[..suffixStart];
        var suffix = raw[suffixStart..];
        if (digits.Length < 5 || !digits.All(char.IsDigit))
            return Outcome.Fail($"'{raw}' is not an integer with at least five digits: there is nothing to group.");

        var grouped = new System.Text.StringBuilder(digits.Length + digits.Length / 3);
        for (var index = 0; index < digits.Length; index++)
        {
            if (index > 0 && (digits.Length - index) % 3 == 0) grouped.Append('_');
            grouped.Append(digits[index]);
        }

        return new Outcome(ReplaceSpan(target, literal.Span, grouped.ToString() + suffix),
            [$"'{raw}' → '{grouped}{suffix}'"]);
    }

    /// <summary>"Remove digit separators" for the literal on the line.</summary>
    private Outcome RemoveDigitSeparators(Target target)
    {
        var literal = target.Root.DescendantTokens()
            .Where(token => IsNumericLiteral(token) && LineOf(token) == target.Line &&
                            token.Text.Contains('_', StringComparison.Ordinal))
            .FirstOrDefault();

        if (string.IsNullOrEmpty(literal.Text))
            return Outcome.Fail($"No numeric literal with digit separators on line {target.Line}.");

        var raw = literal.Text;
        return new Outcome(ReplaceSpan(target, literal.Span, raw.Replace("_", string.Empty, StringComparison.Ordinal)),
            [$"'{raw}' → '{raw.Replace("_", string.Empty, StringComparison.Ordinal)}'"]);
    }

    /// <summary>"Remove argument name": a <c>name:=value</c> argument on the line becomes positional.</summary>
    private Outcome RemoveArgumentName(Target target, string? text)
    {
        var requested = text?.Trim();
        var named = target.Root.DescendantNodes().OfType<SimpleArgumentSyntax>()
            .Where(candidate => candidate.NameColonEquals is not null && LineOf(candidate) == target.Line)
            .Where(candidate => requested is null ||
                                string.Equals(candidate.NameColonEquals!.Name.Identifier.ValueText, requested, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (named is null)
            return Outcome.Fail(requested is null
                ? $"No named argument on line {target.Line}."
                : $"No argument named '{requested}' on line {target.Line}.");

        return new Outcome(ReplaceNode(target, named, named.Expression.ToString()),
            [$"argument '{named.NameColonEquals!.Name}' → positional"]);
    }

    /// <summary>"Use var": the explicit type of a single local declared with an initializer is removed.</summary>
    private Outcome UseVar(Target target)
    {
        var declaration = LocalDeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail($"No local declaration on line {target.Line}.");
        if (declaration.Declarators.Count != 1)
            return Outcome.Fail("The declaration has several declarators; split it first (split_declaration).");

        var declarator = declaration.Declarators[0];
        if (declarator.Names.Count != 1)
            return Outcome.Fail("The declaration names several variables; split it first (split_declaration).");
        if (declarator.AsClause is null) return Outcome.Fail("The declaration has no explicit type to remove.");
        if (declarator.Initializer is null) return Outcome.Fail("The declaration has no initializer, so the type cannot be inferred.");
        if (!OptionInferEnabled(target))
            return Outcome.Fail("Option Infer is off in this compilation, so removing the type would make the local Object.");

        var name = declarator.Names[0];
        return new Outcome(ReplaceSpan(target, TextSpan.FromBounds(name.Span.End, declarator.AsClause.Span.End), string.Empty),
            [$"removed '{declarator.AsClause}'"]);
    }

    /// <summary>"Use explicit type": a local declared with an initializer gets the type the compiler inferred.</summary>
    private Outcome UseExplicitType(Target target)
    {
        var declaration = LocalDeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail($"No local declaration on line {target.Line}.");
        if (declaration.Declarators.Count != 1)
            return Outcome.Fail("The declaration has several declarators; split it first (split_declaration).");

        var declarator = declaration.Declarators[0];
        if (declarator.Names.Count != 1)
            return Outcome.Fail("The declaration names several variables; split it first (split_declaration).");
        if (declarator.AsClause is not null) return Outcome.Fail("The declaration already has an explicit type.");
        if (declarator.Initializer is null) return Outcome.Fail("The declaration has no initializer, so there is no type to write.");

        var type = target.Model.GetTypeInfo(declarator.Initializer.Value).Type;
        if (type is null || type.TypeKind == TypeKind.Error)
            return Outcome.Fail("The compiler could not infer the type of the initializer.");
        if (type.IsAnonymousType)
            return Outcome.Fail("The initializer has an anonymous type, which has no name to write.");

        var text = type.ToMinimalDisplayString(target.Model, declarator.Initializer.SpanStart);
        return new Outcome(target.Text.ToString().Insert(declarator.Names[0].Span.End, " As " + text), [$"→ As {text}"]);
    }

    /// <summary>"Use ConfigureAwait(false)": the awaited expression gets the suffix.</summary>
    private Outcome UseConfigureAwaitFalse(Target target)
    {
        var awaited = target.Root.DescendantNodes().OfType<AwaitExpressionSyntax>()
            .Where(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line)
            .OrderByDescending(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (awaited is null) return Outcome.Fail($"No Await expression on line {target.Line}.");

        var operand = awaited.Expression;
        if (operand.ToString().Contains(".ConfigureAwait(", StringComparison.Ordinal))
            return Outcome.Fail("The awaited expression already calls ConfigureAwait.");

        if (operand is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax))
            return Outcome.Fail("The awaited expression is not an identifier, member access or call: a suffix would not bind to it.");

        return new Outcome(target.Text.ToString().Insert(operand.Span.End, ".ConfigureAwait(False)"),
            [$"Await {operand} → Await {operand}.ConfigureAwait(False)"]);
    }

    /// <summary>"Split declaration": one <c>Dim</c> per declarator, keeping the modifier and the indentation.</summary>
    private Outcome SplitDeclaration(Target target)
    {
        var declaration = LocalDeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail($"No local declaration on line {target.Line}.");
        if (declaration.Declarators.Count < 2)
            return Outcome.Fail("The declaration has one declarator; there is nothing to split.");

        var text = target.Text.ToString();
        var prefix = text.Substring(declaration.SpanStart, declaration.Declarators[0].SpanStart - declaration.SpanStart);
        var newLine = NewLineOf(text);
        var indentation = IndentationAt(target.Text, LineOf(declaration));

        var lines = declaration.Declarators
            .Select(declarator => indentation + prefix + declarator.ToString().Trim())
            .ToList();

        return new Outcome(ReplaceNode(target, declaration, string.Join(newLine, lines)),
            [$"split into {lines.Count} declarations"]);
    }

    /// <summary>
    /// "Convert to StringBuilder": the string local on the line has an empty initializer and is only
    /// appended to, so it becomes a <c>StringBuilder</c> and every append becomes <c>Append</c>. A read
    /// that would need <c>ToString()</c> is refused instead of being rewritten silently.
    /// </summary>
    private Outcome ConcatToStringBuilder(Target target)
    {
        var declaration = LocalDeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail($"No local declaration on line {target.Line}.");
        if (declaration.Declarators.Count != 1 || declaration.Declarators[0].Names.Count != 1)
            return Outcome.Fail("The declaration names several variables; split it first (split_declaration).");

        var declarator = declaration.Declarators[0];
        var name = declarator.Names[0].Identifier.ValueText;
        if (target.Model.GetDeclaredSymbol(declarator.Names[0]) is not ILocalSymbol local ||
            local.Type.SpecialType != SpecialType.System_String)
            return Outcome.Fail($"'{name}' is not a String local.");
        if (declarator.Initializer?.Value is not LiteralExpressionSyntax initializer ||
            !initializer.IsKind(SyntaxKind.StringLiteralExpression) || initializer.Token.ValueText.Length > 0)
            return Outcome.Fail("The local is not initialized with an empty string, so it is not a StringBuilder accumulator.");

        var method = EnclosingMethod(target);
        if (method is null) return Outcome.Fail("The declaration is not inside a method.");

        var appends = new List<(StatementSyntax Statement, string Value)>();
        var appendsSeen = new HashSet<StatementSyntax>();
        foreach (var identifier in method.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!string.Equals(identifier.Identifier.ValueText, name, StringComparison.Ordinal)) continue;
            if (target.Model.GetSymbolInfo(identifier).Symbol is not ILocalSymbol used ||
                !SymbolEqualityComparer.Default.Equals(used, local))
                continue;

            var statement = identifier.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            if (statement is AssignmentStatementSyntax assignment && assignment.Left == identifier)
            {
                if (assignment.IsKind(SyntaxKind.ConcatenateAssignmentStatement))
                {
                    if (appendsSeen.Add(statement)) appends.Add((statement, assignment.Right.ToString()));
                    continue;
                }

                if (assignment.IsKind(SyntaxKind.SimpleAssignmentStatement) &&
                    assignment.Right is BinaryExpressionSyntax concatenation &&
                    concatenation.IsKind(SyntaxKind.ConcatenateExpression) &&
                    concatenation.Left == identifier)
                {
                    if (appendsSeen.Add(statement)) appends.Add((statement, concatenation.Right.ToString()));
                    continue;
                }
            }

            return Outcome.Fail(
                $"'{name}' is read on line {LineOf(identifier)}; converting it to a StringBuilder would need a ToString() there. " +
                "Rewrite that read first, then retry.");
        }

        if (appends.Count == 0)
            return Outcome.Fail($"'{name}' is never appended to: there is nothing to convert.");

        var text = target.Text.ToString();
        var replacements = new List<(TextSpan Span, string Text)>
        {
            (declarator.Span, name + " As New System.Text.StringBuilder()")
        };
        foreach (var (statement, value) in appends)
            replacements.Add((statement.Span, name + ".Append(" + value + ")"));

        foreach (var (span, replacement) in replacements.OrderByDescending(entry => entry.Span.Start))
            text = text.Remove(span.Start, span.Length).Insert(span.Start, replacement);

        return new Outcome(text, [$"'{name}' → StringBuilder with {appends.Count} append(s)"]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static LocalDeclarationStatementSyntax? LocalDeclarationOnLine(Target target) =>
        target.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line)
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

    /// <summary>Visual Basic spells its numeric literal token by kind (integer, floating, decimal).</summary>
    private static bool IsNumericLiteral(SyntaxToken token) =>
        token.IsKind(SyntaxKind.IntegerLiteralToken) ||
        token.IsKind(SyntaxKind.FloatingLiteralToken) ||
        token.IsKind(SyntaxKind.DecimalLiteralToken);

    private static bool OptionInferEnabled(Target target)
    {
        if (target.Compilation.Options is VisualBasicCompilationOptions options && !options.OptionInfer)
            return false;

        return target.Root is not CompilationUnitSyntax unit ||
               !unit.Options.Any(option => option.ToString().Replace(" ", string.Empty, StringComparison.Ordinal)
                   .Equals("OptionInferOff", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Text for a piece of an interpolated string: quotes and braces are escaped the VB way.</summary>
    private static string EscapeInterpolatedText(string value) =>
        value.Replace("{", "{{", StringComparison.Ordinal)
             .Replace("}", "}}", StringComparison.Ordinal)
             .Replace("\"", "\"\"", StringComparison.Ordinal);

    /// <summary>The namespaces that declare the type: the exact metadata name when qualified, else every match.</summary>
    private static List<string> ResolveNamespaces(Compilation compilation, string requested)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);

        if (requested.Contains('.', StringComparison.Ordinal))
        {
            if (compilation.GetTypeByMetadataName(requested) is { } exact)
                namespaces.Add(NamespaceOf(exact));
            return namespaces.ToList();
        }

        Collect(compilation.SourceModule.GlobalNamespace, requested, namespaces);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                Collect(assembly.GlobalNamespace, requested, namespaces);
        }

        return namespaces.ToList();
    }

    private static void Collect(INamespaceSymbol container, string name, ISet<string> found)
    {
        foreach (var member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol child:
                    Collect(child, name, found);
                    break;
                case INamedTypeSymbol type when string.Equals(type.Name, name, StringComparison.Ordinal):
                    found.Add(NamespaceOf(type));
                    break;
            }
        }
    }

    private static string NamespaceOf(INamedTypeSymbol type)
    {
        var outer = type;
        while (outer.ContainingType is not null)
            outer = outer.ContainingType;

        var ns = outer.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    private static bool IsSymbolVisible(SemanticModel model, int position, string name) =>
        model.LookupSymbols(position, name: name).Any(symbol => symbol is INamedTypeSymbol);

    /// <summary>Inserts the Imports after the last one, after the Option statements, or at the top.</summary>
    private static string AddImports(string text, CompilationUnitSyntax unit, string ns)
    {
        var newLine = NewLineOf(text);
        var insertAt = 0;

        if (unit.Imports.Count > 0)
            insertAt = AfterLineEnd(text, unit.Imports[unit.Imports.Count - 1].Span.End);
        else if (unit.Options.Count > 0)
            insertAt = AfterLineEnd(text, unit.Options[unit.Options.Count - 1].Span.End);

        if (insertAt == text.Length && text.Length > 0 && !text.EndsWith('\n'))
            return text + newLine + "Imports " + ns + newLine;

        return text.Insert(insertAt, "Imports " + ns + newLine);
    }

    private static int AfterLineEnd(string text, int position)
    {
        var lineEnd = text.IndexOf('\n', position);
        return lineEnd < 0 ? text.Length : lineEnd + 1;
    }
}
