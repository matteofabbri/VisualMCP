using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Snitch.Metacode.Symbols;

/// <summary>
/// The Visual Basic half of the symbol index. It answers the same four questions the C# frontend does
/// — which nodes declare a symbol, which names use one, how the use reads, and what a declaration's
/// signature looks like — so <c>reference_index</c> queries the same tables and returns the same shape
/// for a <c>.vb</c> document.
/// </summary>
/// <remarks>
/// The differences that matter are all in the syntax, not in the model: a VB member is a block or a
/// single statement (a <c>Property</c> with no body is an auto-property, not a shorthand), a field's
/// name is a <see cref="ModifiedIdentifierSyntax"/> inside a declarator that may name several fields,
/// inheritance and interface implementation are two separate statements (<c>Inherits</c> and
/// <c>Implements</c>) rather than one base list, and a compound assignment is its own statement kind
/// rather than an assignment expression.
/// </remarks>
public sealed class VisualBasicSymbolIndexFrontend : ISymbolIndexFrontend
{
    public bool CanHandle(SyntaxTree tree) => tree.Options.Language == LanguageNames.VisualBasic;

    public bool IsDeclaration(SyntaxNode node) => node switch
    {
        TypeBlockSyntax => true,
        EnumBlockSyntax => true,
        EnumMemberDeclarationSyntax => true,
        // A method with a body is declared by its block; a body-less one (an interface member, a
        // MustOverride member) is the statement itself. Declaring both would index it twice.
        MethodBlockSyntax => true,
        MethodStatementSyntax statement => statement.Parent is not MethodBlockSyntax,
        ConstructorBlockSyntax => true,
        OperatorBlockSyntax => true,
        PropertyBlockSyntax => true,
        PropertyStatementSyntax statement => statement.Parent is not PropertyBlockSyntax,
        EventBlockSyntax => true,
        EventStatementSyntax statement => statement.Parent is not EventBlockSyntax,
        DelegateStatementSyntax => true,
        // A field's declarator names one or more fields; the declaration above it holds the type.
        ModifiedIdentifierSyntax identifier => identifier.Parent is VariableDeclaratorSyntax { Parent: FieldDeclarationSyntax },
        _ => false,
    };

    public ISymbol? DeclaredSymbol(SyntaxNode node, SemanticModel model) => node switch
    {
        TypeBlockSyntax type => model.GetDeclaredSymbol(type),
        EnumBlockSyntax enumeration => model.GetDeclaredSymbol(enumeration),
        EnumMemberDeclarationSyntax member => model.GetDeclaredSymbol(member),
        MethodBlockSyntax method => model.GetDeclaredSymbol(method),
        MethodStatementSyntax statement => model.GetDeclaredSymbol(statement),
        ConstructorBlockSyntax constructor => model.GetDeclaredSymbol(constructor),
        OperatorBlockSyntax @operator => model.GetDeclaredSymbol(@operator),
        PropertyBlockSyntax property => model.GetDeclaredSymbol(property),
        PropertyStatementSyntax property => model.GetDeclaredSymbol(property),
        EventBlockSyntax @event => model.GetDeclaredSymbol(@event),
        EventStatementSyntax @event => model.GetDeclaredSymbol(@event),
        DelegateStatementSyntax @delegate => model.GetDeclaredSymbol(@delegate),
        ModifiedIdentifierSyntax identifier => FieldSymbol(identifier, model),
        _ => null,
    };

    public bool IsReferenceName(SyntaxNode node)
    {
        if (node is not SimpleNameSyntax) return false;

        // Namespace.Member binds on Member: counting the qualifier too would report the same use twice.
        return node.Parent switch
        {
            QualifiedNameSyntax qualified => qualified.Right == node,
            _ => true,
        };
    }

    public bool IsAttributeName(SyntaxNode node) => node.Parent is AttributeSyntax;

