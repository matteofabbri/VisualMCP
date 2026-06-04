using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VisualMCP.Workspace;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class CreateTaskChecklistTool
{
    [McpServerTool(Name = "create_task_checklist"), Description(
        "At the START of any multi-step operation, create a Markdown checklist file with a two-column " +
        "table (Task | Done) listing what needs to be done and whether each step is done. Mark steps off " +
        "as you complete them with update_task_checklist. Use this to plan and track multi-step work so " +
        "progress is visible and resumable. The file is written next to the solution by default.")]
    public static object CreateTaskChecklist(
        [Description("Title of the checklist / the overall task.")] string title,
        [Description("The ordered list of steps to perform.")] string[] steps,
        [Description("Optional: file name (a '.md' extension is added if missing). Defaults to a name derived from the title.")] string? fileName = null,
        [Description("Optional: target directory. Defaults to the loaded solution's directory, or the current directory.")] string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new { error = "A title is required." };
        if (steps is null || steps.Length == 0)
            return new { error = "At least one step is required." };

        // Resolve the directory.
        string dir;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (!Directory.Exists(directory)) return new { error = $"Directory not found: {directory}" };
            dir = Path.GetFullPath(directory);
        }
        else
        {
            var sln = RoslynWorkspaceService.Instance.LoadedSolutionPath;
            dir = sln is not null ? Path.GetDirectoryName(sln)! : Directory.GetCurrentDirectory();
        }

        var name = string.IsNullOrWhiteSpace(fileName) ? Slug(title) + ".md" : fileName!.Trim();
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name += ".md";
        var path = Path.Combine(dir, name);

        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"_Created {DateTime.Now:yyyy-MM-dd HH:mm}. Update the Done column with `update_task_checklist`._");
        sb.AppendLine();
        sb.AppendLine("| Task | Done |");
        sb.AppendLine("| ---- | ---- |");
        foreach (var step in steps)
            sb.AppendLine($"| {EscapeCell(step)} | ☐ |");

        var content = sb.ToString();

        try { File.WriteAllText(path, content); }
        catch (Exception ex) { return new { error = $"Failed to write checklist: {ex.Message}", path }; }

        return new
        {
            path,
            fileName = name,
            stepCount = steps.Length,
            content,
        };
    }

    private static string EscapeCell(string s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();

    private static string Slug(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "checklist" : slug;
    }
}
