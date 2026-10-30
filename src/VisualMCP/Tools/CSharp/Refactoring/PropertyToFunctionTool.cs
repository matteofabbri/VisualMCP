using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class PropertyToFunctionTool
{
    [McpServerTool(Name = "property_to_function"), Description(
        "Turn a property into methods and rewrite every use (Rider's 'Property to method'): a get-only property " +
        "becomes GetX(), a get/set property becomes GetX() and SetX(value), and `x.P = v` becomes `x.SetP(v)`. " +
        "Reads, writes, compound assignments and ++/-- are rewritten; nameof(), ref/out arguments and object " +
        "initializers are refused because a method cannot stand in for them. Auto-properties are refused (their " +
        "accessors have no body). dryRun defaults to true.")]
    public static Task<object> PropertyToFunction(
        IPropertyToFunctionService svc,
        [Description("File that declares the property (absolute or relative).")] string filePath,
        [Description("Property name.")] string propertyName,
        [Description("Optional containing type name, when more than one type declares a property with this name.")] string? containingType = null,
        [Description("Optional getter name; defaults to 'Get' + property name.")] string? getterName = null,
        [Description("Optional setter name; defaults to 'Set' + property name.")] string? setterName = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.PropertyToFunction(filePath, propertyName, containingType, getterName, setterName, dryRun);
}
