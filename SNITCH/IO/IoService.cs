using Microsoft.CodeAnalysis;
using Snitch.Languages.CSharp.Parsing;
using Snitch.Languages.CSharp.Parsing.Model;
using Snitch.Workspace;

namespace Snitch.IO;

/// <summary>
/// Default <see cref="IIoService"/>: the filesystem work is done here, the solution-shaped
/// answers come from the active <see cref="IRoslynWorkspace"/>.
/// </summary>
public sealed class IoService : IIoService
{
    private readonly IRoslynWorkspace _workspace;

    public IoService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── load_solution ─────────────────────────────────────────────────────────
    public async Task<LoadSolutionResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            return new LoadSolutionResult { Error = $"File not found: {path}" };
        }

        var result = await _workspace.LoadSolutionAsync(path, cancellationToken);
        var solution = result.Solution;

        var projects = solution.Projects
            .Select(p => new LoadedProject(
                p.Name,
                p.FilePath,
                p.Language,
                p.AssemblyName,
                p.Documents.Count(),
                p.ProjectReferences.Count(),
                p.MetadataReferences.Count))
            .ToList();

        return new LoadSolutionResult
        {
            Solution = Path.GetFileName(path),
            Directory = Path.GetDirectoryName(path),
            ProjectCount = projects.Count,
            WorkspaceDiagnostics = result.Diagnostics,
            Projects = projects,
        };
    }

    // ── list_solutions ────────────────────────────────────────────────────────
    public ListSolutionsResult ListSolutions(string rootPath, int maxDepth)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
        {
            return new ListSolutionsResult { Error = $"Directory not found: {rootPath}" };
        }

        var solutions = SolutionDiscovery.Enumerate(rootPath, maxDepth)
            .Select(f => new SolutionFileEntry(f, Path.GetRelativePath(rootPath, f)))
            .ToList();

        return new ListSolutionsResult
        {
            RootPath = rootPath,
            SolutionCount = solutions.Count,
            Solutions = solutions,
        };
    }

    // ── read_file ─────────────────────────────────────────────────────────────
    public ReadFileResult ReadFile(string path, int? fromLine, int? toLine)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            return new ReadFileResult { Path = path, Error = $"File not found: {path}" };
        }

        var lines = File.ReadAllLines(path);
        int start = Math.Max(0, (fromLine ?? 1) - 1);
        int end = Math.Min(lines.Length, toLine ?? lines.Length);

        var numbered = lines[start..end].Select((l, i) => $"{start + i + 1,5}: {l}").ToList();
        return new ReadFileResult
        {
            Path = path,
            TotalLines = lines.Length,
            ReturnedLines = numbered.Count,
            Content = string.Join("\n", numbered),
        };
    }

    // ── check_solution ────────────────────────────────────────────────────────
    public CheckSolutionResult CheckSolution()
    {
        var solution = _workspace.CurrentSolution;
        var solutionPath = _workspace.LoadedSolutionPath;

        if (solution is null || solutionPath is null)
        {
            return new CheckSolutionResult
            {
                IsLoaded = false,
                Message = "No .NET solution is currently loaded. Load one first, or stop here if the current workspace is not a .NET C# or Visual Basic project — there is nothing useful to offer outside of a .NET solution.",
            };
        }

        var projects = solution.Projects.ToList();
        var csharpProjects = projects.Count(project => project.Language == LanguageNames.CSharp);
        var visualBasicProjects = projects.Count(project => project.Language == LanguageNames.VisualBasic);

        if (csharpProjects == 0 && visualBasicProjects == 0)
        {
            return new CheckSolutionResult
            {
                IsLoaded = false,
                SolutionPath = solutionPath,
                Message = "The loaded solution contains no C# or Visual Basic projects. Only .NET C# and Visual Basic solutions are supported.",
            };
        }

        // A Visual Basic solution is a supported workspace: the language-neutral tools and the VB
        // style engine work on it, and only the C#-only analyses have nothing to read.
        if (csharpProjects == 0)
        {
            return new CheckSolutionResult
            {
                IsLoaded = true,
                SolutionPath = solutionPath,
                CSharpProjectCount = 0,
                VisualBasicProjectCount = visualBasicProjects,
                Message = $"A Visual Basic solution is loaded with {visualBasicProjects} project(s). Language-neutral tools (find_symbol, read_symbol, text_index, build_project, run_tests, run_project) work, and simplify_vb_code applies the Visual Basic style rules; C#-only analyses (simplify_code, reference_index, inspect_code, complexity_report) have no C# documents to read.",
            };
        }

        return new CheckSolutionResult
        {
            IsLoaded = true,
            SolutionPath = solutionPath,
            CSharpProjectCount = csharpProjects,
            VisualBasicProjectCount = visualBasicProjects,
            Message = visualBasicProjects == 0
                ? $"A .NET C# solution is loaded with {csharpProjects} project(s). You can proceed with other tools."
                : $"A .NET solution is loaded with {csharpProjects} C# and {visualBasicProjects} Visual Basic project(s). C#-only tools read the C# projects; simplify_vb_code reads the Visual Basic ones.",
        };
    }

    // ── get_project_info ──────────────────────────────────────────────────────
    public async Task<ProjectInfoResult> GetProjectInfoAsync(string nameOrPath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        if (solution is null)
        {
            return new ProjectInfoResult
            {
                Error = "No C# solution could be auto-located from the working directory. Provide an explicit path to the .sln/.slnx.",
            };
        }

        var project = FindProject(solution, nameOrPath);
        if (project is null)
        {
            return new ProjectInfoResult { Error = $"Project not found: {nameOrPath}" };
        }

        var sourceFiles = project.Documents
            .Where(d => d.SourceCodeKind == SourceCodeKind.Regular)
            .Select(d => d.FilePath)
            .Where(f => f is not null)
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projectRefs = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.Name ?? r.ProjectId.ToString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var metaRefs = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath))
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var packages = new List<PackageRef>();
        if (project.FilePath is not null && File.Exists(project.FilePath))
        {
            try
            {
                packages = CsprojParser.Parse(project.FilePath).PackageReferences.ToList();
            }
            catch
            {
                // An unreadable or malformed .csproj leaves the package list empty.
            }
        }

        return new ProjectInfoResult
        {
            Name = project.Name,
            AssemblyName = project.AssemblyName,
            Language = project.Language,
            FilePath = project.FilePath,
            OutputType = project.CompilationOptions?.OutputKind.ToString(),
            DocumentCount = sourceFiles.Count,
            SourceFiles = sourceFiles,
            ProjectReferences = projectRefs,
            AssemblyReferences = metaRefs,
            PackageReferences = packages,
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
    public ListDirectoryResult ListDirectory(string path, string pattern, bool recursive, string[]? nameContainsAny, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ListDirectoryResult { Error = "No path provided." };
        }

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return new ListDirectoryResult
            {
                Path = path,
                Exists = true,
                Type = "file",
                SizeBytes = file.Length,
                SizeMB = Mb(file.Length),
                LastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        if (!Directory.Exists(path))
        {
            return new ListDirectoryResult { Path = path, Exists = false };
        }

        maxEntries = Math.Clamp(maxEntries, 1, 5_000);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };

        long totalSize = 0;
        var matched = 0;
        var entries = new List<(string Name, string RelativePath, long Size, DateTime Modified)>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, pattern, options))
            {
                var name = Path.GetFileName(file);
                if (nameContainsAny is { Length: > 0 } &&
                    !nameContainsAny.Any(s => !string.IsNullOrEmpty(s) && name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                long size;
                DateTime modified;
                try
                {
                    var info = new FileInfo(file);
                    size = info.Length;
                    modified = info.LastWriteTime;
                }
                catch
                {
                    size = 0;
                    modified = default;
                }

                matched++;
                totalSize += size;
                if (entries.Count < maxEntries)
                {
                    entries.Add((name, Path.GetRelativePath(path, file), size, modified));
                }
            }
        }
        catch (ArgumentException ex)
        {
            return new ListDirectoryResult { Error = $"Invalid pattern '{pattern}': {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new ListDirectoryResult { Error = $"Failed to list '{path}': {ex.Message}" };
        }

        var files = entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new DirectoryFileEntry(
                e.Name,
                e.RelativePath,
                e.Size,
                Mb(e.Size),
                e.Modified == default ? null : e.Modified.ToString("yyyy-MM-dd HH:mm:ss")))
            .ToList();

        List<string>? subdirectories = null;
        if (!recursive)
        {
            try
            {
                subdirectories = Directory.EnumerateDirectories(path)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
            }
            catch
            {
                // Inaccessible directory: the inventory is returned without the subdirectory list.
            }
        }

        return new ListDirectoryResult
        {
            Path = path,
            Exists = true,
            Type = "directory",
            Pattern = pattern,
            Recursive = recursive,
            MatchedFileCount = matched,
            MatchedTotalSizeBytes = totalSize,
            MatchedTotalSizeMB = Mb(totalSize),
            ReturnedCount = files.Count,
            Truncated = matched > files.Count,
            Subdirectories = subdirectories,
            Files = files,
        };
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1024d / 1024d, 2);

    // ── move_path (move/rename a file or directory) ───────────────────────────
    public MovePathResult MovePath(string source, string destination, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new MovePathResult { Error = "A source path is required." };
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            return new MovePathResult { Error = "A destination path is required." };
        }

        var src = Path.GetFullPath(source);
        var dst = Path.GetFullPath(destination);

        var isFile = File.Exists(src);
        var isDir = Directory.Exists(src);
        if (!isFile && !isDir)
        {
            return new MovePathResult { Error = $"Source not found: {src}" };
        }

        try
        {
            var destParent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            if (isFile)
            {
                if (File.Exists(dst) && !overwrite)
                {
                    return new MovePathResult { Error = $"Destination file already exists: {dst} (set overwrite=true)." };
                }

                File.Move(src, dst, overwrite);
            }
            else
            {
                if (Directory.Exists(dst) || File.Exists(dst))
                {
                    return new MovePathResult { Error = $"Destination already exists: {dst} (directory move does not overwrite)." };
                }

                Directory.Move(src, dst);
            }

            return new MovePathResult
            {
                Moved = true,
                Type = isFile ? "file" : "directory",
                Source = src,
                Destination = dst,
            };
        }
        catch (Exception ex)
        {
            return new MovePathResult { Error = $"Move failed: {ex.Message}", Source = src, Destination = dst };
        }
    }
}
