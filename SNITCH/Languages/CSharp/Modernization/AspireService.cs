using Microsoft.CodeAnalysis;
using Snitch.Execution;
using Snitch.Platform;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Modernization;

/// <summary>
/// Installs Aspire on demand and scaffolds it into the solution. "On demand" is the contract: the
/// templates are installed only when they are missing, the AppHost is created only when the solution
/// has none, and the wiring is verified by compiling the AppHost — because a half-wired orchestrator
/// that does not build is worse than none.
/// </summary>
public interface IAspireService
{
    Task<object> StatusAsync(int timeoutSeconds, CancellationToken cancellationToken = default);

    Task<object> InstallAsync(bool includeWorkload, bool force, int timeoutSeconds, CancellationToken cancellationToken = default);

    Task<object> AddAsync(
        string? appHostName, string[]? projects, bool serviceDefaults, bool installIfMissing, bool verify,
        int timeoutSeconds, CancellationToken cancellationToken = default);
}

public sealed class AspireService : IAspireService
{
    private readonly IRoslynWorkspace _workspace;

    public AspireService(IRoslynWorkspace workspace) => _workspace = workspace;

    // ── status ────────────────────────────────────────────────────────────────
    public async Task<object> StatusAsync(int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken);
        var version = await DotnetAsync("--version", context.Directory, timeoutSeconds);
        if (!version.Succeeded)
            return new
            {
                error = "The .NET SDK was not found ('dotnet --version' failed).",
                output = ProcessRunner.Truncate(version.Output, 2000),
            };

        var sdkVersion = version.Output.Trim();
        var workload = await DotnetAsync("workload list", context.Directory, timeoutSeconds);
        var templates = await DotnetAsync("new list aspire", context.Directory, timeoutSeconds);

        var projects = new List<object>();
        var appHosts = new List<string>();
        var serviceDefaults = new List<string>();
        var consumers = new List<string>();

        if (context.Solution is not null)
            foreach (var project in context.Solution.Projects)
            {
                var file = project.FilePath;
                var text = file is not null && File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : "";
                var isAppHost = AspireProbe.IsAppHostProject(text);
                var isServiceDefaults = AspireProbe.IsServiceDefaultsProject(text);
                var isConsumer = AspireProbe.IsAspireConsumer(text);
                if (isAppHost) appHosts.Add(project.Name);
                if (isServiceDefaults) serviceDefaults.Add(project.Name);
                if (isConsumer) consumers.Add(project.Name);

                projects.Add(new
                {
                    name = project.Name,
                    path = file,
                    isAppHost,
                    isServiceDefaults,
                    isAspireConsumer = isConsumer,
                });
            }

