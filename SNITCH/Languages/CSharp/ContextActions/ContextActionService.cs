using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.ContextActions;

/// <summary>
/// The context actions a headless engine can apply deterministically: the ReSharper families that are a
/// single verified edit over a document. Every action resolves its target from a 1-based line, previews
/// by default, and is refused when the projected edit would add a compiler error.
/// </summary>
public sealed partial class ContextActionService : IContextActionService
{
    private readonly IRoslynWorkspace _workspace;

    public ContextActionService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Every action, with the ReSharper action it mirrors. The list is the contract: an id that is not
    /// here is refused with the whole catalogue, so "not implemented" never reads as "not found".
    /// </summary>
    public static IReadOnlyList<(string Id, string Mirrors)> Catalogue { get; } =
    [
        ("import_type", "Import type / Add using"),
        ("check_param_null", "Check parameter for null"),
        ("check_all_params_null", "Check all parameters for null"),
        ("check_string_not_null_or_empty", "Check string parameter for null or empty"),
        ("check_collection_not_empty", "Check collection parameter for null or empty"),
        ("check_variable_null", "Check variable for null"),
        ("wrap_in_try_catch", "Surround with try/catch"),
        ("wrap_in_try_finally", "Surround with try/finally"),
        ("wrap_in_if", "Surround with if"),
        ("wrap_in_using", "Surround with using"),
        ("string_to_raw", "Convert to raw string"),
        ("string_to_verbatim", "Convert to verbatim string"),
        ("string_to_regular", "Convert to regular string"),
        ("add_braces", "Add braces to an embedded statement"),
        ("wrap_in_region", "Surround with #region"),
        ("foreach_to_for", "Convert foreach to for"),
        ("for_to_foreach", "Convert for to foreach"),
        ("if_to_switch", "Convert if/else-if chain to switch"),
        ("concat_to_interpolated", "Convert concatenation to interpolated string"),
        ("interpolated_to_concat", "Convert interpolated string to concatenation"),
        ("digit_separators", "Add digit separators to a numeric literal"),
        ("remove_argument_name", "Remove argument name"),
        ("check_dictionary_key", "Check dictionary key"),
        ("check_enum_defined", "Check enum value is defined"),
        ("invert_loop", "Invert loop direction"),
        ("check_index_range", "Check index for out of range"),
        ("merge_sequential_checks", "Merge sequential checks"),
        ("split_and_in_if", "Split '&&' condition in if"),
        ("join_null_check_with_usage", "Join null check with usage"),
        ("use_var", "Use 'var'"),
        ("use_explicit_type", "Use explicit type"),
        ("split_declaration", "Split declaration"),
        ("remove_braces", "Remove braces"),
        ("migrate_to_nullable", "Migrate to nullable reference types"),
        ("remove_digit_separators", "Remove digit separators"),
        ("use_configure_await_false", "Use ConfigureAwait(false)"),
    ];

    public async Task<object> Apply(
        string action,
        string filePath,
        int line,
        int? toLine = null,
        string? text = null,
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(action))
            return new { error = "An action is required." };
        if (string.IsNullOrWhiteSpace(filePath))
            return new { error = "A file path is required." };
        if (line < 1)
            return new { error = "Line must be 1-based." };

        var id = Normalize(action);
        if (!Catalogue.Any(entry => entry.Id == id))
            return new { error = $"Unknown context action '{action}'. Known: {string.Join(", ", Catalogue.Select(entry => entry.Id))}." };

        var resolution = await ResolveAsync(filePath, line, toLine ?? line, cancellationToken);
        if (resolution.Error is not null)
            return new { error = resolution.Error };
        var target = resolution.Target!;

        Outcome outcome;
        try
        {
            outcome = Dispatch(id, target, text);
        }
        catch (Exception ex)
        {
            return new { error = $"'{id}' failed on line {line}: {ex.Message}" };
        }

        if (outcome.Error is not null)
            return new { error = outcome.Error, detail = outcome.Details };

        var original = target.Text.ToString();
        if (string.Equals(original, outcome.Text, StringComparison.Ordinal))
            return new { error = $"'{id}' produced no change on line {line}." };

        var refusal = await CheckCompilesAsync(target, outcome.Text, cancellationToken);
        if (refusal is not null)
            return refusal;

        if (!dryRun)
            await RefactoringFileWriter.WriteAndUpdateAsync(_workspace, target.Document, outcome.Text, cancellationToken);

