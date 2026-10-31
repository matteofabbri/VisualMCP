using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MakeNonStaticTool
{
    [McpServerTool(Name = "make_non_static"), Description(
        "The inverse of make_static: a static method or property becomes an instance member and every call site " +
        "is routed through an instance of its type. A call site is rewritten only when an instance is already in " +
        "scope there — `this` inside the type's own instance members, otherwise the nearest local, parameter or " +
        "field of that type — and an unqualified call inside the type stays unqualified (it already means this.M). " +
        "When a use has no instance, the whole operation is refused and those call sites are listed: nothing is " +
        "fabricated. Refuses static classes, abstract/virtual/override members, extension methods, interface " +
        "members and indexers. Compiler-gated across every file it touches. dryRun defaults to true.")]
    public static Task<object> MakeNonStatic(
        IMakeNonStaticService svc,
        [Description("File that declares the member (absolute or relative).")] string filePath,
        [Description("The static method or property to convert.")] string memberName,
        [Description("Optional containing type name, to disambiguate a same-named member or a nested type.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MakeNonStatic(filePath, memberName, containingType, dryRun);
}
