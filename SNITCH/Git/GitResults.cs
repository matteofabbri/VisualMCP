using System.Text.Json;

namespace Snitch.Git;

/// <summary>
/// The typed results of the git read operations. Following the typed-result convention: one record
/// per operation, every member nullable, the MCP serializer drops nulls so each branch keeps its
/// JSON; a shared <see cref="GitResult"/> base carries the error every refusal answers with, and
/// <see cref="GitError"/> is that refusal as an instance the resolved-root plumbing can hand up.
/// </summary>
public abstract record GitResult
{
    public string? Error { get; init; }
}

/// <summary>A refused git operation: the root could not be resolved or the command failed.</summary>
public sealed record GitError(string Message) : GitResult
{
    public override string ToString() => Message;
}

/// <summary>One staged or modified entry of <see cref="GitStatusResult"/>.</summary>
public sealed record GitChange(string Path, string Change);

/// <summary>One unmerged entry of <see cref="GitStatusResult"/>.</summary>
public sealed record GitConflict(string Path, string Code);

public sealed record GitStatusResult : GitResult
{
    public string? RepoDir { get; init; }
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public int? Ahead { get; init; }
    public int? Behind { get; init; }
    public bool? Clean { get; init; }
    public int? StagedCount { get; init; }
    public int? ModifiedCount { get; init; }
    public int? UntrackedCount { get; init; }
    public int? ConflictedCount { get; init; }
    public List<GitChange>? Staged { get; init; }
    public List<GitChange>? Modified { get; init; }
    public List<string>? Untracked { get; init; }
    public List<GitConflict>? Conflicted { get; init; }

    public static GitStatusResult FromError(string message) => new() { Error = message };
}

/// <summary>One commit of <see cref="GitLogResult"/>.</summary>
public sealed record GitCommit(string Hash, string ShortHash, string Author, string Date, string Subject);

public sealed record GitLogResult : GitResult
{
    public string? RepoDir { get; init; }
    public string? PathFilter { get; init; }
    public int? Count { get; init; }
    public List<GitCommit>? Commits { get; init; }

    public static GitLogResult FromError(string message) => new() { Error = message };
}

public sealed record GitShowResult : GitResult
{
    public string? Rev { get; init; }
    public string? Hash { get; init; }
    public string? Author { get; init; }
    public string? Date { get; init; }
    public string? Body { get; init; }

    public static GitShowResult FromError(string message) => new() { Error = message };
}

public sealed record GitBlameResult : GitResult
{
    public string? RepoDir { get; init; }
    public string? Path { get; init; }
    public string? Range { get; init; }
    public string? Text { get; init; }

    public static GitBlameResult FromError(string message) => new() { Error = message };
}

/// <summary>One branch of <see cref="GitBranchesResult"/>.</summary>
public sealed record GitBranch(string Name, bool Current, string? Upstream, string? LastCommitDate, string? LastCommit);

public sealed record GitBranchesResult : GitResult
{
    public string? RepoDir { get; init; }
    public string? Current { get; init; }
    public int? Count { get; init; }
    public List<GitBranch>? Branches { get; init; }

    public static GitBranchesResult FromError(string message) => new() { Error = message };
}

/// <summary>One fetch/push url of <see cref="GitRemotesResult"/>.</summary>
public sealed record GitRemote(string Name, string Url, string Kind);

public sealed record GitRemotesResult : GitResult
{
    public string? RepoDir { get; init; }
    public int? Count { get; init; }
    public List<GitRemote>? Remotes { get; init; }

    public static GitRemotesResult FromError(string message) => new() { Error = message };
}

public sealed record GitDiffResult : GitResult
{
    public string? RepoDir { get; init; }
    public string? Target { get; init; }
    public string? PathFilter { get; init; }
    public bool? SummaryOnly { get; init; }
    public string? Command { get; init; }
    public bool? Empty { get; init; }
    public string? Diff { get; init; }

    public static GitDiffResult FromError(string message) => new() { Error = message };
}
