namespace Snitch.Node;

/// <summary>What <see cref="INodeService.InfoAsync"/> found about the project next to the solution.</summary>
/// <remarks>
/// Every member is nullable so a refusal leaves the success members unset; the MCP serializer drops a
/// null member, so the answer keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record NodeInfoResult
{
    /// <summary>The nearest directory holding a package.json.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The absolute path of that package.json.</summary>
    public string? PackageJson { get; init; }

    /// <summary>The package name.</summary>
    public string? Name { get; init; }

    /// <summary>The package version.</summary>
    public string? Version { get; init; }

    /// <summary>The package manager the repository actually uses.</summary>
    public string? PackageManager { get; init; }

    /// <summary>The <c>packageManager</c> field package.json declares.</summary>
    public string? DeclaredPackageManager { get; init; }

    /// <summary>The package manager's version.</summary>
    public string? PackageManagerVersion { get; init; }

    /// <summary>The lockfile the repository carries.</summary>
    public string? Lockfile { get; init; }

    /// <summary>The scripts package.json declares.</summary>
    public IReadOnlyDictionary<string, string>? Scripts { get; init; }

    /// <summary>How many dependencies the package declares.</summary>
    public int? DependencyCount { get; init; }

    /// <summary>How many dev dependencies the package declares.</summary>
    public int? DevDependencyCount { get; init; }

    /// <summary>The Node version on PATH.</summary>
    public string? NodeVersion { get; init; }

    /// <summary>Whether a local TypeScript is installed.</summary>
    public bool? TypescriptInstalled { get; init; }

    /// <summary>Whether a local ESLint is installed.</summary>
    public bool? EslintInstalled { get; init; }

    /// <summary>Whether node_modules is present.</summary>
    public bool? NodeModulesPresent { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Hint { get; init; }

    /// <summary>The directory that was searched, when nothing was found.</summary>
    public string? Directory { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.InstallAsync"/> did.</summary>
public sealed record NodeInstallResult
{
    /// <summary>The project the package manager ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The package manager that ran.</summary>
    public string? PackageManager { get; init; }

    /// <summary><c>install</c>, <c>add</c> or <c>remove</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The packages that were installed or removed.</summary>
    public IReadOnlyList<string>? Packages { get; init; }

    /// <summary>Whether the packages were added as dev dependencies.</summary>
    public bool? Dev { get; init; }

    /// <summary>Whether the lockfile was frozen.</summary>
    public bool? FrozenLockfile { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>Whether the package manager succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>How long the command took, in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>What the package manager printed.</summary>
    public string? Output { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.RunScriptAsync"/> ran and what it printed.</summary>
public sealed record NodeScriptResult
{
    /// <summary>The project the script ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The package manager that ran it.</summary>
    public string? PackageManager { get; init; }

    /// <summary>The script that ran.</summary>
    public string? Script { get; init; }

    /// <summary>The arguments forwarded to the script.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>Whether the script succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The exit code, or <c>null</c> when the run timed out.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether the script ran out of time.</summary>
    public bool? TimedOut { get; init; }

    /// <summary>How long the script took, in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>What the script printed on stdout.</summary>
    public string? Stdout { get; init; }

    /// <summary>What it printed on stderr.</summary>
    public string? Stderr { get; init; }

    /// <summary>The scripts package.json declares, when the name was not one of them.</summary>
    public IReadOnlyList<string>? Scripts { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.TypecheckAsync"/> found.</summary>
public sealed record NodeTypecheckResult
{
    /// <summary>The project TypeScript ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The tsconfig that was asked for.</summary>
    public string? Project { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>Whether tsc accepted the project.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether tsc ran out of time.</summary>
    public bool? TimedOut { get; init; }

    /// <summary>How long tsc took, in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>How many diagnostics are errors.</summary>
    public int? ErrorCount { get; init; }

    /// <summary>How many diagnostics are warnings.</summary>
    public int? WarningCount { get; init; }

    /// <summary>The diagnostics, capped at 500.</summary>
    public IReadOnlyList<TscDiagnostic>? Diagnostics { get; init; }

    /// <summary>Whether more diagnostics were produced than were returned.</summary>
    public bool? Truncated { get; init; }

    /// <summary>tsc's own text, when it failed without a parseable diagnostic.</summary>
    public string? RawOutput { get; init; }

    /// <summary>The command that would install TypeScript, when it is missing.</summary>
    public string? SuggestedCommand { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.LintAsync"/> found.</summary>
public sealed record NodeLintResult
{
    /// <summary>The project ESLint ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The path pattern that was linted.</summary>
    public string? Pattern { get; init; }

    /// <summary>Whether ESLint was asked to fix what it found.</summary>
    public bool? Fix { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>Whether ESLint exited cleanly.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether ESLint ran out of time.</summary>
    public bool? TimedOut { get; init; }

    /// <summary>How many findings are errors.</summary>
    public int? ErrorCount { get; init; }

    /// <summary>How many findings are warnings.</summary>
    public int? WarningCount { get; init; }

    /// <summary>How many files carry a finding.</summary>
    public int? FileCount { get; init; }

    /// <summary>The rules that fire most, with their counts.</summary>
    public IReadOnlyDictionary<string, int>? TopRules { get; init; }

    /// <summary>The findings, capped at 500.</summary>
    public IReadOnlyList<EslintFinding>? Findings { get; init; }

    /// <summary>Whether more findings were produced than were returned.</summary>
    public bool? Truncated { get; init; }

    /// <summary>ESLint's own text, when it failed without a parseable finding.</summary>
    public string? RawOutput { get; init; }

    /// <summary>The command that would install ESLint, when it is missing.</summary>
    public string? SuggestedCommand { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.OutdatedAsync"/> found.</summary>
public sealed record NodeOutdatedResult
{
    /// <summary>The project the package manager ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The package manager that ran.</summary>
    public string? PackageManager { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>How many packages have a newer version.</summary>
    public int? Count { get; init; }

    /// <summary>Whether every package is current.</summary>
    public bool? UpToDate { get; init; }

    /// <summary>The outdated packages, capped at 300.</summary>
    public IReadOnlyList<OutdatedPackage>? Packages { get; init; }

    /// <summary>Whether more packages were reported than were returned.</summary>
    public bool? Truncated { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What <see cref="INodeService.AuditAsync"/> found.</summary>
public sealed record NodeAuditResult
{
    /// <summary>The project the package manager ran in.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The package manager that ran.</summary>
    public string? PackageManager { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>The vulnerabilities counted by severity.</summary>
    public VulnerabilityCounts? Counts { get; init; }

    /// <summary>How many vulnerabilities were reported in total.</summary>
    public int? Total { get; init; }

    /// <summary>The vulnerable packages, capped at 200.</summary>
    public IReadOnlyList<VulnerabilityRow>? Vulnerabilities { get; init; }

    /// <summary>Whether more packages were reported than were returned.</summary>
    public bool? Truncated { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}
