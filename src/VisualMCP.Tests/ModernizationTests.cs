using Snitch.Languages.CSharp.Modernization;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The version-pinning files, edited as text: what gets bumped, what must stay (netstandard/net4x), and
/// the three shapes a package version can take.
/// </summary>
public sealed class ProjectFilesTests
{
    [Theory]
    [InlineData("net8.0", "net10.0", "net10.0")]
    [InlineData("net9.0", "net10.0", "net10.0")]
    [InlineData("net8.0-windows10.0.19041.0", "net10.0", "net10.0-windows10.0.19041.0")]
    [InlineData("net9.0-android", "net10.0", "net10.0-android")]
    [InlineData("net8.0", "net10.0-windows", "net10.0-windows")]
    [InlineData("net10.0", "net10.0", null)]
    [InlineData("net11.0", "net10.0", null)]
    [InlineData("netstandard2.0", "net10.0", null)]
    [InlineData("netstandard2.1", "net10.0", null)]
    [InlineData("net48", "net10.0", null)]
    [InlineData("netcoreapp3.1", "net10.0", null)]
    public void Bump_maps_only_modern_net_targets(string tfm, string target, string? expected) =>
        Assert.Equal(expected, ProjectFiles.BumpTargetFramework(tfm, target));

    [Theory]
    [InlineData("net10.0", "net10.0")]
    [InlineData("net10", "net10.0")]
    [InlineData("10.0", "net10.0")]
    [InlineData("NET10.0-WINDOWS", "net10.0-windows")]
    [InlineData("  net10.0  ", "net10.0")]
    public void Target_normalisation_accepts_what_callers_type(string input, string expected)
    {
        Assert.True(ProjectFiles.TryNormalizeTargetFramework(input, out var normalized, out var error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("netstandard2.0")]
    [InlineData("net48")]
    [InlineData("")]
    [InlineData("latest")]
    public void Target_normalisation_rejects_what_is_not_a_modern_tfm(string input)
    {
        Assert.False(ProjectFiles.TryNormalizeTargetFramework(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Bump_rewrites_target_frameworks_and_keeps_everything_else()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """;

        Assert.True(ProjectFiles.TryBumpTargetFrameworks(csproj, "net10.0", out var updated, out var edits));

        Assert.Contains("<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>", updated);
        Assert.Contains("<Nullable>enable</Nullable>", updated);
        var edit = Assert.Single(edits);
        Assert.Equal("TargetFrameworks", edit.Property);
        Assert.Equal("net8.0", edit.From);
        Assert.Equal("net10.0", edit.To);
    }

    [Fact]
    public void A_project_already_on_the_target_is_left_byte_identical()
    {
        const string csproj = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

        Assert.False(ProjectFiles.TryBumpTargetFrameworks(csproj, "net10.0", out var updated, out var edits));
        Assert.Equal(csproj, updated);
        Assert.Empty(edits);
    }

    [Fact]
    public void Read_target_frameworks_sees_both_properties()
    {
        const string csproj = """
            <Project>
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """;

        Assert.Equal(new[] { "net8.0", "netstandard2.0" }, ProjectFiles.ReadTargetFrameworks(csproj));
    }

    [Fact]
    public void Sdk_band_and_preview_versions_map_to_their_tfm()
    {
        Assert.Equal("net10.0", ProjectFiles.SdkTargetFromVersion("10.0.101"));
        Assert.Equal("net10.0", ProjectFiles.SdkTargetFromVersion("10.0.100-preview.7.25380.108"));
        Assert.Null(ProjectFiles.SdkTargetFromVersion("4.8.1"));
        Assert.Null(ProjectFiles.SdkTargetFromVersion(""));
    }

    [Fact]
    public void Newest_sdk_comes_from_list_sdks_output()
    {
        const string output = """
            8.0.404 [C:\Program Files\dotnet\sdk]
            10.0.101 [C:\Program Files\dotnet\sdk]
            9.0.200 [C:\Program Files\dotnet\sdk]
            """;

        Assert.Equal("10.0.101", ProjectFiles.NewestSdkVersion(output));
    }

    [Fact]
    public void Global_json_updates_the_version_and_keeps_roll_forward()
    {
        const string json = """
            {
              "sdk": {
                "version": "8.0.404",
                "rollForward": "latestFeature"
              }
            }
            """;

        Assert.Equal("8.0.404", ProjectFiles.ReadGlobalJsonSdkVersion(json));
        Assert.True(ProjectFiles.TryUpdateGlobalJsonSdkVersion(json, "10.0.101", out var updated));
        Assert.Contains("\"version\": \"10.0.101\"", updated);
        Assert.Contains("\"rollForward\": \"latestFeature\"", updated);
    }

    [Fact]
    public void Global_json_without_a_version_is_refused()
    {
        const string json = """{ "sdk": { "rollForward": "latestMajor" } }""";

        Assert.False(ProjectFiles.TryUpdateGlobalJsonSdkVersion(json, "10.0.101", out var updated));
        Assert.Equal(json, updated);
    }

    [Fact]
    public void Package_reference_version_attribute_is_replaced_in_place()
    {
        const string csproj = """<PackageReference Include="Newtonsoft.Json" Version="12.0.1" />""";

        Assert.True(ProjectFiles.TrySetPackageVersion(csproj, "newtonsoft.json", "13.0.3", out var updated));
        Assert.Equal("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />""", updated);
    }

    [Fact]
    public void Package_reference_child_version_is_replaced()
    {
        const string csproj = """
            <ItemGroup>
              <PackageReference Include="Serilog">
                <Version>3.0.0</Version>
              </PackageReference>
            </ItemGroup>
            """;

        Assert.True(ProjectFiles.TrySetPackageVersion(csproj, "Serilog", "4.1.0", out var updated));
        Assert.Contains("<Version>4.1.0</Version>", updated);
        Assert.Contains("Include=\"Serilog\"", updated);
    }

    [Fact]
    public void Central_package_management_row_is_replaced()
    {
        const string props = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(ProjectFiles.TrySetPackageVersion(
            props, "Microsoft.Extensions.Hosting", "10.0.1", out var updated, "PackageVersion"));
        Assert.Contains("Version=\"10.0.1\"", updated);
    }

    [Fact]
    public void A_versionless_reference_is_reported_as_not_written()
    {
        const string csproj = """<PackageReference Include="FluentAssertions" />""";

        Assert.False(ProjectFiles.TrySetPackageVersion(csproj, "FluentAssertions", "7.0.0", out var updated));
        Assert.Equal(csproj, updated);
    }

    [Fact]
    public void Higher_version_compares_semantically_not_alphabetically()
    {
        Assert.Equal("10.0.0", ProjectFiles.HigherVersion("9.0.1", "10.0.0"));
        Assert.Equal("10.0.2", ProjectFiles.HigherVersion("10.0.2", "10.0.1"));
        Assert.Equal("2.0.0-beta", ProjectFiles.HigherVersion("2.0.0-beta", "1.9.9"));
    }

    [Fact]
    public void Aspire_names_are_derived_from_the_project_name()
    {
        Assert.Equal("my-app-api", ProjectFiles.ResourceName("My.App.Api"));
        Assert.Equal("Projects.My_App_Api", ProjectFiles.ProjectsMember("My.App.Api"));
        Assert.Equal("my-api", ProjectFiles.ResourceName("My API"));
    }

    [Fact]
    public void Test_and_runnable_projects_are_told_apart()
    {
        Assert.True(ProjectFiles.IsTestProject("""<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />"""));
        Assert.True(ProjectFiles.IsRunnableProject("<Project Sdk=\"Microsoft.NET.Sdk.Web\">"));
        Assert.True(ProjectFiles.IsRunnableProject("<OutputType>Exe</OutputType>"));
        Assert.False(ProjectFiles.IsRunnableProject("<Project Sdk=\"Microsoft.NET.Sdk\">"));
    }
}

/// <summary>Aspire is recognised from captured command output, not from a machine's installation.</summary>
public sealed class AspireProbeTests
{
    [Fact]
    public void Templates_are_recognised_from_dotnet_new_list()
    {
        const string output = """
            These templates matched your input: 'aspire'

            Template Name                                 Short Name            Language    Tags
            --------------------------------------------  --------------------  ----------  -------------------
            Aspire App Host                               aspire-apphost        [C#]        Aspire/Web/API
            Aspire Service Defaults                       aspire-servicedefaults  [C#]      Aspire/Web/API
            """;

        Assert.True(AspireProbe.TemplatesInstalled(output));
        Assert.False(AspireProbe.TemplatesInstalled("No templates found matching: 'aspire'."));
    }

    [Fact]
    public void Workload_is_recognised_from_dotnet_workload_list()
    {
        const string output = """
            Installed Workload Id    Manifest Version    Installation Source
            --------------------------------------------------------------------
            aspire                   8.2.2/8.0.400       SDK 8.0.400
            """;

        Assert.True(AspireProbe.WorkloadInstalled(output));
        Assert.False(AspireProbe.WorkloadInstalled("Installed Workload Id    Manifest Version"));
    }

    [Fact]
    public void Project_roles_are_recognised_from_the_project_file()
    {
        const string appHost = """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" />
            </Project>
            """;
        const string serviceDefaults = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PackageReference Include="Microsoft.Extensions.ServiceDiscovery" Version="9.0.0" />
            </Project>
            """;
        const string consumer = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PackageReference Include="Aspire.Hosting.Redis" Version="9.0.0" />
            </Project>
            """;

        Assert.True(AspireProbe.IsAppHostProject(appHost));
        Assert.True(AspireProbe.IsServiceDefaultsProject(serviceDefaults));
        Assert.True(AspireProbe.IsAspireConsumer(consumer));
        Assert.False(AspireProbe.IsAppHostProject(consumer));
    }
}

/// <summary>The two Program.cs edits scaffolding needs, over real Program.cs shapes.</summary>
public sealed class AspireWiringTests
{
    [Fact]
    public void AddProject_lines_go_before_the_build_call()
    {
        const string program = """
            var builder = DistributedApplication.CreateBuilder(args);

            builder.Build().Run();
            """;

        var (updated, added) = AspireWiring.AddProjects(program, new[]
        {
            ("Projects.My_App_Api", "my-app-api"),
            ("Projects.My_App_Web", "my-app-web"),
        });

        Assert.Equal(2, added.Count);
        Assert.Contains("builder.AddProject<Projects.My_App_Api>(\"my-app-api\");", updated);
        Assert.Contains("builder.AddProject<Projects.My_App_Web>(\"my-app-web\");", updated);
        Assert.True(
            updated.IndexOf("AddProject", StringComparison.Ordinal) <
            updated.IndexOf(".Build()", StringComparison.Ordinal));
    }

    [Fact]
    public void AddProject_wiring_is_idempotent()
    {
        const string program = """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddProject<Projects.My_App_Api>("my-app-api");

            builder.Build().Run();
            """;

        var (updated, added) = AspireWiring.AddProjects(program, new[] { ("Projects.My_App_Api", "my-app-api") });

        Assert.Empty(added);
        Assert.Equal(program, updated);
    }

    [Fact]
    public void A_program_without_a_build_call_is_left_alone()
    {
        const string program = "Console.WriteLine(\"nothing to orchestrate\");";

        var (updated, added) = AspireWiring.AddProjects(program, new[] { ("Projects.X", "x") });

        Assert.Empty(added);
        Assert.Equal(program, updated);
    }

    [Fact]
    public void ServiceDefaults_calls_are_inserted_around_the_application_lifecycle()
    {
        const string program = """
            var builder = WebApplication.CreateBuilder(args);

            var app = builder.Build();

            app.MapGet("/", () => "ok");
            app.Run();
            """;

        var (updated, changed) = AspireWiring.AddServiceDefaults(program);

        Assert.True(changed);
        Assert.Contains("builder.AddServiceDefaults();", updated);
        Assert.Contains("app.MapDefaultEndpoints();", updated);
        Assert.True(
            updated.IndexOf("AddServiceDefaults", StringComparison.Ordinal) <
            updated.IndexOf("builder.Build()", StringComparison.Ordinal));
        Assert.True(
            updated.IndexOf("MapDefaultEndpoints", StringComparison.Ordinal) <
            updated.IndexOf("app.Run()", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceDefaults_wiring_is_idempotent()
    {
        const string program = """
            var builder = WebApplication.CreateBuilder(args);
            builder.AddServiceDefaults();

            var app = builder.Build();

            app.MapDefaultEndpoints();
            app.Run();
            """;

        var (updated, changed) = AspireWiring.AddServiceDefaults(program);

        Assert.False(changed);
        Assert.Equal(program, updated);
    }

    [Fact]
    public void A_program_that_is_not_top_level_statements_is_not_edited()
    {
        const string program = """
            internal static class Program
            {
                public static void Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                }
            }
            """;

        var (updated, changed) = AspireWiring.AddServiceDefaults(program);

        Assert.False(changed);
        Assert.Equal(program, updated);
    }
}

/// <summary>The planning half of update_framework/update_solution, over a real solution on disk.</summary>
public sealed class UpgradePlanningTests
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public async Task A_dry_run_reports_the_change_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var project = workspace.PathOf("Probe.csproj");
        await File.WriteAllTextAsync(project, Csproj);

        // The dry run never reaches the build gate or the edit history; null stands in for both.
        var service = new SolutionUpgradeService(workspace, null!, null!);
        var result = await service.UpdateFrameworkAsync(
            "net10.0", null, dryRun: true, buildGate: false,
            updateGlobalJson: false, createGlobalJson: false, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, json.GetProperty("changedFiles").GetInt32());

        var file = json.GetProperty("files")[0];
        Assert.Equal("TargetFramework", file.GetProperty("kind").GetString());
        Assert.Equal("net8.0", file.GetProperty("from")[0].GetString());
        Assert.Equal("net10.0", file.GetProperty("to")[0].GetString());

        Assert.Equal(Csproj.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(project)).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Applying_without_the_build_gate_writes_the_bump()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var project = workspace.PathOf("Probe.csproj");
        await File.WriteAllTextAsync(project, Csproj);

        var service = new SolutionUpgradeService(workspace, null!, null!);
        var result = await service.UpdateFrameworkAsync(
            "net10.0", null, dryRun: false, buildGate: false,
            updateGlobalJson: false, createGlobalJson: false, timeoutSeconds: 30);

        Assert.True(ResultAssert.Json(result).GetProperty("succeeded").GetBoolean());
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task A_project_already_on_the_target_is_reported_as_nothing_to_do()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        await File.WriteAllTextAsync(workspace.PathOf("Probe.csproj"),
            Csproj.Replace("net8.0", "net10.0"));

        var service = new SolutionUpgradeService(workspace, null!, null!);
        var result = await service.UpdateFrameworkAsync(
            "net10.0", null, dryRun: true, buildGate: false,
            updateGlobalJson: false, createGlobalJson: false, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("changedFiles").GetInt32());
        Assert.Contains("already target", json.GetProperty("note").GetString() ?? "");
    }

    [Fact]
    public async Task A_project_without_a_target_framework_gets_a_note_instead_of_an_edit()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        await File.WriteAllTextAsync(workspace.PathOf("Probe.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");

        var service = new SolutionUpgradeService(workspace, null!, null!);
        var result = await service.UpdateFrameworkAsync(
            "net10.0", null, dryRun: true, buildGate: false,
            updateGlobalJson: false, createGlobalJson: false, timeoutSeconds: 30);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("changedFiles").GetInt32());
        Assert.Contains("Directory.Build.props", json.GetProperty("notes")[0].GetProperty("reason").GetString() ?? "");
    }

    [Fact]
    public async Task An_unknown_project_name_is_refused_before_anything_is_written()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        await File.WriteAllTextAsync(workspace.PathOf("Probe.csproj"), Csproj);

        var service = new SolutionUpgradeService(workspace, null!, null!);
        var result = await service.UpdateFrameworkAsync(
            "net10.0", ["NoSuchProject"], dryRun: false, buildGate: false,
            updateGlobalJson: false, createGlobalJson: false, timeoutSeconds: 30);

        Assert.Contains("Unknown project", ResultAssert.Error(result));
        Assert.Contains("<TargetFramework>net8.0</TargetFramework>", await File.ReadAllTextAsync(workspace.PathOf("Probe.csproj")));
    }
}
