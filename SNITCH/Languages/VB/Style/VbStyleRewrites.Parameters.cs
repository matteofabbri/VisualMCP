using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;
using static Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.VB.Style;

/// <summary>
/// IDE0032: a private field that exists only to back one property becomes an auto-property. The two
/// edits are one suggestion — the field disappears and the property's accessors with it — and both
/// halves accept the same predicate, so neither can be applied without the other.
/// </summary>
public sealed class UseAutoPropertyRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0032", "Use auto property",
        "Private _x As Integer + Property X (Get/Set) → Public Property X As Integer");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;

        return node switch
        {
            FieldDeclarationSyntax field => Backing(field, context) is { } backed ? Removal(backed.Field) : null,
            PropertyBlockSyntax property => Trivial(property, context) is { } trivial
                ? MatchMember(node, AutoPropertyText(trivial))
                : null,
            _ => null,
        };
    }

    /// <summary>The field/property pair this field backs, or null when the field is not a backing field.</summary>
    private static TrivialProperty? Backing(FieldDeclarationSyntax field, StyleContext context)
    {
        if (field.Declarators.Count != 1 || field.AttributeLists.Count > 0) return null;
        if (field.Modifiers.Any(token => token.Kind() is SyntaxKind.ReadOnlyKeyword or SyntaxKind.ConstKeyword
                or SyntaxKind.WithEventsKeyword))
            return null;

        var declarator = field.Declarators[0];
        if (declarator.Names.Count != 1) return null;
        if (context.Model.GetDeclaredSymbol(declarator.Names[0], context.CancellationToken) is not IFieldSymbol symbol)
            return null;
        if (symbol.DeclaredAccessibility != Accessibility.Private) return null;

        var references = VbStyleFacts.References(symbol, context.Solution!, context.CancellationToken);
        if (references.Count == 0) return null;

        var properties = references
            .Select(reference => reference.Node.Ancestors().OfType<PropertyBlockSyntax>().FirstOrDefault())
            .Distinct()
            .ToList();

        if (properties.Count != 1 || properties[0] is null) return null;

        var trivial = Trivial(properties[0], context);
        if (trivial is null || trivial.Field.Span != field.Span) return null;

        return trivial;
    }

    /// <summary>A property whose accessors only read and write one field, with nothing else in them.</summary>
    private static TrivialProperty? Trivial(PropertyBlockSyntax property, StyleContext context)
    {
        if (property.Accessors.Count != 2) return null;
        if (property.PropertyStatement.AttributeLists.Count > 0) return null;
        if (property.PropertyStatement.ParameterList is not null) return null;

        var getter = property.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorBlock));
        var setter = property.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorBlock));
        if (getter is null || setter is null) return null;

        if (getter.Statements.Count != 1 || setter.Statements.Count != 1) return null;

        if (getter.Statements[0] is not ReturnStatementSyntax returned) return null;
        if (returned.Expression is not IdentifierNameSyntax read) return null;
        if (context.Model.GetSymbolInfo(read, context.CancellationToken).Symbol is not IFieldSymbol field) return null;

        if (setter.Statements[0] is not AssignmentStatementSyntax assignment) return null;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)) return null;
        if (assignment.Left is not IdentifierNameSyntax written) return null;
        if (!SameSymbol(context.Model.GetSymbolInfo(written, context.CancellationToken).Symbol, field)) return null;
        if (assignment.Right is not IdentifierNameSyntax value ||
            !string.Equals(value.Identifier.ValueText, "value", StringComparison.Ordinal))
            return null;

        // The property and the field have to agree on the type, or the auto-property would not compile.
        if (property.PropertyStatement.AsClause is not SimpleAsClauseSyntax asClause) return null;
        var propertyType = asClause.Type;
        if (!SymbolEqualityComparer.Default.Equals(
                context.Model.GetTypeInfo(propertyType, context.CancellationToken).Type, field.Type))
            return null;

        var fieldDeclaration = FieldDeclarationOf(field, context);
        if (fieldDeclaration is null) return null;

        return new TrivialProperty(fieldDeclaration, property, propertyType,
            fieldDeclaration.Declarators[0].Initializer);
    }

    /// <summary>
    /// The field declaration a field symbol came from. VB points the reference at the declarator, not at
    /// the declaration statement, so the answer is the nearest <c>FieldDeclarationSyntax</c> above it.
    /// </summary>
    private static FieldDeclarationSyntax? FieldDeclarationOf(IFieldSymbol field, StyleContext context)
    {
        foreach (var reference in field.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(context.CancellationToken);
            var declaration = syntax as FieldDeclarationSyntax ?? syntax.FirstAncestorOrSelf<FieldDeclarationSyntax>();
            if (declaration is not null) return declaration;
        }

        return null;
    }

    private static string AutoPropertyText(TrivialProperty trivial)
    {
        var statement = trivial.Property.PropertyStatement;

        // The property keeps its own words: only the accessors and the backing field disappear.
        var modifiers = statement.Modifiers.Count == 0 ? string.Empty : statement.Modifiers.ToString() + " ";

        return modifiers + "Property " + statement.Identifier.ValueText +
               " As " + trivial.Type + (trivial.Initializer is null ? string.Empty : " " + trivial.Initializer);
    }

    private sealed record TrivialProperty(
        FieldDeclarationSyntax Field,
        PropertyBlockSyntax Property,
        TypeSyntax Type,
        EqualsValueSyntax? Initializer);
}

