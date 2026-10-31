using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Languages.VB.ContextActions;

public sealed partial class VbContextActionService
{
    private enum Guard
    {
        Null,
        NotNullOrEmpty,
        CollectionNotEmpty,
    }

    /// <summary>
    /// ReSharper's argument guards: the check goes at the top of the method body, for the parameter named
    /// in <c>text</c> or for every eligible parameter.
    /// </summary>
    private Outcome ArgumentGuards(Target target, string? text, Guard guard, bool all)
    {
        var method = EnclosingMethod(target);
        if (method is null)
            return Outcome.Fail($"Line {target.Line} is not inside a method, constructor or property accessor.");
        if (target.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
            return Outcome.Fail($"Could not resolve the declaration that contains line {target.Line}.");

        var requested = text?.Trim();
        var eligible = symbol.Parameters.Where(parameter => IsEligible(parameter, guard)).ToList();

        List<IParameterSymbol> chosen;
        if (all)
        {
            chosen = eligible;
            if (chosen.Count == 0)
                return Outcome.Fail($"'{symbol.Name}' has no parameter this guard applies to.");
        }
        else if (requested is not null)
        {
            var match = symbol.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, requested, StringComparison.Ordinal));
            if (match is null)
                return Outcome.Fail($"'{symbol.Name}' has no parameter named '{requested}'.");
            if (!IsEligible(match, guard))
                return Outcome.Fail(Reason(match, guard));
            chosen = [match];
        }
        else
        {
            if (eligible.Count == 0)
                return Outcome.Fail($"'{symbol.Name}' has no parameter this guard applies to.");
            if (eligible.Count > 1)
                return Outcome.Fail($"'{symbol.Name}' has {eligible.Count} eligible parameters: name one in text.");
            chosen = [eligible[0]];
        }

