using System.Text;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.Planning;

internal static class CreateTaskChecklistImpl
{
    internal static object Run(string title, string[] steps, string? fileName, string? directory)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new { error = "A title is required." };
        if (steps is null || steps.Length == 0)
            return new { error = "At least one step is required." };

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

        return new { path, fileName = name, stepCount = steps.Length, content };
    }

    private static string EscapeCell(string s) =>
        (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();

    private static string Slug(string title)
    {
        var chars = title.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "checklist" : slug;
    }
}

internal static class UpdateTaskChecklistImpl
{
    private const string DoneMark = "☑";
    private const string TodoMark = "☐";

    internal static object Run(string path, int[]? done, int[]? notDone)
    {
        if (!File.Exists(path))
            return new { error = $"Checklist file not found: {path}" };

        var doneSet    = new HashSet<int>(done ?? Array.Empty<int>());
        var notDoneSet = new HashSet<int>(notDone ?? Array.Empty<int>());

        List<string> lines;
        try { lines = File.ReadAllLines(path).ToList(); }
        catch (Exception ex) { return new { error = $"Failed to read checklist: {ex.Message}" }; }

        var sepIdx = lines.FindIndex(IsSeparatorRow);
        if (sepIdx < 0)
            return new { error = "No Markdown table found in the file (expected a '| ---- | ---- |' separator)." };

        var rowNum = 0; var updated = 0; var doneCount = 0; var totalSteps = 0;

        for (var i = sepIdx + 1; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) break;
            rowNum++; totalSteps++;

            bool? target = doneSet.Contains(rowNum) ? true : notDoneSet.Contains(rowNum) ? false : null;
            if (target is not null) { lines[i] = SetDoneCell(lines[i], target.Value); updated++; }
            if (lines[i].Contains(DoneMark, StringComparison.Ordinal)) doneCount++;
        }

        try { File.WriteAllLines(path, lines); }
        catch (Exception ex) { return new { error = $"Failed to write checklist: {ex.Message}" }; }

        return new
        {
            path, updated, totalSteps, doneCount,
            remaining = totalSteps - doneCount,
            allDone = totalSteps > 0 && doneCount == totalSteps,
            content = string.Join(Environment.NewLine, lines),
        };
    }

    private static bool IsSeparatorRow(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("|", StringComparison.Ordinal)) return false;
        return t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0 && t.Contains('-');
    }

    private static string SetDoneCell(string line, bool isDone)
    {
        var cells = line.Split('|');
        if (cells.Length < 3) return line;
        cells[^2] = isDone ? $" {DoneMark} " : $" {TodoMark} ";
        return string.Join("|", cells);
    }
}
