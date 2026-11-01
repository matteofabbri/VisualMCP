using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.Style;

namespace Snitch.Languages.VB.Style;

// The rules that rewrite a declaration rather than an expression. A VB member is a statement — a method
// block, a property block, a field — so each of them is one splice in the statement list of its type,
// which is why VbBlocks also reads a type block's member list.

/// <summary>
/// The declaration that carries a member's modifier list, for the rules that read or rewrite it. A block
/// member keeps its words on the statement that opens it (<c>Public Sub</c>), a field keeps them on itself.
/// </summary>
internal static class VbMemberSyntax
{
    /// <summary>The statement of the member that owns the modifier list.</summary>
    public static SyntaxNode? DeclarationOf(SyntaxNode member) => member switch
    {
        MethodBlockSyntax block => block.SubOrFunctionStatement,
        ConstructorBlockSyntax block => block.SubNewStatement,
        PropertyBlockSyntax block => block.PropertyStatement,
        OperatorBlockSyntax block => block.OperatorStatement,
        AccessorBlockSyntax block => block.AccessorStatement,
        EventBlockSyntax block => block.EventStatement,
        ClassBlockSyntax block => block.ClassStatement,
        ModuleBlockSyntax block => block.ModuleStatement,
        StructureBlockSyntax block => block.StructureStatement,
        InterfaceBlockSyntax block => block.InterfaceStatement,
        EnumBlockSyntax block => block.EnumStatement,
        FieldDeclarationSyntax => member,
        PropertyStatementSyntax => member,
        EventStatementSyntax => member,
        DelegateStatementSyntax => member,
        _ => null,
    };

    /// <summary>The modifier list of a declaration the switch above named.</summary>
    public static SyntaxTokenList ModifiersOf(SyntaxNode declaration) => declaration switch
    {
        MethodStatementSyntax statement => statement.Modifiers,
        SubNewStatementSyntax statement => statement.Modifiers,
        PropertyStatementSyntax statement => statement.Modifiers,
        OperatorStatementSyntax statement => statement.Modifiers,
        AccessorStatementSyntax statement => statement.Modifiers,
        EventStatementSyntax statement => statement.Modifiers,
        ClassStatementSyntax statement => statement.Modifiers,
        ModuleStatementSyntax statement => statement.Modifiers,
        StructureStatementSyntax statement => statement.Modifiers,
        InterfaceStatementSyntax statement => statement.Modifiers,
        EnumStatementSyntax statement => statement.Modifiers,
        FieldDeclarationSyntax statement => statement.Modifiers,
        DelegateStatementSyntax statement => statement.Modifiers,
        _ => default,
    };

    /// <summary>True for a declaration that an accessibility modifier can be written on.</summary>
    public static bool AcceptsAccessibility(SyntaxNode declaration) =>
        declaration is MethodStatementSyntax or SubNewStatementSyntax or PropertyStatementSyntax
            or OperatorStatementSyntax or EventStatementSyntax or ClassStatementSyntax or ModuleStatementSyntax
            or StructureStatementSyntax or InterfaceStatementSyntax or EnumStatementSyntax
            or FieldDeclarationSyntax or DelegateStatementSyntax;

    public static bool HasAccessibility(SyntaxTokenList modifiers) => modifiers.Any(token => token.Kind() is
        SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.FriendKeyword);

    /// <summary>The accessibility VB gives the declaration when nothing is written.</summary>
    public static string DefaultAccessibility(SyntaxNode member) =>
        member is FieldDeclarationSyntax && member.Parent is not ModuleBlockSyntax ? "Private" : "Public";
}

