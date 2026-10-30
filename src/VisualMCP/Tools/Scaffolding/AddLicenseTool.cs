using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Scaffolding;

namespace VisualMCP.Tools.Scaffolding;

[McpServerToolType]
public static class AddLicenseTool
{
    [McpServerTool(Name = "add_license"), Description(
        "Write a LICENSE file into a repository from a bundled template. 20 licenses, or call " +
        "list_licenses for the catalogue with SPDX ids, copyleft family, OSI approval and patent " +
        "grants. Common ids: mit, apache-2.0, bsd-2-clause, bsd-3-clause, gpl-3.0-only, " +
        "lgpl-3.0-only, agpl-3.0-only, mpl-2.0, ms-pl, unlicense. Also: 0bsd, boost-1.0, cc0-1.0, " +
        "epl-2.0, gpl-2.0-only, isc, lgpl-2.1-only, polyform-noncommercial-1.0.0, " +
        "polyform-small-business-1.0.0, zlib. Use this INSTEAD OF hand-writing or shell-copying a license.")]
    public static object AddLicense(
        IScaffoldService scaffold,
        [Description("License id, e.g. 'mit', 'apache-2.0' or 'gpl-3.0-only' (call list_licenses for all).")] string licenseId,
        [Description("Repository directory to write LICENSE into.")] string directory,
        [Description("Copyright holder (name/org). Defaults to 'the project authors'.")] string? holder = null,
        [Description("Copyright year (defaults to the current year).")] int? year = null,
        [Description("Overwrite an existing LICENSE (default: false).")] bool overwrite = false)
        => scaffold.AddLicense(licenseId, directory, holder, year, overwrite);
}
