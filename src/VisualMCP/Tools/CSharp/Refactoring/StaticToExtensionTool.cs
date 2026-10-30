using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class StaticToExtensionTool
{
    [McpServerTool(Name = "static_to_extension"), Description(
        "Turn a static method into an extension method (Rider's 'Convert static to extension'): the first parameter " +
        "becomes the 'this' receiver and every call rotates — `Type.M(receiver, x)` and bare `M(receiver, x)` become " +
        "`receiver.M(x)`. The method must already be static, in a non-generic non-nested static class, with a first " +
        "parameter that is not ref/out/in or params. Call sites with named arguments or non-call uses (method group, " +
        "nameof) are refused rather than mangled. When a consuming file cannot see the extension's namespace, the " +
        "missing using is added. dryRun defaults to true.")]
    public static Task<object> StaticToExtension(
        IStaticToExtensionService svc,
        [Description("File that declares the static method (absolute or relative).")] string filePath,
        [Description("Static method name.")] string methodName,
        [Description("Optional containing type name, when nested or ambiguous.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.StaticToExtension(filePath, methodName, containingType, dryRun);
}
