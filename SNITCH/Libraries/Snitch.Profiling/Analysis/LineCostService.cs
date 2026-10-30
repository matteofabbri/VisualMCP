using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface ILineCostService
{
    Task<LineCostReport> AnalyzeAsync(
        string tracePath, string? method, int top, double minPercent, bool resolveSource,
        bool includeText, CancellationToken cancellationToken = default);
}

/// <summary>
/// Answers "which line inside this method burns the CPU?" — the question a function-level hotspot
/// cannot answer.
///
/// The sampling profiler records an instruction pointer per sample. TraceEvent resolves that
/// pointer through the method's IL→native map (emitted under the JIT keyword, which the cpu and
/// diagnose presets already enable) to an IL offset, and <see cref="SourceMapResolver"/> turns the
/// IL offset into the PDB sequence point that covers it. The result is the line the instruction
/// really belongs to, not the method's declaration line: a sample in the prologue or in a method
/// with no map carries no line at all and is reported as unattributed instead of being pushed onto
/// line 1.
///
/// The scope is the whole trace by default, or one method (substring, case-insensitive). Shares are
/// of the scope, so a line that owns half of a small method still reads 50%.
/// </summary>
public sealed class LineCostService : ILineCostService
{
    private readonly ITraceStackReader _reader;

    public LineCostService(ITraceStackReader reader) => _reader = reader;

    public Task<LineCostReport> AnalyzeAsync(
        string tracePath, string? method, int top, double minPercent, bool resolveSource,
        bool includeText, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        var normalizedTop = Math.Clamp(top <= 0 ? 20 : top, 1, 100);
        var normalizedPercent = Math.Clamp(minPercent, 0, 100);

        return Task.Run(
            () => Analyze(tracePath, method, normalizedTop, normalizedPercent, resolveSource, includeText),
            cancellationToken);
    }

    private LineCostReport Analyze(
        string tracePath, string? method, int top, double minPercent, bool resolveSource, bool includeText)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var warnings = new List<string>(model.Warnings);

        var methodFilter = string.IsNullOrWhiteSpace(method) ? null : method.Trim();
        var scope = methodFilter is null ? "trace" : $"method {methodFilter}";

        var byLine = new Dictionary<(string File, int Line, string Method), (long Samples, double Ms)>();
        var traceCpuMs = model.CpuSamples.Sum(s => s.MetricMs);
        double scopeCpuMs = 0, attributedCpuMs = 0;
        long samples = 0, attributedSamples = 0;

