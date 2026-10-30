using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class PropertyToAutoTool
{
    [McpServerTool(Name = "property_to_auto"), Description(
        "Turn a property whose accessors only read and write one backing field into an auto-property " +
        "({ get; set; }), dropping the field (Rider's 'To auto-property'). Only trivial shapes qualify: the getter " +
        "returns the field and the setter assigns it; a private setter and init are preserved, a field initializer is " +
        "moved onto the property. It REFUSES when the field is used anywhere else in the solution, because those uses " +
        "would silently stop tracking the property. dryRun defaults to true and returns a line diff.")]
    public static Task<object> PropertyToAuto(
        IPropertyToAutoService svc,
        [Description("File that declares the property (absolute or relative).")] string filePath,
        [Description("Property name.")] string propertyName,
        [Description("Optional containing type name, when the file has more than one property with that name.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.PropertyToAuto(filePath, propertyName, containingType, dryRun);
}
