using System.Diagnostics;
using System.Text.Json;

namespace Snitch.Platform;

/// <summary>
/// Where this process — and every process it starts — may write temporary and cache data.
///
/// The .NET toolchain is the reason this exists. <c>dotnet build</c> and <c>dotnet test</c> put MSBuild
/// temp, testhost working directories and NuGet scratch under the process TEMP; a restore puts packages
/// under the user's NuGet <c>global-packages</c> folder; the CLI keeps state under <c>DOTNET_CLI_HOME</c>;
/// a single-file tool extracts under <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>; and every <c>Path.GetTempPath()</c>
/// in this server resolves TEMP. On a machine whose system drive is small, a long session fills it even
/// when the repository lives on another drive.
///
/// Setting those variables for the process is enough for all of it: child processes inherit the
/// environment, and <c>Path.GetTempPath()</c> re-reads TEMP on each call. <see cref="DiskUsageService"/>
/// writes the policy, applies it, and reports what is still on the system drive; this class is the one
/// place that knows which variables exist and how the policy is persisted.
/// </summary>
public static class DiskPolicy
{
    /// <summary>The variable that names the root even when TEMP itself cannot be moved.</summary>
    public const string TempVariable = "VISUALMCP_TEMP";

    /// <summary>Policy location relative to a workspace: <c>&lt;workspace&gt;/.visualMCP/disk-policy.json</c>.</summary>
    public const string RelativePolicyPath = ".visualMCP/disk-policy.json";

    /// <summary>
    /// The variables a policy owns. Only these are read back from a policy file, so a hand-edited file
    /// cannot set an unrelated variable.
    /// </summary>
    public static readonly string[] ManagedVariables =
    [
        TempVariable,
        "TEMP",
        "TMP",
        "NUGET_PACKAGES",
        "NUGET_HTTP_CACHE_PATH",
        "DOTNET_CLI_HOME",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
        "MSBUILDDISABLENODEREUSE",
    ];

    private static DiskPolicyState _current = DiskPolicyState.Disabled;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>The policy in force in this process, or <see cref="DiskPolicyState.Disabled"/>.</summary>
    public static DiskPolicyState Current => Volatile.Read(ref _current);

    /// <summary>
    /// Publishes a state without touching the environment. <see cref="ApplyToCurrentProcess"/> is the
    /// normal entry point; this is for a host that already set the variables and for restoring a state
    /// in a test.
    /// </summary>
    public static void Set(DiskPolicyState state) => Volatile.Write(ref _current, state);

    /// <summary>
    /// The environment a policy rooted at <paramref name="root"/> sets. Every directory is a child of the
    /// root, so one drive choice moves all of it: <c>temp</c>, <c>nuget-packages</c>, <c>nuget-http-cache</c>,
    /// <c>dotnet-home</c> and <c>dotnet-bundle</c>.
    /// </summary>
    public static DiskPolicyState Build(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A disk policy needs a root directory.", nameof(root));

        var fullRoot = Path.GetFullPath(root);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TempVariable] = Path.Combine(fullRoot, "temp"),
            ["TEMP"] = Path.Combine(fullRoot, "temp"),
            ["TMP"] = Path.Combine(fullRoot, "temp"),
            ["NUGET_PACKAGES"] = Path.Combine(fullRoot, "nuget-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(fullRoot, "nuget-http-cache"),
            ["DOTNET_CLI_HOME"] = Path.Combine(fullRoot, "dotnet-home"),
            ["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(fullRoot, "dotnet-bundle"),
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        return new DiskPolicyState(true, fullRoot, variables, PolicyPath: null);
    }

    /// <summary>
    /// Sets the policy on this process. Child processes inherit it; a <c>Path.GetTempPath()</c> call made
    /// after this line resolves the new TEMP. Directory creation is best effort — the policy must not fail
    /// a server start because one folder could not be created yet.
    /// </summary>
    public static void ApplyToCurrentProcess(DiskPolicyState state)
    {
        if (!state.Enabled) return;

        foreach (var (name, value) in state.Variables)
        {
            try
            {
                if (IsPathVariable(name))
                    Directory.CreateDirectory(value);

                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The variable is still set below; the folder is created on first use or reported by the tool.
                try { Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process); }
                catch { /* the process environment itself refused the value */ }
            }
        }

        Set(state);
    }

    /// <summary>
    /// Adds the policy to one child process. A caller's explicit <c>environment</c> is applied after this
    /// call and still wins, which is what <see cref="Snitch.Execution.ProcessRunner"/> does.
    /// </summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        var state = Current;
        if (!state.Enabled) return;

