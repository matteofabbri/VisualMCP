using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Inspections;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class InspectionAdministrationTools
{
    [McpServerTool(Name = "custom_inspections"), Description(
        "Inspections declared in the repository instead of compiled into an analyzer: banned symbols and " +
        "namespaces, banned attributes, identifier patterns that must not appear, and patterns in string " +
        "literals and comments. Rules live in .visualMCP/inspections.json (or a path you pass) as " +
        "{ id (VMC…), kind, value, severity, message }; value is a substring or /regex/. " +
        "action=validate checks the file without analysing; action=run (default) evaluates it over the " +
        "solution and returns findings with rule id, severity, file:line and snippet.")]
    public static Task<object> CustomInspections(
        ICustomInspectionService service,
        [Description("'run' (default) or 'validate'.")] string action = "run",
        [Description("Optional: restrict to one project by name.")] string? projectName = null,
        [Description("Optional: restrict to one file (absolute or relative).")] string? filePath = null,
        [Description("Optional: the rule file. Default: <solution>/.visualMCP/inspections.json.")] string? path = null,
        [Description("Minimum severity to return: error, warning or info (default info).")] string minSeverity = "info",
        [Description("Maximum findings (default 200, max 5000).")] int maxResults = 200)
        => action.Trim().ToLowerInvariant() == "validate"
            ? service.ValidateAsync(path)
            : service.RunAsync(projectName, filePath, path, minSeverity, maxResults);

    [McpServerTool(Name = "read_dot_settings"), Description(
        "Read the ReSharper .DotSettings layers a team shares (.DotSettings and .DotSettings.user next to " +
        "the solution): inspection severities, disabled inspections and the custom spelling dictionary. The " +
        "severities are ReSharper inspection ids, reported rather than applied to Roslyn diagnostics " +
        "(configure_inspection writes those); the dictionary words feed find_spelling_issues. Pass a file or " +
        "a folder, or omit the path to scan the solution folder.")]
    public static Task<object> ReadDotSettings(
        IDotSettingsService service,
        [Description("Optional: a .DotSettings file or a folder to scan. Default: the solution folder.")] string? path = null)
        => service.ReadAsync(path);

    [McpServerTool(Name = "find_spelling_issues"), Description(
        "Spelling inspection without a bundled dictionary: flags words one edit away from a word the " +
        "solution or the repository dictionary already uses (Calcualte next to Calculate, recieve next to " +
        "receive), with occurrences and locations. Words come from identifiers and comments; the dictionary " +
        "is .visualMCP/spelling.dic (one word per line) plus the .DotSettings custom words. Add a word to the " +
        "dictionary to silence it.")]
    public static Task<object> FindSpellingIssues(
        ISpellingService service,
        [Description("Optional: restrict to one project by name.")] string? projectName = null,
        [Description("Optional: restrict to one file (absolute or relative).")] string? filePath = null,
        [Description("Optional: an explicit dictionary file (one word per line, # comments).")] string? dictionaryPath = null,
        [Description("Maximum candidates (default 100, max 1000).")] int maxResults = 100)
        => service.FindAsync(projectName, filePath, dictionaryPath, maxResults);

    [McpServerTool(Name = "naming_rules"), Description(
        "Apply the repository's dotnet_naming_rule settings from .editorconfig: it names every violating " +
        "type or member the way the configured style asks and, with apply=true, renames it through " +
        "apply_rename (each write recorded and revertible). Without apply it lists the violations with " +
        "current name, expected name, rule, file:line. Locals and parameters are skipped (a rename by name " +
        "cannot tell two same-named locals apart).")]
    public static Task<object> NamingRules(
        INamingRulesService service,
        [Description("Optional: restrict to one project by name.")] string? projectName = null,
        [Description("Rename the violations instead of listing them (default false).")] bool apply = false,
        [Description("Maximum violations (default 200, max 5000).")] int maxResults = 200)
        => service.RunAsync(projectName, apply, maxResults);

    [McpServerTool(Name = "background_analysis"), Description(
        "The warm inspection loop: action=start runs the inspection packs whenever the loaded solution " +
        "changes (and once now), caching the findings; stop ends the loop, refresh runs one warm pass, " +
        "status reports the loop and the last run. While it runs, analysis_findings answers from the cache " +
        "instead of paying for a full inspection, which is what a client polls between calls. inspect_code " +
        "stays the one-shot, filterable inspection.")]
    public static Task<object> BackgroundAnalysis(
        IBackgroundAnalysisService service,
        [Description("'start' (default), 'stop', 'status' or 'refresh'.")] string action = "start",
        [Description("Seconds between change checks while running (default 60, min 5).")] int intervalSeconds = 60)
    {
        var verb = (action ?? "start").Trim().ToLowerInvariant();
        return verb switch
        {
            "stop" => service.StopAsync(),
            "status" => Task.FromResult(service.Status()),
            "refresh" => service.RefreshAsync(),
            _ => service.StartAsync(intervalSeconds),
        };
    }

    [McpServerTool(Name = "analysis_findings"), Description(
        "The findings of the last background analysis run, from the warm cache: by severity, top inspection " +
        "ids and the findings themselves, optionally narrowed by file, project, severity or id. Read-only, " +
        "cheap enough to poll between edits; call background_analysis action=refresh when you need it fresh.")]
    public static object AnalysisFindings(
        IBackgroundAnalysisService service,
        [Description("Optional: only this file (absolute or relative).")] string? filePath = null,
        [Description("Optional: only this project.")] string? projectName = null,
        [Description("Minimum severity: error, warning or info (default info).")] string minSeverity = "info",
        [Description("Maximum findings (default 100, max 5000).")] int maxResults = 100)
        => service.Findings(filePath, projectName, minSeverity, maxResults);
}
