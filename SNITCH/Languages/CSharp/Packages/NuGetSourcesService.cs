using System.Text.RegularExpressions;
using Snitch.Execution;

namespace Snitch.Languages.CSharp.Packages;

/// <summary>
/// The NuGet package sources of the repository, through the .NET CLI: list them with their state,
/// add/update/remove one and enable/disable it. Credentials are passed to the CLI and never echoed
/// back — the command this service reports has the password redacted.
/// </summary>
public interface INuGetSourcesService
{
    Task<object> ListAsync(string? configFile, int timeoutSeconds);
    Task<object> AddAsync(string name, string url, string? username, string? password,
        bool storePasswordInClearText, string? configFile, int timeoutSeconds);
    Task<object> UpdateAsync(string name, string? url, string? username, string? password,
        bool storePasswordInClearText, string? configFile, int timeoutSeconds);
    Task<object> RemoveAsync(string name, string? configFile, int timeoutSeconds);
    Task<object> ToggleAsync(string name, bool enable, string? configFile, int timeoutSeconds);
}

public sealed class NuGetSourcesService : INuGetSourcesService
{
    private static readonly Regex SourceRow = new(
        @"(?m)^\s*\d+\.\s+(?<name>.+?)\s+\[(?<state>[^\]]+)\]\s*$",
        RegexOptions.CultureInvariant);

    // ── list ──────────────────────────────────────────────────────────────────
    public async Task<object> ListAsync(string? configFile, int timeoutSeconds)
    {
        var arguments = ListArguments(configFile);
        var (ok, command, stdout, stderr, error) = await RunAsync(arguments, arguments, timeoutSeconds);

        if (!ok || error is not null)
            return new { error = error ?? $"dotnet nuget list source failed.", command, output = Truncate(stdout, stderr) };

        return new
        {
            sources = ParseSources(stdout),
            command,
            note = configFile is null
                ? "These are the sources the repository's NuGet.Config chain resolves to, with their enabled state."
                : $"Read from {configFile}.",
            output = ProcessRunner.Truncate(stdout.Trim(), 4000),
        };
    }

