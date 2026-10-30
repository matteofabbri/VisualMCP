using Snitch.Execution;
using Snitch.Workspace;

namespace Snitch.Git;

/// <summary>
/// Git operations over a repository (the loaded solution's repo by default). Reads are free; the
/// rewriting operations (squash, amend, reset, rebase) refuse when they would silently swallow
/// uncommitted work, and a hard reset or a force push additionally requires <c>confirm=true</c>.
/// </summary>
public interface IGitService
{
    // reads
    Task<object> StatusAsync(string? repoPath);
    Task<object> LogAsync(int maxCount, string? path, string? repoPath);
    Task<object> DiffAsync(string target, string? path, bool summaryOnly, string? repoPath);
    Task<object> ShowAsync(string rev, string? path, string? repoPath);
    Task<object> BlameAsync(string path, int? fromLine, int? toLine, string? repoPath);
    Task<object> BranchesAsync(string? repoPath);
    Task<object> RemotesAsync(string? repoPath);

    // staging and history
    Task<object> StageAsync(string[]? paths, bool all, string? repoPath);
    Task<object> CommitAsync(string message, bool stageAll, bool allowEmpty, string? repoPath);
    Task<object> SquashAsync(int count, string message, string? repoPath);
    Task<object> AmendAsync(string message, string? repoPath);

    // branches
    Task<object> CreateBranchAsync(string name, bool checkout, string? startPoint, string? repoPath);
    Task<object> DeleteBranchAsync(string name, bool force, string? repoPath);
    Task<object> RenameBranchAsync(string from, string name, string? repoPath);
    Task<object> CheckoutAsync(string reference, bool create, string? repoPath);

    // integration
    Task<object> MergeAsync(string branch, string strategy, string? message, bool abort, string? repoPath);
    Task<object> RebaseAsync(string? onto, string action, string? repoPath);
    Task<object> CherryPickAsync(string[] commits, string action, string? repoPath);
    Task<object> RevertAsync(string[] commits, string? repoPath);
    Task<object> ResetAsync(string target, string mode, bool confirm, string? repoPath);

    // stash, tags, remotes
    Task<object> StashAsync(string action, string? message, string? name, string? repoPath);
    Task<object> TagAsync(string action, string? name, string? message, string? reference, string? repoPath);
    Task<object> RemoteAsync(string action, string? name, string? url, string? repoPath);
    Task<object> SyncAsync(string action, string? remote, string? branch, bool tags, bool force, bool confirm, string? repoPath);

    /// <summary>Escape hatch: any git command, verbatim, inside the repository.</summary>
    Task<object> RunAsync(string args, string? repoPath, int timeoutSeconds);
}

/// <summary>Default <see cref="IGitService"/> implementation, shelling out to the git CLI.</summary>
public sealed class GitService : IGitService
{
    private readonly SnitchOptions _options;
    private readonly IRoslynWorkspace _workspace;

    public GitService(SnitchOptions options, IRoslynWorkspace workspace)
    {
        _options = options;
        _workspace = workspace;
    }