        var newLine = NewLineOf(target.Text.ToString());
        var lines = string.Join(newLine, chosen.Select(parameter => Build(parameter, guard)));
        return new Outcome(InsertAfterHeader(target, method, lines),
            [$"guarded {string.Join(", ", chosen.Select(parameter => parameter.Name))}"]);
    }

    private static bool IsEligible(IParameterSymbol parameter, Guard guard) => guard switch
    {
        Guard.Null => parameter.Type.IsReferenceType && !parameter.IsParams,
        Guard.NotNullOrEmpty => parameter.Type.SpecialType == SpecialType.System_String,
        Guard.CollectionNotEmpty => parameter.Type.IsReferenceType && CountMember(parameter.Type) is not null,
        _ => false,
    };

    private static string Reason(IParameterSymbol parameter, Guard guard) => guard switch
    {
        Guard.Null => $"'{parameter.Name}' is a value type: it cannot be Nothing.",
        Guard.NotNullOrEmpty => $"'{parameter.Name}' is not a String.",
        Guard.CollectionNotEmpty => $"'{parameter.Name}' is not a collection with Count or Length.",
        _ => $"'{parameter.Name}' cannot be guarded by this action.",
    };

    private static string Build(IParameterSymbol parameter, Guard guard)
    {
        var name = parameter.Name;
        return guard switch
        {
            Guard.Null =>
                $"If {name} Is Nothing Then Throw New ArgumentNullException(NameOf({name}))",
            Guard.NotNullOrEmpty =>
                $"If String.IsNullOrEmpty({name}) Then Throw New ArgumentException(\"Value cannot be null or empty.\", NameOf({name}))",
            _ => $"If {name} Is Nothing OrElse {name}.{CountMember(parameter.Type)} = 0 Then " +
                 $"Throw New ArgumentException(\"Value cannot be null or empty.\", NameOf({name}))",
        };
    }

    /// <summary>"Check variable for Nothing": the guard goes right after the local declaration on the line.</summary>
    private Outcome CheckVariableNull(Target target, string? text)
    {
        var declaration = LocalDeclarationOnLine(target);
        if (declaration is null) return Outcome.Fail($"No local declaration on line {target.Line}.");
        if (declaration.Declarators.Count != 1 || declaration.Declarators[0].Names.Count != 1)
            return Outcome.Fail("The declaration names several variables; split it first (split_declaration).");

        var declarator = declaration.Declarators[0];
        var name = declarator.Names[0].Identifier.ValueText;
        var requested = text?.Trim();
        if (requested is not null && !string.Equals(requested, name, StringComparison.Ordinal))
            return Outcome.Fail($"The declaration on line {target.Line} declares '{name}', not '{requested}'.");

        var type = target.Model.GetDeclaredSymbol(declarator.Names[0]) is ILocalSymbol local
            ? local.Type
            : declarator.Initializer is null ? null : target.Model.GetTypeInfo(declarator.Initializer.Value).Type;

        if (type is not null && !IsReference(type))
            return Outcome.Fail($"'{name}' is a value type: it cannot be Nothing.");

        var text0 = target.Text.ToString();
        var newLine = NewLineOf(text0);
        var indent = IndentationAt(target.Text, LineOf(declaration));
        var guard = $"If {name} Is Nothing Then Throw New InvalidOperationException(\"The value cannot be null.\")";

        var lineEnd = text0.IndexOf('\n', declaration.Span.End);
        var insertAt = lineEnd < 0 ? text0.Length : lineEnd + 1;
        return new Outcome(text0.Insert(insertAt, indent + guard + newLine), [$"guarded {name} after its declaration"]);
    }

    /// <summary>
    /// "Check dictionary key": a <c>ContainsKey</c> guard before the statement that indexes a dictionary
    /// with the key named in <c>text</c> (or with the only key on the line).
    /// </summary>
    private Outcome CheckDictionaryKey(Target target, string? text)
    {
        var key = text?.Trim();
        var access = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line &&
                                candidate.ArgumentList is { Arguments.Count: 1 })
            .Where(candidate => key is null ||
                                candidate.ArgumentList.Arguments[0].GetExpression()?.ToString() == key)
            .Where(candidate => target.Model.GetSymbolInfo(candidate).Symbol is IPropertySymbol { IsIndexer: true } indexer &&
                                indexer.ContainingType.GetMembers("ContainsKey").OfType<IMethodSymbol>()
                                    .Any(method => method.Parameters.Length == 1))
            .OrderBy(candidate => candidate.Span.Length)
            .FirstOrDefault();

        if (access is null)
            return Outcome.Fail($"No dictionary indexer on line {target.Line} (the action needs a type with ContainsKey): pass the key expression in text.");

        var keyExpression = access.ArgumentList.Arguments[0].GetExpression()!.ToString();
        var receiver = access.Expression.ToString();
        var statement = access.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is null) return Outcome.Fail("The indexer is not inside a statement.");

        var guard = $"If Not {receiver}.ContainsKey({keyExpression}) Then " +
                    "Throw New KeyNotFoundException(\"The dictionary does not contain the requested key.\")";

        var edited = InsertBeforeStatement(target, statement, guard, out _,
            needsImport: !IsSymbolVisible(target.Model, statement.SpanStart, "KeyNotFoundException"),
            importNamespace: "System.Collections.Generic");

        return new Outcome(edited, [$"guarded {receiver}({keyExpression}) against a missing key"]);
    }

    /// <summary>
    /// "Check enum value is defined": an <c>[Enum].IsDefined</c> guard at the top of the method for the
    /// enum parameter named in <c>text</c> (or the only enum parameter).
    /// </summary>
    private Outcome CheckEnumDefined(Target target, string? text)
    {
        var method = EnclosingMethod(target);
        if (method is null) return Outcome.Fail($"Line {target.Line} is not inside a method or constructor.");
        if (target.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
            return Outcome.Fail($"Could not resolve the declaration that contains line {target.Line}.");

        var requested = text?.Trim();
        var candidates = symbol.Parameters.Where(parameter => parameter.Type.TypeKind == TypeKind.Enum).ToList();
        if (requested is not null)
            candidates = candidates.Where(parameter => string.Equals(parameter.Name, requested, StringComparison.Ordinal)).ToList();

        if (candidates.Count == 0)
            return Outcome.Fail(requested is null
                ? $"'{symbol.Name}' has no enum parameter to check."
                : $"'{symbol.Name}' has no enum parameter named '{requested}'.");
        if (candidates.Count > 1)
            return Outcome.Fail($"'{symbol.Name}' has {candidates.Count} enum parameters: name one in text.");

        var parameter = candidates[0];
        var typeName = parameter.Type.ToMinimalDisplayString(target.Model, method.BlockStatement.SpanStart);
        var name = parameter.Name;
        var guard = $"If Not [Enum].IsDefined(GetType({typeName}), {name}) Then " +
                    $"Throw New ArgumentOutOfRangeException(NameOf({name}), {name}, \"Unknown {typeName} value.\")";

        var text0 = target.Text.ToString();
        var edited = text0;
        if (!IsSymbolVisible(target.Model, method.BlockStatement.SpanStart, "ArgumentOutOfRangeException") &&
            target.Root is CompilationUnitSyntax unit)
            edited = AddImports(edited, unit, "System");

        var shift = edited.Length - text0.Length;
        var newLine = NewLineOf(text0);
        var indent = method.Statements.Count > 0
            ? IndentationAt(target.Text, LineOf(method.Statements[0]))
            : IndentationAt(target.Text, LineOf(method.BlockStatement)) + "    ";
        var lineEnd = edited.IndexOf('\n', method.BlockStatement.Span.End + shift);
        var insertAt = lineEnd < 0 ? edited.Length : lineEnd + 1;

        return new Outcome(edited.Insert(insertAt, indent + guard + newLine), [$"guarded {name} against an undefined {typeName}"]);
    }

    /// <summary>
    /// "Check index for out of range": a guard for the integral parameter used as the index on the line.
    /// The collection must expose Count or Length, which is what the bound is read from.
    /// </summary>
    private Outcome CheckIndexRange(Target target, string? text)
    {
        var requested = text?.Trim();
        var access = target.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(candidate => LineOf(candidate) == target.Line &&
                                candidate.ArgumentList is { Arguments.Count: 1 })
            .Select(candidate => (Invocation: candidate, Index: candidate.ArgumentList!.Arguments[0].GetExpression()))
            .Where(candidate => candidate.Index is IdentifierNameSyntax)
            .Where(candidate => target.Model.GetSymbolInfo(candidate.Index!).Symbol is IParameterSymbol parameter &&
                                IsIntegral(parameter.Type) &&
                                (requested is null || string.Equals(parameter.Name, requested, StringComparison.Ordinal)))
            .Where(candidate => CountMember(target.Model.GetTypeInfo(candidate.Invocation.Expression).Type) is not null)
            .OrderBy(candidate => candidate.Invocation.Span.Length)
            .FirstOrDefault();

        if (access.Invocation is null)
            return Outcome.Fail(requested is null
                ? $"No indexer call with an integral parameter on line {target.Line}."
                : $"No indexer on line {target.Line} is called with the parameter '{requested}'.");

        var index = access.Index!.ToString();
        var receiver = access.Invocation.Expression.ToString();
        var count = CountMember(target.Model.GetTypeInfo(access.Invocation.Expression).Type)!;
        var statement = access.Invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement is null) return Outcome.Fail("The indexer is not inside a statement.");

        var guard = $"If {index} < 0 OrElse {index} >= {receiver}.{count} Then " +
                    $"Throw New ArgumentOutOfRangeException(NameOf({index}))";

        var edited = InsertBeforeStatement(target, statement, guard, out _,
            needsImport: !IsSymbolVisible(target.Model, statement.SpanStart, "ArgumentOutOfRangeException"),
            importNamespace: "System");

        return new Outcome(edited, [$"guarded {index} against the bounds of {receiver}.{count}"]);
    }

    private static bool IsIntegral(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or
        SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 or
        SpecialType.System_Byte or SpecialType.System_SByte;

    /// <summary>Inserts the guard line (and the Imports it needs) before the whole statement.</summary>
    private static string InsertBeforeStatement(
        Target target, StatementSyntax statement, string guard, out int shift,
        bool needsImport, string importNamespace)
    {
        var text = target.Text.ToString();
        var edited = text;
        shift = 0;

        if (needsImport && target.Root is CompilationUnitSyntax unit)
        {
            edited = AddImports(edited, unit, importNamespace);
            shift = edited.Length - text.Length;
        }

        var newLine = NewLineOf(text);
        var indent = IndentationAt(target.Text, LineOf(statement));
        return edited.Insert(statement.SpanStart + shift, indent + guard + newLine);
    }
}
