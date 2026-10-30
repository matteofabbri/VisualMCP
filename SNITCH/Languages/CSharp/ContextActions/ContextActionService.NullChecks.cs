using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    private enum Guard
    {
        Null,
        NotNullOrEmpty,
        CollectionNotEmpty,
    }

    /// <summary>
    /// The argument-check family: a guard statement at the top of the method body for one parameter, all of
    /// them, the string parameters, or the collection parameters. A parameter that already carries a guard
    /// is skipped; a body whose guards would not compile (missing <c>System</c>, an unnameable parameter) is
    /// refused by the compiler gate like any other edit.
    /// </summary>
    private Outcome ArgumentGuards(Target target, string? text, Guard guard, bool all)
    {
        var lineStart = target.Text.Lines[target.Line - 1].Start;
        var method = target.Root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .Where(candidate => candidate.FullSpan.Contains(lineStart))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (method is null)
            return Outcome.Fail($"Line {target.Line} is not inside a method or constructor.");
        if (method.Body is null)
            return Outcome.Fail("The method is expression-bodied: give it a block body first, then retry.");
        if (target.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
            return Outcome.Fail($"Could not resolve the declaration that contains line {target.Line}.");

        var candidates = symbol.Parameters.Where(parameter => parameter.Type.IsReferenceType).ToList();
        if (guard == Guard.NotNullOrEmpty)
            candidates = candidates.Where(parameter => parameter.Type.SpecialType == SpecialType.System_String).ToList();
        if (guard == Guard.CollectionNotEmpty)
            candidates = candidates.Where(parameter => IsCollection(parameter.Type)).ToList();

        if (!all)
        {
            var name = text?.Trim();
            if (string.IsNullOrEmpty(name))
                return Outcome.Fail($"{Describe(guard)} needs the parameter name in text.");

            candidates = candidates.Where(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0)
                return Outcome.Fail($"'{symbol.Name}' has no matching reference-type parameter named '{name}'.");
        }
        else if (candidates.Count == 0)
        {
            return Outcome.Fail($"'{symbol.Name}' has no reference-type parameter to check.");
        }

        var bodyText = method.Body.ToString();
        var pending = candidates.Where(parameter => !AlreadyGuarded(bodyText, parameter.Name)).ToList();
        if (pending.Count == 0)
            return Outcome.Fail("Every selected parameter already carries a null guard.");

        var needsLinq = false;
        var guards = new List<string>();
        foreach (var parameter in pending)
        {
            switch (guard)
            {
                case Guard.Null:
                    guards.Add($"if ({parameter.Name} is null) throw new ArgumentNullException(nameof({parameter.Name}));");
                    break;

                case Guard.NotNullOrEmpty:
                    guards.Add($"if (string.IsNullOrEmpty({parameter.Name})) throw new ArgumentException(\"Value cannot be null or empty.\", nameof({parameter.Name}));");
                    break;

                case Guard.CollectionNotEmpty:
                    var condition = CollectionCondition(parameter, ref needsLinq);
                    guards.Add($"if ({condition}) throw new ArgumentException(\"Collection cannot be empty.\", nameof({parameter.Name}));");
                    break;
            }
        }

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(method));
        var inner = indentation + "    ";
        var open = method.Body.OpenBraceToken.Span.End;

        var edited = method.Body.Statements.Count == 0
            ? target.Text.ToString().Insert(
                open,
                string.Concat(guards.Select(guardLine => newLine + inner + guardLine)) + newLine + indentation)
            : target.Text.ToString().Insert(
                open,
                string.Concat(guards.Select(guardLine => newLine + inner + guardLine)));

        var needed = MissingNamespaces(
            target,
            method.SpanStart,
            needsLinq ? ["System", "System.Linq"] : ["System"]);
        if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
            edited = AddUsings(edited, unit, needed);

        return new Outcome(edited, guards);
    }

    /// <summary>ReSharper's "Check variable for null": a guard right after the statement that declares it.</summary>
    private Outcome CheckVariableNull(Target target, string? text)
    {
        var requested = text?.Trim();
        var declarator = target.Root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(candidate => requested is null
                ? LineOf(candidate) == target.Line
                : string.Equals(candidate.Identifier.ValueText, requested, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (declarator is null)
            return Outcome.Fail($"No variable declaration on line {target.Line}: pass the variable name in text.");

        ITypeSymbol? type = target.Model.GetDeclaredSymbol(declarator) switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        if (type is null || !type.IsReferenceType)
            return Outcome.Fail($"'{declarator.Identifier.ValueText}' is not a reference-type variable.");

        if (declarator.FirstAncestorOrSelf<StatementSyntax>() is not { } statement)
            return Outcome.Fail($"Could not find the statement that declares '{declarator.Identifier.ValueText}'.");

        var newLine = NewLineOf(target.Text.ToString());
        var indentation = IndentationAt(target.Text, LineOf(statement));
        var name = declarator.Identifier.ValueText;
        var guardLine = $"if ({name} is null) throw new InvalidOperationException(\"{name} is null.\");";

        var edited = target.Text.ToString().Insert(statement.Span.End, newLine + indentation + guardLine);

        var needed = MissingNamespaces(target, statement.SpanStart, "System");
        if (needed.Count > 0 && target.Root is CompilationUnitSyntax unit)
            edited = AddUsings(edited, unit, needed);

        return new Outcome(edited, [guardLine]);
    }

    private static bool IsCollection(ITypeSymbol type) =>
        type.SpecialType != SpecialType.System_String &&
        type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable);

    /// <summary><c>x is null || x.Count == 0</c> when the type has Count, <c>!x.Any()</c> otherwise.</summary>
    private static string CollectionCondition(IParameterSymbol parameter, ref bool needsLinq)
    {
        var name = parameter.Name;
        if (parameter.Type is IArrayTypeSymbol)
            return $"{name} is null || {name}.Length == 0";

        if (parameter.Type.GetMembers("Count").OfType<IPropertySymbol>().Any())
            return $"{name} is null || {name}.Count == 0";

        needsLinq = true;
        return $"{name} is null || !{name}.Any()";
    }

    /// <summary>Whether the body already mentions this parameter in a guard (nameof or a null test).</summary>
    private static bool AlreadyGuarded(string bodyText, string name) =>
        bodyText.Contains($"nameof({name})", StringComparison.Ordinal) ||
        bodyText.Contains($"{name} is null", StringComparison.Ordinal) ||
        bodyText.Contains($"{name} == null", StringComparison.Ordinal);

    private static string Describe(Guard guard) => guard switch
    {
        Guard.Null => "check_param_null",
        Guard.NotNullOrEmpty => "check_string_not_null_or_empty",
        Guard.CollectionNotEmpty => "check_collection_not_empty",
        _ => "the action",
    };
}