    // ── status ───────────────────────────────────────────────────────────────
    public async Task<object> StatusAsync(string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
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

    // ── log / show / blame ───────────────────────────────────────────────────
    private const char RecordSep = (char)0x1e;
    private const char UnitSep   = (char)0x1f;

    public async Task<object> LogAsync(int maxCount, string? path, string? repoPath)
    {
        if (maxCount < 1) maxCount = 1;
        if (maxCount > 200) maxCount = 200;

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        const string fmt = "%H%x1f%h%x1f%an%x1f%ad%x1f%s%x1e";
        var pathArg = string.IsNullOrWhiteSpace(path) ? "" : $" -- {Q(path!)}";
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

    public async Task<object> ShowAsync(string rev, string? path, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(rev)) return new { error = "A revision is required (e.g. HEAD, a hash, a tag)." };
        if (LooksLikeFlag(rev)) return new { error = $"'{rev}' is not a revision." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = $"show --stat --format=\"%H%n%an%n%ad%n%n%B\" {rev}";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- {Q(path!)}";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 45);
        if (timedOut) return new { error = "git show timed out." };
        if (exitCode != 0) return new { error = $"git show failed: {stderr.Trim()}", rev };

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        return new
        {
            rev,
            hash = lines.ElementAtOrDefault(0),
            author = lines.ElementAtOrDefault(1),
            date = lines.ElementAtOrDefault(2),
            body = ProcessRunner.Truncate(string.Join('\n', lines.Skip(3)).TrimStart('\n'), 24_000),
        };
    }

    public async Task<object> BlameAsync(string path, int? fromLine, int? toLine, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return new { error = "A file path is required." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = "blame --date=short";
        if (fromLine is { } from)
        {
            if (from < 1) from = 1;
            var to = toLine ?? from;
            if (to < from) to = from;
            args += $" -L {from},{to}";
        }

        args += $" -- {Q(path)}";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);
        if (timedOut) return new { error = "git blame timed out." };
        if (exitCode != 0) return new { error = $"git blame failed: {stderr.Trim()}", path };

        return new
        {
            repoDir,
            path,
            range = fromLine is null ? null : $"{fromLine}-{toLine ?? fromLine}",
            text = ProcessRunner.Truncate(stdout.TrimEnd(), 24_000),
        };
    }

    public async Task<object> BranchesAsync(string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        // %1f is the ref-filter spelling of the unit separator (--pretty wants %x1f instead);
        // it keeps the fields apart even when a subject contains spaces or a tab.
        const string format = "--format=%(refname:short)%1f%(HEAD)%1f%(upstream:short)%1f%(committerdate:short)%1f%(subject)";
        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, $"branch {format}", 30);
        if (timedOut) return new { error = "git branch timed out." };
        if (exitCode != 0) return new { error = $"git branch failed: {stderr.Trim()}" };

        var branches = new List<object>();
        string? current = null;
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = raw.TrimEnd('\r').Split(UnitSep);
            if (fields.Length < 5) continue;

            var isCurrent = fields[1].Trim() == "*";
            if (isCurrent) current = fields[0];

            branches.Add(new
            {
                name = fields[0],
                current = isCurrent,
                upstream = string.IsNullOrWhiteSpace(fields[2]) ? null : fields[2],
                lastCommitDate = string.IsNullOrWhiteSpace(fields[3]) ? null : fields[3],
                lastCommit = string.IsNullOrWhiteSpace(fields[4]) ? null : fields[4],
            });
        }

        return new { repoDir, current, count = branches.Count, branches };
    }

    public async Task<object> RemotesAsync(string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, "remote -v", 20);
        if (exitCode != 0) return new { error = $"git remote failed: {stderr.Trim()}" };

