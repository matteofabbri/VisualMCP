using System.Runtime.CompilerServices;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// Reads an MSBuild binary log and answers what the build actually did: the errors and warnings with
/// their project, the duration of every project and every target, which target dominates the wall
/// clock, and the project dependency graph the build walked. When no log is given it runs the build
/// first (writing the log under <c>.visualMCP/binlogs</c>) and analyses that.
/// </summary>
public interface IBinlogAnalysisService
{
    Task<object> AnalyzeAsync(
        string? binlogPath, string? projectName, string configuration, int top, int timeoutSeconds);
}

public sealed class BinlogAnalysisService : IBinlogAnalysisService
{
    private readonly IRoslynWorkspace _workspace;

    public BinlogAnalysisService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<object> AnalyzeAsync(
        string? binlogPath, string? projectName, string configuration, int top, int timeoutSeconds)
    {
        top = top < 1 ? 25 : top > 500 ? 500 : top;
        configuration = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();

        object? build = null;
        var path = binlogPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            var solution = await _workspace.EnsureSolutionLoadedAsync();

            string target;
            string root;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var project = solution?.Projects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, projectName, StringComparison.OrdinalIgnoreCase));
                if (project?.FilePath is null)
                    return new { error = $"Project '{projectName}' not found in the solution.", knownProjects = solution?.Projects.Select(candidate => candidate.Name) };

                target = project.FilePath;
                root = Path.GetDirectoryName(_workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath() ?? target)!;
            }
            else
            {
                var solutionPath = _workspace.LoadedSolutionPath ?? _workspace.DiscoverSolutionPath() ?? solution?.FilePath;
                if (solutionPath is null)
                    return new { error = "No C# solution could be auto-located, so there is nothing to build. Pass binlogPath to analyse an existing log." };

                target = solutionPath;
                root = Path.GetDirectoryName(solutionPath)!;
            }

            var directory = Path.Combine(root, ".visualMCP", "binlogs");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"build-{DateTime.Now:yyyyMMdd-HHmmss}.binlog");

            var arguments = $"build \"{target}\" -nologo -v:m -c {configuration} -bl:\"{path}\"";
            var (exitCode, timedOut, stdout, stderr, elapsed) = await ProcessRunner.RunAsync(
                "dotnet", arguments, Path.GetDirectoryName(target)!, Clamp(timeoutSeconds));

            if (timedOut)
                return new { error = $"dotnet build timed out after {Clamp(timeoutSeconds)}s; no complete binlog was produced.", command = $"dotnet {arguments}", binlog = path };

            build = new
            {
                exitCode,
                durationMs = (long)elapsed.TotalMilliseconds,
                output = ProcessRunner.Truncate((stdout + "\n" + stderr).Trim(), 4000),
            };
        }

        if (!File.Exists(path))
            return new { error = $"Binary log '{path}' does not exist.", generated = build is not null };

        Facts facts;
        try
        {
            facts = Replay(path);
        }
        catch (Exception exception)
        {
            return new
            {
                error = $"Could not read the binary log: {exception.Message}",
                binlog = path,
                note = "The MSBuild reader must match the compiler that wrote the log. Re-generate it with " +
                       "'dotnet build -bl:file.binlog' on this machine (or call this tool without binlogPath).",
            };
        }

        return new
        {
            binlog = path,
            generatedByThisCall = build is not null,
            build,
            summary = new
            {
                succeeded = facts.ErrorCount == 0,
                durationMs = (long)Math.Max(0, facts.Duration.TotalMilliseconds),
                startTime = facts.Start,
                endTime = facts.End,
                projects = facts.Projects.Count,
                targets = facts.Targets.Count,
                errors = facts.ErrorCount,
                warnings = facts.WarningCount,
            },
            slowestProjects = facts.Projects
                .OrderByDescending(project => project.Duration)
                .Take(top)
                .Select(project => new
                {
                    project = project.Name,
                    file = project.File,
                    durationMs = (long)project.Duration.TotalMilliseconds,
                    succeeded = project.Succeeded,
                    parent = facts.Projects.FirstOrDefault(candidate => candidate.ContextId == project.ParentContextId)?.Name,
                    targets = facts.Targets.Count(target => target.ProjectContextId == project.ContextId),
                })
                .ToList(),
            slowestTargets = facts.Targets
                .OrderByDescending(target => target.Duration)
                .Take(top)
                .Select(target => new
                {
                    project = target.Project,
                    target = target.Name,
                    durationMs = (long)target.Duration.TotalMilliseconds,
                    succeeded = target.Succeeded,
                })
                .ToList(),
            targetTotals = facts.Targets
                .GroupBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    target = group.Key,
                    runs = group.Count(),
                    totalMs = (long)group.Sum(target => target.Duration.TotalMilliseconds),
                    averageMs = (long)group.Average(target => target.Duration.TotalMilliseconds),
                })
                .OrderByDescending(row => row.totalMs)
                .Take(top)
                .ToList(),
            errors = facts.Errors.Take(top).ToList(),
            warnings = facts.Warnings.Take(top).ToList(),
            graph = new
            {
                nodes = facts.Projects.Select(project => project.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                edges = facts.Projects
                    .Where(project => project.ParentContextId is not null &&
                                      facts.Projects.Any(parent => parent.ContextId == project.ParentContextId))
                    .Select(project => new
                    {
                        from = facts.Projects.First(parent => parent.ContextId == project.ParentContextId).Name,
                        to = project.Name,
                    })
                    .Distinct()
                    .ToList(),
            },
            note = facts.ErrorCount > 0
                ? "The build failed: the errors below carry the project, file and line that produced them."
                : "Self time is not reported — a target's duration includes the projects it builds; compare it with its children in the graph before treating it as the problem.",
        };
    }

    // ── replay ────────────────────────────────────────────────────────────────
    private static Facts Replay(string path)
    {
        // Registration has to happen before a single MSBuild type is touched, or the CLR tries to
        // resolve Microsoft.Build.dll from the app folder and fails: the assembly lives in the SDK.
        EnsureMsBuildLoaded();
        return ReplayCore(path);
    }

    /// <summary>
    /// The whole analysis is these event pairs: every project and target reports a start and a finish
    /// from the same log clock, so the difference is the real duration and not a wall-clock guess.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Facts ReplayCore(string path)
    {
        var facts = new Facts();
        var projects = new Dictionary<int, ProjectFacts>();
        var targets = new Dictionary<(int Project, int Target), PendingTarget>();

        var source = new BinaryLogReplayEventSource();

        source.BuildStarted += (_, args) => facts.Start = args.Timestamp;
        source.BuildFinished += (_, args) => facts.End = args.Timestamp;

        source.ProjectStarted += (_, args) =>
        {
            var context = args.BuildEventContext?.ProjectContextId ?? -1;
            var project = new ProjectFacts
            {
                ContextId = context,
                File = args.ProjectFile ?? "",
                Name = ProjectName(args.ProjectFile),
                Started = args.Timestamp,
                ParentContextId = args.ParentProjectBuildEventContext?.ProjectContextId,
                TargetNames = args.TargetNames ?? "",
            };

            projects[context] = project;
            facts.Projects.Add(project);
        };

        source.ProjectFinished += (_, args) =>
        {
            var context = args.BuildEventContext?.ProjectContextId ?? -1;
            if (!projects.TryGetValue(context, out var project)) return;

            project.Duration = args.Timestamp - project.Started;
            project.Succeeded = args.Succeeded;
        };

        source.TargetStarted += (_, args) =>
        {
            var context = args.BuildEventContext;
            if (context is null) return;

            targets[(context.ProjectContextId, context.TargetId)] = new PendingTarget
            {
                Name = args.TargetName ?? "",
                Started = args.Timestamp,
            };
        };

        source.TargetFinished += (_, args) =>
        {
            var context = args.BuildEventContext;
            if (context is null || !targets.Remove((context.ProjectContextId, context.TargetId), out var pending)) return;

            facts.Targets.Add(new TargetFacts
            {
                ProjectContextId = context.ProjectContextId,
                Project = ProjectName(args.ProjectFile),
                Name = pending.Name,
                Duration = args.Timestamp - pending.Started,
                Succeeded = args.Succeeded,
            });
        };

        source.ErrorRaised += (_, args) =>
        {
            facts.ErrorCount++;
            facts.Errors.Add(new
            {
                project = ProjectName(args.ProjectFile, args.BuildEventContext, projects),
                file = args.File,
                line = args.LineNumber,
                column = args.ColumnNumber,
                code = args.Code,
                message = args.Message,
            });
        };

        source.WarningRaised += (_, args) =>
        {
            facts.WarningCount++;
            facts.Warnings.Add(new
            {
                project = ProjectName(args.ProjectFile, args.BuildEventContext, projects),
                file = args.File,
                line = args.LineNumber,
                column = args.ColumnNumber,
                code = args.Code,
                message = args.Message,
            });
        };

        source.Replay(path);

        if (facts.Start is null && facts.Projects.Count > 0) facts.Start = facts.Projects.Min(project => project.Started);
        if (facts.End is null && facts.Projects.Count > 0)
            facts.End = facts.Projects.Max(project => project.Started + project.Duration);

        return facts;
    }

    /// <summary>
    /// In the server, Program.cs has already registered MSBuild; in a test host nobody has, and the
    /// reader has to come from an installed SDK. The second registration throws and is ignored.
    /// </summary>
    private static readonly Lazy<bool> MsBuildRegistration = new(() =>
    {
        try { Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults(); }
        catch (Exception) { /* already registered, or no SDK: the caller sees the failure as a read error */ }
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void EnsureMsBuildLoaded() => _ = MsBuildRegistration.Value;

    // ── shapes ────────────────────────────────────────────────────────────────
    private sealed class Facts
    {
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public List<ProjectFacts> Projects { get; } = [];
        public List<TargetFacts> Targets { get; } = [];
        public List<object> Errors { get; } = [];
        public List<object> Warnings { get; } = [];
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }

        public TimeSpan Duration => End is not null && Start is not null ? End.Value - Start.Value : TimeSpan.Zero;
    }

    private sealed class ProjectFacts
    {
        public int ContextId { get; init; }
        public string File { get; init; } = "";
        public string Name { get; init; } = "";
        public string TargetNames { get; init; } = "";
        public DateTime Started { get; init; }
        public int? ParentContextId { get; init; }
        public bool Succeeded { get; set; }
        public TimeSpan Duration { get; set; }
    }

    private sealed class PendingTarget
    {
        public string Name { get; init; } = "";
        public DateTime Started { get; init; }
    }

    private sealed class TargetFacts
    {
        public int ProjectContextId { get; init; }
        public string Project { get; init; } = "";
        public string Name { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public bool Succeeded { get; init; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static string ProjectName(string? projectFile) =>
        string.IsNullOrWhiteSpace(projectFile) ? "(unknown project)" : Path.GetFileNameWithoutExtension(projectFile);

    private static string ProjectName(
        string? projectFile, BuildEventContext? context, Dictionary<int, ProjectFacts> projects)
    {
        if (!string.IsNullOrWhiteSpace(projectFile)) return ProjectName(projectFile);
        if (context is not null && projects.TryGetValue(context.ProjectContextId, out var project)) return project.Name;
        return "(unknown project)";
    }

    private static int Clamp(int seconds) => seconds < 1 ? 1 : seconds > 1800 ? 1800 : seconds;
}
