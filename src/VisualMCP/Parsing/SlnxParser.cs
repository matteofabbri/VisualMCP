using System.Xml.Linq;
using VisualMCP.Parsing.Model;

namespace VisualMCP.Parsing;

public static class SlnxParser
{
    public static SolutionInfo Parse(string slnxPath)
    {
        var doc = XDocument.Load(slnxPath);
        var projects = doc.Descendants("Project")
            .Select(p => new SolutionProject(
                Name: p.Attribute("DisplayName")?.Value ?? System.IO.Path.GetFileNameWithoutExtension(p.Attribute("Path")?.Value ?? ""),
                Path: p.Attribute("Path")?.Value ?? "",
                TypeGuid: p.Attribute("TypeGuid")?.Value))
            .ToList();

        return new SolutionInfo(slnxPath, projects);
    }
}
