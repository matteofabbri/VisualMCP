using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Style;

/// <summary>
/// Whether a local function or a lambda can be marked <c>static</c>: it may use only what it declares
/// itself — its parameters, its locals — and must not read instance state through the implicit
/// <c>this</c>. The rewrite is mechanical, but a wrong answer is a compile error, so the check is
/// semantic and errs on the side of refusing.
/// </summary>
internal static class CaptureAnalysis
{
    public static bool Captures(SyntaxNode function, ISymbol? self, SemanticModel model, CancellationToken cancellationToken)
    {
        var body = function switch
        {
            LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody?.Expression,
            AnonymousFunctionExpressionSyntax lambda => lambda.Body,
            _ => null,
        };

        if (body is null) return true;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax) return true;
            if (node is not IdentifierNameSyntax identifier) continue;

            var symbol = model.GetSymbolInfo(identifier, cancellationToken).Symbol;
            switch (symbol)
            {
                case ILocalSymbol local when !DeclaredInside(local, function, cancellationToken):
                    return true;

                case IParameterSymbol parameter when
                    !SameSymbol(parameter.ContainingSymbol, self) &&
                    !DeclaredInside(parameter, function, cancellationToken):
                    return true;

                case IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol when !symbol.IsStatic:
                    if (BindsToThis(identifier)) return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>Whether the symbol is declared inside the function itself (its own locals/parameters).</summary>
    private static bool DeclaredInside(ISymbol symbol, SyntaxNode function, CancellationToken cancellationToken) =>
        symbol.DeclaringSyntaxReferences.Any(reference => function.Span.Contains(reference.Span));

    /// <summary>
    /// A bare name that binds to an instance member is `this.Member`; a name used as the receiver of a
    /// member access, or inside an object initializer, is not.
    /// </summary>
    private static bool BindsToThis(IdentifierNameSyntax identifier) => identifier.Parent switch
    {
        MemberAccessExpressionSyntax access when access.Name == identifier => false,
        MemberBindingExpressionSyntax => false,
        AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } => false,
        _ => true,
    };

    private static bool SameSymbol(ISymbol? left, ISymbol? right) =>
        left is not null && right is not null && SymbolEqualityComparer.Default.Equals(left, right);
}

/// <summary>IDE0062: a local function that captures nothing becomes <c>static</c>.</summary>
public sealed class StaticLocalFunctionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0062", "Make local function 'static'",
        "int Twice(int v) { … } → static int Twice(int v) { … }");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not LocalFunctionStatementSyntax local) return null;
        if (local.Modifiers.Any(SyntaxKind.StaticKeyword)) return null;
        if (local.Body is null && local.ExpressionBody is null) return null;

        var self = model.GetDeclaredSymbol(local, cancellationToken);
        if (self is null) return null;
        if (CaptureAnalysis.Captures(local, self, model, cancellationToken)) return null;

        return Match(node, local.WithModifiers(local.Modifiers.Add(Static())));
    }

    internal static SyntaxToken Static() => Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(Space);
}

/// <summary>IDE0320: a lambda that captures nothing becomes a <c>static</c> lambda.</summary>
public sealed class StaticAnonymousFunctionRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0320", "Make anonymous function 'static'",
        "x => x + 1 → static x => x + 1");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not AnonymousFunctionExpressionSyntax lambda) return null;
        if (lambda.Modifiers.Any(SyntaxKind.StaticKeyword)) return null;

        var self = model.GetSymbolInfo(lambda, cancellationToken).Symbol;
        if (self is not IMethodSymbol) return null;
        if (CaptureAnalysis.Captures(lambda, self, model, cancellationToken)) return null;

        return Match(node, lambda.WithModifiers(lambda.Modifiers.Add(StaticLocalFunctionRewrite.Static())));
    }
}

/// <summary>
/// IDE0070: a hand-written <c>GetHashCode</c> that folds fields in sequence becomes
/// <c>System.HashCode.Combine(...)</c>. The hash values change, which is allowed: the contract is only
/// that equal objects hash equally, and Combine honours it.
/// </summary>
public sealed class HashCodeCombineRewrite : CSharpStyleRewrite
{
    private const int MaxOperands = 8;

