using System.Text;
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
    Task<object> RebaseAsync(string? onto, string action, string plan, string? repoPath);
    Task<object> CherryPickAsync(string[] commits, string action, string? repoPath);
    Task<object> RevertAsync(string[] commits, string? repoPath);
    Task<object> ResetAsync(string target, string mode, bool confirm, string? repoPath);

    // conflicts: a merge editor as data
    Task<object> ConflictsAsync(string action, string? path, string strategy, int? hunkIndex, string? content, bool stage, string? repoPath);

    // patches: named shelves on disk
    Task<object> PatchAsync(string action, string? name, string? target, string? path, bool reverse, bool check, bool confirm, string? directory, string? repoPath);

    // stash, tags, remotes
    Task<object> StashAsync(string action, string? message, string? name, string? repoPath);
    Task<object> TagAsync(string action, string? name, string? message, string? reference, string? repoPath);
    Task<object> RemoteAsync(string action, string? name, string? url, string? repoPath);
    Task<object> SyncAsync(string action, string? remote, string? branch, bool tags, bool force, bool confirm, string? repoPath);

    /// <summary>Escape hatch: any git command, verbatim, inside the repository.</summary>
    Task<object> RunAsync(string args, string? repoPath, int timeoutSeconds);

    // worktrees, bisect, submodules
    Task<object> WorktreeAsync(string action, string? path, string? branch, bool newBranch, bool force, string? repoPath);
    Task<object> BisectAsync(string action, string? bad, string? good, string? repoPath);
    Task<object> SubmoduleAsync(string action, string? repoPath);
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

        var conflicts = await UnmergedPathsAsync(repoDir!);
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

    public async Task<object> RebaseAsync(string? onto, string action, string plan, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "start").Trim().ToLowerInvariant();
        if (verb == "plan") return await RebasePlanAsync(repoDir!, onto, plan);

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
                    : $"Unknown action '{action}'. Use start, continue, abort, skip or plan.",
            };

        // An in-progress rebase reopens the commit message editor on continue/skip; without a
        // definitive editor the child would wait on a terminal that is not there.
        var env = verb is "continue" or "skip"
            ? new Dictionary<string, string> { ["GIT_EDITOR"] = "true" }
            : null;

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 180, env);
        if (timedOut) return new { error = "git rebase timed out (resolve conflicts, then action=continue)." };

        var conflicts = await UnmergedPathsAsync(repoDir!);
        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

        if (exitCode != 0)
            return new
            {
                rebased = false,
                action = verb,
                command = $"git {args}",
                conflicts,
                error = conflicts.Count > 0
                    ? $"Rebase stopped with {conflicts.Count} conflict(s). Resolve them with git_conflicts, then call action=continue; or action=abort."
                    : $"git {args} failed: {stderr.Trim()}",
                output,
            };

        var (_, _, branch, _, _) = await GitCli.RunAsync(repoDir!, "rev-parse --abbrev-ref HEAD", 20);
        return new { rebased = true, action = verb, onto, branch = branch.Trim(), command = $"git {args}", output };
    }

    /// <summary>
    /// A non-interactive <c>rebase -i</c>: the todo list is written by this call and handed to git
    /// through GIT_SEQUENCE_EDITOR, so the order and the action of every commit are explicit data
    /// rather than an editor session. No editor is ever opened.
    /// </summary>
    private async Task<object> RebasePlanAsync(string repoDir, string? onto, string plan)
    {
        if (string.IsNullOrWhiteSpace(onto))
            return new { error = "A base revision is required for a plan: the commit BEFORE the first one in the plan (e.g. HEAD~4 or main)." };
        if (LooksLikeFlag(onto)) return new { error = $"'{onto}' is not a revision." };
        if (string.IsNullOrWhiteSpace(plan)) return new { error = "A plan is required: [{ \"commit\": \"abc123\", \"action\": \"pick\" }, …]." };

        List<RebaseStep>? steps;
        try
        {
            steps = System.Text.Json.JsonSerializer.Deserialize<List<RebaseStep>>(plan,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new { error = $"The plan is not valid JSON: {ex.Message}", expected = "[{ \"commit\": \"hash\", \"action\": \"pick|reword|squash|fixup|drop\", \"message\": \"…\" }]" };
        }

        if (steps is not { Count: > 0 })
            return new { error = "The plan is empty: give at least one step." };

        var todo = new System.Text.StringBuilder();
        var tempFiles = new List<string>();

        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var sha = step.Commit?.Trim();
                if (string.IsNullOrWhiteSpace(sha) || LooksLikeFlag(sha))
                    return new { error = $"Step {i + 1}: 'commit' must be a hash or revision.", step = i + 1 };

                var verb = (step.Action ?? "pick").Trim().ToLowerInvariant();
                switch (verb)
                {
                    case "pick":
                        todo.AppendLine($"pick {sha}");
                        break;

                    case "drop":
                        todo.AppendLine($"drop {sha}");
                        break;

                    case "reword":
                        if (string.IsNullOrWhiteSpace(step.Message))
                            return new { error = $"Step {i + 1}: reword needs a message.", step = i + 1, action = verb };
                        todo.AppendLine($"pick {sha}");
                        todo.AppendLine(ExecCommitMessage(repoDir, step.Message!, tempFiles));
                        break;

                    case "squash":
                    case "fixup":
                        // Deterministic fold: no editor. The previous commit's message survives
                        // unless this step carries its own.
                        todo.AppendLine($"fixup {sha}");
                        if (!string.IsNullOrWhiteSpace(step.Message))
                            todo.AppendLine(ExecCommitMessage(repoDir, step.Message!, tempFiles));
                        break;

                    default:
                        return new { error = $"Step {i + 1}: unknown action '{step.Action}'. Use pick, reword, squash, fixup or drop.", step = i + 1 };
                }
            }

            var todoFile = Path.Combine(Path.GetTempPath(), $"{_options.TempFilePrefix}-todo-{Guid.NewGuid():N}.txt");
            tempFiles.Add(todoFile);
            await File.WriteAllTextAsync(todoFile, todo.ToString());

            // git runs "$GIT_SEQUENCE_EDITOR <todoPath>" through sh, so a plain `cp` copies our
            // prepared list over the one git generated — on Windows, macOS and Linux alike.
            var forward = Path.GetFullPath(todoFile).Replace('\\', '/');
            var environment = new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = $"cp \"{forward}\"",
                ["GIT_EDITOR"] = "true",
            };

            var args = $"rebase -i {Q(onto)}";
            var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir, args, 240, environment);
            if (timedOut) return new { error = "git rebase -i timed out (resolve conflicts, then action=continue)." };

            var conflicts = await UnmergedPathsAsync(repoDir);
            var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);

            if (exitCode != 0)
                return new
                {
                    rebased = false,
                    action = "plan",
                    command = $"git {args}",
                    plan = steps.Select((s, i) => new { step = i + 1, commit = s.Commit, action = s.Action }).ToList(),
                    conflicts,
                    error = conflicts.Count > 0
                        ? $"The plan stopped with {conflicts.Count} conflict(s). Resolve them with git_conflicts, then call action=continue; or action=abort."
                        : $"git rebase -i failed: {stderr.Trim()}",
                    output,
                };

            var (_, _, branch, _, _) = await GitCli.RunAsync(repoDir, "rev-parse --abbrev-ref HEAD", 20);
            var (_, _, head, _, _) = await GitCli.RunAsync(repoDir, "rev-parse --short HEAD", 20);
            var (_, _, subjects, _, _) = await GitCli.RunAsync(repoDir, "log -n 15 --pretty=%h %s", 20);

            return new
            {
                rebased = true,
                action = "plan",
                onto,
                branch = branch.Trim(),
                head = head.Trim(),
                steps = steps.Count,
                dropped = steps.Count(s => string.Equals(s.Action, "drop", StringComparison.OrdinalIgnoreCase)),
                command = $"git {args}",
                history = subjects.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList(),
                output,
            };
        }
        finally
        {
            foreach (var file in tempFiles) { try { File.Delete(file); } catch { /* best effort */ } }
        }
    }

    /// <summary>`exec git commit --amend -F "…"` for one prepared message file.</summary>
    private string ExecCommitMessage(string repoDir, string message, List<string> tempFiles)
    {
        var file = Path.Combine(Path.GetTempPath(), $"{_options.TempFilePrefix}-msg-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, message);
        tempFiles.Add(file);
        var forward = Path.GetFullPath(file).Replace('\\', '/');
        return $"exec git commit --amend -F \"{forward}\"";
    }

    /// <summary>One step of a rebase plan; the JSON shape of <c>git_rebase</c> action=plan.</summary>
    public sealed class RebaseStep
    {
        public string? Commit { get; set; }
        public string? Action { get; set; }
        public string? Message { get; set; }
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

        var conflicts = await UnmergedPathsAsync(repoDir!);
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

        var conflicts = await UnmergedPathsAsync(repoDir!);
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

    // ── worktrees ─────────────────────────────────────────────────────────────
    /// <summary>List/add/remove worktrees; the porcelain list output is parsed into structured rows.</summary>
    public async Task<object> WorktreeAsync(string action, string? path, string? branch, bool newBranch, bool force, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "list").Trim().ToLowerInvariant();

        if (verb is "list" or "prune")
        {
            var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, $"worktree {verb} --porcelain", 30);
            if (exitCode != 0) return new { error = $"git worktree {verb} failed: {stderr.Trim()}" };

            if (verb == "prune")
                return new { repoDir, action = verb, pruned = true, output = ProcessRunner.Truncate(stdout.Trim(), 2000) };

            var worktrees = ParseWorktrees(stdout);
            return new { repoDir, count = worktrees.Count, worktrees };
        }

        if (verb == "add")
        {
            if (string.IsNullOrWhiteSpace(path)) return new { error = "A target path for the new worktree is required." };
            if (LooksLikeFlag(path)) return new { error = $"'{path}' is not a path." };

            var args = "worktree add";
            if (!string.IsNullOrWhiteSpace(branch))
            {
                if (LooksLikeFlag(branch)) return new { error = $"'{branch}' is not a branch name." };
                args += newBranch ? $" -b {Q(branch!)}" : $" {Q(branch!)}";
            }
            args += $" {Q(path!)}";

            var (addExit, _, addOut, addErr, _) = await GitCli.RunAsync(repoDir!, args, 60);
            if (addExit != 0) return new { error = $"git {args} failed: {addErr.Trim()}", command = $"git {args}" };

            return new
            {
                repoDir, added = true, path, branch, newBranch,
                command = $"git {args}",
                output = ProcessRunner.Truncate((addOut + addErr).Trim(), 2000),
            };
        }

        if (verb == "remove")
        {
            if (string.IsNullOrWhiteSpace(path)) return new { error = "The worktree path to remove is required." };
            if (LooksLikeFlag(path)) return new { error = $"'{path}' is not a path." };

            var args = $"worktree remove {Q(path!)}{(force ? " --force" : "")}";
            var (rmExit, _, rmOut, rmErr, _) = await GitCli.RunAsync(repoDir!, args, 60);
            if (rmExit != 0)
                return new
                {
                    error = $"git {args} failed: {rmErr.Trim()}",
                    hint = force ? null : "The worktree may hold uncommitted changes; pass force=true to remove it anyway.",
                    command = $"git {args}",
                };

            return new { repoDir, removed = true, path, force, command = $"git {args}", output = ProcessRunner.Truncate((rmOut + rmErr).Trim(), 2000) };
        }

        return new { error = $"Unknown action '{action}'. Use list, add, remove or prune." };
    }

    /// <summary>Parses <c>git worktree list --porcelain</c> into one row per worktree.</summary>
    private static List<object> ParseWorktrees(string porcelain)
    {
        var worktrees = new List<object>();
        string? path = null, branch = null, head = null;
        var detached = false;

        void Flush()
        {
            if (path is not null)
                worktrees.Add(new { path, branch = detached ? null : branch, head, detached });
            path = branch = head = null;
            detached = false;
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..];
            else if (line == "detached") detached = true;
        }
        Flush();
        return worktrees;
    }

    // ── bisect ────────────────────────────────────────────────────────────────
    public async Task<object> BisectAsync(string action, string? bad, string? good, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "log").Trim().ToLowerInvariant();
        if (bad is { } b && LooksLikeFlag(b)) return new { error = $"'{bad}' is not a revision." };
        if (good is { } g && LooksLikeFlag(g)) return new { error = $"'{good}' is not a revision." };

        string? args = verb switch
        {
            "start" => "bisect start" +
                       (string.IsNullOrWhiteSpace(bad) ? "" : $" {Q(bad!)}") +
                       (string.IsNullOrWhiteSpace(good) ? "" : $" {Q(good!)}"),
            "good" => "bisect good" + (string.IsNullOrWhiteSpace(good) ? "" : $" {Q(good!)}"),
            "bad" => "bisect bad" + (string.IsNullOrWhiteSpace(bad) ? "" : $" {Q(bad!)}"),
            "skip" => "bisect skip",
            "reset" => "bisect reset",
            "log" => "bisect log",
            "view" => "bisect view",
            _ => null,
        };

        if (args is null)
            return new { error = $"Unknown action '{action}'. Use start, good, bad, skip, log, view or reset." };

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);
        if (timedOut) return new { error = $"git {args} timed out." };

        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 4000);
        if (exitCode != 0)
            return new { succeeded = false, action = verb, command = $"git {args}", error = $"git {args} failed: {stderr.Trim()}", output };

        // When marking a commit finishes the bisection, git says "X is the first bad commit".
        var finished = verb is "good" or "bad" or "skip" &&
                       (stdout + stderr).Contains("first bad commit", StringComparison.OrdinalIgnoreCase);

        return new
        {
            repoDir, succeeded = true, action = verb, command = $"git {args}", finished, output,
            note = finished ? "The bisection is complete; call git_bisect with action=reset to leave the bisecting state." : null,
        };
    }

    // ── submodules ────────────────────────────────────────────────────────────
    public async Task<object> SubmoduleAsync(string action, string? repoPath)
    {
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var verb = (action ?? "status").Trim().ToLowerInvariant();
        var args = verb switch
        {
            "status" => "submodule status",
            "update" => "submodule update --init --recursive",
            "init" => "submodule init",
            "sync" => "submodule sync",
            "deinit" => "submodule deinit --all",
            _ => "",
        };
        if (args.Length == 0)
            return new { error = $"Unknown action '{action}'. Use status, update, init, sync or deinit." };

        var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 120);
        if (timedOut) return new { error = $"git {args} timed out (network for update/sync?)." };

        var output = ProcessRunner.Truncate((stdout + stderr).Trim(), 12_000);
        if (exitCode != 0)
            return new { succeeded = false, action = verb, command = $"git {args}", error = $"git {args} failed: {stderr.Trim()}", output };

        var submodules = new List<object>();
        if (verb == "status")
        {
            foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length < 2) continue;
                var state = line[0] switch
                {
                    ' ' => "clean",
                    '+' => "uninitialized",
                    '-' => "not-initialized",
                    'U' => "merge-conflict",
                    _ => "other",
                };
                var content = line.TrimStart();
                var space = content.IndexOf(' ');
                if (space < 0) continue;
                submodules.Add(new { sha = content[..space], path = content[(space + 1)..], state });
            }
        }

        return new { repoDir, succeeded = true, action = verb, command = $"git {args}", count = submodules.Count, submodules, output };
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

    // ── conflicts ────────────────────────────────────────────────────────────
    public async Task<object> ConflictsAsync(string action, string? path, string strategy, int? hunkIndex,
        string? content, bool stage, string? repoPath)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        if (verb == "list")
        {
            var files = new List<object>();
            foreach (var relative in await UnmergedFileListAsync(repoDir!))
                files.Add(Summarize(await ParseConflictFileAsync(repoDir!, relative)));

            return new
            {
                repoDir,
                operation = await OperationInProgressAsync(repoDir!),
                conflictCount = files.Count,
                files,
                note = files.Count == 0
                    ? "No conflicted files: there is no merge, rebase, cherry-pick or revert waiting for a resolution."
                    : "Resolve a whole file or one hunk with action=resolve, then commit (merge) or action=continue (rebase/cherry-pick).",
            };
        }

        if (verb == "show")
        {
            if (string.IsNullOrWhiteSpace(path)) return new { error = "A file path is required for show." };
            var (full, relative, pathError) = ResolveInsideRepo(repoDir!, path!);
            if (pathError is not null) return pathError;
            if (!File.Exists(full)) return new { error = $"File not found in the working tree: {relative}" };

            var parsed = await ParseConflictFileAsync(repoDir!, relative);
            return new
            {
                repoDir,
                path = relative,
                parsed.Binary,
                parsed.TooLarge,
                parsed.LineCount,
                conflictCount = parsed.Hunks.Count,
                hunks = parsed.Hunks.Select(hunk => new
                {
                    index = hunk.Index,
                    startLine = hunk.StartLine,
                    endLine = hunk.EndLine,
                    oursLabel = hunk.OursLabel,
                    baseLabel = hunk.BaseLabel,
                    theirsLabel = hunk.TheirsLabel,
                    oursLineCount = hunk.OursLineCount,
                    baseLineCount = hunk.BaseLineCount,
                    theirsLineCount = hunk.TheirsLineCount,
                    ours = ProcessRunner.Truncate(string.Join("\n", hunk.Ours), 6000),
                    baseText = ProcessRunner.Truncate(string.Join("\n", hunk.Base), 6000),
                    theirs = ProcessRunner.Truncate(string.Join("\n", hunk.Theirs), 6000),
                    text = ProcessRunner.Truncate(ConflictModel.Render(hunk), 12_000),
                }).ToList(),
            };
        }

        if (verb != "resolve")
            return new { error = $"Unknown action '{action}'. Use list, show or resolve." };

        if (string.IsNullOrWhiteSpace(path)) return new { error = "A file path is required for resolve." };

        var (targetFile, targetRelative, resolveError) = ResolveInsideRepo(repoDir!, path!);
        if (resolveError is not null) return resolveError;
        if (!File.Exists(targetFile)) return new { error = $"File not found in the working tree: {targetRelative}" };

        var (text, encoding) = await ReadTextPreservingBomAsync(targetFile);
        var (hunks, _, _) = ConflictModel.Parse(text);
        if (hunks.Count == 0)
            return new
            {
                error = "No conflict markers in this file: it is already resolved. " +
                        "If git still lists it as unmerged, stage it with git_stage.",
                path = targetRelative,
            };

        var chosen = string.IsNullOrEmpty(content)
            ? (strategy ?? string.Empty).Trim().ToLowerInvariant()
            : ConflictModel.Custom;

        if (chosen.Length == 0)
            return new
            {
                error = "A strategy is required: ours, theirs, both (ours then theirs), base (needs merge.conflictStyle=diff3) or custom (with content).",
                path = targetRelative,
                hunks = hunks.Count,
            };

        if (!ConflictModel.IsStrategy(chosen))
            return new { error = $"Unknown strategy '{strategy}'. Use ours, theirs, both, base or custom.", path = targetRelative };

        if (chosen == ConflictModel.Base && hunks.Any(h => !h.HasBase))
            return new
            {
                error = "This file has no ||||||| base section, so 'base' cannot be chosen. " +
                        "Set merge.conflictStyle=diff3 (or zdiff3) and redo the merge to get one.",
                path = targetRelative,
            };

        if (hunkIndex is { } index && (index < 0 || index >= hunks.Count))
            return new
            {
                error = $"There is no hunk {index} in this file: it has {hunks.Count} (0-based).",
                path = targetRelative,
                hunks = hunks.Count,
            };

        var (rewritten, resolved, remaining) = ConflictModel.Resolve(text, hunks, chosen, hunkIndex, content);
        await File.WriteAllTextAsync(targetFile, rewritten, encoding);

        var staged = false;
        string? stageError = null;
        if (remaining == 0 && stage)
        {
            var (addExit, _, _, addErr, _) = await GitCli.RunAsync(repoDir!, $"add -- {Q(targetRelative)}", 30);
            staged = addExit == 0;
            if (addExit != 0) stageError = addErr.Trim();
        }

        return new
        {
            repoDir,
            path = targetRelative,
            strategy = chosen,
            resolvedHunks = resolved,
            remainingHunks = remaining,
            staged,
            stageError,
            operation = await OperationInProgressAsync(repoDir!),
            note = remaining == 0 && !stage
                ? "The file has no markers left; stage it with git_stage to mark it resolved."
                : remaining > 0
                    ? "Other hunks remain in this file: call resolve again (optionally with blockIndex) or resolve the rest at once."
                    : null,
        };
    }

    /// <summary>One file's conflict summary, as <c>git_conflicts action=list</c> reports it.</summary>
    private static object Summarize(ParsedConflictFile parsed) => new
    {
        path = parsed.Path,
        binary = parsed.Binary,
        tooLarge = parsed.TooLarge,
        lineCount = parsed.LineCount,
        hunks = parsed.Hunks.Count,
        preview = parsed.Hunks.Count > 0
            ? parsed.Hunks.Select(h => new
            {
                index = h.Index,
                startLine = h.StartLine,
                endLine = h.EndLine,
                oursLabel = h.OursLabel,
                theirsLabel = h.TheirsLabel,
                oursLineCount = h.OursLineCount,
                theirsLineCount = h.TheirsLineCount,
                oursFirstLine = h.Ours.FirstOrDefault(),
                theirsFirstLine = h.Theirs.FirstOrDefault(),
            }).ToList()
            : null,
    };

    private static async Task<ParsedConflictFile> ParseConflictFileAsync(string repoDir, string relative)
    {
        var full = Path.Combine(repoDir, relative.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(full);
        if (!info.Exists) return new ParsedConflictFile(relative, false, false, 0, []);
        if (info.Length > ConflictModel.MaxParseBytes) return new ParsedConflictFile(relative, false, true, 0, []);

        var bytes = await File.ReadAllBytesAsync(full);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return new ParsedConflictFile(relative, true, false, 0, []);

        var text = DecodeText(bytes, out _);
        var (hunks, lines, _) = ConflictModel.Parse(text);
        return new ParsedConflictFile(relative, false, false, lines.Length, hunks);
    }

    /// <summary>Reads a text file and remembers the encoding its BOM asked for, so a rewrite keeps it.</summary>
    private static async Task<(string Text, Encoding Encoding)> ReadTextPreservingBomAsync(string full)
    {
        var bytes = await File.ReadAllBytesAsync(full);
        var text = DecodeText(bytes, out var encoding);
        return (text, encoding);
    }

    private static string DecodeText(byte[] bytes, out Encoding encoding)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Paths the user gives are always relative to (or inside) the repository being acted on.</summary>
    private static (string Full, string Relative, object? Error) ResolveInsideRepo(string repoDir, string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repoDir, path));
        var relative = Path.GetRelativePath(repoDir, full);

        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            return (full, relative, new { error = $"'{path}' is outside the repository ({repoDir})." });

        return (full, relative.Replace('\\', '/'), null);
    }

    /// <summary>"merge", "rebase", "cherry-pick", "revert" or null — what the repository is in the middle of.</summary>
    private static async Task<string?> OperationInProgressAsync(string repoDir)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, "rev-parse --absolute-git-dir", 20);
        if (exitCode != 0) return null;

        var gitDir = stdout.Trim();
        if (gitDir.Length == 0) return null;

        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) ||
            Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "rebase";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        return null;
    }

    // ── patches ──────────────────────────────────────────────────────────────
    public async Task<object> PatchAsync(string action, string? name, string? target, string? path,
        bool reverse, bool check, bool confirm, string? directory, string? repoPath)
    {
        var verb = (action ?? "list").Trim().ToLowerInvariant();
        var (repoDir, error) = await ResolveAsync(repoPath);
        if (error is not null) return error;

        var patches = await PatchDirectoryAsync(repoDir!, directory);
        if (patches.Error is not null) return patches.Error;

        switch (verb)
        {
            case "create":
            {
                var source = (target ?? "working").Trim();
                var args = "diff";
                if (string.Equals(source, "staged", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(source, "cached", StringComparison.OrdinalIgnoreCase))
                    args += " --cached";
                else if (!string.Equals(source, "working", StringComparison.OrdinalIgnoreCase) && source.Length > 0)
                {
                    if (LooksLikeFlag(source)) return new { error = $"'{source}' is not a revision or range." };
                    args += $" {source}";
                }

                if (!string.IsNullOrWhiteSpace(path)) args += $" -- {Q(path!)}";

                var (exitCode, timedOut, stdout, stderr, _) = await GitCli.RunAsync(repoDir!, args, 60);
                if (timedOut) return new { error = "git diff timed out." };
                if (exitCode != 0) return new { error = $"git diff failed: {stderr.Trim()}", command = $"git {args}" };
                if (string.IsNullOrWhiteSpace(stdout)) return new { error = "There is no diff to capture: the tree (or the range) is empty.", source, command = $"git {args}" };

                var (fileName, nameError) = PatchFileName(name);
                if (nameError is not null) return nameError;

                var full = Path.Combine(patches.Directory!, fileName);
                await File.WriteAllTextAsync(full, stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                return new
                {
                    repoDir,
                    action = "create",
                    name = fileName,
                    path = full,
                    source,
                    bytes = new FileInfo(full).Length,
                    files = PatchFiles(stdout),
                    command = $"git {args}",
                    note = "Apply it later with git_patch action=apply (and reverse=true to undo it).",
                };
            }

            case "list":
            {
                var entries = new DirectoryInfo(patches.Directory!)
                    .EnumerateFiles()
                    .Where(f => f.Extension is ".patch" or ".diff")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => (object)new
                    {
                        name = f.Name,
                        path = f.FullName,
                        bytes = f.Length,
                        modified = f.LastWriteTimeUtc.ToString("u"),
                        files = CountPatchFiles(f.FullName),
                    })
                    .ToList();

                return new { repoDir, directory = patches.Directory, count = entries.Count, patches = entries };
            }

            case "show":
            {
                var (full, fileError) = ResolvePatch(patches.Directory!, name);
                if (fileError is not null) return fileError;
                if (!File.Exists(full)) return new { error = $"Patch not found: {name}", directory = patches.Directory };

                var text = await File.ReadAllTextAsync(full);
                return new
                {
                    path = full,
                    name = Path.GetFileName(full),
                    bytes = new FileInfo(full).Length,
                    files = PatchFiles(text),
                    text = ProcessRunner.Truncate(text, 32_000),
                };
            }

            case "apply":
            {
                var (full, fileError) = ResolvePatch(patches.Directory!, name);
                if (fileError is not null) return fileError;
                if (!File.Exists(full)) return new { error = $"Patch not found: {name}", directory = patches.Directory };

                var baseArgs = "apply" + (check ? " --check" : "") + (reverse ? " --reverse" : "");
                var first = await GitCli.RunAsync(repoDir!, $"{baseArgs} {Q(full)}", 120);
                if (first.timedOut) return new { error = "git apply timed out." };

                var usedThreeWay = false;
                var result = first;

                // A plain apply is all-or-nothing; --3way merges the patch when the context moved.
                if (first.exitCode != 0 && !check)
                {
                    var retry = await GitCli.RunAsync(repoDir!, $"{baseArgs} --3way {Q(full)}", 120);
                    if (retry.timedOut) return new { error = "git apply --3way timed out." };
                    if (retry.exitCode == 0) { usedThreeWay = true; result = retry; }
                }

                var output = ProcessRunner.Truncate((result.stdout + result.stderr).Trim(), 4000);
                if (result.exitCode != 0)
                {
                    var conflicts = await UnmergedPathsAsync(repoDir!);
                    return new
                    {
                        applied = false,
                        checkOnly = check,
                        reversed = reverse,
                        name = Path.GetFileName(full),
                        command = $"git {baseArgs} {Q(full)}",
                        conflicts,
                        error = conflicts.Count > 0
                            ? $"The 3-way apply stopped with {conflicts.Count} conflict(s). Resolve them with git_conflicts."
                            : $"git apply failed: {result.stderr.Trim()}",
                        hint = reverse ? null : "The tree may already contain this change (try reverse=true) or hold conflicting edits.",
                        output,
                    };
                }

                return new
                {
                    applied = !check,
                    checkOnly = check,
                    reversed = reverse,
                    threeWay = usedThreeWay,
                    name = Path.GetFileName(full),
                    files = PatchFiles(await File.ReadAllTextAsync(full)),
                    command = $"git {baseArgs}{(usedThreeWay ? " --3way" : "")} {Q(full)}",
                    firstAttempt = usedThreeWay ? ProcessRunner.Truncate((first.stdout + first.stderr).Trim(), 2000) : null,
                    output,
                };
            }

            case "delete":
            {
                var (full, fileError) = ResolvePatch(patches.Directory!, name);
                if (fileError is not null) return fileError;
                if (!File.Exists(full)) return new { error = $"Patch not found: {name}", directory = patches.Directory };
                if (!confirm) return new { error = "Deleting a patch is irreversible. Pass confirm=true to proceed.", name = Path.GetFileName(full) };

                File.Delete(full);
                return new { deleted = true, name = Path.GetFileName(full), directory = patches.Directory };
            }

            default:
                return new { error = $"Unknown action '{action}'. Use create, list, show, apply or delete." };
        }
    }

    /// <summary>The patch folder: <paramref name="directory"/> when given, else <c>&lt;git-dir&gt;/visualmcp-patches</c>.</summary>
    private static async Task<(string? Directory, object? Error)> PatchDirectoryAsync(string repoDir, string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var full = Path.GetFullPath(directory!);
            Directory.CreateDirectory(full);
            return (full, null);
        }

        var (exitCode, _, stdout, stderr, _) = await GitCli.RunAsync(repoDir, "rev-parse --absolute-git-dir", 20);
        if (exitCode != 0) return (null, new { error = $"Could not locate the git directory: {stderr.Trim()}" });

        var dir = Path.Combine(stdout.Trim(), "visualmcp-patches");
        Directory.CreateDirectory(dir);
        return (dir, null);
    }

    private static (string Name, object? Error) PatchFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ($"patch-{DateTime.UtcNow:yyyyMMdd-HHmmss}.patch", null);

        var trimmed = name!.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains("..") || Path.IsPathRooted(trimmed))
            return (trimmed, new { error = "A patch name is a file name, not a path: no separators and no '..'." });

        if (!trimmed.EndsWith(".patch", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.EndsWith(".diff", StringComparison.OrdinalIgnoreCase))
            trimmed += ".patch";

        return (trimmed, null);
    }

    private static (string Full, object? Error) ResolvePatch(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", new { error = "A patch name is required (see action=list)." });

        var (fileName, nameError) = PatchFileName(name);
        if (nameError is not null) return ("", nameError);

        return (Path.Combine(directory, fileName), null);
    }

    /// <summary>The files a patch touches, read from its <c>diff --git</c> headers.</summary>
    private static List<string> PatchFiles(string patchText) =>
        patchText.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("diff --git ", StringComparison.Ordinal))
            .Select(line =>
            {
                var parts = line["diff --git ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return line;
                var first = parts[0];
                return first.StartsWith("a/", StringComparison.Ordinal) ? first[2..] : first;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static int CountPatchFiles(string full)
    {
        try { return PatchFiles(File.ReadAllText(full)).Count; }
        catch (IOException) { return 0; }
    }

    /// <summary>The files git reports as conflicted right now (empty when the tree is fine).</summary>
    private static async Task<List<object>> UnmergedPathsAsync(string repoDir)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, "diff --name-only --diff-filter=U", 20);
        if (exitCode != 0) return [];

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (object)new { path = line.TrimEnd('\r') })
            .ToList();
    }

    private static async Task<List<string>> UnmergedFileListAsync(string repoDir)
    {
        var (exitCode, _, stdout, _, _) = await GitCli.RunAsync(repoDir, "diff --name-only --diff-filter=U", 20);
        if (exitCode != 0) return [];

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();
    }

    private static bool LooksLikeFlag(string value) => value.TrimStart().StartsWith('-');

    private static string Q(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
