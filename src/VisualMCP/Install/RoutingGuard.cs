using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisualMCP.Install;

/// <summary>
/// The PreToolUse routing guard as a decision: the hook payload in, a deny reason or null out.
/// It is the POSIX counterpart of <c>hooks/pre-tool-use.ps1</c> and ships inside the server binary
/// (<c>VisualMCP --hook pre-tool-use</c>), so the guard needs no interpreter of its own: the one
/// runtime the install already requires — the .NET SDK — is enough.
/// </summary>
public static partial class RoutingGuard
{
    private const string SearchTool =
        "search_text{pattern, path, glob} (use find_symbol / find_references for C# symbols, never text search)";
    private const string ReadTool = "read_file{path, fromLine, toLine}";
    private const string ListTool = "list_directory{path} (or search_text to match file names)";
    private const string RewriteTool = "Edit (or Write for a new file), not a shell heredoc";

    /// <summary>The tools whose input carries a file path the guard inspects.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit",
    };

    /// <summary>The published documents a repository root may hold; any other new .md there is a plan.</summary>
    private static readonly HashSet<string> RootDocuments = new(StringComparer.Ordinal)
    {
        "readme", "contributing", "license", "changelog", "code_of_conduct",
        "security", "authors", "notice", "support", "governance", "citation",
    };

    /// <summary>
    /// The decision for one tool call. <paramref name="filePath"/> is a Write/Edit target and
    /// <paramref name="command"/> the Bash command; the tool name decides which one is read.
    /// </summary>
    public static string? Decide(string? toolName, string? command, string? filePath, string? cwd)
    {
        cwd = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;

        // A write into a memory store, or the creation of a plan-like .md, belongs on the board.
        if (toolName is not null && WriteTools.Contains(toolName))
            return ProtectedWriteReason(toolName, filePath, cwd);

        if (toolName != "Bash") return null;
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (command.Contains("vmcp-allow", StringComparison.Ordinal)) return null;
        if (!IsDotNetRepository(cwd)) return null;

        var probe = ProbeFor(command);
        return probe is null ? null : ReasonFor(probe);
    }

    /// <summary>
    /// Reads the hook payload from <paramref name="input"/> and writes the deny decision, if any, to
    /// <paramref name="output"/>. It never throws and always returns 0: a hook must not break the session.
    /// </summary>
    public static async Task<int> RunHookAsync(Stream input, TextWriter output, CancellationToken ct = default)
    {
        try
        {
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var raw = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return 0;

            string? toolName = null;
            string? command = null;
            string? filePath = null;
            string? cwd = null;

            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return 0;
            var payload = document.RootElement;

            if (payload.TryGetProperty("tool_name", out var name) && name.ValueKind == JsonValueKind.String)
                toolName = name.GetString();
            if (payload.TryGetProperty("cwd", out var working) && working.ValueKind == JsonValueKind.String)
                cwd = working.GetString();

            if (payload.TryGetProperty("tool_input", out var toolInput) &&
                toolInput.ValueKind == JsonValueKind.Object)
            {
                if (toolInput.TryGetProperty("command", out var text) && text.ValueKind == JsonValueKind.String)
                    command = text.GetString();
                if (toolInput.TryGetProperty("file_path", out var file) && file.ValueKind == JsonValueKind.String)
                    filePath = file.GetString();
                if (filePath is null &&
                    toolInput.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    filePath = path.GetString();
            }

            var reason = Decide(toolName, command, filePath, cwd);
            if (reason is null) return 0;

            var deny = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    permissionDecision = "deny",
                    permissionDecisionReason = reason,
                },
            };
            await output.WriteAsync(JsonSerializer.Serialize(deny)).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A hook must never break the session.
        }

        return 0;
    }

    // ── the write rules ─────────────────────────────────────────────────────

    private static string? ProtectedWriteReason(string toolName, string? path, string cwd)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalized = path.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (fileName.Equals("memory.md", StringComparison.OrdinalIgnoreCase) ||
            MemoryStore().IsMatch(normalized))
            return MemoryRoute();

        // Only creation is refused, never the edit of a document that already exists: docs/gaps.md stays editable.
        if (toolName != "Write" || !normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;

        var root = RepositoryRoot(cwd);
        if (root is null) return null;

        string absolute;
        try
        {
            absolute = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (File.Exists(absolute)) return null;

        var folder = Path.GetDirectoryName(absolute);
        // The repository root holds a fixed set of published documents. Anything else created there — a
        // plan, a checklist, a working note named after the task it describes — reads as documentation
        // to the next session while it is really a to-do list; that belongs on the board.
        if (PathEquals(folder, root))
        {
            var stem = Path.GetFileNameWithoutExtension(absolute).ToLowerInvariant();
            return RootDocuments.Contains(stem) ? null : RootRoute();
        }

        if (!PathEquals(folder, Path.Combine(root, "docs"))) return null;
        return PlanFile().IsMatch(Path.GetFileName(absolute)) ? PlanRoute() : null;
    }

    /// <summary>The nearest directory at or above <paramref name="directory"/> holding a .sln/.slnx.</summary>
    private static string? RepositoryRoot(string? directory, int maxParents = 3)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        string current;
        try { current = Path.GetFullPath(directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var level = 0; level <= maxParents; level++)
        {
            if (Directory.Exists(current) && HoldsSolution(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal)) break;
            current = parent;
        }

        return null;
    }

    private static bool HoldsSolution(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.sln").Any() ||
                   Directory.EnumerateFiles(directory, "*.slnx").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDotNetRepository(string? directory) => RepositoryRoot(directory) is not null;

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ── the shell rules ─────────────────────────────────────────────────────

    /// <summary>Whether the command writes files through the shell instead of a harness tool.</summary>
    private static bool IsRewrite(string command)
    {
        if (PythonInline().IsMatch(command) && WriteIntent().IsMatch(command)) return true;
        if (SedInPlace().IsMatch(command)) return true;
        return ShellCreate().IsMatch(command) || Tee().IsMatch(command);
    }

    /// <summary>The probe this command performs, or null when it is legitimate shell work.</summary>
    private static string? ProbeFor(string command)
    {
        if (IsRewrite(command)) return "rewrite";

        foreach (var part in SegmentSplit().Split(command))
        {
            var segment = part.Trim();
            if (segment.Length == 0) continue;
            if (Tooling().IsMatch(segment)) continue;
            if (WriteRedirect().IsMatch(segment)) continue;
            if (Search().IsMatch(segment)) return "search";
            if (SedRead().IsMatch(segment) || Cat().IsMatch(segment)) return "read";
            if (List().IsMatch(segment)) return "list";
        }

        return null;
    }

    private static string ReasonFor(string probe)
    {
        if (probe == "rewrite")
            return "VisualMCP: this command rewrites files through the shell inside a .NET repository. " +
                   $"Use the harness tool instead: {RewriteTool} - no quoting or encoding traps, " +
                   "and the change stays reviewable. " +
                   "To keep Bash deliberately, include 'vmcp-allow' in the command.";

        var name = probe switch
        {
            "search" => "full-text search",
            "read" => "line-range read",
            _ => "directory listing",
        };
        var tool = probe switch
        {
            "search" => SearchTool,
            "read" => ReadTool,
            _ => ListTool,
        };
        return $"VisualMCP: this is a {name} shell probe inside a .NET repository. " +
               $"Use the MCP tool instead: {tool}. " +
               "The solution is already loaded; do not call load_solution first. " +
               "If the shell form is genuinely required (binary files, counting, complex pipelines), " +
               "call the MCP tool run_command with the exact same command. " +
               "To keep Bash deliberately, include 'vmcp-allow' in the command.";
    }

    private static string MemoryRoute() =>
        "VisualMCP: project memory lives on the board. " +
        "Use board_remember{topic, body, kind} and read it back with board_recall{query}; pinned notes ride " +
        "along in board_brief and read_project_docs. A MEMORY.md or a note file is not read by the next session. " +
        "To write it deliberately, use the MCP tool run_command.";

    private static string RootRoute() =>
        "VisualMCP: a new Markdown file at the repository root is not where this belongs. " +
        "The root holds the published documents (README, CONTRIBUTING, LICENSE, CHANGELOG); documentation goes " +
        "under docs/, and work to be done goes on the board: create_task_checklist{title, steps} or " +
        "board_add{title, description, steps} create a card, board_move{status, note} records progress — no file " +
        "is written and the card is what the next session reads. " +
        "To write the file deliberately, use the MCP tool run_command.";

    private static string PlanRoute() =>
        "VisualMCP: a plan/notes file is not the plan. " +
        "Put the work on the board: create_task_checklist{title, steps} creates one card with the steps as a " +
        "checklist (no file), board_add{title, steps} the same on a card you describe, board_move{status, note} " +
        "records progress. The board is what the next session reads. " +
        "To write the file deliberately, use the MCP tool run_command.";

    // ── patterns (the Python hook's rules, verbatim) ────────────────────────

    // A path inside a memory store: the client's own (~/.zcode/cli/memories/...) or Claude Code's
    // (~/.claude/projects/<slug>/memory/...). MEMORY.md is checked separately, by name.
    [GeneratedRegex(@"[\x2F\\]\.zcode[\x2F\\]cli[\x2F\\]memories[\x2F\\]|[\x2F\\]\.claude[\x2F\\]projects[\x2F\\][^\x2F\\]+[\x2F\\]memory[\x2F\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryStore();

    // The names of a file that is a plan/backlog rather than documentation.
    [GeneratedRegex("(checklist|plan|backlog|todo|handoff|notes|gaps|memory)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanFile();

    [GeneratedRegex(@"(^|[\s;&|(])(dotnet|msbuild|nuget|git|gh|npm|pnpm|yarn|npx|node|python|python3|py|pip|cargo|go|make|cmake|ninja|docker|kubectl|terraform|gradle|mvn|java|curl|wget|rm|rmdir|mkdir|mv|cp|touch|chmod|chown|ln|tar|zip|unzip|7z)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tooling();

    [GeneratedRegex(@">>?\s*(?!/dev/null)[^\s|&;]")]
    private static partial Regex WriteRedirect();

    [GeneratedRegex(@"(^|[\s|;&(])(grep|egrep|fgrep|rg|ag|ack|Select-String)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Search();

    [GeneratedRegex(@"(^|[\s|;&(])sed\b[^|;&]*\s-n\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SedRead();

    [GeneratedRegex(@"(^|[\s|;&(])(cat|head|tail|more|less|bat|nl|tac|Get-Content)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Cat();

    [GeneratedRegex(@"(^|[\s|;&(])(ls|dir|find|tree|Get-ChildItem)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex List();

    [GeneratedRegex(@"(^|[\s;&|(])(py|python3?(\.\d+)?)\b[^;|&]*?(<<\s*\S*|\s-c\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PythonInline();

    [GeneratedRegex("write_text\\s*\\(|\\.write\\s*\\(|open\\s*\\([^)]*[\"']w|\\.unlink\\s*\\(|os\\.replace\\s*\\(|shutil\\.(copy|move|rmtree)|\\.rename\\s*\\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteIntent();

    [GeneratedRegex(@"(^|[\s;&|(])sed\b[^|;&]*\s-i", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SedInPlace();

    [GeneratedRegex(@"(^|[\s;&|(])(cat|printf|echo)\b[^|;&]*?>>?\s*[^\s|&;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellCreate();

    [GeneratedRegex(@"(^|[\s;&|(])tee(\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tee();

    [GeneratedRegex("&&|\\|\\||;")]
    private static partial Regex SegmentSplit();
}