    public override StyleRule Rule { get; } = new(
        "IDE0070", "Use 'System.HashCode.Combine'",
        "int h = 17; h = h * 31 + _a.GetHashCode(); return h; → return HashCode.Combine(_a);");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MethodDeclarationSyntax method || method.Body is null) return null;
        if (method.Identifier.ValueText != nameof(GetHashCode)) return null;
        if (!method.Modifiers.Any(SyntaxKind.OverrideKeyword)) return null;
        if (method.ParameterList.Parameters.Count != 0) return null;

        var statements = Unchecked(method.Body).Statements;
        if (statements.Count < 3) return null;
        if (statements[0] is not LocalDeclarationStatementSyntax seed ||
            seed.Declaration.Variables.Count != 1) return null;
        if (statements[^1] is not ReturnStatementSyntax { Expression: IdentifierNameSyntax returned }) return null;

        var accumulator = seed.Declaration.Variables[0].Identifier.ValueText;
        if (returned.Identifier.ValueText != accumulator) return null;

        var operands = new List<ExpressionSyntax>();
        for (var index = 1; index < statements.Count - 1; index++)
        {
            if (statements[index] is not ExpressionStatementSyntax
                { Expression: AssignmentExpressionSyntax assignment } step) return null;
            if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return null;
            if (assignment.Left is not IdentifierNameSyntax target || target.Identifier.ValueText != accumulator)
                return null;

            if (Operand(assignment.Right, accumulator) is not { } operand) return null;
            if (!IsHashable(operand)) return null;

            operands.Add(operand);
        }

        if (operands.Count is < 1 or > MaxOperands) return null;
        if (ConditionalShape.HasComment(method.Body)) return null;

        var call = $"System.HashCode.Combine({string.Join(", ", operands)})";
        var replacementStatements = new List<StatementSyntax>
        {
            ReturnStatement(ParseExpression(call)).WithTrailingTrivia(method.Body.GetTrailingTrivia()),
        };

        var replacement = method.WithBody(method.Body.WithStatements(List(replacementStatements)));

        return Match(node, replacement);
    }

    /// <summary>The block inside <c>unchecked { … }</c>, or the body itself.</summary>
    private static BlockSyntax Unchecked(BlockSyntax body) =>
        body.Statements.Count == 1 &&
        body.Statements[0] is CheckedStatementSyntax { RawKind: (int)SyntaxKind.UncheckedStatement } uncheckedBlock
            ? uncheckedBlock.Block
            : body;

    /// <summary>Reads <c>h * 31 + X</c>, <c>(h * 31) + X</c> and the <c>^</c> variants, returning X.</summary>
    private static ExpressionSyntax? Operand(ExpressionSyntax expression, string accumulator)
    {
        if (expression is not BinaryExpressionSyntax combine) return null;
        if (!combine.IsKind(SyntaxKind.AddExpression) && !combine.IsKind(SyntaxKind.ExclusiveOrExpression))
            return null;

        if (combine.Left is not BinaryExpressionSyntax multiply ||
            !multiply.IsKind(SyntaxKind.MultiplyExpression)) return null;
        if (multiply.Left is not IdentifierNameSyntax folded || folded.Identifier.ValueText != accumulator)
            return null;
        if (multiply.Right is not LiteralExpressionSyntax) return null;

        // `_a.GetHashCode()` folds to `_a`: Combine does the same thing, and handles null.
        if (combine.Right is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetHashCode" },
                ArgumentList.Arguments.Count: 0,
            } invocation)
        {
            return invocation.Expression is MemberAccessExpressionSyntax access ? access.Expression : null;
        }

        return combine.Right;
    }

    /// <summary>Only an expression that can be evaluated twice without cost or surprise.</summary>
    private static bool IsHashable(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax => true,
        ThisExpressionSyntax => true,
        MemberAccessExpressionSyntax access => IsHashable(access.Expression),
        _ => false,
    };
}

/// <summary>
/// The members that carry a modifier list, in one place: a rule that reads or rewrites modifiers should
/// not repeat this switch.
/// </summary>
internal static class MemberShape
{
    public static SyntaxTokenList? ModifiersOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.Modifiers,
        BaseMethodDeclarationSyntax method => method.Modifiers,
        BasePropertyDeclarationSyntax property => property.Modifiers,
        BaseFieldDeclarationSyntax field => field.Modifiers,
        DelegateDeclarationSyntax deleg => deleg.Modifiers,
        _ => null,
    };

    public static SyntaxNode? WithModifiers(SyntaxNode node, SyntaxTokenList modifiers) => node switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.WithModifiers(modifiers),
        BaseMethodDeclarationSyntax method => method.WithModifiers(modifiers),
        BasePropertyDeclarationSyntax property => property.WithModifiers(modifiers),
        BaseFieldDeclarationSyntax field => field.WithModifiers(modifiers),
        DelegateDeclarationSyntax deleg => deleg.WithModifiers(modifiers),
        _ => null,
    };
}

