using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Platform;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class FixLineEndingsTool
{
    [McpServerTool(Name = "fix_line_endings"), Description(
        "Make text files use one line-ending style. style=auto (default) fixes mixed files to their own " +
        "majority and leaves uniform files alone; style=lf or crlf forces the whole scope. Use it when a tool or " +
        "an agent left CRLF in an LF repository (or the reverse) — every other write in VisualMCP already normalises " +
        "to the target file's existing convention, so this is only needed for a deliberate migration. " +
        "scope=file (needs path), directory (path, recursive, bin/obj/.git skipped), project (projectName) or " +
        "solution. dryRun defaults to true and lists what would change; pass dryRun=false to rewrite.")]
    public static Task<object> FixLineEndings(
        IFixLineEndingsService service,
        [Description("file | directory | project | solution")] string scope = "solution",
        [Description("File or directory, for scope=file/directory.")] string? path = null,
        [Description("Project name, for scope=project.")] string? projectName = null,
        [Description("auto (default) | lf | crlf")] string? style = null,
        [Description("Comma-separated extensions to include, e.g. '.cs,.md'. Default: a sensible text set.")] string? extensions = null,
        [Description("true (default) previews only; pass false to rewrite the files.")] bool dryRun = true)
        => service.FixAsync(scope, path, projectName, style, extensions, dryRun);
}

[McpServerToolType]
public static class HostSystemTool
{
    [McpServerTool(Name = "host_system"), Description(
        "What this machine is: OS family (windows/linux/macos), architecture, the newline a file written here " +
        "conventionally uses, whether paths are case-insensitive, and how the line endings of a specific file or " +
        "directory would be resolved (existing file → .editorconfig → .gitattributes → sibling files → OS). " +
        "Use it before writing files from a script, or to diagnose a line-ending mismatch. Read-only.")]
    public static object HostSystemInfo(
        [Description("Optional file or directory whose effective line-ending style should be resolved.")] string? path = null)
    {
        LineEndingsStyle? style = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            style = File.Exists(path)
                ? LineEndingsStyle.ForExisting(path)
                : LineEndingsStyle.ForNew(Path.GetFullPath(path));
        }

        return new
        {
            family = HostSystem.Family.ToString().ToLowerInvariant(),
            host = HostSystem.Describe(),
            defaultNewLine = HostSystem.DefaultNewLine == LineEndings.CrLf ? "crlf" : "lf",
            caseInsensitivePaths = HostSystem.IsCaseInsensitiveFileSystem,
            executableSuffix = HostSystem.ExecutableSuffix,
            lineEndings = style,
        };
    }

    private sealed record LineEndingsStyle(string Path, string Style, bool Mixed, int CrLfLines, int LfLines)
    {
        public static LineEndingsStyle ForExisting(string path)
        {
            var text = File.ReadAllText(path);
            var (crlf, lf) = LineEndings.Count(text);
            var style = LineEndings.Detect(text, LineEndings.ForFile(path));

            return new LineEndingsStyle(System.IO.Path.GetFullPath(path), LineEndings.Describe(style), crlf > 0 && lf > 0, crlf, lf);
        }

        public static LineEndingsStyle ForNew(string path)
        {
            var style = LineEndings.ForNewFile(path);
            return new LineEndingsStyle(System.IO.Path.GetFullPath(path), LineEndings.Describe(style), false, 0, 0);
        }
    }
}