/// <summary>IDE0036: the modifiers of a declaration in the order <c>visual_basic_preferred_modifier_order</c> asks for.</summary>
public sealed class ModifierOrderRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0036", "Order modifiers",
        "Private Shared Function → Shared Private Function when the repository's modifier order says so");

    public override string? RequiredOption => "visual_basic_preferred_modifier_order";

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preferred = context.Options.ModifierOrder;
        if (preferred.Count == 0) return null;

        var declaration = VbMemberSyntax.DeclarationOf(node);
        if (declaration is null) return null;

        var modifiers = VbMemberSyntax.ModifiersOf(declaration);
        if (modifiers.Count < 2) return null;

        var groups = Group(modifiers);
        if (groups.Count < 2) return null;

        var ordered = groups
            .OrderBy(group => Rank(preferred, group.Text))
            .SelectMany(group => group.Tokens)
            .ToList();

        if (ordered.Select(token => token.Text).SequenceEqual(modifiers.Select(token => token.Text), StringComparer.Ordinal))
            return null;

        var text = node.ToString();
        var start = modifiers[0].SpanStart - node.SpanStart;
        var end = modifiers[modifiers.Count - 1].Span.End - node.SpanStart;
        var replacement = string.Join(" ", ordered.Select(token => token.Text));

        return MatchMember(node, text[..start] + replacement + text[end..]);
    }

    private static int Rank(IReadOnlyList<string> preferred, string text)
    {
        for (var index = 0; index < preferred.Count; index++)
            if (string.Equals(preferred[index], text, StringComparison.Ordinal))
                return index;

        return int.MaxValue;
    }

    /// <summary>
    /// The modifier tokens grouped the way the option spells them: <c>Protected Friend</c> is one
    /// accessibility in VB and one entry in the preferred order, so the two words travel together.
    /// </summary>
    private static List<(string Text, List<SyntaxToken> Tokens)> Group(SyntaxTokenList modifiers)
    {
        var groups = new List<(string Text, List<SyntaxToken> Tokens)>();

        for (var index = 0; index < modifiers.Count; index++)
        {
            var token = modifiers[index];
            if (token.IsKind(SyntaxKind.ProtectedKeyword) && index + 1 < modifiers.Count &&
                modifiers[index + 1].IsKind(SyntaxKind.FriendKeyword))
            {
                groups.Add(("protected friend", [token, modifiers[index + 1]]));
                index++;
                continue;
            }

            groups.Add((token.Text.ToLowerInvariant(), [token]));
        }

        return groups;
    }
}

/// <summary>
/// IDE0040: the accessibility modifier is written where the option asks for it, and removed where the
/// option is <c>omit_if_default</c> and the modifier only repeats what VB would assume.
/// </summary>
public sealed class AccessibilityModifierRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0040", "Add accessibility modifiers",
        "Sub F() → Public Sub F(), and Private back to Dim-less default when omit_if_default");

    public override string? RequiredOption => "dotnet_style_require_accessibility_modifiers";

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        var preference = context.Options.Accessibility;
        if (preference == AccessibilityPreference.None) return null;
        if (node.Parent is not TypeBlockSyntax type) return null;

        var declaration = VbMemberSyntax.DeclarationOf(node);
        if (declaration is null || !VbMemberSyntax.AcceptsAccessibility(declaration)) return null;
        if (declaration is EnumStatementSyntax or DelegateStatementSyntax) return null;

        var isInterface = type is InterfaceBlockSyntax;
        if (preference == AccessibilityPreference.ForNonInterfaceMembers && isInterface) return null;

        var modifiers = VbMemberSyntax.ModifiersOf(declaration);
        var hasAccessibility = VbMemberSyntax.HasAccessibility(modifiers);

        if (preference == AccessibilityPreference.OmitIfDefault)
        {
            if (!hasAccessibility) return null;

            var expected = VbMemberSyntax.DefaultAccessibility(node);
            var accessibility = modifiers.First(token => token.Kind() is
                SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.FriendKeyword);

            // Protected Friend is two tokens and never the default, so only a single word is removed.
            if (accessibility.IsKind(SyntaxKind.ProtectedKeyword)) return null;
            if (!string.Equals(accessibility.Text, expected, StringComparison.OrdinalIgnoreCase)) return null;

            var text = node.ToString();
            var start = accessibility.SpanStart - node.SpanStart;
            var end = accessibility.Span.End - node.SpanStart;
            while (end < text.Length && text[end] == ' ') end++;

            return MatchMember(node, text[..start] + text[end..]);
        }

        if (hasAccessibility) return null;
        if (declaration is FieldDeclarationSyntax && modifiers.Any(token => token.IsKind(SyntaxKind.DimKeyword)))
            return null;

        var memberText = node.ToString();
        var insertAt = declaration.SpanStart - node.SpanStart;
        return MatchMember(node, memberText.Insert(insertAt, "Public "));
    }
}

