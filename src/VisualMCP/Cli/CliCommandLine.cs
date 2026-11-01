using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snitch;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Inspections;
using Snitch.Languages.CSharp.Refactoring;
using Snitch.Languages.VB;

namespace VisualMCP.Cli;

/// <summary>
/// The standalone command line: the same engines the MCP tools drive, without an MCP client, for a
/// pipeline. <c>VisualMCP --cli inspect|duplicates|cleanup [--solution path] [--json]</c> prints the
/// answer on stdout and returns the exit code the ReSharper command-line tools use —
/// 0 when the gate passes, 1 when it fails, 2 on a usage or load error.
/// </summary>
/// <remarks>
/// It never starts the MCP transport, the dashboard or the background warm-up: the process does one
/// job and exits. MSBuild has already been located by <c>Program</c> before this runs, which is what
/// lets Roslyn load a solution here.
/// </remarks>
public static class CliCommandLine
{
    private const int Passed = 0;
    private const int Failed = 1;
    private const int Usage = 2;

    /// <summary>Dispatches <c>--cli …</c>; false when the arguments are not for the CLI at all.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], "--cli", StringComparison.OrdinalIgnoreCase))
            return false;

        exitCode = RunAsync(args[1..]).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        // Deliberately not disposed: closing the writer would close the process's standard handles, and
        // this method also runs in-process from the tests.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await stdout.WriteLineAsync(UsageText);
            return args.Length == 0 ? Usage : Passed;
        }

        var command = args[0].ToLowerInvariant();
        if (command is not ("inspect" or "duplicates" or "cleanup"))
        {
            await stderr.WriteLineAsync($"unknown command '{args[0]}'; expected inspect, duplicates or cleanup.");
            return Usage;
        }

        var options = CliOptions.Parse(args[1..]);
        if (options.Error is not null)
        {
            await stderr.WriteLineAsync(options.Error);
            return Usage;
        }

        using var host = BuildHost();
        var context = host.Services.GetRequiredService<SnitchContext>();

        try
        {
            if (options.Solution is not null)
            {
                var solutionPath = Path.GetFullPath(options.Solution);
                if (!File.Exists(solutionPath))
                {
                    await stderr.WriteLineAsync($"solution not found: {solutionPath}");
                    return Usage;
                }

                var loaded = await context.LoadSolutionAsync(solutionPath);
                if (loaded.Solution is null)
                {
                    await stderr.WriteLineAsync(
                        $"could not load {solutionPath}: {string.Join("; ", loaded.Diagnostics)}");
                    return Usage;
                }
            }
            else
            {
                var opened = await context.EnsureSolutionLoadedAsync();
                if (opened is null)
                {
                    await stderr.WriteLineAsync(
                        "no solution found under the working directory; pass --solution <path>.");
                    return Usage;
                }
            }

            return command switch
            {
                "inspect" => await InspectAsync(host.Services, options, stdout),
                "duplicates" => await DuplicatesAsync(host.Services, options, stdout),
                _ => await CleanupAsync(host.Services, options, stdout),
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"{command} failed: {ex.Message}");
            return Usage;
        }
    }

    private static async Task<int> InspectAsync(IServiceProvider services, CliOptions options, TextWriter output)
    {
        var report = await services.GetRequiredService<IInspectCodeService>().RunAsync(
            options.Project,
            null,
            options.Get("--packs", "house,roslynator,netanalyzers,asyncfixer"),
            options.Get("--severity", "info"),
            options.Optional("--ids"),
            options.Optional("--exclude-ids"),
            options.Has("--enable-all"),
            options.Int("--max-results", 3000),
            options.Int("--timeout", 900));

        if (report.Error is not null)
        {
            await output.WriteLineAsync(report.Error);
            return Usage;
        }

        if (options.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            foreach (var finding in report.Findings)
            {
                await output.WriteLineAsync(
                    $"{finding.File}({finding.Line},{finding.Column}): {finding.Severity.ToLowerInvariant()} " +
                    $"{finding.InspectionId} {finding.Message}");
            }

            await output.WriteLineAsync(
                $"{report.Total} finding(s) in {report.Solution} ({report.ElapsedMs} ms): " +
                string.Join(", ", report.BySeverity.OrderByDescending(entry => entry.Value)
                    .Select(entry => $"{entry.Value} {entry.Key.ToLowerInvariant()}")) +
                (report.Truncated ? $" — the report was truncated at {report.Returned}" : string.Empty));

            foreach (var warning in report.Warnings) await output.WriteLineAsync($"warning: {warning}");
        }

        return FailsTheGate(report, options) ? Failed : Passed;
    }

    private static async Task<int> DuplicatesAsync(IServiceProvider services, CliOptions options, TextWriter output)
    {
        var result = await services.GetRequiredService<IFindDuplicateCodeService>()
            .FindDuplicateCode(options.Project, options.Int("--min-statements", 4), options.Int("--min-tokens", 30));

        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));

        // A clone report has no severity: the gate is "the report holds no cluster".
        var clusters = JsonSerializer.SerializeToNode(result) is JsonObject report &&
                       report["totalClusters"] is JsonValue value &&
                       value.TryGetValue<int>(out var count)
            ? count
            : -1;

        return clusters == 0 ? Passed : Failed;
    }

    private static async Task<int> CleanupAsync(IServiceProvider services, CliOptions options, TextWriter output)
    {
        var result = await services.GetRequiredService<ICodeCleanupService>().Cleanup(
            options.Get("--scope", "solution"),
            filePath: null,
            options.Project,
            options.Optional("--steps"),
            dryRun: !options.Has("--apply"),
            profile: options.Get("--profile", "safe"));

        await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
        return Passed;
    }

    private static bool FailsTheGate(InspectionReport report, CliOptions options)
    {
        var threshold = options.Get("--fail-on", "error").ToLowerInvariant();
        if (threshold == "none") return false;

        var failing = report.BySeverity
            .Where(entry => entry.Key.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                            (threshold == "warning" && entry.Key.Contains("warning", StringComparison.OrdinalIgnoreCase)))
            .Sum(entry => entry.Value);

        return failing > 0;
    }

    /// <summary>A host with the engines and no transport: the same library the server loads, one command.</summary>
    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                // A console logger would corrupt the CLI's own stdout.
                logging.ClearProviders();
            })
            .ConfigureServices(services =>
            {
                services.AddSnitch(o =>
                {
                    o.ApplicationName = "VisualMCP";
                    o.ProcessName = "VisualMCP-cli";
                });
                services.AddSnitchVisualBasic();
                // Registered here rather than by AddSnitch for the same reason the server does it: the
                // packs ride in this assembly, not in the library.
                services.AddSingleton<IInspectCodeService, InspectCodeService>();
            })
            .Build();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string UsageText = """
        VisualMCP command line — the IDE analyses, without an MCP client.

        Usage:
          VisualMCP --cli inspect    [--solution <path>] [--project <name>] [--packs <list>]
                                     [--severity error|warning|info] [--ids <glob,list>]
                                     [--exclude-ids <list>] [--enable-all] [--max-results <n>]
                                     [--fail-on error|warning|none] [--json]
          VisualMCP --cli duplicates [--solution <path>] [--project <name>]
                                     [--min-statements <n>] [--min-tokens <n>] [--json]
          VisualMCP --cli cleanup    [--solution <path>] [--project <name>] [--scope solution|project|document]
                                     [--steps style,usings,regions,reorder,format] [--profile <name>]
                                     [--apply] [--json]

        Exit codes: 0 the gate passed, 1 the gate failed, 2 a usage or load error.
        Without --solution the solution under the working directory is used.
        Without --json every command still prints its findings; duplicates and cleanup print JSON.
        """;

    /// <summary>The arguments after the command: <c>--name value</c> pairs and flags.</summary>
    private sealed class CliOptions
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Error { get; private init; }

        public string? Solution => _values.GetValueOrDefault("--solution");

        public string? Project => _values.GetValueOrDefault("--project");

        public bool Json => _values.ContainsKey("--json");

        public bool Has(string name) => _values.ContainsKey(name);

        public string Get(string name, string fallback) => _values.GetValueOrDefault(name) ?? fallback;

        public string? Optional(string name) => _values.GetValueOrDefault(name);

        public int Int(string name, int fallback) =>
            int.TryParse(_values.GetValueOrDefault(name), out var value) ? value : fallback;

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();

            for (var index = 0; index < args.Length; index++)
            {
                var name = args[index];
                if (!name.StartsWith("--", StringComparison.Ordinal))
                    return new CliOptions { Error = $"unexpected argument '{name}': options start with --." };

                // Flags take no value; every other option takes the next argument.
                if (name is "--json" or "--apply" or "--enable-all")
                {
                    options._values[name] = null;
                    continue;
                }

                if (index + 1 >= args.Length)
                    return new CliOptions { Error = $"'{name}' needs a value." };

                options._values[name] = args[++index];
            }

            return options;
        }
    }
}