        var templatesInstalled = templates.Succeeded && AspireProbe.TemplatesInstalled(templates.Output);
        return new
        {
            action = "status",
            solution = context.SolutionPath,
            sdk = new
            {
                version = sdkVersion,
                target = ProjectFiles.SdkTargetFromVersion(sdkVersion),
            },
            templatesInstalled,
            workloadInstalled = workload.Succeeded && AspireProbe.WorkloadInstalled(workload.Output),
            appHosts,
            serviceDefaults,
            aspireProjects = consumers,
            projects,
            next = templatesInstalled
                ? (appHosts.Count > 0
                    ? "The solution already has an AppHost; add Aspire resources in its Program.cs."
                    : "Call aspire(action='add') to create the AppHost and ServiceDefaults projects.")
                : "Call aspire(action='install') to install Aspire.ProjectTemplates.",
            probe = new
            {
                templates = templates.ToReport(),
                workload = workload.ToReport(),
            },
        };
    }

    // ── install ───────────────────────────────────────────────────────────────
    public async Task<object> InstallAsync(bool includeWorkload, bool force, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken);
        var version = await DotnetAsync("--version", context.Directory, timeoutSeconds);
        if (!version.Succeeded)
            return new { error = "The .NET SDK was not found ('dotnet --version' failed)." };

        var sdkVersion = version.Output.Trim();
        var steps = new List<object>();

        var probe = await DotnetAsync("new list aspire", context.Directory, timeoutSeconds);
        var templatesInstalled = probe.Succeeded && AspireProbe.TemplatesInstalled(probe.Output);

        if (templatesInstalled && !force)
        {
            return new
            {
                action = "install",
                alreadyInstalled = true,
                templatesInstalled = true,
                sdkVersion,
                note = "Aspire.ProjectTemplates is already installed; pass force=true to reinstall it.",
            };
        }

        var install = await DotnetAsync("new install Aspire.ProjectTemplates", context.Directory, timeoutSeconds);
        steps.Add(install.ToReport());

        // The workload is a .NET 8-era requirement; from .NET 9 the AppHost SDK arrives through NuGet.
        // Install it automatically only there, or when the caller asks for it.
        var sdkMajor = int.TryParse(sdkVersion.Split('.')[0], out var major) ? major : 0;
        var wantsWorkload = includeWorkload || sdkMajor == 8;
        DotnetResult? workload = null;
        if (wantsWorkload)
        {
            workload = await DotnetAsync("workload install aspire", context.Directory, timeoutSeconds);
            steps.Add(workload.ToReport());
        }

        var after = await DotnetAsync("new list aspire", context.Directory, timeoutSeconds);
        var installed = after.Succeeded && AspireProbe.TemplatesInstalled(after.Output);

        if (!installed)
            return new
            {
                error = "Aspire.ProjectTemplates is still not listed after the install.",
                steps,
                hint = "Run 'dotnet new install Aspire.ProjectTemplates' from a shell to see the full error (NuGet feed, proxy or elevation).",
            };

        var workloadList = await DotnetAsync("workload list", context.Directory, timeoutSeconds);
        return new
        {
            action = "install",
            templatesInstalled = true,
            workloadInstalled = workloadList.Succeeded && AspireProbe.WorkloadInstalled(workloadList.Output),
            workloadAttempted = wantsWorkload,
            sdkVersion,
            steps,
            note = "Templates installed. Call aspire(action='add') to scaffold the AppHost, or 'dotnet new aspire-starter' for a full sample.",
        };
    }

    // ── add (scaffold into the solution) ──────────────────────────────────────
    public async Task<object> AddAsync(
        string? appHostName, string[]? projects, bool serviceDefaults, bool installIfMissing, bool verify,
        int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        var context = await ContextAsync(cancellationToken);
        if (context.Solution is null || context.SolutionPath is null)
            return new
            {
                error = "No solution loaded. Call load_solution with a path, or open a folder that contains one.",
            };

        var solution = context.Solution;
        var solutionPath = context.SolutionPath;
        var solutionDir = context.Directory;
        var baseName = Path.GetFileNameWithoutExtension(solutionPath);

        // Validate an explicit selection before anything is created: a typo must not leave a half
        // scaffolded solution behind.
        if (projects is { Length: > 0 })
        {
            var unknown = projects
                .Where(specification => !solution.Projects.Any(project => Matches(project, specification)))
                .ToList();

            if (unknown.Count > 0)
                return new
                {
                    error = $"Unknown project(s): {string.Join(", ", unknown)}.",
                    knownProjects = solution.Projects.Select(project => project.Name),
                };
        }

        // Idempotence: a solution that already orchestrates something is not scaffolded twice.
        foreach (var project in solution.Projects.Where(p => p.FilePath is not null && File.Exists(p.FilePath)))
        {
            var text = await File.ReadAllTextAsync(project.FilePath!, cancellationToken);
            if (AspireProbe.IsAppHostProject(text))
                return new
                {
                    alreadyPresent = true,
                    appHost = project.Name,
                    path = project.FilePath,
                    note = "The solution already has an Aspire AppHost; nothing was scaffolded. Add resources in its Program.cs.",
                };
        }

        var steps = new List<object>();
        var templates = await DotnetAsync("new list aspire", solutionDir, timeoutSeconds);
        if (!(templates.Succeeded && AspireProbe.TemplatesInstalled(templates.Output)))
        {
            if (!installIfMissing)
                return new
                {
                    error = "The Aspire project templates are not installed.",
                    templatesInstalled = false,
                    hint = "Run aspire(action='install'), or call again with installIfMissing=true.",
                };

            var install = await DotnetAsync("new install Aspire.ProjectTemplates", solutionDir, timeoutSeconds);
            steps.Add(new { step = "install templates", result = install.ToReport() });
            if (!install.Succeeded)
                return new { error = "Installing Aspire.ProjectTemplates failed.", steps };
        }

        var appHost = string.IsNullOrWhiteSpace(appHostName) ? baseName + ".AppHost" : appHostName!.Trim();
        if (appHost.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new { error = $"'{appHost}' is not a usable project name." };

        var serviceDefaultsName = baseName + ".ServiceDefaults";
        if (solution.Projects.Any(p => string.Equals(p.Name, appHost, StringComparison.OrdinalIgnoreCase)))
            return new { error = $"A project named '{appHost}' already exists; pass appHostName with another name." };

        var appHostDir = Path.Combine(solutionDir, appHost);
        var appHostProject = Path.Combine(appHostDir, appHost + ".csproj");

        var serviceDefaultsDir = Path.Combine(solutionDir, serviceDefaultsName);
        var serviceDefaultsProject = Path.Combine(serviceDefaultsDir, serviceDefaultsName + ".csproj");
        var createServiceDefaults = serviceDefaults && !File.Exists(serviceDefaultsProject);

        if (createServiceDefaults)
        {
            var created = await DotnetAsync(
                $"new aspire-servicedefaults -n \"{serviceDefaultsName}\" -o \"{serviceDefaultsDir}\"",
                solutionDir, timeoutSeconds);
            steps.Add(new { step = "create ServiceDefaults", result = created.ToReport() });
            if (!created.Succeeded)
                return new { error = "dotnet new aspire-servicedefaults failed.", steps };
        }

        if (!File.Exists(appHostProject))
        {
            var created = await DotnetAsync(
                $"new aspire-apphost -n \"{appHost}\" -o \"{appHostDir}\"",
                solutionDir, timeoutSeconds);
            steps.Add(new { step = "create AppHost", result = created.ToReport() });
            if (!created.Succeeded)
                return new { error = "dotnet new aspire-apphost failed.", steps };
        }

        var slnAdd = $"sln \"{solutionPath}\" add \"{appHostProject}\""
                   + (File.Exists(serviceDefaultsProject) ? $" \"{serviceDefaultsProject}\"" : "");
        var added = await DotnetAsync(slnAdd, solutionDir, timeoutSeconds);
        steps.Add(new { step = "add to solution", result = added.ToReport() });
        if (!added.Succeeded)
            return new { error = "Adding the new projects to the solution failed.", steps };

        // Which projects become resources? Explicit names if given, otherwise every runnable project
        // (tests and libraries are not resources an AppHost can start).
        var candidates = new List<(string Name, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null || !File.Exists(project.FilePath)) continue;
            if (string.Equals(project.Name, appHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(project.Name, serviceDefaultsName, StringComparison.OrdinalIgnoreCase)) continue;

            var text = await File.ReadAllTextAsync(project.FilePath, cancellationToken);
            if (ProjectFiles.IsTestProject(text)) continue;

            var explicitMatch = projects is { Length: > 0 } && projects.Any(spec => Matches(project, spec));
            if (projects is { Length: > 0 })
            {
                if (!explicitMatch) continue;
            }
            else if (!ProjectFiles.IsRunnableProject(text))
            {
                continue;
            }

            if (seen.Add(project.Name)) candidates.Add((project.Name, project.FilePath));
        }

        var wired = new List<object>();
        var wiredProjects = new List<(string Name, string Path)>();
        var programOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var programChanges = new List<object>();

        foreach (var (name, path) in candidates)
        {
            var reference = await DotnetAsync($"add \"{appHostProject}\" reference \"{path}\"", appHostDir, timeoutSeconds);
            steps.Add(new { step = $"reference {name}", result = reference.ToReport() });
            if (!reference.Succeeded) continue;

            if (File.Exists(serviceDefaultsProject))
            {
                var sdReference = await DotnetAsync($"add \"{path}\" reference \"{serviceDefaultsProject}\"",
                    Path.GetDirectoryName(path)!, timeoutSeconds);
                steps.Add(new { step = $"ServiceDefaults reference {name}", result = sdReference.ToReport() });
            }

            wiredProjects.Add((name, path));
            wired.Add(new { project = name, path, resource = ProjectFiles.ResourceName(name) });
        }

        // AppHost Program.cs: one AddProject<T>("name") line per resource, before builder.Build().
        var appHostProgram = Path.Combine(appHostDir, "Program.cs");
        if (File.Exists(appHostProgram))
        {
            var text = await File.ReadAllTextAsync(appHostProgram, cancellationToken);
            var resources = wiredProjects.Select(c => (ProjectFiles.ProjectsMember(c.Name), ProjectFiles.ResourceName(c.Name)));
            var (updated, addedLines) = AspireWiring.AddProjects(text, resources);
            if (addedLines.Count > 0)
            {
                programOriginals[appHostProgram] = text;
                FileText.Write(appHostProgram, updated);
                programChanges.Add(new { file = appHostProgram, added = addedLines });
            }
        }

        // ServiceDefaults in each wired project: AddServiceDefaults() + MapDefaultEndpoints().
        if (File.Exists(serviceDefaultsProject))
            foreach (var (name, path) in wiredProjects)
            {
                var program = Path.Combine(Path.GetDirectoryName(path)!, "Program.cs");
                if (!File.Exists(program)) continue;

                var text = await File.ReadAllTextAsync(program, cancellationToken);
                var (updated, changed) = AspireWiring.AddServiceDefaults(text);
                if (!changed) continue;

                // Two packages can share a Program.cs only when the candidate list has duplicate names,
                // which the seen-set already prevents; keep the first original either way.
                programOriginals.TryAdd(program, text);
                FileText.Write(program, updated);
                programChanges.Add(new { file = program, added = new[] { "AddServiceDefaults()", "MapDefaultEndpoints()" } });
            }

        object? buildResult = null;
        if (verify && File.Exists(appHostProject))
        {
            var build = await DotnetAsync($"build \"{appHostProject}\" --verbosity minimal", solutionDir, timeoutSeconds);
            buildResult = new { build.Succeeded, build.ExitCode, output = ProcessRunner.Truncate(build.Output, 4000) };

            if (!build.Succeeded)
            {
                // The workspace wiring is the part this tool invented; revert exactly that and leave
                // the created projects and references in place, which are valid on their own.
                foreach (var (file, original) in programOriginals) FileText.Write(file, original);
                await ReloadAsync();

                return new
                {
                    error = "The AppHost does not compile after the wiring; the Program.cs edits were reverted.",
                    reverted = programOriginals.Keys,
                    build = buildResult,
                    steps,
                    note = "The AppHost and ServiceDefaults projects and their references were kept. Fix the project and re-run, or wire Program.cs by hand.",
                };
            }
        }

        await ReloadAsync();
        return new
        {
            succeeded = true,
            solution = solutionPath,
            appHost = new { name = appHost, path = appHostProject, addedToSolution = true },
            serviceDefaults = File.Exists(serviceDefaultsProject)
                ? (object)new { name = serviceDefaultsName, path = serviceDefaultsProject, created = createServiceDefaults }
                : null,
            projects = wired,
            programChanges,
            build = buildResult,
            steps,
            note = "Run build_project (or run_tests) to check the whole solution. Start the AppHost with run_project(projectName='" +
                   appHost + "') or 'dotnet run --project " + appHost + "'.",
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private sealed record DotnetResult(string Command, int ExitCode, bool TimedOut, string Output)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;

        public object ToReport() => new
        {
            command = Command,
            exitCode = ExitCode,
            timedOut = TimedOut,
            succeeded = Succeeded,
            output = ProcessRunner.Truncate(Output, 3000),
        };
    }

    private static async Task<DotnetResult> DotnetAsync(string args, string workingDirectory, int timeoutSeconds)
    {
        var (exitCode, timedOut, stdout, stderr, _) =
            await ProcessRunner.RunAsync("dotnet", args, workingDirectory, timeoutSeconds);
        return new DotnetResult($"dotnet {args}", exitCode, timedOut, (stdout + "\n" + stderr).Trim());
    }

    private static bool Matches(Project project, string specification)
    {
        if (string.Equals(project.Name, specification, StringComparison.OrdinalIgnoreCase)) return true;
        if (project.FilePath is null) return false;

        try
        {
            return string.Equals(Path.GetFullPath(project.FilePath), Path.GetFullPath(specification),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<(Solution? Solution, string Directory, string? SolutionPath)> ContextAsync(CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var path = _workspace.LoadedSolutionPath ?? solution?.FilePath;
        var directory = path is not null ? Path.GetDirectoryName(path)! : Directory.GetCurrentDirectory();
        return (solution, directory, path);
    }

    private async Task ReloadAsync()
    {
        if (_workspace.LoadedSolutionPath is not { } solutionPath) return;
        try { await _workspace.LoadSolutionAsync(solutionPath); } catch { /* the files are on disk; the next tool reloads on demand */ }
    }
}