    public string Classify(SyntaxNode node, ISymbol symbol, SemanticModel model)
    {
        if (node is not SimpleNameSyntax name) return SymbolUsageKind.Read;

        var slot = Slot(name);

        foreach (var ancestor in slot.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case TypeOfExpressionSyntax typeOf when typeOf.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Pattern;

                case GetTypeExpressionSyntax getType when getType.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.TypeOf;

                case NameOfExpressionSyntax:
                    return SymbolUsageKind.NameOf;

                case CastExpressionSyntax cast when cast.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Cast;

                case ObjectCreationExpressionSyntax creation when creation.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Instantiate;

                case ArrayCreationExpressionSyntax array when array.Type.Span.Contains(node.Span):
                    return SymbolUsageKind.Array;

                case AttributeSyntax attribute when attribute.Name.Span.Contains(node.Span):
                    return SymbolUsageKind.Attribute;

                case InheritsStatementSyntax:
                case ImplementsStatementSyntax:
                    return BaseList(ancestor, symbol);

                case ImportsStatementSyntax:
                    return SymbolUsageKind.Using;

                case SyncLockBlockSyntax @lock when @lock.SyncLockStatement.Expression.Span.Contains(node.Span):
                    return SymbolUsageKind.Lock;

                case ObjectMemberInitializerSyntax initializer when initializer.Span.Contains(node.Span):
                    return SymbolUsageKind.InitializerWrite;

                case NamedFieldInitializerSyntax fieldInitializer when fieldInitializer.Name.Span.Contains(node.Span):
                    return SymbolUsageKind.InitializerWrite;

                case AddRemoveHandlerStatementSyntax handler:
                    return handler.IsKind(SyntaxKind.AddHandlerStatement)
                        ? SymbolUsageKind.Subscribe
                        : SymbolUsageKind.Unsubscribe;

                case AssignmentStatementSyntax assignment when assignment.Left.Span.Contains(node.Span):
                    return AssignmentKind(assignment);

                case EventStatementSyntax when ancestor.Parent is TypeBlockSyntax:
                    // The handler list of a WithEvents field names events, which are not written.
                    return SymbolUsageKind.Read;

                case TypeArgumentListSyntax:
                    return SymbolUsageKind.TypeArgument;

                case ParameterSyntax parameter when TypeOf(parameter.AsClause)?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.ParameterType;

                case MethodStatementSyntax method when TypeOf(method.AsClause)?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.ReturnType;

                case PropertyStatementSyntax property when TypeOf(property.AsClause)?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.PropertyType;

                case EventStatementSyntax @event when TypeOf(@event.AsClause)?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.EventType;

                case CatchStatementSyntax @catch when TypeOf(@catch.AsClause)?.Span.Contains(node.Span) == true:
                    return SymbolUsageKind.Catch;

                case VariableDeclaratorSyntax declarator when TypeOf(declarator.AsClause)?.Span.Contains(node.Span) == true:
                    return declarator.Parent is FieldDeclarationSyntax
                        ? SymbolUsageKind.FieldType
                        : SymbolUsageKind.LocalType;

                case InvocationExpressionSyntax invocation when invocation.Expression == slot:
                    return SymbolUsageKind.Call;
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
        TypeBlockSyntax type => Compact(TypeSignature(type, symbol)),
        EnumBlockSyntax enumeration => Compact(Header(enumeration, symbol) + " Enum " + enumeration.EnumStatement.Identifier.Text),
        EnumMemberDeclarationSyntax member => member.Initializer is null
            ? member.Identifier.Text
            : Compact($"{member.Identifier.Text} = {member.Initializer.Value}"),
        MethodBlockSyntax method => SignedHeader(method.SubOrFunctionStatement, symbol),
        MethodStatementSyntax statement => SignedHeader(statement, symbol),
        ConstructorBlockSyntax constructor => SignedHeader(constructor.SubNewStatement, symbol),
        OperatorBlockSyntax @operator => SignedHeader(@operator.OperatorStatement, symbol),
        PropertyBlockSyntax property => SignedHeader(property.PropertyStatement, symbol),
        PropertyStatementSyntax property => SignedHeader(property, symbol),
        EventBlockSyntax @event => SignedHeader(@event.EventStatement, symbol),
        EventStatementSyntax @event => SignedHeader(@event, symbol),
        DelegateStatementSyntax @delegate => Compact(@delegate.ToString()),
        ModifiedIdentifierSyntax identifier => FieldSignature(identifier, symbol),
        _ => Compact(symbol.ToDisplayString()),
    };

    // ── classification helpers ───────────────────────────────────────────────

    /// <summary>
    /// <c>x.y.Z</c> is one slot: the name and every member access above it answer the same question
    /// ("is this a call, a write, a read?"), so climb to the outermost one. <c>a?.b</c> is a member
    /// access inside a conditional access, which continues the same slot.
    /// </summary>
    private static ExpressionSyntax Slot(ExpressionSyntax node)    {
        ExpressionSyntax current = node;
        while (true)
        {
            switch (current.Parent)
            {
                case MemberAccessExpressionSyntax member when member.Name == current:
                    current = member;
                    continue;
                case ConditionalAccessExpressionSyntax access when access.WhenNotNull == current:
                    current = access;
                    continue;
                default:
                    return current;
            }
        }
    }

    /// <summary>The type an <c>As …</c> clause names; an <c>As New</c> clause names none here.</summary>
    private static TypeSyntax? TypeOf(AsClauseSyntax? clause) =>
        clause is SimpleAsClauseSyntax simple ? simple.Type : null;

    private static string AssignmentKind(AssignmentStatementSyntax assignment) =>
        assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)
            ? SymbolUsageKind.Write
            : SymbolUsageKind.ReadWrite;

