using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.ComponentModel;
using Snitch.Workspace;
using CSharpCompilationUnit = Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpSyntaxFactory = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using VisualBasicCompilationUnit = Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using VisualBasicSyntaxFactory = Microsoft.CodeAnalysis.VisualBasic.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

public interface IOptimizeUsingsService
{
    Task<object> OptimizeUsings(string? projectName, string? filePath, bool sort, bool dryRun);
}

/// <summary>
/// ReSharper's "Optimize Usings" for both languages a .NET solution can hold: C# removes the
/// <c>using</c> directives no symbol needs and sorts the rest, Visual Basic does the same to its
/// <c>Imports</c> statements. The two halves answer the same question — which namespaces does the file
/// actually reference — with the same semantic model, and keep the same things: an alias, a type import
/// (<c>Imports System.Math</c>), and a namespace an unqualified name still needs.
/// </summary>
public sealed class OptimizeUsingsService : IOptimizeUsingsService
{
    private readonly IRoslynWorkspace _workspace;
    public OptimizeUsingsService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> OptimizeUsings(
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("Optional: restrict to a single file by absolute path")] string? filePath = null,
        [Description("Sort remaining using directives / Imports statements alphabetically (default: true)")] bool sort = true,
        [Description("Dry run — report changes without writing to disk (default: false)")] bool dryRun = false)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var projects = solution.Projects
            .Where(p => projectName is null || p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectName is not null && projects.Count == 0)
            return new { error = $"Project '{projectName}' not found." };

        var results = new List<object>();
        int totalRemoved = 0;
        int totalFiles = 0;

        foreach (var project in projects)
        {
            var docs = project.Documents
                .Where(d => d.SourceCodeKind == SourceCodeKind.Regular &&
                            (filePath is null || string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (var document in docs)
            {
                var root = await document.GetSyntaxRootAsync();
                var model = await document.GetSemanticModelAsync();
                if (root is null || model is null) continue;

                var change = project.Language == LanguageNames.VisualBasic
                    ? OptimizeVisualBasic(root, model, sort)
                    : OptimizeCSharp(root, model, sort);

                if (change is not { } applied) continue;

                totalFiles++;
                totalRemoved += applied.Removed.Count;

                results.Add(new
                {
                    document.FilePath,
                    language = project.Language,
                    RemovedUsings = applied.Removed.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    Sorted = sort,
                });

                if (!dryRun && document.FilePath is not null)
                    await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, document, applied.Text);
            }
        }

        return new
        {
            dryRun,
            projectFilter = projectName ?? "all",
            fileFilter = filePath ?? "all",
            filesModified = totalFiles,
            totalUsingsRemoved = totalRemoved,
            files = results,
        };
    }

    // ── C# ───────────────────────────────────────────────────────────────────

    private static UsingChange? OptimizeCSharp(SyntaxNode root, SemanticModel model, bool sort)
    {
        if (root is not CSharpCompilationUnit unit) return null;

        var usings = unit.Usings;
        if (usings.Count == 0) return null;

        var usedNamespaces = CollectUsedNamespacesCSharp(unit, model);

        var removed = new HashSet<string>();
        foreach (var u in usings)
        {
            // Keep aliases (using X = Y) and using static — only remove plain namespace usings
            if (u.Alias is not null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                continue;

            var ns = u.Name?.ToString();
            if (ns is not null && !usedNamespaces.Contains(ns))
                removed.Add(ns);
        }

        if (removed.Count == 0 && !sort) return null;

        var kept = usings.Where(u =>
        {
            if (u.Alias is not null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) return true;
            var ns = u.Name?.ToString();
            return ns is null || !removed.Contains(ns);
        }).ToList();

        if (sort)
            kept = SortUsings(kept);

        var newRoot = unit.WithUsings(CSharpSyntaxFactory.List(kept));
        if (removed.Count == 0 && newRoot.IsEquivalentTo(unit)) return null;

        return new UsingChange(newRoot.ToFullString(), removed.OrderBy(x => x).ToList());
    }

    private static HashSet<string> CollectUsedNamespacesCSharp(CSharpCompilationUnit root, SemanticModel model)
    {
        var used = new HashSet<string>();

        foreach (var node in root.DescendantNodes())
        {
            // Skip the using directives themselves
            if (node.Parent is UsingDirectiveSyntax) continue;
            if (node is UsingDirectiveSyntax) continue;

            ISymbol? symbol = null;

            if (node is CSharpSyntax.IdentifierNameSyntax or CSharpSyntax.GenericNameSyntax)
            {
                var info = model.GetSymbolInfo(node);
                symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            }

            if (symbol is null) continue;

            AddNamespaces(symbol, used);
        }

        return used;
    }

    private static List<UsingDirectiveSyntax> SortUsings(List<UsingDirectiveSyntax> usings) =>
        // Order: System.* first, then others, each group alphabetically
        usings
            .OrderBy(u => u.Alias is not null ? 2 : u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 1 : 0)
            .ThenBy(u =>
            {
                var name = u.Name?.ToString() ?? "";
                return name.StartsWith("System", StringComparison.Ordinal) ? 0 : 1;
            })
            .ThenBy(u => u.Name?.ToString() ?? "", StringComparer.Ordinal)
            .Select(u => u.WithLeadingTrivia(CSharpSyntaxFactory.ElasticMarker).WithTrailingTrivia(CSharpSyntaxFactory.ElasticMarker))
            .ToList();

    // ── Visual Basic ─────────────────────────────────────────────────────────

    /// <summary>
    /// VB spells an import as a statement, and one statement may carry several clauses
    /// (<c>Imports System.Text, System.IO</c>), so a statement is rebuilt when only some of its clauses
    /// are unnecessary, and dropped when none is left. An aliased or XML-namespace clause is always kept;
    /// only file-level imports are touched, which is where VB conventions put them.
    /// </summary>
    private static UsingChange? OptimizeVisualBasic(SyntaxNode root, SemanticModel model, bool sort)
    {
        if (root is not VisualBasicCompilationUnit unit) return null;

        var usedNamespaces = CollectUsedNamespacesVisualBasic(unit, model);
        var removed = new List<string>();

        var rewritten = unit.WithImports(Optimize(unit.Imports));

        var text = rewritten.ToFullString();
        return string.Equals(text, unit.ToFullString(), StringComparison.Ordinal)
            ? null
            : new UsingChange(text, removed);

        SyntaxList<ImportsStatementSyntax> Optimize(SyntaxList<ImportsStatementSyntax> imports)
        {
            if (imports.Count == 0) return imports;

            var kept = new List<ImportsStatementSyntax>();
            foreach (var statement in imports)
            {
                var clauses = new List<ImportsClauseSyntax>();
                foreach (var clause in statement.ImportsClauses)
                {
                    switch (clause)
                    {
                        // An alias (Imports IO = System.IO) and an XML namespace import are not
                        // namespace imports: the first renames a name, the second is not code.
                        case XmlNamespaceImportsClauseSyntax:
                            clauses.Add(clause);
                            break;

                        case SimpleImportsClauseSyntax simple when simple.Alias is not null:
                            clauses.Add(clause);
                            break;

                        case SimpleImportsClauseSyntax simple:
                        {
                            var name = simple.Name.ToString();

                            // `Imports System.Math` imports the members of a type, not a namespace:
                            // the symbol tells them apart, and a type import is always kept.
                            if (model.GetSymbolInfo(simple.Name).Symbol is INamedTypeSymbol)
                            {
                                clauses.Add(clause);
                                break;
                            }

                            if (IsUsedNamespace(name, usedNamespaces)) clauses.Add(clause);
                            else removed.Add(name);
                            break;
                        }

                        default:
                            clauses.Add(clause);
                            break;
                    }
                }

                if (clauses.Count == 0) continue;

                kept.Add(clauses.Count == statement.ImportsClauses.Count
                    ? statement
                    : statement.WithImportsClauses(VisualBasicSyntaxFactory.SeparatedList(clauses)));
            }

            if (!sort || SameOrder(kept)) return VisualBasicSyntaxFactory.List(kept);

            return VisualBasicSyntaxFactory.List(kept
                .OrderBy(OptimizationRank)
                .ThenBy(statement => ImportsName(statement).StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(ImportsName, StringComparer.Ordinal)
                .Select(statement => statement
                    .WithLeadingTrivia(VisualBasicSyntaxFactory.ElasticMarker)
                    .WithTrailingTrivia(VisualBasicSyntaxFactory.ElasticCarriageReturnLineFeed)));
        }

        int OptimizationRank(ImportsStatementSyntax statement) =>
            statement.ImportsClauses.OfType<SimpleImportsClauseSyntax>().Any(simple => simple.Alias is not null) ? 2
            : IsTypeImport(statement) ? 1
            : 0;

        string ImportsName(ImportsStatementSyntax statement) => statement.ImportsClauses.Count switch
        {
            0 => "",
            _ => statement.ImportsClauses[0] switch
            {
                SimpleImportsClauseSyntax simple => simple.Name.ToString(),
                _ => statement.ImportsClauses[0].ToString(),
            },
        };

        bool IsTypeImport(ImportsStatementSyntax statement) =>
            statement.ImportsClauses.Count == 1 &&
            statement.ImportsClauses[0] is SimpleImportsClauseSyntax { Alias: null } simple &&
            model.GetSymbolInfo(simple.Name).Symbol is INamedTypeSymbol;

        /// <summary>Whether sorting would move nothing: rewriting a sorted file would only reformat it.</summary>
        bool SameOrder(List<ImportsStatementSyntax> statements) => statements
            .Select(statement => Normalize(statement.ToString()))
            .SequenceEqual(
                statements
                    .OrderBy(OptimizationRank)
                    .ThenBy(statement => ImportsName(statement).StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(ImportsName, StringComparer.Ordinal)
                    .Select(statement => Normalize(statement.ToString())),
                StringComparer.Ordinal);

        static string Normalize(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    }

    private static HashSet<string> CollectUsedNamespacesVisualBasic(VisualBasicCompilationUnit root, SemanticModel model)
    {
        var used = new HashSet<string>();

        foreach (var node in root.DescendantNodes())
        {
            if (node is ImportsStatementSyntax) continue;
            if (node.Parent is ImportsStatementSyntax) continue;

            if (node is not VisualBasicSyntax.SimpleNameSyntax) continue;

            var symbol = model.GetSymbolInfo(node).Symbol;
            if (symbol is null) continue;

            AddNamespaces(symbol, used);
        }

        return used;
    }

    /// <summary>
    /// An import is needed when a referenced symbol lives in that namespace or in one below it:
    /// <c>Imports System.Text</c> is kept while a <c>System.Text.RegularExpressions</c> type is named,
    /// because the qualifier the source still uses starts with the imported namespace.
    /// </summary>
    private static bool IsUsedNamespace(string import, HashSet<string> used)
    {
        var name = import.StartsWith("Global.", StringComparison.OrdinalIgnoreCase)
            ? import["Global.".Length..]
            : import;

        return used.Any(namespaceName =>
            namespaceName.Equals(name, StringComparison.Ordinal) ||
            namespaceName.StartsWith(name + ".", StringComparison.Ordinal));
    }

    // ── shared ───────────────────────────────────────────────────────────────

    private static void AddNamespaces(ISymbol symbol, HashSet<string> used)
    {
        if (GetNamespace(symbol) is { } ns) used.Add(ns);

        // Also record namespace of the symbol's containing type (for nested types)
        if (symbol.ContainingType is not null && GetNamespace(symbol.ContainingType) is { } parent)
            used.Add(parent);
    }

    private static string? GetNamespace(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace) return null;
        return ns.ToDisplayString();
    }

    /// <summary>A file's rewritten text and the imports that were dropped from it.</summary>
    private readonly record struct UsingChange(string Text, List<string> Removed);
}
