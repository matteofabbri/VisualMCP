using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Metacode.Symbols;

namespace VisualMCP.Tools.Maintenance;

[McpServerToolType]
public static class ReferenceIndexTool
{
    [McpServerTool(Name = "reference_index"), Description(
        "The solution's symbol index, persisted in .visualMCP/visualmcp.db: what ILSpy and Visual Studio answer from, " +
        "as queries. action=build indexes the documents whose content hash changed (force re-indexes all); " +
        "action=status reports what is stored; action=find searches declarations by name; action=describe returns one " +
        "symbol in full (signature, documentation, where declared, its uses by kind, its callers); action=refs lists " +
        "every use with HOW it is used (call, read, write, instantiate, inherit, implement, attribute, cast, typeof, " +
        "type-argument, pattern, override, ...) and the member it happens inside; action=callers groups the uses by " +
        "the member that performs them; action=callees lists what a member uses; action=hierarchy gives what a type " +
        "extends, implements, and what extends or implements it; action=outline lists the members of a type or the " +
        "declarations of a file; action=metacode returns the metacode tree of a declaration (or every tree of a file) — " +
        "the same canonical pseudocode the translator prints, stored per symbol; action=unused lists non-public " +
        "declarations nothing uses. action=who is refs. " +
        "The index is a snapshot keyed on file content: build it before querying, and rebuild after edits.")]
    public static Task<object> ReferenceIndex(
        ISymbolIndexService index,
        [Description("build | status | find | describe | refs | who | callers | callees | hierarchy | outline | metacode | unused (default: status).")]
        string action = "status",
        [Description("The symbol: a name ('SimplifyAsync'), a qualified name ('C.Add') or a full key. Required by find, describe, refs, callers, callees, hierarchy and metacode.")]
        string? symbol = null,
        [Description("For outline and metacode: a file path, instead of a type name.")]
        string? path = null,
        [Description("Only uses of this kind, e.g. 'call', 'write', 'inherit'. For refs, find and unused.")]
        string? kind = null,
        [Description("For find: match the name as a substring instead of exactly.")]
        bool partial = false,
        [Description("For build: only this project.")]
        string? projectName = null,
        [Description("For build: re-index every document even when its hash matches.")]
        bool force = false,
        [Description("How many rows to return (100-200 depending on the action).")]
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "build" => index.BuildAsync(projectName, force, cancellationToken),
            "status" => Task.FromResult(index.Status()),
            "find" => Task.FromResult(index.Find(symbol ?? string.Empty, kind, partial, limit)),
            "describe" => Task.FromResult(index.Describe(symbol ?? string.Empty, limit)),
            "refs" or "who" => Task.FromResult(index.References(symbol ?? string.Empty, kind, limit)),
            "callers" => Task.FromResult(index.Callers(symbol ?? string.Empty, limit)),
            "callees" => Task.FromResult(index.Callees(symbol ?? string.Empty, limit)),
            "hierarchy" => Task.FromResult(index.Hierarchy(symbol ?? string.Empty, limit)),
            "outline" => Task.FromResult(index.Outline(symbol, path, limit)),
            "metacode" => Task.FromResult(index.Metacode(symbol, path, limit)),
            "unused" => Task.FromResult(index.Unused(limit, kind)),
            _ => Task.FromResult<object>(new
            {
                error = "action must be build, status, find, describe, refs, who, callers, callees, hierarchy, outline, metacode or unused.",
            }),
        };
    }
}
