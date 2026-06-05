using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Scaffolding;

namespace VisualMCP.Tools.Scaffolding;

[McpServerToolType]
public static class AddContributingTool
{
    [McpServerTool(Name = "add_contributing"), Description(
        "Write a CONTRIBUTING.md into a repository, optionally including a Contributor License Agreement (CLA) " +
        "that grants the maintainer a broad license — with the right to relicense commercially — over " +
        "contributions (important when the project is dual/commercial-licensed). Use INSTEAD OF hand-writing it.")]
    public static object AddContributing(
        [Description("Repository directory to write CONTRIBUTING.md into.")] string directory,
        [Description("The maintainer name/org the CLA grants rights to (e.g. 'Matteo Fabbri').")] string? holder = null,
        [Description("Human name of the license (e.g. 'the PolyForm Noncommercial License').")] string? licenseName = null,
        [Description("Include the CLA section (default: true).")] bool includeCla = true,
        [Description("Overwrite an existing CONTRIBUTING.md (default: false).")] bool overwrite = false)
        => ScaffoldImpl.AddContributing(directory, holder, licenseName, includeCla, overwrite);
}
