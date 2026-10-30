using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using Snitch.IO;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.IO;

[McpServerToolType]
public static class ReadFilesTool
{
    [McpServerTool(Name = "read_files"), Description(
        "Read SEVERAL files in one call, each with line numbers. Use this INSTEAD OF calling read_file once per file " +
        "when the paths are already known (from list_directory, read_tree, search_text, find_symbol or a previous read): " +
        "one round-trip instead of one per file. A file that fails does not abort the rest \u2014 its error is reported " +
        "in place. The total output is capped: files that do not fit are named at the end so they can be requested again. " +
        "For a single file prefer read_file; for a declaration body prefer read_symbol.")]
    public static CallToolResult ReadFiles(
        IIoService io,
        [Description("File paths to read, in order. Absolute or relative; duplicates are dropped.")] string[] paths,
        [Description("Total output budget in characters (2000-190000, default 60000). Files past the budget are skipped and listed.")] int maxBytes = 60_000)
    {
        if (paths is null || paths.Length == 0)
        {
            return ToolText.Fail("read_files needs at least one path.");
        }

        int budget = Math.Clamp(maxBytes, 2_000, ToolText.MaxChars - 10_000);
        var output = new StringBuilder();
        var read = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < paths.Length; i++)
        {
            string path = (paths[i] ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                continue;
            }

            string identity;
            try
            {
                identity = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{path} \u2014 invalid path: {ex.Message}");
                continue;
            }

            if (!seen.Add(identity))
            {
                continue;
            }

            var file = io.ReadFile(path, null, null);
            if (file.Error is not null)
            {
                errors.Add($"{file.Path ?? path} \u2014 {file.Error}");
                continue;
            }

            int returned = file.ReturnedLines ?? 0;
            int total = file.TotalLines ?? 0;
            string header = returned == 0
                ? $"{file.Path} (no lines; the file has {total} lines)"
                : $"{file.Path} (lines 1-{returned} of {total})";
            string body = file.Content ?? string.Empty;
            string block = output.Length == 0 ? $"{header}\n\n{body}" : $"\n\n{header}\n\n{body}";

            int remaining = budget - output.Length;
            if (remaining <= header.Length + 200)
            {
                skipped.Add(file.Path ?? path);
                continue;
            }

            if (block.Length > remaining)
            {
                // Cut on a line boundary so the last visible line is a whole one.
                int cut = block.LastIndexOf('\n', Math.Min(remaining, block.Length - 1));
                if (cut <= 0)
                {
                    cut = remaining;
                }

                string shown = block[..cut];
                int shownLines = shown.Count(c => c == '\n');
                output.Append(shown)
                      .Append("\n\u2026 truncated by the output budget after ")
                      .Append(shownLines)
                      .Append(" line(s) of this file: read_file with fromLine/toLine for the rest.");
                read.Add(file.Path ?? path);

                for (int j = i + 1; j < paths.Length; j++)
                {
                    string rest = (paths[j] ?? string.Empty).Trim();
                    if (rest.Length > 0)
                    {
                        skipped.Add(rest);
                    }
                }

                break;
            }

            output.Append(block);
            read.Add(file.Path ?? path);
        }

        var footer = new StringBuilder();
        footer.Append(read.Count == 0
            ? "No file could be read."
            : $"Read {read.Count} file(s), {output.Length} character(s).");
        if (errors.Count > 0)
        {
            footer.Append($"\n{errors.Count} error(s):\n  - ").Append(string.Join("\n  - ", errors));
        }

        if (skipped.Count > 0)
        {
            footer.Append($"\n{skipped.Count} file(s) left out (budget or truncation) \u2014 call read_files again with:\n  - ")
                  .Append(string.Join("\n  - ", skipped));
        }

        return ToolText.Ok($"{output}\n\n\u2014\n{footer}");
    }
}
