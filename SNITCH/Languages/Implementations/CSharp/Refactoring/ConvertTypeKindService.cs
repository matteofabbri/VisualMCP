using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Abstract class to interface" and its inverse: an abstract class whose members are all
/// abstract and whose state is only constants becomes an interface, and an interface whose members are
/// all abstract becomes an abstract class. Both directions rewrite the implementers/derived types in the
/// same operation — an interface conversion has to drop <c>override</c> from the derived members, and the
/// inverse has to add it — and both are gated on the compiler, which is what turns every case the rules
/// below cannot express into a refusal instead of a broken tree.
/// </summary>
public interface IConvertTypeKindService
{
    Task<object> ConvertTypeKind(
        string filePath,
        string typeName,
        string target,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class ConvertTypeKindService : IConvertTypeKindService
{
    private readonly IRoslynWorkspace _workspace;

    public ConvertTypeKindService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> ConvertTypeKind(
        string filePath,
        string typeName,
        string target,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(typeName)) return new { error = "The type to convert is required." };

        var direction = Normalize(target);
        if (direction is null)
            return new { error = $"target must be 'interface' or 'abstract-class'; '{target}' is neither." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return new { error = $"Invalid path '{filePath}': {ex.Message}" }; }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null) return new { error = $"{path} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read the syntax tree of {Path.GetFileName(path)}." };

        if (direction == "interface")
        {
            var declaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
            if (declaration is null) return new { error = $"No class '{typeName}' in {Path.GetFileName(path)}." };
            if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
                return new { error = $"'{typeName}' did not resolve to a type." };

            return await ToInterfaceAsync(solution, document, root, model, declaration, symbol, dryRun, cancellationToken);
        }

        var interfaceDeclaration = root.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == typeName);
        if (interfaceDeclaration is null) return new { error = $"No interface '{typeName}' in {Path.GetFileName(path)}." };
        if (model.GetDeclaredSymbol(interfaceDeclaration, cancellationToken) is not INamedTypeSymbol interfaceSymbol)
            return new { error = $"'{typeName}' did not resolve to a type." };

