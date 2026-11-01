using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Execution;

namespace VisualMCP.Tools.Execution;

[McpServerToolType]
public static class ProjectTemplateTool
{
    [McpServerTool(Name = "project_template"), Description(
        "Create a PROJECT, not just a file inside one: 'dotnet new' as data. action=list returns the " +
        "templates this SDK has (name, short name, languages, tags); action=create runs one " +
        "(template=<shortName>, name=<project name>, optional output folder, framework, language) and " +
        "reports the command, its exit code, its output and the files it created. Nothing is written on a " +
        "dry run (the default). Add the new project to a solution with add_projects_to_solution.")]
    public static Task<ProjectTemplateResult> ProjectTemplate(
        IProjectTemplateService templates,
        [Description("list (the installed templates) or create (run one).")] string action,
        [Description("For create: the template's short name, e.g. classlib, console, web, xunit. Omit for list.")]
        string? template = null,
        [Description("For create: the project name (also the output folder's name by default).")]
        string? name = null,
        [Description("For create: the folder to create the project in (default: ./<name>).")]
        string? output = null,
        [Description("For create: the target framework the template accepts, e.g. net10.0.")]
        string? framework = null,
        [Description("For create: the language the template accepts, e.g. C#, F#, VB.")]
        string? language = null,
        [Description("Preview only (default true): the command is returned and nothing is created.")]
        bool dryRun = true,
        [Description("Timeout for the dotnet command in seconds (default 300).")] int timeoutSeconds = 300)
        => templates.RunAsync(action, template, name, output, framework, language, dryRun, timeoutSeconds);
}
