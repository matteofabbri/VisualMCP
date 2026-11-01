using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

// ── language details ────────────────────────────────────────────────────────
/// <summary>IDE0033: <c>tuple.Item1</c> → <c>tuple.Name</c> when the tuple declares that name.</summary>
public sealed class TupleElementNameRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0033", "Use explicitly provided tuple name", "tuple.Item1 → tuple.Name");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;

        var name = memberAccess.Name.Identifier.ValueText;
        if (!name.StartsWith("Item", StringComparison.Ordinal)) return null;
        if (!int.TryParse(name["Item".Length..], out var index) || index < 1) return null;

        if (model.GetTypeInfo(memberAccess.Expression, cancellationToken).Type is not INamedTypeSymbol { IsTupleType: true } tuple)
            return null;

        var elements = tuple.TupleElements;
        if (index > elements.Length) return null;

        var explicitName = elements[index - 1].Name;
        if (string.IsNullOrEmpty(explicitName) || explicitName == name) return null;

        return Match(node, memberAccess.WithName(IdentifierName(explicitName).WithTriviaFrom(memberAccess.Name)));
    }
}

/// <summary>IDE0037: <c>new { Name = Name }</c> → <c>new { Name }</c>, same for tuple elements.</summary>
public sealed class InferredMemberNameRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0037", "Use inferred member name", "new { Name = Name } → new { Name }");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case AnonymousObjectMemberDeclaratorSyntax declarator when declarator.NameEquals is not null:
            {
                if (declarator.Expression is not IdentifierNameSyntax identifier) return null;
                if (declarator.NameEquals.Name.Identifier.ValueText != identifier.Identifier.ValueText) return null;

                return Match(node, declarator.WithNameEquals(null).WithTriviaFrom(declarator));
            }

            case ArgumentSyntax { NameColon: not null } argument when argument.Expression is IdentifierNameSyntax identifier:
            {
                if (argument.NameColon!.Name.Identifier.ValueText != identifier.Identifier.ValueText) return null;

                return Match(node, argument.WithNameColon(null).WithTriviaFrom(argument));
            }

            default:
                return null;
        }
    }
}

/// <summary>IDE0082: <c>typeof(X).Name</c> → <c>nameof(X)</c>.</summary>
public sealed class TypeofToNameofRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0082", "Convert 'typeof' to 'nameof'", "typeof(X).Name → nameof(X)");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax memberAccess) return null;
        if (memberAccess.Expression is not TypeOfExpressionSyntax typeOf) return null;
        if (memberAccess.Name.Identifier.ValueText != "Name") return null;

        if (model.GetSymbolInfo(memberAccess, cancellationToken).Symbol is not IPropertySymbol property) return null;
        if (property.Name != "Name") return null;

        // Name is declared on MemberInfo, which Type inherits: the receiver is the thing to check.
        if (model.GetTypeInfo(memberAccess.Expression, cancellationToken).Type?.ToDisplayString() != "System.Type") return null;

        // nameof(string) does not compile (CS1525): a keyword type has to be written qualified, and
        // typeof(string).Name is "String" either way.
        var typeSymbol = model.GetTypeInfo(typeOf.Type, cancellationToken).Type as INamedTypeSymbol;
        var name = typeOf.Type is PredefinedTypeSyntax && typeSymbol is not null
            ? $"{typeSymbol.ContainingNamespace.ToDisplayString()}.{typeSymbol.Name}"
            : typeOf.Type.ToString();

        return Match(node, $"nameof({name})");
    }
}

/// <summary>IDE0150: <c>x is object</c> → <c>x != null</c> for a reference type.</summary>
public sealed class NullCheckOverTypeCheckRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0150", "Prefer 'null' check over type check", "x is object → x != null");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not BinaryExpressionSyntax isExpression) return null;
        if (!isExpression.IsKind(SyntaxKind.IsExpression)) return null;
        if (isExpression.Right is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ObjectKeyword }) return null;

        var type = model.GetTypeInfo(isExpression.Left, cancellationToken).Type;
        if (type is null || !type.IsReferenceType) return null;

        return Match(node, $"{isExpression.Left} != null");
    }
}

