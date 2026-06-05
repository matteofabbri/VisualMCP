using VisualMCP.Implementation.Execution;

namespace VisualMCP.Implementation.Git;

/// <summary>Implementation for the git MCP commands.</summary>
internal static class GitImpl
{
    // ── status ───────────────────────────────────────────────────────────────
    internal static async Task<object> StatusAsync(string? repoPath)
    {
        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
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
            var x = line[0]; var y = line[1]; var path = line[3..];

            if (x == '?' && y == '?') { untracked.Add(path); continue; }
            if (x == '!' && y == '!') continue;

            if (x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D'))
            { conflicted.Add(new { path, code = $"{x}{y}" }); continue; }

            if (x != ' ' && x != '?') staged.Add(new { path, change = DescribeCode(x) });
            if (y != ' ' && y != '?') modified.Add(new { path, change = DescribeCode(y) });
        }

        var clean = staged.Count == 0 && modified.Count == 0 && untracked.Count == 0 && conflicted.Count == 0;

        return new
        {
            repoDir, branch, upstream, ahead, behind, clean,
            stagedCount = staged.Count, modifiedCount = modified.Count,
            untrackedCount = untracked.Count, conflictedCount = conflicted.Count,
            staged, modified, untracked, conflicted,
        };
    }

    private static void ParseBranchLine(string s, ref string? branch, ref string? upstream, ref int? ahead, ref int? behind)
    {
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
        'M' => "modified", 'A' => "added", 'D' => "deleted", 'R' => "renamed",
        'C' => "copied", 'T' => "type-changed", 'U' => "unmerged", _ => c.ToString(),
    };

    // ── log ──────────────────────────────────────────────────────────────────
    private const char RecordSep = (char)0x1e;
    private const char UnitSep   = (char)0x1f;

    internal static async Task<object> LogAsync(int maxCount, string? path, string? repoPath)
    {
        if (maxCount < 1) maxCount = 1;
        if (maxCount > 200) maxCount = 200;

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
        if (error is not null) return error;

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
            commits.Add(new { hash = fields[0], shortHash = fields[1], author = fields[2], date = fields[3], subject = fields[4] });
        }

        return new { repoDir, pathFilter = path, count = commits.Count, commits };
    }

    // ── diff ─────────────────────────────────────────────────────────────────
    internal static async Task<object> DiffAsync(string target, string? path, bool summaryOnly, string? repoPath)
    {
        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
        if (error is not null) return error;

        var t = (target ?? "working").Trim();
        var args = "diff";
        if (string.Equals(t, "staged", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "cached", StringComparison.OrdinalIgnoreCase))
            args += " --cached";
        else if (!string.Equals(t, "working", StringComparison.OrdinalIgnoreCase) && t.Length > 0)
            args += $" {t}";

        if (summaryOnly) args += " --stat";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- \"{path}\"";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 45);
        if (timedOut) return new { error = "git diff timed out." };
        if (exitCode != 0) return new { error = $"git diff failed: {stderr.Trim()}", command = $"git {args}" };

        return new
        {
            repoDir, target = t, pathFilter = path, summaryOnly, command = $"git {args}",
            empty = string.IsNullOrWhiteSpace(stdout), diff = ProcessRunner.Truncate(stdout, 32_000),
        };
    }

    // ── stage ────────────────────────────────────────────────────────────────
    internal static async Task<object> StageAsync(string[]? paths, bool all, string? repoPath)
    {
        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
        if (error is not null) return error;

        string addArgs;
        if (all) addArgs = "add -A";
        else if (paths is { Length: > 0 })
            addArgs = "add -- " + string.Join(" ", paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => $"\"{p}\""));
        else return new { error = "Provide one or more paths, or set all=true." };

        var (exitCode, timedOut, _, stderr, _) = await GitCli.RunAsync(repoDir!, addArgs, 30);
        if (timedOut) return new { error = "git add timed out." };
        if (exitCode != 0) return new { error = $"git add failed: {stderr.Trim()}", command = $"git {addArgs}" };

        var (_, _, names, _, _) = await GitCli.RunAsync(repoDir!, "diff --cached --name-only", 30);
        var staged = names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToList();

        return new { repoDir, command = $"git {addArgs}", stagedCount = staged.Count, staged };
    }

    // ── commit ───────────────────────────────────────────────────────────────
    internal static async Task<object> CommitAsync(string message, bool stageAll, bool allowEmpty, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { error = "A commit message is required." };

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
        if (error is not null) return error;

        if (stageAll)
        {
            var (addExit, _, _, addErr, _) = await GitCli.RunAsync(repoDir!, "add -A", 30);
            if (addExit != 0) return new { error = $"git add -A failed: {addErr.Trim()}" };
        }

        var (stagedCheck, _, _, _, _) = await GitCli.RunAsync(repoDir!, "diff --cached --quiet", 30);
        if (stagedCheck == 0 && !allowEmpty)
            return new { error = "Nothing staged to commit. Stage changes (git_stage) or set stageAll=true / allowEmpty=true." };

        var msgFile = Path.Combine(Path.GetTempPath(), $"visualmcp-commit-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(msgFile, message);

        try
        {
            var commitArgs = $"commit -F \"{msgFile}\"" + (allowEmpty ? " --allow-empty" : "");
            var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, commitArgs, 45);

            if (timedOut) return new { error = "git commit timed out." };
            if (exitCode != 0)
                return new { error = $"git commit failed: {stderr.Trim()}", output = ProcessRunner.Truncate(stdout + "\n" + stderr, 2000) };

            var (_, _, hash, _, _)   = await GitCli.RunAsync(repoDir!, "rev-parse HEAD", 20);
            var (_, _, short_, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --short HEAD", 20);
            var (_, _, branch, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);

            return new
            {
                repoDir, committed = true, hash = hash.Trim(), shortHash = short_.Trim(),
                branch = branch.Trim(), output = ProcessRunner.Truncate(stdout.Trim(), 2000),
            };
        }
        finally { try { File.Delete(msgFile); } catch { } }
    }

    // ── create branch ────────────────────────────────────────────────────────
    internal static async Task<object> CreateBranchAsync(string name, bool checkout, string? startPoint, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A branch name is required." };

        var (repoDir, error) = await GitCli.ResolveRepoAsync(repoPath);
        if (error is not null) return error;

        var sp = string.IsNullOrWhiteSpace(startPoint) ? "" : $" \"{startPoint}\"";
        var args = checkout ? $"checkout -b \"{name}\"{sp}" : $"branch \"{name}\"{sp}";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (timedOut) return new { error = "git branch creation timed out." };
        if (exitCode != 0) return new { error = $"Failed to create branch: {stderr.Trim()}", command = $"git {args}" };

        var (_, _, current, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);

        return new
        {
            repoDir, branch = name, checkedOut = checkout, currentBranch = current.Trim(),
            startPoint, command = $"git {args}", output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000),
        };
    }
}
