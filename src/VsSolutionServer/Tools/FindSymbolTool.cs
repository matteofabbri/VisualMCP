using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;
using VsSolutionServer.Parsing;

namespace VsSolutionServer.Tools;

[McpServerToolType]
public static class FindSymbolTool
{
    [McpServerTool, Description("Search for a class, interface, method, record, or enum by name across all .cs files in a solution or project directory.")]
    public static object FindSymbol(
        [Description("Symbol name to search for (class, interface, method, record, enum)")] string symbolName,
        [Description("Root directory to search in (solution folder or project folder)")] string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
            return new { error = $"Directory not found: {rootPath}" };

        var pattern = new Regex(
            $@"\b(class|interface|record|enum|struct)\s+{Regex.Escape(symbolName)}\b|\bvoid\s+{Regex.Escape(symbolName)}\s*[\(<]|\b\w[\w<>]*\s+{Regex.Escape(symbolName)}\s*[\(<]",
            RegexOptions.Compiled);

        var files = Directory.GetFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var results = new List<object>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                    results.Add(new { file, line = i + 1, text = lines[i].Trim() });
            }
        }

        return new { symbolName, rootPath, matchCount = results.Count, matches = results };
    }
}
