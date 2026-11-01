using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Snitch.Workspace;

namespace Snitch.Execution;

/// <summary>One file <c>installer_project</c> wrote, or that was already there.</summary>
public sealed record InstallerFile(string Path, bool Written, long Bytes);

/// <summary>
/// The answer of <c>installer_project</c>. Every member is nullable so a refusal leaves the rest unset
/// and the MCP serializer drops them.
/// </summary>
public sealed record InstallerAuthoringResult
{
    /// <summary><c>wix</c> or <c>clickonce</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><c>scaffold</c> or <c>verify</c>.</summary>
    public string? Action { get; init; }

    /// <summary>The files the scaffold wrote (or found already present).</summary>
    public IReadOnlyList<InstallerFile>? Files { get; init; }

    /// <summary>The command the verification ran.</summary>
    public string? Command { get; init; }

    /// <summary>The verification's exit code.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The verification's own output.</summary>
    public string? Output { get; init; }

    /// <summary>Whether the verification's build succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether the call only reported what it would write.</summary>
    public bool? DryRun { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Installer authoring, the one packaging target this server did not have: a WiX v4 project that packs a
/// publish folder into an MSI, and a ClickOnce publish profile. Both are written as XML, verified to
/// parse, and — on request — built, so the "a build can verify it" half is a real command, not a claim.
/// </summary>
public interface IInstallerAuthoringService
{
    Task<InstallerAuthoringResult> ScaffoldAsync(
        string projectName,
        string kind,
        string? name,
        bool dryRun,
        CancellationToken cancellationToken = default);

    Task<InstallerAuthoringResult> VerifyAsync(
        string projectName,
        string kind,
        string? name,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

public sealed class InstallerAuthoringService : IInstallerAuthoringService
{
    /// <summary>The WiX toolset the generated project targets (v4 and v5 share the vocabulary used here).</summary>
    public const string WixSdk = "WixToolset.Sdk/5.0.0";

    private readonly IRoslynWorkspace _workspace;

    public InstallerAuthoringService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<InstallerAuthoringResult> ScaffoldAsync(
        string projectName,
        string kind,
        string? name,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var project = await FindAsync(projectName, cancellationToken);
        if (project is null) return new InstallerAuthoringResult { Kind = kind, Error = $"Project '{projectName}' not found." };
        if (project.FilePath is null) return new InstallerAuthoringResult { Kind = kind, Error = $"Project '{projectName}' has no file on disk." };

        var directory = Path.GetDirectoryName(project.FilePath)!;
        var kindName = (kind ?? string.Empty).Trim().ToLowerInvariant();

        return kindName switch
        {
            "wix" => await ScaffoldWixAsync(directory, projectName, name, dryRun, cancellationToken),
            "clickonce" => await ScaffoldClickOnceAsync(directory, name, dryRun, cancellationToken),
            _ => new InstallerAuthoringResult { Kind = kind, Error = $"Unknown kind '{kind}': use wix or clickonce." },
        };
    }

    public async Task<InstallerAuthoringResult> VerifyAsync(
        string projectName,
        string kind,
        string? name,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var project = await FindAsync(projectName, cancellationToken);
        if (project is null) return new InstallerAuthoringResult { Kind = kind, Error = $"Project '{projectName}' not found." };
        if (project.FilePath is null) return new InstallerAuthoringResult { Kind = kind, Error = $"Project '{projectName}' has no file on disk." };

        var kindName = (kind ?? string.Empty).Trim().ToLowerInvariant();
        var timeout = Math.Clamp(timeoutSeconds < 30 ? 300 : timeoutSeconds, 30, 1800);

        string command;
        string arguments;
        if (kindName == "wix")
        {
            var wixProject = Path.Combine(
                Path.GetDirectoryName(project.FilePath)!,
                "Installer",
                $"{InstallerName(projectName + ".Installer", name)}.wixproj");

            if (!File.Exists(wixProject))
                return new InstallerAuthoringResult { Kind = "wix", Action = "verify", Error = $"No WiX project at {wixProject}; scaffold it first." };

            command = "dotnet";
            arguments = $"build \"{wixProject}\" -c Release --nologo";
        }
        else if (kindName == "clickonce")
        {
            command = "dotnet";
            arguments = $"publish \"{project.FilePath}\" -c Release -p:PublishProfile={InstallerName("ClickOnce", name)} --nologo";
        }
        else
        {
            return new InstallerAuthoringResult { Kind = kind, Error = $"Unknown kind '{kind}': use wix or clickonce." };
        }

        var (exitCode, timedOut, stdout, stderr, _) = await ProcessRunner.RunAsync(
            command, arguments, Path.GetDirectoryName(project.FilePath)!, timeout);

        var succeeded = !timedOut && exitCode == 0;

        return new InstallerAuthoringResult
        {
            Kind = kindName,
            Action = "verify",
            Command = $"{command} {arguments}",
            ExitCode = exitCode,
            Output = Truncate(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout),
            Succeeded = succeeded,
            Note = succeeded
                ? kindName == "wix"
                    ? "The WiX project built: the MSI is under its bin folder."
                    : "The ClickOnce publish completed."
                : timedOut
                    ? "The build timed out."
                    : kindName == "wix"
                        ? "The WiX SDK restores from NuGet on the first build; a failure with NU1101 means no network or a mistyped WixToolset.Sdk version."
                        : "Read Output: a missing signing certificate or an install URL the SDK refuses is reported there.",
            Error = succeeded ? null : timedOut ? "timed out." : Truncate(stderr, 2000),
        };
    }

    private static async Task<InstallerAuthoringResult> ScaffoldWixAsync(
        string projectDirectory,
        string projectName,
        string? name,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var installerName = InstallerName(projectName + ".Installer", name);
        var directory = Path.Combine(projectDirectory, "Installer");
        var projectPath = Path.Combine(directory, installerName + ".wixproj");
        var packagePath = Path.Combine(directory, "Package.wxs");

        var projectXml = $"""
            <Project Sdk="{WixSdk}">
              <PropertyGroup>
                <OutputName>{installerName}</OutputName>
                <OutputType>Package</OutputType>
                <PublishDir Condition="'$(PublishDir)' == ''">$(MSBuildProjectDirectory)\..\bin\Release\publish\</PublishDir>
              </PropertyGroup>
            </Project>
            """;

        var packageXml = $"""
            <Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
              <Package Name="{projectName}"
                       Manufacturer="{projectName}"
                       Version="1.0.0"
                       UpgradeCode="{StableGuid(projectName)}">
                <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." />
                <StandardDirectory Id="ProgramFiles64Folder">
                  <Directory Id="INSTALLFOLDER" Name="{projectName}" />
                </StandardDirectory>
                <Feature Id="Main">
                  <ComponentGroupRef Id="PublishedFiles" />
                </Feature>
                <ComponentGroup Id="PublishedFiles" Directory="INSTALLFOLDER">
                  <Files Include="$(var.PublishDir)\**" />
                </ComponentGroup>
              </Package>
            </Wix>
            """;

        return await WriteAsync("wix", "scaffold", dryRun, cancellationToken,
            (projectPath, projectXml),
            (packagePath, packageXml));
    }

    private static async Task<InstallerAuthoringResult> ScaffoldClickOnceAsync(
        string projectDirectory,
        string? name,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var profileName = InstallerName("ClickOnce", name);
        var path = Path.Combine(projectDirectory, "Properties", "PublishProfiles", profileName + ".pubxml");

        var xml = """
            <Project>
              <PropertyGroup>
                <PublishProtocol>ClickOnce</PublishProtocol>
                <PublishUrl>publish/</PublishUrl>
                <InstallUrl>http://localhost/publish/</InstallUrl>
                <UpdateUrl>http://localhost/publish/</UpdateUrl>
                <CreateWebPageOnPublish>true</CreateWebPageOnPublish>
                <WebPageFileName>default.htm</WebPageFileName>
                <ApplicationRevision>0</ApplicationRevision>
                <ApplicationVersion>1.0.0.*</ApplicationVersion>
                <UseApplicationTrust>false</UseApplicationTrust>
                <BootstrapperEnabled>true</BootstrapperEnabled>
                <PublishSingleFile>false</PublishSingleFile>
                <SelfContained>false</SelfContained>
                <PublishReadyToRun>false</PublishReadyToRun>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
              </PropertyGroup>
            </Project>
            """;

        return await WriteAsync("clickonce", "scaffold", dryRun, cancellationToken, (path, xml));
    }

    /// <summary>Writes each file, refusing a generated document that would not parse as XML.</summary>
    private static async Task<InstallerAuthoringResult> WriteAsync(
        string kind,
        string action,
        bool dryRun,
        CancellationToken cancellationToken,
        params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            try
            {
                XDocument.Parse(content);
            }
            catch (Exception ex)
            {
                return new InstallerAuthoringResult { Kind = kind, Action = action, Error = $"The generated {Path.GetFileName(path)} is not XML: {ex.Message}" };
            }
        }

        var written = new List<InstallerFile>();
        foreach (var (path, content) in files)
        {
            if (dryRun)
            {
                written.Add(new InstallerFile(path, Written: false, Encoding.UTF8.GetByteCount(content)));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                written.Add(new InstallerFile(path, Written: false, new FileInfo(path).Length));
                continue;
            }

            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
            written.Add(new InstallerFile(path, Written: true, Encoding.UTF8.GetByteCount(content)));
        }

        var created = written.Count(file => file.Written);
        return new InstallerAuthoringResult
        {
            Kind = kind,
            Action = action,
            Files = written,
            DryRun = dryRun,
            Succeeded = true,
            Note = dryRun
                ? "Preview only; pass dryRun=false to write the files."
                : $"{created} file(s) written, {written.Count - created} already present. " +
                  (kind == "wix"
                      ? "Verify with action=verify (it builds the .wixproj; the WiX SDK restores from NuGet)."
                      : "Verify with action=verify (it runs dotnet publish with this profile)."),
        };
    }

    private async Task<Project?> FindAsync(string projectName, CancellationToken cancellationToken)
    {
        var solution = await _workspace.EnsureSolutionLoadedAsync(cancellationToken);
        return solution?.Projects.FirstOrDefault(candidate =>
            candidate.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
    }

    private static string InstallerName(string fallback, string? name) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name!.Trim();

    /// <summary>A GUID derived from the name, so re-scaffolding keeps the product's upgrade identity.</summary>
    private static string StableGuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(name));
        return new Guid(hash).ToString("D").ToUpperInvariant();
    }

    private static string Truncate(string value, int limit = 8000) =>
        value.Length <= limit ? value : value[..limit] + $"…(truncated, {value.Length - limit} more chars)";
}