    // ── add ───────────────────────────────────────────────────────────────────
    public async Task<object> AddAsync(
        string name, string url, string? username, string? password,
        bool storePasswordInClearText, string? configFile, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A source name is required." };
        if (string.IsNullOrWhiteSpace(url)) return new { error = "A source URL (or local folder) is required." };
        if (password is not null && string.IsNullOrWhiteSpace(username))
            return new { error = "A password needs a username: NuGet stores the credential pair together." };

        var arguments = new List<string> { "nuget", "add", "source", Quote(url), "--name", Quote(name) };
        AppendCredentials(arguments, username, password, storePasswordInClearText);
        AppendConfigFile(arguments, configFile);

        var (ok, command, stdout, stderr, error) = await RunAsync(arguments, Redacted(arguments), timeoutSeconds);
        if (!ok || error is not null)
            return new { error = error ?? "dotnet nuget add source failed.", command, output = Truncate(stdout, stderr) };

        return new
        {
            name,
            url,
            credentials = CredentialState(username, password, storePasswordInClearText),
            command,
            note = "The source is now part of the NuGet.Config chain this repository resolves; push and restore use it immediately.",
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000),
        };
    }

    // ── update ────────────────────────────────────────────────────────────────
    public async Task<object> UpdateAsync(
        string name, string? url, string? username, string? password,
        bool storePasswordInClearText, string? configFile, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A source name is required." };
        if (url is null && username is null && password is null)
            return new { error = "Nothing to update: pass url, username and/or password." };
        if (password is not null && string.IsNullOrWhiteSpace(username))
            return new { error = "A password needs a username: NuGet stores the credential pair together." };

        var arguments = new List<string> { "nuget", "update", "source", Quote(name) };
        if (url is not null) { arguments.Add("--source"); arguments.Add(Quote(url)); }
        AppendCredentials(arguments, username, password, storePasswordInClearText);
        AppendConfigFile(arguments, configFile);

        var (ok, command, stdout, stderr, error) = await RunAsync(arguments, Redacted(arguments), timeoutSeconds);
        if (!ok || error is not null)
            return new { error = error ?? "dotnet nuget update source failed.", command, output = Truncate(stdout, stderr) };

        return new
        {
            name,
            url,
            credentials = CredentialState(username, password, storePasswordInClearText),
            command,
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000),
        };
    }

    // ── remove ────────────────────────────────────────────────────────────────
    public async Task<object> RemoveAsync(string name, string? configFile, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A source name is required." };

        var arguments = new List<string> { "nuget", "remove", "source", Quote(name) };
        AppendConfigFile(arguments, configFile);

        var (ok, command, stdout, stderr, error) = await RunAsync(arguments, arguments, timeoutSeconds);
        if (!ok || error is not null)
            return new { error = error ?? "dotnet nuget remove source failed.", command, output = Truncate(stdout, stderr) };

        return new { name, removed = true, command, output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000) };
    }

    // ── enable / disable ─────────────────────────────────────────────────────
    public async Task<object> ToggleAsync(string name, bool enable, string? configFile, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(name)) return new { error = "A source name is required." };

        var arguments = new List<string> { "nuget", enable ? "enable" : "disable", "source", Quote(name) };
        AppendConfigFile(arguments, configFile);

        var (ok, command, stdout, stderr, error) = await RunAsync(arguments, arguments, timeoutSeconds);
        if (!ok || error is not null)
            return new { error = error ?? $"dotnet nuget {(enable ? "enable" : "disable")} source failed.", command, output = Truncate(stdout, stderr) };

        return new
        {
            name,
            enabled = enable,
            command,
            note = enable
                ? "Restores and pushes from this source are attempted again."
                : "The source stays configured but is skipped by restore and push until it is enabled again.",
            output = ProcessRunner.Truncate((stdout + stderr).Trim(), 2000),
        };
    }

    // ── parsing ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Parses <c>dotnet nuget list source --format detailed</c>: a numbered name with its state, then
    /// the URL on the following line. A row whose URL line is missing still comes back, with a null url.
    /// </summary>
    public static IReadOnlyList<object> ParseSources(string output)
    {
        var sources = new List<object>();
        foreach (Match row in SourceRow.Matches(output).Cast<Match>())
        {
            var rest = output[(row.Index + row.Length)..];
            var nextLine = rest
                .Split('\n')
                .Select(line => line.Trim('\r', ' ', '\t'))
                .FirstOrDefault(line => line.Length > 0 && !line.EndsWith(':'));

            var url = nextLine is not null && !SourceRow.IsMatch(nextLine) ? nextLine : null;

            sources.Add(new
            {
                name = row.Groups["name"].Value.Trim(),
                enabled = IsEnabled(row.Groups["state"].Value),
                url,
            });
        }

        return sources;
    }

    /// <summary>The CLI prints its state word in the machine's language; only "disabled" is decisive.</summary>
    private static bool IsEnabled(string state) => !state.Contains("dis", StringComparison.OrdinalIgnoreCase);

    // ── plumbing ──────────────────────────────────────────────────────────────
    private static List<string> ListArguments(string? configFile)
    {
        var arguments = new List<string> { "nuget", "list", "source", "--format", "detailed" };
        AppendConfigFile(arguments, configFile);
        return arguments;
    }

    private static void AppendCredentials(
        List<string> arguments, string? username, string? password, bool storePasswordInClearText)
    {
        if (!string.IsNullOrWhiteSpace(username)) { arguments.Add("--username"); arguments.Add(Quote(username)); }
        if (password is not null) { arguments.Add("--password"); arguments.Add(Quote(password)); }
        if (password is not null && storePasswordInClearText) arguments.Add("--store-password-in-clear-text");
    }

    private static void AppendConfigFile(List<string> arguments, string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile)) return;
        arguments.Add("--configfile");
        arguments.Add(Quote(configFile));
    }

    private static List<string> Redacted(List<string> arguments)
    {
        var copy = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            copy.Add(arguments[index]);
            if (arguments[index] != "--password") continue;

            copy[^1] = "--password";
            copy.Add("\"***\"");
            index++; // skip the real value
        }

        return copy;
    }

    private static string CredentialState(string? username, string? password, bool storePasswordInClearText)
    {
        if (password is null) return username is null ? "none" : "username set, password unchanged";
        return storePasswordInClearText
            ? "username and password stored in clear text (requested)"
            : "username and password stored encrypted by NuGet";
    }

    private static string Truncate(string stdout, string stderr) =>
        ProcessRunner.Truncate((stdout + "\n" + stderr).Trim(), 4000);

    private static string Quote(string value) => $"\"{value}\"";

    private static async Task<(bool Ok, string Command, string Stdout, string Stderr, string? Error)> RunAsync(
        List<string> arguments, List<string> displayArguments, int timeoutSeconds)
    {
        var command = "dotnet " + string.Join(" ", displayArguments);
        try
        {
            // A stable output language: the rows are parsed, and the CLI translates its own output.
            var environment = new Dictionary<string, string>
            {
                ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                ["DOTNET_NOLOGO"] = "1",
            };

            var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
                "dotnet", string.Join(" ", arguments), Directory.GetCurrentDirectory(), Clamp(timeoutSeconds), environment);

            if (timedOut) return (false, command, stdout, stderr, $"dotnet nuget timed out after {Clamp(timeoutSeconds)}s.");
            if (exitCode != 0) return (false, command, stdout, stderr, null);

            return (true, command, stdout, stderr, null);
        }
        catch (Exception exception)
        {
            return (false, command, "", "", $"Could not start the .NET CLI: {exception.Message}");
        }
    }

    private static int Clamp(int seconds) => seconds < 1 ? 1 : seconds > 900 ? 900 : seconds;
}
