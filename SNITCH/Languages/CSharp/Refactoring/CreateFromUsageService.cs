using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Storage;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>
/// ReSharper's "Create from usage": the code already calls a member (or names a type) that does not
/// exist, and the compiler says so. Instead of the caller writing the stub by hand, this reads the
/// usage — receiver, arguments and their types from the semantic model — and creates the member on
/// the receiver's own type, or the type in the current namespace. The edit is accepted only when the
/// project does not gain compiler errors.
/// </summary>
public interface ICreateFromUsageService
{
    Task<object> CreateFromUsage(
        string filePath,
        int line,
        string? memberName,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class CreateFromUsageService : ICreateFromUsageService
{
    private const string Throws = "throw new System.NotImplementedException();";

    private readonly IRoslynWorkspace _workspace;
    private readonly IVisualMcpStore? _store;

    public CreateFromUsageService(IRoslynWorkspace workspace, IVisualMcpStore? store = null)
    {
        _workspace = workspace;
        _store = store;
    }

    public async Task<object> CreateFromUsage(
        string filePath,
        int line,
        string? memberName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new { error = "A file path is required." };
        if (line <= 0) return new { error = "A 1-based line number is required." };

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution first." };

        var document = FindDocument(solution, filePath);
        if (document is null) return new { error = $"{filePath} is not a document of the loaded solution." };

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (root is null || model is null) return new { error = $"Could not read {Path.GetFileName(document.FilePath)}." };

        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
                                 diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 == line)
            .Where(diagnostic => memberName is null ||
                                 diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan)
                                     .Contains(memberName, StringComparison.Ordinal) == true)
            .ToList();

        var target = diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Id is "CS1061" or "CS0246" or "CS0103" or "CS0117" or "CS1501" or "CS7036");
        if (target is null)
            return new
            {
                error = diagnostics.Count == 0
                    ? $"No compiler error on line {line}" + (memberName is null ? "." : $" mentioning '{memberName}'.")
                    : $"The error on line {line} is not one this tool can satisfy: " +
                      string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Id + " " + diagnostic.GetMessage())),
                supported = new[]
                {
                    "CS1061 member not found",
                    "CS0117 static member not found on a type",
                    "CS1501 no overload takes these arguments",
                    "CS7036 a required argument is missing",
                    "CS0246 type not found",
                    "CS0103 unknown name used as a call",
                },
            };

        var node = root.FindNode(target.Location.SourceSpan, getInnermostNodeForTie: true);

        var edit = target.Id switch
        {
            "CS1061" => await MemberFromUsageAsync(node, model, solution, document, cancellationToken),
            "CS0117" => await StaticMemberOnTypeAsync(node, model, solution, document, cancellationToken),
            "CS1501" or "CS7036" => await OverloadFromUsageAsync(node, model, solution, document, cancellationToken),
            "CS0246" => await TypeFromUsageAsync(node, solution, document, cancellationToken),
            _ => await StaticMemberFromUsageAsync(node, model, document, cancellationToken),
        };

        if (edit.Error is not null) return new { error = edit.Error, line, diagnostic = target.Id, created = edit.Member };

        var afterSolution = solution.WithDocumentText(edit.Document!.Id, SourceText.From(edit.NewText!));
        var gate = await RefactoringCompilerGate.CompareAsync(
            solution, afterSolution, new[] { edit.Document.Project.Id }, cancellationToken);

        if (!gate.Passed)
            return new
            {
                error = $"Refused: creating '{edit.Member}' would add compiler errors ({gate.Before} -> {gate.After}).",
                errors = gate.NewErrors,
                created = edit.Member,
                signature = edit.Signature,
            };

        if (!dryRun)
        {
            _store?.RecordEdit("create_from_usage", edit.Document.FilePath!, edit.Member, edit.BeforeText!, edit.NewText!);
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, edit.Document, edit.NewText!, cancellationToken);
        }

        return new
        {
            tool = "create_from_usage",
            created = edit.Member,
            kind = edit.Kind,
            @file = edit.Document.FilePath,
            line,
            diagnostic = target.Id,
            signature = edit.Signature,
            compilerErrors = new { before = gate.Before, after = gate.After },
            dryRun,
            applied = !dryRun,
            preview = RefactoringFileWriter.Diff(edit.BeforeText!, edit.NewText!),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private sealed record Edit(
        Document? Document, string? NewText, string? BeforeText, string? Member, string Kind, string Signature, string? Error);

    private static Edit Fail(string error) => new(null, null, null, null, string.Empty, string.Empty, error);

    // ── CS1061: a member missing from a type that is in the solution ─────────

    private static async Task<Edit> MemberFromUsageAsync(
        SyntaxNode node, SemanticModel model, Solution solution, Document document, CancellationToken cancellationToken)
    {
        var access = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        var binding = access is null ? node.AncestorsAndSelf().OfType<MemberBindingExpressionSyntax>().FirstOrDefault() : null;

        var receiver = access?.Expression ??
                       binding?.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression;
        var name = access?.Name.Identifier.ValueText ?? binding?.Name.Identifier.ValueText;

        if (receiver is null || string.IsNullOrWhiteSpace(name))
            return Fail("The missing member is not used as `receiver.Member`; nothing to attach it to.");

        if (model.GetTypeInfo(receiver, cancellationToken).Type is not INamedTypeSymbol receiverType)
            return Fail($"The type of `{receiver}` could not be resolved.");

        var reference = receiverType.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return Fail($"'{receiverType.ToDisplayString()}' is not declared in the solution, so a member cannot be added to it.");

        var targetDocument = solution.GetDocument(reference.SyntaxTree) ?? document;
        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        var targetText = await targetDocument.GetTextAsync(cancellationToken);
        if (targetRoot is null) return Fail("Could not read the file that declares the receiver's type.");

        var declaration = targetRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == receiverType.Name);
        if (declaration is null) return Fail($"Could not find the declaration of '{receiverType.Name}'.");

        var invocation = (access ?? (SyntaxNode?)binding)?.Parent as InvocationExpressionSyntax;

        string stub;
        string kind;

        if (invocation is not null)
        {
            var parameters = invocation.ArgumentList.Arguments
                .Select((argument, index) =>
                    $"{TypeOf(argument.Expression, model)} " +
                    $"{argument.NameColon?.Name.Identifier.ValueText ?? "arg" + (index + 1)}")
                .ToList();

            var isStatement = IsVoidContext(invocation);
            var returnType = isStatement ? "void" : ConvertedTypeOf(invocation, model);

            stub = $"public {returnType} {name}({string.Join(", ", parameters)}) {{ {Throws} }}";
            kind = "method";
        }
        else if (access?.Parent is AssignmentExpressionSyntax assignment && assignment.Left == access)
        {
            stub = $"public object {name} {{ get => {Throws} set => {Throws} }}";
            kind = "property";
        }
        else
        {
            stub = $"public object {name} => {Throws}";
            kind = "property";
        }

        var newText = ImplementMembersService.InsertMembers(targetText, declaration, new[] { stub });
        return new Edit(targetDocument, newText, targetText.ToString(), name, kind, stub, null);
    }

    /// <summary>
    /// Whether the call's value is discarded: a statement, or the expression body of a void method.
    /// The converted type of the latter is not a reliable signal, so the context is read directly.
    /// </summary>
    private static bool IsVoidContext(InvocationExpressionSyntax invocation) =>
        invocation.Parent is ExpressionStatementSyntax
        || (invocation.Parent is ArrowExpressionClauseSyntax clause &&
            clause.Parent is MethodDeclarationSyntax method &&
            method.ReturnType.ToString() == "void");

    /// <summary>
    /// The type an argument already has, so `Log(42)` produces an <c>int</c> parameter and not an
    /// <c>object</c> one: the call site is the specification.
    /// </summary>
    private static string TypeOf(ExpressionSyntax expression, SemanticModel model) =>
        model.GetTypeInfo(expression).Type?.ToMinimalDisplayString(model, expression.SpanStart) ?? "object";

    /// <summary>The type the surrounding code expects from a call, when it expects one.</summary>
    private static string ConvertedTypeOf(ExpressionSyntax expression, SemanticModel model)
    {
        var converted = model.GetTypeInfo(expression).ConvertedType;
        return converted is null or { TypeKind: TypeKind.Error } ||
               converted.SpecialType == SpecialType.System_Object
            ? "object"
            : converted.ToMinimalDisplayString(model, expression.SpanStart);
    }

    // ── CS0117: a static member missing from a type that is in the solution ──

    private static async Task<Edit> StaticMemberOnTypeAsync(
        SyntaxNode node, SemanticModel model, Solution solution, Document document, CancellationToken cancellationToken)
    {
        var access = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        if (access is null)
            return Fail("The missing member is not used as `Type.Member`, so there is no type to add it to.");

        var name = access.Name.Identifier.ValueText;
        var type = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol as INamedTypeSymbol
                   ?? model.GetTypeInfo(access.Expression, cancellationToken).Type as INamedTypeSymbol;
        if (type is null) return Fail($"The type of `{access.Expression}` could not be resolved.");

        return await InsertStubAsync(type, name, access.Parent as InvocationExpressionSyntax,
            isStatic: true, model, solution, document, cancellationToken);
    }

    // ── CS1501 / CS7036: the method exists, the arguments do not ─────────────

    private static async Task<Edit> OverloadFromUsageAsync(
        SyntaxNode node, SemanticModel model, Solution solution, Document document, CancellationToken cancellationToken)
    {
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null)
            return Fail("The error is not on a method call, so there is no overload to add.");

        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
        if (name is null) return Fail("The called member has no simple name.");

        INamedTypeSymbol? type = null;
        if (invocation.Expression is MemberAccessExpressionSyntax access)
        {
            type = model.GetSymbolInfo(access.Expression, cancellationToken).Symbol as INamedTypeSymbol
                   ?? model.GetTypeInfo(access.Expression, cancellationToken).Type as INamedTypeSymbol;
        }

        var candidates = model.GetSymbolInfo(invocation, cancellationToken).CandidateSymbols
            .OfType<IMethodSymbol>()
            .ToList();

        type ??= candidates.Select(method => method.ContainingType).FirstOrDefault(containing => containing is not null)
                ?? model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;

        if (type is null) return Fail("The receiver's type could not be resolved.");

        var calledOnTypeName = invocation.Expression is MemberAccessExpressionSyntax staticAccess &&
                               model.GetSymbolInfo(staticAccess.Expression, cancellationToken).Symbol is INamedTypeSymbol;
        var allStatic = candidates.Count > 0 && candidates.All(method => method.IsStatic);

        return await InsertStubAsync(type, name, invocation, calledOnTypeName || allStatic,
            model, solution, document, cancellationToken);
    }

    /// <summary>
    /// The shared tail of CS0117 and CS1501/CS7036: find the type's declaration in the solution, build
    /// the stub from the call site (parameter types and names, return type from the context) and insert
    /// it with the same helper implement_members uses.
    /// </summary>
    private static async Task<Edit> InsertStubAsync(
        INamedTypeSymbol type, string name, InvocationExpressionSyntax? invocation, bool isStatic,
        SemanticModel model, Solution solution, Document document, CancellationToken cancellationToken)
    {
        var reference = type.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return Fail($"'{type.ToDisplayString()}' is not declared in the solution, so a member cannot be added to it.");

        var targetDocument = solution.GetDocument(reference.SyntaxTree) ?? document;
        var targetRoot = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        var targetText = await targetDocument.GetTextAsync(cancellationToken);
        if (targetRoot is null) return Fail("Could not read the file that declares the receiver's type.");

        var declaration = targetRoot.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == type.Name);
        if (declaration is null) return Fail($"Could not find the declaration of '{type.Name}'.");

        string stub;
        string kind;

        if (invocation is not null)
        {
            var parameters = invocation.ArgumentList.Arguments
                .Select((argument, index) =>
                    $"{TypeOf(argument.Expression, model)} " +
                    $"{argument.NameColon?.Name.Identifier.ValueText ?? "arg" + (index + 1)}")
                .ToList();

            var returnType = IsVoidContext(invocation) ? "void" : ConvertedTypeOf(invocation, model);
            var modifier = isStatic ? "static " : string.Empty;
            stub = $"public {modifier}{returnType} {name}({string.Join(", ", parameters)}) {{ {Throws} }}";
            kind = "method";
        }
        else
        {
            var modifier = isStatic ? "static " : string.Empty;
            stub = $"public {modifier}object {name} {{ get => {Throws} set => {Throws} }}";
            kind = "property";
        }

        var newText = ImplementMembersService.InsertMembers(targetText, declaration, new[] { stub });
        return new Edit(targetDocument, newText, targetText.ToString(), name, kind, stub, null);
    }

    // ── CS0246: the type does not exist ──────────────────────────────────────

    private static async Task<Edit> TypeFromUsageAsync(
        SyntaxNode node, Solution solution, Document document, CancellationToken cancellationToken)
    {
        var identifier = node.AncestorsAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault()
                         ?? node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault();
        if (identifier is null) return Fail("The missing type could not be identified at this position.");

        var name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name)) return Fail("The missing type has no name.");

        var text = await document.GetTextAsync(cancellationToken);
        var source = text.ToString();
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var declaration = $"public sealed class {name}";

        var namespaceDeclaration = node.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        string newText;

        if (namespaceDeclaration is not null)
        {
            var namespaceLine = text.Lines.GetLineFromPosition(namespaceDeclaration.SpanStart);
            var namespaceIndent = text.ToString(TextSpan.FromBounds(namespaceLine.Start, namespaceDeclaration.SpanStart));
            var indentation = namespaceIndent + "    ";

            var block = $"{indentation}{declaration}{newline}{indentation}{{{newline}{indentation}}}{newline}{newline}";
            var closingLine = text.Lines.GetLineFromPosition(namespaceDeclaration.CloseBraceToken.SpanStart);
            newText = source[..closingLine.Start] + block + source[closingLine.Start..];
        }
        else
        {
            var suffix = source.EndsWith(newline, StringComparison.Ordinal) ? string.Empty : newline;
            newText = source + suffix + newline + declaration + newline + "{" + newline + "}" + newline;
        }

        return new Edit(document, newText, source, name, "type", declaration, null);
    }

    // ── CS0103: an unknown name, when it is invoked ──────────────────────────

    private static async Task<Edit> StaticMemberFromUsageAsync(
        SyntaxNode node, SemanticModel model, Document document, CancellationToken cancellationToken)
    {
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression is not IdentifierNameSyntax identifier)
            return Fail("The unknown name is not used as a method call; only that shape can be created safely.");

        var enclosing = model.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        if (enclosing is null)
            return Fail("The unknown name is not inside a type declared in the solution.");

        var text = await document.GetTextAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null) return Fail("Could not read the file.");

        var declaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(candidate => candidate.Identifier.ValueText == enclosing.Name);
        if (declaration is null) return Fail($"Could not find the declaration of '{enclosing.Name}'.");

        var parameters = invocation.ArgumentList.Arguments.Select((argument, index) =>
            $"{TypeOf(argument.Expression, model)} " +
            $"{argument.NameColon?.Name.Identifier.ValueText ?? "arg" + (index + 1)}");

        var isStatement = IsVoidContext(invocation);
        var stub = $"public {(isStatement ? "void" : "object")} {identifier.Identifier.ValueText}(" +
                   string.Join(", ", parameters) + $") {{ {Throws} }}";

        var newText = ImplementMembersService.InsertMembers(text, declaration, new[] { stub });
        return new Edit(document, newText, text.ToString(), identifier.Identifier.ValueText, "method", stub, null);
    }

    private static Document? FindDocument(Solution solution, string filePath)
    {
        var path = Path.GetFullPath(filePath);
        return solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path,
                                                       StringComparison.OrdinalIgnoreCase));
    }
}
