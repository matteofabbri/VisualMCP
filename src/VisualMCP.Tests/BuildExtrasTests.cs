using System.Xml.Linq;
using Snitch.Execution;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The three last build/run/packaging targets: the template wrapper's command and its refusal paths, the
/// installer files that are actually written (and verified to parse), and the deployment gate.
/// </summary>
public sealed class BuildExtrasTests
{
    [Fact]
    public async Task Creating_from_a_template_is_a_command_and_nothing_else_on_a_dry_run()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var service = new ProjectTemplateService();

        var result = await service.RunAsync(
            "create", "classlib", "Demo.Library", output: null, framework: "net10.0", language: null,
            dryRun: true, timeoutSeconds: 60);

        Assert.True(result.DryRun);
        Assert.Contains("dotnet new classlib --name Demo.Library", result.Command);
        Assert.Contains("--framework net10.0", result.Command);
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Demo.Library")));
    }

    [Fact]
    public async Task An_unknown_template_action_is_a_usage_error()
    {
        var result = await new ProjectTemplateService().RunAsync(
            "instantiate", null, null, null, null, null, dryRun: true, timeoutSeconds: 30);

        Assert.Contains("list or create", result.Error);
    }

    [Fact]
    public async Task Creating_a_project_needs_a_name()
    {
        var result = await new ProjectTemplateService().RunAsync(
            "create", "classlib", null, null, null, null, dryRun: true, timeoutSeconds: 30);

        Assert.Contains("name is required", result.Error);
    }

    [Fact]
    public async Task The_wix_installer_is_scaffolded_as_two_xml_files()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var service = new InstallerAuthoringService(workspace);

        var result = await service.ScaffoldAsync("Probe", "wix", null, dryRun: false);

        Assert.Null(result.Error);
        Assert.True(result.Succeeded);
        Assert.All(result.Files!, file => Assert.True(File.Exists(file.Path)));

        var wixProject = Path.Combine(workspace.RootDirectory, "Installer", "Probe.Installer.wixproj");
        var package = Path.Combine(workspace.RootDirectory, "Installer", "Package.wxs");
        Assert.True(File.Exists(wixProject));
        Assert.True(File.Exists(package));

        Assert.Contains(InstallerAuthoringService.WixSdk, XDocument.Load(wixProject).ToString());
        var packageXml = XDocument.Load(package);
        Assert.Equal("http://wixtoolset.org/schemas/v4/wxs", packageXml.Root!.Name.NamespaceName);
        Assert.Contains("UpgradeCode", packageXml.ToString());
    }

    [Fact]
    public async Task Scaffolding_twice_does_not_overwrite_the_files_it_wrote()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var service = new InstallerAuthoringService(workspace);

        await service.ScaffoldAsync("Probe", "wix", null, dryRun: false);
        var second = await service.ScaffoldAsync("Probe", "wix", null, dryRun: false);

        Assert.All(second.Files!, file => Assert.False(file.Written));
        Assert.Contains("already present", second.Note);
    }

    [Fact]
    public async Task The_clickonce_profile_carries_the_clickonce_keys()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));

        var result = await new InstallerAuthoringService(workspace).ScaffoldAsync("Probe", "clickonce", null, dryRun: false);

        Assert.Null(result.Error);
        var profile = Path.Combine(workspace.RootDirectory, "Properties", "PublishProfiles", "ClickOnce.pubxml");
        Assert.True(File.Exists(profile));

        var xml = XDocument.Load(profile).ToString();
        Assert.Contains("<PublishProtocol>ClickOnce</PublishProtocol>", xml);
        Assert.Contains("<InstallUrl>", xml);
    }

    [Fact]
    public async Task An_unknown_installer_kind_is_refused()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));

        var result = await new InstallerAuthoringService(workspace).ScaffoldAsync("Probe", "msix", null, dryRun: true);

        Assert.Contains("wix or clickonce", result.Error);
    }

    [Fact]
    public async Task A_deployment_is_refused_without_confirm()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));

        var result = await new DeploymentService(workspace).DeployAsync(
            "iis", "Probe", profile: "Folder", resourceGroup: null, appName: null, sourcePath: null,
            confirm: false, timeoutSeconds: 60);

        Assert.Contains("confirm=true", result.Error);
    }

    [Fact]
    public async Task An_iis_deployment_needs_a_web_deploy_profile()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var directory = Path.Combine(workspace.RootDirectory, "Properties", "PublishProfiles");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Folder.pubxml"),
            "<Project><PropertyGroup><PublishMethod>FileSystem</PublishMethod></PropertyGroup></Project>");

        var result = await new DeploymentService(workspace).DeployAsync(
            "iis", "Probe", profile: "Folder", resourceGroup: null, appName: null, sourcePath: null,
            confirm: true, timeoutSeconds: 60);

        Assert.Contains("MSDeploy", result.Error);
    }

    [Fact]
    public async Task An_azure_deployment_needs_its_coordinates()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }\n"));
        var service = new DeploymentService(workspace);

        var noGroup = await service.DeployAsync(
            "azure-webapp", "Probe", null, resourceGroup: null, appName: "app", sourcePath: null,
            confirm: true, timeoutSeconds: 60);
        Assert.Contains("resourceGroup", noGroup.Error);

        var unknown = await service.DeployAsync(
            "ftp", "Probe", null, null, null, null, confirm: true, timeoutSeconds: 60);
        Assert.Contains("iis, azure-webapp or azure-staticwebapp", unknown.Error);
    }
}
