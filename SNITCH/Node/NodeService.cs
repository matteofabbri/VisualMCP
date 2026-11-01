using System.Text.Json;
using System.Text.RegularExpressions;
using Snitch.Execution;

namespace Snitch.Node;

/// <summary>
/// The JavaScript/TypeScript toolchain a .NET solution often carries next to it: the package
/// manager the repository actually uses (npm, pnpm or yarn, read from the lockfile or the
/// <c>packageManager</c> field), install/scripts, and the two diagnostics tools whose output is
/// worth interpreting — <c>tsc</c> and <c>eslint</c>. Nothing is downloaded implicitly: a project
/// tool that is not installed is reported with the command that installs it.
/// </summary>
public interface INodeService
{
    Task<NodeInfoResult> InfoAsync(string? directory);
    Task<NodeInstallResult> InstallAsync(string? directory, string[]? packages, bool dev, bool remove, bool frozenLockfile,
        int timeoutSeconds);
    Task<NodeScriptResult> RunScriptAsync(string script, string? directory, string[]? args, int timeoutSeconds);
    Task<NodeTypecheckResult> TypecheckAsync(string? directory, string? project, bool emit, int timeoutSeconds);
    Task<NodeLintResult> LintAsync(string? directory, string? pattern, bool fix, int timeoutSeconds);
    Task<NodeOutdatedResult> OutdatedAsync(string? directory, int timeoutSeconds);
    Task<NodeAuditResult> AuditAsync(string? directory, int timeoutSeconds);
}

/// <summary>One diagnostic line of <c>tsc --pretty false</c>.</summary>
public sealed record TscDiagnostic(string File, int Line, int Column, string Severity, string Code, string Message);

/// <summary>One message of <c>eslint --format json</c>.</summary>
public sealed record EslintFinding(string File, string? RuleId, string Severity, int Line, int Column, string Message);

/// <summary>One row of <c>npm|pnpm|yarn outdated --json</c>.</summary>
public sealed record OutdatedPackage(string Name, string? Current, string? Wanted, string? Latest, string? Type);