/// <summary>
/// IDE0040: makes the accessibility modifier explicit, as <c>dotnet_style_require_accessibility_modifiers</c>
/// asks. The value added is the accessibility the compiler already assumes, so the meaning never changes;
/// with <c>omit_if_default</c> the same rule removes an explicit modifier that is the default.
/// </summary>
public sealed class AccessibilityModifierRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0040", "Add accessibility modifiers",
        "int F() => 1; → private int F() => 1;");

    public override string? RequiredOption => "dotnet_style_require_accessibility_modifiers";

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        null;

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.Accessibility;
        if (preference == AccessibilityPreference.None) return null;

        if (MemberShape.ModifiersOf(node) is not { } modifiers) return null;
        if (modifiers.Any(token => token.IsKind(SyntaxKind.PartialKeyword))) return null;

        var symbol = context.Model.GetDeclaredSymbol(node, context.CancellationToken);
        if (symbol is null) return null;
        if (!HasAccessibility(node, symbol)) return null;

        var declared = modifiers.Where(IsAccessibility).ToList();

        if (preference == AccessibilityPreference.OmitIfDefault)
        {
            if (declared.Count != 1) return null;

            var defaultAccessibility = DefaultFor(symbol);
            if (ModifierFor(defaultAccessibility) != declared[0].Kind()) return null;

            return Match(node, MemberShape.WithModifiers(node, TokenList(modifiers.Where(token => token != declared[0])))!);
        }

        if (declared.Count > 0) return null;
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum or TypeKind.Delegate }) return null;

        var required = ModifierFor(symbol.DeclaredAccessibility);
        if (required == SyntaxKind.None) return null;

        var added = modifiers.Insert(0, Token(required).WithTrailingTrivia(Space));
        return Match(node, MemberShape.WithModifiers(node, added)!);
    }

    /// <summary>`for_non_interface_members` leaves interface members alone.</summary>
    private static bool HasAccessibility(SyntaxNode node, ISymbol symbol) =>
        symbol is INamedTypeSymbol { ContainingType: null } ||
        symbol.ContainingType is null ||
        symbol.ContainingType.TypeKind != TypeKind.Interface;

    /// <summary>The accessibility the compiler assumes when no modifier is written.</summary>
    private static Accessibility DefaultFor(ISymbol symbol) =>
        symbol.ContainingType is null
            ? Accessibility.Internal
            : symbol.ContainingType.TypeKind == TypeKind.Interface
                ? Accessibility.Public
                : Accessibility.Private;

    private static bool IsAccessibility(SyntaxToken token) => token.Kind() is
        SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.InternalKeyword or
        SyntaxKind.ProtectedKeyword;

    private static SyntaxKind ModifierFor(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => SyntaxKind.PublicKeyword,
        Accessibility.Private => SyntaxKind.PrivateKeyword,
        Accessibility.Internal => SyntaxKind.InternalKeyword,
        Accessibility.Protected => SyntaxKind.ProtectedKeyword,
        Accessibility.ProtectedOrInternal => SyntaxKind.ProtectedKeyword,
        Accessibility.ProtectedAndInternal => SyntaxKind.PrivateKeyword,
        _ => SyntaxKind.None,
    };
}

/// <summary>
/// IDE0380: an <c>unsafe</c> modifier whose span contains no unsafe construct is removed. The scan is
/// deliberately broad — any pointer, function pointer, <c>stackalloc</c>, <c>fixed</c> or
/// address-of keeps the modifier.
/// </summary>
public sealed class UnnecessaryUnsafeRewrite : CSharpStyleRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0380", "Remove unnecessary 'unsafe' modifier",
        "public unsafe int F() => 1; → public int F() => 1;");

    public override StyleMatch? TryMatch(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        var modifiers = MemberShape.ModifiersOf(node);
        if (modifiers is not { } list) return null;

        var unsafeToken = list.FirstOrDefault(token => token.IsKind(SyntaxKind.UnsafeKeyword));
        if (unsafeToken.IsKind(SyntaxKind.None)) return null;

        if (node.DescendantNodes().Any(UsesUnsafe)) return null;

        var replacement = MemberShape.WithModifiers(node, TokenList(list.Where(token => !token.IsKind(SyntaxKind.UnsafeKeyword))));
        return replacement is null ? null : Match(node, replacement);
    }

    private static bool UsesUnsafe(SyntaxNode node) => node switch
    {
        PointerTypeSyntax => true,
        FunctionPointerTypeSyntax => true,
        StackAllocArrayCreationExpressionSyntax => true,
        ImplicitStackAllocArrayCreationExpressionSyntax => true,
        FixedStatementSyntax => true,
        PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.AddressOfExpression),
        _ => false,
    };
}
