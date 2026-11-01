using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Snitch.Languages.CSharp.Refactoring;

/// <summary>One file a campaign rewrote, with its summary and a short diff.</summary>
public sealed record CampaignChange(string File, string Summary, string Diff);

/// <summary>One file a campaign left alone, and why.</summary>
public sealed record CampaignRefusal(string File, string Reason);

/// <summary>
/// The answer of <c>migration_campaign</c>. Every member is nullable so a refusal leaves the rest unset
/// and the MCP serializer drops them.
/// </summary>
public sealed record MigrationCampaignResult
{
    /// <summary>The campaign that ran.</summary>
    public string? Campaign { get; init; }

    /// <summary><c>solution</c> or the project the scope named.</summary>
    public string? Scope { get; init; }

    /// <summary>Whether the files were actually written.</summary>
    public bool? DryRun { get; init; }

    /// <summary>How many files the campaign considered as candidates.</summary>
    public int? FilesConsidered { get; init; }

    /// <summary>How many were changed (or would be, on a dry run).</summary>
    public int? FilesChanged { get; init; }

    /// <summary>The files the campaign rewrote.</summary>
    public IReadOnlyList<CampaignChange>? Changed { get; init; }

    /// <summary>The files it refused, each with the compiler's reason.</summary>
    public IReadOnlyList<CampaignRefusal>? Refused { get; init; }

