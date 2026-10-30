using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ExtractSuperclassTool
{
    [McpServerTool(Name = "extract_superclass"), Description(
        "Rider's 'Extract superclass': move the selected methods, properties and fields into a NEW base class " +
        "(`baseClassName.cs`, same namespace) and make the type derive from it. The selection must be " +
        "self-contained: a member that moves may only read members that move with it, and a field cannot stay " +
        "readable by the members left behind — those cases are refused with the name of the offender instead of " +
        "producing code that does not compile. A class that already has a base class is refused. Compiler-gated. " +
        "dryRun defaults to true.")]
    public static Task<object> ExtractSuperclass(
        IExtractSuperclassService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Class to extract from.")] string typeName,
        [Description("Members to move into the new base class (methods, properties, single-variable fields).")] string[] memberNames,
        [Description("Name of the new base class; also the new file name.")] string baseClassName,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ExtractSuperclass(filePath, typeName, memberNames, baseClassName, dryRun);
}
