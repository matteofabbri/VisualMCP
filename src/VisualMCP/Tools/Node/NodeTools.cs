using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Node;

namespace VisualMCP.Tools.Node;

[McpServerToolType]
public static class NodeTools
{
    [McpServerTool(Name = "node_info"), Description(
        "What the JavaScript/TypeScript project next to the solution is: the nearest package.json, its name and " +
        "version, the package manager the repository actually uses (packageManager field, then pnpm-lock.yaml / " +
        "yarn.lock / package-lock.json, then npm), the node and package-manager versions, the scripts, the dependency " +
        "counts and whether typescript/eslint are installed locally. Use it FIRST, then node_script/node_typecheck/" +
        "node_lint. Use this INSTEAD OF shell 'npm --version' / reading package.json by hand.")]
    public static Task<NodeInfoResult> NodeInfo(
        INodeService node,
        [Description("Directory to start from (default: the working directory); the nearest package.json above it is used.")]
        string? directory = null)
        => node.InfoAsync(directory);

    [McpServerTool(Name = "node_install"), Description(
        "Install JavaScript dependencies with the project's own package manager: no packages installs the lockfile " +
        "(npm 'ci' when frozenLockfile, pnpm/yarn 'install --frozen-lockfile'), named packages add them (dev=true " +
        "for --save-dev), remove=true uninstalls them. The command, exit code and duration come back as structured " +
        "data. Use this INSTEAD OF shell 'npm install' / 'pnpm add' / 'yarn add'.")]
    public static Task<NodeInstallResult> NodeInstall(
        INodeService node,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("Packages to add or remove, e.g. ['typescript', 'vite@5']; omit to install the lockfile.")]
        string[]? packages = null,
        [Description("Add as a dev dependency (--save-dev / --dev).")] bool dev = false,
        [Description("Uninstall the named packages instead of installing them.")] bool remove = false,
        [Description("Refuse to update the lockfile (npm ci / --frozen-lockfile): the CI install.")]
        bool frozenLockfile = false,
        [Description("Timeout in seconds (default 300; installs can take a while).")] int timeoutSeconds = 300)
        => node.InstallAsync(directory, packages, dev, remove, frozenLockfile, timeoutSeconds);

    [McpServerTool(Name = "node_script"), Description(
        "Run one package.json script (build, test, lint, dev, …) with the project's package manager, forwarding " +
        "arguments after '--', and return exit code, duration, stdout and stderr. It refuses a script package.json " +
        "does not declare and lists the ones it does. Use this INSTEAD OF shell 'npm run build' / 'pnpm build'.")]
    public static Task<NodeScriptResult> NodeScript(
        INodeService node,
        [Description("Script name from package.json, e.g. build, test, typecheck.")] string script,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("Arguments forwarded to the script (after '--').")] string[]? args = null,
        [Description("Timeout in seconds (default 600).")] int timeoutSeconds = 600)
        => node.RunScriptAsync(script, directory, args, timeoutSeconds);

    [McpServerTool(Name = "node_typecheck"), Description(
        "Type-check the project with its OWN TypeScript compiler (node_modules/.bin/tsc, never a download): " +
        "'tsc --noEmit --pretty false' by default, parsed into file:line:column + TSxxxx diagnostics with counts. " +
        "This is the JS/TS equivalent of get_diagnostics. Reports that typescript is not installed, with the command " +
        "that installs it, instead of guessing.")]
    public static Task<NodeTypecheckResult> NodeTypecheck(
        INodeService node,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("tsconfig file or directory to pass to -p (default: tsc finds tsconfig.json).")] string? project = null,
        [Description("Emit output instead of --noEmit (default: false — check only).")] bool emit = false,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => node.TypecheckAsync(directory, project, emit, timeoutSeconds);

    [McpServerTool(Name = "node_lint"), Description(
        "Lint the project with its OWN ESLint (node_modules/.bin/eslint, never a download): '--format json' is " +
        "parsed into findings with file, rule id, severity and position, plus error/warning counts and the rules " +
        "that fire most. fix=true runs --fix first. Reports that eslint is not installed instead of failing. " +
        "Use this INSTEAD OF shell 'npx eslint'.")]
    public static Task<NodeLintResult> NodeLint(
        INodeService node,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("File, directory or glob to lint (default: the whole project, '.').")] string? pattern = null,
        [Description("Apply ESLint's automatic fixes (--fix).")] bool fix = false,
        [Description("Timeout in seconds (default 300).")] int timeoutSeconds = 300)
        => node.LintAsync(directory, pattern, fix, timeoutSeconds);

    [McpServerTool(Name = "node_outdated"), Description(
        "Packages that have a newer version, from the package manager's own 'outdated --json' (npm, pnpm and yarn 1 " +
        "shapes are normalised to name/current/wanted/latest/type). Use this INSTEAD OF shell 'npm outdated'. " +
        "Read-only.")]
    public static Task<NodeOutdatedResult> NodeOutdated(
        INodeService node,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("Timeout in seconds (default 180).")] int timeoutSeconds = 180)
        => node.OutdatedAsync(directory, timeoutSeconds);

    [McpServerTool(Name = "node_audit"), Description(
        "Known vulnerabilities in the JavaScript dependencies, from the package manager's own 'audit --json' (npm " +
        "metadata/vulnerabilities, pnpm advisories, yarn NDJSON are normalised): severity counts, and per package the " +
        "severity, title, vulnerable range and whether a fix exists. Use this INSTEAD OF shell 'npm audit'. Read-only.")]
    public static Task<NodeAuditResult> NodeAudit(
        INodeService node,
        [Description("Directory to start from (default: the working directory).")] string? directory = null,
        [Description("Timeout in seconds (default 180).")] int timeoutSeconds = 180)
        => node.AuditAsync(directory, timeoutSeconds);
}
