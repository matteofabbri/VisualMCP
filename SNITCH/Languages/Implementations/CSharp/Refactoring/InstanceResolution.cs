using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>An expression, usable at the position it was resolved for, that holds an instance of a type.</summary>
internal sealed record ResolvedInstance(string Expression, string Kind);

/// <summary>
/// Answers the question "at this point in the code, which expression of type T is available?" — the
/// piece <c>make_non_static</c> and the parameter→property transform both need, because making a
/// member an instance member is only a rewrite when every call site already has an instance to call it
/// on. Nothing is invented: when no instance is in scope the answer is null, and the caller refuses.
/// </summary>
internal static class InstanceResolution
{
    /// <summary>
    /// <c>this</c> when the position is inside an instance member of the target type (or a type derived
    /// from it), otherwise the nearest local, parameter or field whose type is the target. Only
    /// side-effect-free expressions are returned, so evaluating one twice (the assignment and the call)
    /// is harmless.
    /// </summary>
    public static ResolvedInstance? Find(
        SyntaxNode position,
        SemanticModel model,
        INamedTypeSymbol target,
        CancellationToken cancellationToken = default)
    {
        var member = position.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();

        // A `static` lambda or local function between the position and its member has no `this`.
        var staticContext = position.Ancestors()
            .TakeWhile(ancestor => ancestor != member)
            .Any(ancestor => IsStaticFunction(ancestor));

        if (member is not null && !staticContext && IsInstanceContext(member))
        {
            var owner = DeclaredContainingType(member, model, cancellationToken);
            if (owner is not null && IsOrDerivesFrom(owner, target)) return new ResolvedInstance("this", "this");
        }

        var instanceContext = member is not null && !staticContext && IsInstanceContext(member);
        var ownerType = member is null ? null : DeclaredContainingType(member, model, cancellationToken);

        // Locals: the latest declaration that still contains the position wins.
        var local = LocalsInScope(position, model, cancellationToken)
            .Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.Type, target))
            .OrderByDescending(candidate => candidate.SpanStart)
            .FirstOrDefault();

        if (local.Name is not null) return new ResolvedInstance(local.Name, "local");

        var parameter = ParametersInScope(position, model, cancellationToken)
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.Type, target));
        if (parameter is not null) return new ResolvedInstance(parameter.Name, "parameter");

        if (member is not null)
        {
            var field = FieldsInScope(member, position, model, ownerType, target, instanceContext, cancellationToken);
            if (field is not null) return field;
        }

        return null;
    }

    /// <summary>True when the position sits inside an instance (non-static) member.</summary>
    public static bool IsInstancePosition(SyntaxNode position)
    {
        var member = position.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member is not null && IsInstanceContext(member) &&
               !position.Ancestors().TakeWhile(ancestor => ancestor != member).Any(IsStaticFunction);
    }

    /// <summary>The type that declares the member holding the position, resolved through the model.</summary>
    public static INamedTypeSymbol? EnclosingType(SyntaxNode position, SemanticModel model, CancellationToken cancellationToken)
    {
        var member = position.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member is null ? null : DeclaredContainingType(member, model, cancellationToken);
    }

    public static bool IsOrDerivesFrom(INamedTypeSymbol candidate, INamedTypeSymbol target)
    {
        for (var type = candidate; type is not null; type = type.BaseType)
            if (SymbolEqualityComparer.Default.Equals(type, target)) return true;

        return target.TypeKind == TypeKind.Interface &&
               candidate.AllInterfaces.Any(implemented => SymbolEqualityComparer.Default.Equals(implemented, target));
    }

    private static bool IsStaticFunction(SyntaxNode node) => node switch
    {
        LocalFunctionStatementSyntax localFunction => localFunction.Modifiers.Any(SyntaxKind.StaticKeyword),
        AnonymousFunctionExpressionSyntax lambda => lambda.Modifiers.Any(SyntaxKind.StaticKeyword),
        _ => false,
    };

    private static bool IsInstanceContext(MemberDeclarationSyntax member) =>
        !member.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static INamedTypeSymbol? DeclaredContainingType(
        MemberDeclarationSyntax member, SemanticModel model, CancellationToken cancellationToken)
    {
        // A FieldDeclaration's declared symbol is the field, a method's is the method: both know their type.
        var symbol = model.GetDeclaredSymbol(member, cancellationToken);
        return symbol switch
        {
            null => (member.Parent as TypeDeclarationSyntax) is { } parent
                ? model.GetDeclaredSymbol(parent, cancellationToken)
                : null,
            INamedTypeSymbol type => type,
            _ => symbol.ContainingType,
        };
    }

    private static IEnumerable<(string Name, ITypeSymbol? Type, int SpanStart)> LocalsInScope(
        SyntaxNode position, SemanticModel model, CancellationToken cancellationToken)
    {
        var scope = position.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? position.SyntaxTree!.GetRoot();

        foreach (var declaration in scope.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (declaration.Span.End > position.SpanStart) continue;
            if (!DeclarationContains(declaration, position)) continue;

            foreach (var variable in declaration.Declaration.Variables)
            {
                if (model.GetDeclaredSymbol(variable, cancellationToken) is ILocalSymbol local)
                    yield return (local.Name, local.Type, declaration.SpanStart);
            }
        }
    }

    /// <summary>A local is in scope only inside the block that declares it (or, top-level, after it).</summary>
    private static bool DeclarationContains(LocalDeclarationStatementSyntax declaration, SyntaxNode position)
    {
        var block = declaration.AncestorsAndSelf().OfType<BlockSyntax>().FirstOrDefault();
        if (block is not null) return block.Span.Contains(position.SpanStart);

        // `switch` sections and top-level statements keep their locals without an enclosing block.
        var section = declaration.AncestorsAndSelf().OfType<SwitchSectionSyntax>().FirstOrDefault();
        return section is null || section.Span.Contains(position.SpanStart);
    }

    private static IEnumerable<IParameterSymbol> ParametersInScope(
        SyntaxNode position, SemanticModel model, CancellationToken cancellationToken)
    {
        foreach (var ancestor in position.Ancestors())
        {
            IEnumerable<ParameterSyntax>? parameters = ancestor switch
            {
                BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
                LocalFunctionStatementSyntax localFunction => localFunction.ParameterList.Parameters,
                ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
                SimpleLambdaExpressionSyntax lambda => [lambda.Parameter],
                _ => null,
            };

            if (parameters is null) continue;

            foreach (var parameter in parameters)
            {
                if (model.GetDeclaredSymbol(parameter, cancellationToken) is IParameterSymbol symbol)
                    yield return symbol;
            }
        }
    }

    /// <summary>
    /// A field of the enclosing (or an enclosing outer) type whose type is the target: a static one is
    /// reachable by name, an instance one only where `this` exists.
    /// </summary>
    private static ResolvedInstance? FieldsInScope(
        MemberDeclarationSyntax member,
        SyntaxNode position,
        SemanticModel model,
        INamedTypeSymbol? owner,
        INamedTypeSymbol target,
        bool instanceContext,
        CancellationToken cancellationToken)
    {
        foreach (var typeDeclaration in member.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol declaring) continue;

            foreach (var fieldDeclaration in typeDeclaration.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in fieldDeclaration.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable, cancellationToken) is not IFieldSymbol field) continue;
                    if (!SymbolEqualityComparer.Default.Equals(field.Type, target)) continue;

                    var name = field.Name;
                    if (field.IsStatic) return new ResolvedInstance(name, "field");

                    var sameType = owner is not null && SymbolEqualityComparer.Default.Equals(declaring, owner);
                    if (instanceContext && sameType) return new ResolvedInstance(name, "field");
                }
            }
        }

        return null;
    }
}