        var remotes = new List<object>();
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var kind = parts[1].Contains("(push)") ? "push" : "fetch";
            remotes.Add(new { name = parts[0], url = parts[1].Split(' ')[0], kind });
        }

        return new { repoDir, count = remotes.Count, remotes };
    }

    // ── diff ─────────────────────────────────────────────────────────────────
    public async Task<object> DiffAsync(string target, string? path, bool summaryOnly, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var t = (target ?? "working").Trim();
        var args = "diff";
        if (string.Equals(t, "staged", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "cached", StringComparison.OrdinalIgnoreCase))
            args += " --cached";
        else if (!string.Equals(t, "working", StringComparison.OrdinalIgnoreCase) && t.Length > 0)
            args += $" {t}";

        if (summaryOnly) args += " --stat";
        if (!string.IsNullOrWhiteSpace(path)) args += $" -- {Q(path!)}";

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
    public async Task<object> StageAsync(string[]? paths, bool all, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        string addArgs;
        if (all) addArgs = "add -A";
        else if (paths is { Length: > 0 })
            addArgs = "add -- " + string.Join(" ", paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Q));
        else return new { error = "Provide one or more paths, or set all=true." };

        var (exitCode, timedOut, _, stderr, _) = await GitCli.RunAsync(repoDir!, addArgs, 30);
        if (timedOut) return new { error = "git add timed out." };
        if (exitCode != 0) return new { error = $"git add failed: {stderr.Trim()}", command = $"git {addArgs}" };

        var (_, _, names, _, _) = await GitCli.RunAsync(repoDir!, "diff --cached --name-only", 30);
        var staged = names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')).ToList();

        return new { repoDir, command = $"git {addArgs}", stagedCount = staged.Count, staged };
    }

    // ── commit / amend / squash ──────────────────────────────────────────────
    public async Task<object> CommitAsync(string message, bool stageAll, bool allowEmpty, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { error = "A commit message is required." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        if (stageAll)
        {
            var (addExit, _, _, addErr, _) = await GitCli.RunAsync(repoDir!, "add -A", 30);
            if (addExit != 0) return new { error = $"git add -A failed: {addErr.Trim()}" };
        }

        var (stagedCheck, _, _, _, _) = await GitCli.RunAsync(repoDir!, "diff --cached --quiet", 30);
        if (stagedCheck == 0 && !allowEmpty)
            return new { error = "Nothing staged to commit. Stage changes first, or set stageAll=true / allowEmpty=true." };

        return await CommitWithMessageAsync(repoDir!, message, allowEmpty, amend: false);
    }

    /// <summary>Reword the last commit: the message changes, the tree does not.</summary>
    public async Task<object> AmendAsync(string message, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(message)) return new { error = "A commit message is required." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        if (await CountAsync(repoDir!, "HEAD") == 0) return new { error = "The repository has no commits yet: nothing to amend." };

        return await CommitWithMessageAsync(repoDir!, message, allowEmpty: false, amend: true);
    }

    /// <summary>
    /// Fold the last <paramref name="count"/> commits into one with a new message. Refused on a dirty
    /// tree: with staged changes present they would be swallowed by the new commit.
    /// </summary>
    public async Task<object> SquashAsync(int count, string message, string? repoPath)
    {
        if (count < 2) return new { error = "count must be at least 2: squashing one commit would only reword it (use git_amend)." };
        if (string.IsNullOrWhiteSpace(message)) return new { error = "A commit message is required." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var (statusExit, _, status, statusErr, _) = await GitCli.RunAsync(repoDir!, "status --porcelain", 30);
        if (statusExit != 0) return new { error = $"Could not read the working tree state: {statusErr.Trim()}" };

        if (!string.IsNullOrWhiteSpace(status))
            return new
            {
                error = "The working tree is not clean; its changes would be folded into the squashed commit. " +
                        "Commit or stash them first.",
                changes = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(10).Select(l => l.TrimEnd('\r')).ToList(),
            };

        var total = await CountAsync(repoDir!, "HEAD");
        if (total < count)
            return new { error = $"Only {total} commit(s) on this branch; cannot squash the last {count}." };

        var (treeExit, _, treeOut, treeErr, _) = await GitCli.RunAsync(repoDir!, "rev-parse HEAD^{tree}", 20);
        if (treeExit != 0) return new { error = $"Could not read the HEAD tree: {treeErr.Trim()}" };

        // The new commit is built from HEAD's tree, so the content is exactly the current one. Its
        // parent is the commit before the squashed range — or nothing when the whole branch is
        // squashed into a new root commit (HEAD~count does not exist in that case).
        var parent = total == count ? string.Empty : $"-p HEAD~{count}";
        var msgFile = Path.Combine(Path.GetTempPath(), $"{_options.TempFilePrefix}-squash-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(msgFile, message);

        try
        {
            var (commitExit, timedOut, commitOut, commitErr, _) =
                await GitCli.RunAsync(repoDir!, $"commit-tree {treeOut.Trim()} {parent} -F \"{msgFile}\"", 45);

            if (timedOut) return new { error = "git commit-tree timed out." };
            if (commitExit != 0) return new { error = $"git commit-tree failed: {commitErr.Trim()}" };

            var squashed = commitOut.Trim();
            var (resetExit, _, _, resetErr, _) = await GitCli.RunAsync(repoDir!, $"reset --soft {squashed}", 30);
            if (resetExit != 0) return new { error = $"git reset --soft {squashed} failed: {resetErr.Trim()}" };

            var (_, _, shortHash, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --short HEAD", 20);
            var (_, _, branch, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);

            return new
            {
                repoDir,
                squashed = count,
                becameRootCommit = total == count,
                hash = squashed,
                shortHash = shortHash.Trim(),
                branch = branch.Trim(),
                remainingCommits = await CountAsync(repoDir!, "HEAD"),
                note = total == count ? "Every commit was squashed: the branch now has a single root commit." : null,
            };
        }
        finally { try { File.Delete(msgFile); } catch { } }
    }

    private async Task<object> CommitWithMessageAsync(string repoDir, string message, bool allowEmpty, bool amend)
    {
        var msgFile = Path.Combine(Path.GetTempPath(), $"{_options.TempFilePrefix}-commit-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(msgFile, message);

        try
        {
            var commitArgs = $"commit{(amend ? " --amend" : string.Empty)} -F \"{msgFile}\"" + (allowEmpty ? " --allow-empty" : "");
            var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir, commitArgs, 45);

            if (timedOut) return new { error = "git commit timed out." };
            if (exitCode != 0)
                return new { error = $"git commit failed: {stderr.Trim()}", output = ProcessRunner.Truncate(stdout + "\n" + stderr, 2000) };

            var (_, _, hash, _, _)      = await GitCli.RunAsync(repoDir, "rev-parse HEAD", 20);
            var (_, _, shortHash, _, _) = await GitCli.RunAsync(repoDir, "rev-parse --short HEAD", 20);
            var (_, _, branch, _, _)    = await GitCli.RunAsync(repoDir, "rev-parse --abbrev-ref HEAD", 20);

            return new
            {
                repoDir, committed = true, amended = amend, hash = hash.Trim(), shortHash = shortHash.Trim(),
                branch = branch.Trim(), output = ProcessRunner.Truncate(stdout.Trim(), 2000),
            };
        }
        finally { try { File.Delete(msgFile); } catch { } }
    }

    // ── branches ─────────────────────────────────────────────────────────────
    public async Task<object> CreateBranchAsync(string name, bool checkout, string? startPoint, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A branch name is required." };
        if (LooksLikeFlag(name)) return new { error = $"'{name}' is not a branch name." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var sp = string.IsNullOrWhiteSpace(startPoint) ? "" : $" {Q(startPoint!)}";
        var args = checkout ? $"checkout -b {Q(name)}{sp}" : $"branch {Q(name)}{sp}";

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

    public async Task<object> DeleteBranchAsync(string name, bool force, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A branch name is required." };
        if (LooksLikeFlag(name)) return new { error = $"'{name}' is not a branch name." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = $"branch {(force ? "-D" : "-d")} {Q(name)}";
        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (exitCode != 0)
            return new
            {
                error = $"Could not delete '{name}': {stderr.Trim()}",
                hint = force ? null : "The branch may hold unmerged commits; pass force=true to delete it anyway.",
                command = $"git {args}",
            };

        return new { repoDir, deleted = name, force, output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000) };
    }

    public async Task<object> RenameBranchAsync(string from, string name, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "The new branch name is required." };
        if (LooksLikeFlag(name) || (from is not null && LooksLikeFlag(from)))
            return new { error = "Branch names never start with '-'." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = string.IsNullOrWhiteSpace(from) ? $"branch -m {Q(name)}" : $"branch -m {Q(from)} {Q(name)}";
        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (exitCode != 0) return new { error = $"git branch -m failed: {stderr.Trim()}", command = $"git {args}" };

        return new
        {
            repoDir,
            from = string.IsNullOrWhiteSpace(from) ? null : from,
            to = name,
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 1000),
        };
    }

    public async Task<object> CheckoutAsync(string reference, bool create, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(reference)) return new { error = "A branch, tag or commit is required." };
        if (LooksLikeFlag(reference)) return new { error = $"'{reference}' is not a revision." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = create ? $"switch -c {Q(reference)}" : $"switch {Q(reference)}";
        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);

        // git switch only exists on recent clients; fall back to checkout when it is not understood.
        if (exitCode != 0 &&
            (stderr.Contains("not a git command", StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase)))
        {
            var fallback = create ? $"checkout -b {Q(reference)}" : $"checkout {Q(reference)}";
            var (fallbackExit, _, fallbackOut, fallbackErr, _) = await GitCli.RunAsync(repoDir!, fallback, 60);
            if (fallbackExit != 0) return new { error = $"git checkout {reference} failed: {fallbackErr.Trim()}" };
            return new { repoDir, current = reference, created = create, output = ProcessRunner.Truncate((fallbackOut + fallbackErr).Trim(), 2000) };
        }

        if (exitCode != 0) return new { error = $"git switch {reference} failed: {stderr.Trim()}" };

        var (_, _, current, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);
        return new { repoDir, current = current.Trim(), created = create, output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000) };
    }

    // ── merge / rebase / cherry-pick / revert ────────────────────────────────
    public async Task<object> MergeAsync(string branch, string strategy, string? message, bool abort, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        if (abort)
        {
            var (abortExit, _, abortOut, abortErr, _) = await GitCli.RunAsync(repoDir!, "merge --abort", 60);
            if (abortExit != 0) return new { error = $"git merge --abort failed: {abortErr.Trim()}" };
            return new { repoDir, aborted = true, output = ProcessRunner.Truncate((abortOut + abortErr).Trim(), 2000) };
        }

        if (string.IsNullOrWhiteSpace(branch)) return new { error = "A branch or revision to merge is required." };
        if (LooksLikeFlag(branch)) return new { error = $"'{branch}' is not a revision." };

        var flag = (strategy ?? "default").Trim().ToLowerInvariant() switch
        {
            "default" or "" => "",
            "no-ff" or "noff" => " --no-ff",
            "ff-only" or "ffonly" => " --ff-only",
            "squash" => " --squash",
            _ => null,
        };

        if (flag is null) return new { error = $"Unknown strategy '{strategy}'. Use default, no-ff, ff-only or squash." };

        var args = "merge" + flag;
        if (!string.IsNullOrWhiteSpace(message)) args += $" -m {Q(message!)}";
        args += $" {Q(branch)}";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 120);
        if (timedOut) return new { error = "git merge timed out." };

        var conflicts = await ConflictsAsync(repoDir!);
        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

        if (exitCode != 0)
            return new
            {
                merged = false,
                command = $"git {args}",
                conflicts,
                error = conflicts.Count > 0
                    ? $"Merge stopped with {conflicts.Count} conflict(s). Resolve them and commit, or call git_merge with abort=true."
                    : $"git merge failed: {stderr.Trim()}",
                output,
            };

        var squashed = flag.Contains("--squash");
        return new
        {
            merged = true,
            branch,
            strategy = strategy ?? "default",
            squashed,
            note = squashed ? "Squash merge: the changes are staged but not committed — commit them yourself." : null,
            command = $"git {args}",
            output,
        };
    }

    public async Task<object> RebaseAsync(string? onto, string action, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "start").Trim().ToLowerInvariant();
        var args = verb switch
        {
            "start" => string.IsNullOrWhiteSpace(onto) ? null : $"rebase {Q(onto!)}",
            "continue" => "rebase --continue",
            "abort" => "rebase --abort",
            "skip" => "rebase --skip",
            _ => null,
        };

        if (args is null)
            return new
            {
                error = verb == "start"
                    ? "A revision to rebase onto is required."
                    : $"Unknown action '{action}'. Use start, continue, abort or skip.",
            };

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 180);
        if (timedOut) return new { error = "git rebase timed out (resolve conflicts, then action=continue)." };

        var conflicts = await ConflictsAsync(repoDir!);
        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

        if (exitCode != 0)
            return new
            {
                rebased = false,
                action = verb,
                command = $"git {args}",
                conflicts,
                error = conflicts.Count > 0
                    ? $"Rebase stopped with {conflicts.Count} conflict(s). Resolve them and call action=continue, or action=abort."
                    : $"git {args} failed: {stderr.Trim()}",
                output,
            };

        var (_, _, branch, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);
        return new { rebased = true, action = verb, onto, branch = branch.Trim(), command = $"git {args}", output };
    }

    public async Task<object> CherryPickAsync(string[] commits, string action, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "apply").Trim().ToLowerInvariant();
        string? args;
        if (verb is "continue" or "abort" or "skip")
            args = $"cherry-pick --{verb}";
        else if (commits is { Length: > 0 })
        {
            if (commits.Any(LooksLikeFlag)) return new { error = "A commit is a hash or ref; it never starts with '-'." };
            args = "cherry-pick " + string.Join(" ", commits.Select(Q));
        }
        else return new { error = "Pass one or more commits, or action=continue|abort|skip." };

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 180);
        if (timedOut) return new { error = "git cherry-pick timed out." };

        var conflicts = await ConflictsAsync(repoDir!);
        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

        if (exitCode != 0)
            return new
            {
                picked = false,
                action = verb,
                conflicts,
                error = conflicts.Count > 0
                    ? $"Cherry-pick stopped with {conflicts.Count} conflict(s). Resolve them and call action=continue, or action=abort."
                    : $"git cherry-pick failed: {stderr.Trim()}",
                output,
            };

        return new { picked = true, action = verb, commits, output };
    }

    public async Task<object> RevertAsync(string[] commits, string? repoPath)
    {
        if (commits is not { Length: > 0 }) return new { error = "One or more commits to revert are required." };
        if (commits.Any(LooksLikeFlag)) return new { error = "A commit is a hash or ref; it never starts with '-'." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = "revert --no-edit " + string.Join(" ", commits.Select(Q));
        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 180);
        if (timedOut) return new { error = "git revert timed out." };

        var conflicts = await ConflictsAsync(repoDir!);
        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

        if (exitCode != 0)
            return new
            {
                reverted = false,
                conflicts,
                error = conflicts.Count > 0
                    ? $"Revert stopped with {conflicts.Count} conflict(s); resolve them and commit, or run 'git revert --abort' via git_run."
                    : $"git revert failed: {stderr.Trim()}",
                output,
            };

        return new { reverted = true, commits, output };
    }

    public async Task<object> ResetAsync(string target, string mode, bool confirm, string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(target)) return new { error = "A revision to reset to is required." };
        if (LooksLikeFlag(target)) return new { error = $"'{target}' is not a revision." };

        var how = (mode ?? "mixed").Trim().ToLowerInvariant();
        if (how is not ("soft" or "mixed" or "hard")) return new { error = "mode must be soft, mixed or hard." };

        if (how == "hard" && !confirm)
            return new { error = "A hard reset discards every uncommitted change. Pass confirm=true to proceed.", target, mode = how };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var args = $"reset --{how} {Q(target)}";
        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);
        if (exitCode != 0) return new { error = $"git reset failed: {stderr.Trim()}", command = $"git {args}" };

        var (_, _, head, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --short HEAD", 20);
        return new
        {
            repoDir,
            mode = how,
            target,
            head = head.Trim(),
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000),
            note = how switch
            {
                "soft" => "Changes from the discarded commits are staged.",
                "mixed" => "Changes from the discarded commits are left in the working tree, unstaged.",
                _ => "Working tree and index were reset: uncommitted work is gone.",
            },
        };
    }

    // ── stash / tags / remotes / sync ────────────────────────────────────────
    public async Task<object> StashAsync(string action, string? message, string? name, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "push").Trim().ToLowerInvariant();
        var reference = string.IsNullOrWhiteSpace(name) ? "" : $" {Q(name!)}";

        var args = verb switch
        {
            "push" or "save" => "stash push" + (string.IsNullOrWhiteSpace(message) ? "" : $" -m {Q(message!)}"),
            "list" => "stash list",
            "pop" => "stash pop" + reference,
            "apply" => "stash apply" + reference,
            "drop" => "stash drop" + reference,
            "show" => "stash show -p" + reference,
            "clear" => "stash clear",
            _ => null,
        };

        if (args is null)
            return new { error = $"Unknown action '{action}'. Use push, list, pop, apply, drop, show or clear." };

        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);
        if (exitCode != 0) return new { error = $"git {args} failed: {stderr.Trim()}", command = $"git {args}" };

        var (_, _, list, _, _) = await GitCli.RunAsync(repoDir!, "stash list", 20);
        return new
        {
            action = verb,
            command = $"git {args}",
            entries = list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList(),
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 12_000),
        };
    }

    public async Task<object> TagAsync(string action, string? name, string? message, string? reference, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "list").Trim().ToLowerInvariant();

        if (verb == "list")
        {
            const string fmt = "--format=%(refname:short)%1f%(objectname:short)%1f%(creatordate:short)%1f%(subject)";
            var (listExit, _, listOut, listErr, _) = await GitCli.RunAsync(repoDir!, $"tag -l {fmt}", 20);
            if (listExit != 0) return new { error = $"git tag failed: {listErr.Trim()}" };

            var tags = new List<object>();
            foreach (var raw in listOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = raw.TrimEnd('\r').Split(UnitSep);
                if (fields.Length < 4) continue;
                tags.Add(new { name = fields[0], commit = fields[1], date = fields[2], subject = fields[3] });
            }

            return new { repoDir, count = tags.Count, tags };
        }

        if (string.IsNullOrWhiteSpace(name)) return new { error = "A tag name is required." };
        if (LooksLikeFlag(name)) return new { error = $"'{name}' is not a tag name." };

        var target = string.IsNullOrWhiteSpace(reference) ? "" : $" {Q(reference!)}";
        var args = verb switch
        {
            "create" or "add" => string.IsNullOrWhiteSpace(message)
                ? $"tag {Q(name!)}{target}"
                : $"tag -a {Q(name!)} -m {Q(message!)}{target}",
            "delete" or "remove" => $"tag -d {Q(name!)}",
            _ => null,
        };

        if (args is null) return new { error = $"Unknown action '{action}'. Use list, create or delete." };

        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (exitCode != 0) return new { error = $"git {args} failed: {stderr.Trim()}", command = $"git {args}" };

        return new { repoDir, action = verb, tag = name, command = $"git {args}", output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000) };
    }

    public async Task<object> RemoteAsync(string action, string? name, string? url, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "list").Trim().ToLowerInvariant();
        if (verb == "list") return await RemotesAsync(repoDir);

        if (string.IsNullOrWhiteSpace(name)) return new { error = "A remote name is required (e.g. 'origin')." };
        if (LooksLikeFlag(name)) return new { error = $"'{name}' is not a remote name." };

        var args = verb switch
        {
            "add" => string.IsNullOrWhiteSpace(url) ? null : $"remote add {Q(name!)} {Q(url!)}",
            "remove" or "rm" => $"remote remove {Q(name!)}",
            "set-url" => string.IsNullOrWhiteSpace(url) ? null : $"remote set-url {Q(name!)} {Q(url!)}",
            _ => null,
        };

        if (args is null)
            return new
            {
                error = verb is "add" or "set-url"
                    ? "A url is required for this action."
                    : $"Unknown action '{action}'. Use list, add, remove or set-url.",
            };

        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 30);
        if (exitCode != 0) return new { error = $"git {args} failed: {stderr.Trim()}" };

        return new { repoDir, action = verb, remote = name, command = $"git {args}", output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000) };
    }

    public async Task<object> SyncAsync(string action, string? remote, string? branch, bool tags, bool force, bool confirm, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "fetch").Trim().ToLowerInvariant();
        if (verb is not ("fetch" or "pull" or "push"))
            return new { error = "action must be fetch, pull or push." };
        if (force && !confirm)
            return new { error = "A force push rewrites published history. Pass confirm=true to proceed.", action = verb };
        if (force && verb != "push")
            return new { error = "force only applies to push." };

        var args = verb;
        if (!string.IsNullOrWhiteSpace(remote)) args += $" {Q(remote!)}";
        if (verb is "pull" or "push" && !string.IsNullOrWhiteSpace(branch)) args += $" {Q(branch!)}";
        if (verb == "push" && tags) args += " --tags";
        if (verb == "push" && force) args += " --force-with-lease";

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 180);
        if (timedOut) return new { error = $"git {args} timed out (network?).", command = $"git {args}" };

        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 8000);
        if (exitCode != 0)
            return new
            {
                succeeded = false,
                action = verb,
                command = $"git {args}",
                error = $"git {args} failed: {stderr.Trim()}",
                output,
            };

        return new { repoDir, succeeded = true, action = verb, remote, branch, forced = force, command = $"git {args}", output };
    }

    // ── escape hatch ─────────────────────────────────────────────────────────
    public async Task<object> RunAsync(string args, string? repoPath, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(args)) return new { error = "Provide git arguments, e.g. 'log --oneline' or 'reflog'." };

        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 900) timeoutSeconds = 900;

        var (exitCode, timedOut, stdout, stderr, elapsed) = await GitCli.RunAsync(repoDir!, args, timeoutSeconds);
        if (timedOut) return new { error = $"git {args} timed out after {timeoutSeconds}s.", command = $"git {args}" };

        return new
        {
            repoDir,
            command = $"git {args}",
            exitCode,
            succeeded = exitCode == 0,
            elapsedMs = (int)elapsed.TotalMilliseconds,
            stdout = ProcessRunner.Truncate(stdout, 24_000),
            stderr = ProcessRunner.Truncate(stderr, 8000),
        };
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    private Task<(string? repoDir, object? error)> ResolveAsync(string? repoPath) =>
        GitCli.ResolveRepoAsync(repoPath, _workspace.LoadedSolutionPath);

    private static async Task<int> CountAsync(string repoDir, string reference)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, $"rev-list --count {reference}", 20);
        return exitCode == 0 && int.TryParse(stdout.Trim(), out var count) ? count : 0;
    }

    /// <summary>The files git reports as conflicted right now (empty when the tree is fine).</summary>
    private static async Task<List<object>> ConflictsAsync(string repoDir)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, "diff --name-only --diff-filter=U", 20);
        if (exitCode != 0) return [];

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (object)new { path = line.TrimEnd('\r') })
            .ToList();
    }

    private static bool LooksLikeFlag(string value) => value.TrimStart().StartsWith('-');

    private static string Q(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
