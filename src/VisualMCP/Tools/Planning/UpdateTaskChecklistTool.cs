using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Planning;

[McpServerToolType]
public static class UpdateTaskChecklistTool
{
    private const string DoneMark = "☑";
    private const string TodoMark = "☐";

    [McpServerTool(Name = "update_task_checklist"), Description(
        "Mark steps done (or not-done) in a checklist created by create_task_checklist. Identify steps by " +
        "their 1-based position in the table. Call this as you finish each step of a multi-step operation " +
        "so the Task|Done table stays current. Returns the updated counts and the new file content.")]
    public static object UpdateTaskChecklist(
        [Description("Path to the checklist .md file (as returned by create_task_checklist).")] string path,
        [Description("1-based positions of the steps to mark as DONE.")] int[]? done = null,
        [Description("1-based positions of the steps to mark as NOT done (to undo).")] int[]? notDone = null)
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

        var rowNum = 0;
        var updated = 0;
        var doneCount = 0;
        var totalSteps = 0;

        for (var i = sepIdx + 1; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal)) break; // table ended
            rowNum++;
            totalSteps++;

            bool? target = doneSet.Contains(rowNum) ? true
                         : notDoneSet.Contains(rowNum) ? false
                         : null;

            if (target is not null)
            {
                lines[i] = SetDoneCell(lines[i], target.Value);
                updated++;
            }

            if (lines[i].Contains(DoneMark, StringComparison.Ordinal)) doneCount++;
        }

        try { File.WriteAllLines(path, lines); }
        catch (Exception ex) { return new { error = $"Failed to write checklist: {ex.Message}" }; }

        return new
        {
            path,
            updated,
            totalSteps,
            doneCount,
            remaining = totalSteps - doneCount,
            allDone = totalSteps > 0 && doneCount == totalSteps,
            content = string.Join(Environment.NewLine, lines),
        };
    }

    private static bool IsSeparatorRow(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("|", StringComparison.Ordinal)) return false;
        return t.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0
               && t.Contains('-');
    }

    private static string SetDoneCell(string line, bool isDone)
    {
        var cells = line.Split('|');
        if (cells.Length < 3) return line;          // not a 2-column data row
        cells[^2] = isDone ? $" {DoneMark} " : $" {TodoMark} ";
        return string.Join("|", cells);
    }
}
