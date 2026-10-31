using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class AnalysisLimitsTools
{
    [McpServerTool(Name = "find_reflection_usage"), Description(
        "List the reflective lookups in the solution — Activator.CreateInstance, Type.GetType, Assembly.Load/" +
        "LoadFrom, Type.GetMethod/GetProperty/GetField/GetMember, Delegate.CreateDelegate — with the name each " +
        "one looks up and whether that name resolves to a declaration in the solution. Reflection is not " +
        "followed by the call graph, so this is how you check a dead-code or unused-symbol result: the names " +
        "under resolvedNames must be treated as live.")]
    public static Task<object> FindReflectionUsage(
        IFindReflectionUsageService service,
        [Description("Optional: restrict to one project (exact name).")] string? projectName = null)
        => service.FindReflectionUsage(projectName);

    [McpServerTool(Name = "list_generated_sources"), Description(
        "Show what source generators contributed to each project: the generators the project references and " +
        "the syntax trees they produced. Those trees exist in the compilation but not as documents, so " +
        "navigation, read_symbol and the editors cannot address them — this answers 'is that type generated, " +
        "and by what'. With includeSource=true each tree's text is returned (capped at maxCharsPerTree), which " +
        "is how generated code is read without a document. Read-only.")]
    public static Task<object> ListGeneratedSources(
        IGeneratedSourcesService service,
        [Description("Optional: restrict to one project (exact name).")] string? projectName = null,
        [Description("Return each generated tree's text as well as its metadata (default false).")] bool includeSource = false,
        [Description("Characters of each tree's text to return when includeSource is true (default 20000, max 200000).")] int maxCharsPerTree = 20_000)
        => service.ListAsync(projectName, includeSource, maxCharsPerTree);

    [McpServerTool(Name = "find_external_usages"), Description(
        "Which types and members of assemblies OUTSIDE the solution it actually uses, with call sites and a " +
        "per-assembly summary (distinct members, usage count, top types). The internals of a referenced " +
        "assembly are still not analysed, but its used surface is: this is 'what do we depend on, and where'. " +
        "System/Microsoft framework assemblies are excluded unless includeFramework=true. Read-only.")]
    public static Task<object> FindExternalUsages(
        IExternalUsagesService service,
        [Description("Optional: restrict to one project (exact name).")] string? projectName = null,
        [Description("Optional: only assemblies whose name contains this text (e.g. 'Newtonsoft').")] string? assemblyName = null,
        [Description("Include System/Microsoft framework assemblies too (default false: third-party libraries only).")] bool includeFramework = false,
        [Description("How many usage rows to return (default 200, max 5000).")] int maxResults = 200)
        => service.FindExternalUsages(projectName, assemblyName, includeFramework, maxResults);

    [McpServerTool(Name = "framework_entry_points"), Description(
        "List the members a framework calls by convention instead of through a reference: MVC actions, Razor " +
        "Pages handlers, SignalR hub methods, hosted services, Blazor lifecycle methods and parameters, EF Core " +
        "overrides and DbSets, middleware Invoke, test methods, [JSInvokable], [Inject] — each with the reason. " +
        "find_unused_symbols applies the same classification before calling anything unused. Read-only.")]
    public static Task<object> FrameworkEntryPoints(
        IFrameworkEntryPointsService service,
        [Description("Optional: restrict to one project (exact name).")] string? projectName = null,
        [Description("How many entries to return (default 500, max 5000).")] int maxResults = 500)
        => service.ListAsync(projectName, maxResults);
}
