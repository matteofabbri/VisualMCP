using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>
/// The answer of <c>deploy_target</c>. Every member is nullable so a refusal leaves the rest unset and
/// the MCP serializer drops them.
/// </summary>
public sealed record DeploymentResult
{
    /// <summary><c>iis</c>, <c>azure-webapp</c> or <c>azure-staticwebapp</c>.</summary>
    public string? Target { get; init; }

    /// <summary>The command that ran.</summary>
    public string? Command { get; init; }

    /// <summary>The command's exit code.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The command's own output (the publish output for IIS, the CLI's for Azure).</summary>
    public string? Output { get; init; }

    /// <summary>Whether the deployment command succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The deployment targets this server did not have: IIS through a Web Deploy publish profile (the same
/// <c>dotnet publish -p:PublishProfile</c> the Web SDK runs, which drives <c>msdeploy</c>), and Azure
/// through the <c>az</c> CLI (a Web App, or a Static Web App from a built folder). Both are wrappers:
/// a missing tool is a structured answer, never a silent no-op, and every call needs <c>confirm</c>
/// because it changes a machine that is not this one.
/// </summary>
public interface IDeploymentService
{
    Task<DeploymentResult> DeployAsync(
        string target,
        string projectName,
        string? profile,
        string? resourceGroup,
        string? appName,
        string? sourcePath,
        bool confirm,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

public sealed class DeploymentService : IDeploymentService
{
    private readonly IRoslynWorkspace _workspace;

    public DeploymentService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<DeploymentResult> DeployAsync(
        string target,
        string projectName,
        string? profile,
        string? resourceGroup,
        string? appName,
        string? sourcePath,
        bool confirm,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var kind = (target ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not ("iis" or "azure-webapp" or "azure-staticwebapp"))
        {
            return new DeploymentResult
            {
                Error = $"Unknown target '{target}': use iis, azure-webapp or azure-staticwebapp.",
            };
        }

        if (!confirm)
        {
            return new DeploymentResult
            {
                Target = kind,
                Error = "Refused: deploying changes a machine that is not this one. Pass confirm=true to proceed.",
            };
        }

        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        var project = solution?.Projects.FirstOrDefault(candidate =>
            candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (project?.FilePath is null) return new DeploymentResult { Target = kind, Error = $"Project '{projectName}' not found." };

        var timeout = Math.Clamp(timeoutSeconds < 60 ? 600 : timeoutSeconds, 60, 3600);
        return kind switch
        {
            "iis" => await IisAsync(project, profile, timeout, cancellationToken),
            "azure-webapp" => await AzureWebAppAsync(project, resourceGroup, appName, sourcePath, timeout, cancellationToken),
            _ => await AzureStaticWebAppAsync(appName, sourcePath, timeout, cancellationToken),
        };
    }

    /// <summary>
    /// IIS: the project's own Web Deploy profile. The SDK's MSDeploy target reads the pubxml's
    /// MSDeployServiceURL/DeployIisAppPath/UserName and calls msdeploy, so there is nothing to reimplement.
    /// </summary>
    private static async Task<DeploymentResult> IisAsync(
        Project project,
        string? profile,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile))
            return new DeploymentResult { Target = "iis", Error = "An IIS deployment needs profile: the name of a pubxml under Properties/PublishProfiles." };

        var profilePath = Path.Combine(
            Path.GetDirectoryName(project.FilePath)!,
            "Properties", "PublishProfiles", profile!.Trim() + ".pubxml");

        if (!File.Exists(profilePath))
        {
            return new DeploymentResult
            {
                Target = "iis",
                Error = $"No publish profile at {profilePath}. Create it with publish_profile " +
                        "(PublishMethod=MSDeploy, MSDeployServiceURL, DeployIisAppPath, UserName, Password) — " +
                        "or use the Web Deploy profile Visual Studio writes.",
            };
        }

        var xml = await File.ReadAllTextAsync(profilePath, cancellationToken);
        if (!xml.Contains("MSDeploy", StringComparison.OrdinalIgnoreCase))
        {
            return new DeploymentResult
            {
                Target = "iis",
                Error = $"{Path.GetFileName(profilePath)} is not a Web Deploy profile: it does not mention MSDeploy. " +
                        "An IIS deployment is a PublishMethod=MSDeploy profile, which dotnet publish drives through msdeploy.",
            };
        }

        var arguments = $"publish \"{project.FilePath}\" -c Release -p:PublishProfile={profile.Trim()} --nologo";
        return await RunAsync("dotnet", arguments, Path.GetDirectoryName(project.FilePath)!, timeoutSeconds, "iis",
            "IIS deployment completed: the Web SDK ran msdeploy with the profile's settings.",
            cancellationToken);
    }

    private static async Task<DeploymentResult> AzureWebAppAsync(
        Project project,
        string? resourceGroup,
        string? appName,
        string? sourcePath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroup) || string.IsNullOrWhiteSpace(appName))
        {
            return new DeploymentResult
            {
                Target = "azure-webapp",
                Error = "An Azure Web App deployment needs resourceGroup and appName.",
            };
        }

