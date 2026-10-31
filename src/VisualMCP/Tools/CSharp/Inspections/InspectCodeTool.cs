using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using Snitch.Languages.CSharp.Inspections;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.CSharp.Inspections;

[McpServerToolType]
public static class InspectCodeTool
{
    [McpServerTool(Name = "inspect_code"), Description(
        "Inspect the solution like an IDE: run the built-in Rider-style inspection packs " +
        "over every project and return findings with severity, inspection id, file, line and message. " +
        "Covers redundant code, unused members, simplifications, async pitfalls, CA design/reliability rules, " +
        "correctness and security smells, plus the VisualMCP house rules (pack 'house', VMC0001: one type " +
        "per file). Works on ANY solution without referencing any analyzer package. " +
        "This is the tool to call after implementing or changing code, and before a refactor; it finds what " +
        "the compiler does not. Narrow it with projectName / filePath, or filter with ids (e.g. 'RCS,CA18*') " +
        "and excludeIds (e.g. 'CA1707').")]
    public static async Task<CallToolResult> InspectCode(
        IInspectCodeService inspections,
        [Description("Optional: restrict to a single project by name.")] string? projectName = null,
        [Description("Optional: restrict to a single file (absolute or relative path).")] string? filePath = null,
        [Description("Inspection packs: 'house' (VisualMCP rules, VMC0001 one-type-per-file), 'rider' (ReSharper-shaped " +
                     "inspections under ReSharper's own ids, opt-in), 'roslynator', 'netanalyzers', 'asyncfixer', " +
                     "'meziantou', 'stylecop', a comma-separated list, or 'all'. Default: house,roslynator,netanalyzers,asyncfixer.")]
        string packs = "",
        [Description("Minimum severity: error, warning, info or hidden. Default: info, which is the " +
                     "Rider-like view (suggestions arrive as WEAK_WARNING); pass 'warning' for action items only.")]
        string minSeverity = "info",
        [Description("Only these inspection ids; supports prefixes with '*' (e.g. 'RCS*,CA18*').")] string? ids = null,
        [Description("Skip these inspection ids; supports prefixes with '*' (e.g. 'CA1707,SA*').")] string? excludeIds = null,
        [Description("Also enable rules the packs keep off by default; they are reported as WEAK_WARNING, " +
                     "so pass minSeverity 'info' to see them. Default: true.")]
        bool enableAll = true,
        [Description("Maximum findings to return (default 300, max 5000).")] int maxResults = 300,
        [Description("Timeout in seconds (default 120, max 900).")] int timeoutSeconds = 120)
    {
        var report = await inspections.RunAsync(
            projectName, filePath, packs, minSeverity, ids, excludeIds, enableAll, maxResults, timeoutSeconds);

        var builder = new StringBuilder();
        builder.Append("Code inspections — ").Append(Path.GetFileName(report.Solution)).Append('\n');

        if (report.State == "failed")
        {
            if (report.Error is not null) builder.Append(report.Error).Append('\n');
            return ToolText.Fail(builder.ToString().TrimEnd('\n'));
        }

        if (report.Packs.Count > 0)
            builder.Append("packs: ").Append(string.Join(", ", report.Packs)).Append('\n');

        builder.Append("state: ").Append(report.State)
               .Append(" in ").Append((report.ElapsedMs / 1000d).ToString("0.0")).Append(" s — ")
               .Append(report.Total).Append(" finding(s)").Append('\n');

        if (report.Error is not null) builder.Append(report.Error).Append('\n');

        if (report.BySeverity.Count > 0)
            builder.Append("by severity: ")
                   .Append(string.Join(", ", report.BySeverity.Select(pair => $"{pair.Key} {pair.Value}")))
                   .Append('\n');

        if (report.ByInspection.Count > 0)
            builder.Append("top inspections: ")
                   .Append(string.Join(", ", report.ByInspection.Take(12).Select(pair => $"{pair.Key} {pair.Value}")))
                   .Append('\n');

        builder.Append("showing ").Append(report.Returned).Append(" of ").Append(report.Total)
               .Append(report.Truncated ? " (truncated)" : "").Append('\n');

        foreach (var group in report.Findings.GroupBy(f => f.File))
        {
            builder.Append('\n').Append(group.Key).Append('\n');

            foreach (var finding in group.OrderBy(f => f.Line).ThenBy(f => f.Column))
            {
                builder.Append(finding.Line.ToString().PadLeft(6)).Append(": ")
                       .Append('[').Append(finding.Severity).Append("] ")
                       .Append(finding.InspectionId).Append(" — ")
                       .Append(finding.Message).Append('\n');
            }
        }

        if (report.Warnings.Count > 0)
        {
            builder.Append("\npack warnings:\n");
            foreach (var warning in report.Warnings.Take(8))
                builder.Append("  ").Append(warning).Append('\n');
        }

        return ToolText.Ok(builder.ToString().TrimEnd('\n'));
    }
}
