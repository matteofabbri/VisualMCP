using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.ContextActions;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ContextActionTool
{
    [McpServerTool(Name = "context_action"), Description(
        "The ReSharper context actions that are neither IDE-style rewrites (simplify_code) nor whole refactorings, " +
        "applied to the document that contains `line` and previewed unless dryRun=false. Actions: " +
        "import_type (add the using that brings the type on the line into scope) · " +
        "check_param_null / check_all_params_null / check_string_not_null_or_empty / check_collection_not_empty " +
        "(argument guards at the top of the method body, with `text` naming one parameter where the action takes one) · " +
        "check_variable_null (a guard right after the declaration) · " +
        "check_dictionary_key (a ContainsKey guard before the statement that indexes a dictionary; `text` names the key) · " +
        "check_enum_defined (an Enum.IsDefined guard for an enum parameter; `text` names it) · " +
        "check_index_range (an out-of-range guard for an index on the line) · " +
        "wrap_in_try_catch / wrap_in_try_finally / wrap_in_if / wrap_in_using (lines `line`..`toLine`, whole statements; " +
        "`text` is the condition or the resource for if/using) · " +
        "wrap_in_region (a #region around the selected whole lines) · " +
        "add_braces (wrap a brace-less embedded statement in a block) · " +
        "foreach_to_for / for_to_foreach / invert_loop (loop conversions; invert_loop turns the canonical ascending for into a descending one) · " +
        "if_to_switch (an if/else-if chain over one selector into a switch) · " +
        "concat_to_interpolated / interpolated_to_concat (the two directions of string formatting) · " +
        "digit_separators (group an integer literal with '_') · " +
        "remove_argument_name (a named argument becomes positional; `text` names it) · " +
        "string_to_raw / string_to_verbatim / string_to_regular (re-encoded from the value the compiler sees). " +
        "Every action is refused when the projected edit would add a compiler error, and every write is recorded in the edit history.")]
    public static Task<object> ContextAction(
        IContextActionService svc,
        [Description("Action id, e.g. 'import_type' or 'check_param_null'. An unknown id returns the whole catalogue.")] string action,
        [Description("File to edit (absolute or relative to the solution).")] string filePath,
        [Description("1-based anchor line: the type, the method, the literal, or the first wrapped statement.")] int line,
        [Description("Last line of the wrapped range; defaults to `line`. Only the wrap actions use it.")] int? toLine = null,
        [Description("Action-specific argument: the parameter name, the key expression for check_dictionary_key, the condition for wrap_in_if, the resource expression for wrap_in_using, or the type name for import_type.")] string? text = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.Apply(action, filePath, line, toLine, text, dryRun);
}
