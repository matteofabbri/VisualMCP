using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VisualMCP.Tools.Git;

[McpServerToolType]
public static class GitStatusTool
{
    [McpServerTool(Name = "git_status"), Description(
        "Show the git status of the repository containing the loaded solution: current branch, upstream " +
        "tracking with ahead/behind counts, and the staged, modified, untracked and conflicted files. " +
        "Use this INSTEAD OF a shell 'git status' to get a structured, parsed result. Read-only.")]
    public static async Task<object> GitStatus()
    {
        var (repoDir, error) = await GitCli.ResolveRepoAsync();
        if (error is not null) return error;

        var (exitCode, timedOut, stdout, stderr, _) =
            await GitCli.RunAsync(repoDir!, "status --porcelain=v1 --branch", 30);

        if (timedOut) return new { error = "git status timed out." };
        if (exitCode != 0) return new { error = $"git status failed: {stderr.Trim()}" };

        string? branch = null, upstream = null;
        int? ahead = null, behind = null;
        var staged     = new List<object>();
        var modified   = new List<object>();
        var untracked  = new List<string>();
        var conflicted = new List<object>();

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchLine(line[3..], ref branch, ref upstream, ref ahead, ref behind);
                continue;
            }

            if (line.Length < 3) continue;
            var x = line[0];               // index / staged
            var y = line[1];               // working tree
            var path = line[3..];

            if (x == '?' && y == '?') { untracked.Add(path); continue; }
            if (x == '!' && y == '!') continue; // ignored

            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            {
                conflicted.Add(new { path, code = $"{x}{y}" });
                continue;
            }

            if (x != ' ' && x != '?')
                staged.Add(new { path, change = DescribeCode(x) });
            if (y != ' ' && y != '?')
                modified.Add(new { path, change = DescribeCode(y) });
        }

        var clean = staged.Count == 0 && modified.Count == 0 && untracked.Count == 0 && conflicted.Count == 0;

        return new
        {
            repoDir,
            branch,
            upstream,
            ahead,
            behind,
            clean,
            stagedCount    = staged.Count,
            modifiedCount  = modified.Count,
            untrackedCount = untracked.Count,
            conflictedCount = conflicted.Count,
            staged,
            modified,
            untracked,
            conflicted,
        };
    }

    private static void ParseBranchLine(string s, ref string? branch, ref string? upstream, ref int? ahead, ref int? behind)
    {
        // Examples:
        //   main...origin/main [ahead 1, behind 2]
        //   main
        //   HEAD (no branch)
        var bracket = s.IndexOf(" [", StringComparison.Ordinal);
        var head = bracket >= 0 ? s[..bracket] : s;

        var sep = head.IndexOf("...", StringComparison.Ordinal);
        if (sep >= 0) { branch = head[..sep]; upstream = head[(sep + 3)..]; }
        else branch = head.Trim();

        if (bracket >= 0)
        {
            var inside = s[(bracket + 2)..].TrimEnd(']');
            foreach (var part in inside.Split(',', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(part[6..], out var a)) ahead = a;
                else if (part.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(part[7..], out var b)) behind = b;
            }
        }
    }

    private static string DescribeCode(char c) => c switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type-changed",
        'U' => "unmerged",
        _   => c.ToString(),
    };
}