        foreach (var sample in model.CpuSamples)
        {
            if (sample.Frames.Count == 0)
                continue;

            var leaf = sample.Frames[0];
            if (methodFilter is not null &&
                !leaf.Method.Contains(methodFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            samples++;
            scopeCpuMs += sample.MetricMs;

            if (leaf.File is not { } file || leaf.Line is not int line)
                continue;

            attributedSamples++;
            attributedCpuMs += sample.MetricMs;
            var key = (file, line, leaf.Method);
            var current = byLine.TryGetValue(key, out var stats) ? stats : default;
            byLine[key] = (current.Samples + 1, current.Ms + sample.MetricMs);
        }

        var scopeTotal = Math.Max(1.0, scopeCpuMs);
        IReadOnlyList<CpuLineHotspot> lines = byLine
            .OrderByDescending(kv => kv.Value.Ms)
            .ThenBy(kv => kv.Key.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => kv.Key.Line)
            .Select(kv => new CpuLineHotspot(
                kv.Key.File, kv.Key.Line, kv.Key.Method, kv.Value.Samples,
                Math.Round(kv.Value.Ms * 100.0 / scopeTotal, 2),
                (long)Math.Round(kv.Value.Ms)))
            .Where(line => line.Share >= minPercent)
            .Take(top)
            .ToList();

        if (includeText && lines.Count > 0)
            lines = WithSourceText(lines);

        var findings = BuildFindings(lines, methodFilter, samples, attributedSamples);
        if (samples > 0 && attributedSamples == 0)
            warnings.Add(
                "No sample carried a source line: the module has no portable PDB next to it, or the trace " +
                "was collected without the JIT keyword (the cpu and diagnose presets include it).");
        else if (samples > 0 && attributedCpuMs < scopeCpuMs * 0.5)
            warnings.Add(
                $"{scopeCpuMs - attributedCpuMs:N0} ms of the scope ({100 - attributedCpuMs * 100.0 / Math.Max(1, scopeCpuMs):0}%) " +
                "ran in samples with no resolvable source line (framework code or missing PDBs); the line shares are of the scope, " +
                "so they do not add up to 100%.");

        var nextSteps = findings
            .Select(f => f.SuggestedAction)
            .Where(step => !string.IsNullOrWhiteSpace(step))
            .Select(step => step!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        if (nextSteps.Count == 0)
            nextSteps.Add(
                "No line dominates this scope. Use profile_hotpaths for the method-level fix list, " +
                "or collect a longer trace while the slow operation runs.");

        return new LineCostReport(
            Path.GetFullPath(tracePath), scope,
            Math.Round(scopeCpuMs, 1), Math.Round(traceCpuMs, 1),
            samples, attributedSamples, Math.Round(attributedCpuMs, 1),
            lines, findings, nextSteps, warnings);
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        IReadOnlyList<CpuLineHotspot> lines, string? methodFilter,
        long samples, long attributedSamples)
    {
        var findings = new List<ProfilingFinding>();
        var top = lines.FirstOrDefault();

        if (samples == 0 && methodFilter is not null)
        {
            findings.Add(new ProfilingFinding(
                "line-method-not-found", "info",
                $"No CPU samples matched '{methodFilter}'",
                "The method name is matched as a substring of the frame's full name; either the method never ran " +
                "during the trace, or it was inlined into its caller.",
                "Shorten the filter (the method name alone), or run profile_analyze to see which methods did run.",
                "profile_analyze on the same trace.",
                null));
            return findings;
        }

        if (top is not null && top.Share >= 25 && top.EstimatedCpuMs >= 200)
            findings.Add(new ProfilingFinding(
                "cpu-hot-line", top.Share >= 50 ? "critical" : "warning",
                $"Line {top.Line} owns {top.Share:0.0}% of the sampled CPU",
                $"{top.File}:{top.Line} in {FrameNames.ShortName(top.Method)}: {top.EstimatedCpuMs:N0} ms " +
                $"across {top.Samples:N0} samples" +
                (top.Text is null ? "." : $" — `{top.Text}`"),
                "This is the statement to change: hoist work out of the line, batch it, cache the result, or replace the API it calls. " +
                "A single dominant line usually means an allocation, a lock or a virtual/interface call inside a loop.",
                $"Open {top.File}:{top.Line}",
                $"{top.File}:{top.Line}"));

        if (top is not null && top.Share < 10 && lines.Count >= 3)
            findings.Add(new ProfilingFinding(
                "cpu-spread-over-lines", "info",
                "No single line dominates",
                $"{lines.Count} lines share the scope; the hottest owns {top.Share:0.0}%.",
                "The cost is spread: the fix is structural (fewer iterations, cheaper data layout, fewer calls) rather than one line.",
                "profile_calltree on the same trace to find the caller that drives these lines.",
                null));

        if (samples > 0 && attributedSamples == 0)
            findings.Add(new ProfilingFinding(
                "line-no-symbols", "warning",
                "The samples have no source line",
                $"{samples:N0} samples ran in code with no portable PDB (or with no IL→native map in the trace).",
                "Without a PDB there is no sequence point to attribute a sample to; the method-level view is still available.",
                "profile_symbols to download the PDBs, then re-run; collect with profile='cpu' or 'diagnose' (they enable the JIT keyword).",
                null));

        return findings;
    }

    /// <summary>
    /// Reads the source text of the reported lines. The file is read once per answer and only the
    /// requested lines are kept; a source that is no longer on disk is simply reported without text.
    /// </summary>
    private static IReadOnlyList<CpuLineHotspot> WithSourceText(IReadOnlyList<CpuLineHotspot> lines)
    {
        var files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CpuLineHotspot>(lines.Count);

        foreach (var line in lines)
        {
            if (!files.TryGetValue(line.File, out var content))
            {
                try
                {
                    content = File.Exists(line.File) ? File.ReadAllLines(line.File) : [];
                }
                catch
                {
                    content = [];
                }
                files[line.File] = content;
            }

            string? text = null;
            if (line.Line > 0 && line.Line <= content.Length)
            {
                text = content[line.Line - 1].Trim();
                if (text.Length > 200)
                    text = text[..200] + "…";
                if (text.Length == 0)
                    text = null;
            }

            result.Add(line with { Text = text });
        }

        return result;
    }
}
