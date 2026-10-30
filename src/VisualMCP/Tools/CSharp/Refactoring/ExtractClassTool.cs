using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ExtractClassTool
{
    [McpServerTool(Name = "extract_class"), Description(
        "Extract members into a new class (Rider's 'Extract class'): the selected methods/properties/field move into " +
        "'NewClass.cs', the original keeps a 'private readonly NewClass _holder' plus forwarding members with the same " +
        "signatures, so every caller keeps compiling. The selection must be self-contained: it may only read instance " +
        "members that are also selected (otherwise the refusal names the member that stays behind), no static members, " +
        "no field assigned by a constructor that stays, and no field read from outside the type (a field cannot be " +
        "forwarded). GATED on the compiler. dryRun defaults to true.")]
    public static Task<object> ExtractClass(
        IExtractClassService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Type to extract from.")] string typeName,
        [Description("Members to move: method, property and single-variable field names.")] string[] memberNames,
        [Description("Name of the new class (its file is created next to the original).")] string newClassName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ExtractClass(filePath, typeName, memberNames, newClassName, dryRun);
}