public sealed class NodeService : INodeService
{
    private static readonly string[] Managers = ["npm", "pnpm", "yarn"];
    private static readonly Regex TscLine = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>TS\d+):\s+(?<message>.+)$",
        RegexOptions.Compiled);

    // ── info ─────────────────────────────────────────────────────────────────
    public async Task<NodeInfoResult> InfoAsync(string? directory)
    {
        var project = FindProjectDirectory(directory);
        if (project is null)
            return new NodeInfoResult
            {
                Error = "No package.json was found in this directory or above it.",
                Directory = directory ?? Directory.GetCurrentDirectory(),
            };

        var package = await ReadPackageJsonAsync(project);
        var manager = DetectPackageManager(project, package?.PackageManager);
        var managerVersion = await ExternalTool.VersionAsync(manager, "--version", project);

        var bin = Path.Combine(project, "node_modules", ".bin");
        return new NodeInfoResult
        {
            ProjectDirectory = project,
            PackageJson = Path.Combine(project, "package.json"),
            Name = package?.Name,
            Version = package?.Version,
            PackageManager = manager,
            DeclaredPackageManager = package?.PackageManager,
            PackageManagerVersion = managerVersion,
            Lockfile = package?.Lockfile,
            Scripts = package?.Scripts,
            DependencyCount = package?.DependencyCount ?? 0,
            DevDependencyCount = package?.DevDependencyCount ?? 0,
            NodeVersion = await ExternalTool.VersionAsync("node", "--version", project),
            TypescriptInstalled = ExternalTool.Exists("tsc", project, [bin]),
            EslintInstalled = ExternalTool.Exists("eslint", project, [bin]),
            NodeModulesPresent = Directory.Exists(Path.Combine(project, "node_modules")),
            Hint = package?.Scripts.Count > 0
                ? "Run one with node_script; typecheck with node_typecheck; lint with node_lint."
                : "No scripts in package.json; node_install installs dependencies.",
        };
    }

    // ── install ──────────────────────────────────────────────────────────────
    public async Task<NodeInstallResult> InstallAsync(string? directory, string[]? packages, bool dev, bool remove,
        bool frozenLockfile, int timeoutSeconds)
    {
        var project = FindProjectDirectory(directory);
        if (project is null) return new NodeInstallResult { Error = "No package.json was found in this directory or above it." };

        foreach (var package in packages ?? [])
            if (!IsSafePackage(package))
                return new NodeInstallResult { Error = $"'{package}' is not a package name (or name@version)." };

        var manager = DetectPackageManager(project);
        var names = (packages ?? []).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).ToList();

        string args;
        if (remove)
        {
            if (names.Count == 0) return new NodeInstallResult { Error = "remove=true needs at least one package name." };
            args = manager == "yarn" ? $"remove {string.Join(' ', names)}" : $"uninstall {string.Join(' ', names)}";
        }
        else if (names.Count == 0)
        {
            args = manager switch
            {
                "npm" => frozenLockfile ? "ci" : "install",
                _ => "install" + (frozenLockfile ? " --frozen-lockfile" : string.Empty),
            };
        }
        else
        {
            var verb = manager == "npm" ? "install" : "add";
            args = $"{verb} {string.Join(' ', names)}";
            if (dev) args += manager == "npm" ? " --save-dev" : manager == "pnpm" ? " --save-dev" : " --dev";
        }

        var run = await RunManagerAsync(manager, args, project, Math.Clamp(timeoutSeconds, 10, 3600));
        return new NodeInstallResult
        {
            ProjectDirectory = project,
            PackageManager = manager,
            Action = remove ? "remove" : names.Count == 0 ? "install" : "add",
            Packages = names,
            Dev = dev,
            FrozenLockfile = frozenLockfile,
            Command = $"{manager} {args}",
            Succeeded = run.Succeeded,
            DurationMs = (long)run.Elapsed.TotalMilliseconds,
            Output = ProcessRunner.Truncate(run.Text, 12_000),
            Error = run.Succeeded ? null : FirstNonEmpty(run.Stderr, run.Stdout) ?? $"{manager} {args} failed (exit {run.ExitCode}).",
        };
    }

    // ── run a script ─────────────────────────────────────────────────────────
    public async Task<NodeScriptResult> RunScriptAsync(string script, string? directory, string[]? args, int timeoutSeconds)
    {
        if (!IsSafeScript(script))
            return new NodeScriptResult { Error = "A script name is required (letters, digits, '-', '_', ':', '.')." };

        var project = FindProjectDirectory(directory);
        if (project is null) return new NodeScriptResult { Error = "No package.json was found in this directory or above it." };

        var package = await ReadPackageJsonAsync(project);
        if (package is not null && !package.Scripts.ContainsKey(script))
            return new NodeScriptResult
            {
                Error = $"package.json has no script named '{script}'.",
                Scripts = package.Scripts.Keys.OrderBy(name => name).ToList(),
            };

        var manager = DetectPackageManager(project, package?.PackageManager);
        var forwarded = (args ?? []).Where(arg => !string.IsNullOrWhiteSpace(arg)).ToList();
        foreach (var argument in forwarded)
            if (argument.Contains('"') || argument.Contains('\n'))
                return new NodeScriptResult { Error = $"'{argument}' cannot be forwarded as an argument." };

        var command = $"run {script}";
        if (forwarded.Count > 0) command += " -- " + string.Join(' ', forwarded);

        var run = await RunManagerAsync(manager, command, project, Math.Clamp(timeoutSeconds, 10, 1800));
        return new NodeScriptResult
        {
            ProjectDirectory = project,
            PackageManager = manager,
            Script = script,
            Arguments = forwarded,
            Command = $"{manager} {command}",
            Succeeded = run.Succeeded,
            ExitCode = run.TimedOut ? (int?)null : run.ExitCode,
            TimedOut = run.TimedOut,
            DurationMs = (long)run.Elapsed.TotalMilliseconds,
            Stdout = ProcessRunner.Truncate(run.Stdout, 24_000),
            Stderr = ProcessRunner.Truncate(run.Stderr, 12_000),
            Error = run.TimedOut ? $"The script timed out after {timeoutSeconds}s."
                : run.Succeeded ? null
                : FirstNonEmpty(run.Stderr, run.Stdout) ?? $"The script exited with {run.ExitCode}.",
        };
    }

    // ── typecheck (tsc) ──────────────────────────────────────────────────────
    public async Task<NodeTypecheckResult> TypecheckAsync(string? directory, string? project, bool emit, int timeoutSeconds)
    {
        var projectDirectory = FindProjectDirectory(directory);
        if (projectDirectory is null) return new NodeTypecheckResult { Error = "No package.json was found in this directory or above it." };

        var bin = Path.Combine(projectDirectory, "node_modules", ".bin");
        var tsc = ExternalTool.Resolve("tsc", projectDirectory, [bin]);
        if (tsc is null)
            return new NodeTypecheckResult
            {
                Error = "TypeScript is not installed in this project (node_modules/.bin/tsc is missing).",
                SuggestedCommand = $"{DetectPackageManager(projectDirectory)} install -D typescript",
            };

        var args = "--pretty false";
        if (!emit) args += " --noEmit";
        if (!string.IsNullOrWhiteSpace(project)) args += $" -p {Quote(project!)}";

        var run = await ExternalTool.RunAsync(tsc, args, projectDirectory, Math.Clamp(timeoutSeconds, 10, 900));
        var diagnostics = ParseTscOutput(run.stdout + "\n" + run.stderr);

        return new NodeTypecheckResult
        {
            ProjectDirectory = projectDirectory,
            Project = project,
            Command = $"tsc {args}",
            Succeeded = run.exitCode == 0 && !run.timedOut,
            TimedOut = run.timedOut,
            DurationMs = (long)run.elapsed.TotalMilliseconds,
            ErrorCount = diagnostics.Count(diagnostic => diagnostic.Severity == "error"),
            WarningCount = diagnostics.Count(diagnostic => diagnostic.Severity == "warning"),
            Diagnostics = diagnostics.Take(500).ToList(),
            Truncated = diagnostics.Count > 500,
            RawOutput = diagnostics.Count == 0 && run.exitCode != 0
                ? ProcessRunner.Truncate(run.stdout + "\n" + run.stderr, 8_000)
                : null,
        };
    }

    // ── lint (eslint) ────────────────────────────────────────────────────────
    public async Task<NodeLintResult> LintAsync(string? directory, string? pattern, bool fix, int timeoutSeconds)
    {
        var projectDirectory = FindProjectDirectory(directory);
        if (projectDirectory is null) return new NodeLintResult { Error = "No package.json was found in this directory or above it." };

        var bin = Path.Combine(projectDirectory, "node_modules", ".bin");
        var eslint = ExternalTool.Resolve("eslint", projectDirectory, [bin]);
        if (eslint is null)
            return new NodeLintResult
            {
                Error = "ESLint is not installed in this project (node_modules/.bin/eslint is missing).",
                SuggestedCommand = $"{DetectPackageManager(projectDirectory)} install -D eslint",
            };

        var args = "--format json";
        if (fix) args += " --fix";
        args += " " + Quote(string.IsNullOrWhiteSpace(pattern) ? "." : pattern!);

        var run = await ExternalTool.RunAsync(eslint, args, projectDirectory, Math.Clamp(timeoutSeconds, 10, 900));
        var findings = ParseEslintJson(run.stdout);

        var byRule = findings
            .Where(finding => finding.RuleId is not null)
            .GroupBy(finding => finding.RuleId!)
            .OrderByDescending(group => group.Count())
            .Take(15)
            .ToDictionary(group => group.Key, group => group.Count());

        return new NodeLintResult
        {
            ProjectDirectory = projectDirectory,
            Pattern = pattern ?? ".",
            Fix = fix,
            Command = $"eslint {args}",
            Succeeded = run.exitCode == 0 && !run.timedOut,
            TimedOut = run.timedOut,
            ErrorCount = findings.Count(finding => finding.Severity == "error"),
            WarningCount = findings.Count(finding => finding.Severity == "warning"),
            FileCount = findings.Select(finding => finding.File).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TopRules = byRule,
            Findings = findings.Take(500).ToList(),
            Truncated = findings.Count > 500,
            RawOutput = findings.Count == 0 && run.exitCode != 0
                ? ProcessRunner.Truncate(run.stdout + "\n" + run.stderr, 8_000)
                : null,
        };
    }

    // ── outdated / audit ─────────────────────────────────────────────────────
    public async Task<NodeOutdatedResult> OutdatedAsync(string? directory, int timeoutSeconds)
    {
        var projectDirectory = FindProjectDirectory(directory);
        if (projectDirectory is null) return new NodeOutdatedResult { Error = "No package.json was found in this directory or above it." };

        var manager = DetectPackageManager(projectDirectory);
        var run = await RunManagerAsync(manager, "outdated --json", projectDirectory, Math.Clamp(timeoutSeconds, 10, 600));
        var packages = ParseOutdatedJson(run.Stdout, manager);

        return new NodeOutdatedResult
        {
            ProjectDirectory = projectDirectory,
            PackageManager = manager,
            Command = $"{manager} outdated --json",
            Count = packages.Count,
            UpToDate = packages.Count == 0,
            Packages = packages.Take(300).ToList(),
            Truncated = packages.Count > 300,
            Error = packages.Count == 0 && !run.Succeeded && run.Stdout.Trim().Length == 0
                ? FirstNonEmpty(run.Stderr, run.Stdout)
                : null,
        };
    }

    public async Task<NodeAuditResult> AuditAsync(string? directory, int timeoutSeconds)
    {
        var projectDirectory = FindProjectDirectory(directory);
        if (projectDirectory is null) return new NodeAuditResult { Error = "No package.json was found in this directory or above it." };

        var manager = DetectPackageManager(projectDirectory);
        var run = await RunManagerAsync(manager, "audit --json", projectDirectory, Math.Clamp(timeoutSeconds, 10, 600));
        var audit = ParseAuditJson(run.Stdout, manager);

        return new NodeAuditResult
        {
            ProjectDirectory = projectDirectory,
            PackageManager = manager,
            Command = $"{manager} audit --json",
            Counts = audit.Counts,
            Total = audit.Counts.Total,
            Vulnerabilities = audit.Packages.Take(200).ToList(),
            Truncated = audit.Packages.Count > 200,
            Error = audit.Packages.Count == 0 && !run.Succeeded && run.Stdout.Trim().Length == 0
                ? FirstNonEmpty(run.Stderr, run.Stdout)
                : null,
        };
    }

    // ── project discovery and detection ──────────────────────────────────────
    /// <summary>The nearest directory at or above <paramref name="start"/> that holds a package.json.</summary>
    public static string? FindProjectDirectory(string? start)
    {
        var directory = start;
        if (string.IsNullOrWhiteSpace(directory)) directory = Directory.GetCurrentDirectory();
        if (File.Exists(directory)) directory = Path.GetDirectoryName(Path.GetFullPath(directory));

        var current = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        for (var depth = 0; current is not null && depth < 40; depth++)
        {
            if (File.Exists(Path.Combine(current, "package.json"))) return current;
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        return null;
    }

    /// <summary>
    /// The package manager this project uses: the <c>packageManager</c> field wins (corepack makes
    /// it authoritative), then the lockfile, then npm. <paramref name="packageManagerField"/> lets a
    /// caller that already read package.json pass the field in instead of reading it twice.
    /// </summary>
    public static string DetectPackageManager(string projectDirectory, string? packageManagerField = null)
    {
        var field = packageManagerField;
        if (field is null)
        {
            try
            {
                var path = Path.Combine(projectDirectory, "package.json");
                if (File.Exists(path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.TryGetProperty("packageManager", out var value) &&
                        value.ValueKind == JsonValueKind.String)
                        field = value.GetString();
                }
            }
            catch (JsonException) { /* a malformed package.json is reported by info/install instead */ }
        }

        if (!string.IsNullOrWhiteSpace(field))
        {
            var name = field.Split('@', 2)[0].Trim().ToLowerInvariant();
            if (Managers.Contains(name)) return name;
        }

        if (File.Exists(Path.Combine(projectDirectory, "pnpm-lock.yaml"))) return "pnpm";
        if (File.Exists(Path.Combine(projectDirectory, "yarn.lock"))) return "yarn";
        if (File.Exists(Path.Combine(projectDirectory, "package-lock.json"))) return "npm";
        return "npm";
    }

    // ── parsers (public: they are the behaviour a test can pin) ──────────────
    /// <summary>Reads the <c>path(line,col): error TSxxxx: message</c> lines of <c>tsc --pretty false</c>.</summary>
    public static IReadOnlyList<TscDiagnostic> ParseTscOutput(string? output)
    {
        var diagnostics = new List<TscDiagnostic>();
        if (string.IsNullOrWhiteSpace(output)) return diagnostics;

        foreach (var raw in output.Split('\n'))
        {
            var match = TscLine.Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;

            diagnostics.Add(new TscDiagnostic(
                match.Groups["file"].Value.Trim(),
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["col"].Value),
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim()));
        }

        return diagnostics;
    }

    /// <summary>Reads the array <c>eslint --format json</c> prints, one entry per file.</summary>
    public static IReadOnlyList<EslintFinding> ParseEslintJson(string? json)
    {
        var findings = new List<EslintFinding>();
        if (string.IsNullOrWhiteSpace(json)) return findings;

        var start = json.IndexOf('[');
        var end = json.LastIndexOf(']');
        if (start < 0 || end <= start) return findings;

        try
        {
            using var document = JsonDocument.Parse(json[start..(end + 1)]);
            foreach (var file in document.RootElement.EnumerateArray())
            {
                var path = file.TryGetProperty("filePath", out var filePath) ? filePath.GetString() ?? string.Empty : string.Empty;
                if (!file.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var message in messages.EnumerateArray())
                {
                    var severity = message.TryGetProperty("severity", out var severityElement) && severityElement.TryGetInt32(out var value)
                        ? value == 2 ? "error" : "warning"
                        : "warning";
                    findings.Add(new EslintFinding(
                        path,
                        message.TryGetProperty("ruleId", out var rule) && rule.ValueKind == JsonValueKind.String ? rule.GetString() : null,
                        severity,
                        message.TryGetProperty("line", out var line) && line.TryGetInt32(out var lineNumber) ? lineNumber : 0,
                        message.TryGetProperty("column", out var column) && column.TryGetInt32(out var columnNumber) ? columnNumber : 0,
                        message.TryGetProperty("message", out var text) ? text.GetString() ?? string.Empty : string.Empty));
                }
            }
        }
        catch (JsonException) { /* eslint printed a warning instead of JSON; the caller reports raw output */ }

        return findings;
    }

    /// <summary>
    /// Normalises <c>outdated --json</c> across the three managers: npm and pnpm answer with an
    /// object keyed by package (or, for newer pnpm, an array), yarn 1 with one JSON object per line
    /// whose table rows carry the columns named in its head.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseOutdatedJson(string? json, string packageManager)
    {
        var packages = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(json)) return packages;

        if (packageManager == "yarn")
        {
            foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line.Trim());
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type) || type.GetString() != "table") continue;
                    if (!root.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Array) continue;

                    var head = data.TryGetProperty("head", out var headElement) && headElement.ValueKind == JsonValueKind.Array
                        ? headElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList()
                        : ["Package", "Current", "Wanted", "Latest", "Package Type", "URL"];

                    foreach (var row in body.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) continue;
                        var cells = row.EnumerateArray().Select(cell => cell.GetString()).ToList();
                        packages.Add(new OutdatedPackage(
                            Cell(cells, head, "Package") ?? "?",
                            Cell(cells, head, "Current"),
                            Cell(cells, head, "Wanted"),
                            Cell(cells, head, "Latest"),
                            Cell(cells, head, "Package Type") ?? Cell(cells, head, "Type")));
                    }
                }
                catch (JsonException) { /* a non-JSON line */ }
            }

            return packages;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    var value = property.Value;
                    if (value.ValueKind != JsonValueKind.Object) continue;
                    packages.Add(new OutdatedPackage(
                        property.Name,
                        StringOf(value, "current"),
                        StringOf(value, "wanted"),
                        StringOf(value, "latest"),
                        StringOf(value, "type") ?? StringOf(value, "dependent")));
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in root.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Object) continue;
                    var name = StringOf(value, "packageName") ?? StringOf(value, "name");
                    if (name is null) continue;
                    packages.Add(new OutdatedPackage(
                        name,
                        StringOf(value, "current"),
                        StringOf(value, "wanted"),
                        StringOf(value, "latest"),
                        StringOf(value, "type") ?? StringOf(value, "dependent")));
                }
            }
        }
        catch (JsonException) { /* not JSON */ }

        return packages;
    }

    /// <summary>Normalises <c>audit --json</c>: npm's metadata/vulnerabilities, pnpm's advisories, yarn's NDJSON.</summary>
    public static (VulnerabilityCounts Counts, IReadOnlyList<VulnerabilityRow> Packages) ParseAuditJson(
        string? json, string packageManager)
    {
        var packages = new List<VulnerabilityRow>();
        var info = 0; var low = 0; var moderate = 0; var high = 0; var critical = 0;

        if (string.IsNullOrWhiteSpace(json)) return (new VulnerabilityCounts(0, 0, 0, 0, 0, 0), packages);

        if (packageManager == "yarn")
        {
            foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(line.Trim());
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type) || type.GetString() != "auditAdvisory") continue;
                    if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("advisory", out var advisory))
                        continue;

                    var severity = StringOf(advisory, "severity") ?? "low";
                    switch (severity) { case "info": info++; break; case "low": low++; break; case "moderate": moderate++; break; case "high": high++; break; case "critical": critical++; break; }
                    packages.Add(new VulnerabilityRow(
                        StringOf(advisory, "module_name") ?? "?",
                        severity,
                        StringOf(advisory, "title") ?? string.Empty,
                        StringOf(advisory, "vulnerable_versions") ?? string.Empty,
                        StringOf(advisory, "url") ?? string.Empty));
                }
                catch (JsonException) { /* a non-JSON line */ }
            }

            return (new VulnerabilityCounts(info, low, moderate, high, critical, info + low + moderate + high + critical), packages);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("vulnerabilities", out var counts) && counts.ValueKind == JsonValueKind.Object)
            {
                info = IntOf(counts, "info");
                low = IntOf(counts, "low");
                moderate = IntOf(counts, "moderate");
                high = IntOf(counts, "high");
                critical = IntOf(counts, "critical");
            }

            if (root.TryGetProperty("vulnerabilities", out var vulnerabilities) &&
                vulnerabilities.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in vulnerabilities.EnumerateObject())
                {
                    var value = property.Value;
                    packages.Add(new VulnerabilityRow(
                        property.Name,
                        StringOf(value, "severity") ?? "?",
                        SeverityTitle(value),
                        StringOf(value, "range") ?? string.Empty,
                        FixOf(value)));
                }
            }
            else if (root.TryGetProperty("advisories", out var advisories) && advisories.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in advisories.EnumerateObject())
                {
                    var value = property.Value;
                    var severity = StringOf(value, "severity") ?? "low";
                    switch (severity) { case "info": info++; break; case "low": low++; break; case "moderate": moderate++; break; case "high": high++; break; case "critical": critical++; break; }
                    packages.Add(new VulnerabilityRow(
                        StringOf(value, "module_name") ?? property.Name,
                        severity,
                        StringOf(value, "title") ?? string.Empty,
                        StringOf(value, "vulnerable_versions") ?? string.Empty,
                        StringOf(value, "url") ?? string.Empty));
                }
            }
        }
        catch (JsonException) { /* not JSON */ }

        if (packages.Count > 0 && info + low + moderate + high + critical == 0)
            info = packages.Count;

        return (new VulnerabilityCounts(info, low, moderate, high, critical, info + low + moderate + high + critical), packages);
    }

    private static string SeverityTitle(JsonElement value)
    {
        if (!value.TryGetProperty("via", out var via)) return string.Empty;
        if (via.ValueKind == JsonValueKind.Array)
        {
            var first = via.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object) return StringOf(first, "title") ?? string.Empty;
            if (first.ValueKind == JsonValueKind.String) return first.GetString() ?? string.Empty;
        }

        return StringOf(value, "title") ?? string.Empty;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private sealed record PackageInfo(
        string? Name, string? Version, string? PackageManager, string? Lockfile,
        Dictionary<string, string> Scripts, int DependencyCount, int DevDependencyCount);

    private static async Task<PackageInfo?> ReadPackageJsonAsync(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "package.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = document.RootElement;

            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("scripts", out var scriptElement) && scriptElement.ValueKind == JsonValueKind.Object)
                foreach (var script in scriptElement.EnumerateObject())
                    if (script.Value.ValueKind == JsonValueKind.String)
                        scripts[script.Name] = script.Value.GetString() ?? string.Empty;

            var dependencyCount = CountProperties(root, "dependencies");
            var devDependencyCount = CountProperties(root, "devDependencies");

            var lockfile = File.Exists(Path.Combine(projectDirectory, "pnpm-lock.yaml")) ? "pnpm-lock.yaml"
                : File.Exists(Path.Combine(projectDirectory, "yarn.lock")) ? "yarn.lock"
                : File.Exists(Path.Combine(projectDirectory, "package-lock.json")) ? "package-lock.json"
                : null;

            return new PackageInfo(
                StringOf(root, "name"),
                StringOf(root, "version"),
                StringOf(root, "packageManager"),
                lockfile,
                scripts,
                dependencyCount,
                devDependencyCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int CountProperties(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Count()
            : 0;

    private async Task<ToolRun> RunManagerAsync(string manager, string args, string projectDirectory, int timeoutSeconds)
    {
        var (exitCode, timedOut, stdout, stderr, elapsed) =
            await ExternalTool.RunAsync(manager, args, projectDirectory, timeoutSeconds);
        return new ToolRun(exitCode, timedOut, stdout, stderr, elapsed);
    }

    private static string? StringOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>npm reports <c>fixAvailable</c> as a boolean or as the fixed version; both are readable.</summary>
    private static string FixOf(JsonElement value)
    {
        if (!value.TryGetProperty("fixAvailable", out var fix)) return "unknown";
        return fix.ValueKind switch
        {
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.Object when fix.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                => version.GetString() ?? "yes",
            _ => "unknown",
        };
    }

    private static int IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? Cell(IReadOnlyList<string?> cells, IReadOnlyList<string> head, string column)
    {
        for (var index = 0; index < head.Count && index < cells.Count; index++)
            if (string.Equals(head[index], column, StringComparison.OrdinalIgnoreCase))
                return cells[index];
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static bool IsSafePackage(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.TrimStart().StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '@' or '/' or '.' or '_' or '-' or '^' or '~' or '<' or '>' or '=' or '*' or '+');

    private static bool IsSafeScript(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.TrimStart().StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.');

    private sealed record ToolRun(int ExitCode, bool TimedOut, string Stdout, string Stderr, TimeSpan Elapsed)
    {
        public bool Succeeded => !TimedOut && ExitCode == 0;
        public string Text => (Stdout + "\n" + Stderr).Trim();
    }
}

/// <summary>One vulnerable package, as an audit reports it.</summary>
public sealed record VulnerabilityRow(string Name, string Severity, string Title, string Range, string Fix);

/// <summary>Vulnerability counts by severity, as npm's audit metadata reports them.</summary>
public sealed record VulnerabilityCounts(int Info, int Low, int Moderate, int High, int Critical, int Total);