/// <summary>IDE0170: <c>{ A: { B: 1 } }</c> → <c>{ A.B: 1 }</c> (extended property pattern).</summary>
public sealed class ExtendedPropertyPatternRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0170", "Simplify property pattern", "{ A: { B: 1 } } → { A.B: 1 }");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not RecursivePatternSyntax pattern) return null;
        if (pattern.Type is not null || pattern.PositionalPatternClause is not null || pattern.Designation is not null)
            return null;
        if (pattern.PropertyPatternClause is not { Subpatterns.Count: 1 } clause) return null;

        var outer = clause.Subpatterns[0];
        if (outer.NameColon?.Name is not { } outerName) return null;
        if (outer.Pattern is not RecursivePatternSyntax
            {
                Type: null,
                PositionalPatternClause: null,
                Designation: null,
                PropertyPatternClause: { Subpatterns: var innerSubpatterns },
            }) return null;
        if (innerSubpatterns.Count == 0) return null;
        if (innerSubpatterns.Any(subpattern => subpattern.NameColon?.Name is null)) return null;

        // NameColonSyntax.Name is an identifier, not a dotted name: the merged pattern is parsed from
        // its text (inside a throwaway `is` expression) instead of being assembled from tokens.
        var merged = string.Join(", ",
            innerSubpatterns.Select(subpattern => $"{outerName}.{subpattern.NameColon!.Name}: {subpattern.Pattern}"));

        var parsed = (IsPatternExpressionSyntax)ParseExpression($"__probe__ is {{ {merged} }}");
        return Match(node, parsed.Pattern.WithTriviaFrom(pattern));
    }
}

/// <summary>IDE0230: <c>Encoding.UTF8.GetBytes("x")</c> → <c>"x"u8</c>, when a span is what is wanted.</summary>
public sealed class Utf8StringLiteralRewrite : CSharpStyleRewrite
{
    private static readonly HashSet<string> SpanTypes = new(StringComparer.Ordinal)
    {
        "System.ReadOnlySpan<byte>",
        "System.Span<byte>",
    };

    public override StyleRule Rule { get; } = new(
        "IDE0230", "Use UTF-8 string literal", "Encoding.UTF8.GetBytes(\"x\") → \"x\"u8");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not InvocationExpressionSyntax invocation) return null;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetBytes" } call)
            return null;
        if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "UTF8" } utf8)
            return null;
        if (model.GetSymbolInfo(utf8, cancellationToken).Symbol is not IPropertySymbol { ContainingType.Name: "Encoding" })
            return null;

        if (invocation.ArgumentList.Arguments.Count != 1) return null;
        if (invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal) return null;
        if (!literal.IsKind(SyntaxKind.StringLiteralExpression)) return null;

        // The literal is a ReadOnlySpan<byte>: only legal where a span is expected.
        var converted = model.GetTypeInfo(invocation, cancellationToken).ConvertedType?.ToDisplayString();
        if (converted is null || !SpanTypes.Contains(converted)) return null;

        return Match(node, literal.ToString() + "u8");
    }
}

/// <summary>IDE0302: <c>stackalloc int[] { 1, 2 }</c> → <c>[1, 2]</c>.</summary>
public sealed class StackAllocCollectionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0302", "Use collection expression for stackalloc", "stackalloc int[] { 1, 2 } → [1, 2]");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not StackAllocArrayCreationExpressionSyntax stackAlloc) return null;
        if (stackAlloc.Initializer is null || stackAlloc.Initializer.Expressions.Count == 0) return null;

        var converted = model.GetTypeInfo(stackAlloc, cancellationToken).ConvertedType?.ToDisplayString();
        if (converted is null || !converted.StartsWith("System.Span<", StringComparison.Ordinal) &&
                                 !converted.StartsWith("System.ReadOnlySpan<", StringComparison.Ordinal)) return null;

        return Match(node, CollectionExpression(SpacedElements(stackAlloc.Initializer.Expressions)).WithTriviaFrom(node));
    }
}

