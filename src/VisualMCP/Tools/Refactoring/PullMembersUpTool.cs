using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Refactoring;

[McpServerToolType]
public static class PullMembersUpTool
{
    [McpServerTool, Description(
        "Move one or more members from a class up into its base class or a directly implemented interface. " +
        "When pulling into an interface, only the signature is added (the implementation stays in the class). " +
        "When pulling into a base class, the member is physically moved. " +
        "Equivalent to ReSharper 'Pull Members Up'. Requires LoadSolution first.")]
    public static async Task<object> PullMembersUp(
        [Description("Name of the source class")] string className,
        [Description("Names of the members to pull up")] string[] memberNames,
        [Description("Name of the target base class or interface to pull into (must already be in the class's base list)")] string targetName,
        [Description("Dry run â€” show changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        // Find source class
        var classCandidates = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.Equals(className, StringComparison.OrdinalIgnoreCase),
            SymbolFilter.Type);

        var classSymbol = classCandidates.OfType<INamedTypeSymbol>()
            .Where(s => s.TypeKind == TypeKind.Class)
            .FirstOrDefault();

        if (classSymbol is null)
            return new { error = $"Class '{className}' not found." };

        // Find target (base class or interface)
        var allBases = classSymbol.AllInterfaces
            .Cast<INamedTypeSymbol>()
            .Concat(EnumerateBases(classSymbol))
            .ToList();

        var targetSymbol = allBases.FirstOrDefault(t =>
            t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

        if (targetSymbol is null)
            return new
            {
                error = $"'{targetName}' is not in the base list of '{className}'. Available targets: " +
                        string.Join(", ", allBases.Select(b => b.Name)),
            };

        bool pullIntoInterface = targetSymbol.TypeKind == TypeKind.Interface;

        // Resolve member symbols to pull
        var resolvedMembers = new List<ISymbol>();
        foreach (var mName in memberNames)
        {
            var m = classSymbol.GetMembers(mName).FirstOrDefault();
            if (m is null)
                return new { error = $"Member '{mName}' not found in '{className}'." };
            resolvedMembers.Add(m);
        }

        // Validate: interface pull only allows methods, properties, events
        if (pullIntoInterface)
        {
            var invalid = resolvedMembers.Where(m => m is not (IMethodSymbol or IPropertySymbol or IEventSymbol)).ToList();
            if (invalid.Count > 0)
                return new { error = $"Cannot pull fields into an interface: {string.Join(", ", invalid.Select(m => m.Name))}." };
        }

        // â”€â”€ Locate source document â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var srcLoc = classSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (srcLoc is null) return new { error = "Class has no source location." };

        var srcTree = srcLoc.SourceTree!;
        var srcDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == srcTree);
        if (srcDoc?.FilePath is null) return new { error = "Could not locate source document." };

        var srcRoot = await srcDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (srcRoot is null) return new { error = "Could not parse source file." };

        var srcClassNode = srcRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.Text.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (srcClassNode is null) return new { error = "Could not locate class node." };

        // â”€â”€ Locate target document â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var tgtLoc = targetSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (tgtLoc is null) return new { error = $"'{targetName}' has no source location (may be external)." };

        var tgtTree = tgtLoc.SourceTree!;
        var tgtDoc = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync().Result == tgtTree);
        if (tgtDoc?.FilePath is null) return new { error = "Could not locate target document." };

        var tgtRoot = await tgtDoc.GetSyntaxRootAsync() as CompilationUnitSyntax;
        if (tgtRoot is null) return new { error = "Could not parse target file." };

        var tgtTypeNode = tgtRoot.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(n => n.Identifier.Text.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (tgtTypeNode is null) return new { error = "Could not locate target type node." };

        // â”€â”€ Build moved/added member syntax â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Collect the syntax nodes for the members in the source class
        var memberNodesToMove = new List<MemberDeclarationSyntax>();
        foreach (var sym in resolvedMembers)
        {
            var symLoc = sym.Locations.FirstOrDefault(l => l.IsInSource);
            if (symLoc is null) continue;
            var mNode = srcRoot.FindNode(symLoc.SourceSpan)
                               .FirstAncestorOrSelf<MemberDeclarationSyntax>();
            if (mNode is not null)
                memberNodesToMove.Add(mNode);
        }

        if (memberNodesToMove.Count == 0)
            return new { error = "Could not locate any member declaration nodes." };

        var pulledMemberNames = memberNodesToMove.Select(MemberName).ToList();

        if (dryRun)
            return new
            {
                dryRun = true,
                className,
                targetName,
                pullMode = pullIntoInterface ? "add signature to interface" : "move to base class",
                members = pulledMemberNames,
            };

        // Build target-side additions
        List<MemberDeclarationSyntax> targetAdditions;
        if (pullIntoInterface)
        {
            // Add abstract-style signatures (no body, no access modifier)
            targetAdditions = memberNodesToMove
                .Select(m => ToInterfaceMember(m))
                .Where(m => m is not null)
                .Cast<MemberDeclarationSyntax>()
                .ToList();
        }
        else
        {
            // Physical move: use the nodes as-is (strip private, keep public/protected)
            targetAdditions = memberNodesToMove
                .Select(m => m.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed))
                .ToList();
        }

        // Update target type
        var newTgtTypeNode = tgtTypeNode.AddMembers(targetAdditions.ToArray());
        var newTgtRoot = tgtRoot.ReplaceNode(tgtTypeNode, newTgtTypeNode);

        // Update source type: remove moved members (only when pulling into base class)
        CompilationUnitSyntax newSrcRoot = srcRoot;
        if (!pullIntoInterface)
        {
            var toRemove = memberNodesToMove.ToHashSet(SyntaxNodeReferenceComparer.Instance);
            var newSrcClassNode = srcClassNode.RemoveNodes(toRemove.Cast<SyntaxNode>(), SyntaxRemoveOptions.KeepLeadingTrivia)!;
            newSrcRoot = srcRoot.ReplaceNode(srcClassNode, newSrcClassNode);
        }

        // Write files
        await File.WriteAllTextAsync(tgtDoc.FilePath, newTgtRoot.ToFullString(), System.Text.Encoding.UTF8);
        if (!string.Equals(srcDoc.FilePath, tgtDoc.FilePath, StringComparison.OrdinalIgnoreCase))
            await File.WriteAllTextAsync(srcDoc.FilePath, newSrcRoot.ToFullString(), System.Text.Encoding.UTF8);

        return new
        {
            dryRun = false,
            className,
            targetName,
            pullMode = pullIntoInterface ? "signature added to interface" : "members moved to base class",
            members = pulledMemberNames,
            sourceFile = srcDoc.FilePath,
            targetFile = tgtDoc.FilePath,
        };
    }

