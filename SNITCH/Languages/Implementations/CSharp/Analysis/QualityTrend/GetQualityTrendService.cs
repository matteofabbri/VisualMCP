using System.IO.Compression;
using System.Text;
using Snitch.Execution;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Analysis.QualityTrend;

/// <summary>
/// The quality trend between two points in history. Each side is read as raw source — a commit through
/// <c>git archive</c>, the working tree from disk — and measured with <see cref="SourceQualityReader"/>,
/// so no compilation is needed for a revision that cannot be built in place. The deltas then come from
/// <see cref="QualityTrendComparer"/>, which is also what the tests exercise without git.
/// </summary>
public sealed class GetQualityTrendService : IQualityTrendService
{
    /// <summary>A guard, not a policy: a huge repository is measured in slices with <c>path</c>.</summary>
    private const int MaxFilesPerSide = 5000;

    private static readonly string[] SkippedFolders =
        [".git", "bin", "obj", "node_modules", ".vs", ".idea", ".artifacts", ".visualMCP", "TestResults"];

    private readonly IRoslynWorkspace _workspace;
    private volatile CacheEntry? _cache;

    public GetQualityTrendService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> MeasureAsync(
        string? fromRef,
        string? toRef,
        string? repoPath,
        string? path,
        int top,
        CancellationToken cancellationToken = default)
    {
        var limit = top <= 0 ? 10 : top;
        var fromValue = string.IsNullOrWhiteSpace(fromRef) ? "HEAD~1" : fromRef.Trim();
        var toValue = string.IsNullOrWhiteSpace(toRef) ? "HEAD" : toRef.Trim();
        var filter = NormalizeFilter(path);

        var repository = ResolveRepository(repoPath);
        if (repository is null)
            return new
            {
                error = repoPath is null
                    ? "No solution is loaded, so the repository cannot be located; pass repoPath."
                    : $"'{repoPath}' is not inside a git repository.",
            };

        var key = $"{repository}|{fromValue}|{toValue}|{filter ?? "*"}|{limit}";
        if (_cache is { } cached && string.Equals(cached.Key, key, StringComparison.Ordinal))
            return cached.Result;

        var fromIsWorktree = IsWorktree(fromValue);
        var toIsWorktree = IsWorktree(toValue);

        if (fromIsWorktree && toIsWorktree)
            return new { error = "from and to are both the working tree: there is nothing to compare." };

        string? fromSha = null;
        string? toSha = null;

        if (!fromIsWorktree)
        {
            var (sha, error) = await RevParseAsync(repository, fromValue);
            if (error is not null) return new { error = $"from: {error}" };
            fromSha = sha;
        }

        if (!toIsWorktree)
        {
            var (sha, error) = await RevParseAsync(repository, toValue);
            if (error is not null) return new { error = $"to: {error}" };
            toSha = sha;
        }

        if (fromSha is not null && string.Equals(fromSha, toSha, StringComparison.Ordinal))
            return new
            {
                error = $"from and to resolve to the same revision ({fromSha[..Math.Min(7, fromSha.Length)]}): there is nothing to compare.",
                revision = fromSha,
            };

        var (fromFiles, fromError) = fromIsWorktree
            ? ReadWorktree(repository, filter)
            : await ReadRevisionAsync(repository, fromSha!, filter, cancellationToken);

        if (fromError is not null) return new { error = $"from: {fromError}" };

        var (toFiles, toError) = toIsWorktree
            ? ReadWorktree(repository, filter)
            : await ReadRevisionAsync(repository, toSha!, filter, cancellationToken);

        if (toError is not null) return new { error = $"to: {toError}" };

        var before = Measure(fromIsWorktree ? "worktree" : fromSha!, fromFiles);
        var after = Measure(toIsWorktree ? "worktree" : toSha!, toFiles);

        var diff = QualityTrendComparer.Compare(before, after, limit);

        var fromMeta = fromIsWorktree
            ? ("(uncommitted working tree)", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"))
            : await DescribeAsync(repository, fromSha!);

        var toMeta = toIsWorktree
            ? ("(uncommitted working tree)", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"))
            : await DescribeAsync(repository, toSha!);

        object Side(RevisionQuality quality, string requested, string? sha, bool worktree, (string Subject, string Date) meta) => new
        {
            @ref = worktree ? "worktree" : requested,
            revision = worktree ? "worktree" : sha,
            shortSha = worktree || sha is null ? null : sha[..Math.Min(7, sha.Length)],
            subject = meta.Subject,
            date = meta.Date,
            worktree,
            metrics = new
            {
                files = quality.FileCount,
                sourceLines = quality.SourceLines,
                types = quality.TypeCount,
                methods = quality.MethodCount,
                complexity = quality.Complexity,
                averageComplexity = Math.Round(quality.AverageComplexity, 2),
                methodsOver10 = quality.MethodsOver(10),
                methodsOver20 = quality.MethodsOver(20),
                longMethods = quality.LongMethods,
                todos = quality.Todos,
                maxMethodComplexity = quality.MaxMethodComplexity,
            },
        };

        var result = new
        {
            tool = "get_quality_trend",
            repository,
            path = filter ?? ".",
            from = Side(before, fromValue, fromSha, fromIsWorktree, fromMeta),
            to = Side(after, toValue, toSha, toIsWorktree, toMeta),
            filesCompared = diff.FilesCompared,
            verdict = diff.Verdict,
            signals = diff.Signals,
            worseFiles = diff.WorseFiles,
            betterFiles = diff.BetterFiles,
            addedFiles = diff.AddedFiles,
            removedFiles = diff.RemovedFiles,
            note = "Both sides are measured syntactically with the same rule set (one path per body plus if, loop, " +
                   "catch, non-default case, switch arm, conditional, &&, ||, ??, ??=), so a revision that does not build " +
                   "is still comparable. The verdict counts only average complexity (1% threshold), methods over 10 and 20, " +
                   "long methods (over 40 lines) and TODO markers; files, lines, types, methods and the complexity total " +
                   "are context and never change it. Generated files are skipped. `to=worktree` measures what is on disk now.",
        };

        _cache = new CacheEntry(key, result);
        return result;
    }

    private static RevisionQuality Measure(string revision, IReadOnlyDictionary<string, string> files)
    {
        var byFile = new Dictionary<string, FileQuality>(StringComparer.Ordinal);

        foreach (var (path, source) in files)
        {
            if (SourceQualityReader.IsGenerated(path)) continue;
            byFile[path] = SourceQualityReader.Read(path, source);
        }

        return new RevisionQuality(revision, revision, byFile);
    }

    private static async Task<(Dictionary<string, string> Files, string? Error)> ReadRevisionAsync(
        string repository, string sha, string? filter, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var archive = ArchivePath(repository);

        try
        {
            var pathspec = filter is null
                ? "*.cs"
                : filter.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? filter : filter + "/*.cs";

            var (exitCode, timedOut, _, stderr, _) = await ProcessRunner.RunAsync(
                "git", $"archive --format=zip --output=\"{archive}\" \"{sha}\" -- \"{pathspec}\"", repository, 300);

            if (exitCode != 0)
            {
                // A path filter that matches nothing is an empty side, not a failure.
                if (stderr.Contains("did not match any files", StringComparison.OrdinalIgnoreCase))
                    return (files, null);

                return (files, timedOut ? "git archive timed out." : $"git archive failed: {stderr.Trim()}");
            }

            using var zip = ZipFile.OpenRead(archive);

            foreach (var entry in zip.Entries)
            {
                if (files.Count >= MaxFilesPerSide) break;
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;

                var name = entry.FullName.Replace('\\', '/');
                if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (SourceQualityReader.IsGenerated(name)) continue;

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                files[name] = await reader.ReadToEndAsync(cancellationToken);
            }
        }
        finally
        {
            try { if (File.Exists(archive)) File.Delete(archive); } catch (IOException) { /* best-effort */ }
        }

        return (files, null);
    }

    private static (Dictionary<string, string> Files, string? Error) ReadWorktree(string repository, string? filter)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = repository;

        if (filter is not null)
        {
            root = Path.Combine(repository, filter.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(root))
            {
                files[filter] = File.ReadAllText(root);
                return (files, null);
            }
        }

        if (!Directory.Exists(root))
            return (files, $"'{filter}' does not exist under {repository}.");

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (files.Count >= MaxFilesPerSide) break;
                if (IsInSkippedFolder(repository, file)) continue;

                var relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
                if (SourceQualityReader.IsGenerated(relative)) continue;

                files[relative] = File.ReadAllText(file);
            }
        }
        catch (IOException exception)
        {
            return (files, "reading the working tree failed: " + exception.Message);
        }