        foreach (var (name, value) in state.Variables)
            startInfo.Environment[name] = value;
    }

    /// <summary>
    /// Looks for a persisted policy at <paramref name="startDirectory"/> or up to eight folders above it.
    /// A server started inside a project subfolder still finds the workspace's policy.
    /// </summary>
    public static string? FindPolicyFile(string startDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, ".visualMCP", "disk-policy.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable start folder means no policy, not a failed server.
        }

        return null;
    }

    /// <summary>
    /// Reads a policy file. Returns null when the file is absent, disabled or unusable, with
    /// <paramref name="error"/> saying which — the caller decides whether to warn or stay silent.
    /// </summary>
    public static DiskPolicyState? TryLoadFromFile(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"no policy file at {path}";
                return null;
            }

            var document = JsonSerializer.Deserialize<PolicyDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null)
            {
                error = "the policy file is empty";
                return null;
            }

            if (!document.Enabled)
            {
                error = "the policy is disabled";
                return null;
            }

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var documentVariables = document.Variables;
            foreach (var name in ManagedVariables)
            {
                if (documentVariables is null) break;
                if (!TryGetValue(documentVariables, name, out var value) || string.IsNullOrWhiteSpace(value)) continue;

                if (IsPathVariable(name) && !Path.IsPathRooted(value))
                {
                    error = $"{name} is not an absolute path: {value}";
                    return null;
                }

                variables[name] = value;
            }

            if (!TryGetValue(variables, "TEMP", out var temp) || !Path.IsPathRooted(temp))
            {
                error = "the policy does not set an absolute TEMP";
                return null;
            }

            var root = string.IsNullOrWhiteSpace(document.Root) ? Path.GetDirectoryName(temp) : document.Root;
            return new DiskPolicyState(true, root, variables, Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="state"/> to <c>&lt;workspaceDirectory&gt;/.visualMCP/disk-policy.json</c> and
    /// returns the path. Writing is a temp file plus a move, so an interrupted write never leaves a
    /// half-parsed policy behind.
    /// </summary>
    public static string Save(string workspaceDirectory, DiskPolicyState state)
    {
        if (!state.Enabled || state.Root is null)
            throw new ArgumentException("Only an enabled policy can be saved.", nameof(state));

        var directory = Path.Combine(Path.GetFullPath(workspaceDirectory), ".visualMCP");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "disk-policy.json");
        var document = new PolicyDocument
        {
            Enabled = true,
            Root = state.Root,
            Variables = state.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Note = "Written by disk_usage action=guard. TEMP/TMP, the NuGet caches, the .NET CLI home and " +
                   "single-file extraction are redirected here for this process and every child process. " +
                   "Delete this file, or set enabled=false, to stop redirecting.",
        };

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, path, overwrite: true);

        return path;
    }

    /// <summary>
    /// Applies the workspace policy, if there is one, at server start. This is the half that makes the
    /// redirect survive a restart: <c>disk_usage action=guard apply=true</c> writes the file, and the next
    /// process reads it here before anything spawns a build.
    /// </summary>
    public static DiskPolicyState ApplyStartupPolicy(string workingDirectory)
    {
        var path = FindPolicyFile(workingDirectory);
        if (path is null) return Current;

        var state = TryLoadFromFile(path, out _);
        if (state is null) return Current;

        ApplyToCurrentProcess(state);
        return state;
    }

    /// <summary>Whether a managed variable holds a directory (every one except the MSBuild switch).</summary>
    public static bool IsPathVariable(string name) =>
        !string.Equals(name, "MSBUILDDISABLENODEREUSE", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetValue(IReadOnlyDictionary<string, string> values, string name, out string value)
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>The on-disk shape. Deliberately small: a root, the variables, and a note for a human.</summary>
    private sealed class PolicyDocument
    {
        public bool Enabled { get; set; } = true;
        public string? Root { get; set; }
        public Dictionary<string, string>? Variables { get; set; }
        public string? CreatedUtc { get; set; }
        public string? Note { get; set; }
    }
}

/// <summary>
/// An enabled policy: the root it redirects to, the exact environment it sets, and the file it came from
/// (null when it was built in this call rather than loaded).
/// </summary>
public sealed record DiskPolicyState(
    bool Enabled,
    string? Root,
    IReadOnlyDictionary<string, string> Variables,
    string? PolicyPath)
{
    /// <summary>The absence of a policy — what the process starts with.</summary>
    public static DiskPolicyState Disabled { get; } =
        new(false, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null);
}
