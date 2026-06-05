using ModelContextProtocol.Server;
using System.ComponentModel;
using VisualMCP.Implementation.CSharp.Navigation;

namespace VisualMCP.Tools.CSharp.Navigation;

[McpServerToolType]
public static class GetTypeMembersTool
{
    [McpServerTool, Description(
        "When you need the full member list of a type (methods, properties, fields, events, constructors) with resolved signatures and XML docs, use this INSTEAD OF reading the file. " +
        "Roslyn gives fully-resolved signatures and can include inherited members from base types, which reading one file cannot show. " +
        "The working-directory solution auto-loads on first use.")]
    public static Task<object> GetTypeMembers(
        [Description("Full or partial type name (class, interface, struct, record, enum)")] string typeName,
        [Description("Include inherited members from base types (default: false)")] bool includeInherited = false)
        => NavigationImpl.GetTypeMembersAsync(typeName, includeInherited);
}
