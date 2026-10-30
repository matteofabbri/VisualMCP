using System.Text.RegularExpressions;

namespace Snitch.Languages.CSharp.Modernization;

/// <summary>
/// Recognises Aspire in the three places it manifests: the installed templates (the <c>dotnet new</c>
/// list), the SDK workload, and project files. Kept free of process calls so the recognition rules can
/// be tested against captured output instead of a machine's actual installation.
/// </summary>
public static class AspireProbe
{
    /// <summary>True when the Aspire project templates are installed (<c>dotnet new list aspire</c>).</summary>
    public static bool TemplatesInstalled(string dotnetNewListOutput) =>
        Regex.IsMatch(dotnetNewListOutput ?? "", @"\baspire-apphost\b|\baspire-servicedefaults\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when the <c>aspire</c> workload is installed (<c>dotnet workload list</c>).</summary>
    public static bool WorkloadInstalled(string workloadListOutput) =>
        Regex.IsMatch(workloadListOutput ?? "", @"(?m)^\s*aspire\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The orchestrator: the project with the Aspire AppHost SDK.</summary>
    public static bool IsAppHostProject(string projectFileText) =>
        (projectFileText ?? "").Contains("Aspire.AppHost.Sdk", StringComparison.OrdinalIgnoreCase) ||
        (projectFileText ?? "").Contains("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase);

    /// <summary>The shared ServiceDefaults project (telemetry, health checks, resilience handlers).</summary>
    public static bool IsServiceDefaultsProject(string projectFileText) =>
        (projectFileText ?? "").Contains("Microsoft.Extensions.ServiceDiscovery", StringComparison.OrdinalIgnoreCase) ||
        (projectFileText ?? "").Contains("Microsoft.Extensions.Http.Resilience", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any project that already consumes Aspire hosting or client packages.</summary>
    public static bool IsAspireConsumer(string projectFileText) =>
        Regex.IsMatch(projectFileText ?? "", @"Include=""Aspire\.[A-Za-z]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// The two Program.cs edits Aspire scaffolding needs. Pure text in, pure text out: the AppHost must list
/// its resources before <c>builder.Build()</c>, and a service that adopts ServiceDefaults gets one call
/// after its builder and one before its <c>Run()</c>. Nothing is assumed about the file beyond those
/// anchors — a Program.cs that does not match is left untouched and reported.
/// </summary>
public static class AspireWiring
{
    /// <summary>Adds <c>AddProject&lt;Projects.X&gt;("x")</c> lines before the builder's Build/Run call.</summary>
    public static (string Text, List<string> Added) AddProjects(
        string programText, IEnumerable<(string Member, string Resource)> resources)
    {
        // Column 0 is the anchor: top-level statements are unindented, so an indented `builder.Build()`
        // belongs to a method (an explicit Main) and guessing around it would break the file.
        var build = Regex.Matches(programText, @"(?m)^(?<receiver>[A-Za-z_]\w*)\.Build\s*\(\s*\)")
            .Cast<Match>().LastOrDefault();
        if (build is null) return (programText, new List<string>());

        var receiver = build.Groups["receiver"].Value;
        var lines = new List<string>();
        var added = new List<string>();

        foreach (var (member, resource) in resources)
        {
            if (programText.Contains($"AddProject<{member}>", StringComparison.Ordinal)) continue;
            lines.Add($"{receiver}.AddProject<{member}>(\"{resource}\");");
            added.Add($"{member} -> \"{resource}\"");
        }

        if (lines.Count == 0) return (programText, added);
        return (programText[..build.Index] + string.Join("\n", lines) + "\n" + programText[build.Index..], added);
    }

    /// <summary>Adds <c>AddServiceDefaults()</c> after the builder and <c>MapDefaultEndpoints()</c> before Run.</summary>
    public static (string Text, bool Changed) AddServiceDefaults(string programText)
    {
        // The builder statement must start at column 0 (top-level statements): an indented one is
        // inside a method, and the insertion point would be inside that method too.
        var create = Regex.Match(programText,
            @"(?m)^(?!\s)(?:[A-Za-z_][\w.?:<>,]*[ \t]+)?(?<builder>[A-Za-z_]\w*)[ \t]*=[^;\r\n]*CreateBuilder\s*\(");
        if (!create.Success) return (programText, false);

        var builder = create.Groups["builder"].Value;
        if (programText.Contains($"{builder}.AddServiceDefaults()", StringComparison.Ordinal)) return (programText, false);

        var lineEnd = programText.IndexOf('\n', create.Index);
        var statementEnd = lineEnd < 0 ? programText.Length : lineEnd + 1;
        if (!programText[create.Index..statementEnd].Contains(';')) return (programText, false); // multi-line statement: don't guess where it ends

        var updated = programText[..statementEnd]
                    + $"{builder}.AddServiceDefaults();\n"
                    + programText[statementEnd..];

        var run = Regex.Match(updated, @"(?m)^(?<app>[A-Za-z_]\w*)\.Run\s*\(");
        if (run.Success && !updated.Contains($"{run.Groups["app"].Value}.MapDefaultEndpoints()", StringComparison.Ordinal))
            updated = updated[..run.Index]
                    + $"{run.Groups["app"].Value}.MapDefaultEndpoints();\n"
                    + updated[run.Index..];

        return (updated, true);
    }
}