/// <summary>
/// IDE0060: a parameter nobody reads is removed, together with the argument every call site passes.
/// The suggestion spans documents — the declaration is one edit, each call site another — so it is one
/// span-edit shape, gated and written as a whole. Only a private method with an ordinary body qualifies:
/// nothing outside the solution can call it, and no interface decides its signature.
/// </summary>
public sealed class UnusedParameterRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0060", "Remove unused parameter",
        "Sub M(unused As Integer) → Sub M(), and the argument at every call site");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;
        if (node is not MethodBlockSyntax method) return null;
        if (method.SubOrFunctionStatement.AttributeLists.Count > 0) return null;

        // A Handles clause binds the signature to an event, so its parameters are not the method's to drop.
        if (method.SubOrFunctionStatement.HandlesClause is not null) return null;
        if (method.SubOrFunctionStatement.ImplementsClause is not null) return null;

        var parameters = method.SubOrFunctionStatement.ParameterList;
        if (parameters is null) return null;

        if (context.Model.GetDeclaredSymbol(method, context.CancellationToken) is not IMethodSymbol symbol)
            return null;

        if (symbol.DeclaredAccessibility != Accessibility.Private) return null;
        if (symbol.IsOverride || symbol.IsVirtual || symbol.IsAbstract || symbol.IsExtern) return null;
        if (symbol.ExplicitInterfaceImplementations.Length > 0) return null;

        if (parameters.Parameters.Count != symbol.Parameters.Length) return null;

        var index = -1;
        for (var position = 0; position < symbol.Parameters.Length; position++)
        {
            var parameter = symbol.Parameters[position];
            if (parameter.RefKind != RefKind.None || parameter.IsParams) return null;
            if (parameters.Parameters[position].AttributeLists.Count > 0) return null;
            if (IsUsed(parameter, method, context)) continue;

            // One removal per suggestion: two would produce overlapping spans.
            if (index >= 0) return null;
            index = position;
        }

        if (index < 0) return null;

        var declarationPath = node.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(declarationPath)) return null;

        var callSites = CallSites(symbol, context);
        if (callSites is null) return null;

        var edits = new List<SpanEdit>();
        AddSpan(parameters.Parameters, index, declarationPath, edits);

        foreach (var call in callSites)
        {
            var path = call.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(path)) return null;

            AddSpan(call.ArgumentList.Arguments, index, path, edits);
        }

        return Shape(Rule, node, edits, symbol.Parameters[index].Name, string.Empty);
    }

    /// <summary>
    /// The span of one element of a comma-separated list, comma included: the element itself when it is
    /// the only one, otherwise the separator and the element next to it.
    /// </summary>
    private static void AddSpan<TNode>(SeparatedSyntaxList<TNode> list, int index, string path, List<SpanEdit> edits)
        where TNode : SyntaxNode
    {
        if (list.Count == 1)
        {
            edits.Add(new SpanEdit(path, list[0].Span.Start, list[0].Span.Length));
            return;
        }

        if (index == 0)
        {
            edits.Add(new SpanEdit(path, list[0].Span.Start, list[1].Span.Start - list[0].Span.Start));
            return;
        }

        edits.Add(new SpanEdit(path, list[index - 1].Span.End, list[index].Span.End - list[index - 1].Span.End));
    }

    private static bool IsUsed(IParameterSymbol parameter, SyntaxNode block, StyleContext context) =>
        block.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SameSymbol(context.Model.GetSymbolInfo(identifier, context.CancellationToken).Symbol, parameter));

    /// <summary>Every call, or null when one reference cannot be rewritten as an argument list.</summary>
    private static List<InvocationExpressionSyntax>? CallSites(IMethodSymbol symbol, StyleContext context)
    {
        var references = SymbolFinder.FindReferencesAsync(symbol, context.Solution!, context.CancellationToken)
            .GetAwaiter().GetResult();

        var calls = new List<InvocationExpressionSyntax>();

        foreach (var location in references.SelectMany(reference => reference.Locations))
        {
            if (location.IsImplicit) continue;

            var root = location.Document.GetSyntaxRootAsync(context.CancellationToken).GetAwaiter().GetResult();
            if (root is null) return null;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null) return null;          // a method group or an AddressOf: no argument list

            if (invocation.ArgumentList.Arguments.Count != symbol.Parameters.Length) return null;
            if (invocation.ArgumentList.Arguments.Any(argument =>
                    argument is SimpleArgumentSyntax { NameColonEquals: not null })) return null;

            calls.Add(invocation);
        }

        return calls;
    }
}