/// <summary>
/// IDE0044: a field that no constructor-free code writes becomes <c>ReadOnly</c>. The verdict is the
/// symbol's references, not a text scan: a compound assignment is a write, a read anywhere blocks it,
/// and the assignment a constructor performs is exactly what <c>ReadOnly</c> allows.
/// </summary>
public sealed class ReadonlyFieldRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0044", "Make field readonly",
        "Private _value As Integer → Private ReadOnly _value As Integer");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;
        if (node is not FieldDeclarationSyntax field) return null;
        if (field.Declarators.Count != 1) return null;
        if (field.AttributeLists.Count > 0) return null;

        var modifiers = field.Modifiers;
        if (modifiers.Any(token => token.Kind() is SyntaxKind.ReadOnlyKeyword or SyntaxKind.ConstKeyword
                or SyntaxKind.WithEventsKeyword or SyntaxKind.ShadowsKeyword))
            return null;

        var declarator = field.Declarators[0];
        if (declarator.Names.Count != 1) return null;
        if (context.Model.GetDeclaredSymbol(declarator.Names[0], context.CancellationToken) is not IFieldSymbol symbol)
            return null;
        if (symbol.DeclaredAccessibility != Accessibility.Private || symbol.IsReadOnly) return null;

        // Reading a ReadOnly field anywhere is legal; only a write outside a constructor blocks the change.
        var references = VbStyleFacts.References(symbol, context.Solution, context.CancellationToken);
        if (references.Any(reference => reference is { Usage: VbSymbolUsage.Write or VbSymbolUsage.ReadWrite, InConstructor: false }))
            return null;

        var text = field.ToString();
        if (modifiers.Any(token => token.IsKind(SyntaxKind.DimKeyword)))
        {
            var dim = modifiers.First(token => token.IsKind(SyntaxKind.DimKeyword));
            var start = dim.SpanStart - field.SpanStart;
            return MatchMember(node, text[..start] + "Private ReadOnly" + text[(dim.Span.End - field.SpanStart)..]);
        }

        var name = declarator.Names[0];
        var insertAt = name.SpanStart - field.SpanStart;
        return MatchMember(node, text.Insert(insertAt, "ReadOnly "));
    }
}