        return (files, null);
    }

    /// <summary>
    /// Where the archive of a revision is written. It is a scratch file of a few MB, so it stays inside the
    /// analysed repository (the git-ignored <c>.visualMCP</c> folder) instead of the system temp folder, which
    /// on a machine with a full system drive is the worst place to put it. Falls back to the temp folder when
    /// the repository is read-only.
    /// </summary>
    private static string ArchivePath(string repository)
    {
        var name = "trend-" + Guid.NewGuid().ToString("N") + ".zip";

        try
        {
            var folder = Path.Combine(repository, ".visualMCP", "tmp");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Path.GetTempPath(), "vmcp-" + name);
        }
    }

    private static bool IsInSkippedFolder(string repository, string file) =>
        Path.GetRelativePath(repository, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase));

    private static bool IsWorktree(string value) =>
        string.Equals(value, "worktree", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "working", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeFilter(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        return normalized.Length == 0 || normalized == "." ? null : normalized;
    }

    private string? ResolveRepository(string? repoPath)
    {
        var start = repoPath;
        if (string.IsNullOrWhiteSpace(start))
        {
            var solutionPath = _workspace.LoadedSolutionPath;
            if (string.IsNullOrWhiteSpace(solutionPath)) return null;
            start = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        }

        if (string.IsNullOrWhiteSpace(start)) return null;

        for (var current = Path.GetFullPath(start); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(Path.Combine(current, ".git")) || File.Exists(Path.Combine(current, ".git")))
                return current;

        return null;
    }

    private static async Task<(string? Sha, string? Error)> RevParseAsync(string repository, string reference)
    {
        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
            "git", $"rev-parse --verify \"{reference}^{{commit}}\"", repository, 60);

        if (exitCode != 0)
            return (null, timedOut ? "git rev-parse timed out." : $"'{reference}' is not a revision: {stderr.Trim()}");

        var sha = stdout.Trim();
        return sha.Length == 0 ? (null, $"'{reference}' resolved to nothing.") : (sha, null);
    }

    private static async Task<(string Subject, string Date)> DescribeAsync(string repository, string sha)
    {
        var (exitCode, _, stdout, _, _) = await ProcessRunner.RunAsync(
            "git", $"show -s --format=%s%n%ci \"{sha}\"", repository, 60);

        if (exitCode != 0) return ("(unavailable)", "(unavailable)");

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length switch
        {
            0 => ("(unavailable)", "(unavailable)"),
            1 => (lines[0], "(unavailable)"),
            _ => (lines[0], lines[1]),
        };
    }

    private sealed record CacheEntry(string Key, object Result);
}
