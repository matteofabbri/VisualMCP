using Snitch.Profiling.Model;

namespace Snitch.Profiling.Analysis;

public interface IAllocationSiteService
{
    Task<AllocationSiteReport> AnalyzeAsync(
        string tracePath, int top, string? focusType, bool resolveSource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Attributes sampled allocation bytes to the method that performed the allocation. This is the
/// answer to "where is this allocation coming from": the leaf frame of the allocation tick's call
/// stack, with file:line, the allocated type, its kind (small/large) and the full stack. It needs
/// a gc trace collected with requestStackwalk (the gc preset does both), because allocation ticks
/// exist only at Verbose and only carry stacks when the session requests stack walks.
/// </summary>
public sealed class AllocationSiteService : IAllocationSiteService
{
    private readonly ITraceStackReader _reader;

    public AllocationSiteService(ITraceStackReader reader) => _reader = reader;

    public Task<AllocationSiteReport> AnalyzeAsync(
        string tracePath, int top, string? focusType, bool resolveSource,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tracePath))
            throw new FileNotFoundException($"Trace not found: {tracePath}", tracePath);

        top = Math.Clamp(top <= 0 ? 25 : top, 1, 200);

        return Task.Run(() => Analyze(tracePath, top, focusType, resolveSource), cancellationToken);
    }

    private AllocationSiteReport Analyze(string tracePath, int top, string? focusType, bool resolveSource)
    {
        var model = _reader.Read(tracePath, resolveSource);
        var allocations = string.IsNullOrWhiteSpace(focusType)
            ? model.Allocations
            : model.Allocations
                .Where(a => a.TypeName.Contains(focusType, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var totalBytes = allocations.Sum(a => a.Bytes);
        var total = Math.Max(1L, totalBytes);

        var sites = new Dictionary<(string Site, string Type), (long Bytes, long Count, string? Source, string Kind)>();
        var types = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
        var stacks = new Dictionary<string, (long Bytes, string Type)>(StringComparer.Ordinal);

        foreach (var allocation in allocations)
        {
            var leaf = allocation.Frames.Count > 0 ? allocation.Frames[0] : null;
            var site = leaf?.Method ?? "(no allocation stack)";
            var siteKey = (site, allocation.TypeName);
            var current = sites.TryGetValue(siteKey, out var stats) ? stats : default;
            sites[siteKey] = (current.Bytes + allocation.Bytes, current.Count + 1, current.Source ?? leaf?.Source, allocation.Kind);

            var typeStats = types.TryGetValue(allocation.TypeName, out var ts) ? ts : default;
            types[allocation.TypeName] = (typeStats.Bytes + allocation.Bytes, typeStats.Count + 1);

            if (allocation.Frames.Count > 0)
            {
                var stack = string.Join(" <- ", allocation.Frames.Take(10).Select(f => f.Method));
                var stackStats = stacks.TryGetValue(stack, out var ss) ? ss : default;
                stacks[stack] = (stackStats.Bytes + allocation.Bytes, stackStats.Type ?? allocation.TypeName);
            }
        }

        var topSites = sites
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(top)
            .Select(kv => new AllocationSiteHotspot(
                kv.Key.Site, kv.Value.Source, kv.Key.Type, kv.Value.Bytes, kv.Value.Count,
                Math.Round(kv.Value.Bytes * 100.0 / total, 2), kv.Value.Kind))
            .ToList();

        var byType = types
            .Select(kv => new AllocationHotspot(
                kv.Key, kv.Value.Bytes, kv.Value.Count,
                Math.Round(kv.Value.Bytes * 100.0 / total, 2)))
            .OrderByDescending(a => a.SampledBytes)
            .Take(top)
            .ToList();

        var topStacks = stacks
            .OrderByDescending(kv => kv.Value.Bytes)
            .Take(Math.Min(top, 15))
            .Select(kv => new AllocationStackHotspot(
                kv.Key, kv.Value.Type, kv.Value.Bytes,
                Math.Round(kv.Value.Bytes * 100.0 / total, 2)))
            .ToList();

        var findings = BuildFindings(topSites, byType, allocations, totalBytes, model.Truncated);

        return new AllocationSiteReport(
            Path.GetFullPath(tracePath), sites.Count, totalBytes, allocations.Count,
            model.Truncated, topSites, byType, topStacks, findings);
    }

    private static IReadOnlyList<ProfilingFinding> BuildFindings(
        IReadOnlyList<AllocationSiteHotspot> sites,
        IReadOnlyList<AllocationHotspot> types,
        IReadOnlyList<AllocationSiteSample> allocations,
        long totalBytes,
        bool truncated)
    {
        var findings = new List<ProfilingFinding>();
        var site = sites.FirstOrDefault();
        var type = types.FirstOrDefault();

        if (site is not null && site.Site != "(no allocation stack)" && site.Share >= 40 && site.SampledBytes >= 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "allocation-site", "warning",
                $"Most allocations happen in {ShortName(site.Site)}",
                $"{site.Site} allocates {ShortName(site.TypeName)}: {site.SampledBytes / 1024.0 / 1024.0:N1} MB " +
                $"({site.Share:0.0}% of sampled bytes, {site.Samples:N0} samples).",
                "Fix the allocation at its source: reuse the object, rent the buffer, or return a struct/span.",
                "Open the site with read_symbol and change it there.",
                site.Source));

        if (type is not null && type.Share >= 40 && type.SampledBytes >= 1024 * 1024)
            findings.Add(new ProfilingFinding(
                "allocation-type", "warning",
                $"Most allocations are {ShortName(type.TypeName)}",
                $"{type.TypeName}: {type.SampledBytes / 1024.0 / 1024.0:N1} MB ({type.Share:0.0}% of sampled bytes).",
                "Pool or reuse this type; if it is a byte[]/char[], ArrayPool<T> or a span usually removes it.",
                null,
                null));

        var largeBytes = allocations.Where(a => a.Kind.Contains("Large", StringComparison.OrdinalIgnoreCase)).Sum(a => a.Bytes);
        if (totalBytes > 0 && largeBytes >= totalBytes * 0.25)
            findings.Add(new ProfilingFinding(
                "loh-allocation", "warning",
                "A quarter of the allocated bytes are large objects",
                $"{largeBytes / 1024.0 / 1024.0:N1} MB of {totalBytes / 1024.0 / 1024.0:N1} MB went to the large object heap.",
                "Large objects are not compacted by default; pool buffers and avoid resizing large arrays.",
                null,
                null));

        if (sites.Count > 0 && sites.All(s => s.Site == "(no allocation stack)"))
            findings.Add(new ProfilingFinding(
                "allocation-no-stacks", "info",
                "Allocation ticks carry no call stacks",
                "The trace contains allocation ticks but every stack is empty.",
                "Collect with profile='gc' (Verbose + requestStackwalk) or pass requestStackwalk=true; stacks are what turn a type into a site.",
                "profile_collect profile='gc' then profile_allocations.",
                null));

        if (truncated)
            findings.Add(new ProfilingFinding(
                "allocation-truncated", "info",
                "The allocation sample list was capped",
                "Only the first samples were analysed; totals are a lower bound.",
                "Collect a shorter window or filter by focusType.",
                null,
                null));

        return findings.OrderByDescending(f => f.Severity == "warning" ? 1 : 0).ToList();
    }

    private static string ShortName(string fullName) => FrameNames.ShortName(fullName);
}
