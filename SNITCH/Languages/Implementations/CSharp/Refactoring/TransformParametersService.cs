using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>An invocation of the target method, with its arguments already mapped to parameter names.</summary>
internal sealed record ParameterCallSite(
    Document Document, InvocationExpressionSyntax Invocation, Dictionary<string, ArgumentSyntax> Arguments);

/// <summary>
/// Rider's "Transform parameters" — the transformations of the Change Signature dialog that are more
/// than a reorder: wrap several parameters in a parameter object, make parameters optional or
/// required, retype them, or replace one with a property of the containing type.
/// </summary>
/// <remarks>
/// The five operations share one resolution of the method and one mapping of its call sites, and every
/// one of them is gated on the compiler: a transformation that would not compile is refused and nothing
/// is written.
/// </remarks>
public interface ITransformParametersService
{
    Task<object> TransformParameters(
        string methodName,
        string operation,
        string[] parameters,
        string? containingType = null,
        string? typeName = null,
        string? newType = null,
        string? defaultValue = null,
        Dictionary<string, string>? defaults = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default);
}

public sealed class TransformParametersService : ITransformParametersService
{
    private const string Wrap = "wrap";
    private const string MakeOptional = "make-optional";
    private const string MakeRequired = "make-required";
    private const string ChangeType = "change-type";
    private const string ReplaceWithProperty = "replace-with-property";

    private static readonly string[] Operations = [Wrap, MakeOptional, MakeRequired, ChangeType, ReplaceWithProperty];

    private readonly IRoslynWorkspace _workspace;

    public TransformParametersService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> TransformParameters(
        string methodName,
        string operation,
        string[] parameters,
        string? containingType = null,
        string? typeName = null,
        string? newType = null,
        string? defaultValue = null,
        Dictionary<string, string>? defaults = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(methodName)) return new { error = "A method name is required." };
        if (string.IsNullOrWhiteSpace(operation)) return new { error = $"An operation is required: {string.Join(", ", Operations)}." };

        var normalized = operation.Trim().ToLowerInvariant();
        if (!Operations.Contains(normalized))
            return new { error = $"Unknown operation '{operation}'. Use one of: {string.Join(", ", Operations)}." };

        var names = (parameters ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return new { error = "Name at least one parameter in 'parameters'." };

        var vb = await Snitch.Languages.VB.Refactoring.VbRefactoringTail.TransformParametersAsync(
            _workspace, methodName, normalized, names.ToArray(), containingType, typeName, newType, defaultValue, defaults,
            dryRun, cancellationToken);
        if (vb is not null) return vb;

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var candidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(methodName, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Member);

        var methods = candidates
            .OfType<IMethodSymbol>()
            .Where(method => containingType is null ||
                             method.ContainingType.Name.Equals(containingType, StringComparison.OrdinalIgnoreCase))
            .Where(method => method.MethodKind == MethodKind.Ordinary)
            .ToList();

        if (methods.Count == 0) return new { error = $"Method '{methodName}' not found." };
        if (methods.Count > 1 && containingType is null)
            return new
            {
                error = $"Ambiguous: {methods.Count} methods named '{methodName}'. Specify containingType.",
                candidates = methods.Select(method => method.ToDisplayString()).ToList(),
            };

        var symbol = methods[0];
        if (symbol.IsGenericMethod && normalized == Wrap)
            return new
            {
                error = "Wrapping a parameter of a generic method is not supported: the parameter object would need the " +
                        "method's type arguments, which a call site does not spell.",
            };

        var unknown = names.Where(name => symbol.Parameters.All(parameter => parameter.Name != name)).ToList();
        if (unknown.Count > 0)
            return new
            {
                error = $"No parameter(s) {string.Join(", ", unknown.Select(name => $"'{name}'"))} on {symbol.ToDisplayString()}.",
                parameters = symbol.Parameters.Select(parameter => parameter.Name).ToList(),
            };

        var selected = symbol.Parameters.Where(parameter => names.Contains(parameter.Name)).ToList();
        var byRef = selected.Where(parameter => parameter.RefKind != RefKind.None).Select(parameter => parameter.Name).ToList();
        if (byRef.Count > 0)
            return new { error = $"Parameter(s) {string.Join(", ", byRef)} are ref/out/in: a transformation cannot preserve their passing mode." };

        var paramsArray = selected.Where(parameter => parameter.IsParams).Select(parameter => parameter.Name).ToList();
        if (paramsArray.Count > 0 && normalized is Wrap or ReplaceWithProperty)
            return new { error = $"Parameter(s) {string.Join(", ", paramsArray)} are 'params': wrapping or replacing a params array has no meaningful call-site form." };

        var declarationLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (declarationLocation?.SourceTree?.FilePath is null) return new { error = "Method has no source location." };

        var sourceDocument = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(document => document.FilePath is not null &&
                                       string.Equals(Path.GetFullPath(document.FilePath),
                                                     Path.GetFullPath(declarationLocation.SourceTree.FilePath),
                                                     StringComparison.OrdinalIgnoreCase));
        if (sourceDocument is null) return new { error = "Could not locate the source document of the method." };

        if (await sourceDocument.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root ||
            await sourceDocument.GetSemanticModelAsync(cancellationToken) is not { } model)
            return new { error = "Could not parse the source file." };

        var declaration = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(node => node.Identifier.ValueText.Equals(methodName, StringComparison.Ordinal) &&
                                    node.Span.Contains(declarationLocation.SourceSpan.Start));
        if (declaration is null) return new { error = "Could not locate the method declaration node." };

        var typeDeclaration = declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration is null) return new { error = "The method is not declared inside a type." };

