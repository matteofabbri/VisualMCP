using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// Rider's "Property to method" (Property2Function): a property becomes one or two methods and every
/// read/write is rewritten. A get-only property produces <c>GetX()</c>. A get/set property produces
/// <c>GetX()</c> and <c>SetX(value)</c>, and <c>x.P = v</c> becomes <c>x.SetP(v)</c>.
/// </summary>
public interface IPropertyToFunctionService
{
    Task<object> PropertyToFunction(
        string filePath,
        string propertyName,
        string? containingType = null,
        string? getterName = null,
        string? setterName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default);
}

public sealed class PropertyToFunctionService : IPropertyToFunctionService
{
    private readonly IRoslynWorkspace _workspace;

    public PropertyToFunctionService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> PropertyToFunction(
        string filePath,
        string propertyName,
        string? containingType = null,
        string? getterName = null,
        string? setterName = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (string.IsNullOrWhiteSpace(propertyName)) return new { error = "A property name is required." };

        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return await Snitch.Languages.VB.Refactoring.VbRefactoringTail.PropertyToFunctionAsync(
                _workspace, filePath, propertyName, containingType, getterName, setterName, dryRun, cancellationToken);

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

        var candidates = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(property => property.Identifier.ValueText == propertyName)
            .Where(property => containingType is null ||
                               string.Equals(model.GetDeclaredSymbol(property, cancellationToken)?.ContainingType?.Name,
                                             containingType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return new { error = $"No property '{propertyName}' in {Path.GetFileName(path)}." };
        if (candidates.Count > 1) return new { error = $"Several properties named '{propertyName}'; pass containingType." };

        var declaration = candidates[0];
        if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IPropertySymbol propertySymbol)
            return new { error = $"'{propertyName}' did not resolve to a property." };

        var refusal = RefuseFor(declaration, propertySymbol);
        if (refusal is not null) return new { error = refusal };

        var owner = propertySymbol.ContainingType;
        if (owner is null) return new { error = $"'{propertyName}' has no containing type." };

        var getterAccessor = Accessor(declaration, SyntaxKind.GetAccessorDeclaration);
        var setterAccessor = Accessor(declaration, SyntaxKind.SetAccessorDeclaration);

        var getter = getterAccessor is null ? null : getterName ?? "Get" + Capitalise(propertyName);
        var setter = setterAccessor is null ? null : setterName ?? "Set" + Capitalise(propertyName);

        if (getter is not null && !SyntaxFacts.IsValidIdentifier(getter))
            return new { error = $"'{getter}' is not a valid method name; pass getterName explicitly." };
        if (setter is not null && !SyntaxFacts.IsValidIdentifier(setter))
            return new { error = $"'{setter}' is not a valid method name; pass setterName explicitly." };

        foreach (var name in new[] { getter, setter }.Where(name => name is not null))
            if (owner.GetMembers(name!).Length > 0)
                return new { error = $"'{owner.ToDisplayString()}' already has a member named '{name}'; pass another name." };

        // ── Every use is rewritten from the ORIGINAL semantic model ────────────────────────
        var references = await SymbolFinder.FindReferencesAsync(propertySymbol, solution, cancellationToken);
        var edits = new List<(Document Document, string NewText, int Usages, string Preview)>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var documents = new Dictionary<DocumentId, Document> { [document.Id] = document };

        foreach (var reference in references)
            foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource))
                if (!documents.ContainsKey(location.Document.Id))
                    documents[location.Document.Id] = location.Document;

        foreach (var (documentId, target) in documents)
        {
            var targetRoot = await target.GetSyntaxRootAsync(cancellationToken);
            var targetModel = await target.GetSemanticModelAsync(cancellationToken);
            if (targetRoot is null || targetModel is null) continue;

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
            var localKinds = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var reference in references)
            {
                foreach (var location in reference.Locations.Where(candidate => candidate.Location.IsInSource &&
                                                                                candidate.Document.Id == documentId))
                {
                    var span = location.Location.SourceSpan;
                    if (documentId == document.Id && declaration.Span.Contains(span)) continue;

                    var node = targetRoot.FindNode(span, getInnermostNodeForTie: true);
                    if (node is not IdentifierNameSyntax identifier) continue;

                    var (edit, error) = Classify(identifier, targetModel, propertySymbol, getter, setter, cancellationToken);
                    if (error is not null) return new { error, file = target.FilePath, line = LineOf(node) };

                    if (edit is null) continue;
                    var (editTarget, replacement, kind) = edit.Value;

                    if (replacements.Keys.Any(existing => existing.Span.Contains(editTarget.Span))) continue;
                    foreach (var contained in replacements.Keys.Where(existing => editTarget.Span.Contains(existing.Span)).ToList())
                        replacements.Remove(contained);

                    replacements[editTarget] = replacement.WithTriviaFrom(editTarget);
                    localKinds[kind] = localKinds.GetValueOrDefault(kind) + 1;
                }
            }