        return await ToAbstractClassAsync(solution, document, root, interfaceDeclaration, interfaceSymbol, dryRun, cancellationToken);
    }

    private static string? Normalize(string target) => target.Trim().ToLowerInvariant() switch
    {
        "interface" or "iface" => "interface",
        "abstract-class" or "abstract" or "class" => "abstract-class",
        _ => null,
    };

    // ── abstract class → interface ───────────────────────────────────────────────────────

    private async Task<object> ToInterfaceAsync(
        Solution solution,
        Document document,
        SyntaxNode root,
        SemanticModel model,
        ClassDeclarationSyntax declaration,
        INamedTypeSymbol symbol,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var name = declaration.Identifier.ValueText;

        if (!declaration.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return new { error = $"'{name}' is not abstract; a concrete class has behaviour and state an interface cannot hold." };

        if (symbol.DeclaringSyntaxReferences.Length != 1)
            return new { error = $"'{name}' is declared in {symbol.DeclaringSyntaxReferences.Length} places (partial); convert a type with one declaration." };

        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            return new { error = $"'{name}' derives from '{baseType.ToDisplayString()}'; an interface can only inherit interfaces." };

        var refused = RefuseMembers(declaration, cancellationToken);
        if (refused is not null) return new { error = refused };

        var keyword = Token(SyntaxKind.InterfaceKeyword).WithTriviaFrom(declaration.Keyword);
        var modifiers = TokenList(declaration.Modifiers.Where(modifier =>
            !modifier.IsKind(SyntaxKind.AbstractKeyword) && !modifier.IsKind(SyntaxKind.SealedKeyword)));

        var members = declaration.Members
            .Where(member => member is not ConstructorDeclarationSyntax)
            .Select(MemberTransform.ToInterfaceMember)
            .ToList();

        var interfaceDeclaration = InterfaceDeclaration(declaration.Identifier)
            .WithKeyword(keyword)
            .WithAttributeLists(declaration.AttributeLists)
            .WithModifiers(modifiers)
            .WithTypeParameterList(declaration.TypeParameterList)
            .WithBaseList(declaration.BaseList)
            .WithConstraintClauses(declaration.ConstraintClauses)
            .WithOpenBraceToken(declaration.OpenBraceToken)
            .WithMembers(List(members))
            .WithCloseBraceToken(declaration.CloseBraceToken)
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(declaration.GetTrailingTrivia());

        var edits = new List<Edit> { new(document, root.ReplaceNode(declaration, interfaceDeclaration), root.ToFullString()) };

        // Every override of one of these members stops overriding: the member now comes from an
        // interface, and `override` there is CS0115.
        var derived = await SymbolFinder.FindDerivedClassesAsync(
            symbol, solution, transitive: true, cancellationToken: cancellationToken);

        foreach (var derivedType in derived)
        {
            foreach (var reference in derivedType.DeclaringSyntaxReferences)
            {
                var declarationNode = await reference.GetSyntaxAsync(cancellationToken);
                var derivedDocument = solution.GetDocument(declarationNode.SyntaxTree);
                if (derivedDocument is null) continue;
                var derivedRoot = await derivedDocument.GetSyntaxRootAsync(cancellationToken);
                var derivedModel = await derivedDocument.GetSemanticModelAsync(cancellationToken);
                if (derivedRoot is null || derivedModel is null) continue;

                var targets = derivedRoot.DescendantNodes()
                    .Where(node => node is MethodDeclarationSyntax or PropertyDeclarationSyntax
                                   or IndexerDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax)
                    .Where(node => node.DescendantTokens().Any(token => token.IsKind(SyntaxKind.OverrideKeyword)))
                    .Where(node => OverridesHidesOrImplements(derivedModel, node, symbol, cancellationToken))
                    .ToList();

                if (targets.Count == 0) continue;

                var newRoot = derivedRoot.ReplaceNodes(targets, (original, _) => MemberTransform.RemoveOverride(original));
                edits.Add(new Edit(derivedDocument, newRoot, derivedRoot.ToFullString()));
            }
        }

        return await ApplyAsync(solution, edits, "interface", name, dryRun, cancellationToken);
    }

    /// <summary>The members an interface cannot hold, named one by one: the answer says what to move.</summary>
    private string? RefuseMembers(ClassDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        foreach (var member in declaration.Members)
        {
            switch (member)
            {
                case ConstructorDeclarationSyntax constructor:
                    if (constructor.Modifiers.Any(SyntaxKind.StaticKeyword))
                        return "a static constructor has no interface equivalent.";
                    if (constructor.ParameterList.Parameters.Count > 0 ||
                        (constructor.Initializer is not null && constructor.Initializer.ArgumentList.Arguments.Count > 0) ||
                        (constructor.Body is not null && constructor.Body.Statements.Count > 0))
                        return "the class has a constructor with parameters or a body; an interface has none, so remove it first.";
                    break;

                case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword)
                                                      && !field.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return $"'{field.Declaration.Variables.First().Identifier.ValueText}' is instance state; an interface cannot hold it. Move the state to the implementers or keep the class.";

                case DestructorDeclarationSyntax:
                    return "the class has a destructor; an interface cannot.";

                case MethodDeclarationSyntax method when !method.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                                         && !method.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return $"'{method.Identifier.ValueText}' has a body; a default interface method is reached only through the interface, so the conversion would change dispatch. Make it abstract first.";

                case PropertyDeclarationSyntax property when !property.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                                             && !property.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return $"property '{property.Identifier.ValueText}' has state or a body; an interface can only declare it. Make it abstract first.";

                case IndexerDeclarationSyntax indexer when !indexer.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                                           && !indexer.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return "the indexer has a body; make it abstract first.";

                case EventDeclarationSyntax @event when !@event.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                                       && !@event.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return $"event '{@event.Identifier.ValueText}' has accessors; make it abstract first.";

                case EventFieldDeclarationSyntax @event when !@event.Modifiers.Any(SyntaxKind.AbstractKeyword)
                                                            && !@event.Modifiers.Any(SyntaxKind.StaticKeyword):
                    return $"event '{@event.Declaration.Variables.First().Identifier.ValueText}' is a field-like event with backing storage; make it abstract first.";

                // Accessors with a body on a member that also carries `abstract` cannot exist, so the
                // method/property cases above already cover the body check.
                default:
                    break;
            }
        }

        return null;
    }

    // ── interface → abstract class ───────────────────────────────────────────────────────

    private async Task<object> ToAbstractClassAsync(
        Solution solution,
        Document document,
        SyntaxNode root,
        InterfaceDeclarationSyntax declaration,
        INamedTypeSymbol symbol,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var name = declaration.Identifier.ValueText;

        if (symbol.DeclaringSyntaxReferences.Length != 1)
            return new { error = $"'{name}' is declared in {symbol.DeclaringSyntaxReferences.Length} places (partial); convert a type with one declaration." };

        foreach (var member in declaration.Members)
        {
            var hasBody = member switch
            {
                MethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
                PropertyDeclarationSyntax property => property.ExpressionBody is not null ||
                                                      (property.AccessorList?.Accessors.Any(accessor =>
                                                          accessor.Body is not null || accessor.ExpressionBody is not null) ?? false),
                IndexerDeclarationSyntax indexer => indexer.ExpressionBody is not null ||
                                                    (indexer.AccessorList?.Accessors.Any(accessor =>
                                                        accessor.Body is not null || accessor.ExpressionBody is not null) ?? false),
                EventDeclarationSyntax @event => @event.AccessorList?.Accessors.Any(accessor => accessor.Body is not null) ?? false,
                _ => false,
            };

            if (hasBody)
                return new { error = "A default interface implementation becomes a virtual method on the class, which is a dispatch change; remove the body first." };
        }

        var implementers = (await SymbolFinder.FindImplementationsAsync(
                symbol, solution, cancellationToken: cancellationToken))
            .OfType<INamedTypeSymbol>()
            .ToList();

        var derivedInterfaces = (await SymbolFinder.FindDerivedInterfacesAsync(
                symbol, solution, transitive: true, cancellationToken: cancellationToken))
            .ToList();
        if (derivedInterfaces.Count > 0)
            return new { error = $"'{derivedInterfaces[0].Name}' inherits this interface; an interface cannot inherit a class." };

        var keyword = Token(SyntaxKind.ClassKeyword).WithTriviaFrom(declaration.Keyword);
        var modifiers = declaration.Modifiers.Any(SyntaxKind.AbstractKeyword)
            ? declaration.Modifiers
            : declaration.Modifiers.Add(Token(SyntaxKind.AbstractKeyword).WithTrailingTrivia(Space));

        var members = declaration.Members.Select(MemberTransform.ToClassMember).ToList();

        var classDeclaration = ClassDeclaration(declaration.Identifier)
            .WithKeyword(keyword)
            .WithAttributeLists(declaration.AttributeLists)
            .WithModifiers(modifiers)
            .WithTypeParameterList(declaration.TypeParameterList)
            .WithBaseList(declaration.BaseList)
            .WithConstraintClauses(declaration.ConstraintClauses)
            .WithOpenBraceToken(declaration.OpenBraceToken)
            .WithMembers(List(members))
            .WithCloseBraceToken(declaration.CloseBraceToken)
            .WithLeadingTrivia(declaration.GetLeadingTrivia())
            .WithTrailingTrivia(declaration.GetTrailingTrivia());

        var edits = new List<Edit> { new(document, root.ReplaceNode(declaration, classDeclaration), root.ToFullString()) };

        // Each implementer's members were implicit implementations; a class needs `override` on them.
        foreach (var implementer in implementers)
        {
            switch (implementer.TypeKind)
            {
                case TypeKind.Struct:
                    return new { error = $"struct '{implementer.Name}' implements this interface; a struct cannot inherit an abstract class." };
                case TypeKind.Interface:
                    return new { error = $"interface '{implementer.Name}' implements this interface; it cannot inherit a class." };
                case TypeKind.Class when implementer.BaseType is { SpecialType: not SpecialType.System_Object }:
                    return new { error = $"'{implementer.Name}' already derives from '{implementer.BaseType!.ToDisplayString()}'; a class has one base class." };
                case TypeKind.Class when implementer.IsRecord:
                    return new { error = $"'{implementer.Name}' is a record; a record can only inherit a record." };
            }

            foreach (var reference in implementer.DeclaringSyntaxReferences)
            {
                var declarationNode = await reference.GetSyntaxAsync(cancellationToken);
                if (declarationNode is not TypeDeclarationSyntax typeDeclaration) continue;

                var implementerDocument = solution.GetDocument(declarationNode.SyntaxTree);
                if (implementerDocument is null) continue;
                var implementerRoot = await implementerDocument.GetSyntaxRootAsync(cancellationToken);
                var implementerModel = await implementerDocument.GetSemanticModelAsync(cancellationToken);
                if (implementerRoot is null || implementerModel is null) continue;

                var targets = new List<SyntaxNode>();
                foreach (var member in typeDeclaration.Members)
                {
                    var memberSymbol = implementerModel.GetDeclaredSymbol(member, cancellationToken);
                    if (memberSymbol is null) continue;
                    if (memberSymbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 })
                        return new { error = $"'{implementer.Name}' implements a member explicitly by interface name; explicit implementations cannot be marked override." };
                    if (memberSymbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 })
                        return new { error = $"'{implementer.Name}' implements a property explicitly by interface name; explicit implementations cannot be marked override." };

                    var existingModifiers = ModifiersOf(member);
                    if (ImplementsMemberOf(memberSymbol, symbol) && !existingModifiers.Any(token => token.IsKind(SyntaxKind.OverrideKeyword)))
                        targets.Add(member);
                }

                if (targets.Count == 0) continue;

                var newRoot = implementerRoot.ReplaceNodes(targets, (original, _) => MemberTransform.AddOverride(original));
                edits.Add(new Edit(implementerDocument, newRoot, implementerRoot.ToFullString()));
            }
        }

        return await ApplyAsync(solution, edits, "abstract-class", name, dryRun, cancellationToken);
    }

    /// <summary>
    /// Whether this symbol is the implementation of a member declared by <paramref name="contract"/>.
    /// The interface member list is walked explicitly because the answer must be exact: adding
    /// <c>override</c> to a member that implements nothing is a compiler error.
    /// </summary>
    private static bool ImplementsMemberOf(ISymbol member, INamedTypeSymbol contract)
    {
        var containing = member.ContainingType;
        if (containing is null) return false;

        return contract.GetMembers().Any(contractMember =>
        {
            var implementation = containing.FindImplementationForInterfaceMember(contractMember);
            if (implementation is null) return false;

            if (SymbolEqualityComparer.Default.Equals(implementation, member))
                return true;

            // A property's accessors implement the interface accessors; the property itself is the answer.
            return implementation is IPropertySymbol property &&
                   SymbolEqualityComparer.Default.Equals(property, member);
        });
    }

    private static bool OverridesHidesOrImplements(
        SemanticModel model, SyntaxNode node, INamedTypeSymbol converted, CancellationToken cancellationToken)
    {
        var symbol = model.GetDeclaredSymbol(node, cancellationToken);
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };

        for (var current = overridden; current is not null;)
        {
            if (SymbolEqualityComparer.Default.Equals(current.ContainingType?.OriginalDefinition, converted.OriginalDefinition))
                return true;

            current = current switch
            {
                IMethodSymbol method => method.OverriddenMethod,
                IPropertySymbol property => property.OverriddenProperty,
                IEventSymbol @event => @event.OverriddenEvent,
                _ => null,
            };
        }

        return false;
    }

    // ── application ─────────────────────────────────────────────────────────────────────

    private async Task<object> ApplyAsync(
        Solution solution,
        List<Edit> edits,
        string target,
        string typeName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var texts = new List<(Document Document, string Text, string Diff)>();
        var projection = solution;
        foreach (var edit in edits)
        {
            var text = edit.NewRoot.ToFullString();
            texts.Add((edit.Document, text, RefactoringFileWriter.Diff(edit.OriginalText, text)));
            projection = projection.WithDocumentText(edit.Document.Id, SourceText.From(text));
        }

        var projects = edits.Select(edit => edit.Document.Project.Id).Distinct().ToArray();
        var gate = await RefactoringCompilerGate.CompareAsync(solution, projection, projects, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the conversion");

        if (!dryRun)
        {
            foreach (var (editDocument, text, _) in texts)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, editDocument, text, cancellationToken);
        }

        var changed = texts.Select(item => new
        {
            file = item.Document.FilePath,
            diff = item.Diff,
        }).ToList();

        return new
        {
            @refactoring = "convert_type_kind",
            type = typeName,
            target,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            files = changed,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private sealed record Edit(Document Document, SyntaxNode NewRoot, string OriginalText);

    private static SyntaxTokenList ModifiersOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax method => method.Modifiers,
        PropertyDeclarationSyntax property => property.Modifiers,
        IndexerDeclarationSyntax indexer => indexer.Modifiers,
        EventDeclarationSyntax @event => @event.Modifiers,
        EventFieldDeclarationSyntax @event => @event.Modifiers,
        FieldDeclarationSyntax field => field.Modifiers,
        BaseTypeDeclarationSyntax type => type.Modifiers,
        DelegateDeclarationSyntax @delegate => @delegate.Modifiers,
        _ => default(SyntaxTokenList),
    };

    /// <summary>The two member rewrites, kept in one place so both directions use the same trivia rules.</summary>
    private static class MemberTransform
    {
        public static MemberDeclarationSyntax ToInterfaceMember(MemberDeclarationSyntax member) => Apply(member, stripped => stripped switch
        {
            MethodDeclarationSyntax method => method.WithModifiers(DropAbstract(method.Modifiers)),
            PropertyDeclarationSyntax property => property.WithModifiers(DropAbstract(property.Modifiers)),
            IndexerDeclarationSyntax indexer => indexer.WithModifiers(DropAbstract(indexer.Modifiers)),
            EventDeclarationSyntax @event => @event.WithModifiers(DropAbstract(@event.Modifiers)),
            EventFieldDeclarationSyntax @event => @event.WithModifiers(DropAbstract(@event.Modifiers)),
            _ => stripped,
        });

        public static MemberDeclarationSyntax ToClassMember(MemberDeclarationSyntax member) => Apply(member, stripped => stripped switch
        {
            MethodDeclarationSyntax method => method.WithModifiers(PublicAbstract(method.Modifiers)),
            PropertyDeclarationSyntax property => property.WithModifiers(PublicAbstract(property.Modifiers)),
            IndexerDeclarationSyntax indexer => indexer.WithModifiers(PublicAbstract(indexer.Modifiers)),
            EventDeclarationSyntax @event => @event.WithModifiers(PublicAbstract(@event.Modifiers)),
            EventFieldDeclarationSyntax @event => @event.WithModifiers(PublicAbstract(@event.Modifiers)),
            // A constant, a static field or a nested type keeps its shape; it only loses the implicit
            // public an interface gave it.
            FieldDeclarationSyntax field => field.WithModifiers(EnsurePublic(field.Modifiers)),
            BaseTypeDeclarationSyntax type => type.WithModifiers(EnsurePublic(type.Modifiers)),
            DelegateDeclarationSyntax @delegate => @delegate.WithModifiers(EnsurePublic(@delegate.Modifiers)),
            _ => stripped,
        });

        public static SyntaxNode AddOverride(SyntaxNode node) => node is MemberDeclarationSyntax member
            ? Apply(member, stripped => stripped switch
            {
                MethodDeclarationSyntax method => method.WithModifiers(PublicAndOverride(method.Modifiers)),
                PropertyDeclarationSyntax property => property.WithModifiers(PublicAndOverride(property.Modifiers)),
                IndexerDeclarationSyntax indexer => indexer.WithModifiers(PublicAndOverride(indexer.Modifiers)),
                EventDeclarationSyntax @event => @event.WithModifiers(PublicAndOverride(@event.Modifiers)),
                EventFieldDeclarationSyntax @event => @event.WithModifiers(PublicAndOverride(@event.Modifiers)),
                _ => stripped,
            })
            : node;

        public static SyntaxNode RemoveOverride(SyntaxNode node) => node is MemberDeclarationSyntax member
            ? Apply(member, stripped => stripped switch
            {
                MethodDeclarationSyntax method => method.WithModifiers(DropOverride(method.Modifiers)),
                PropertyDeclarationSyntax property => property.WithModifiers(DropOverride(property.Modifiers)),
                IndexerDeclarationSyntax indexer => indexer.WithModifiers(DropOverride(indexer.Modifiers)),
                EventDeclarationSyntax @event => @event.WithModifiers(DropOverride(@event.Modifiers)),
                EventFieldDeclarationSyntax @event => @event.WithModifiers(DropOverride(@event.Modifiers)),
                _ => stripped,
            })
            : node;

        /// <summary>
        /// Rewrites a member's modifiers without moving it on its line: the leading trivia belongs to the
        /// member, not to whichever modifier happened to come first, so it is lifted, the edit runs on a
        /// stripped copy, and it is put back on the new first token.
        /// </summary>
        private static MemberDeclarationSyntax Apply(
            MemberDeclarationSyntax member, Func<MemberDeclarationSyntax, MemberDeclarationSyntax> edit)
        {
            var leading = member.GetLeadingTrivia();
            var stripped = member.WithLeadingTrivia(default(SyntaxTriviaList));
            return edit(stripped).WithLeadingTrivia(leading);
        }

        private static SyntaxTokenList DropAbstract(SyntaxTokenList modifiers) =>
            TokenList(modifiers.Where(token => !token.IsKind(SyntaxKind.AbstractKeyword) && !token.IsKind(SyntaxKind.VirtualKeyword)));

        private static SyntaxTokenList DropOverride(SyntaxTokenList modifiers) =>
            TokenList(modifiers.Where(token => !token.IsKind(SyntaxKind.OverrideKeyword) && !token.IsKind(SyntaxKind.SealedKeyword)));

        /// <summary>Interface members are public by default; a class member without an accessibility is private.</summary>
        private static SyntaxTokenList PublicAbstract(SyntaxTokenList modifiers)
        {
            var result = EnsurePublic(modifiers);

            return result.Any(token => token.IsKind(SyntaxKind.AbstractKeyword))
                ? result
                : result.Add(Token(SyntaxKind.AbstractKeyword).WithTrailingTrivia(Space));
        }

        private static SyntaxTokenList EnsurePublic(SyntaxTokenList modifiers) =>
            HasAccessibility(modifiers)
                ? modifiers
                : modifiers.Insert(0, Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space));

        private static SyntaxTokenList PublicAndOverride(SyntaxTokenList modifiers)
        {
            var result = EnsurePublic(modifiers);

            return result.Any(token => token.IsKind(SyntaxKind.OverrideKeyword))
                ? result
                : result.Add(Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(Space));
        }

        private static bool HasAccessibility(SyntaxTokenList modifiers) => modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword)
            || token.IsKind(SyntaxKind.InternalKeyword)
            || token.IsKind(SyntaxKind.ProtectedKeyword)
            || token.IsKind(SyntaxKind.PrivateKeyword));
    }
}
