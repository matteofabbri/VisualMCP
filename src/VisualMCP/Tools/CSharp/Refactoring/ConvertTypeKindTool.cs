using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConvertTypeKindTool
{
    [McpServerTool(Name = "convert_type_kind"), Description(
        "Rider's 'Abstract class to interface' and its inverse, in one tool. target='interface': the abstract " +
        "class becomes an interface (members lose `abstract`, constants and nested types stay), and every derived " +
        "type that overrode one of its members loses `override` — refused when the class has instance state, a " +
        "constructor with parameters or a body, or a member with a body, because none of those can live in an " +
        "interface. target='abstract-class': the interface becomes an abstract class whose members gain " +
        "`public abstract`, and every implementing class gains `override` on the members that implemented it; " +
        "refused for structs, records, implementers that already have a base class, and default interface methods. " +
        "Compiler-gated across every file it touches. dryRun defaults to true.")]
    public static Task<object> ConvertTypeKind(
        IConvertTypeKindService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Class or interface to convert.")] string typeName,
        [Description("'interface' (abstract class → interface) or 'abstract-class' (interface → abstract class).")] string target,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ConvertTypeKind(filePath, typeName, target, dryRun);
}