    /// <summary>A class implementing an interface implements it; an interface extending one inherits it.</summary>
    private static string BaseList(SyntaxNode node, ISymbol symbol)
    {
        var declaresInterface = node.Ancestors().OfType<TypeBlockSyntax>().FirstOrDefault() is InterfaceBlockSyntax;

        return !declaresInterface && symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }
            ? SymbolUsageKind.Implement
            : SymbolUsageKind.Inherit;
    }

    // ── signature helpers ────────────────────────────────────────────────────

    private static ISymbol? FieldSymbol(ModifiedIdentifierSyntax identifier, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(identifier) is not IFieldSymbol field) return null;

        // An event field's identifier declares the backing field; the member a reader sees is the event.
        return field.AssociatedSymbol ?? field;
    }

    private static string TypeSignature(TypeBlockSyntax type, ISymbol symbol)
    {
        var keyword = type switch
        {
            InterfaceBlockSyntax => "Interface",
            StructureBlockSyntax => "Structure",
            ModuleBlockSyntax => "Module",
            _ => "Class",
        };

        var name = type.BlockStatement.Identifier.Text + (type.BlockStatement.TypeParameterList?.ToString() ?? "");

        var inherited = type.Inherits
            .SelectMany(statement => statement.Types)
            .Select(baseType => Compact(baseType.ToString()))
            .ToList();
        var implemented = type.Implements
            .SelectMany(statement => statement.Types)
            .Select(@interface => Compact(@interface.ToString()))
            .ToList();

        var bases = inherited.Count > 0 ? " Inherits " + string.Join(", ", inherited) : "";
        bases += implemented.Count > 0 ? " Implements " + string.Join(", ", implemented) : "";

        return $"{Header(type, symbol)} {keyword} {name}{bases}";
    }

    private static string FieldSignature(ModifiedIdentifierSyntax identifier, ISymbol symbol)
    {
        var declarator = identifier.Parent as VariableDeclaratorSyntax;
        var declaration = declarator?.Parent as FieldDeclarationSyntax;
        if (declarator is null) return identifier.Identifier.Text;

        var type = TypeOf(declarator.AsClause)?.ToString();
        var initializer = declarator.Initializer is null ? "" : " = " + Compact(declarator.Initializer.Value.ToString());
        var header = Header(declaration ?? (SyntaxNode)identifier, symbol);
        var asClause = type is null ? "" : " As " + Compact(type);

        return Compact($"{header} {identifier.Identifier.Text}{asClause}{initializer}");
    }

    /// <summary>
    /// The source header with the accessibility made explicit. VB defaults differ per declaration kind
    /// (<c>Public</c> in a Module, <c>Private</c> for a <c>Dim</c> field), so a signature that leaves the
    /// modifier out would not say what the declaration is.
    /// </summary>
    private static string SignedHeader(SyntaxNode declaration, ISymbol symbol) =>
        Compact(Header(declaration, symbol) + " " + StripModifiers(declaration));

    /// <summary>The declaration's own text without a duplicated modifier prefix.</summary>
    private static string StripModifiers(SyntaxNode declaration)
    {
        var text = declaration.ToString();
        var modifiers = ModifiersOf(declaration).ToList();
        if (modifiers.Count == 0) return text;

        var prefix = string.Join(" ", modifiers.Select(token => token.Text));
        var trimmed = text.TrimStart();
        return trimmed.StartsWith(prefix, StringComparison.Ordinal)
            ? trimmed[prefix.Length..].TrimStart()
            : trimmed;
    }

    private static string Header(SyntaxNode node, ISymbol symbol)
    {
        var modifiers = ModifiersOf(node).Select(token => token.Text).ToList();
        if (!modifiers.Any(IsAccessibility)) modifiers.Insert(0, AccessibilityText(symbol));

        return string.Join(" ", modifiers.Where(part => part.Length > 0));
    }

    private static bool IsAccessibility(string text) => text is
        "Public" or "Private" or "Protected" or "Friend";

    private static IEnumerable<SyntaxToken> ModifiersOf(SyntaxNode node) => node switch
    {
        TypeBlockSyntax type => type.BlockStatement.Modifiers,
        EnumBlockSyntax enumeration => enumeration.EnumStatement.Modifiers,
        MethodBlockSyntax method => method.SubOrFunctionStatement.Modifiers,
        MethodStatementSyntax statement => statement.Modifiers,
        ConstructorBlockSyntax constructor => constructor.SubNewStatement.Modifiers,
        OperatorBlockSyntax @operator => @operator.OperatorStatement.Modifiers,
        PropertyBlockSyntax property => property.PropertyStatement.Modifiers,
        PropertyStatementSyntax property => property.Modifiers,
        EventBlockSyntax @event => @event.EventStatement.Modifiers,
        EventStatementSyntax @event => @event.Modifiers,
        DelegateStatementSyntax @delegate => @delegate.Modifiers,
        FieldDeclarationSyntax field => field.Modifiers,
        ModifiedIdentifierSyntax identifier => ModifiersOf(identifier.Parent!),
        _ => [],
    };

    private static string AccessibilityText(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "Public",
        Accessibility.Internal => "Friend",
        Accessibility.Protected => "Protected",
        Accessibility.ProtectedOrInternal => "Protected Friend",
        Accessibility.ProtectedAndInternal => "Private Protected",
        Accessibility.Private => "Private",
        _ => "Private",
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
