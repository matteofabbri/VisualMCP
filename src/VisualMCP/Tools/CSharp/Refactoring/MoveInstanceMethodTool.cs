using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MoveInstanceMethodTool
{
    [McpServerTool(Name = "move_instance_method"), Description(
        "Rider's 'Move instance method': the method becomes a STATIC method of the target type with the source " +
        "instance as its first parameter (`receiverName`), `this`/implicit instance members inside the body are " +
        "re-pointed at that parameter, and every call `a.M(x)` becomes `Target.M(a, x)`. Refuses virtual/override/" +
        "abstract methods, `base` usage, overloads (move one at a time) and method-group uses. Compiler-gated. " +
        "dryRun defaults to true.")]
    public static Task<object> MoveInstanceMethod(
        IMoveInstanceMethodService svc,
        [Description("File that declares the source type (absolute or relative).")] string filePath,
        [Description("Type the method currently lives in.")] string typeName,
        [Description("Method to move.")] string methodName,
        [Description("Type that receives it: a simple name, or a fully qualified one when ambiguous.")] string targetTypeName,
        [Description("Name of the parameter that replaces `this` (default: self).")] string receiverName = "self",
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MoveInstanceMethod(filePath, typeName, methodName, targetTypeName, receiverName, dryRun);
}
