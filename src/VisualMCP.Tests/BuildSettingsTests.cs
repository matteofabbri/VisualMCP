using Snitch.Execution;
using Snitch.Languages.CSharp.Packages;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The text editor behind project_properties, exercised on the shapes a project file really has:
/// a value already written, a PropertyGroup to extend, no PropertyGroup at all, and a property that
/// only exists under a Condition (which must be refused, not guessed).
/// </summary>
public sealed class MsBuildPropertyFileTests
{
    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void An_existing_value_is_replaced_in_place()
    {
        Assert.True(MsBuildPropertyFile.TrySet(SdkProject, "Nullable", "disable", out var updated, out var previous, out var note));

        Assert.Null(note);
        Assert.Equal("enable", previous);
        Assert.Contains("<Nullable>disable</Nullable>", updated);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", updated);
    }

    [Fact]
    public void A_new_property_goes_into_the_first_unconditioned_group()
    {
        Assert.True(MsBuildPropertyFile.TrySet(SdkProject, "LangVersion", "latest", out var updated, out var previous, out _));

        Assert.Null(previous);
        Assert.Contains("    <LangVersion>latest</LangVersion>", updated);
        Assert.Contains("    <TargetFramework>net10.0</TargetFramework>", updated);
        Assert.Contains("  </PropertyGroup>", updated);
        Assert.DoesNotContain("PropertyGroup Condition", updated);
    }

