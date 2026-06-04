using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitLogTool
{
    private const char RecordSep = (char)0x1e; // matches git pretty-format %x1e
    private const char UnitSep   = (char)0x1f; // matches git pretty-format %x1f

    [McpServerTool(Name = "git_log"), Description(
        "Show recent git commits of the repository containing the loaded solution: hash, author, ISO date " +
        "and subject, most recent first. Optionally restrict to a file/directory path. Use this INSTEAD OF " +
        "a shell 'git log' for a structured result. Read-only.")]
    public static async Task<object> GitLog(
        [Description("How many commits to return (default: 20, max: 200).")] int maxCount = 20,
        [Description("Optional: only commits that touched this file or directory path.")] string? path = null)
    {
        if (maxCount < 1) maxCount = 1;
        if (maxCount > 200) maxCount = 200;

        var (repoDir, error) = await GitCli.ResolveRepoAsync();
        if (error is not null) return error;

        // Unit-separator between fields, record-separator between commits.
        const string fmt = "%H%x1f%h%x1f%an%x1f%ad%x1f%s%x1e";
        var pathArg = string.IsNullOrWhiteSpace(path) ? "" : $" -- \"{path}\"";
        var args = $"log -n {maxCount} --date=iso --pretty=format:{fmt}{pathArg}";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (timedOut) return new { error = "git log timed out." };
        if (exitCode != 0) return new { error = $"git log failed: {stderr.Trim()}" };

        var commits = new List<object>();
        foreach (var record in stdout.Split(RecordSep, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\r', '\n').Split(UnitSep);
            if (fields.Length < 5) continue;
            commits.Add(new
            {
                hash      = fields[0],
                shortHash = fields[1],
                author    = fields[2],
                date      = fields[3],
                subject   = fields[4],
            });
        }

        return new
        {
            repoDir,
            pathFilter = path,
            count = commits.Count,
            commits,
        };
    }
}