        return new
        {
            contextAction = id,
            mirrors = Catalogue.First(entry => entry.Id == id).Mirrors,
            file = target.Document.FilePath,
            line,
            toLine = target.ToLine,
            dryRun,
            applied = !dryRun,
            changed = outcome.Details,
            preview = RefactoringFileWriter.Diff(original, outcome.Text),
            note = dryRun ? "Preview only: pass dryRun=false to write the file." : "File written.",
        };
    }

    private Outcome Dispatch(string id, Target target, string? text) => id switch
    {
        "import_type" => ImportType(target, text),
        "check_param_null" => ArgumentGuards(target, text, Guard.Null, all: false),
        "check_all_params_null" => ArgumentGuards(target, null, Guard.Null, all: true),
        "check_string_not_null_or_empty" => ArgumentGuards(target, text, Guard.NotNullOrEmpty, all: false),
        "check_collection_not_empty" => ArgumentGuards(target, text, Guard.CollectionNotEmpty, all: false),
        "check_variable_null" => CheckVariableNull(target, text),
        "wrap_in_try_catch" => Wrap(target, null, WrapKind.TryCatch),
        "wrap_in_try_finally" => Wrap(target, null, WrapKind.TryFinally),
        "wrap_in_if" => string.IsNullOrWhiteSpace(text)
            ? Outcome.Fail("wrap_in_if needs the condition in text, e.g. 'value > 0'.")
            : Wrap(target, text.Trim(), WrapKind.If),
        "wrap_in_using" => string.IsNullOrWhiteSpace(text)
            ? Outcome.Fail("wrap_in_using needs the resource expression in text, e.g. 'new FileStream(path, FileMode.Open)'.")
            : Wrap(target, text.Trim(), WrapKind.Using),
        "string_to_raw" => ConvertString(target, StringTarget.Raw),
        "string_to_verbatim" => ConvertString(target, StringTarget.Verbatim),
        "string_to_regular" => ConvertString(target, StringTarget.Regular),
        "add_braces" => AddBraces(target),
        "wrap_in_region" => WrapInRegion(target, text),
        "foreach_to_for" => ForeachToFor(target),
        "for_to_foreach" => ForToForeach(target),
        "if_to_switch" => IfChainToSwitch(target),
        "concat_to_interpolated" => ConcatToInterpolated(target),
        "interpolated_to_concat" => InterpolatedToConcat(target),
        "digit_separators" => DigitSeparators(target),
        "remove_argument_name" => RemoveArgumentName(target, text),
        "check_dictionary_key" => CheckDictionaryKey(target, text),
        "check_enum_defined" => CheckEnumDefined(target, text),
        "invert_loop" => InvertLoop(target),
        "check_index_range" => CheckIndexRange(target, text),
        "merge_sequential_checks" => MergeSequentialChecks(target),
        "split_and_in_if" => SplitAndInIf(target),
        "join_null_check_with_usage" => JoinNullCheckWithUsage(target),
        "use_var" => UseVar(target),
        "use_explicit_type" => UseExplicitType(target),
        "split_declaration" => SplitDeclaration(target),
        "remove_braces" => RemoveBraces(target),
        "migrate_to_nullable" => MigrateToNullable(target),
        "remove_digit_separators" => RemoveDigitSeparators(target),
        "use_configure_await_false" => UseConfigureAwaitFalse(target),
        _ => Outcome.Fail($"'{id}' has no implementation."),
    };

    /// <summary>The action id in snake_case, so <c>import-type</c> and <c>ImportType</c> reach the same entry.</summary>
    private static string Normalize(string action)
    {
        var builder = new System.Text.StringBuilder(action.Length + 4);
        foreach (var c in action.Trim())
        {
            if (c is '-' or ' ')
            {
                builder.Append('_');
                continue;
            }

            if (char.IsUpper(c) && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    // ── The document ─────────────────────────────────────────────────────────────────────────────

    private async Task<Resolution> ResolveAsync(string filePath, int line, int toLine, CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
            return Resolution.Failed("No C# solution could be auto-located from the working directory. Call load_solution first.");

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return Resolution.Failed($"Invalid path '{filePath}': {ex.Message}"); }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return Resolution.Failed($"{path} is not a document of the loaded solution.");

        var text = await document.GetTextAsync(cancellationToken);
        if (line > text.Lines.Count)
            return Resolution.Failed($"Line {line} is out of range: the file has {text.Lines.Count} line(s).");
        if (toLine < line)
            return Resolution.Failed($"toLine ({toLine}) is before line ({line}).");
        if (toLine > text.Lines.Count)
            return Resolution.Failed($"Line {toLine} is out of range: the file has {text.Lines.Count} line(s).");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);

        if (root is null || model is null || tree is null || compilation is null)
            return Resolution.Failed($"Could not read the syntax or semantic model of {Path.GetFileName(path)}.");

        return new Resolution(new Target(document, root, model, compilation, tree, text, line, toLine), null);
    }

    /// <summary>Null when the projected text compiles as well as the original; the refusal body otherwise.</summary>
    private static Task<object?> CheckCompilesAsync(Target target, string newText, CancellationToken cancellationToken)
    {
        var before = target.Compilation.GetDiagnostics(cancellationToken)
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        var changedTree = target.Tree.WithChangedText(
            SourceText.From(newText, target.Text.Encoding ?? System.Text.Encoding.UTF8));
        var after = target.Compilation.ReplaceSyntaxTree(target.Tree, changedTree)
            .GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();

        var added = after.Count - before;
        if (added <= 0)
            return Task.FromResult<object?>(null);

        return Task.FromResult<object?>(new
        {
            error = $"Refused: the edit would add {added} compiler error(s) ({before} → {after.Count}).",
            errors = after
                .Select(diagnostic => $"{diagnostic.Id} {diagnostic.GetMessage()}")
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList(),
        });
    }

    // ── Shared editing helpers ───────────────────────────────────────────────────────────────────

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    /// <summary>The whitespace that starts the given 1-based line.</summary>
    private static string IndentationAt(SourceText text, int line) =>
        line >= 1 && line <= text.Lines.Count
            ? new string(text.Lines[line - 1].ToString().TakeWhile(char.IsWhiteSpace).ToArray())
            : string.Empty;

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>Cheap syntax check for the fragments the caller supplies (a condition, a resource).</summary>
    private static bool Parses(string expression) =>
        !SyntaxFactory.ParseExpression(expression).ContainsDiagnostics;

    private static string IndentBlock(string body, string indentation, string newLine) =>
        string.Join(newLine, body.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0 ? line : indentation + "    " + line));

    private sealed record Outcome(string Text, IReadOnlyList<string> Details, string? Error = null)
    {
        public static Outcome Fail(string error) => new(string.Empty, [], error);
    }

    private sealed record Target(
        Document Document,
        SyntaxNode Root,
        SemanticModel Model,
        Compilation Compilation,
        SyntaxTree Tree,
        SourceText Text,
        int Line,
        int ToLine);

    private sealed record Resolution(Target? Target, string? Error)
    {
        public static Resolution Failed(string error) => new(null, error);
    }
}