    [Fact]
    public void A_file_without_a_property_group_gets_one()
    {
        const string bare = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";

        Assert.True(MsBuildPropertyFile.TrySet(bare, "Nullable", "enable", out var updated, out _, out _));

        Assert.Contains("<PropertyGroup>", updated);
        Assert.Contains("<Nullable>enable</Nullable>", updated);
        Assert.True(updated.IndexOf("<PropertyGroup>", StringComparison.Ordinal) < updated.IndexOf("</Project>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_property_defined_only_under_a_condition_is_refused_with_a_reason()
    {
        const string conditional = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <DefineConstants>DEBUG</DefineConstants>
              </PropertyGroup>
            </Project>
            """;

        Assert.False(MsBuildPropertyFile.TrySet(conditional, "DefineConstants", "TRACE", out var updated, out _, out var note));

        Assert.Equal(conditional, updated);
        Assert.Contains("Condition", note);
    }

    [Fact]
    public void Removing_takes_the_whole_line_and_leaves_the_rest()
    {
        Assert.True(MsBuildPropertyFile.TryRemove(SdkProject, "Nullable", out var updated, out var removed, out var note));

        Assert.Null(note);
        Assert.Equal("enable", removed);
        Assert.DoesNotContain("Nullable", updated);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", updated);
    }

    [Fact]
    public void Removing_a_property_that_is_not_written_reports_it()
    {
        Assert.False(MsBuildPropertyFile.TryRemove(SdkProject, "LangVersion", out var updated, out _, out var note));

        Assert.Equal(SdkProject, updated);
        Assert.Contains("not written", note);
    }

    [Fact]
    public void A_value_is_xml_escaped_and_a_bad_name_is_refused()
    {
        Assert.True(MsBuildPropertyFile.TrySet(SdkProject, "DefineConstants", "A&B<C>", out var updated, out _, out _));
        Assert.Contains("<DefineConstants>A&amp;B&lt;C&gt;</DefineConstants>", updated);

        Assert.False(MsBuildPropertyFile.TrySet(SdkProject, "Bad Name", "x", out _, out _, out var note));
        Assert.Contains("not a valid MSBuild property name", note);
    }
}

/// <summary>The parsed shape of 'dotnet nuget list source --format detailed'.</summary>
public sealed class NuGetSourceParsingTests
{
    private const string Output = """
        Registered Sources:
          1.  nuget.org [Enabled]
              https://api.nuget.org/v3/index.json
          2.  internal [Disabled]
              https://nuget.internal.example/v3/index.json
        """;

    [Fact]
    public void Both_sources_are_parsed_with_their_state_and_url()
    {
        var sources = NuGetSourcesService.ParseSources(Output);
        Assert.Equal(2, sources.Count);

        var json = ResultAssert.Json(sources);
        Assert.Equal("nuget.org", json[0].GetProperty("name").GetString());
        Assert.True(json[0].GetProperty("enabled").GetBoolean());
        Assert.Equal("https://api.nuget.org/v3/index.json", json[0].GetProperty("url").GetString());

        Assert.Equal("internal", json[1].GetProperty("name").GetString());
        Assert.False(json[1].GetProperty("enabled").GetBoolean());
        Assert.Equal("https://nuget.internal.example/v3/index.json", json[1].GetProperty("url").GetString());
    }

    [Fact]
    public async Task The_CLI_reads_a_config_file_it_is_given()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var config = Path.Combine(workspace.RootDirectory, "NuGet.Config");
        await File.WriteAllTextAsync(config, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-folder" value="D:\packages" />
              </packageSources>
            </configuration>
            """);

        var service = new NuGetSourcesService();
        var result = await service.ListAsync(config, 60);

        ResultAssert.Succeeded(result);
        var json = ResultAssert.Json(result);
        Assert.Contains(json.GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("name").GetString() == "local-folder");
    }
}

/// <summary>Saved run configurations, in the solution store, without launching anything.</summary>
public sealed class RunConfigurationTests
{
    [Fact]
    public async Task Save_writes_the_file_and_resolve_returns_the_values()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new RunConfigurationService(workspace);

        var saved = await service.SaveAsync("api-dev", "Probe", "--urls http://localhost:5000", "Release", noBuild: true,
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" });
        ResultAssert.Succeeded(saved);

        var path = Path.Combine(workspace.RootDirectory, ".visualMCP", "run-configs.json");
        Assert.True(File.Exists(path));
        Assert.Contains("\"api-dev\"", await File.ReadAllTextAsync(path));

        var (configuration, error) = await service.ResolveAsync("api-dev");
        Assert.Null(error);
        Assert.NotNull(configuration);
        Assert.Equal("Probe", configuration!.Project);
        Assert.Equal("--urls http://localhost:5000", configuration.Args);
        Assert.Equal("Release", configuration.Configuration);
        Assert.True(configuration.NoBuild);
        Assert.Equal("Development", configuration.Environment["ASPNETCORE_ENVIRONMENT"]);
    }

    [Fact]
    public async Task Save_replaces_an_existing_configuration_with_the_same_name()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new RunConfigurationService(workspace);

        await service.SaveAsync("api", "Probe", "--first", null, false, null);
        var second = await service.SaveAsync("api", "Probe", "--second", null, false, null);

        Assert.True(ResultAssert.Json(second).GetProperty("replaced").GetBoolean());
        var (configuration, _) = await service.ResolveAsync("api");
        Assert.Equal("--second", configuration!.Args);
    }

    [Fact]
    public async Task An_unknown_project_is_refused_before_the_file_is_written()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new RunConfigurationService(workspace);

        var result = await service.SaveAsync("x", "NoSuchProject", null, null, false, null);

        Assert.Contains("not found", ResultAssert.Error(result));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, ".visualMCP", "run-configs.json")));
    }

    [Fact]
    public async Task Delete_removes_only_the_named_configuration()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new RunConfigurationService(workspace);
        await service.SaveAsync("one", "Probe", null, null, false, null);
        await service.SaveAsync("two", "Probe", null, null, false, null);

        var deleted = await service.DeleteAsync("one");
        ResultAssert.Succeeded(deleted);

        var json = ResultAssert.Json(await service.ListAsync());
        Assert.Single(json.GetProperty("configs").EnumerateArray());
        Assert.Equal("two", json.GetProperty("configs")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_missing_configuration_is_reported_with_the_known_names()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new RunConfigurationService(workspace);

        var (configuration, error) = await service.ResolveAsync("nope");

        Assert.Null(configuration);
        Assert.True(ResultAssert.Json(error!).TryGetProperty("knownConfigs", out _));
    }
}

/// <summary>launchSettings.json edited as a document, not as text.</summary>
public sealed class LaunchSettingsTests
{
    [Fact]
    public async Task Set_creates_the_file_with_the_schema_and_the_profile()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new LaunchSettingsService(workspace);

        var result = await service.SetAsync(null, workspace.RootDirectory, "Api", "Project",
            "https://localhost:7001", "--seed", new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
            replaceEnvironmentVariables: false, dryRun: false);

        ResultAssert.Succeeded(result);