            if (documentId == document.Id)
            {
                // The declaration itself: annotate it, rewrite the usages, then swap the property for
                // the new methods. The annotation survives the usage rewrites; spans would not.
                var annotation = new SyntaxAnnotation("property-to-function");
                var annotatedRoot = targetRoot.ReplaceNode(declaration, declaration.WithAdditionalAnnotations(annotation));
                var rewritten = annotatedRoot.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
                var annotated = rewritten.GetAnnotatedNodes(annotation).OfType<PropertyDeclarationSyntax>().SingleOrDefault();
                if (annotated?.Parent is not TypeDeclarationSyntax containing)
                    return new { error = "The property is not a member of a type declaration." };

                var newMembers = new List<MemberDeclarationSyntax>();
                var indentation = IndentationOf(annotated);
                var getterMethod = getterAccessor is null || getter is null ? null : BuildGetter(annotated, getterAccessor, getter);
                var setterMethod = setterAccessor is null || setter is null ? null : BuildSetter(annotated, setterAccessor, setter);

                foreach (var member in containing.Members)
                {
                    if (member != annotated) { newMembers.Add(member); continue; }

                    if (getterMethod is not null && setterMethod is not null)
                    {
                        newMembers.Add(getterMethod
                            .WithLeadingTrivia(annotated.GetLeadingTrivia())
                            .WithTrailingTrivia(ElasticCarriageReturnLineFeed));
                        newMembers.Add(setterMethod
                            .WithLeadingTrivia(Whitespace(indentation))
                            .WithTrailingTrivia(annotated.GetTrailingTrivia()));
                    }
                    else if (getterMethod is not null)
                    {
                        newMembers.Add(getterMethod
                            .WithLeadingTrivia(annotated.GetLeadingTrivia())
                            .WithTrailingTrivia(annotated.GetTrailingTrivia()));
                    }
                    else if (setterMethod is not null)
                    {
                        newMembers.Add(setterMethod
                            .WithLeadingTrivia(annotated.GetLeadingTrivia())
                            .WithTrailingTrivia(annotated.GetTrailingTrivia()));
                    }
                }

                var finalRoot = rewritten.ReplaceNode(containing, containing.WithMembers(List(newMembers)));

                var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
                var newText = finalRoot.ToFullString();
                if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                    edits.Add((target, newText, localKinds.Values.Sum(), RefactoringFileWriter.Diff(oldText, newText)));
            }
            else
            {
                if (replacements.Count == 0) continue;

                var newRoot = targetRoot.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
                var oldText = (await target.GetTextAsync(cancellationToken)).ToString();
                var newText = newRoot.ToFullString();
                if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

                edits.Add((target, newText, localKinds.Values.Sum(), RefactoringFileWriter.Diff(oldText, newText)));
            }

