using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;
using System.ComponentModel;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class EncapsulateFieldTool
{
    [McpServerTool, Description(
        "When you want to wrap a field in a property, use this INSTEAD OF manual edits: it makes the field private, generates a public get/set property, " +
        "and rewrites every reference across the solution to use the property (ReSharper 'Encapsulate Field'). " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> EncapsulateField(
        IEncapsulateFieldService svc,
        [Description("Name of the field to encapsulate")] string fieldName,
        [Description("Containing type name to disambiguate (required if multiple types have a field with this name)")] string? containingType = null,
        [Description("Name for the generated property (default: PascalCase version of the field name)")] string? propertyName = null,
        [Description("Dry run — show changes without writing to disk (default: false)")] bool dryRun = false)
        => svc.EncapsulateField(fieldName, containingType, propertyName, dryRun);
}