    private sealed class SyntaxNodeReferenceComparer : IEqualityComparer<MemberDeclarationSyntax>
    {
        public static readonly SyntaxNodeReferenceComparer Instance = new();
        public bool Equals(MemberDeclarationSyntax? x, MemberDeclarationSyntax? y) => ReferenceEquals(x, y);
        public int GetHashCode(MemberDeclarationSyntax obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateBases(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    private static string MemberName(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax meth   => meth.Identifier.Text,
        PropertyDeclarationSyntax prop => prop.Identifier.Text,
        FieldDeclarationSyntax field   => field.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
        EventDeclarationSyntax evt     => evt.Identifier.Text,
        _                              => "",
    };

    private static MemberDeclarationSyntax? ToInterfaceMember(MemberDeclarationSyntax m)
    {
        // Strip access modifiers, body, attributes â€” leave only the signature + semicolon
        switch (m)
        {
            case MethodDeclarationSyntax meth:
                return meth
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithBody(null)
                    .WithExpressionBody(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

            case PropertyDeclarationSyntax prop:
                // Produce: ReturnType Name { get; set; }
                var accessors = new List<AccessorDeclarationSyntax>();
                if (prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true)
                    accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                if (prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
                    accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));

                return prop
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                    .WithExpressionBody(null)
                    .WithInitializer(null)
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

            case EventDeclarationSyntax evt:
                return evt
                    .WithModifiers(SyntaxFactory.TokenList())
                    .WithAccessorList(null)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed);

            default:
                return null;
        }
    }
}
