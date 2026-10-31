using Snitch.Languages.Xaml;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The XAML layer beyond the markup scan: the dialect a project writes, the validation of a binding
/// path against the type behind it, the binding inspector, the element tree, and the four verified
/// edits — each asserted on what the tool returns and on what the file holds after a write.
/// </summary>
public sealed class XamlBindingTests
{
    private const string MainWindowXaml = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="clr-namespace:Probe"
                x:Class="Probe.MainWindow">
          <StackPanel>
            <TextBlock Text="{Binding Name}" />
            <TextBlock Text="{Binding Customer.City}" />
            <TextBlock Text="{Binding Missing}" />
            <TextBlock Text="{Binding Path=Customer.Nope}" />
            <TextBlock Text="{Binding ElementName=Ghost, Path=Text}" />
            <TextBlock Text="{x:Bind Name}" />
            <ListBox ItemsSource="{Binding Customers}">
              <ListBox.ItemTemplate>
                <DataTemplate DataType="{x:Type local:Customer}">
                  <TextBlock Text="{Binding City}" />
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
            <Button Content="Save" Click="OnSave" Width="120" />
          </StackPanel>
        </Window>
        """;

    private const string CodeBehind = """
        namespace Probe;

        public partial class MainWindow
        {
            private int Clicks;

            public MainWindow()
            {
                DataContext = new MainViewModel();
            }

            public void OnSave(object sender, object args)
            {
                Clicks = Clicks + 1;
                TitleText.Text = "saved";
            }
        }

        public sealed class MainViewModel
        {
            public string Name { get; set; } = "";
            public Customer Customer { get; set; } = new Customer();
            public System.Collections.Generic.List<Customer> Customers { get; set; } = new();
        }

