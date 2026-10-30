using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class FunctionToPropertyTool
{
    [McpServerTool(Name = "function_to_property"), Description(
        "Turn a parameterless, non-void, non-generic, non-async method into a property and rewrite its calls " +
        "(Rider's 'Method to property'): `GetCount()` becomes `Count`. The name loses a leading 'Get' unless " +
        "propertyName says otherwise, and a collision with an existing member is refused. Every use must be a plain " +
        "call — a method group or nameof() would change meaning, so the whole operation is refused instead of silently " +
        "breaking it. The body is reused verbatim as the getter. dryRun defaults to true.")]
    public static Task<object> FunctionToProperty(
        IFunctionToPropertyService svc,
        [Description("File that declares the method (absolute or relative).")] string filePath,
        [Description("Method name.")] string methodName,
        [Description("Optional containing type name, when a nested type declares a method with the same name.")] string? containingType = null,
        [Description("Optional property name; defaults to the method name without a leading 'Get'.")] string? propertyName = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.FunctionToProperty(filePath, methodName, containingType, propertyName, dryRun);
}