        var path = Path.Combine(workspace.RootDirectory, "Properties", "launchSettings.json");
        Assert.True(File.Exists(path));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("https://json.schemastore.org/launchsettings.json", text);
        Assert.Contains("\"commandName\": \"Project\"", text);
        Assert.Contains("\"applicationUrl\": \"https://localhost:7001\"", text);
        Assert.Contains("\"ASPNETCORE_ENVIRONMENT\": \"Development\"", text);
    }

    [Fact]
    public async Task A_second_set_merges_environment_variables_and_keeps_other_profiles()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new LaunchSettingsService(workspace);
        await service.SetAsync(null, workspace.RootDirectory, "Api", null, "http://localhost:5000", null, null, false, false);
        await service.SetAsync(null, workspace.RootDirectory, "Worker", null, null, null, null, false, false);
        await service.SetAsync(null, workspace.RootDirectory, "Api", null, null, null,
            new Dictionary<string, string> { ["A"] = "1" }, false, false);
        var result = await service.SetAsync(null, workspace.RootDirectory, "Api", null, "http://localhost:5001", null,
            new Dictionary<string, string> { ["B"] = "2" }, false, false);

        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(await service.ListAsync(null, workspace.RootDirectory, null));
        var profiles = json.GetProperty("profiles").EnumerateArray().ToList();
        Assert.Equal(2, profiles.Count);

        var api = profiles.Single(profile => profile.GetProperty("name").GetString() == "Api");
        Assert.Equal("http://localhost:5001", api.GetProperty("applicationUrl").GetString());
        var environment = api.GetProperty("environmentVariables");
        Assert.Equal("1", environment.GetProperty("A").GetString());
        Assert.Equal("2", environment.GetProperty("B").GetString());
    }

    [Fact]
    public async Task Removing_a_profile_needs_confirm_and_then_removes_it()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new LaunchSettingsService(workspace);
        await service.SetAsync(null, workspace.RootDirectory, "Api", null, null, null, null, false, false);

        var refused = await service.RemoveAsync(null, workspace.RootDirectory, "Api", confirm: false, dryRun: false);
        Assert.True(ResultAssert.Json(refused).GetProperty("confirmRequired").GetBoolean());

        var removed = await service.RemoveAsync(null, workspace.RootDirectory, "Api", confirm: true, dryRun: false);
        ResultAssert.Succeeded(removed);
        Assert.Empty(ResultAssert.Json(await service.ListAsync(null, workspace.RootDirectory, null))
            .GetProperty("profiles").EnumerateArray());
    }

    [Fact]
    public async Task A_dry_run_returns_the_preview_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new LaunchSettingsService(workspace);

        var result = await service.SetAsync(null, workspace.RootDirectory, "Api", null, null, null, null, false, dryRun: true);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Contains("Api", json.GetProperty("preview").GetString());
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "Properties", "launchSettings.json")));
    }
}

/// <summary>Publish profiles authored as the MSBuild files they are.</summary>
public sealed class PublishProfileTests
{
    [Fact]
    public async Task Set_creates_the_profile_and_list_reads_the_properties()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new PublishProfilesService(workspace);

        var result = await service.SetAsync(null, workspace.RootDirectory, "Folder",
            new Dictionary<string, string>
            {
                ["PublishDir"] = "bin/publish",
                ["RuntimeIdentifier"] = "win-x64",
                ["SelfContained"] = "true",
            },
            createIfMissing: true, dryRun: false);

        ResultAssert.Succeeded(result);

        var path = Path.Combine(workspace.RootDirectory, "Properties", "PublishProfiles", "Folder.pubxml");
        Assert.True(File.Exists(path));

        var json = ResultAssert.Json(await service.ListAsync(null, workspace.RootDirectory, "Folder"));
        var properties = json.GetProperty("profile").GetProperty("properties");
        Assert.Equal("bin/publish", properties.GetProperty("PublishDir").GetString());
        Assert.Equal("win-x64", properties.GetProperty("RuntimeIdentifier").GetString());
        Assert.Equal("true", properties.GetProperty("SelfContained").GetString());
        Assert.Equal("FileSystem", properties.GetProperty("WebPublishMethod").GetString());
    }

    [Fact]
    public async Task A_second_set_updates_the_value_in_place()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new PublishProfilesService(workspace);
        await service.SetAsync(null, workspace.RootDirectory, "Folder",
            new Dictionary<string, string> { ["PublishDir"] = "old" }, true, false);

        var result = await service.SetAsync(null, workspace.RootDirectory, "Folder",
            new Dictionary<string, string> { ["PublishDir"] = "new" }, true, false);

        Assert.True(ResultAssert.Json(result).GetProperty("createdFile").GetBoolean() is false);
        var json = ResultAssert.Json(await service.ListAsync(null, workspace.RootDirectory, "Folder"));
        Assert.Equal("new", json.GetProperty("profile").GetProperty("properties").GetProperty("PublishDir").GetString());
    }

    [Fact]
    public async Task Creating_an_existing_profile_is_refused_when_creation_is_off()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new PublishProfilesService(workspace);

        var result = await service.SetAsync(null, workspace.RootDirectory, "Missing",
            new Dictionary<string, string> { ["PublishDir"] = "x" }, createIfMissing: false, dryRun: false);

        Assert.Contains("does not exist", ResultAssert.Error(result));
    }

    [Fact]
    public async Task Delete_needs_confirm_and_then_removes_the_file()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new PublishProfilesService(workspace);
        await service.SetAsync(null, workspace.RootDirectory, "Folder",
            new Dictionary<string, string> { ["PublishDir"] = "x" }, true, false);

        var refused = await service.DeleteAsync(null, workspace.RootDirectory, "Folder", confirm: false);
        Assert.True(ResultAssert.Json(refused).GetProperty("confirmRequired").GetBoolean());

        ResultAssert.Succeeded(await service.DeleteAsync(null, workspace.RootDirectory, "Folder", confirm: true));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "Properties", "PublishProfiles", "Folder.pubxml")));
    }
}

