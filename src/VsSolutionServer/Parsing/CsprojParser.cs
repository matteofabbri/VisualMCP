using System.Xml.Linq;

namespace VsSolutionServer.Parsing;

public record ProjectInfo(
    string ProjectPath,
    string Name,
    string TargetFramework,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<PackageRef> PackageReferences);

public record PackageRef(string Name, string Version);

public static class CsprojParser
{
    public static ProjectInfo Parse(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);
        var dir = Path.GetDirectoryName(csprojPath)!;

        var tf = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
               ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value
               ?? "unknown";

        var sourceFiles = doc.Descendants("Compile")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v != null)
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!)))
            .ToList();

        // If no explicit Compile items, glob *.cs (SDK-style project)
        if (sourceFiles.Count == 0)
            sourceFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToList();

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v != null)
            .Select(v => Path.GetFullPath(Path.Combine(dir, v!)))
            .ToList()!;

        var packages = doc.Descendants("PackageReference")
            .Select(e => new PackageRef(
                e.Attribute("Include")?.Value ?? "",
                e.Attribute("Version")?.Value ?? e.Element("Version")?.Value ?? ""))
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .ToList();

        return new ProjectInfo(
            ProjectPath: csprojPath,
            Name: Path.GetFileNameWithoutExtension(csprojPath),
            TargetFramework: tf,
            SourceFiles: sourceFiles,
            ProjectReferences: projectRefs,
            PackageReferences: packages);
    }
}