        // A parameter used by name in nameof() keeps its *name*, and wrap/replace change the name.
        if (normalized is Wrap or ReplaceWithProperty)
        {
            var nameOfHits = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
                .SelectMany(invocation => invocation.ArgumentList.Arguments)
                .Where(argument => model.GetSymbolInfo(argument.Expression, cancellationToken).Symbol is IParameterSymbol parameter &&
                                   names.Contains(parameter.Name))
                .Select(argument => argument.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
                .ToList();

            if (nameOfHits.Count > 0)
                return new
                {
                    error = "A parameter of this method is used in nameof(): its name is a compile-time constant and the " +
                            "transformation would change it. Nothing was written.",
                    lines = nameOfHits,
                };
        }

        var context = new MethodContext(solution, sourceDocument, root, model, declaration, typeDeclaration, symbol);

        return normalized switch
        {
            MakeOptional => await MakeOptionalAsync(context, selected, defaultValue, defaults, dryRun, cancellationToken),
            MakeRequired => await MakeRequiredAsync(context, selected, dryRun, cancellationToken),
            ChangeType => await ChangeTypeAsync(context, selected, newType, dryRun, cancellationToken),
            Wrap => await WrapAsync(context, selected, typeName, dryRun, cancellationToken),
            ReplaceWithProperty => await ReplaceWithPropertyAsync(context, selected, dryRun, cancellationToken),
            _ => new { error = $"Unknown operation '{operation}'." },
        };
    }

    // ── make-optional ────────────────────────────────────────────────────────────────

    private async Task<object> MakeOptionalAsync(
        MethodContext context,
        IReadOnlyList<IParameterSymbol> selected,
        string? defaultValue,
        Dictionary<string, string>? defaults,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in selected)
        {
            var value = defaults is not null && defaults.TryGetValue(parameter.Name, out var configured)
                ? configured
                : defaultValue;

            if (string.IsNullOrWhiteSpace(value))
                return new { error = $"No default value for '{parameter.Name}': pass defaultValue, or defaults with an entry per parameter." };

            values[parameter.Name] = value!;
        }

        var newParameters = new List<ParameterSyntax>();
        foreach (var parameter in context.Declaration.ParameterList.Parameters)
        {
            if (!values.TryGetValue(parameter.Identifier.ValueText, out var value))
            {
                newParameters.Add(parameter);
                continue;
            }

            var clause = EqualsValueClause(
                Token(SyntaxKind.EqualsToken).WithLeadingTrivia(Space).WithTrailingTrivia(Space),
                ParseExpression(value));

            newParameters.Add(parameter.WithDefault(clause));
        }

        // C# requires every parameter after the first optional one to be optional too.
        var seenOptional = false;
        var requiredAfterOptional = new List<string>();
        foreach (var parameter in newParameters)
        {
            if (parameter.Default is not null) seenOptional = true;
            else if (seenOptional) requiredAfterOptional.Add(parameter.Identifier.ValueText);
        }

        if (requiredAfterOptional.Count > 0)
            return new
            {
                error = "C# does not allow a required parameter after an optional one: " +
                        $"{string.Join(", ", requiredAfterOptional.Select(name => $"'{name}'"))} would need a default too (or move it before).",
            };

        var rewritten = context.Declaration.WithParameterList(
            context.Declaration.ParameterList.WithParameters(SeparatedList(newParameters, Commas(newParameters.Count))));

