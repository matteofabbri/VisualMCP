using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;

namespace VisualMCP.Tools.CSharp.Decompilation;

[McpServerToolType]
public static class AssemblyWorkspaceTool
{
    [McpServerTool(Name = "assembly_workspace"), Description(
        "Work INSIDE a decompiled assembly instead of reading its text: the binary is decompiled once into a " +
        "Roslyn workspace (one document per type, platform + same-folder references), keyed on its module " +
        "version id, and the actions answer from it.\n" +
        "action=load — decompile and keep it (maxTypes to page a huge assembly); action=search — types/members by " +
        "name (exact > prefix > CamelHumps: 'CPS' finds CheckParameterSafety > contains); action=source — the " +
        "decompiled C# of a type (Type) or member (Type.Member) with file:line; action=il — the same symbol as " +
        "IL, the C#↔IL toggle; action=references — every use " +
        "INSIDE the decompiled set, through Roslyn's symbol finder; action=outline — the types of the assembly " +
        "or one type's members with line numbers; action=list — what is loaded; action=unload — drop it. " +
        "This closes what decompile_type could not do: go-to-definition, find-usages and an outline on code " +
        "that has no source. Read-only on disk.")]
    public static Task<object> AssemblyWorkspace(
        IAssemblyWorkspaceService workspaces,
        [Description("load | search | source | il | references | outline | list | unload")] string action,
        [Description("Path to the .dll/.exe. Required for every action except list.")] string? assemblyPath = null,
        [Description("search: the name to look for. source/il/references: Type or Type.Member. outline: the type to outline (omit for all types).")] string? symbol = null,
        [Description("load: maximum types to decompile (0 = all, max 5000).")] int maxTypes = 0,
        [Description("search/il/references/outline: how many results (default 50/50/100/200).")] int maxResults = 50,
        [Description("source: characters of C# to return (default 20000).")] int maxChars = 20000,
        [Description("load: decompile again even when that module version is already loaded.")] bool force = false)
    {
        var name = (action ?? string.Empty).Trim().ToLowerInvariant();

        return name switch
        {
            "load" => workspaces.LoadAsync(assemblyPath ?? string.Empty, maxTypes, force),
            "list" => Task.FromResult(workspaces.List()),
            "search" => workspaces.SearchAsync(assemblyPath ?? string.Empty, symbol ?? string.Empty, maxResults),
            "source" => workspaces.SourceAsync(assemblyPath ?? string.Empty, symbol ?? string.Empty, maxChars),
            "il" => workspaces.IlAsync(assemblyPath ?? string.Empty, symbol ?? string.Empty, maxResults),
            "references" => workspaces.ReferencesAsync(assemblyPath ?? string.Empty, symbol ?? string.Empty, maxResults),
            "outline" => workspaces.OutlineAsync(assemblyPath ?? string.Empty, symbol, maxResults),
            "unload" => Task.FromResult(workspaces.Unload(assemblyPath)),
            _ => Task.FromResult<object>(new
            {
                error = $"Unknown action '{action}'. Use one of: load, list, search, source, il, references, outline, unload.",
            }),
        };
    }
}
