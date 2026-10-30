using Snitch.Execution;
using Snitch.Languages.Xaml;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The XAML layer against real files in the fake workspace: the scanner and every rule, the
/// navigation actions, and the EF migrations command surface (validation only — a live
/// <c>dotnet ef</c> run needs a database project).
/// </summary>
public sealed class XamlTests
{
    private const string MainWindowXaml = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="Probe.MainWindow">
          <Window.Resources>
            <SolidColorBrush x:Key="AccentBrush" Color="Red" />
            <SolidColorBrush x:Key="AccentBrush" Color="Blue" />
          </Window.Resources>
          <StackPanel>
            <TextBlock x:Name="TitleText" Text="{StaticResource AccentBrush}" />
            <TextBlock x:Name="TitleText" Text="{StaticResource MissingBrush}" />
            <TextBlock x:Name="Clicks" />
            <Button Content="Save" Click="OnSave" />
            <Button Content="Delete" Click="OnMissing" />
            <ListBox>
              <ListBox.ItemTemplate>
                <DataTemplate>
                  <TextBlock Text="{Binding Name}" />
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
          </StackPanel>
        </Window>
        """;

    private const string CodeBehind = """
        namespace Probe;

        public partial class MainWindow
        {
            private int Clicks;

            public void OnSave(object sender, object args) { }
        }
        """;

    private const string GhostXaml = """
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="Probe.Ghost">
        </UserControl>
        """;

    private const string StylesXaml = """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="BaseStyle" TargetType="Button" />
          <Style x:Key="PrimaryStyle" TargetType="Button" BasedOn="{StaticResource BaseStyle}" />
        </ResourceDictionary>
        """;

    private static (FakeWorkspace Workspace, XamlPagesService Pages, AnalyzeXamlService Analysis, XamlNavigationService Navigation) NewXaml()
    {
        var workspace = FakeWorkspace.Create(("MainWindow.cs", CodeBehind));
        File.WriteAllText(workspace.PathOf("MainWindow.xaml"), MainWindowXaml);
        File.WriteAllText(workspace.PathOf("Ghost.xaml"), GhostXaml);
        File.WriteAllText(workspace.PathOf("Styles.xaml"), StylesXaml);

        return (workspace,
            new XamlPagesService(workspace),
            new AnalyzeXamlService(workspace),
            new XamlNavigationService(workspace));
    }

    [Fact]
    public async Task XamlPages_ClassifiesTheFilesAndResolvesTheCodeBehind()
    {
        var (workspace, pages, _, _) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await pages.ListAsync("Probe", null, 50));
        Assert.Equal(3, result.GetProperty("fileCount").GetInt32());

        var window = result.GetProperty("pages").EnumerateArray()
            .First(page => page.GetProperty("relativePath").GetString() == "MainWindow.xaml");
        Assert.Equal("window", window.GetProperty("kind").GetString());
        Assert.Equal("Probe.MainWindow", window.GetProperty("className").GetString());
        Assert.Equal("Probe.MainWindow", window.GetProperty("codeBehind").GetProperty("fullName").GetString());

        var dictionary = result.GetProperty("pages").EnumerateArray()
            .First(page => page.GetProperty("relativePath").GetString() == "Styles.xaml");
        Assert.Equal("resourceDictionary", dictionary.GetProperty("kind").GetString());
        Assert.Equal(2, dictionary.GetProperty("counts").GetProperty("styles").GetInt32());
    }

    [Fact]
    public async Task AnalyzeXaml_ReportsEveryRuleWithItsLine()
    {
        var (workspace, _, analysis, _) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, null, null, "Low", 200));
        var findings = result.GetProperty("findings").EnumerateArray().ToList();
        var rules = findings.Select(f => f.GetProperty("ruleId").GetString()).ToList();

        Assert.Contains("XAML0001", rules); // duplicate x:Name
        Assert.Contains("XAML0003", rules); // Ghost does not resolve
        Assert.Contains("XAML0004", rules); // Click="OnMissing"
        Assert.Contains("XAML0005", rules); // MissingBrush
        Assert.Contains("XAML0006", rules); // DataTemplate without DataType
        Assert.Contains("XAML0007", rules); // x:Name collides with the declared field Clicks
        Assert.Contains("XAML0009", rules); // duplicate x:Key

        // Click="OnSave" resolves: no finding names it.
        Assert.DoesNotContain(findings, finding =>
            (finding.GetProperty("message").GetString() ?? string.Empty).Contains("OnSave"));

        var missingHandler = findings.First(finding => finding.GetProperty("ruleId").GetString() == "XAML0004");
        Assert.True(missingHandler.GetProperty("line").GetInt32() > 0);
        Assert.Equal("Markup", missingHandler.GetProperty("category").GetString());
        Assert.Equal("High", missingHandler.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AnalyzeXaml_FiltersByRuleAndSeverity()
    {
        var (workspace, _, analysis, _) = NewXaml();
        using var _ = workspace;

        var onlyTemplates = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, null, "XAML0006", "Low", 200));
        var rules = onlyTemplates.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("ruleId").GetString()).Distinct().ToList();
        Assert.Equal(["XAML0006"], rules);

        var highOnly = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, null, null, "High", 200));
        Assert.All(highOnly.GetProperty("findings").EnumerateArray(),
            finding => Assert.Equal("High", finding.GetProperty("severity").GetString()));
    }

    [Fact]
    public async Task XamlNavigate_FindsNamesResourcesStylesAndHandlers()
    {
        var (workspace, _, _, navigation) = NewXaml();
        using var _ = workspace;

        var name = ResultAssert.Json(await navigation.NavigateAsync("name", "TitleText", "Probe"));
        Assert.Equal(2, name.GetProperty("definitionCount").GetInt32());
        Assert.Equal("Probe.MainWindow", name.GetProperty("codeBehind")[0].GetProperty("type").GetString());

        var resource = ResultAssert.Json(await navigation.NavigateAsync("resource", "MissingBrush", "Probe"));
        Assert.Equal(0, resource.GetProperty("definitionCount").GetInt32());
        Assert.Equal(1, resource.GetProperty("useCount").GetInt32());

        var style = ResultAssert.Json(await navigation.NavigateAsync("style", "PrimaryStyle", "Probe"));
        var chain = style.GetProperty("basedOnChain");
        Assert.Equal("PrimaryStyle", chain[0].GetProperty("key").GetString());
        Assert.Equal("BaseStyle", chain[0].GetProperty("basedOn").GetString());

        var derived = ResultAssert.Json(await navigation.NavigateAsync("style", "BaseStyle", "Probe"));
        Assert.Equal("PrimaryStyle", derived.GetProperty("derivedStyles")[0].GetProperty("key").GetString());

        var handler = ResultAssert.Json(await navigation.NavigateAsync("handler", "OnMissing", "Probe"));
        Assert.Equal(1, handler.GetProperty("referenceCount").GetInt32());
        Assert.False(handler.GetProperty("codeBehind")[0].GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task EfMigrations_ValidatesTheActionAndTheProject()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        using var _ = workspace;
        var ef = new EfMigrationsService(workspace);

        Assert.Contains("Unknown action", ResultAssert.Error(await ef.RunAsync("Probe", "drop")));
        Assert.Contains("needs a migration name", ResultAssert.Error(await ef.RunAsync("Probe", "add")));
        Assert.Contains("not found", ResultAssert.Error(await ef.RunAsync("Nope", "list")));
    }
}