/// <summary>
/// project_properties: the evaluated view comes from MSBuild itself, and the write side is a real
/// edit of the project file. The build gate is off in these tests (there is nothing to build).
/// </summary>
public sealed class ProjectPropertiesTests
{
    [Fact]
    public async Task Get_returns_the_value_MSBuild_evaluates()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);

        var result = await service.GetAsync("Probe", null, ["TargetFramework", "Nullable"], null, null, null, 120);

        ResultAssert.Succeeded(result);
        var properties = ResultAssert.Json(result).GetProperty("properties");
        Assert.Equal("net10.0", properties.GetProperty("TargetFramework").GetString());
    }

    [Fact]
    public async Task Get_reports_an_unknown_project_name()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);

        var result = await service.GetAsync("NoSuchProject", null, null, null, null, null, 120);

        Assert.Contains("not found", ResultAssert.Error(result));
    }

    [Fact]
    public async Task A_dry_run_reports_the_edit_and_writes_nothing()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);
        var before = await File.ReadAllTextAsync(workspace.PathOf("Probe.csproj"));

        var result = await service.SetAsync("Probe", null, "LangVersion", "latest", false, "project", dryRun: true, buildGate: false, 120);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("latest", json.GetProperty("to").GetString());
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.PathOf("Probe.csproj")));
    }

    [Fact]
    public async Task Set_and_unset_change_the_project_file()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);

        ResultAssert.Succeeded(await service.SetAsync("Probe", null, "Nullable", "enable", false, "project", false, false, 120));
        Assert.Contains("<Nullable>enable</Nullable>", await File.ReadAllTextAsync(workspace.PathOf("Probe.csproj")));

        ResultAssert.Succeeded(await service.SetAsync("Probe", null, "Nullable", null, true, "project", false, false, 120));
        Assert.DoesNotContain("Nullable", await File.ReadAllTextAsync(workspace.PathOf("Probe.csproj")));
    }

    [Fact]
    public async Task Setting_the_same_value_reports_nothing_to_change()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);
        await service.SetAsync("Probe", null, "Nullable", "enable", false, "project", false, false, 120);

        var result = await service.SetAsync("Probe", null, "Nullable", "enable", false, "project", false, false, 120);

        Assert.False(ResultAssert.Json(result).GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Scope_props_without_a_Directory_Build_props_is_refused()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { }"));
        var service = new ProjectPropertiesService(workspace, null!);

        var result = await service.SetAsync("Probe", null, "Nullable", "enable", false, "props", false, false, 120);

        Assert.Contains("Directory.Build.props", ResultAssert.Error(result));
    }
}

/// <summary>
/// The binlog reader end to end: a real build writes a real log, and the analysis finds the compiler
/// error in it with the project that produced it.
/// </summary>
public sealed class BinlogAnalysisTests
{
    [Fact]
    public async Task A_failing_build_is_read_back_with_its_error_and_its_targets()
    {
        using var workspace = FakeWorkspace.Create(("Program.cs", "internal class C { int X = \"not an int\"; }"));
        var service = new BinlogAnalysisService(workspace);

        var result = await service.AnalyzeAsync(null, "Probe", "Debug", 10, 180);

        if (ResultAssert.Json(result).TryGetProperty("error", out var error))
            Assert.Fail("binlog analysis failed: " + error.GetString());

        var json = ResultAssert.Json(result);
        var summary = json.GetProperty("summary");
        Assert.False(summary.GetProperty("succeeded").GetBoolean());
        Assert.True(summary.GetProperty("errors").GetInt32() > 0);
        Assert.True(json.GetProperty("errors").GetArrayLength() > 0);

        var firstError = json.GetProperty("errors")[0];
        Assert.Equal("Probe", firstError.GetProperty("project").GetString());
        Assert.StartsWith("CS", firstError.GetProperty("code").GetString() ?? "");

        Assert.True(json.GetProperty("slowestTargets").GetArrayLength() > 0);
        Assert.Contains(json.GetProperty("graph").GetProperty("nodes").EnumerateArray(),
            node => node.GetString() == "Probe");
    }
}
