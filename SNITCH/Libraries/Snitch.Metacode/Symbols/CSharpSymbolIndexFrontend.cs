using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The language half of the symbol index. Everything C#-specific lives behind this interface — which
/// nodes declare a symbol, which names use one, how a use reads, and what a declaration's signature
/// looks like — so a second language is one more implementation of it, not a second index.
/// </summary>
public interface ISymbolIndexFrontend
{
    /// <summary>Whether this frontend can read the tree's language.</summary>
    bool CanHandle(SyntaxTree tree);

    /// <summary>Whether the node declares a symbol the index records.</summary>
    bool IsDeclaration(SyntaxNode node);

    /// <summary>The symbol a declaration node declares, or null when it declares nothing indexable.</summary>
    ISymbol? DeclaredSymbol(SyntaxNode node, SemanticModel model);

    /// <summary>
    /// Whether the node is a name that refers to a symbol — the identifier of a qualified name counts
    /// once, on its right-most part, and a declaration's own name is a token rather than a node.
    /// </summary>
    bool IsReferenceName(SyntaxNode node);

    /// <summary>
    /// Whether the name is the type half of an attribute. Its parent then binds to the attribute's
    /// constructor, and the reference a reader makes is to the attribute type.
    /// </summary>
    bool IsAttributeName(SyntaxNode node);

    /// <summary>How the name at <paramref name="node"/> uses <paramref name="symbol"/>.</summary>
    string Classify(SyntaxNode node, ISymbol symbol, SemanticModel model);

    /// <summary>The one-line signature shown for a declaration, as the source writes it.</summary>
    string Signature(SyntaxNode node, ISymbol symbol);
}

/// <summary>
/// The C# frontend: Roslyn's syntax decides the role of every name, and the symbol decides whether the
/// role is "inherit" or "implement". The order of the rules below is the order of the syntax tree,
/// bottom-up, so the nearest construct that explains the name wins.
/// </summary>
public sealed class CSharpSymbolIndexFrontend : ISymbolIndexFrontend
{
    public bool CanHandle(SyntaxTree tree) => tree.Options.Language == LanguageNames.CSharp;

    public bool IsDeclaration(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax => true,
        BaseMethodDeclarationSyntax => true,
        BasePropertyDeclarationSyntax => true,
        EnumMemberDeclarationSyntax => true,
        DelegateDeclarationSyntax => true,
        // A field or event field is declared by its declarator: the type and the modifiers are on the
        // declaration above it, the name and the initializer on the declarator itself.
        VariableDeclaratorSyntax declarator => declarator.Parent?.Parent is BaseFieldDeclarationSyntax,
        _ => false,
    };

    public ISymbol? DeclaredSymbol(SyntaxNode node, SemanticModel model) => node switch
    {
        BaseTypeDeclarationSyntax type => model.GetDeclaredSymbol(type),
        BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
        BasePropertyDeclarationSyntax property => model.GetDeclaredSymbol(property),
        EnumMemberDeclarationSyntax member => model.GetDeclaredSymbol(member),
        DelegateDeclarationSyntax @delegate => model.GetDeclaredSymbol(@delegate),
        VariableDeclaratorSyntax declarator => FieldSymbol(declarator, model),
        _ => null,
    };

    public bool IsReferenceName(SyntaxNode node)
    {
        if (node is not SimpleNameSyntax) return false;

        // Ns.Foo binds on Foo: the left of a qualified name is the qualifier, and counting it as well
        // would report the same use twice.
        return node.Parent switch
        {
            QualifiedNameSyntax qualified => qualified.Right == node,
            AliasQualifiedNameSyntax alias => alias.Name == node,
            _ => true,
        };
    }

    public bool IsAttributeName(SyntaxNode node) => node.Parent is AttributeSyntax;

