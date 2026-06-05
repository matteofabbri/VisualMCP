using Microsoft.CodeAnalysis;
using VisualMCP.Parsing;
using VisualMCP.Workspace;

namespace VisualMCP.Implementation.IO;

internal static class IoImpl
{
    // ── load_solution ─────────────────────────────────────────────────────────
    internal static async Task<object> LoadAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return new { error = $"File not found: {path}" };

        var result = await RoslynWorkspaceService.Instance.LoadSolutionAsync(path);
        var solution = result.Solution;

        var projects = solution.Projects.Select(p => new
        {
            p.Name,
            Path = p.FilePath,
            Language = p.Language,
            AssemblyName = p.AssemblyName,
            DocumentCount = p.Documents.Count(),
            ProjectReferenceCount = p.ProjectReferences.Count(),
            MetadataReferenceCount = p.MetadataReferences.Count,
        }).ToList();

        return new
        {
            solution = Path.GetFileName(path),
            directory = Path.GetDirectoryName(path),
            projectCount = projects.Count,
            workspaceDiagnostics = result.Diagnostics,
            projects,
        };
    }

    // ── list_solutions ────────────────────────────────────────────────────────
    internal static object ListSolutions(string rootPath, int maxDepth)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath)) return new { error = $"Directory not found: {rootPath}" };

        var found = new List<object>();
        ScanDir(rootPath, rootPath, 0, maxDepth, found);
        return new { rootPath, solutionCount = found.Count, solutions = found };
    }

    private static void ScanDir(string dir, string root, int depth, int maxDepth, List<object> found)
    {
        if (depth > maxDepth) return;
        foreach (var f in Directory.GetFiles(dir, "*.sln").Concat(Directory.GetFiles(dir, "*.slnx")))
            found.Add(new { path = f, relativePath = Path.GetRelativePath(root, f) });
        try { foreach (var sub in Directory.GetDirectories(dir)) ScanDir(sub, root, depth + 1, maxDepth, found); }
        catch (UnauthorizedAccessException) { }
    }

    // ── read_file ─────────────────────────────────────────────────────────────
    internal static object ReadFile(string path, int? fromLine, int? toLine)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return new { error = $"File not found: {path}" };

        var lines = File.ReadAllLines(path);
        int start = Math.Max(0, (fromLine ?? 1) - 1);
        int end = Math.Min(lines.Length, toLine ?? lines.Length);

        var numbered = lines[start..end].Select((l, i) => $"{start + i + 1,5}: {l}").ToList();
        return new { path, totalLines = lines.Length, returnedLines = numbered.Count, content = string.Join("\n", numbered) };
    }

    // ── check_solution ────────────────────────────────────────────────────────
    internal static object CheckSolution()
    {
        var svc = RoslynWorkspaceService.Instance;

        if (svc.CurrentSolution is null || svc.LoadedSolutionPath is null)
            return new
            {
                isLoaded = false,
                message = "No .NET C# solution is currently loaded. Load one first with load_solution, or stop here if the current workspace is not a .NET C# project — this plugin has nothing useful to offer outside of a .NET C# solution."
            };

        var hasCSharp = svc.CurrentSolution.Projects.Any(p => p.Language == LanguageNames.CSharp);
        if (!hasCSharp)
            return new
            {
                isLoaded = false,
                solutionPath = svc.LoadedSolutionPath,
                message = "The loaded solution contains no C# projects. This plugin only works with .NET C# solutions — do not query it further for this workspace."
            };

        var projectCount = svc.CurrentSolution.Projects.Count();
        return new
        {
            isLoaded = true,
            solutionPath = svc.LoadedSolutionPath,
            csharpProjectCount = projectCount,
            message = $"A .NET C# solution is loaded with {projectCount} project(s). You can proceed with other tools."
        };
    }

    // ── get_project_info ──────────────────────────────────────────────────────
    internal static async Task<object> GetProjectInfoAsync(string nameOrPath)
    {
        var solution = await RoslynWorkspaceService.Instance.EnsureSolutionLoadedAsync();
        if (solution is null)
            return new { error = "No C# solution could be auto-located from the working directory. Call load_solution with an explicit path to the .sln/.slnx." };

        var project = FindProject(solution, nameOrPath);
        if (project is null) return new { error = $"Project not found: {nameOrPath}" };

        var sourceFiles = project.Documents.Where(d => d.SourceCodeKind == SourceCodeKind.Regular)
            .Select(d => d.FilePath).OrderBy(f => f).ToList();

        var projectRefs = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString())
            .OrderBy(n => n).ToList();

        var metaRefs = project.MetadataReferences.OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath)).Where(n => n is not null)
            .OrderBy(n => n).ToList();

        var packages = new List<object>();
        if (project.FilePath is not null && File.Exists(project.FilePath))
        {
            try
            {
                var csprojInfo = CsprojParser.Parse(project.FilePath);
                packages = csprojInfo.PackageReferences.Select(p => (object)new { p.Name, p.Version }).ToList();
            }
            catch { }
        }

        return new
        {
            Name = project.Name,
            AssemblyName = project.AssemblyName,
            Language = project.Language,
            FilePath = project.FilePath,
            OutputType = project.CompilationOptions?.OutputKind.ToString(),
            DocumentCount = sourceFiles.Count,
            sourceFiles,
            projectReferences = projectRefs,
            assemblyReferences = metaRefs,
            packageReferences = packages,
        };
    }

    private static Project? FindProject(Microsoft.CodeAnalysis.Solution solution, string nameOrPath)
    {
        var byName = solution.Projects.FirstOrDefault(p => string.Equals(p.Name, nameOrPath, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName;
        var absPath = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.GetFullPath(nameOrPath);
        return solution.Projects.FirstOrDefault(p => string.Equals(p.FilePath, absPath, StringComparison.OrdinalIgnoreCase));
    }

    // ── list_directory ────────────────────────────────────────────────────────
    internal static object ListDirectory(string path, string pattern, bool recursive, string[]? nameContainsAny, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(path)) return new { error = "No path provided." };

        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return new { path, exists = true, type = "file", sizeBytes = fi.Length, sizeMB = Mb(fi.Length), lastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") };
        }
        if (!Directory.Exists(path)) return new { path, exists = false };

        if (maxEntries < 1) maxEntries = 1;
        if (maxEntries > 5000) maxEntries = 5000;

        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, MatchType = MatchType.Simple };

        long totalSize = 0; var matched = 0;
        var entries = new List<(string name, string rel, long size, DateTime modified)>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern, options))
            {
                var name = Path.GetFileName(file);
                if (nameContainsAny is { Length: > 0 } &&
                    !nameContainsAny.Any(s => !string.IsNullOrEmpty(s) && name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    continue;

                long size; DateTime modified;
                try { var fi = new FileInfo(file); size = fi.Length; modified = fi.LastWriteTime; }
                catch { size = 0; modified = default; }

                matched++; totalSize += size;
                if (entries.Count < maxEntries) entries.Add((name, Path.GetRelativePath(path, file), size, modified));
            }
        }
        catch (ArgumentException ex) { return new { error = $"Invalid pattern '{pattern}': {ex.Message}" }; }
        catch (Exception ex) { return new { error = $"Failed to list '{path}': {ex.Message}" }; }

        var files = entries.OrderBy(e => e.name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new { e.name, relativePath = e.rel, sizeBytes = e.size, sizeMB = Mb(e.size), lastModified = e.modified == default ? null : e.modified.ToString("yyyy-MM-dd HH:mm:ss") })
            .ToList();

        List<string>? subdirectories = null;
        if (!recursive)
        {
            try
            {
                subdirectories = Directory.EnumerateDirectories(path).Select(Path.GetFileName)
                    .Where(n => n is not null).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()!;
            }
            catch { }
        }

        return new
        {
            path, exists = true, type = "directory", pattern, recursive,
            matchedFileCount = matched, matchedTotalSizeBytes = totalSize, matchedTotalSizeMB = Mb(totalSize),
            returnedCount = files.Count, truncated = matched > files.Count, subdirectories, files,
        };
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024d / 1024d, 2);
}
