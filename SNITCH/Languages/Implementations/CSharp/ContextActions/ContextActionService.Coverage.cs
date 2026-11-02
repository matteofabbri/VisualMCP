using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The census tail: the ReSharper context actions that were still absent from the catalogue and are a
/// deterministic edit over one document — argument and tuple names, switch-section braces, numeric literal
/// bases, target-typed <c>new</c>, default expressions, relational/ternary shapes, documentation derived
/// from the base member or from the exceptions a body throws, region removal, Environment.NewLine,
/// params and await foreach. Each is previewed, compiler-gated and recorded like every other action.
/// </summary>
public sealed partial class ContextActionService
{
    // ── Argument and tuple names ───────────────────────────────────────────────

    /// <summary>ReSharper "Add argument name": a positional argument on the line becomes named.</summary>
    private Outcome AddArgumentName(Target target, string? text)
    {
        var argument = target.Root.DescendantNodes().OfType<ArgumentSyntax>()
            .Where(candidate => candidate.NameColon is null && OnSelectedLine(target, candidate))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (argument is null) return Outcome.Fail($"No positional argument on line {target.Line}.");

        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var list = argument.Parent as ArgumentListSyntax;
            var owner = list?.Parent;
            var method = owner is null ? null : target.Model.GetSymbolInfo(owner).Symbol as IMethodSymbol;
            var index = list?.Arguments.IndexOf(argument) ?? -1;

            if (method is null || index < 0 || index >= method.Parameters.Length)
                return Outcome.Fail("Pass the parameter name in text: the invocation could not be resolved.");

            name = method.Parameters[index].Name;
        }

