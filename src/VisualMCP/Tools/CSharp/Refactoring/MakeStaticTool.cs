using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class MakeStaticTool
{
    [McpServerTool(Name = "make_static"), Description(
        "Make an instance method or property static and rewrite its call sites (Rider's 'Make static'): " +
        "`instance.M(...)` becomes `Type.M(...)`, using the shortest type name the file can see. It REFUSES when the " +
        "member touches instance state (fields, properties, methods, this/base), implements an interface member, is " +
        "abstract/virtual/override, or is called through '?.'. Overloads must be addressed one declaration at a time. " +
        "dryRun defaults to true and returns one line diff per affected file.")]
    public static Task<object> MakeStatic(
        IMakeStaticService svc,
        [Description("File that declares the member (absolute or relative).")] string filePath,
        [Description("Member name (method or property).")] string memberName,
        [Description("Optional containing type name, when a nested type declares a member with the same name.")] string? containingType = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.MakeStatic(filePath, memberName, containingType, dryRun);
}
