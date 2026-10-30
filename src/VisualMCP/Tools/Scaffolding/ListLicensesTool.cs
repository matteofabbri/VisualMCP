using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Scaffolding;

namespace VisualMCP.Tools.Scaffolding;

[McpServerToolType]
public static class ListLicensesTool
{
    [McpServerTool(Name = "list_licenses"), Description(
        "List every LICENSE template add_license can write: id, display name, SPDX identifier, family " +
        "(permissive, weak-copyleft, strong-copyleft, network-copyleft, public-domain, source-available), " +
        "OSI approval and whether the license carries an explicit patent grant. Call it before " +
        "add_license when the user has not named a license.")]
    public static object ListLicenses(IScaffoldService scaffold) => scaffold.ListLicenses();
}
