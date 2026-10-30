using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.Razor;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class RazorEditTool
{
    [McpServerTool(Name = "razor_edit"), Description(
        "Edit a Razor page (.cshtml) or Blazor component (.razor) — the write side of razor_pages / " +
        "analyze_razor_pages. Six operations, each one a single verified text splice:\n" +
        "• set-attribute — set tag + attribute (+ value) on the nth element with that tag; adds it when absent, " +
        "replaces its value when present. This is how you add rel=\"noopener noreferrer\", alt text, a missing " +
        "type=\"button\", asp-for, an aria-* attribute.\n" +
        "• remove-attribute — remove tag + attribute, whitespace included.\n" +
        "• add-directive — insert a directive (model / using / inject / layout / inherits / addTagHelper) after the " +
        "last directive the page already declares; a duplicate is a no-op, not an error.\n" +
        "• insert-lines / replace-lines / delete-lines — by 1-based line range, for edits that are not an attribute.\n" +
        "SAFETY: pass expect=<the text those lines hold now> (or the attribute's current value) and the edit is " +
        "refused unless the file still matches — that is what stops an edit from landing on a changed file. Every " +
        "edit is re-scanned before writing: if it would raise the unbalanced-syntax count the file is left " +
        "untouched and the new issues are returned. The result reports the diff and the rule findings the edit " +
        "resolved and introduced (e.g. adding rel fixes RZ1007), and the write is recorded in the solution's edit " +
        "history, so revert_edit undoes it. dryRun defaults to true.")]
    public static Task<object> RazorEdit(
        IRazorEditService svc,
        [Description("The .cshtml/.razor file (absolute, or relative to the solution/working directory).")] string path,
        [Description("set-attribute | remove-attribute | add-directive | insert-lines | replace-lines | delete-lines.")] string operation,
        [Description("Element name for the attribute operations, e.g. 'a', 'img', 'form'.")] string? tag = null,
        [Description("Which occurrence of tag, 1-based in document order (default 1).")] int nth = 1,
        [Description("The attribute to set or remove, e.g. 'rel'.")] string? attribute = null,
        [Description("New attribute value, written verbatim (a Razor expression is allowed). Omit for a valueless attribute.")] string? value = null,
        [Description("Text to insert or substitute, for the line operations.")] string? text = null,
        [Description("The directive body for add-directive, without the leading @, e.g. 'model MyViewModel' or 'using MyApp.Data'.")] string? directive = null,
        [Description("First line (1-based) of insert-lines/replace-lines/delete-lines; also the anchor line for insert-lines.")] int fromLine = 0,
        [Description("Last line (1-based, inclusive); 0 means fromLine.")] int toLine = 0,
        [Description("The text those lines (or that attribute value) must currently hold; the edit is refused if it does not match.")] string? expect = null,
        [Description("For insert-lines: 'after' (default), 'before', 'start' or 'end'.")] string position = "after",
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
    {
        var request = new RazorEditRequest
        {
            Path = path,
            Operation = operation,
            Tag = tag,
            Nth = nth,
            AttributeName = attribute,
            AttributeValue = value,
            Text = text,
            FromLine = fromLine,
            ToLine = toLine,
            Expect = expect,
            Directive = directive,
            Position = position,
            DryRun = dryRun,
        };

        return svc.EditAsync(request);
    }
}
