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
        ("merge_patterns", "Merge pattern checks into one 'or'/'and' pattern"),
        ("concat_to_stringbuilder", "Convert string concatenation to StringBuilder"),
        ("linq_query_to_method", "Convert LINQ query syntax to a method chain"),
        ("linq_method_to_query", "Convert a LINQ method chain to query syntax"),
        ("convert_to_extension_block", "Convert extension methods into a C# 14 extension block"),
        ("empty_string_to_string_empty", "Convert '' to 'string.Empty'"),
        ("string_empty_to_empty_string", "Convert 'string.Empty' to ''"),
        ("auto_property_to_explicit", "To explicit property over a backing field"),
        ("insert_file_header", "Insert the .editorconfig file header template"),
        ("import_static_member", "Import a static type (using static)"),
        ("qualify_member", "Qualify a static member with its type"),
        ("unqualify_member", "Unqualify a static member and import it"),
        ("wrap_in_quotes", "Wrap the selected lines in a string literal"),
        ("split_string", "Split a string literal into a concatenation"),
        ("join_string", "Join a concatenation of literals into one"),
        ("check_span_index", "Check an index against a span's length"),
        ("block_body_to_expression", "Convert a body to an expression body"),
        ("expression_body_to_block", "Convert an expression body to a block"),
        ("check_string_not_null_or_whitespace", "Check string parameter for null or whitespace"),
        ("add_argument_name", "Add argument name"),
        ("add_tuple_component_name", "Add tuple component name"),
        ("remove_tuple_component_name", "Remove tuple component name"),
        ("add_switch_section_braces", "Add braces to a switch section"),
        ("convert_literal_to_hex", "Convert a numeric literal to hexadecimal"),
        ("convert_literal_to_binary", "Convert a numeric literal to binary"),
        ("convert_literal_to_decimal", "Convert a numeric literal to decimal"),
        ("use_target_typed_new", "Use target-typed new"),
        ("use_explicitly_typed_new", "Use explicitly typed new"),
        ("convert_to_default_expression", "Convert default(T) to default"),
        ("flip_relational_expression", "Flip the operands of a relational expression"),
        ("invert_ternary_condition", "Invert a ternary condition"),
        ("if_to_ternary", "Convert if/else to a ternary expression"),
        ("add_inheritdoc", "Add <inheritdoc/>"),
        ("add_exception_docs", "Add <exception> documentation"),
        ("remove_region", "Remove the region around the line"),
        ("use_environment_newline", "Use Environment.NewLine"),
        ("remove_params_modifier", "Remove the params modifier"),
        ("set_params_modifier", "Set the params modifier"),
        ("await_foreach", "Convert foreach to await foreach"),
    ];

    /// <summary>
    /// ReSharper's own names that are not the id we spell: after the trailing Action wording is
    /// normalized away, <c>AddBracesAction</c> already reaches <c>add_braces</c>, and this table covers the
    /// ones whose ReSharper name differs from the id that implements the same edit.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["string_parameter_is_not_null_or_empty_check"] = "check_string_not_null_or_empty",
        ["all_string_parameter_is_not_null_or_empty_check"] = "check_string_not_null_or_empty",
        ["string_parameter_is_not_null_or_whitespace_check"] = "check_string_not_null_or_whitespace",
        ["all_string_parameter_is_not_null_or_whitespace_check"] = "check_string_not_null_or_whitespace",
        ["check_dictionary_key_value"] = "check_dictionary_key",
        ["check_array_index_value"] = "check_index_range",
        ["check_span_index_value"] = "check_span_index",
        ["collection_is_not_empty_check"] = "check_collection_not_empty",
        ["enum_parameter_is_defined_check"] = "check_enum_defined",
        ["import_static_members"] = "import_static_member",
        ["qualify_static_members"] = "qualify_member",
        ["surround_with_quotes"] = "wrap_in_quotes",
        ["join_strings"] = "join_string",
        ["convert_concatenation_to_interpolation"] = "concat_to_interpolated",
        ["interpolation_to_string_concat"] = "interpolated_to_concat",
        ["type_to_var"] = "use_var",
        ["var_to_type"] = "use_explicit_type",
        ["body_expression_to_statement"] = "expression_body_to_block",
        ["body_statement_to_expression"] = "block_body_to_expression",
        ["c_sharp_use_empty_string_literal"] = "empty_string_to_string_empty",
        ["c_sharp_use_string_empty"] = "string_empty_to_empty_string",
        ["convert_regular_string_to_verbatim"] = "string_to_verbatim",
        ["convert_regular_to_raw_string"] = "string_to_raw",
        ["convert_verbatim_string_to_regular"] = "string_to_regular",
        ["convert_verbatim_to_raw_string"] = "string_to_raw",
        ["migrate_to_nullable_enable"] = "migrate_to_nullable",
        ["separate_integral_literal_digits"] = "digit_separators",
        ["separate_real_literal_digits"] = "digit_separators",
        ["convert_to_using_statement"] = "wrap_in_using",
        ["convert_to_try_finally"] = "wrap_in_try_finally",
        ["merge_into_pattern"] = "merge_patterns",
        ["convert_literal_to_bin"] = "convert_literal_to_binary",
        ["convert_literal_to_dec"] = "convert_literal_to_decimal",
        ["add_exception_doc_comment"] = "add_exception_docs",
        ["use_environment_new_line"] = "use_environment_newline",
        ["iterate_via_await_foreach"] = "await_foreach",
    };

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

        var normalized = Normalize(action);
        var id = Aliases.TryGetValue(normalized, out var alias) ? alias : normalized;
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
        "merge_patterns" => MergePatterns(target),
        "concat_to_stringbuilder" => ConcatToStringBuilder(target),
        "linq_query_to_method" => LinqQueryToMethod(target),
        "linq_method_to_query" => LinqMethodToQuery(target),
        "convert_to_extension_block" => ConvertToExtensionBlock(target),
        "empty_string_to_string_empty" => EmptyStringToStringEmpty(target),
        "string_empty_to_empty_string" => StringEmptyToLiteral(target),
        "auto_property_to_explicit" => AutoPropertyToExplicit(target),
        "insert_file_header" => InsertFileHeader(target),
        "import_static_member" => ImportStaticMember(target, text),
        "qualify_member" => QualifyMember(target),
        "unqualify_member" => UnqualifyMember(target),
        "wrap_in_quotes" => WrapInQuotes(target),
        "split_string" => SplitString(target, text),
        "join_string" => JoinString(target),
        "check_span_index" => CheckSpanIndex(target, text),
        "block_body_to_expression" => BlockBodyToExpression(target),
        "expression_body_to_block" => ExpressionBodyToBlock(target),
        "check_string_not_null_or_whitespace" => ArgumentGuards(target, text, Guard.NotNullOrWhitespace, all: false),
        "add_argument_name" => AddArgumentName(target, text),
        "add_tuple_component_name" => AddTupleComponentName(target, text),
        "remove_tuple_component_name" => RemoveTupleComponentName(target),
        "add_switch_section_braces" => AddSwitchSectionBraces(target),
        "convert_literal_to_hex" => ConvertLiteralToHex(target),
        "convert_literal_to_binary" => ConvertLiteralToBinary(target),
        "convert_literal_to_decimal" => ConvertLiteralToDecimal(target),
        "use_target_typed_new" => UseTargetTypedNew(target),
        "use_explicitly_typed_new" => UseExplicitlyTypedNew(target),
        "convert_to_default_expression" => ConvertToDefaultExpression(target),
        "flip_relational_expression" => FlipRelationalExpression(target),
        "invert_ternary_condition" => InvertTernaryCondition(target),
        "if_to_ternary" => IfToTernary(target),
        "add_inheritdoc" => AddInheritdoc(target),
        "add_exception_docs" => AddExceptionDocs(target),
        "remove_region" => RemoveRegion(target),
        "use_environment_newline" => UseEnvironmentNewLine(target),
        "remove_params_modifier" => RemoveParamsModifier(target),
        "set_params_modifier" => SetParamsModifier(target),
        "await_foreach" => AwaitForeach(target),
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

        var normalized = builder.ToString();
        if (normalized.EndsWith("_context_action", StringComparison.Ordinal))
            normalized = normalized[..^"_context_action".Length];
        else if (normalized.EndsWith("_check_action", StringComparison.Ordinal))
            normalized = normalized[..^"_check_action".Length];
        else if (normalized.EndsWith("_action", StringComparison.Ordinal))
            normalized = normalized[..^"_action".Length];

        return normalized;
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
