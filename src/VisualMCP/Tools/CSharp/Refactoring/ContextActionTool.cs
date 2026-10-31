using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.ContextActions;
using Snitch.Languages.VB.ContextActions;
using VisualMCP.Tools;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ContextActionTool
{
    [McpServerTool(Name = "context_action"), Description(
        "The ReSharper context actions that are neither IDE-style rewrites (simplify_code) nor whole refactorings, " +
        "applied to the document that contains `line` and previewed unless dryRun=false. Actions: " +
        "import_type (add the using/Imports that brings the type on the line into scope) · " +
        "check_param_null / check_all_params_null / check_string_not_null_or_empty / check_collection_not_empty " +
        "(argument guards at the top of the method body, with `text` naming one parameter where the action takes one) · " +
        "check_variable_null (a guard right after the declaration) · " +
        "check_dictionary_key (a ContainsKey guard before the statement that indexes a dictionary; `text` names the key) · " +
        "check_enum_defined (an Enum.IsDefined guard for an enum parameter; `text` names it) · " +
        "check_index_range (an out-of-range guard for an index on the line) · " +
        "wrap_in_try_catch / wrap_in_try_finally / wrap_in_if / wrap_in_using (lines `line`..`toLine`, whole statements; " +
        "`text` is the condition or the resource for if/using) · " +
        "wrap_in_region (a #region/#Region around the selected whole lines) · " +
        "add_braces (wrap a brace-less embedded statement in a block) · " +
        "foreach_to_for / for_to_foreach / invert_loop (loop conversions; invert_loop turns the canonical ascending for into a descending one) · " +
        "if_to_switch (an if/else-if chain over one selector into a switch/Select Case) · " +
        "concat_to_interpolated / interpolated_to_concat (the two directions of string formatting) · " +
        "digit_separators (group an integer literal with '_') · remove_digit_separators · " +
        "remove_argument_name (a named argument becomes positional; `text` names it) · " +
        "merge_sequential_checks / split_and_in_if (the two directions of an 'if (a) { if (b) }' chain) · " +
        "join_null_check_with_usage (if (x != null) { x.M(); } → x?.M();) · " +
        "use_var / use_explicit_type (a local declaration, one way or the other) · split_declaration · " +
        "remove_braces (a one-statement block, refused when it would rebind an else or a declaration) · " +
        "migrate_to_nullable (#nullable enable at the top, or a disable turned into it) · " +
        "use_configure_await_false (append .ConfigureAwait(false) to the awaited expression) · " +
        "merge_patterns ('x is A || x is B' → 'x is A or B', and the '&&'/'and' form) · " +
        "concat_to_stringbuilder (a string local built with '+=' becomes a StringBuilder, appends and reads rewritten with it) · " +
        "linq_query_to_method / linq_method_to_query (the two directions of LINQ syntax, over from/where/orderby/select) · " +
        "convert_to_extension_block (a static class of 'this' extension methods becomes a C# 14 `extension(T receiver)` block) · " +
        "string_to_raw / string_to_verbatim / string_to_regular (re-encoded from the value the compiler sees). " +
        "A .vb file routes to the Visual Basic engine, which implements the ids that have a VB spelling (guards, Try/If/Using " +
        "wrapping, Imports, interpolation, digit separators, named arguments, use var/explicit type, split declaration, " +
        "StringBuilder, merge/split checks, conditional access, Select Case, For Each↔For, invert loop) and refuses the " +
        "C#-only ones (raw/verbatim strings, braces, #nullable, patterns, the C# 14 extension block) with the reason. " +
        "Every action is refused when the projected edit would add a compiler error, and every write is recorded in the edit history.")]
    public static Task<object> ContextAction(
        IContextActionService csharp,
        IServiceProvider services,
        [Description("Action id, e.g. 'import_type' or 'check_param_null'. An unknown id returns the whole catalogue.")] string action,
        [Description("File to edit (absolute or relative to the solution). A .vb file uses the Visual Basic engine.")] string filePath,
        [Description("1-based anchor line: the type, the method, the literal, or the first wrapped statement.")] int line,
        [Description("Last line of the wrapped range; defaults to `line`. Only the wrap actions use it.")] int? toLine = null,
        [Description("Action-specific argument: the parameter name, the key expression for check_dictionary_key, the condition for wrap_in_if, the resource expression for wrap_in_using, or the type name for import_type.")] string? text = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
    {
        if (!filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            return csharp.Apply(action, filePath, line, toLine, text, dryRun);

        var visualBasic = ToolServices.Find<IVbContextActionService>(services);
        return visualBasic is null
            ? Task.FromResult<object>(ToolServices.Missing<IVbContextActionService>(
                "The Visual Basic context-action engine", "services.AddSnitchVisualBasic()"))
            : visualBasic.Apply(action, filePath, line, toLine, text, dryRun);
    }
}
