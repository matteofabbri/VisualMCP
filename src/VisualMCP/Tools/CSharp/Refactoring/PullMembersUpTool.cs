using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class PullMembersUpTool
{
    [McpServerTool, Description(
        "Move one or more members from a class up into its base class or a directly implemented interface. " +
        "When pulling into an interface, only the signature is added (the implementation stays in the class). " +
        "When pulling into a base class, the member is physically moved. " +
        "Equivalent to ReSharper 'Pull Members Up'. Requires LoadSolution first.")]
    public static Task<object> PullMembersUp(
        IPullMembersUpService svc,
        [Description("Name of the source class")] string className,
        [Description("Names of the members to pull up")] string[] memberNames,
        [Description("Name of the target base class or interface to pull into (must already be in the class's base list)")] string targetName,
        [Description("Dry run — show changes without writing to disk (default: false)")] bool dryRun = false)
        => svc.PullMembersUp(className, memberNames, targetName, dryRun);
}
