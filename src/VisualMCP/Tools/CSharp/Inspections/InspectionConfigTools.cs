using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Inspections;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class InspectionConfigTools
{
    [McpServerTool(Name = "configure_inspection"), Description(
        "Set a diagnostic's severity where the compiler reads it: dotnet_diagnostic.<id>.severity in an " +
        ".editorconfig. scope=solution (default) writes the solution-root file, scope=project the project " +
        "folder's, scope=file a section for that single file. severity: none (off), silent, suggestion, " +
        "warning, error, default (back to the analyzer's own). Idempotent: the key is replaced, never " +
        "duplicated, and the previous value is reported. This is the .editorconfig authoring the formatter " +
        "tools never had; an id that is not a diagnostic id is refused with examples.")]
    public static Task<object> ConfigureInspection(
        IInspectionConfigurationService inspections,
        [Description("Diagnostic id, e.g. CA1822, RCS1060, IDE0001, VMC0001.")] string diagnosticId,
        [Description("none | silent | suggestion | warning | error | default")] string severity,
        [Description("solution (default) | project | file")] string? scope = null,
        [Description("The file, for scope=file (absolute or relative to the solution).")] string? filePath = null,
        [Description("The project, for scope=project.")] string? projectName = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => inspections.ConfigureAsync(diagnosticId, severity, scope, filePath, projectName, dryRun);

    [McpServerTool(Name = "suppress_finding"), Description(
        "Silence one finding in the place that actually silences it. strategy=pragma writes " +
        "#pragma warning disable <id> before the member that contains `line` and warning restore after it " +
        "(the whole member, not one statement). strategy=attribute puts " +
        "[SuppressMessage(\"<category>\", \"<id>\", Justification = \"…\")] on the member instead — what " +
        "analyzers that read SuppressMessage honour. Without `line` the whole file is guarded. " +
        "Compiler-gated: an edit that would add errors is refused and nothing is written. Previewed unless " +
        "dryRun=false.")]
    public static Task<object> SuppressFinding(
        IInspectionConfigurationService inspections,
        [Description("Diagnostic id to silence, e.g. CA1822.")] string diagnosticId,
        [Description("pragma (default) | attribute")] string? strategy = null,
        [Description("The file to edit.")] string filePath = null!,
        [Description("The line with the finding (1-based). Omit to guard the whole file.")] int? line = null,
        [Description("Why the finding is acceptable; it is written into the comment / Justification.")] string? justification = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => inspections.SuppressAsync(diagnosticId, strategy, filePath, line, justification, dryRun);

    [McpServerTool(Name = "configure_formatting"), Description(
        "Author any other .editorconfig option the product understands — the formatting and style surface " +
        "the formatter and the analyzers read: csharp_* (e.g. csharp_prefer_braces), dotnet_* (e.g. " +
        "dotnet_style_qualification_for_field), indent_size/indent_style, tab_width, end_of_line, charset, " +
        "insert_final_newline, trim_trailing_whitespace, max_line_length, file_header_template. Same scopes " +
        "as configure_inspection (solution/project/file), same upsert: the key is replaced, never duplicated. " +
        "Diagnostic severities stay with configure_inspection.")]
    public static Task<object> ConfigureFormatting(
        IInspectionConfigurationService inspections,
        [Description("The option name, e.g. csharp_prefer_braces or indent_size.")] string option,
        [Description("The value, e.g. true, 4, space.")] string value,
        [Description("solution (default) | project | file")] string? scope = null,
        [Description("The file, for scope=file.")] string? filePath = null,
        [Description("The project, for scope=project.")] string? projectName = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => inspections.ConfigureOptionAsync(option, value, scope, filePath, projectName, dryRun);
}
