using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ImplementMembersTool
{
    [McpServerTool(Name = "implement_members"), Description(
        "ReSharper's 'Implement missing members': for a class that declares an interface or derives from " +
        "an abstract class, generates one stub for each member it still owes — real signature from the " +
        "symbol, body that throws NotImplementedException. It is refused unless the CS0535/CS0534 errors " +
        "for that type actually disappear and the project gains no error. Dry run by default.")]
    public static Task<object> ImplementMembers(
        IImplementMembersService svc,
        [Description("The file that declares the type")] string filePath,
        [Description("The class or struct to complete")] string typeName,
        [Description("Preview only (default true). Pass false to write the file.")] bool dryRun = true)
        => svc.ImplementMembers(filePath, typeName, dryRun);
}