        string source;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            // Publish first, then hand the folder to the CLI (it zips a directory for --type zip).
            var output = Path.Combine(Path.GetDirectoryName(project.FilePath)!, "bin", "Release", "publish");
            var publish = await RunAsync("dotnet", $"publish \"{project.FilePath}\" -c Release -o \"{output}\" --nologo",
                Path.GetDirectoryName(project.FilePath)!, timeoutSeconds, "azure-webapp", null, cancellationToken);

            if (publish.Succeeded != true) return publish;
            source = output;
        }
        else
        {
            source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source) && !Directory.Exists(source))
                return new DeploymentResult { Target = "azure-webapp", Error = $"sourcePath does not exist: {source}" };
        }

        var arguments =
            $"webapp deploy --resource-group {Quote(resourceGroup!)} --name {Quote(appName!)} " +
            $"--src-path \"{source}\" --type zip";
        return await RunAsync("az", arguments, Directory.GetCurrentDirectory(), timeoutSeconds, "azure-webapp",
            "Azure Web App deployment completed.", cancellationToken);
    }

    private static async Task<DeploymentResult> AzureStaticWebAppAsync(
        string? appName,
        string? sourcePath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return new DeploymentResult { Target = "azure-staticwebapp", Error = "A Static Web App deployment needs appName." };

        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(Path.GetFullPath(sourcePath)))
        {
            return new DeploymentResult
            {
                Target = "azure-staticwebapp",
                Error = "A Static Web App deployment needs sourcePath: the folder a build produced.",
            };
        }

        var arguments = $"staticwebapp deploy --name {Quote(appName!)} --source \"{Path.GetFullPath(sourcePath)}\"";
        return await RunAsync("az", arguments, Directory.GetCurrentDirectory(), timeoutSeconds, "azure-staticwebapp",
            "Azure Static Web App deployment completed.", cancellationToken);
    }

    private static async Task<DeploymentResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        string target,
        string? successNote,
        CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
                executable, arguments, workingDirectory, timeoutSeconds);

            var succeeded = !timedOut && exitCode == 0;

            return new DeploymentResult
            {
                Target = target,
                Command = $"{executable} {arguments}",
                ExitCode = exitCode,
                Output = Truncate(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout),
                Succeeded = succeeded,
                Note = succeeded ? successNote : timedOut ? "The command timed out." : null,
                Error = succeeded
                    ? null
                    : timedOut
                        ? "timed out."
                        : Truncate(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 4000),
            };
        }
        catch (Win32Exception)
        {
            return new DeploymentResult
            {
                Target = target,
                Command = $"{executable} {arguments}",
                Error = $"'{executable}' was not found on PATH. " +
                        (executable == "az"
                            ? "Install the Azure CLI (https://aka.ms/azure-cli) and sign in, then retry."
                            : "Install Web Deploy and the .NET SDK's IIS publishing support, then retry."),
            };
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Truncate(string value, int limit = 8000) =>
        value.Length <= limit ? value : value[..limit] + $"…(truncated, {value.Length - limit} more chars)";
}
