using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConstructorToFactoryTool
{
    [McpServerTool(Name = "constructor_to_factory"), Description(
        "Turn the single public constructor of a type into a static factory (Rider's 'Constructor to factory method'): " +
        "the constructor becomes private, a public static Create(...) returns new instances, and every 'new T(args)' " +
        "becomes 'T.Create(args)'. Refuses when the type has several constructors, a non-public one, a member already " +
        "named like the factory, or a call site that uses an object initializer (it cannot be expressed as a call). " +
        "GATED on the compiler. dryRun defaults to true.")]
    public static Task<object> ConstructorToFactory(
        IConstructorToFactoryService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Type whose constructor becomes a factory.")] string typeName,
        [Description("Factory method name (default: Create).")] string factoryName = "Create",
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.ConstructorToFactory(filePath, typeName, factoryName, dryRun);
}