        public sealed class Customer
        {
            public string City { get; set; } = "";
        }
        """;

    private static (FakeWorkspace Workspace, AnalyzeXamlService Analysis, XamlBindingsService Bindings,
        XamlEditService Edits, XamlTreeService Tree) NewXaml(string projectXml = "")
    {
        var workspace = FakeWorkspace.Create(("MainWindow.xaml.cs", CodeBehind));
        File.WriteAllText(workspace.PathOf("MainWindow.xaml"), MainWindowXaml);

        if (projectXml.Length > 0)
            File.WriteAllText(workspace.PathOf("Probe.csproj"), projectXml);

        return (workspace,
            new AnalyzeXamlService(workspace),
            new XamlBindingsService(workspace),
            new XamlEditService(store: null, workspace),
            new XamlTreeService(workspace));
    }

    private static string? RuleMessage(System.Text.Json.JsonElement result, string ruleId, string contains) =>
        result.GetProperty("findings").EnumerateArray()
            .Where(finding => finding.GetProperty("ruleId").GetString() == ruleId)
            .Select(finding => finding.GetProperty("message").GetString())
            .FirstOrDefault(message => message is not null && message.Contains(contains, StringComparison.Ordinal));

    [Fact]
    public void XamlProjects_DetectsTheDialectFromTheProjectFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-xaml-" + Guid.NewGuid().ToString("N"));

        try
        {
            string Detect(string name, params string[] props)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, name + ".csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                    string.Join("\n", props.Select(property => "    " + property)) +
                    "\n  </PropertyGroup>\n</Project>");

                return XamlProjects.ForDirectory(directory, name).Dialect.ToString();
            }

            Assert.Equal("Wpf", Detect("Wpf", "<UseWPF>true</UseWPF>", "<TargetFramework>net10.0-windows</TargetFramework>"));
            Assert.Equal("Maui", Detect("Maui", "<UseMaui>true</UseMaui>", "<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-windows10.0.19041.0</TargetFrameworks>"));
            Assert.Equal("WinUi", Detect("WinUi", "<UseWinUI>true</UseWinUI>"));
            Assert.Equal("Uwp", Detect("Uwp", "<TargetPlatformIdentifier>UAP</TargetPlatformIdentifier>"));
            Assert.Equal("Avalonia", Detect("Avalonia",
                "<PackageReference Include=\"Avalonia\" Version=\"11.0.0\" />"));
            Assert.Equal("Unknown", Detect("Plain", "<TargetFramework>net10.0</TargetFramework>"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        var maui = XamlProjects.ForDirectory(null, "Nothing");
        Assert.Equal("unknown", maui.DialectName);
        Assert.False(maui.SupportsCompiledBinding);
    }

    [Fact]
    public void XamlProjects_ReportsTheMauiTargetFrameworks()
    {
        var root = Path.Combine(Path.GetTempPath(), "vmcp-xaml-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <UseMaui>true</UseMaui>
                    <TargetFrameworks>net10.0-android;net10.0-ios;net10.0-windows10.0.19041.0</TargetFrameworks>
                  </PropertyGroup>
                </Project>
                """);

            var facts = XamlProjects.ForDirectory(root, "App");

            Assert.Equal(XamlDialect.Maui, facts.Dialect);
            Assert.True(facts.MultiTargeted);
            Assert.Equal(3, facts.TargetFrameworks.Count);
            Assert.Contains("android", facts.Platforms);
            Assert.Contains("ios", facts.Platforms);
            Assert.Contains("UseMaui=true", facts.Signals);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AnalyzeXaml_ValidatesEachBindingPathAgainstTheDataContext()
    {
        var (workspace, analysis, _, _, _) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, "Binding", null, "Low", 200));

        // The two good paths are silent; the two bad ones are named with the member that is missing.
        Assert.Null(RuleMessage(result, "XAML0010", "{Binding Name}"));
        Assert.Null(RuleMessage(result, "XAML0010", "'City'"));
        Assert.Contains(result.GetProperty("findings").EnumerateArray(), finding =>
            finding.GetProperty("ruleId").GetString() == "XAML0010" &&
            (finding.GetProperty("message").GetString() ?? "").Contains("'Missing'", StringComparison.Ordinal));
        Assert.Contains(result.GetProperty("findings").EnumerateArray(), finding =>
            finding.GetProperty("ruleId").GetString() == "XAML0010" &&
            (finding.GetProperty("message").GetString() ?? "").Contains("'Nope'", StringComparison.Ordinal));

        var missing = result.GetProperty("findings").EnumerateArray()
            .First(finding => (finding.GetProperty("message").GetString() ?? "").Contains("'Missing'", StringComparison.Ordinal));
        Assert.Equal("High", missing.GetProperty("severity").GetString());
        Assert.Equal("Binding", missing.GetProperty("category").GetString());
        Assert.Contains("Probe.MainViewModel", missing.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnalyzeXaml_ChecksElementNamesAndCompiledBindings()
    {
        var (workspace, analysis, _, _, _) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, null, null, "Low", 200));

        // ElementName=Ghost names no x:Name in the file.
        Assert.Contains(result.GetProperty("findings").EnumerateArray(), finding =>
            finding.GetProperty("ruleId").GetString() == "XAML0014" &&
            (finding.GetProperty("message").GetString() ?? "").Contains("Ghost", StringComparison.Ordinal));

        // x:Bind reads the page type, which declares no Name: reported, and the dialect is unknown so
        // XAML0013 (dialect support) does not fire.
        Assert.Contains(result.GetProperty("findings").EnumerateArray(), finding =>
            finding.GetProperty("ruleId").GetString() == "XAML0010" &&
            (finding.GetProperty("message").GetString() ?? "").Contains("'Name'", StringComparison.Ordinal) &&
            (finding.GetProperty("message").GetString() ?? "").Contains("x:Bind", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("ruleId").GetString() == "XAML0013");

        // The DataTemplate's own DataType is the scope of the binding inside it: City is found.
        Assert.DoesNotContain(result.GetProperty("findings").EnumerateArray(), finding =>
            (finding.GetProperty("message").GetString() ?? "").Contains("'City'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeXaml_ReportsXBIndInAWpfProject()
    {
        var (workspace, analysis, _, _, _) = NewXaml(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);
        using var _ = workspace;

        var result = ResultAssert.Json(await analysis.AnalyzeAsync("Probe", null, "Binding", "XAML0013", "Low", 200));
        var findings = result.GetProperty("findings").EnumerateArray().ToList();

        Assert.Single(findings);
        Assert.Contains("WPF", findings[0].GetProperty("message").GetString());
        Assert.Equal("High", findings[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task AnalyzeXaml_ChecksSettersAndTargetTypes()
    {
        var workspace = FakeWorkspace.Create(("Controls.cs", """
            namespace Probe;

            public class Button
            {
                public double Width { get; set; }
            }

            public sealed class OkButton : Button
            {
                public string Caption { get; set; } = "";
            }
            """));

        using var _ = workspace;

        File.WriteAllText(workspace.PathOf("Styles.xaml"),
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Probe">
              <Style x:Key="GoodStyle" TargetType="Button">
                <Setter Property="Width" Value="120" />
              </Style>
              <Style x:Key="BadStyle" TargetType="Button">
                <Setter Property="Caption" Value="x" />
                <Setter Value="y" />
              </Style>
              <Style x:Key="GhostStyle" TargetType="local:GhostButton">
                <Setter Property="Width" Value="120" />
              </Style>
              <Style x:Key="DerivedStyle" TargetType="local:OkButton">
                <Setter Property="Caption" Value="ok" />
                <Setter Property="Width" Value="10" />
              </Style>
            </ResourceDictionary>
            """);

        var result = ResultAssert.Json(await new AnalyzeXamlService(workspace).AnalyzeAsync("Probe", null, "Binding", null, "Low", 200));
        var findings = result.GetProperty("findings").EnumerateArray().ToList();

        // Caption is on OkButton, not on Button; a Setter with no Property is a required attribute.
        Assert.Contains(findings, finding => finding.GetProperty("ruleId").GetString() == "XAML0012" &&
                                             (finding.GetProperty("message").GetString() ?? "").Contains("Caption", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.GetProperty("ruleId").GetString() == "XAML0011");
        Assert.Contains(findings, finding => finding.GetProperty("ruleId").GetString() == "XAML0015" &&
                                             (finding.GetProperty("message").GetString() ?? "").Contains("GhostButton", StringComparison.Ordinal));

        // Width on Button and Caption on its derived type are both fine.
        Assert.DoesNotContain(findings, finding => (finding.GetProperty("message").GetString() ?? "").Contains("'Width'", StringComparison.Ordinal) &&
                                                   finding.GetProperty("ruleId").GetString() == "XAML0012");
    }

    [Fact]
    public async Task XamlBindings_ListsEveryBindingWithItsStatus()
    {
        var (workspace, _, bindings, _, _) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await bindings.ListAsync("Probe", null, validate: true, maxBindings: 500));

        Assert.Equal(8, result.GetProperty("bindingCount").GetInt32());
        var rows = result.GetProperty("bindings").EnumerateArray().ToList();

        var valid = rows.First(row => row.GetProperty("path").GetString() == "Customer.City");
        Assert.Equal("valid", valid.GetProperty("status").GetString());
        Assert.Equal("Probe.MainViewModel", valid.GetProperty("dataType").GetString());
        Assert.Contains("DataContext", valid.GetProperty("dataTypeSource").GetString());
        Assert.Equal(2, valid.GetProperty("steps").GetArrayLength());

        var missing = rows.First(row => row.GetProperty("path").GetString() == "Missing");
        Assert.Equal("missing-member", missing.GetProperty("status").GetString());
        Assert.Equal("Missing", missing.GetProperty("missingMember").GetString());

        var template = rows.First(row => row.GetProperty("path").GetString() == "City");
        Assert.Equal("Probe.Customer", template.GetProperty("dataType").GetString());
        Assert.Equal("valid", template.GetProperty("status").GetString());

        var elementName = rows.First(row => row.GetProperty("elementName").GetString() is null &&
                                             row.GetProperty("path").GetString() == "Text");
        Assert.Equal("not-checked", elementName.GetProperty("status").GetString());
        Assert.Contains("ElementName", elementName.GetProperty("source").GetString());

        // Missing, Customer.Nope and the x:Bind against the page type (which declares no Name).
        Assert.Equal(3, result.GetProperty("byStatus").GetProperty("missing-member").GetInt32());
    }

    [Fact]
    public async Task XamlTree_ReturnsTheElementTreeAndItsLines()
    {
        var (workspace, _, _, _, tree) = NewXaml();
        using var _ = workspace;

        var result = ResultAssert.Json(await tree.TreeAsync(workspace.PathOf("MainWindow.xaml"), "Probe", maxDepth: 12));
        ResultAssert.Succeeded(result);

        Assert.Equal("Window", result.GetProperty("rootElement").GetString());
        Assert.Equal("Probe.MainWindow", result.GetProperty("className").GetString());
        Assert.True(result.GetProperty("counts").GetProperty("elements").GetInt32() > 5);

        var root = result.GetProperty("tree");
        Assert.Equal("Window", root.GetProperty("element").GetString());
        Assert.True(root.GetProperty("children").GetArrayLength() > 0);

        var text = result.GetProperty("text").GetString() ?? "";
        Assert.Contains("StackPanel", text);
        Assert.Contains("line ", text);
    }

    [Fact]
    public async Task XamlRename_RenamesTheNameInMarkupAndTheGeneratedFieldInCSharp()
    {
        var workspace = FakeWorkspace.Create(("MainWindow.xaml.cs", CodeBehind));
        using var _ = workspace;

        File.WriteAllText(workspace.PathOf("MainWindow.xaml"),
            """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="Probe.MainWindow">
              <StackPanel>
                <TextBlock x:Name="TitleText" />
                <TextBlock Text="{Binding ElementName=TitleText, Path=Text}" />
              </StackPanel>
            </Window>
            """);

        var edits = new XamlEditService(store: null, workspace);

        var preview = ResultAssert.Json(await edits.RenameAsync(new XamlRenameRequest
        {
            Path = workspace.PathOf("MainWindow.xaml"),
            OldName = "TitleText",
            NewName = "Heading",
            ProjectName = "Probe",
        }));

        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, preview.GetProperty("filesChanged").GetInt32());
        Assert.Contains("TitleText", await File.ReadAllTextAsync(workspace.PathOf("MainWindow.xaml")));

        var applied = ResultAssert.Json(await edits.RenameAsync(new XamlRenameRequest
        {
            Path = workspace.PathOf("MainWindow.xaml"),
            OldName = "TitleText",
            NewName = "Heading",
            ProjectName = "Probe",
            DryRun = false,
        }));

        ResultAssert.Succeeded(applied);
        var xaml = await File.ReadAllTextAsync(workspace.PathOf("MainWindow.xaml"));
        Assert.Contains("x:Name=\"Heading\"", xaml);
        Assert.Contains("ElementName=Heading", xaml);
        Assert.DoesNotContain("TitleText", xaml);

        var code = await File.ReadAllTextAsync(workspace.PathOf("MainWindow.xaml.cs"));
        Assert.Contains("Heading.Text = \"saved\"", code);
        Assert.Contains("Clicks = Clicks + 1", code); // a real member, untouched
    }

    [Fact]
    public async Task XamlRename_RenamesAResourceKeyAcrossTheProject()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var _ = workspace;

        File.WriteAllText(workspace.PathOf("Styles.xaml"),
            """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="AccentBrush" Color="Red" />
            </ResourceDictionary>
            """);
        File.WriteAllText(workspace.PathOf("View.xaml"),
            """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TextBlock Foreground="{StaticResource AccentBrush}" />
            </UserControl>
            """);

        var edits = new XamlEditService(store: null, workspace);
        ResultAssert.Succeeded(await edits.RenameAsync(new XamlRenameRequest
        {
            Path = workspace.PathOf("Styles.xaml"),
            OldName = "AccentBrush",
            NewName = "BrandBrush",
            ProjectName = "Probe",
            DryRun = false,
        }));

        Assert.Contains("BrandBrush", await File.ReadAllTextAsync(workspace.PathOf("Styles.xaml")));
        Assert.Contains("BrandBrush", await File.ReadAllTextAsync(workspace.PathOf("View.xaml")));
    }

    [Fact]
    public async Task XamlExtractStyle_MovesTheAttributesIntoANewStyle()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var _ = workspace;

        var path = workspace.PathOf("View.xaml");
        await File.WriteAllTextAsync(path,
            """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Button Width="120" Height="32" Click="OnSave" />
            </UserControl>
            """);

        var edits = new XamlEditService(store: null, workspace);
        var result = ResultAssert.Json(await edits.ExtractStyleAsync(new XamlExtractStyleRequest
        {
            Path = path,
            Line = 3,
            Key = "PrimaryButton",
            DryRun = false,
        }));

        ResultAssert.Succeeded(result);
        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("<UserControl.Resources>", text);
        Assert.Contains("<Style x:Key=\"PrimaryButton\" TargetType=\"Button\">", text);
        Assert.Contains("<Setter Property=\"Width\" Value=\"120\" />", text);
        Assert.Contains("<Setter Property=\"Height\" Value=\"32\" />", text);
        Assert.Contains("Style=\"{StaticResource PrimaryButton}\"", text);
        Assert.Contains("Click=\"OnSave\"", text);
        Assert.DoesNotContain("Setter Property=\"Click\"", text);

        var xml = System.Xml.Linq.XDocument.Parse(text);
        Assert.Equal(2, xml.Descendants().Count(element => element.Name.LocalName == "Setter"));
    }

    [Fact]
    public async Task XamlExtractStyle_RefusesAKeyThatAlreadyExists()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var _ = workspace;

        var path = workspace.PathOf("View.xaml");
        await File.WriteAllTextAsync(path,
            """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <UserControl.Resources>
                <Style x:Key="Taken" TargetType="Button" />
              </UserControl.Resources>
              <Button Width="10" />
            </UserControl>
            """);

        var edits = new XamlEditService(store: null, workspace);
        var error = ResultAssert.Error(await edits.ExtractStyleAsync(new XamlExtractStyleRequest
        {
            Path = path, Line = 6, Key = "Taken", DryRun = false,
        }));

        Assert.Contains("already exists", error);
    }

    [Fact]
    public async Task XamlPromoteResource_CreatesTheWpfScalarAndReferencesIt()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var _ = workspace;

        await File.WriteAllTextAsync(workspace.PathOf("Probe.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <UseWPF>true</UseWPF>
              </PropertyGroup>
            </Project>
            """);

        var path = workspace.PathOf("View.xaml");
        await File.WriteAllTextAsync(path,
            """
            <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TextBlock FontSize="16" />
            </UserControl>
            """);

        var edits = new XamlEditService(store: null, workspace);
        var result = ResultAssert.Json(await edits.PromoteResourceAsync(new XamlPromoteRequest
        {
            Path = path, Line = 3, Attribute = "FontSize", Key = "BodyFont", DryRun = false,
        }));

        ResultAssert.Succeeded(result);
        Assert.Equal("double", result.GetProperty("details").GetProperty("kind").GetString());

        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("xmlns:sys=", text);
        Assert.Contains("<sys:Double x:Key=\"BodyFont\">16</sys:Double>", text);
        Assert.Contains("FontSize=\"{StaticResource BodyFont}\"", text);
        System.Xml.Linq.XDocument.Parse(text);
    }

    [Fact]
    public async Task FormatXaml_ReindentsWithoutChangingTheDocument()
    {
        var workspace = FakeWorkspace.Create(("C.cs", "namespace Probe; internal class C { }"));
        using var _ = workspace;

        var path = workspace.PathOf("View.xaml");
        await File.WriteAllTextAsync(path,
            "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n" +
            "   xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n" +
            "      <StackPanel>\n" +
            "  <TextBlock Text=\"Hi\" />\n" +
            "      </StackPanel>\n" +
            "</UserControl>\n");

        var edits = new XamlEditService(store: null, workspace);
        var result = ResultAssert.Json(await edits.FormatAsync(new XamlFormatRequest
        {
            Path = path, Indent = "2", DryRun = false,
        }));

        ResultAssert.Succeeded(result);
        var lines = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").Split('\n');

        Assert.StartsWith("<UserControl", lines[0]);
        Assert.StartsWith("             xmlns:x=", lines[1]);
        Assert.Equal("  <StackPanel>", lines[2]);
        Assert.Equal("    <TextBlock Text=\"Hi\" />", lines[3]);
        Assert.Equal("  </StackPanel>", lines[4]);
        Assert.Equal("</UserControl>", lines[5]);
    }

    [Fact]
    public void XamlMarkup_ParsesPathsSourcesAndSegments()
    {
        Assert.True(XamlMarkup.TryParse("{Binding Path=Customer.City, Mode=TwoWay}", out var binding));
        Assert.Equal("Binding", binding.Type);
        Assert.Equal("Customer.City", XamlMarkup.PathOf(binding));
        Assert.Equal("TwoWay", binding.Get("Mode"));
        Assert.False(binding.Get("ElementName") is not null);

        Assert.True(XamlMarkup.TryParse("{Binding Name}", out var positional));
        Assert.Equal("Name", XamlMarkup.PathOf(positional));

        Assert.True(XamlMarkup.TryParse("{Binding RelativeSource={RelativeSource Self}, Path=Title}", out var relative));
        Assert.Equal("{RelativeSource Self}", relative.Get("RelativeSource"));
        Assert.Equal("Title", XamlMarkup.PathOf(relative));

        Assert.True(XamlMarkup.TryParse("{x:Bind vm.Items[0].Name, Mode=OneWay}", out var compiled));
        Assert.Equal("x:Bind", compiled.Type);

        var segments = XamlMarkup.Segments("Items[0].Owner.Name");
        Assert.Equal(3, segments.Count);
        Assert.False(segments[0].Checkable);
        Assert.True(segments[2].Checkable);

        Assert.True(XamlMarkup.TryParse("{StaticResource {x:Type Button}}", out var nested));
        Assert.Equal("{x:Type Button}", nested.Positional[0]);

        Assert.False(XamlMarkup.TryParse("Plain text", out _));
        Assert.False(XamlMarkup.TryParse("{Binding Broken", out _));
    }

    [Fact]
    public void XamlTextIndex_FindsTagsAndAttributesPastComments()
    {
        var text = """
            <!-- <Button Width="1" /> -->
            <Grid>
              <Button Width="120" Height="32" />
            </Grid>
            """;

        var tags = XamlTextIndex.Scan(text);
        Assert.Equal(3, tags.Count);
        Assert.Equal("Button", tags[1].Name);
        Assert.True(tags[1].IsSelfClosing);

        var attributes = XamlTextIndex.Attributes(text, tags[1]);
        Assert.Equal(2, attributes.Count);
        Assert.Equal("Width", attributes[0].Name);
        Assert.Equal("120", attributes[0].Value(text));

        var after = text[..tags[1].AttributeInsertionOffset] + " Click=\"OnSave\"" + text[tags[1].AttributeInsertionOffset..];
        Assert.Contains("Click=\"OnSave\"", after);
        Assert.Contains("Click=\"OnSave\"", XamlTextIndex.Signature(after));
        Assert.DoesNotContain("Click=\"OnSave\"", XamlTextIndex.Signature(text));
    }
}
