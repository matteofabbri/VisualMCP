using System.Text.RegularExpressions;

namespace VsSolutionServer.Parsing;

public static class SlnParser
{
    private static readonly Regex ProjectLineRx = new(
        @"Project\(""\{(?<typeGuid>[^}]+)\}""\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled);

    public static SolutionInfo Parse(string slnPath)
    {
        var lines = File.ReadAllLines(slnPath);
        var projects = new List<SolutionProject>();

        foreach (var line in lines)
        {
            var m = ProjectLineRx.Match(line);
            if (!m.Success) continue;

            var typeGuid = m.Groups["typeGuid"].Value;
            // Skip solution folders
            if (typeGuid.Equals("2150E333-8FDC-42A3-9474-1A3956D46DE8", StringComparison.OrdinalIgnoreCase))
                continue;

            projects.Add(new SolutionProject(
                Name: m.Groups["name"].Value,
                Path: m.Groups["path"].Value,
                TypeGuid: typeGuid));
        }

        return new SolutionInfo(slnPath, projects);
    }
}
