using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class AddParamsModifierTool
{
    [McpServerTool(Name = "add_params"), Description(
        "Add the 'params' modifier to a method's last array parameter (Rider's 'Add params modifier'), so callers may " +
        "pass the elements directly. Existing call sites keep compiling because they pass an explicit array, which is " +
        "why this is safe without touching them. Refuses single-dimensional check failures: a non-array last parameter, " +
        "multi-dimensional arrays, ref/out/in, an existing params, and overrides or interface implementations (their " +
        "signature must stay identical). dryRun defaults to true.")]
    public static Task<object> AddParams(
        IAddParamsModifierService svc,
        [Description("File that declares the method (absolute or relative).")] string filePath,
        [Description("Method name.")] string methodName,
        [Description("Optional containing type name to disambiguate.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.AddParams(filePath, methodName, containingType, dryRun);
}