            foreach (var (kind, count) in localKinds) kinds[kind] = kinds.GetValueOrDefault(kind) + count;
        }

        if (edits.Count == 0) return new { error = "Nothing to rewrite." };

        // ── Gate: every affected project must still compile ───────────────────────────────
        var projected = solution;
        foreach (var edit in edits) projected = projected.WithDocumentText(edit.Document.Id, SourceText.From(edit.NewText));

        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, projected, edits.Select(edit => edit.Document.Project.Id), cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, "the conversion");

        if (!dryRun)
            foreach (var edit in edits)
                await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText, cancellationToken);

        return new
        {
            @refactoring = "property_to_function",
            property = propertySymbol.ToDisplayString(),
            getter,
            setter,
            file = document.FilePath,
            dryRun,
            applied = !dryRun,
            usages = kinds,
            filesChanged = edits.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            files = edits.Select(edit => new
            {
                file = edit.Document.FilePath,
                rewrites = edit.Usages,
                preview = edit.Preview,
            }).ToList(),
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    private static (SyntaxNode Target, SyntaxNode Replacement, string Kind)? Edit(
        SyntaxNode target, SyntaxNode replacement, string kind) => (target, replacement, kind);

    /// <summary>
    /// What the reference at <paramref name="identifier"/> means and how it is rewritten. The target is
    /// the outermost node to replace (an assignment or a unary expression for writes, the access
    /// expression for a read), so a write also rewrites the property reads inside its value.
    /// </summary>
    private static ((SyntaxNode Target, SyntaxNode Replacement, string Kind)? Edit, string? Error) Classify(
        IdentifierNameSyntax identifier,
        SemanticModel model,
        IPropertySymbol property,
        string? getter,
        string? setter,
        CancellationToken cancellationToken)
    {
        if (identifier.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }))
            return (null, "The property is used in nameof(); a method name would change the string, so nothing was written.");

        SyntaxNode target;
        ExpressionSyntax? receiver = null;
        var conditional = false;

        if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
        {
            target = access;
            receiver = access.Expression;
        }
        else if (identifier.Parent is MemberBindingExpressionSyntax)
        {
            target = identifier.Parent;
            conditional = true;
        }
        else
        {
            target = identifier;
        }

        if (target.Parent is ArgumentSyntax argument && argument.RefKindKeyword.Kind() != SyntaxKind.None)
            return (null, "The property is passed by ref/out/in; a method call cannot stand in for that.");

        if (target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target)
        {
            if (assignment.Parent is InitializerExpressionSyntax)
                return (null, "The property is set in an object or with initializer; that syntax needs a property.");

            if (setter is null) return (null, "The property has no setter, but it is assigned.");

            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                if (conditional) return (null, "A conditional-access write is not valid C#.");
                var value = RewriteReads(assignment.Right, model, property, getter, cancellationToken);
                var call = receiver is null
                    ? $"{setter}({value})"
                    : $"{Clean(receiver)}.{setter}({value})";
                return (Edit(assignment, ParseExpression(call), "write"), null);
            }

            if (assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
                return (null, "`??=` on the property cannot be expressed with a getter/setter pair; convert it by hand.");

            if (getter is null) return (null, "A compound assignment reads the property, but it has no getter.");
            if (receiver is not null && !IsStableReceiver(receiver))
                return (null, "A compound assignment would have to evaluate the receiver twice; the property object is not a stable expression.");

            var compoundValue = RewriteReads(assignment.Right, model, property, getter, cancellationToken);
            var binary = CompoundOperator(assignment.Kind());
            if (binary is null) return (null, "Unsupported compound assignment.");

            var prefix = receiver is null ? string.Empty : Clean(receiver) + ".";
            var read = receiver is null ? $"{getter}()" : $"{Clean(receiver)}.{getter}()";
            return (Edit(assignment, ParseExpression($"{prefix}{setter}({read} {binary} ({compoundValue}))"), "compound-write"), null);
        }

        if (target.Parent is PrefixUnaryExpressionSyntax prefixUnary &&
            (prefixUnary.IsKind(SyntaxKind.PreIncrementExpression) || prefixUnary.IsKind(SyntaxKind.PreDecrementExpression)) &&
            prefixUnary.Operand == target)
            return Increment(prefixUnary, target, receiver, conditional, property, getter, setter, "prefix");

        if (target.Parent is PostfixUnaryExpressionSyntax postfixUnary &&
            (postfixUnary.IsKind(SyntaxKind.PostIncrementExpression) || postfixUnary.IsKind(SyntaxKind.PostDecrementExpression)) &&
            postfixUnary.Operand == target)
            return Increment(postfixUnary, target, receiver, conditional, property, getter, setter, "postfix");

        if (getter is null) return (null, "The property has no getter, but it is read.");

        SyntaxNode readReplacement = target switch
        {
            MemberAccessExpressionSyntax memberAccess => InvocationExpression(memberAccess.WithName(IdentifierName(getter))),
            MemberBindingExpressionSyntax binding => InvocationExpression(binding.WithName(IdentifierName(getter))),
            _ => InvocationExpression(IdentifierName(getter)),
        };

        return (Edit(target, readReplacement, "read"), null);
    }

    private static ((SyntaxNode Target, SyntaxNode Replacement, string Kind)? Edit, string? Error) Increment(
        ExpressionSyntax node,
        SyntaxNode target,
        ExpressionSyntax? receiver,
        bool conditional,
        IPropertySymbol property,
        string? getter,
        string? setter,
        string direction)
    {
        if (setter is null) return (null, "The property has no setter, but it is incremented.");
        if (getter is null) return (null, "The property has no getter, but it is incremented.");
        if (conditional) return (null, "A conditional-access increment is not valid C#.");
        if (receiver is not null && !IsStableReceiver(receiver))
            return (null, "An increment would have to evaluate the receiver twice; the property object is not a stable expression.");
        if (!CanIncrement(property.Type))
            return (null, $"'{property.Type}' has no '++'/'--' that '+ 1'/'- 1' would reproduce.");

        var isIncrement = direction is "prefix"
            ? node.IsKind(SyntaxKind.PreIncrementExpression)
            : node.IsKind(SyntaxKind.PostIncrementExpression);

        var prefix = receiver is null ? string.Empty : Clean(receiver) + ".";
        var read = receiver is null ? $"{getter}()" : $"{Clean(receiver)}.{getter}()";
        var replacement = ParseExpression($"{prefix}{setter}({read} {(isIncrement ? "+" : "-")} 1)");
        return (Edit(node, replacement, isIncrement ? "increment" : "decrement"), null);
    }

    /// <summary>Rewrites the property reads inside a value expression (`x.P = x.P + 1`).</summary>
    private static ExpressionSyntax RewriteReads(
        ExpressionSyntax expression,
        SemanticModel model,
        IPropertySymbol property,
        string? getter,
        CancellationToken cancellationToken)
    {
        if (getter is null) return expression;

        var references = expression.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Where(identifier => SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(identifier, cancellationToken).Symbol, property))
            .ToList();
        if (references.Count == 0) return expression;

        var targets = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var identifier in references)
        {
            SyntaxNode target = identifier;
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) target = access;
            else if (identifier.Parent is MemberBindingExpressionSyntax) target = identifier.Parent;

            if (targets.Keys.Any(existing => existing.Span.Contains(target.Span))) continue;
            foreach (var contained in targets.Keys.Where(existing => target.Span.Contains(existing.Span)).ToList())
                targets.Remove(contained);

            SyntaxNode replacement = target switch
            {
                MemberAccessExpressionSyntax memberAccess => InvocationExpression(memberAccess.WithName(IdentifierName(getter))),
                MemberBindingExpressionSyntax binding => InvocationExpression(binding.WithName(IdentifierName(getter))),
                _ => InvocationExpression(IdentifierName(getter)),
            };
            targets[target] = replacement.WithTriviaFrom(target);
        }

        return (ExpressionSyntax)expression.ReplaceNodes(targets.Keys, (original, _) => targets[original]);
    }

    private static string? RefuseFor(PropertyDeclarationSyntax declaration, IPropertySymbol property)
    {
        if (property.IsIndexer) return "Indexers are not converted; use indexer_to_function.";
        if (declaration.AttributeLists.Count > 0) return $"'{property.Name}' carries attributes; move them by hand first.";
        if (declaration.ExpressionBody is not null) return $"'{property.Name}' is a ref/expression-bodied property; not supported.";
        if (property.IsAbstract || property.IsVirtual || property.IsOverride)
            return $"'{property.Name}' is abstract/virtual/override: every override would have to change too.";
        if (property.IsExtern) return $"'{property.Name}' is extern.";
        if (property.ContainingType is { TypeKind: TypeKind.Interface })
            return "An interface property cannot become a method this way.";

        var accessors = declaration.AccessorList?.Accessors;
        if (accessors is null) return $"'{property.Name}' has no accessor list.";

        foreach (var accessor in accessors.Value)
        {
            if (accessor.AttributeLists.Count > 0)
                return $"An accessor of '{property.Name}' carries attributes; move them by hand first.";
            if (accessor.Body is null && accessor.ExpressionBody is null)
                return $"'{property.Name}' is an auto-property: its accessors have no body to turn into a method.";
        }

        return null;
    }

    private static AccessorDeclarationSyntax? Accessor(PropertyDeclarationSyntax declaration, SyntaxKind kind) =>
        declaration.AccessorList?.Accessors.FirstOrDefault(accessor => accessor.IsKind(kind));

    private static MethodDeclarationSyntax BuildGetter(
        PropertyDeclarationSyntax property, AccessorDeclarationSyntax accessor, string getterName)
    {
        var method = MethodDeclaration(
                property.Type.GetTrailingTrivia().Count == 0 ? property.Type.WithTrailingTrivia(Space) : property.Type,
                Identifier(getterName).WithLeadingTrivia(property.Identifier.LeadingTrivia))
            .WithModifiers(EffectiveModifiers(property.Modifiers, accessor.Modifiers))
            .WithParameterList(ParameterList());

        return accessor.ExpressionBody is not null
            ? method.WithExpressionBody(accessor.ExpressionBody).WithSemicolonToken(accessor.SemicolonToken)
            : method.WithBody(accessor.Body!);
    }

    private static MethodDeclarationSyntax BuildSetter(
        PropertyDeclarationSyntax property, AccessorDeclarationSyntax accessor, string setterName)
    {
        var parameter = ParseParameterList(property.Type.WithoutTrivia() + " value").Parameters[0];

        var method = MethodDeclaration(
                PredefinedType(Token(SyntaxKind.VoidKeyword).WithTrailingTrivia(Space)), Identifier(setterName))
            .WithModifiers(EffectiveModifiers(property.Modifiers, accessor.Modifiers))
            .WithParameterList(ParameterList(SingletonSeparatedList(parameter)));

        return accessor.ExpressionBody is not null
            ? method.WithExpressionBody(accessor.ExpressionBody).WithSemicolonToken(accessor.SemicolonToken)
            : method.WithBody(accessor.Body!);
    }

    /// <summary>
    /// The property's modifiers, with the accessor's own accessibility when it declares one
    /// (<c>public int P { get; private set; }</c> keeps the setter private).
    /// </summary>
    private static SyntaxTokenList EffectiveModifiers(SyntaxTokenList propertyModifiers, SyntaxTokenList accessorModifiers)
    {
        if (accessorModifiers.Count == 0) return propertyModifiers;

        var kept = propertyModifiers
            .Where(token => !IsAccessibility(token.Kind()) && !token.IsKind(SyntaxKind.VirtualKeyword) &&
                            !token.IsKind(SyntaxKind.OverrideKeyword) && !token.IsKind(SyntaxKind.AbstractKeyword))
            .Select(token => token.WithLeadingTrivia().WithTrailingTrivia(Space))
            .ToList();

        var declared = accessorModifiers
            .Select(token => token.WithLeadingTrivia().WithTrailingTrivia(Space))
            .ToList();

        return TokenList(declared.Concat(kept));
    }

    private static bool IsAccessibility(SyntaxKind kind) => kind is
        SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword;

    private static bool IsStableReceiver(ExpressionSyntax expression) => expression switch
    {
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax access => IsStableReceiver(access.Expression),
        _ => false,
    };

    private static bool CanIncrement(ITypeSymbol type)
    {
        if (type.SpecialType is SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Int16 or
            SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal or
            SpecialType.System_Char)
            return true;

        return type.GetMembers("op_Addition").OfType<IMethodSymbol>().Any(method => method.Parameters.Length == 2) &&
               type.GetMembers("op_Subtraction").OfType<IMethodSymbol>().Any(method => method.Parameters.Length == 2);
    }

    private static string? CompoundOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddAssignmentExpression => "+",
        SyntaxKind.SubtractAssignmentExpression => "-",
        SyntaxKind.MultiplyAssignmentExpression => "*",
        SyntaxKind.DivideAssignmentExpression => "/",
        SyntaxKind.ModuloAssignmentExpression => "%",
        SyntaxKind.AndAssignmentExpression => "&",
        SyntaxKind.OrAssignmentExpression => "|",
        SyntaxKind.ExclusiveOrAssignmentExpression => "^",
        SyntaxKind.LeftShiftAssignmentExpression => "<<",
        SyntaxKind.RightShiftAssignmentExpression => ">>",
        SyntaxKind.UnsignedRightShiftAssignmentExpression => ">>>",
        _ => null,
    };

    private static string Clean(SyntaxNode node) => node.WithoutLeadingTrivia().WithoutTrailingTrivia().ToString().Trim();

    private static string Capitalise(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string IndentationOf(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }
}
