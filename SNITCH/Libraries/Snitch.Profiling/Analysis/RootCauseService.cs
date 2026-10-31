using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IRootCauseService
{
    /// <summary>
    /// Joins the measured sites of a trace (CPU, allocation, blocking, exceptions) with the metacode
    /// AST of the declaration each site belongs to. <paramref name="userCodeRoot"/> narrows the answer
    /// to the solution when the trace was collected elsewhere; <paramref name="top"/> caps the sites
    /// returned, <paramref name="minShare"/> drops the ones that do not matter.
    /// </summary>
    Task<RootCauseReport> AnalyzeAsync(
        string tracePath,
        int top,
        double minShare,
        string? userCodeRoot,
        bool resolveSource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Answers "which CODE creates the problem?", which a hotspot list cannot: a sample says a line burned
/// CPU, this says that the line materialises a LINQ query inside two nested loops, or that the
/// allocation was executed once per element of a <c>foreach</c>.
///
/// The measured half comes from the same reader the other trace tools use, so the numbers agree with
/// <c>profile_analyze</c>/<c>profile_hotpaths</c> on the same file. The static half comes from
/// <see cref="ICodeStructureIndex"/>, which the host implements over the solution's metacode: the
/// declaration that contains the line, its loops with their nesting depth and header text, and the
/// constructs a running cost can be attributed to (allocation, LINQ materialiser, string concatenation,
/// await, lock, synchronous wait).
///
/// A site with no resolvable line is still reported from the measurement alone — and the report says
/// the structure is missing instead of guessing a declaration from a method name.
/// </summary>
public sealed class RootCauseService : IRootCauseService
{
    private readonly ITraceStackReader _reader;
    private readonly ICodeStructureIndex _structure;

    public RootCauseService(ITraceStackReader reader, ICodeStructureIndex structure)
    {
        _reader = reader;
        _structure = structure;
    }

    public Task<RootCauseReport> AnalyzeAsync(
        string tracePath,
        int top,
        double minShare,
        string? userCodeRoot,
        bool resolveSource,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var normalizedTop = Math.Clamp(top <= 0 ? 15 : top, 1, 100);
        var normalizedShare = Math.Clamp(minShare, 0, 100);

        return Task.Run(
            () => Analyze(tracePath, normalizedTop, normalizedShare, userCodeRoot, resolveSource),
            cancellationToken);
    }

    private RootCauseReport Analyze(
        string tracePath, int top, double minShare, string? userCodeRoot, bool resolveSource)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var warnings = new List<string>(model.Warnings);
        var files = new Dictionary<string, CodeFileStructure?>(StringComparer.OrdinalIgnoreCase);

        var sites = new List<SiteTotal>();
        var cpuTotal = model.CpuSamples.Sum(sample => sample.MetricMs);
        var allocationTotal = model.Allocations.Sum(allocation => (double)allocation.Bytes);
        var blockedTotal = model.BlockedSamples.Sum(sample => sample.DurationMs);
        var exceptionTotal = model.Exceptions.Count;

        Accumulate(sites, model.CpuSamples, "cpu", "ms", cpuTotal, userCodeRoot,
            sample => sample.Frames.FirstOrDefault(), (sample, leaf) => sample.MetricMs);

        Accumulate(sites, model.Allocations, "allocations", "bytes", allocationTotal, userCodeRoot,
            allocation => allocation.Frames.FirstOrDefault(), (allocation, _) => allocation.Bytes,
            allocation => allocation.TypeName);

        Accumulate(sites, model.BlockedSamples, "blocked", "ms", blockedTotal, userCodeRoot,
            sample => sample.Frames.FirstOrDefault(), (sample, _) => sample.DurationMs);

        Accumulate(sites, model.Exceptions, "exceptions", "throws", exceptionTotal, userCodeRoot,
            exception => exception.Frames.FirstOrDefault(), (_, _) => 1,
            exception => exception.Type);

        var ranked = sites
            .Where(site => site.Cost > 0 && Significant(site))
            .Select(site => site with { Share = site.Total > 0 ? site.Cost * 100.0 / site.Total : 0 })
            .Where(site => site.Share >= minShare)
            .OrderByDescending(site => site.Share)
            .ThenByDescending(site => site.Cost)
            .Take(top)
            .ToList();

        var hot = new List<CodeHotSite>(ranked.Count);
        var missingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = 0;

        foreach (var site in ranked)
        {
            var declaration = Resolve(site, files, missingFiles);
            if (declaration is not null) resolved++;

            var constructs = declaration is null
                ? Array.Empty<CodeConstructInfo>()
                : Relevant(declaration, site);
            hot.Add(new CodeHotSite(
                site.Metric,
                site.Method,
                site.Source,
                site.File,
                site.Line,
                site.Subject,
                Math.Round(site.Cost, site.Unit == "bytes" ? 0 : 1),
                site.Unit,
                Math.Round(site.Share, 2),
                declaration,
                constructs,
                Explain(site, declaration, constructs)));
        }

        var structureAvailable = resolved > 0;
        if (!structureAvailable && hot.Count > 0)
        {
            warnings.Add(
                "No hot site could be joined with the metacode AST: the frames either carry no source line " +
                "(no portable PDB) or the files are not part of the solution the code model holds. " +
                "The measured sites are still reported.");
        }
        else if (missingFiles.Count > 0)
        {
            warnings.Add(
                $"{missingFiles.Count} file(s) with hot sites could not be read into the metacode model " +
                $"(e.g. {string.Join(", ", missingFiles.Take(3))}); their sites are reported without constructs.");
        }

        var findings = BuildFindings(hot, structureAvailable);
        var nextSteps = findings
            .Select(finding => finding.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (nextSteps.Count == 0)
        {
            nextSteps.Add(
                "Nothing dominates above the threshold. Raise the duration of the capture, or lower " +
                "minShare to see the smaller sites.");
        }

        return new RootCauseReport(
            Path.GetFullPath(tracePath),
            Math.Round(model.DurationMs, 1),
            Math.Round(cpuTotal, 1),
            Math.Round(blockedTotal, 1),
            model.Allocations.Sum(allocation => allocation.Bytes),
            model.Exceptions.Count,
            structureAvailable,
            hot,
            findings,
            nextSteps,
            warnings);
    }

    /// <summary>
    /// Aggregates one metric by site. A site is the leaf frame of the stack (the code that ran, not the
    /// caller): its file and line when the PDB resolved them, the method name otherwise. Shares are of
    /// the metric's own total, so a CPU site and an allocation site are not compared with each other.
    /// </summary>
    private static void Accumulate<T>(
        List<SiteTotal> sites,
        IReadOnlyList<T> samples,
        string metric,
        string unit,
        double total,
        string? userCodeRoot,
        Func<T, TraceFrameInfo?> leafOf,
        Func<T, TraceFrameInfo?, double> costOf,
        Func<T, string?>? subjectOf = null)
    {
        var index = new Dictionary<string, SiteTotal>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            var leaf = leafOf(sample);
            if (leaf is null) continue;
            if (!UserCode.IsUser(leaf.Source, userCodeRoot)) continue;

            var subject = subjectOf?.Invoke(sample);
            var key = $"{metric}|{leaf.File}|{leaf.Line}|{leaf.Method}|{subject}";
            if (!index.TryGetValue(key, out var site))
            {
                site = new SiteTotal(
                    metric, leaf.Method, leaf.Source, leaf.File, leaf.Line, subject, 0, unit, total, 0);
                index[key] = site;
                sites.Add(site);
            }

            index[key] = site with { Cost = site.Cost + costOf(sample, leaf) };
        }

        // The list holds records (immutable); write the accumulated costs back in place.
        for (var i = 0; i < sites.Count; i++)
        {
            var key = $"{sites[i].Metric}|{sites[i].File}|{sites[i].Line}|{sites[i].Method}|{sites[i].Subject}";
            if (index.TryGetValue(key, out var updated)) sites[i] = updated;
        }
    }

    /// <summary>
    /// Whether a site is worth a line in the answer. A one-off exception or a 4 KB allocation that is
    /// 100 % of its metric would otherwise outrank a CPU line that owns half the trace.
    /// </summary>
    private static bool Significant(SiteTotal site) => site.Metric switch
    {
        "cpu" => site.Cost >= 10,
        "allocations" => site.Cost >= 64 * 1024,
        "blocked" => site.Cost >= 10,
        "exceptions" => site.Cost >= 3,
        _ => site.Cost > 0,
    };

    /// <summary>The declaration that contains the site, from the file's structure (cached per run).</summary>
    private CodeDeclarationInfo? Resolve(
        SiteTotal site, Dictionary<string, CodeFileStructure?> files, HashSet<string> missingFiles)
    {
        if (site.File is not { Length: > 0 } file) return null;

        if (!files.TryGetValue(file, out var structure))
        {
            try
            {
                structure = _structure.Describe(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                structure = new CodeFileStructure(file, [], exception.Message);
            }

            files[file] = structure;
        }

        if (structure is null || structure.Declarations.Count == 0)
        {
            missingFiles.Add(Path.GetFileName(file));
            return null;
        }

        if (site.Line is int line)
        {
            return structure.Declarations
                .Where(declaration => declaration.Contains(line))
                .OrderBy(declaration => declaration.EndLine - declaration.Line)
                .FirstOrDefault();
        }

        // No line: only a name match in the same file is trustworthy (a method name alone can be
        // another type's member).
        var name = FrameNames.ShortName(site.Method);
        return structure.Declarations
            .Where(declaration => string.Equals(declaration.Name, name, StringComparison.Ordinal))
            .OrderBy(declaration => declaration.Line)
            .FirstOrDefault();
    }

    /// <summary>
    /// The constructs that explain the site, nearest first: the ones whose span contains the line (the
    /// loop chain and the statement itself), then the constructs of the same family inside the
    /// declaration. Never returns more than the reader can act on.
    /// </summary>
    private static IReadOnlyList<CodeConstructInfo> Relevant(CodeDeclarationInfo declaration, SiteTotal site)
    {
        if (site.Line is not int line)
            return declaration.Constructs.Take(6).ToList();

        var containing = declaration.Constructs
            .Where(construct => construct.Line > 0 && construct.Line <= line &&
                                (construct.EndLine == 0 || construct.EndLine >= line))
            .OrderBy(construct => construct.EndLine == 0 ? int.MaxValue : construct.EndLine - construct.Line)
            .ThenByDescending(construct => construct.LoopDepth)
            .ToList();

        var relevantKinds = site.Metric switch
        {
            "allocations" => new[] { "allocation", "loop", "linq", "string-concat" },
            "blocked" => new[] { "sync-over-async", "lock", "await", "loop" },
            "exceptions" => new[] { "throw", "loop", "linq" },
            _ => new[] { "loop", "linq", "allocation", "string-concat", "await" },
        };

        var nearby = declaration.Constructs
            .Where(construct => !containing.Contains(construct))
            .Where(construct => relevantKinds.Contains(construct.Kind))
            .OrderBy(construct => Math.Abs(construct.Line - line))
            .ThenByDescending(construct => construct.LoopDepth);

        return containing.Concat(nearby).Take(6).ToList();
    }

    /// <summary>
    /// The sentence that connects the measurement to the construct: what was measured, where, and what
    /// the AST says the line is — the part a hotspot list cannot provide.
    /// </summary>
    private static string Explain(
        SiteTotal site, CodeDeclarationInfo? declaration, IReadOnlyList<CodeConstructInfo> constructs)
    {
        var measured = site.Metric switch
        {
            "cpu" => $"{site.Cost:N0} ms of sampled CPU ({site.Share:0.0}%) is self time at {Where(site)}",
            "allocations" => $"{site.Cost:N0} sampled bytes ({site.Share:0.0}%) are allocated at {Where(site)}" +
                             (site.Subject is { Length: > 0 } type ? $" as {type}" : string.Empty),
            "blocked" => $"{site.Cost:N0} ms ({site.Share:0.0}%) were spent blocked at {Where(site)}",
            "exceptions" => $"{site.Cost:N0} exception(s) ({site.Share:0.0}%) were thrown at {Where(site)}" +
                            (site.Subject is { Length: > 0 } type ? $" ({type})" : string.Empty),
            _ => $"{site.Cost:N0} {site.Unit} at {Where(site)}",
        };

        var text = measured + ".";

        if (declaration is null)
            return text + " No AST declaration was found for this site.";

        text += $" It is inside {declaration.Name} (cyclomatic {declaration.Cyclomatic}, " +
                $"cognitive {declaration.Cognitive}, max nesting {declaration.MaxNesting}).";

        var loops = constructs
            .Where(construct => construct.Kind == "loop")
            .OrderByDescending(construct => construct.LoopDepth)
            .ThenBy(construct => construct.Line)
            .ToList();

        var enclosing = constructs
            .Where(construct => construct.Kind == "loop" && construct.IsInsideLoop)
            .Max(construct => construct.LoopDepth);

        if (enclosing > 0)
        {
            var headers = loops
                .Select(loop => loop.LoopHeader)
                .Where(header => !string.IsNullOrWhiteSpace(header))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            text += enclosing == 1
                ? " The line runs inside a loop" +
                  (headers.Count > 0 ? $" ({headers[0]})" : string.Empty) +
                  ", so the work runs once per element."
                : $" The line sits inside {enclosing} nested loops" +
                  (headers.Count > 0 ? $" — {string.Join(" → ", headers)}" : string.Empty) +
                  ", so the work runs once per element of each.";
        }

        var details = constructs
            .Where(construct => construct.Kind is not "loop")
            .Take(3)
            .Select(construct => $"{construct.Kind} `{construct.Detail}` at line {construct.Line}")
            .ToList();

        if (details.Count > 0)
            text += $" On this path the AST sees: {string.Join("; ", details)}.";

        return text;
    }

    private static string Where(SiteTotal site) =>
        site.File is { Length: > 0 } file && site.Line is int line
            ? $"{Path.GetFileName(file)}:{line}"
            : FrameNames.ShortName(site.Method);

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        IReadOnlyList<CodeHotSite> sites, bool structureAvailable)
    {
        var findings = new List<ProfilingFinding>();

        if (sites.Count == 0)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-no-samples", "info",
                "No user-code site was measured",
                "The trace has no CPU, allocation, blocking or exception site that resolves to code outside the " +
                "framework (or the PDBs are missing, so nothing resolves at all).",
                "Collect a longer trace while the operation runs, with portable PDBs next to the modules.",
                "profile_symbols to download the PDBs, then re-run; profile_diagnose for the live path.",
                null));
            return findings;
        }

        var allocation = sites
            .Where(site => site.Metric == "allocations" && site.Constructs.Any(construct => construct.IsInsideLoop))
            .OrderByDescending(site => site.Share)
            .FirstOrDefault();
        if (allocation is not null)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-allocation-in-loop", allocation.Share >= 40 ? "critical" : "warning",
                $"An allocation inside a loop owns {allocation.Share:0.0}% of the sampled bytes",
                allocation.Explanation,
                "Move the allocation out of the loop (reuse a buffer/list), or change the loop so it does not " +
                "run once per element of another loop.",
                $"Open {allocation.File}:{allocation.Line}",
                allocation.File is null ? null : $"{allocation.File}:{allocation.Line}"));
        }

        var nested = sites
            .Where(site => site.Metric == "cpu" && MaxLoopDepth(site) >= 2)
            .OrderByDescending(site => site.Share)
            .FirstOrDefault();
        if (nested is not null)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-nested-loop-work", "warning",
                $"Nested loops drive {nested.Share:0.0}% of the CPU",
                nested.Explanation,
                "Reduce the loop nesting (index the inner collection with a dictionary, hoist invariant work, " +
                "or batch the operation) — the cost multiplies with each level.",
                $"Open {nested.File}:{nested.Line}",
                nested.File is null ? null : $"{nested.File}:{nested.Line}"));
        }

        var linq = sites
            .Where(site => site.Constructs.Any(construct =>
                construct.Kind == "linq" && construct.IsInsideLoop && construct.Detail.Contains("materialis", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(site => site.Share)
            .FirstOrDefault();
        if (linq is not null)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-linq-in-loop", "warning",
                "LINQ materialises a collection inside a loop",
                linq.Explanation,
                "Materialise once before the loop (or use a streaming operator and break early); ToList/ToArray " +
                "per iteration allocates the whole sequence every time.",
                $"Open {linq.File}:{linq.Line}",
                linq.File is null ? null : $"{linq.File}:{linq.Line}"));
        }

        var syncOverAsync = sites
            .Where(site => site.Constructs.Any(construct => construct.Kind == "sync-over-async"))
            .OrderByDescending(site => site.Share)
            .FirstOrDefault();
        if (syncOverAsync is not null)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-sync-over-async", "warning",
                "A synchronous wait blocks the thread",
                syncOverAsync.Explanation,
                "Await the operation instead of blocking on it (profile_io reports the same wait as sync-over-async), " +
                "so the thread returns to the pool while the work runs.",
                $"Open {syncOverAsync.File}:{syncOverAsync.Line}",
                syncOverAsync.File is null ? null : $"{syncOverAsync.File}:{syncOverAsync.Line}"));
        }

        var concat = sites
            .Where(site => site.Constructs.Any(construct => construct.Kind == "string-concat" && construct.IsInsideLoop))
            .OrderByDescending(site => site.Share)
            .FirstOrDefault();
        if (concat is not null)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-string-concat-in-loop", "warning",
                "String concatenation inside a loop",
                concat.Explanation,
                "Use a StringBuilder (or string.Join) instead of growing an immutable string once per iteration.",
                $"Open {concat.File}:{concat.Line}",
                concat.File is null ? null : $"{concat.File}:{concat.Line}"));
        }

        if (!structureAvailable)
        {
            findings.Add(new ProfilingFinding(
                "root-cause-no-code-model", "info",
                "The measurements could not be joined with the AST",
                "No hot site had a declaration in the code model, so the answer is the measured half only.",
                "Make sure the profiled project is the solution this server has loaded and its portable PDBs are " +
                "next to the modules (profile_symbols downloads them), then re-run profile_root_cause.",
                "metacode_index action=status to see what the code model holds; profile_symbols for the PDBs.",
                null));
        }

        return findings;
    }

    private static int MaxLoopDepth(CodeHotSite site) =>
        site.Constructs.Where(construct => construct.Kind == "loop")
            .Select(construct => construct.LoopDepth)
            .DefaultIfEmpty(0)
            .Max();

    /// <summary>One site while it is being aggregated: the cost is mutable by replacement, the rest is fixed.</summary>
    private sealed record SiteTotal(
        string Metric,
        string Method,
        string? Source,
        string? File,
        int? Line,
        string? Subject,
        double Cost,
        string Unit,
        double Total,
        double Share);
}
