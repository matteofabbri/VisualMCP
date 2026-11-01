using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Workspace;

namespace Snitch.Languages.VB.ContextActions;

/// <summary>
/// The context actions the C# engine offers, read from Visual Basic syntax. The contract is the same as
/// the C# service: every action resolves its target from a 1-based line, previews by default, and is
/// refused when the projected edit would add a compiler error.
/// </summary>
public sealed partial class VbContextActionService : IVbContextActionService
{
    private readonly IRoslynWorkspace _workspace;

    public VbContextActionService(IRoslynWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Every action this engine implements, with the ReSharper action it mirrors. An id that is in the
    /// C# catalogue but not here is answered from <see cref="NotApplicable"/> with a written reason.
    /// </summary>
    public static IReadOnlyList<(string Id, string Mirrors)> Catalogue { get; } =
    [
        ("import_type", "Import type / Add Imports"),
        ("check_param_null", "Check parameter for Nothing"),
        ("check_all_params_null", "Check all parameters for Nothing"),
        ("check_string_not_null_or_empty", "Check string parameter for null or empty"),
        ("check_collection_not_empty", "Check collection parameter for null or empty"),
        ("check_variable_null", "Check variable for Nothing"),
        ("check_dictionary_key", "Check dictionary key"),
        ("check_enum_defined", "Check enum value is defined"),
        ("check_index_range", "Check index for out of range"),
        ("wrap_in_try_catch", "Surround with Try/Catch"),
        ("wrap_in_try_finally", "Surround with Try/Finally"),
        ("wrap_in_if", "Surround with If"),
        ("wrap_in_using", "Surround with Using"),
        ("wrap_in_region", "Surround with #Region"),
        ("concat_to_interpolated", "Convert concatenation to an interpolated string"),
        ("interpolated_to_concat", "Convert an interpolated string to concatenation"),
        ("digit_separators", "Add digit separators to a numeric literal"),
        ("remove_digit_separators", "Remove digit separators"),
        ("remove_argument_name", "Remove argument name"),
        ("use_var", "Remove the explicit type of a local declared with an initializer"),
        ("use_explicit_type", "Write the type the compiler inferred"),
        ("use_configure_await_false", "Use ConfigureAwait(False)"),
        ("split_declaration", "Split a multi-name declaration"),
        ("concat_to_stringbuilder", "Convert string appends to a StringBuilder"),
        ("merge_sequential_checks", "Merge sequential Ifs"),
        ("split_and_in_if", "Split an AndAlso condition into nested Ifs"),
        ("join_null_check_with_usage", "Join a Nothing check with its use"),
        ("if_to_switch", "Convert an If/ElseIf chain to Select Case"),
        ("linq_query_to_method", "Convert LINQ query syntax to a method chain"),
        ("linq_method_to_query", "Convert a LINQ method chain to query syntax"),
        ("foreach_to_for", "Convert For Each to an indexed For"),
        ("for_to_foreach", "Convert an indexed For to For Each"),
        ("invert_loop", "Invert a For loop"),
    ];

    /// <summary>
    /// The C# action ids a Visual Basic document cannot run, with the reason. Most have no VB spelling at
    /// all; a few are rewrites the VB engine has not been taught yet, and the reason says which is which,
    /// so the answer never claims a language limit that does not exist.
    /// </summary>
    public static IReadOnlyList<(string Id, string Reason)> NotApplicable { get; } =
    [
        ("string_to_raw", "Visual Basic has no raw string literal: a multi-line literal is already the nearest form."),
        ("string_to_verbatim", "Visual Basic has no verbatim string literal: quotes are always doubled and there is no @ prefix."),
        ("string_to_regular", "Visual Basic has no verbatim string literal to convert back from."),
        ("add_braces", "Visual Basic is block-terminated (Then/End If): there are no braces to add."),
        ("remove_braces", "Visual Basic is block-terminated (Then/End If): there are no braces to remove."),
        ("migrate_to_nullable", "Visual Basic has no nullable-reference-type directives: #nullable is C# syntax."),
        ("merge_patterns", "Visual Basic has no pattern combinators: 'is A or B' does not exist in this language."),
        ("convert_to_extension_block", "The C# 14 extension block has no Visual Basic counterpart; write the extension method in a Module."),
        ("empty_string_to_string_empty", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine has one (String.Empty exists in VB too)."),
        ("string_empty_to_empty_string", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine has one (\"\" exists in VB too)."),
        ("auto_property_to_explicit", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine has one (an auto-Property can be expanded in VB too)."),
        ("insert_file_header", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine inserts the .editorconfig file_header_template."),
        ("block_body_to_expression", "Visual Basic has no expression-bodied members: a Sub or Function always has a block body."),
        ("expression_body_to_block", "Visual Basic has no expression-bodied members to convert from."),
        ("import_static_member", "Not a language limit: Visual Basic imports a type with 'Imports System.Math'; the VB engine has no rewrite for this id yet."),
        ("qualify_member", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine qualifies a static member with its type."),
        ("unqualify_member", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine unqualifies and adds 'using static'."),
        ("wrap_in_quotes", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine wraps the selected lines in a literal."),
        ("split_string", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine splits a literal at a separator."),
        ("join_string", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine joins a '+' concatenation."),
        ("check_span_index", "Not a language limit: the VB engine has no rewrite for this id yet. The C# engine inserts a bounds guard."),
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
            return DescribeCatalogue("An action is required.");
        if (string.IsNullOrWhiteSpace(filePath))
            return new { error = "A file path is required." };
        if (line < 1)
            return new { error = "Line must be 1-based." };

        var id = Normalize(action);
        if (!Catalogue.Any(entry => entry.Id == id))
        {
            if (NotApplicable.FirstOrDefault(entry => entry.Id == id) is { Id: not null } refused)
            {
                return new
                {
                    error = refused.Reason.StartsWith("Not a language limit", StringComparison.Ordinal)
                        ? $"'{id}' cannot run on a Visual Basic document yet: {refused.Reason}"
                        : $"'{id}' does not apply to Visual Basic: {refused.Reason}",
                    language = "vb",
                    notApplicable = NotApplicable.Select(entry => new { id = entry.Id, reason = entry.Reason }).ToList(),
                };
            }

            return DescribeCatalogue($"Unknown context action '{action}'.");
        }

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
            language = "vb",
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

    private static object DescribeCatalogue(string error) => new
    {
        error,
        language = "vb",
        catalogue = Catalogue.Select(entry => new { id = entry.Id, mirrors = entry.Mirrors }).ToList(),
        notApplicable = NotApplicable.Select(entry => new { id = entry.Id, reason = entry.Reason }).ToList(),
    };

    private Outcome Dispatch(string id, Target target, string? text) => id switch
    {
        "import_type" => ImportType(target, text),
        "check_param_null" => ArgumentGuards(target, text, Guard.Null, all: false),
        "check_all_params_null" => ArgumentGuards(target, null, Guard.Null, all: true),
        "check_string_not_null_or_empty" => ArgumentGuards(target, text, Guard.NotNullOrEmpty, all: false),
        "check_collection_not_empty" => ArgumentGuards(target, text, Guard.CollectionNotEmpty, all: false),
        "check_variable_null" => CheckVariableNull(target, text),
        "check_dictionary_key" => CheckDictionaryKey(target, text),
        "check_enum_defined" => CheckEnumDefined(target, text),
        "check_index_range" => CheckIndexRange(target, text),
        "wrap_in_try_catch" => Wrap(target, null, WrapKind.TryCatch),
        "wrap_in_try_finally" => Wrap(target, null, WrapKind.TryFinally),
        "wrap_in_if" => string.IsNullOrWhiteSpace(text)
            ? Outcome.Fail("wrap_in_if needs the condition in text, e.g. 'value > 0'.")
            : Wrap(target, text.Trim(), WrapKind.If),
        "wrap_in_using" => string.IsNullOrWhiteSpace(text)
            ? Outcome.Fail("wrap_in_using needs the resource expression in text, e.g. 'stream'.")
            : Wrap(target, text.Trim(), WrapKind.Using),
        "wrap_in_region" => WrapInRegion(target, text),
        "concat_to_interpolated" => ConcatToInterpolated(target),
        "interpolated_to_concat" => InterpolatedToConcat(target),
        "digit_separators" => DigitSeparators(target),
        "remove_digit_separators" => RemoveDigitSeparators(target),
        "remove_argument_name" => RemoveArgumentName(target, text),
        "use_var" => UseVar(target),
        "use_explicit_type" => UseExplicitType(target),
        "use_configure_await_false" => UseConfigureAwaitFalse(target),
        "split_declaration" => SplitDeclaration(target),
        "concat_to_stringbuilder" => ConcatToStringBuilder(target),
        "merge_sequential_checks" => MergeSequentialChecks(target),
        "split_and_in_if" => SplitAndInIf(target),
        "join_null_check_with_usage" => JoinNullCheckWithUsage(target),
        "if_to_switch" => IfToSwitch(target),
        "linq_query_to_method" => LinqQueryToMethod(target),
        "linq_method_to_query" => LinqMethodToQuery(target),
        "foreach_to_for" => ForeachToFor(target),
        "for_to_foreach" => ForToForeach(target),
        "invert_loop" => InvertLoop(target),
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
            return Resolution.Failed("No solution could be auto-located from the working directory. Call load_solution first.");

        string path;
        try { path = Path.GetFullPath(filePath); }
        catch (Exception ex) { return Resolution.Failed($"Invalid path '{filePath}': {ex.Message}"); }

        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return Resolution.Failed($"{path} is not a document of the loaded solution.");
        if (document.Project.Language != LanguageNames.VisualBasic)
            return Resolution.Failed($"{Path.GetFileName(path)} is not a Visual Basic document (the VB engine reads .vb files).");

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

    private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

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

    /// <summary>Moves each line of a block from one indentation prefix to another, keeping the rest.</summary>
    private static string Reindent(string body, string from, string to, string newLine) =>
        string.Join(newLine, body.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0 ? line : line.StartsWith(from, StringComparison.Ordinal) ? to + line[from.Length..] : line));

    private static string ReplaceSpan(Target target, TextSpan span, string replacement) =>
        target.Text.ToString().Remove(span.Start, span.Length).Insert(span.Start, replacement);

    private static string ReplaceNode(Target target, SyntaxNode node, string replacement) =>
        ReplaceSpan(target, node.Span, replacement);

    /// <summary>The statements of the block whose own statement list covers the requested lines.</summary>
    private static (SyntaxNode Owner, List<StatementSyntax> Selected)? SelectStatements(Target target)
    {
        var candidates = target.Root.DescendantNodes()
            .Select(node => (Node: node, Statements: Snitch.Languages.VB.Style.VbBlocks.StatementsOf(node)))
            .Where(candidate => candidate.Statements.Count > 0)
            .Select(candidate => (candidate.Node, Statements: candidate.Statements.Cast<StatementSyntax>().ToList()))
            .Where(candidate => LineOf(candidate.Statements[0]) <= target.Line &&
                                EndLine(candidate.Statements[^1]) >= target.ToLine)
            .OrderBy(candidate => candidate.Node.Span.Length)
            .ToList();

        foreach (var (owner, statements) in candidates)
        {
            var selected = statements
                .Where(statement => LineOf(statement) >= target.Line && EndLine(statement) <= target.ToLine)
                .ToList();

            if (selected.Count == 0) return (owner, []);

            var first = statements.IndexOf(selected[0]);
            var contiguous = true;
            for (var index = 1; index < selected.Count; index++)
            {
                if (statements.IndexOf(selected[index]) != first + index)
                {
                    contiguous = false;
                    break;
                }
            }

            return (owner, contiguous ? selected : []);
        }

        return null;
    }

    /// <summary>The method, constructor or accessor whose body contains the anchor line.</summary>
    private static MethodBlockBaseSyntax? EnclosingMethod(Target target) =>
        target.Root.DescendantNodes().OfType<MethodBlockBaseSyntax>()
            .Where(block => LineOf(block) <= target.Line && EndLine(block) >= target.Line)
            .OrderBy(block => block.Span.Length)
            .FirstOrDefault();

    /// <summary>Inserts one or more lines (already indented as a body) after the method's header line.</summary>
    private static string InsertAfterHeader(Target target, MethodBlockBaseSyntax block, string lines)
    {
        var text = target.Text.ToString();
        var newLine = NewLineOf(text);
        var indent = block.Statements.Count > 0
            ? IndentationAt(target.Text, LineOf(block.Statements[0]))
            : IndentationAt(target.Text, LineOf(block.BlockStatement)) + "    ";

        var body = string.Join(newLine, lines.Replace("\r\n", "\n").Split('\n').Select(line => indent + line));

        var lineEnd = text.IndexOf('\n', block.BlockStatement.Span.End);
        if (lineEnd < 0)
            return text + newLine + body + newLine;

        return text.Insert(lineEnd + 1, body + newLine);
    }

    private static bool IsReference(ITypeSymbol? type) => type is { IsReferenceType: true };

    /// <summary>The property a collection is counted with: <c>Length</c> for an array, <c>Count</c> otherwise.</summary>
    private static string? CountMember(ITypeSymbol? type) => type switch
    {
        null => null,
        IArrayTypeSymbol => "Length",
        _ => type.GetMembers("Count").OfType<IPropertySymbol>().Any() ? "Count" : null,
    };

    /// <summary>Whether writing <paramref name="expression"/> needs parentheses before a member access.</summary>
    private static bool NeedsParentheses(ExpressionSyntax expression) =>
        expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax
            or LiteralExpressionSyntax or ParenthesizedExpressionSyntax or MeExpressionSyntax or MyBaseExpressionSyntax);

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
