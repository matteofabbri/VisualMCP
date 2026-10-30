using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class GenerateMembersTool
{
    [McpServerTool, Description(
        "When a class needs boilerplate, use this INSTEAD OF hand-writing it: it generates a constructor (from fields/properties), value-equality Equals/GetHashCode, and/or ToString " +
        "from the class's actual members, inserts them and writes to disk. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> GenerateMembers(
        IGenerateMembersService svc,
        [Description("Name of the class to generate members for")] string className,
        [Description("Generate a constructor that assigns all eligible fields/properties (default: true)")] bool generateConstructor = true,
        [Description("Generate Equals and GetHashCode based on all eligible fields/properties (default: true)")] bool generateEquality = true,
        [Description("Generate a ToString returning a readable summary of all eligible fields/properties (default: true)")] bool generateToString = true,
        [Description("Dry run — return generated code without writing to disk (default: false)")] bool dryRun = false)
        => svc.GenerateMembers(className, generateConstructor, generateEquality, generateToString, dryRun);
}