/// <summary>
/// IDE0051: a private member nothing in the solution refers to. The exclusions are the ways a private
/// member is used without being named: an attribute, a <c>Handles</c> clause, an <c>Implements</c> clause,
/// an override, a partial declaration, and a field the designer binds with <c>WithEvents</c>.
/// </summary>
public sealed class UnusedPrivateMemberRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0051", "Remove unused private member",
        "a private Sub nothing refers to is removed");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;
        if (node.Parent is not TypeBlockSyntax) return null;

        var declaration = VbMemberSyntax.DeclarationOf(node);
        if (declaration is null) return null;

        var modifiers = VbMemberSyntax.ModifiersOf(declaration);
        if (!modifiers.Any(token => token.IsKind(SyntaxKind.PrivateKeyword))) return null;
        if (modifiers.Any(token => token.Kind() is SyntaxKind.OverridesKeyword or SyntaxKind.OverridableKeyword
                or SyntaxKind.MustOverrideKeyword or SyntaxKind.NotOverridableKeyword or SyntaxKind.PartialKeyword
                or SyntaxKind.WithEventsKeyword))
            return null;

        if (HasAttributes(declaration)) return null;
        if (HasImplementsOrHandles(declaration)) return null;

        var symbol = context.Model.GetDeclaredSymbol(declaration, context.CancellationToken);
        if (symbol is not (IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.PropertyGet or MethodKind.PropertySet }
            or IPropertySymbol or IFieldSymbol or IEventSymbol))
            return null;

        // A nested type is not a member this rule removes: it is a declaration other declarations can
        // name from a generated file, and the C# catalogue keeps it out too.
        var references = VbStyleFacts.References(symbol, context.Solution, context.CancellationToken);
        if (references.Count != 0) return null;

        return Removal(node);
    }

    private static bool HasAttributes(SyntaxNode declaration) => declaration switch
    {
        MethodStatementSyntax statement => statement.AttributeLists.Count > 0,
        SubNewStatementSyntax statement => statement.AttributeLists.Count > 0,
        PropertyStatementSyntax statement => statement.AttributeLists.Count > 0,
        EventStatementSyntax statement => statement.AttributeLists.Count > 0,
        FieldDeclarationSyntax statement => statement.AttributeLists.Count > 0,
        OperatorStatementSyntax statement => statement.AttributeLists.Count > 0,
        _ => false,
    };

    private static bool HasImplementsOrHandles(SyntaxNode declaration) => declaration switch
    {
        MethodStatementSyntax statement => statement.HandlesClause is not null || statement.ImplementsClause is not null,
        PropertyStatementSyntax statement => statement.ImplementsClause is not null,
        EventStatementSyntax statement => statement.ImplementsClause is not null,
        _ => false,
    };
}

/// <summary>
/// IDE0052: a private field that is written and never read. Removing the field means removing every
/// store too, so the suggestion is a set of text edits over the declaration and each standalone
/// assignment — and only while the stored value can be dropped without losing a side effect.
/// </summary>
public sealed class UnreadPrivateMemberRewrite : VbContextRewrite
{
    public override StyleRule Rule { get; } = new(
        "IDE0052", "Remove unread private member",
        "a private field only ever assigned is removed together with its assignments");

    public override StyleMatch? TryMatch(SyntaxNode node, StyleContext context)
    {
        if (context.Solution is null || !context.IsPristine) return null;
        if (node is not FieldDeclarationSyntax field) return null;
        if (field.Declarators.Count != 1) return null;
        if (field.AttributeLists.Count > 0) return null;
        if (field.Modifiers.Any(token => token.Kind() is SyntaxKind.ReadOnlyKeyword or SyntaxKind.ConstKeyword
                or SyntaxKind.WithEventsKeyword))
            return null;

        var declarator = field.Declarators[0];
        if (declarator.Names.Count != 1) return null;
        if (context.Model.GetDeclaredSymbol(declarator.Names[0], context.CancellationToken) is not IFieldSymbol symbol)
            return null;
        if (symbol.DeclaredAccessibility != Accessibility.Private) return null;

        var path = field.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(path)) return null;

        var references = VbStyleFacts.References(symbol, context.Solution, context.CancellationToken);
        if (references.Count == 0) return null;                       // nothing writes it: that is IDE0051
        if (references.Any(reference => reference.Usage != VbSymbolUsage.Write)) return null;

        var edits = new List<SpanEdit>
        {
            new(path, field.Span.Start, field.Span.Length),
        };

        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference.FilePath)) return null;

            var assignment = reference.Node.AncestorsAndSelf().OfType<AssignmentStatementSyntax>().FirstOrDefault();
            if (assignment is null || !assignment.IsKind(SyntaxKind.SimpleAssignmentStatement)) return null;
            if (assignment.Parent is not MethodBlockBaseSyntax and not TypeBlockSyntax) return null;
            if (!VbStyleFacts.IsSideEffectFree(assignment.Right)) return null;

            edits.Add(new SpanEdit(reference.FilePath, assignment.Span.Start, assignment.Span.Length));
        }

        return Shape(Rule, field, edits, symbol.Name, string.Empty);
    }
}
