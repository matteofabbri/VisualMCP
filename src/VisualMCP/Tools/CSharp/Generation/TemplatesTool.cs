using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Generation;

namespace VisualMCP.Tools.CSharp.Generation;

[McpServerToolType]
public static class TemplatesTool
{
    [McpServerTool(Name = "templates"), Description(
        "ReSharper's templates as data. action=list shows the live, postfix and file templates (built-in plus the " +
        "team's own in .visualMCP/templates.json). action=expand applies a live or postfix template at `line` in " +
        "`filePath`: a live template replaces the line when its trimmed text is the shortcut, otherwise the expansion " +
        "is inserted after it; a postfix template needs the line to end with '.name' (order.if, value.null, list.var, " +
        "task.await, items.foreach, orders.ret) and expands the expression before the dot. `values` is " +
        "'key=value;key2=value2' for the $holes$. action=create makes a new file from a file template (class, " +
        "interface, record, struct, enum, static-class, abstract-class, xunit-test) and adds it to the project. " +
        "action=save / delete maintain the custom templates. Every write is compiler-gated, previewed unless " +
        "dryRun=false, and recorded in the edit history.")]
    public static Task<object> Templates(
        ITemplateService svc,
        [Description("list | expand | create | save | delete")] string action,
        [Description("Template name (expand, create, save, delete).")] string? template = null,
        [Description("For expand: the file to edit.")] string? filePath = null,
        [Description("For expand: 1-based line to expand at (default 1).")] int line = 1,
        [Description("For expand/create: $hole$ values as 'key=value;key2=value2'.")] string? values = null,
        [Description("For create: the new type name. For save/delete: the template name.")] string? name = null,
        [Description("For create: the project that receives the file.")] string? projectName = null,
        [Description("For create: the namespace (defaults to the project's root namespace).")] string? namespaceName = null,
        [Description("For create: the folder (defaults to the project directory).")] string? directory = null,
        [Description("For save: live | postfix | file.")] string? kind = null,
        [Description("For save: the template body with $variable$ holes.")] string? body = null,
        [Description("For save: a one-line description.")] string? description = null,
        [Description("Preview only (default true). Pass false to write.")] bool dryRun = true)
    {
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        return verb switch
        {
            "list" => svc.ListAsync(),
            "expand" => string.IsNullOrWhiteSpace(filePath)
                ? Task.FromResult<object>(new { error = "action=expand needs filePath and template." })
                : string.IsNullOrWhiteSpace(template)
                    ? Task.FromResult<object>(new { error = "action=expand needs template." })
                    : svc.ExpandAsync(filePath!, line, template!, values, dryRun),
            "create" => string.IsNullOrWhiteSpace(template)
                ? Task.FromResult<object>(new { error = "action=create needs template and name." })
                : svc.CreateFileAsync(template!, name, projectName, namespaceName, directory, values, dryRun),
            "save" => string.IsNullOrWhiteSpace(name)
                ? Task.FromResult<object>(new { error = "action=save needs a name." })
                : svc.SaveAsync(name!, kind ?? "live", body ?? string.Empty, description, dryRun),
            "delete" => string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(template)
                ? Task.FromResult<object>(new { error = "action=delete needs a name." })
                : svc.DeleteAsync((name ?? template)!, dryRun),
            _ => Task.FromResult<object>(new
            {
                error = $"Unknown action '{action}'.",
                actions = new[] { "list", "expand", "create", "save", "delete" },
            }),
        };
    }
}