    public string Classify(SyntaxNode node, ISymbol symbol, SemanticModel model)
    {
        if (node is not SimpleNameSyntax name) return SymbolUsageKind.Read;

        // x.y.Z is one slot: the name and every member access above it answer the same question
        // ("is this a call, a write, a read?"), so climb to the outermost one.
        var slot = Slot(name);

        foreach (var ancestor in slot.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case PatternSyntax:
                    return SymbolUsageKind.Pattern;

                case SwitchExpressionArmSyntax arm when arm.Pattern.Span.Contains(node.Span):
                    return SymbolUsageKind.Pattern;

                case CasePatternSwitchLabelSyntax label when label.Pattern.Span.Contains(node.Span):
                    return SymbolUsageKind.Pattern;

                case BinaryExpressionSyntax binary when binary.Right.Span.Contains(node.Span):
                    if (binary.IsKind(SyntaxKind.IsExpression)) return SymbolUsageKind.Pattern;
                    if (binary.IsKind(SyntaxKind.AsExpression)) return SymbolUsageKind.As;
                    break;

                case InvocationExpressionSyntax invocation when invocation.Expression == slot:
                    return SymbolUsageKind.Call;

                case InvocationExpressionSyntax invocation when IsNameOf(invocation):
                    return SymbolUsageKind.NameOf;

                case TypeOfExpressionSyntax:
                    return SymbolUsageKind.TypeOf;

                case DefaultExpressionSyntax:
                    return SymbolUsageKind.Default;

                case SizeOfExpressionSyntax:
                    return SymbolUsageKind.SizeOf;

                case CastExpressionSyntax cast when cast.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Cast;

                case ArrayCreationExpressionSyntax array when array.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Array;

                case ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Instantiate;

                case AttributeSyntax attribute when attribute.Name.Span.Contains(node.Span):
                    return SymbolUsageKind.Attribute;

                case BaseListSyntax:
                    return BaseList(ancestor, symbol);

                case TypeConstraintSyntax:
                    return SymbolUsageKind.Constraint;

                case CatchDeclarationSyntax:
                    return SymbolUsageKind.Catch;

                case UsingDirectiveSyntax:
                    return SymbolUsageKind.Using;

                case LockStatementSyntax @lock when @lock.Expression.Span.Contains(node.Span):
                    return SymbolUsageKind.Lock;

                case InitializerExpressionSyntax initializer
                    when initializer.IsKind(SyntaxKind.ObjectInitializerExpression) ||
                         initializer.IsKind(SyntaxKind.WithInitializerExpression):
                    return SymbolUsageKind.InitializerWrite;

                case AssignmentExpressionSyntax assignment when assignment.Parent is InitializerExpressionSyntax:
                    return SymbolUsageKind.InitializerWrite;

                case AssignmentExpressionSyntax assignment when assignment.Left.Span.Contains(node.Span):
                    // (a, b) = f(): a target written by a deconstruction is not a plain write.
                    if (assignment.Left is TupleExpressionSyntax) return SymbolUsageKind.Deconstruction;
                    if (!assignment.Left.Span.Contains(slot.Span) && assignment.Left != slot) break;
                    return AssignmentKind(assignment, symbol);

                case PrefixUnaryExpressionSyntax unary
                    when unary.IsKind(SyntaxKind.PreIncrementExpression) || unary.IsKind(SyntaxKind.PreDecrementExpression):
                    return SymbolUsageKind.ReadWrite;

                case PostfixUnaryExpressionSyntax postfix
                    when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    return SymbolUsageKind.ReadWrite;

                case ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None):
                    return argument.RefKindKeyword.Kind() switch
                    {
                        SyntaxKind.RefKeyword => SymbolUsageKind.RefArgument,
                        SyntaxKind.OutKeyword => SymbolUsageKind.OutArgument,
                        _ => SymbolUsageKind.InArgument,
                    };

                case TypeArgumentListSyntax:
                    return SymbolUsageKind.TypeArgument;

                case ParameterSyntax parameter when parameter.Type?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.ParameterType;

                case MethodDeclarationSyntax method when method.ReturnType.Span.Contains(node.Span):
                    return SymbolUsageKind.ReturnType;

                case LocalFunctionStatementSyntax local when local.ReturnType.Span.Contains(node.Span):
                    return SymbolUsageKind.ReturnType;

                case DelegateDeclarationSyntax @delegate when @delegate.ReturnType.Span.Contains(node.Span):
                    return SymbolUsageKind.ReturnType;

                case PropertyDeclarationSyntax property when property.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.PropertyType;

                case IndexerDeclarationSyntax indexer when indexer.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.PropertyType;

                case EventDeclarationSyntax @event when @event.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.EventType;

                case EventFieldDeclarationSyntax @event when @event.Declaration.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.EventType;

                case VariableDeclarationSyntax variable when variable.Type.Span.Contains(node.Span):
                    return variable.Parent is BaseFieldDeclarationSyntax
                        ? SymbolUsageKind.FieldType
                        : SymbolUsageKind.LocalType;
            }
        }

        // Nothing in the syntax explains the name: a method reached without parentheses is a method
        // group, a type reached without a role is a type used as a qualifier, everything else is read.
        return symbol switch
        {
            IMethodSymbol => SymbolUsageKind.MethodGroup,
            ITypeSymbol => SymbolUsageKind.Type,
            _ => SymbolUsageKind.Read,
        };
    }

    /// <summary>Writes the declaration the way the source does, so a result reads like the editor.</summary>
    public string Signature(SyntaxNode node, ISymbol symbol) => node switch
    {
        BaseTypeDeclarationSyntax type => Compact(TypeSignature(type, symbol)),
        DelegateDeclarationSyntax @delegate => Compact(
            $"{Header(node, symbol)} delegate {@delegate.ReturnType} {@delegate.Identifier.Text}" +
            $"{TypeParameters(@delegate.TypeParameterList)}({Parameters(@delegate.ParameterList)})"),
        MethodDeclarationSyntax method => Compact(
            $"{Header(node, symbol)} {method.ReturnType} {method.Identifier.Text}" +
            $"{TypeParameters(method.TypeParameterList)}({Parameters(method.ParameterList)})"),
        ConstructorDeclarationSyntax constructor => Compact(
            $"{Header(node, symbol)} {constructor.Identifier.Text}({Parameters(constructor.ParameterList)})"),
        DestructorDeclarationSyntax destructor => Compact($"{Header(node, symbol)} ~{destructor.Identifier.Text}()"),
        OperatorDeclarationSyntax @operator => Compact(
            $"{Header(node, symbol)} {@operator.ReturnType} operator {@operator.OperatorToken.Text}" +
            $"({Parameters(@operator.ParameterList)})"),
        ConversionOperatorDeclarationSyntax conversion => Compact(
            $"{Header(node, symbol)} {conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}" +
            $"({Parameters(conversion.ParameterList)})"),
        PropertyDeclarationSyntax property => Compact(
            $"{Header(node, symbol)} {property.Type} {property.Identifier.Text} {Accessors(property.AccessorList)}" +
            $"{ExpressionBody(property.ExpressionBody)}"),
        IndexerDeclarationSyntax indexer => Compact(
            $"{Header(node, symbol)} {indexer.Type} this[{Parameters(indexer.ParameterList)}] " +
            $"{Accessors(indexer.AccessorList)}{ExpressionBody(indexer.ExpressionBody)}"),
        EventDeclarationSyntax @event => Compact(
            $"{Header(node, symbol)} event {@event.Type} {@event.Identifier.Text} {Accessors(@event.AccessorList)}"),
        VariableDeclaratorSyntax declarator => Compact(FieldSignature(declarator)),
        EnumMemberDeclarationSyntax member => member.EqualsValue is null
            ? member.Identifier.Text
            : Compact($"{member.Identifier.Text} = {member.EqualsValue.Value}"),
        _ => Compact(symbol.ToDisplayString()),
    };

    // ── classification helpers ───────────────────────────────────────────────

    private static ExpressionSyntax Slot(ExpressionSyntax node)
    {
        ExpressionSyntax current = node;
        while (true)
        {
            switch (current.Parent)
            {
                case MemberAccessExpressionSyntax member when member.Name == current:
                    current = member;
                    continue;
                case MemberBindingExpressionSyntax binding:
                    current = binding;
                    continue;
                case ConditionalAccessExpressionSyntax access when access.WhenNotNull == current:
                    current = access;
                    continue;
                default:
                    return current;
            }
        }
    }

    private static bool IsNameOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" };

    private static string AssignmentKind(AssignmentExpressionSyntax assignment, ISymbol symbol)
    {
        var kind = assignment.Kind();

        if (symbol is IEventSymbol)
        {
            return kind is SyntaxKind.AddAssignmentExpression
                ? SymbolUsageKind.Subscribe
                : SymbolUsageKind.Unsubscribe;
        }

        return kind is SyntaxKind.SimpleAssignmentExpression
            ? SymbolUsageKind.Write
            : SymbolUsageKind.ReadWrite;
    }

    /// <summary>A class implementing an interface implements it; an interface extending one inherits it.</summary>
    private static string BaseList(SyntaxNode baseList, ISymbol symbol)
    {
        var declaresInterface = baseList.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()
            is InterfaceDeclarationSyntax;

        return !declaresInterface && symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }
            ? SymbolUsageKind.Implement
            : SymbolUsageKind.Inherit;
    }

    // ── signature helpers ────────────────────────────────────────────────────

    private static ISymbol? FieldSymbol(VariableDeclaratorSyntax declarator, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol field) return null;

        // An event field's declarator declares the backing field; the member a reader sees is the event.
        return field.AssociatedSymbol ?? field;
    }

    private static string TypeSignature(BaseTypeDeclarationSyntax type, ISymbol symbol)
    {
        var keyword = type switch
        {
            RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "struct" } => "record struct",
            RecordDeclarationSyntax => "record",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            EnumDeclarationSyntax => "enum",
            _ => "class",
        };

        var header = Header(type, symbol);
        var name = type.Identifier.Text + TypeParameters((type as TypeDeclarationSyntax)?.TypeParameterList);
        var primary = type is RecordDeclarationSyntax record ? $"({Parameters(record.ParameterList)})" : "";
        var bases = type.BaseList is { Types.Count: > 0 } list
            ? " : " + string.Join(", ", list.Types.Select(baseType => Compact(baseType.Type.ToString())))
            : "";

        return $"{header} {keyword} {name}{primary}{bases}";
    }

    private static string FieldSignature(VariableDeclaratorSyntax declarator)
    {
        var declaration = declarator.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>();
        if (declaration is null) return declarator.ToString();

        var type = declaration.Declaration.Type.ToString();
        var modifiers = string.Join(" ", declaration.Modifiers.Select(token => token.Text));
        var isEvent = declaration is EventFieldDeclarationSyntax;

        var header = string.Join(" ", new[] { modifiers, isEvent ? "event" : null }.Where(part => part is { Length: > 0 }));
        var value = declarator.Initializer is null ? "" : " = " + Compact(declarator.Initializer.Value.ToString());

        return $"{header} {type} {declarator.Identifier.Text}{value}".Trim();
    }

    private static string Header(SyntaxNode node, ISymbol symbol)
    {
        var modifiers = string.Join(" ", ModifiersOf(node).Select(token => token.Text));
        var accessibility = AccessibilityText(symbol);

        // An interface member or a local type has no accessibility in the source; it is written here so
        // a signature always says what the declaration is.
        return modifiers.Contains(accessibility, StringComparison.Ordinal)
            ? modifiers
            : $"{accessibility} {modifiers}".Trim();
    }

    private static IEnumerable<SyntaxToken> ModifiersOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Modifiers,
        BaseMethodDeclarationSyntax method => method.Modifiers,
        BasePropertyDeclarationSyntax property => property.Modifiers,
        BaseFieldDeclarationSyntax field => field.Modifiers,
        DelegateDeclarationSyntax @delegate => @delegate.Modifiers,
        _ => node.Parent is null ? [] : ModifiersOf(node.Parent),
    };

    private static string Parameters(BaseParameterListSyntax? list) =>
        list is null ? "" : string.Join(", ", list.Parameters.Select(parameter => Compact(parameter.ToString())));

    private static string TypeParameters(TypeParameterListSyntax? list) =>
        list is null ? "" : list.ToString();

    private static string Accessors(AccessorListSyntax? list) =>
        list is null ? "" : "{ " + string.Join(" ", list.Accessors.Select(accessor => accessor.Keyword.Text + ";")) + " }";

    private static string ExpressionBody(ArrowExpressionClauseSyntax? body) => body is null ? "" : "=> …";

    private static string AccessibilityText(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => "private",
    };

    /// <summary>One space between tokens, one line: a signature is a label, not the declaration body.</summary>
    private static string Compact(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var space = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
        }

        return builder.Length > 400 ? builder.ToString(0, 397) + "..." : builder.ToString();
    }
}