// ── readonly ────────────────────────────────────────────────────────────────
/// <summary>
/// IDE0044: add <c>readonly</c> to a private field that is only written in its own constructors. The
/// write set is computed here rather than left to the compiler: a wrong guess used to make the gate
/// refuse the whole file, taking every other rewrite in it down with the guess.
/// </summary>
public sealed class ReadonlyFieldRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0044", "Add readonly modifier", "private int _x; → private readonly int _x;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not FieldDeclarationSyntax field) return null;
        if (field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return null;
        if (field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Modifiers.Any(SyntaxKind.FixedKeyword)) return null;

        var accessibilities = field.Modifiers.Where(modifier =>
            modifier.IsKind(SyntaxKind.PublicKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword) ||
            modifier.IsKind(SyntaxKind.InternalKeyword) || modifier.IsKind(SyntaxKind.PrivateKeyword)).ToList();

        if (accessibilities.Count > 0 && accessibilities.Any(modifier => !modifier.IsKind(SyntaxKind.PrivateKeyword)))
            return null;

        foreach (var declarator in field.Declaration.Variables)
        {
            if (model.GetDeclaredSymbol(declarator, cancellationToken) is not IFieldSymbol symbol) return null;
            if (IsWrittenOutsideConstructor(symbol, model, cancellationToken)) return null;
        }

        return Match(node, field.WithModifiers(field.Modifiers.Add(Readonly())));
    }

    /// <summary>
    /// Whether a reference binding to the field writes it from anywhere but a constructor of the type
    /// that declares it. Every reference is resolved through the semantic model, so `this._x =` and a
    /// same-named local are told apart. Anything unresolvable counts as a write: no rewrite is worse
    /// than a wrong one.
    /// </summary>
    private static bool IsWrittenOutsideConstructor(IFieldSymbol field, SemanticModel model, CancellationToken cancellationToken)
    {
        var type = field.ContainingType;
        if (type is null) return true;

        // A partial type spread over several files cannot be judged from one model.
        if (type.DeclaringSyntaxReferences.Length != 1) return true;

        var declaration = type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);

        foreach (var reference in declaration.DescendantNodes())
        {
            if (reference is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) continue;
            if (!SameSymbol(model.GetSymbolInfo(reference, cancellationToken).Symbol, field)) continue;
            if (!IsWrite(reference)) continue;

            var owner = reference.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (owner is ConstructorDeclarationSyntax constructor && constructor.Parent == declaration) continue;

            return true;
        }

        return false;
    }

    private static bool IsWrite(SyntaxNode reference) => reference.Parent switch
    {
        AssignmentExpressionSyntax assignment => assignment.Left == reference,
        PrefixUnaryExpressionSyntax prefix =>
            prefix.Operand == reference &&
            (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)),
        PostfixUnaryExpressionSyntax postfix =>
            postfix.Operand == reference &&
            (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)),
        ArgumentSyntax { RefKindKeyword: var keyword } =>
            keyword.IsKind(SyntaxKind.RefKeyword) || keyword.IsKind(SyntaxKind.OutKeyword),
        _ => false,
    };

    internal static SyntaxToken Readonly() => Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(Space);
}

/// <summary>IDE0250: add <c>readonly</c> to a struct; the compiler confirms every member allows it.</summary>
public sealed class ReadonlyStructRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0250", "Struct can be made 'readonly'", "struct S → readonly struct S");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not StructDeclarationSyntax structDeclaration) return null;
        if (structDeclaration.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return null;
        if (structDeclaration.Modifiers.Any(SyntaxKind.RefKeyword)) return null;

        return Match(node, structDeclaration.WithModifiers(
            structDeclaration.Modifiers.Add(ReadonlyFieldRewrite.Readonly())));
    }
}

/// <summary>IDE0251: add <c>readonly</c> to a struct member that does not mutate it.</summary>
public sealed class ReadonlyStructMemberRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0251", "Member can be made 'readonly'", "public int Get() → public readonly int Get()");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node.Parent is not StructDeclarationSyntax owner) return null;
        if (owner.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return null;

        MemberDeclarationSyntax? member = node switch
        {
            MethodDeclarationSyntax method when method.ParameterList is not null => method,
            PropertyDeclarationSyntax property => property,
            _ => null,
        };

        if (member is null) return null;
        if (member.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)) return null;
        if (member.Modifiers.Any(SyntaxKind.StaticKeyword) || member.Modifiers.Any(SyntaxKind.AsyncKeyword)) return null;
        if (member.Modifiers.Any(SyntaxKind.OverrideKeyword) || member.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
            member.Modifiers.Any(SyntaxKind.VirtualKeyword) || member.Modifiers.Any(SyntaxKind.ExternKeyword) ||
            member.Modifiers.Any(SyntaxKind.PartialKeyword)) return null;

        if (member is MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null }) return null;

        return Match(node, member.WithModifiers(member.Modifiers.Add(ReadonlyFieldRewrite.Readonly())));
    }
}