    /// <summary>The campaign names this build supports.</summary>
    public IReadOnlyList<string>? Campaigns { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A feature-level migration: one named rewrite applied across a solution or a project, with the compiler
/// as the gate. The rewrites are the ones a codebase adopts all at once — <c>nullable</c> (a
/// <c>#nullable enable</c> header), <c>file-scoped-namespaces</c> (a block namespace becomes file-scoped),
/// <c>braces</c> (every brace-less embedded statement gets a block) and <c>primary-constructors</c> (each
/// eligible type, through the same compiler-gated conversion the refactoring uses).
/// </summary>
public interface IMigrationCampaignService
{
    Task<MigrationCampaignResult> RunAsync(
        string campaign,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken = default);
}

public sealed class MigrationCampaignService : IMigrationCampaignService
{
    private static readonly string[] Supported =
    [
        "nullable",
        "file-scoped-namespaces",
        "braces",
        "primary-constructors",
    ];

    private static readonly Regex NullableDirective =
        new(@"^[ \t]*#[ \t]*nullable\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IRoslynWorkspace _workspace;
    private readonly IConvertToPrimaryConstructorService _primaryConstructors;

    public MigrationCampaignService(
        IRoslynWorkspace workspace,
        IConvertToPrimaryConstructorService primaryConstructors)
    {
        _workspace = workspace;
        _primaryConstructors = primaryConstructors;
    }

    public async Task<MigrationCampaignResult> RunAsync(
        string campaign,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var name = (campaign ?? string.Empty).Trim().ToLowerInvariant();
        if (!Supported.Contains(name, StringComparer.Ordinal))
        {
            return new MigrationCampaignResult
            {
                Campaigns = Supported,
                Error = $"Unknown campaign '{campaign}'. Use one of {string.Join(", ", Supported)}.",
            };
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return new MigrationCampaignResult { Error = "No C# solution could be auto-located from the working directory." };

        var projects = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .Where(project => projectName is null || project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projects.Count == 0)
        {
            return new MigrationCampaignResult
            {
                Error = projectName is null
                    ? "The solution holds no C# project."
                    : $"Project '{projectName}' not found.",
            };
        }

        return name == "primary-constructors"
            ? await PrimaryConstructorsAsync(projects, projectName, dryRun, cancellationToken)
            : await RewriteAsync(name, solution, projects, projectName, dryRun, cancellationToken);
    }

    /// <summary>The three syntax campaigns: collect, gate, and write what compiled.</summary>
    private async Task<MigrationCampaignResult> RewriteAsync(
        string campaign,
        Solution solution,
        IReadOnlyList<Project> projects,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var pending = new List<Pending>();
        var considered = 0;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.SourceCodeKind != SourceCodeKind.Regular) continue;
                if (document.FilePath is null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root) continue;

                var source = await document.GetTextAsync(cancellationToken);
                var original = source.ToString();
                considered++;

                var rewritten = campaign switch
                {
                    "nullable" => AddNullableHeader(original),
                    "file-scoped-namespaces" => await FileScopedAsync(document, root, cancellationToken),
                    _ => await BracesAsync(document, root, cancellationToken),
                };

                if (rewritten is null || string.Equals(rewritten.Value.Text, original, StringComparison.Ordinal)) continue;
                pending.Add(new Pending(
                    document, original, rewritten.Value.Text, rewritten.Value.Summary,
                    source.Encoding ?? Encoding.UTF8));
            }
        }

        if (pending.Count == 0)
        {
            return new MigrationCampaignResult
            {
                Campaign = campaign,
                Scope = projectName ?? "solution",
                DryRun = dryRun,
                FilesConsidered = considered,
                FilesChanged = 0,
                Changed = [],
                Refused = [],
                Note = "Nothing to migrate: no file matched the campaign's precondition.",
            };
        }

        var kept = new List<Pending>();
        var refused = new List<CampaignRefusal>();
        var working = solution;

        foreach (var project in projects)
        {
            var forProject = pending.Where(entry => entry.Document.Project.Id == project.Id).ToList();
            if (forProject.Count == 0) continue;

            var batch = Apply(working, forProject);
            var gate = await RefactoringCompilerGate.CompareAsync(working, batch, [project.Id], cancellationToken);
            if (gate.Passed)
            {
                working = batch;
                kept.AddRange(forProject);
                continue;
            }

            // The project as a whole did not compile: isolate one file at a time, which is where the
            // per-file verdict comes from. Only the files named here are refused.
            foreach (var entry in forProject)
            {
                var single = Apply(working, [entry]);
                var singleGate = await RefactoringCompilerGate.CompareAsync(working, single, [project.Id], cancellationToken);
                if (singleGate.Passed)
                {
                    working = single;
                    kept.Add(entry);
                }
                else
                {
                    refused.Add(new CampaignRefusal(
                        entry.Document.FilePath!,
                        $"would add {singleGate.Added} compiler error(s): " +
                        string.Join("; ", singleGate.NewErrors.Take(3))));
                }
            }

        }

        var changed = new List<CampaignChange>();
        foreach (var entry in kept)
        {
            var diff = RefactoringFileWriter.Diff(entry.Original, entry.NewText);
            if (!dryRun)
            {
                await RefactoringFileWriter.WriteAndUpdateAsync(
                    _workspace, entry.Document, entry.NewText, cancellationToken);
            }

            changed.Add(new CampaignChange(entry.Document.FilePath!, entry.Summary, diff));
        }

        return new MigrationCampaignResult
        {
            Campaign = campaign,
            Scope = projectName ?? "solution",
            DryRun = dryRun,
            FilesConsidered = considered,
            FilesChanged = changed.Count,
            Changed = changed,
            Refused = refused,
            Note = dryRun
                ? "Preview only: every edit was compiled. Pass dryRun=false to write them."
                : $"{changed.Count} file(s) written, {refused.Count} refused.",
        };
    }

    /// <summary>The primary-constructor campaign: the refactoring, applied to every eligible type.</summary>
    private async Task<MigrationCampaignResult> PrimaryConstructorsAsync(
        IReadOnlyList<Project> projects,
        string? projectName,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var changed = new List<CampaignChange>();
        var refused = new List<CampaignRefusal>();
        var considered = 0;

        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.SourceCodeKind != SourceCodeKind.Regular || document.FilePath is null) continue;
                if (await document.GetSyntaxRootAsync(cancellationToken) is not CompilationUnitSyntax root) continue;

                foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (!IsPrimaryConstructorCandidate(type)) continue;
                    considered++;

                    var result = await _primaryConstructors.ConvertToPrimaryConstructor(
                        document.FilePath, type.Identifier.Text, dryRun, cancellationToken);

                    var json = JsonSerializer.SerializeToNode(result);
                    if (json is JsonObject answer && answer["error"] is JsonValue error)
                    {
                        refused.Add(new CampaignRefusal(
                            document.FilePath,
                            $"{type.Identifier.Text}: {error.GetValue<string>()}"));
                        continue;
                    }

                    var diff = json?["diff"] is JsonValue detail ? detail.GetValue<string>() : string.Empty;
                    changed.Add(new CampaignChange(
                        document.FilePath,
                        $"primary constructor on {type.Identifier.Text}",
                        diff));
                }
            }
        }

        return new MigrationCampaignResult
        {
            Campaign = "primary-constructors",
            Scope = projectName ?? "solution",
            DryRun = dryRun,
            FilesConsidered = considered,
            FilesChanged = changed.Count,
            Changed = changed,
            Refused = refused,
            Note = considered == 0
                ? "No type has the shape the conversion accepts (one constructor that only assigns parameters to fields)."
                : dryRun
                    ? "Preview only: each conversion ran its own compiler gate. Pass dryRun=false to write them."
                    : $"{changed.Count} type(s) converted, {refused.Count} refused.",
        };
    }

    /// <summary>The campaign names this build supports, for a caller that asked for an unknown one.</summary>
    public static IReadOnlyList<string> CampaignNames => Supported;

    // ── the three syntax rewrites ───────────────────────────────────────────────────────────────

    private static (string Text, string Summary)? AddNullableHeader(string text)
    {
        if (NullableDirective.IsMatch(text)) return null;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var header = "#nullable enable" + newline;

        // Keep a byte-order mark (as a character) at the very start of the file.
        return text.StartsWith('\uFEFF')
            ? (text[..1] + header + text[1..], "adds a '#nullable enable' header")
            : (header + text, "adds a '#nullable enable' header");
    }

    private static async Task<(string Text, string Summary)?> FileScopedAsync(
        Document document,
        CompilationUnitSyntax root,
        CancellationToken cancellationToken)
    {
        if (root.Members.Count != 1 || root.Members[0] is not NamespaceDeclarationSyntax block) return null;
        if (block.Name.IsMissing) return null;

        var fileScoped = FileScopedNamespaceDeclaration(
                block.AttributeLists,
                block.Modifiers,
                block.NamespaceKeyword,
                block.Name,
                Token(SyntaxKind.SemicolonToken),
                block.Externs,
                block.Usings,
                block.Members)
            .WithLeadingTrivia(block.GetLeadingTrivia())
            .WithTrailingTrivia(block.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newRoot = root.WithMembers(SingletonList<MemberDeclarationSyntax>(fileScoped));

        // Only the nodes carrying the annotation are formatted, so the rest of the file keeps the
        // formatting its author chose.
        var formatted = await Formatter.FormatAsync(
            document.WithSyntaxRoot(newRoot), Formatter.Annotation, cancellationToken: cancellationToken);

        return ((await formatted.GetTextAsync(cancellationToken)).ToString(),
                $"file-scoped namespace {block.Name}");
    }

    private static async Task<(string Text, string Summary)?> BracesAsync(
        Document document,
        CompilationUnitSyntax root,
        CancellationToken cancellationToken)
    {
        var embedded = EmbeddedStatements(root);
        if (embedded.Count == 0) return null;

        var newRoot = root.ReplaceNodes(embedded, (original, rewritten) =>
            Block(rewritten.WithoutLeadingTrivia())
                .WithLeadingTrivia(original.GetLeadingTrivia())
                .WithTrailingTrivia(original.GetTrailingTrivia())
                .WithAdditionalAnnotations(Formatter.Annotation));

        var formatted = await Formatter.FormatAsync(
            document.WithSyntaxRoot(newRoot), Formatter.Annotation, cancellationToken: cancellationToken);

        return ((await formatted.GetTextAsync(cancellationToken)).ToString(),
                $"adds braces to {embedded.Count} embedded statement(s)");
    }

    /// <summary>The statements a control statement embeds directly, and that can take a block.</summary>
    private static List<StatementSyntax> EmbeddedStatements(CompilationUnitSyntax root)
    {
        var statements = new List<StatementSyntax>();

        void Add(StatementSyntax statement)
        {
            // An 'else if' chain is already a nested statement, not a missing block, and a statement
            // carrying a conditional-compilation directive cannot be wrapped without moving the #if.
            if (statement is BlockSyntax or IfStatementSyntax) return;
            if (statement.ContainsDirectives) return;
            statements.Add(statement);
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax ifStatement:
                    Add(ifStatement.Statement);
                    break;
                case ElseClauseSyntax elseClause:
                    Add(elseClause.Statement);
                    break;
                case ForStatementSyntax forStatement:
                    Add(forStatement.Statement);
                    break;
                case ForEachStatementSyntax forEachStatement:
                    Add(forEachStatement.Statement);
                    break;
                case ForEachVariableStatementSyntax forEachVariable:
                    Add(forEachVariable.Statement);
                    break;
                case WhileStatementSyntax whileStatement:
                    Add(whileStatement.Statement);
                    break;
                case DoStatementSyntax doStatement:
                    Add(doStatement.Statement);
                    break;
                case UsingStatementSyntax usingStatement:
                    Add(usingStatement.Statement);
                    break;
                case LockStatementSyntax lockStatement:
                    Add(lockStatement.Statement);
                    break;
                case FixedStatementSyntax fixedStatement:
                    Add(fixedStatement.Statement);
                    break;
            }
        }

        return statements;
    }

    /// <summary>
    /// The shape the primary-constructor conversion accepts: one instance constructor that takes at
    /// least one parameter and whose body only assigns those parameters to fields and properties.
    /// </summary>
    private static bool IsPrimaryConstructorCandidate(TypeDeclarationSyntax type)
    {
        var constructors = type.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        if (constructors.Count != 1) return false;

        var constructor = constructors[0];
        if (constructor.ParameterList.Parameters.Count == 0) return false;
        if (constructor.Body is null) return false;

        // A base constructor call with arguments has nowhere to go in a primary constructor.
        if (constructor.Initializer is { } initializer && initializer.ArgumentList.Arguments.Count > 0) return false;

        if (constructor.Body.Statements.Count == 0) return false;

        foreach (var statement in constructor.Body.Statements)
        {
            if (statement is not ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax
                    {
                        RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                        Left: MemberAccessExpressionSyntax or IdentifierNameSyntax,
                        Right: IdentifierNameSyntax,
                    },
                })
            {
                return false;
            }
        }

        return true;
    }

    private Solution Apply(Solution solution, IReadOnlyList<Pending> entries)
    {
        foreach (var entry in entries)
            solution = solution.WithDocumentText(entry.Document.Id, SourceText.From(entry.NewText, entry.Encoding));

        return solution;
    }

    /// <summary>One document's rewrite, before the gate decides whether it survives.</summary>
    private sealed record Pending(
        Document Document,
        string Original,
        string NewText,
        string Summary,
        Encoding Encoding);
}