        return await FinishAsync(context, rewritten, null, "make-optional", values.Keys.ToList(),
            new { defaults = values }, [], new Dictionary<(DocumentId, int), string>(), null, dryRun, cancellationToken);
    }

    // ── make-required ────────────────────────────────────────────────────────────────

    private async Task<object> MakeRequiredAsync(
        MethodContext context,
        IReadOnlyList<IParameterSymbol> selected,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var names = selected.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

        var (sites, notPlain) = await CollectCallSitesAsync(context, cancellationToken);
        if (notPlain.Count > 0)
            return new
            {
                error = $"{notPlain.Count} use(s) are not plain calls (method group or delegate); removing a default would break them.",
                uses = notPlain.Take(10).ToList(),
            };

        var omitting = sites
            .Where(site => names.Any(name => !site.Arguments.ContainsKey(name)))
            .Select(site => $"{Path.GetFileName(site.Document.FilePath)}:{site.Invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1}")
            .ToList();

        if (omitting.Count > 0)
            return new
            {
                error = $"{omitting.Count} call site(s) omit one of {string.Join(", ", names.Select(name => $"'{name}'"))}; " +
                        "making them required would not compile. Nothing was written.",
                callSites = omitting.Take(12).ToList(),
            };

        var newParameters = context.Declaration.ParameterList.Parameters
            .Select(parameter => names.Contains(parameter.Identifier.ValueText) && parameter.Default is not null
                ? parameter.WithDefault(null)
                : parameter)
            .ToList();

        var rewritten = context.Declaration.WithParameterList(
            context.Declaration.ParameterList.WithParameters(SeparatedList(newParameters, Commas(newParameters.Count))));

        return await FinishAsync(context, rewritten, null, "make-required", names.ToList(),
            new { required = names.ToList(), callSitesChecked = sites.Count }, [], new Dictionary<(DocumentId, int), string>(), null,
            dryRun, cancellationToken);
    }

    // ── change-type ─────────────────────────────────────────────────────────────────

    private async Task<object> ChangeTypeAsync(
        MethodContext context,
        IReadOnlyList<IParameterSymbol> selected,
        string? newType,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newType)) return new { error = "change-type needs newType." };

        var names = selected.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        var type = ParseTypeName(newType);

        var newParameters = context.Declaration.ParameterList.Parameters
            .Select(parameter => names.Contains(parameter.Identifier.ValueText) && parameter.Type is not null
                ? parameter.WithType(type.WithTriviaFrom(parameter.Type))
                : parameter)
            .ToList();

        var rewritten = context.Declaration.WithParameterList(
            context.Declaration.ParameterList.WithParameters(SeparatedList(newParameters, Commas(newParameters.Count))));

        return await FinishAsync(context, rewritten, null, "change-type", names.ToList(),
            new { newType }, [], new Dictionary<(DocumentId, int), string>(), null, dryRun, cancellationToken);
    }

    // ── wrap: a parameter object ────────────────────────────────────────────────────

    private async Task<object> WrapAsync(
        MethodContext context,
        IReadOnlyList<IParameterSymbol> selected,
        string? typeName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var declaration = context.Declaration;
        if (declaration.Body is null && declaration.ExpressionBody is null)
            return new { error = "The method has no body to rewrite." };
        if (context.TypeDeclaration is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
            return new { error = "The containing type is not a class, struct or record, so a nested parameter object cannot be declared in it." };

        var objectName = string.IsNullOrWhiteSpace(typeName) ? declaration.Identifier.ValueText + "Arguments" : typeName!;
        if (!SyntaxFacts.IsValidIdentifier(objectName))
            return new { error = $"'{objectName}' is not a valid class name." };

        if (context.TypeDeclaration.Members.OfType<BaseTypeDeclarationSyntax>()
            .Any(member => member.Identifier.ValueText.Equals(objectName, StringComparison.Ordinal)))
            return new { error = $"'{context.TypeDeclaration.Identifier.ValueText}' already declares a type '{objectName}'; pass another typeName." };

        var names = selected.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        var firstWrapped = declaration.ParameterList.Parameters
            .First(parameter => names.Contains(parameter.Identifier.ValueText));

        var afterFirst = declaration.ParameterList.Parameters
            .SkipWhile(parameter => parameter != firstWrapped)
            .ToList();
        if (afterFirst.Any(parameter => !names.Contains(parameter.Identifier.ValueText)))
            return new { error = "The wrapped parameters must form one consecutive block of the parameter list." };

        var argumentsName = names.Contains("args") ? LowerFirst(objectName) : "args";
        if (names.Contains(argumentsName))
            return new { error = $"The parameter object's own name '{argumentsName}' collides with a parameter." };

        // ── The class: one constructor parameter and one settable property per wrapped parameter ──
        var properties = new List<(string SourceName, string PropertyName, string TypeText, string? Default)>();
        var used = new HashSet<string>(StringComparer.Ordinal) { objectName };
        foreach (var parameter in selected)
        {
            var typeText = parameter.Type?.ToString();
            if (typeText is null) return new { error = $"Parameter '{parameter.Name}' has no spelled type." };

            var propertyName = UpperFirst(parameter.Name);
            var unique = propertyName;
            for (var suffix = 2; !used.Add(unique); suffix++) unique = propertyName + suffix;

            var parameterNode = declaration.ParameterList.Parameters
                .First(node => node.Identifier.ValueText == parameter.Name);
            properties.Add((parameter.Name, unique, typeText, parameterNode.Default?.Value.ToString()));
        }

        var classText = BuildParameterObjectText(objectName, properties, AccessibilityOf(declaration));
        var argumentsIdentifier = IdentifierName(argumentsName);

        // ── The body: every use of a wrapped parameter reads it from the object ──────────
        var references = ParameterReferences(context, names, cancellationToken);
        var rewrittenDeclaration = declaration.ReplaceNodes(references, (original, _) =>
        {
            var propertyName = properties.First(entry => entry.SourceName == original.Identifier.ValueText).PropertyName;
            return MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, argumentsIdentifier, IdentifierName(propertyName))
                .WithTriviaFrom(original);
        });

        // ── The declaration: the wrapped block becomes one parameter ─────────────────────
        var newParameters = new List<ParameterSyntax>();
        foreach (var parameter in declaration.ParameterList.Parameters)
        {
            if (names.Contains(parameter.Identifier.ValueText))
            {
                if (parameter == firstWrapped)
                    newParameters.Add(ParseParameterList($"({objectName} {argumentsName})").Parameters[0]);
                continue;
            }

            newParameters.Add(parameter);
        }

        rewrittenDeclaration = rewrittenDeclaration.WithParameterList(
            declaration.ParameterList.WithParameters(SeparatedList(newParameters, Commas(newParameters.Count))));

        // ── The call sites: the wrapped arguments become one object creation ─────────────
        var (sites, notPlain) = await CollectCallSitesAsync(context, cancellationToken);
        if (notPlain.Count > 0)
            return new
            {
                error = $"{notPlain.Count} use(s) are not plain calls (method group or delegate); the parameter count changes, " +
                        "so they would break. Nothing was written.",
                uses = notPlain.Take(10).ToList(),
            };

        var newCallTexts = new Dictionary<(DocumentId, int), string>();
        foreach (var site in sites)
        {
            var namedArguments = site.Invocation.ArgumentList.Arguments.Any(argument => argument.NameColon is not null);
            var creationArguments = new List<ArgumentSyntax>();

            foreach (var parameter in selected)
            {
                if (!site.Arguments.TryGetValue(parameter.Name, out var argument)) continue;

                var value = argument.Expression.WithoutTrivia();
                creationArguments.Add(namedArguments
                    ? Argument(value).WithNameColon(NameColon(IdentifierName(parameter.Name)))
                    : Argument(value));
            }

            var creation = ObjectCreationExpression(IdentifierName(objectName))
                .WithNewKeyword(Token(SyntaxKind.NewKeyword).WithTrailingTrivia(Space))
                .WithArgumentList(ArgumentList(SeparatedList(creationArguments, Commas(creationArguments.Count))));

            var rebuilt = new List<ArgumentSyntax>();
            foreach (var parameter in context.Symbol.Parameters)
            {
                if (names.Contains(parameter.Name))
                {
                    if (SymbolEqualityComparer.Default.Equals(parameter, selected[0]))
                        rebuilt.Add(namedArguments
                            ? Argument(creation).WithNameColon(NameColon(IdentifierName(argumentsName)))
                            : Argument(creation));
                    continue;
                }

                if (site.Arguments.TryGetValue(parameter.Name, out var argument))
                    rebuilt.Add(argument.WithoutTrivia());
            }

            var rewritten = site.Invocation.WithArgumentList(
                site.Invocation.ArgumentList.WithArguments(SeparatedList(rebuilt, Commas(rebuilt.Count))));

            newCallTexts[(site.Document.Id, site.Invocation.SpanStart)] = rewritten.ToString();
        }

        return await FinishAsync(context, rewrittenDeclaration, classText, "wrap", names.ToList(),
            new
            {
                parameterObject = objectName,
                properties = properties.Select(entry => new { parameter = entry.SourceName, property = entry.PropertyName, type = entry.TypeText }).ToList(),
            },
            sites, newCallTexts, null, dryRun, cancellationToken);
    }

    // ── replace-with-property ────────────────────────────────────────────────────────

    private async Task<object> ReplaceWithPropertyAsync(
        MethodContext context,
        IReadOnlyList<IParameterSymbol> selected,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var declaration = context.Declaration;
        if (declaration.Body is null && declaration.ExpressionBody is null)
            return new { error = "The method has no body to rewrite." };
        if (context.Symbol.IsStatic)
            return new { error = "A static method has no instance to hold the property; make the method an instance member first." };
        if (context.TypeDeclaration is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
            return new { error = "The containing type cannot declare properties." };

        var names = selected.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        var accessibility = AccessibilityOf(declaration);

        var existing = context.TypeDeclaration.Members
            .SelectMany(member => member switch
            {
                BaseTypeDeclarationSyntax type => new[] { type.Identifier.ValueText },
                MethodDeclarationSyntax method => new[] { method.Identifier.ValueText },
                PropertyDeclarationSyntax property => new[] { property.Identifier.ValueText },
                FieldDeclarationSyntax field => field.Declaration.Variables.Select(variable => variable.Identifier.ValueText).ToArray(),
                EventFieldDeclarationSyntax @event => @event.Declaration.Variables.Select(variable => variable.Identifier.ValueText).ToArray(),
                _ => [],
            });

        var used = new HashSet<string>(existing, StringComparer.Ordinal);
        var properties = new List<(string SourceName, string PropertyName, string TypeText)>();

        foreach (var parameter in selected)
        {
            var typeText = parameter.Type?.ToString();
            if (typeText is null) return new { error = $"Parameter '{parameter.Name}' has no spelled type." };

            var propertyName = UpperFirst(parameter.Name);
            if (!used.Add(propertyName))
                return new { error = $"'{context.TypeDeclaration.Identifier.ValueText}' already has a member '{propertyName}'." };

            properties.Add((parameter.Name, propertyName, typeText));
        }

        var propertyText = string.Join(Environment.NewLine,
            properties.Select(entry => $"{accessibility} {entry.TypeText} {entry.PropertyName} {{ get; set; }}"));

        // ── The body: every use reads the property instead of the parameter ──────────────
        var references = ParameterReferences(context, names, cancellationToken);
        var rewrittenDeclaration = declaration.ReplaceNodes(references, (original, _) =>
        {
            var propertyName = properties.First(entry => entry.SourceName == original.Identifier.ValueText).PropertyName;
            return MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, ThisExpression(), IdentifierName(propertyName))
                .WithTriviaFrom(original);
        });

        var newParameters = declaration.ParameterList.Parameters
            .Where(parameter => !names.Contains(parameter.Identifier.ValueText))
            .ToList();

        rewrittenDeclaration = rewrittenDeclaration.WithParameterList(
            declaration.ParameterList.WithParameters(SeparatedList(newParameters, Commas(newParameters.Count))));

        // ── The call sites: the argument becomes an assignment on the receiver ───────────
        var (sites, notPlain) = await CollectCallSitesAsync(context, cancellationToken);
        if (notPlain.Count > 0)
            return new
            {
                error = $"{notPlain.Count} use(s) are not plain calls (method group or delegate); the parameter count changes, so they would break.",
                uses = notPlain.Take(10).ToList(),
            };

        var writesByCall = new Dictionary<(DocumentId, int), List<string>>();
        var newCallTexts = new Dictionary<(DocumentId, int), string>();
        var unsupported = new List<string>();

        foreach (var site in sites)
        {
            var line = site.Invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var receiver = ReceiverOf(site.Invocation, context.Symbol.ContainingType, context.Model, cancellationToken);
            if (receiver is null)
            {
                unsupported.Add($"{Path.GetFileName(site.Document.FilePath)}:{line} ({site.Invocation.Expression})");
                continue;
            }

            var statement = site.Invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            if (statement?.Parent is not (BlockSyntax or SwitchSectionSyntax))
            {
                unsupported.Add($"{Path.GetFileName(site.Document.FilePath)}:{line} (no statement block to insert the property assignment into)");
                continue;
            }

            var removed = new List<ArgumentSyntax>();
            var writes = new List<string>();
            foreach (var parameter in selected)
            {
                if (!site.Arguments.TryGetValue(parameter.Name, out var argument)) continue;

                removed.Add(argument);
                var propertyName = properties.First(entry => entry.SourceName == parameter.Name).PropertyName;
                writes.Add($"{receiver}.{propertyName} = {argument.Expression};");
            }

            if (writes.Count == 0) continue;

            var kept = site.Invocation.ArgumentList.Arguments.Where(argument => !removed.Contains(argument)).ToList();
            var reduced = site.Invocation.WithArgumentList(
                site.Invocation.ArgumentList.WithArguments(SeparatedList(kept, Commas(kept.Count))));

            writesByCall[(site.Document.Id, site.Invocation.SpanStart)] = writes;
            newCallTexts[(site.Document.Id, site.Invocation.SpanStart)] = reduced.ToString();
        }

        if (unsupported.Count > 0)
            return new
            {
                error = $"{unsupported.Count} call site(s) cannot receive the property assignment (the receiver must be 'this', " +
                        "a local, a parameter or a field, and the call must sit in a statement block). Nothing was written.",
                callSites = unsupported.Take(12).ToList(),
                hint = "For a receiver obtained from a call or a property, introduce a local first.",
            };

        return await FinishAsync(context, rewrittenDeclaration, propertyText, "replace-with-property", names.ToList(),
            new { properties = properties.Select(entry => new { parameter = entry.SourceName, property = entry.PropertyName, type = entry.TypeText }).ToList() },
            sites, newCallTexts, writesByCall, dryRun, cancellationToken);
    }

    // ── shared: collect call sites ──────────────────────────────────────────────────

    private static async Task<(List<ParameterCallSite> Sites, List<string> NotPlain)> CollectCallSitesAsync(
        MethodContext context, CancellationToken cancellationToken)
    {
        var references = await SymbolFinder.FindReferencesAsync(context.Symbol, context.Solution, cancellationToken);
        var sites = new List<ParameterCallSite>();
        var notPlain = new List<string>();
        var seen = new HashSet<(DocumentId, int)>();

        foreach (var location in references.SelectMany(reference => reference.Locations).Where(location => location.Location.IsInSource))
        {
            if (location.Document.Id == context.Document.Id &&
                context.Declaration.Span.Contains(location.Location.SourceSpan)) continue;
            if (!seen.Add((location.Document.Id, location.Location.SourceSpan.Start))) continue;

            var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
            if (root is null) continue;

            var node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation is null)
            {
                var line = location.Location.GetLineSpan().StartLinePosition.Line + 1;
                notPlain.Add($"{Path.GetFileName(location.Document.FilePath)}:{line} ({node})");
                continue;
            }

            sites.Add(new ParameterCallSite(
                location.Document, invocation, MapArguments(invocation, context.Symbol.Parameters)));
        }

        return (sites, notPlain);
    }

    private static Dictionary<string, ArgumentSyntax> MapArguments(
        InvocationExpressionSyntax invocation, IReadOnlyList<IParameterSymbol> parameters)
    {
        var map = new Dictionary<string, ArgumentSyntax>(StringComparer.Ordinal);
        var next = 0;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.NameColon is { } colon)
            {
                map[colon.Name.Identifier.ValueText] = argument;
                continue;
            }

            while (next < parameters.Count && map.ContainsKey(parameters[next].Name)) next++;
            if (next < parameters.Count) map[parameters[next].Name] = argument;
            next++;
        }

        return map;
    }

    // ── shared: finish, gate, write ────────────────────────────────────────────────

    /// <summary>
    /// Builds every new file text (declaration, the new member, every call site and — for
    /// replace-with-property — the assignments inserted before each call), gates the whole thing on the
    /// compiler and writes only when the gate passes. Call sites are annotated in their own trees first,
    /// because replacing the declaration shifts the spans of everything after it.
    /// </summary>
    private async Task<object> FinishAsync(
        MethodContext context,
        MethodDeclarationSyntax rewrittenDeclaration,
        string? memberText,
        string operation,
        IReadOnlyList<string> parameterNames,
        object details,
        IReadOnlyList<ParameterCallSite> sites,
        Dictionary<(DocumentId Document, int SpanStart), string> newCallTexts,
        Dictionary<(DocumentId Document, int SpanStart), List<string>>? propertyAssignments,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var roots = new Dictionary<DocumentId, SyntaxNode>();
        var annotations = new Dictionary<(DocumentId Document, int SpanStart), SyntaxAnnotation>();

        foreach (var group in sites.GroupBy(site => site.Document))
        {
            var document = group.Key;
            var root = document.Id == context.Document.Id
                ? context.Root
                : await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null) continue;

            var plans = group.ToList();
            var byNode = new Dictionary<SyntaxNode, SyntaxAnnotation>();
            foreach (var plan in plans)
            {
                var annotation = new SyntaxAnnotation("transform_parameters_call", plan.Invocation.SpanStart.ToString());
                byNode[plan.Invocation] = annotation;
                annotations[(document.Id, plan.Invocation.SpanStart)] = annotation;
            }

            roots[document.Id] = root.ReplaceNodes(byNode.Keys, (original, _) =>
                original.WithAdditionalAnnotations(byNode[original]));
        }

        // The declaration's own file: the rewritten method, plus the parameter object or the properties.
        var sourceId = context.Document.Id;
        var sourceRoot = roots.TryGetValue(sourceId, out var annotatedSource) ? annotatedSource : context.Root;

        var sourceType = sourceRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(type => type.SpanStart == context.TypeDeclaration.SpanStart &&
                                    type.RawKind == context.TypeDeclaration.RawKind);
        if (sourceType is null) return new { error = "Lost the type declaration while preparing the rewrite." };

        var sourceDeclaration = sourceType.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.SpanStart == context.Declaration.SpanStart &&
                                      method.Identifier.ValueText == context.Symbol.Name);
        if (sourceDeclaration is null) return new { error = "Lost the method declaration while preparing the rewrite." };

        var newType = sourceType.ReplaceNode(sourceDeclaration, rewrittenDeclaration);
        if (memberText is not null) newType = AddMember(newType, memberText);
        roots[sourceId] = sourceRoot.ReplaceNode(sourceType, newType);

        // Every call site, found through its annotation so a shifted span cannot pick the wrong node.
        foreach (var plan in sites)
        {
            var documentId = plan.Document.Id;
            if (!roots.TryGetValue(documentId, out var root)) continue;
            if (!annotations.TryGetValue((documentId, plan.Invocation.SpanStart), out var annotation)) continue;

            var invocation = root.GetAnnotatedNodes(annotation).OfType<InvocationExpressionSyntax>().SingleOrDefault();
            if (invocation is null) continue;

            var key = (documentId, plan.Invocation.SpanStart);
            var replacementText = newCallTexts.TryGetValue(key, out var text) ? text : null;
            var replacement = replacementText is null
                ? null
                : ParseExpression(replacementText).WithTriviaFrom(invocation);

            if (propertyAssignments is not null && propertyAssignments.TryGetValue(key, out var writes))
            {
                var statement = invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                if (statement is null) continue;

                var rewrittenStatement = replacement is null
                    ? statement
                    : statement.ReplaceNode(invocation, replacement);

                switch (statement.Parent)
                {
                    case BlockSyntax block:
                    {
                        var index = block.Statements.IndexOf(statement);
                        var indent = Indentation(statement);
                        var inserted = writes.Select(write =>
                            ParseStatement(write).WithLeadingTrivia(Whitespace(indent))
                                .WithTrailingTrivia(EndOfLine(Environment.NewLine)));

                        var statements = block.Statements.Replace(statement, rewrittenStatement);
                        roots[documentId] = root.ReplaceNode(block,
                            block.WithStatements(statements.InsertRange(index, inserted)));
                        break;
                    }
                    case SwitchSectionSyntax section:
                    {
                        var statements = section.Statements.Replace(statement, rewrittenStatement);
                        roots[documentId] = root.ReplaceNode(section, section.WithStatements(statements));
                        break;
                    }
                }

                continue;
            }

            if (replacement is not null) roots[documentId] = root.ReplaceNode(invocation, replacement);
        }

        // ── Collect what changed, and the text it replaces (before any write) ────────────
        var filesToWrite = new Dictionary<DocumentId, (string OldText, string NewText)>();
        foreach (var (documentId, root) in roots)
        {
            var document = context.Solution.GetDocument(documentId);
            if (document is null) continue;

            var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
            var newText = root.ToFullString();
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) continue;

            filesToWrite[documentId] = (oldText, newText);
        }

        if (filesToWrite.Count == 0) return new { error = "Nothing to rewrite: the transformation produced the same text." };

        // ── Gate ───────────────────────────────────────────────────────────────────────
        var projected = context.Solution;
        var projectIds = new HashSet<ProjectId>();
        foreach (var (documentId, entry) in filesToWrite)
        {
            var document = context.Solution.GetDocument(documentId);
            if (document is null) continue;

            projected = projected.WithDocumentText(documentId, SourceText.From(entry.NewText));
            projectIds.Add(document.Project.Id);
        }

        var gate = await RefactoringCompilerGate.CompareAsync(context.Solution, projected, projectIds, cancellationToken);
        if (!gate.Passed) return RefactoringCompilerGate.Refusal(gate, $"'{operation}'");

        if (!dryRun)
        {
            foreach (var (documentId, entry) in filesToWrite)
            {
                var document = context.Solution.GetDocument(documentId);
                if (document is not null)
                    await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, entry.NewText, cancellationToken);
            }
        }

        var files = filesToWrite.Select(entry =>
        {
            var document = context.Solution.GetDocument(entry.Key);
            return new
            {
                file = document?.FilePath,
                preview = RefactoringFileWriter.Diff(entry.Value.OldText, entry.Value.NewText),
            };
        }).ToList();

        return new
        {
            @refactoring = "transform_parameters",
            operation,
            method = context.Symbol.ToDisplayString(),
            parameters = parameterNames,
            details,
            callSitesAffected = sites.Count,
            filesChanged = filesToWrite.Count,
            compilerErrors = new { before = gate.Before, after = gate.After },
            files,
            dryRun,
            applied = !dryRun,
            note = dryRun ? "Preview only: pass dryRun=false to write the files." : "Files written.",
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────

    private static TypeDeclarationSyntax AddMember(TypeDeclarationSyntax type, string memberText)
    {
        var indent = Indentation(type) + "    ";
        var parsed = ParseMemberDeclaration(Reindent(memberText, indent));
        if (parsed is null) return type;

        parsed = parsed
            .WithLeadingTrivia(ElasticCarriageReturnLineFeed, Whitespace(indent))
            .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

        return type.WithMembers(type.Members.Add(parsed));
    }

    private static string BuildParameterObjectText(
        string objectName,
        IReadOnlyList<(string SourceName, string PropertyName, string TypeText, string? Default)> properties,
        string accessibility)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"{accessibility} sealed class {objectName}");
        builder.AppendLine("{");
        builder.Append("    public ").Append(objectName).Append('(');

        for (var index = 0; index < properties.Count; index++)
        {
            if (index > 0) builder.Append(", ");
            builder.Append(properties[index].TypeText).Append(' ').Append(properties[index].SourceName);
            if (properties[index].Default is { } value) builder.Append(" = ").Append(value);
        }

        builder.AppendLine(")");
        builder.AppendLine("    {");
        foreach (var property in properties)
            builder.Append("        ").Append(property.PropertyName).Append(" = ").Append(property.SourceName).AppendLine(";");
        builder.AppendLine("    }");

        foreach (var property in properties)
        {
            builder.AppendLine();
            builder.Append("    public ").Append(property.TypeText).Append(' ').Append(property.PropertyName).AppendLine(" { get; set; }");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static List<IdentifierNameSyntax> ParameterReferences(
        MethodContext context, ISet<string> parameters, CancellationToken cancellationToken)
    {
        var references = new List<IdentifierNameSyntax>();

        foreach (var node in context.Declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (!parameters.Contains(node.Identifier.ValueText)) continue;
            if (context.Model.GetSymbolInfo(node, cancellationToken).Symbol is not IParameterSymbol symbol) continue;
            if (!parameters.Contains(symbol.Name)) continue;

            references.Add(node);
        }

        return references;
    }

    /// <summary>The receiver an invocation already goes through, when it is safe to name twice.</summary>
    private static string? ReceiverOf(
        InvocationExpressionSyntax invocation, INamedTypeSymbol owner, SemanticModel model, CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax access)
            return IsStable(access.Expression) ? access.Expression.ToString() : null;

        // An unqualified call resolves to this.M inside the type's own instance members.
        return InstanceResolution.IsInstancePosition(invocation) &&
               InstanceResolution.EnclosingType(invocation, model, cancellationToken) is { } enclosing &&
               InstanceResolution.IsOrDerivesFrom(enclosing, owner)
            ? "this"
            : null;
    }

    /// <summary>A receiver with no side effect and no dependence on evaluation order.</summary>
    private static bool IsStable(ExpressionSyntax expression) => expression switch
    {
        ThisExpressionSyntax or BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax access => IsStable(access.Expression),
        _ => false,
    };

    private static string AccessibilityOf(MethodDeclarationSyntax declaration)
    {
        if (declaration.Modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (declaration.Modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        if (declaration.Modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        return "internal";
    }

    private static string UpperFirst(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string LowerFirst(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static IEnumerable<SyntaxToken> Commas(int count) =>
        Enumerable.Repeat(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space), Math.Max(0, count - 1));

    private static string Indentation(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToString().Replace("\r", string.Empty);
        var lastBreak = leading.LastIndexOf('\n');
        return lastBreak < 0 ? string.Empty : leading[(lastBreak + 1)..];
    }

    private static string Reindent(string text, string indentation)
    {
        if (indentation.Length == 0) return text;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) =>
            index == 0 || line.Length == 0 ? line : indentation + line));
    }

    private sealed record MethodContext(
        Solution Solution,
        Document Document,
        CompilationUnitSyntax Root,
        SemanticModel Model,
        MethodDeclarationSyntax Declaration,
        TypeDeclarationSyntax TypeDeclaration,
        IMethodSymbol Symbol);
}
