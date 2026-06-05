using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Scaffolding;

namespace VisualMCP.Tools.Scaffolding;

[McpServerToolType]
public static class AddLicenseTool
{
    [McpServerTool(Name = "add_license"), Description(
        "Write a LICENSE file into a repository from a template. Supported ids: mit, isc, bsd-2-clause, " +
        "unlicense, polyform-noncommercial-1.0.0 (free for noncommercial, commercial requires a paid " +
        "license). Use this INSTEAD OF hand-writing or shell-copying a license.")]
    public static object AddLicense(
        [Description("License id, e.g. 'mit' or 'polyform-noncommercial-1.0.0'.")] string licenseId,
        [Description("Repository directory to write LICENSE into.")] string directory,
        [Description("Copyright holder (name/org). Defaults to 'the project authors'.")] string? holder = null,
        [Description("Copyright year (defaults to the current year).")] int? year = null,
        [Description("Overwrite an existing LICENSE (default: false).")] bool overwrite = false)
        => ScaffoldImpl.AddLicense(licenseId, directory, holder, year, overwrite);
}