        var edited = target.Text.ToString().Insert(argument.SpanStart, name + ": ");
        return new Outcome(edited, [$"argument → '{name}: …'"]);
    }

    /// <summary>ReSharper "Add tuple component name": the tuple type on the line gets element names.</summary>
    private Outcome AddTupleComponentName(Target target, string? text)
    {
        var tuple = target.Root.DescendantNodes().OfType<TupleTypeSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (tuple is null) return Outcome.Fail($"No tuple type on line {target.Line}.");
        if (tuple.Elements.Any(element => element.Identifier.ValueText.Length > 0))
            return Outcome.Fail("The tuple already names at least one component.");

        var names = string.IsNullOrWhiteSpace(text)
            ? tuple.Elements.Select((_, index) => "item" + (index + 1)).ToList()
            : text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        if (names.Count != tuple.Elements.Count)
            return Outcome.Fail($"{tuple.Elements.Count} component(s) need {tuple.Elements.Count} name(s), got {names.Count}.");
        if (names.Any(name => !SyntaxFacts.IsValidIdentifier(name)))
            return Outcome.Fail("Every tuple component name must be a C# identifier.");

        var replacement = "(" + string.Join(", ", tuple.Elements.Select((element, index) =>
            $"{element.Type} {names[index]}")) + ")";

        var file = target.Text.ToString();
        return new Outcome(file[..tuple.SpanStart] + replacement + file[tuple.Span.End..],
            [$"named {names.Count} tuple component(s)"]);
    }

    /// <summary>ReSharper "Remove tuple component name": the tuple type loses its element names.</summary>
    private Outcome RemoveTupleComponentName(Target target)
    {
        var tuple = target.Root.DescendantNodes().OfType<TupleTypeSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (tuple is null) return Outcome.Fail($"No tuple type on line {target.Line}.");
        if (tuple.Elements.All(element => element.Identifier.ValueText.Length == 0))
            return Outcome.Fail("The tuple has no component names to remove.");

        var replacement = "(" + string.Join(", ", tuple.Elements.Select(element => element.Type.ToString())) + ")";
        var file = target.Text.ToString();
        return new Outcome(file[..tuple.SpanStart] + replacement + file[tuple.Span.End..],
            ["removed the tuple component names"]);
    }

    /// <summary>ReSharper "Add switch section braces": the section on the line gets a block.</summary>
    private Outcome AddSwitchSectionBraces(Target target)
    {
        var section = target.Root.DescendantNodes().OfType<SwitchSectionSyntax>()
            .FirstOrDefault(candidate => LineOf(candidate) <= target.ToLine && EndLine(candidate) >= target.Line);

        if (section is null) return Outcome.Fail($"Line {target.Line} is not inside a switch section.");
        if (section.Statements.Count == 1 && section.Statements[0] is BlockSyntax)
            return Outcome.Fail("The section is already a block.");

        var block = SyntaxFactory.Block(section.Statements).WithAdditionalAnnotations(Formatter.Annotation);
        var updated = section.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(block));
        var root = Formatter.Format(target.Root.ReplaceNode(section, updated), target.Document.Project.Solution.Workspace);

        return new Outcome(root.ToFullString(), ["the switch section statements are now a block"]);
    }

    // ── Literals ──────────────────────────────────────────────────────────────

    private Outcome ConvertLiteralToHex(Target target) => ConvertLiteralBase(target, 16);

    private Outcome ConvertLiteralToBinary(Target target) => ConvertLiteralBase(target, 2);

    private Outcome ConvertLiteralToDecimal(Target target) => ConvertLiteralBase(target, 10);

    private Outcome ConvertLiteralBase(Target target, int toBase)
    {
        var token = target.Root.DescendantTokens()
            .FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.NumericLiteralToken) && LineOf(candidate) == target.Line);

        if (token.Text is null or "") return Outcome.Fail($"No numeric literal on line {target.Line}.");

        var raw = token.Text;
        var suffixStart = raw.Length;
        while (suffixStart > 0 && raw[suffixStart - 1] is 'u' or 'U' or 'l' or 'L') suffixStart--;
        var suffix = raw[suffixStart..];
        var body = raw[..suffixStart].Replace("_", string.Empty, StringComparison.Ordinal);

        var (digits, fromBase) = body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (body[2..], 16)
            : body.StartsWith("0b", StringComparison.OrdinalIgnoreCase)
                ? (body[2..], 2)
                : (body, 10);

        if (fromBase == toBase)
            return Outcome.Fail($"'{raw}' is already base {toBase}.");

        ulong value;
        try { value = Convert.ToUInt64(digits, fromBase); }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            return Outcome.Fail($"'{raw}' is not an integer this action can rebase.");
        }

        var converted = toBase switch
        {
            16 => "0x" + value.ToString("x", System.Globalization.CultureInfo.InvariantCulture),
            2 => "0b" + BinaryString(value),
            _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var file = target.Text.ToString();
        return new Outcome(file[..token.SpanStart] + converted + suffix + file[token.Span.End..],
            [$"'{raw}' → '{converted}{suffix}'"]);
    }

    // ── Object creation and default ───────────────────────────────────────────

    /// <summary>ReSharper "Use target-typed new": <c>new T(…)</c> becomes <c>new(…)</c>.</summary>
    private Outcome UseTargetTypedNew(Target target)
    {
        var creation = target.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (creation is null) return Outcome.Fail($"No 'new T(…)' expression on line {target.Line}.");

        var replacement = creation.ArgumentList is null ? "new()" : "new";
        var file = target.Text.ToString();
        return new Outcome(
            file[..creation.NewKeyword.SpanStart] + replacement + file[creation.Type.Span.End..],
            ["explicit 'new T' → target-typed 'new'"]);
    }

    /// <summary>ReSharper "Use explicitly typed new": <c>new(…)</c> becomes <c>new T(…)</c>.</summary>
    private Outcome UseExplicitlyTypedNew(Target target)
    {
        var creation = target.Root.DescendantNodes().OfType<ImplicitObjectCreationExpressionSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (creation is null) return Outcome.Fail($"No target-typed 'new(…)' expression on line {target.Line}.");

        var type = target.Model.GetTypeInfo(creation).Type;
        if (type is null || type.TypeKind == TypeKind.Error || type.IsAnonymousType)
            return Outcome.Fail("The compiler could not name the type of this 'new'.");

        var name = type.ToMinimalDisplayString(target.Model, creation.SpanStart);
        var file = target.Text.ToString();
        return new Outcome(
            file[..creation.NewKeyword.Span.End] + " " + name + file[creation.NewKeyword.Span.End..],
            [$"'new(…)' → 'new {name}(…)'"]);
    }

    /// <summary>ReSharper "Convert to default expression": <c>default(T)</c> becomes <c>default</c>.</summary>
    private Outcome ConvertToDefaultExpression(Target target)
    {
        var expression = target.Root.DescendantNodes().OfType<DefaultExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.Type is not null && OnSelectedLine(target, candidate));

        if (expression is null) return Outcome.Fail($"No 'default(T)' on line {target.Line}.");

        var file = target.Text.ToString();
        return new Outcome(file[..expression.SpanStart] + "default" + file[expression.Span.End..],
            [$"'default({expression.Type})' → default"]);
    }

    // ── Conditions and ternaries ──────────────────────────────────────────────

    /// <summary>ReSharper "Flip relational expression": <c>a &lt; b</c> becomes <c>b &gt; a</c>.</summary>
    private Outcome FlipRelationalExpression(Target target)
    {
        var binary = target.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate) && candidate.Kind() is
                SyntaxKind.LessThanExpression or SyntaxKind.GreaterThanExpression or
                SyntaxKind.LessThanOrEqualExpression or SyntaxKind.GreaterThanOrEqualExpression);

        if (binary is null) return Outcome.Fail($"No relational expression on line {target.Line}.");

        var flipped = binary.Kind() switch
        {
            SyntaxKind.LessThanExpression => ">",
            SyntaxKind.GreaterThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => ">=",
            _ => "<=",
        };

        var replacement = $"{Operand(binary.Right)} {flipped} {Operand(binary.Left)}";
        var file = target.Text.ToString();
        return new Outcome(file[..binary.SpanStart] + replacement + file[binary.Span.End..],
            [$"'{binary}' → '{replacement}'"]);
    }

    /// <summary>ReSharper "Invert ternary condition": <c>c ? a : b</c> becomes <c>!c ? b : a</c>.</summary>
    private Outcome InvertTernaryCondition(Target target)
    {
        var ternary = target.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (ternary is null) return Outcome.Fail($"No ternary expression on line {target.Line}.");

        var condition = Negate(ternary.Condition);
        var replacement = $"{condition} ? {ternary.WhenFalse} : {ternary.WhenTrue}";
        var file = target.Text.ToString();
        return new Outcome(file[..ternary.SpanStart] + replacement + file[ternary.Span.End..],
            [$"'{ternary}' → '{replacement}'"]);
    }

    /// <summary>ReSharper "If to ternary": an if/else of two returns or two assignments.</summary>
    private Outcome IfToTernary(Target target)
    {
        var statement = target.Root.DescendantNodes().OfType<IfStatementSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate) &&
                                         candidate.Else is not null &&
                                         candidate.Else.Statement is not IfStatementSyntax);

        if (statement?.Else is null) return Outcome.Fail($"No if/else (without else-if) on line {target.Line}.");

        var whenTrue = SingleStatement(statement.Statement);
        var whenFalse = SingleStatement(statement.Else.Statement);
        if (whenTrue is null || whenFalse is null)
            return Outcome.Fail("Both branches must be a single statement.");

        var condition = statement.Condition.ToString();
        string replacement;

        if (whenTrue is ReturnStatementSyntax { Expression: { } trueReturn } &&
            whenFalse is ReturnStatementSyntax { Expression: { } falseReturn })
        {
            replacement = $"return {condition} ? {trueReturn} : {falseReturn};";
        }
        else if (whenTrue is ExpressionStatementSyntax
                 {
                     Expression: AssignmentExpressionSyntax
                     {
                         RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                         Left: { } trueLeft,
                         Right: { } trueRight,
                     }
                 } &&
                 whenFalse is ExpressionStatementSyntax
                 {
                     Expression: AssignmentExpressionSyntax
                     {
                         RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                         Left: { } falseLeft,
                         Right: { } falseRight,
                     }
                 } &&
                 trueLeft.ToString() == falseLeft.ToString())
        {
            replacement = $"{trueLeft} = {condition} ? {trueRight} : {falseRight};";
        }
        else
        {
            return Outcome.Fail("The branches are not two returns or two assignments to the same target.");
        }

        var file = target.Text.ToString();
        return new Outcome(file[..statement.SpanStart] + replacement + file[statement.Span.End..],
            ["if/else → ternary expression"]);
    }

    // ── Documentation ─────────────────────────────────────────────────────────

    /// <summary>ReSharper "Add inheritdoc": <c>/// &lt;inheritdoc/&gt;</c> on the member.</summary>
    private Outcome AddInheritdoc(Target target)
    {
        var member = MemberOnLine(target);
        if (member is null) return Outcome.Fail($"No member declaration on line {target.Line}.");
        if (HasDocComment(target, LineOf(member)))
            return Outcome.Fail("The member already has a documentation comment.");

        var indentation = IndentationAt(target.Text, LineOf(member));
        var insertion = indentation + "/// <inheritdoc/>" + NewLineOf(target.Text.ToString());
        return new Outcome(target.Text.ToString().Insert(LineStart(target.Text.ToString(), LineOf(member)), insertion),
            ["inserts /// <inheritdoc/> above the member"]);
    }

    /// <summary>ReSharper "Add exception documentation": one &lt;exception&gt; per type the body throws.</summary>
    private Outcome AddExceptionDocs(Target target)
    {
        var member = MemberOnLine(target);
        if (member is null) return Outcome.Fail($"No member declaration on line {target.Line}.");

        var thrown = member.DescendantNodes()
            .Select(node => node switch
            {
                ThrowStatementSyntax statement => statement.Expression,
                ThrowExpressionSyntax expression => expression.Expression,
                _ => null,
            })
            .Where(expression => expression is not null)
            .Select(expression => target.Model.GetTypeInfo(expression!).Type)
            .Where(type => type is not null && type.TypeKind != TypeKind.Error && !type.IsAnonymousType)
            .Select(type => type!)
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();

        if (thrown.Count == 0) return Outcome.Fail("The member throws no exception the compiler can resolve.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(member));
        var lines = thrown.Select(type =>
            $"{indentation}/// <exception cref=\"{CrefName(type)}\">Thrown when the operation cannot complete.</exception>");

        var text = target.Text.ToString();
        var insertAt = LineStart(text, LineOf(member));
        return new Outcome(text.Insert(insertAt, string.Join(newLine, lines) + newLine),
            [$"documents {thrown.Count} thrown exception type(s)"]);
    }

    // ── File and token edits ──────────────────────────────────────────────────

    /// <summary>ReSharper "Remove region": the #region/#endregion pair around the line.</summary>
    private Outcome RemoveRegion(Target target)
    {
        var text = target.Text.ToString();
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var stack = new Stack<(int Line, string Name)>();
        var candidates = new List<(int Start, int End)>();

        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("#region", StringComparison.Ordinal))
                stack.Push((index + 1, trimmed["#region".Length..].Trim()));
            else if (trimmed.StartsWith("#endregion", StringComparison.Ordinal) && stack.Count > 0)
                candidates.Add((stack.Pop().Line, index + 1));
        }

        var match = candidates
            .Where(candidate => candidate.Start <= target.Line && candidate.End >= target.Line)
            .OrderBy(candidate => candidate.End - candidate.Start)
            .FirstOrDefault();

        if (match.Start == 0) return Outcome.Fail($"Line {target.Line} is not inside a #region.");

        var endStart = LineStart(text, match.End);
        var endLength = Math.Min(LineEnd(text, match.End), text.Length) - endStart;
        var edited = text.Remove(endStart, endLength);

        var startStart = LineStart(edited, match.Start);
        var startLength = Math.Min(LineEnd(edited, match.Start), edited.Length) - startStart;
        edited = edited.Remove(startStart, startLength);

        return new Outcome(edited, [$"removed the region at lines {match.Start}-{match.End}"]);
    }

    /// <summary>ReSharper "Use Environment.NewLine": a "\n" or "\r\n" literal becomes Environment.NewLine.</summary>
    private Outcome UseEnvironmentNewLine(Target target)
    {
        var literal = target.Root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.StringLiteralExpression) &&
                                         OnSelectedLine(target, candidate) &&
                                         candidate.Token.ValueText is "\n" or "\r\n");

        if (literal is null)
            return Outcome.Fail($"No \"\\n\" or \"\\r\\n\" string literal on line {target.Line}.");

        var file = target.Text.ToString();
        var edited = file[..literal.SpanStart] + "Environment.NewLine" + file[literal.Span.End..];

        if (target.Root is CompilationUnitSyntax unit)
        {
            var needed = MissingNamespaces(target, literal.SpanStart, "System");
            if (needed.Count > 0) edited = AddUsings(edited, unit, needed);
        }

        return new Outcome(edited, ["newline literal → Environment.NewLine"]);
    }

    /// <summary>ReSharper "Remove params modifier": the last array parameter loses <c>params</c>.</summary>
    private Outcome RemoveParamsModifier(Target target)
    {
        var parameter = LastParameterOnLine(target);
        if (parameter is null) return Outcome.Fail($"No parameter list on line {target.Line}.");

        var modifier = parameter.Modifiers.FirstOrDefault(candidate => candidate.IsKind(SyntaxKind.ParamsKeyword));
        if (modifier.RawKind == 0) return Outcome.Fail("The last parameter is not a 'params' parameter.");

        var text = target.Text.ToString();
        var end = modifier.Span.End;
        if (end < text.Length && text[end] == ' ') end++;
        return new Outcome(text.Remove(modifier.SpanStart, end - modifier.SpanStart),
            ["removed the params modifier"]);
    }

    /// <summary>ReSharper "Set params modifier": the last array parameter gains <c>params</c>.</summary>
    private Outcome SetParamsModifier(Target target)
    {
        var parameter = LastParameterOnLine(target);
        if (parameter is null) return Outcome.Fail($"No parameter list on line {target.Line}.");
        if (parameter.Type is not ArrayTypeSyntax)
            return Outcome.Fail("The last parameter is not an array: only an array can be a params parameter.");
        if (parameter.Modifiers.Any(candidate => candidate.IsKind(SyntaxKind.ParamsKeyword)))
            return Outcome.Fail("The last parameter is already 'params'.");

        var text = target.Text.ToString();
        return new Outcome(text.Insert(parameter.Type.SpanStart, "params "),
            ["added the params modifier"]);
    }

    // ── Await foreach ─────────────────────────────────────────────────────────

    /// <summary>ReSharper "Iterate via await foreach": a foreach over an async sequence.</summary>
    private Outcome AwaitForeach(Target target)
    {
        var loop = target.Root.DescendantNodes().OfType<ForEachStatementSyntax>()
            .FirstOrDefault(candidate => OnSelectedLine(target, candidate));

        if (loop is null) return Outcome.Fail($"No foreach statement on line {target.Line}.");

        var type = target.Model.GetTypeInfo(loop.Expression).Type;
        var isAsync = type is not null &&
                      ((type is INamedTypeSymbol named &&
                        named.OriginalDefinition.ToDisplayString() ==
                        "System.Collections.Generic.IAsyncEnumerable<T>") ||
                       type.AllInterfaces.Any(@interface =>
                           @interface.OriginalDefinition.ToDisplayString() ==
                           "System.Collections.Generic.IAsyncEnumerable<T>") ||
                       type.GetMembers("GetAsyncEnumerator").OfType<IMethodSymbol>().Any());

        if (!isAsync)
            return Outcome.Fail($"'{type?.ToDisplayString() ?? loop.Expression.ToString()}' is not an async sequence.");

        var file = target.Text.ToString();
        return new Outcome(
            file[..loop.ForEachKeyword.SpanStart] + "await foreach" + file[loop.ForEachKeyword.Span.End..],
            ["foreach over an async sequence → await foreach"]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MemberDeclarationSyntax? MemberOnLine(Target target) =>
        target.Root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(member => member is MethodDeclarationSyntax or PropertyDeclarationSyntax
                or EventDeclarationSyntax or IndexerDeclarationSyntax or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax)
            .Where(member => LineOf(member) == target.Line)
            .OrderBy(member => member.Span.Length)
            .FirstOrDefault();

    private static ParameterSyntax? LastParameterOnLine(Target target)
    {
        var list = target.Root.DescendantNodes().OfType<ParameterListSyntax>()
            .FirstOrDefault(candidate => LineOf(candidate) <= target.Line && EndLine(candidate) >= target.Line);

        return list?.Parameters.LastOrDefault();
    }

    private static bool HasDocComment(Target target, int memberLine) =>
        memberLine > 1 && target.Text.Lines[memberLine - 2].ToString().TrimStart().StartsWith("///", StringComparison.Ordinal);

    private static string CrefName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace('<', '{').Replace('>', '}');

    private static string Operand(ExpressionSyntax expression) =>
        expression is BinaryExpressionSyntax or ConditionalExpressionSyntax or AssignmentExpressionSyntax
            ? "(" + expression + ")"
            : expression.ToString();

    private static string BinaryString(ulong value)
    {
        if (value == 0) return "0";

        var digits = new char[64];
        var index = digits.Length;
        while (value > 0)
        {
            digits[--index] = (char)('0' + (int)(value & 1));
            value >>= 1;
        }

        return new string(digits, index, digits.Length - index);
    }

    private static string Negate(ExpressionSyntax condition) =>
        condition is IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax or LiteralExpressionSyntax
            ? "!" + condition
            : "!(" + condition + ")";
}
