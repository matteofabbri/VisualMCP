using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.Extensions.DependencyInjection;
using Snitch.Languages.Style;
using Snitch.Languages.VB;
using Snitch.Languages.VB.Style;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Visual Basic style engine over in-memory VB projects: which shapes it rewrites, which it must
/// leave alone, and that a VB service never touches a C# document. Every supported id has both a case
/// that changes and a case that must not, and a coverage test keeps the two tables in step with the
/// catalogue.
/// </summary>
public sealed class VbStyleTests
{
    /// <summary>
    /// One case per supported id: the VB source, the id to run, and the text the rewrite must produce.
    /// Each source is a complete file that compiles on its own.
    /// </summary>
    public static IEnumerable<object[]> RuleCases()
    {
        // IDE0001 — a qualified name the file's Imports already make short.
        yield return Case("IDE0001", """
            Imports System.Collections.Generic

            Friend Class C
                Public Function F() As System.Collections.Generic.List(Of Integer)
                    Return New List(Of Integer)()
                End Function
            End Class
            """, "As List(Of Integer)");

        // IDE0002 — Type.SharedMember where the member is reachable unqualified.
        yield return Case("IDE0002", """
            Friend Class C
                Public Shared Value As Integer = 1

                Public Function F() As Integer
                    Return C.Value
                End Function
            End Class
            """, "Return Value");

        // IDE0003 — Me.Member with nothing shadowing it.
        yield return Case("IDE0003", """
            Friend Class C
                Private _x As Integer = 1

                Public Function F() As Integer
                    Return Me._x
                End Function
            End Class
            """, "Return _x");

        // IDE0004 — a cast to the operand's own type.
        yield return Case("IDE0004", """
            Friend Class C
                Public Function F(x As Integer) As Integer
                    Return CType(x, Integer)
                End Function
            End Class
            """, "Return x");

        // IDE0049 — a framework type name to the language keyword.
        yield return Case("IDE0049", """
            Friend Class C
                Public Function F() As System.Int32
                    Return 1
                End Function
            End Class
            """, "As Integer");

        // IDE0041 — a reference test against Nothing becomes Is Nothing.
        yield return Case("IDE0041", """
            Friend Class C
                Public Function F(o As Object) As Boolean
                    Return o = Nothing
                End Function
            End Class
            """, "o Is Nothing");

        // IDE0047 — parentheses around a primary expression.
        yield return Case("IDE0047", """
            Friend Class C
                Public Function F(s As String) As Integer
                    Return (s).Length
                End Function
            End Class
            """, "Return s.Length");

        // IDE0054 — an assignment that is a compound assignment.
        yield return Case("IDE0054", """
            Friend Class C
                Public Sub F()
                    Dim x As Integer = 1
                    x = x + 1
                End Sub
            End Class
            """, "x += 1");

        // IDE0100 — a comparison with a boolean literal.
        yield return Case("IDE0100", """
            Friend Class C
                Public Function F(b As Boolean) As Boolean
                    Return b = True
                End Function
            End Class
            """, "Return b");

        // IDE0075 — a conditional that returns boolean literals.
        yield return Case("IDE0075", """
            Friend Class C
                Public Function F(b As Boolean) As Boolean
                    Return If(b, True, False)
                End Function
            End Class
            """, "Return b");

        // IDE0029 — the conditional null check is VB's binary If coalesce.
        yield return Case("IDE0029", """
            Friend Class C
                Public Function F(s As String) As String
                    Return If(s IsNot Nothing, s, "x")
                End Function
            End Class
            """, "Return If(s, \"x\")");

        // IDE0030 — the same for a nullable value type, through HasValue.
        yield return Case("IDE0030", """
            Friend Class C
                Public Function F(x As Integer?) As Integer
                    Return If(x.HasValue, x, 0)
                End Function
            End Class
            """, "Return If(x, 0)");

        // IDE0031 — a null test whose both branches are an access and Nothing.
        yield return Case("IDE0031", """
            Friend Class C
                Public Function F(s As String) As String
                    Return If(s IsNot Nothing, s.Trim(), Nothing)
                End Function
            End Class
            """, "s?.Trim()");

        // IDE0081 — ByVal is the default passing convention.
        yield return Case("IDE0081", """
            Friend Class C
                Public Sub F(ByVal value As Integer)
                End Sub
            End Class
            """, "Sub F(value As Integer)");

        // IDE0084 — the option-driven IsNot direction; the repository has to ask for it.
        yield return CaseWithConfig("IDE0084", IsNotPreferred, """
            Friend Class C
                Private other As Object

                Public Function F(o As Object) As Boolean
                    Return Not (o Is other)
                End Function
            End Class
            """, "o IsNot other");
    }

    /// <summary>
    /// One case per supported id where the shape is next to the rewrite's but must not match: the
    /// binding fails, the type is not a reference, the option is unset, and so on.
    /// </summary>
    public static IEnumerable<object[]> IntactCases()
    {
        // The short name does not bind: System.Text is not imported.
        yield return Intact("IDE0001", """
            Friend Class C
                Public Function F() As System.Text.StringBuilder
                    Return New System.Text.StringBuilder()
                End Function
            End Class
            """);

        // The shared member is not reachable unqualified from this class.
        yield return Intact("IDE0002", """
            Friend Class Other
                Public Shared Number As Integer = 2
            End Class

            Friend Class C
                Public Function F() As Integer
                    Return Other.Number
                End Function
            End Class
            """);

        // A local shadows the field, so Me._x is the only way to read it.
        yield return Intact("IDE0003", """
            Friend Class C
                Private _x As Integer = 1

                Public Function F() As Integer
                    Dim _x As Integer = 2
                    Return Me._x
                End Function
            End Class
            """);

        // The cast changes the type, so it is not unnecessary.
        yield return Intact("IDE0004", """
            Friend Class C
                Public Function F(x As Integer) As Long
                    Return CType(x, Long)
                End Function
            End Class
            """);

        // Guid has no language keyword.
        yield return Intact("IDE0049", """
            Friend Class C
                Public Function F() As System.Guid
                    Return System.Guid.Empty
                End Function
            End Class
            """);

        // VB compares strings by value: "= Nothing" is not "Is Nothing" for a String.
        yield return Intact("IDE0041", """
            Friend Class C
                Public Function F(s As String) As Boolean
                    Return s = Nothing
                End Function
            End Class
            """);

        // Parentheses around an expression that is not primary: their precedence matters.
        yield return Intact("IDE0047", """
            Friend Class C
                Public Function F(a As Integer, b As Integer) As Integer
                    Return (a + b) * 2
                End Function
            End Class
            """);

        // The left side of the assignment is not the left side of the addition.
        yield return Intact("IDE0054", """
            Friend Class C
                Public Sub F(y As Integer)
                    Dim x As Integer = 1
                    x = y + 1
                End Sub
            End Class
            """);

        // No boolean literal on either side.
        yield return Intact("IDE0100", """
            Friend Class C
                Public Function F(b As Boolean, c As Boolean) As Boolean
                    Return b = c
                End Function
            End Class
            """);

        // The else branch is not a boolean literal.
        yield return Intact("IDE0075", """
            Friend Class C
                Public Function F(b As Boolean, c As Boolean) As Boolean
                    Return If(b, True, c)
                End Function
            End Class
            """);

        // The tested expression is not the one the condition checks.
        yield return Intact("IDE0029", """
            Friend Class C
                Public Function F(s As String) As String
                    Return If(s IsNot Nothing, s.Trim(), "x")
                End Function
            End Class
            """);

        // HasValue is checked on another variable.
        yield return Intact("IDE0030", """
            Friend Class C
                Public Function F(x As Integer?, y As Integer) As Integer
                    Return If(x.HasValue, y, 0)
                End Function
            End Class
            """);

        // The null branch is not Nothing.
        yield return Intact("IDE0031", """
            Friend Class C
                Public Function F(s As String) As String
                    Return If(s IsNot Nothing, s.Trim(), "")
                End Function
            End Class
            """);

        // ByRef is a different passing convention.
        yield return Intact("IDE0081", """
            Friend Class C
                Public Sub F(ByRef value As Integer)
                End Sub
            End Class
            """);

        // The option is unset, so there is no direction to apply.
        yield return Intact("IDE0084", """
            Friend Class C
                Private other As Object

                Public Function F(o As Object) As Boolean
                    Return Not (o Is other)
                End Function
            End Class
            """);
    }

    private const string IsNotPreferred = """
        root = true

        [*.vb]
        visual_basic_style_prefer_isnot_expression = true
        """;

    private static object[] Case(string id, string source, params string[] expected) =>
        [id, source, expected, null];

    private static object[] CaseWithConfig(string id, string editorConfig, string source, params string[] expected) =>
        [id, source, expected, editorConfig];

    private static object[] Intact(string id, string source) => [id, source];

    [Theory]
    [MemberData(nameof(RuleCases))]
    public async Task VbSimplify_AppliesTheRule(string id, string source, string[] expected, string? editorConfig)
    {
        using var workspace = editorConfig is null
            ? VisualBasicWorkspace.Create("C.vb", source)
            : VisualBasicWorkspace.CreateWithEditorConfig(editorConfig, "C.vb", source);

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, id, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("refused").GetArrayLength());
        Assert.Equal("simplify_vb_code", json.GetProperty("refactoring").GetString());
        Assert.Equal(1, json.GetProperty("filesChanged").GetInt32());
        Assert.All(json.GetProperty("edits").EnumerateArray(),
            edit => Assert.Equal(id, edit.GetProperty("rule").GetString()));

        var text = workspace.Read("C.vb");
        foreach (var fragment in expected)
            Assert.Contains(fragment, text);
    }

    [Theory]
    [MemberData(nameof(IntactCases))]
    public async Task VbSimplify_LeavesTheShapeAlone(string id, string source)
    {
        using var workspace = VisualBasicWorkspace.Create("C.vb", source);

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, id, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(0, json.GetProperty("totalEdits").GetInt32());
        Assert.Equal(source, workspace.Read("C.vb"));
    }

    [Fact]
    public void VbRules_EverySupportedIdHasARewriteAndAnIntactCase()
    {
        var supported = VbSimplifyCodeService.Rules.Select(rule => rule.Id).ToList();
        Assert.InRange(supported.Count, 10, 15);

        var rewritten = RuleCases().Select(data => (string)data[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var intact = IntactCases().Select(data => (string)data[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(supported.Where(id => !rewritten.Contains(id)));
        Assert.Empty(supported.Where(id => !intact.Contains(id)));
    }

    [Fact]
    public void VbRules_DoNotClaimAnIdTwiceAndExplainEveryRefusal()
    {
        var supported = VbSimplifyCodeService.Rules.Select(rule => rule.Id).ToList();
        var notRewritten = VbSimplifyCodeService.NotRewritten.Select(entry => entry.Id).ToList();

        var all = supported.Concat(notRewritten).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Every refusal carries a reason, and no reason is a placeholder.
        Assert.All(VbSimplifyCodeService.NotRewritten, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.False(entry.Reason.Contains("TODO", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void VbOptions_ReadTheVisualBasicAndDotnetKeys()
    {
        var options = VbStyleLanguage.Parse(key => key switch
        {
            "visual_basic_preferred_modifier_order" => "Public, Shared, Overrides",
            "dotnet_style_require_accessibility_modifiers" => "always",
            "visual_basic_style_prefer_isnot_expression" => "false",
            _ => null,
        });

        Assert.Equal(["public", "shared", "overrides"], options.ModifierOrder);
        Assert.Equal(AccessibilityPreference.Always, options.Accessibility);
        Assert.Equal(IsNotPreference.PreferNot, options.PreferIsNot);
        Assert.True(options.AnyConfigured);

        // VB has no braces and no file-scoped namespace, so those fields stay at their defaults.
        Assert.Equal(BracePreference.None, options.Braces);
        Assert.Equal(NamespacePreference.None, options.Namespaces);
    }

    [Fact]
    public async Task VbSimplify_UsesIsNotWhenTheRepositoryPrefersIt()
    {
        const string source = """
            Friend Class C
                Private other As Object

                Public Function F(o As Object) As Boolean
                    Return Not (o Is other)
                End Function
            End Class
            """;

        using var workspace = VisualBasicWorkspace.CreateWithEditorConfig(IsNotPreferred, "C.vb", source);

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, "IDE0084", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("Return o IsNot other", workspace.Read("C.vb"));
        Assert.Equal("PreferIsNot",
            ResultAssert.Json(result).GetProperty("styleOptions").GetProperty("preferIsNot").GetString());
    }

    [Fact]
    public async Task VbSimplify_WritesNotWhenTheRepositoryPrefersNot()
    {
        const string source = """
            Friend Class C
                Private other As Object

                Public Function F(o As Object) As Boolean
                    Return o IsNot other
                End Function
            End Class
            """;

        using var workspace = VisualBasicWorkspace.CreateWithEditorConfig("""
            root = true

            [*.vb]
            visual_basic_style_prefer_isnot_expression = false
            """, "C.vb", source);

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, "IDE0084", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("Return Not (o Is other)", workspace.Read("C.vb"));
        Assert.Equal("PreferNot",
            ResultAssert.Json(result).GetProperty("styleOptions").GetProperty("preferIsNot").GetString());
    }

    [Fact]
    public async Task VbSimplify_ExplainsAnIdItDoesNotRewrite()
    {
        using var workspace = VisualBasicWorkspace.Create("C.vb", "Friend Class C\nEnd Class\n");

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, "IDE0140", dryRun: true);

        var json = ResultAssert.Json(result);
        Assert.Contains("not rewritable", json.GetProperty("error").GetString());
        Assert.Contains(json.GetProperty("notRewritten").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "IDE0140" &&
                     entry.GetProperty("reason").GetString()!.Contains("layout"));
    }

    /// <summary>
    /// The extension method the MCP host calls: it must resolve without touching the shared registration.
    /// </summary>
    [Fact]
    public void AddSnitchVisualBasic_RegistersTheVbService()
    {
        var services = new ServiceCollection();
        // AddSnitchVisualBasic is additive: the host already registered the workspace, so the VB
        // service is activated with one here too.
        services.AddSingleton<Snitch.Workspace.IRoslynWorkspace>(
            FakeWorkspace.Create(("C.cs", "internal class C { }")));
        services.AddSnitchVisualBasic();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<VbSimplifyCodeService>(provider.GetRequiredService<IVbSimplifyCodeService>());
    }

    /// <summary>
    /// The VB service is handed a C# document on purpose (document scope does not filter by extension):
    /// no VB matcher may fire on a C# tree, and the file must come back byte for byte.
    /// </summary>
    [Fact]
    public async Task VbService_DoesNotTouchACSharpFile()
    {
        const string csharp = """
            internal class C
            {
                public int F(int x)
                {
                    x = x + 1;
                    return (x);
                }
            }
            """;

        using var workspace = VisualBasicWorkspace.CreateWithCSharpFile(
            ("Probe.vb", """
                Friend Class VbProbe
                    Public Function F(s As String) As Integer
                        Return (s).Length
                    End Function
                End Class
                """),
            ("C.cs", csharp));

        var service = new VbSimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(0, json.GetProperty("totalEdits").GetInt32());
        Assert.Equal(csharp, workspace.Read("C.cs"));
    }

    /// <summary>
    /// With every rule enabled, IDE0001 and IDE0049 both match System.Int32. The service must apply
    /// one of them to the node instead of failing on a duplicate key in the replacement map.
    /// </summary>
    [Fact]
    public async Task VbSimplify_WithEveryRuleEnabled_AppliesOneRewritePerNode()
    {
        const string source = """
            Imports System

            Friend Class C
                Public Function F() As System.Int32
                    Return 1
                End Function
            End Class
            """;

        using var workspace = VisualBasicWorkspace.Create("C.vb", source);
        var service = new VbSimplifyCodeService(workspace);

        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, rules: null, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Equal(1, ResultAssert.Json(result).GetProperty("totalEdits").GetInt32());

        // IDE0001 comes first in the catalogue, so it owns the node (System.Int32 → Int32);
        // IDE0049 is shadowed rather than applied a second time to the same span.
        var text = workspace.Read("C.vb");
        Assert.DoesNotContain("System.Int32", text);
        Assert.Contains("As Int32", text);
    }

    /// <summary>
    /// IDE0031 writes s?.Trim(), and the inner member access of a conditional access has no receiver.
    /// The next pass visits it: every rule must survive the syntax the previous pass produced.
    /// </summary>
    [Fact]
    public async Task VbSimplify_WithEveryRuleEnabled_SurvivesTheConditionalAccessItCreates()
    {
        const string source = """
            Friend Class C
                Public Function F(s As String) As String
                    Return If(s IsNot Nothing, s.Trim(), Nothing)
                End Function
            End Class
            """;

        using var workspace = VisualBasicWorkspace.Create("C.vb", source);
        var service = new VbSimplifyCodeService(workspace);

        var result = await service.SimplifyAsync("document", workspace.PathOf("C.vb"), null, rules: null, dryRun: false);

        ResultAssert.Succeeded(result);
        Assert.Contains("s?.Trim()", workspace.Read("C.vb"));
    }

    /// <summary>
    /// A VB project in an <see cref="AdhocWorkspace"/>, with real files in a temp folder so the service
    /// writes through its normal path and the test reads the result from disk. It implements the same
    /// mutation contract as the server's workspace, and the metacode tests use it too: it is the only
    /// workspace in this suite whose documents are in a language the frontends cannot read.
    /// </summary>
    internal sealed class VisualBasicWorkspace : IRoslynWorkspace
    {
        private readonly AdhocWorkspace _workspace = new();
        private readonly string _directory;
        private Solution _solution = null!;

        private VisualBasicWorkspace()
        {
            _directory = Path.Combine(Path.GetTempPath(), "vmcp-vb-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public static VisualBasicWorkspace Create(string name, string source) => Build([(name, source)], null, null);

        public static VisualBasicWorkspace CreateWithEditorConfig(string editorConfig, string name, string source) =>
            Build([(name, source)], editorConfig, null);

        public static VisualBasicWorkspace CreateWithCSharpFile(
            (string Name, string Source) visualBasic, (string Name, string Source) csharp) =>
            Build([visualBasic], null, csharp);

        private static VisualBasicWorkspace Build(
            (string Name, string Source)[] files,
            string? editorConfig,
            (string Name, string Source)? csharpFile)
        {
            var fake = new VisualBasicWorkspace();

            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "ProbeVb",
                "ProbeVb",
                LanguageNames.VisualBasic,
                filePath: Path.Combine(fake._directory, "ProbeVb.vbproj"),
                compilationOptions: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new VisualBasicParseOptions(
                    Microsoft.CodeAnalysis.VisualBasic.LanguageVersion.Latest,
                    DocumentationMode.None,
                    SourceCodeKind.Regular),
                metadataReferences: References());

            var solution = fake._workspace.CurrentSolution.AddProject(projectInfo);
            foreach (var (name, source) in files)
            {
                var path = fake.PathOf(name);
                File.WriteAllText(path, source);
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId), name, SourceText.From(source), filePath: path);
            }

            if (csharpFile is { } other)
            {
                var csharpId = ProjectId.CreateNewId();
                var csharpInfo = ProjectInfo.Create(
                    csharpId,
                    VersionStamp.Create(),
                    "ProbeCs",
                    "ProbeCs",
                    LanguageNames.CSharp,
                    filePath: Path.Combine(fake._directory, "ProbeCs.csproj"),
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    parseOptions: new CSharpParseOptions(
                        Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview,
                        DocumentationMode.None,
                        SourceCodeKind.Regular),
                    metadataReferences: References());

                solution = solution.AddProject(csharpInfo);
                var csharpPath = fake.PathOf(other.Name);
                File.WriteAllText(csharpPath, other.Source);
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(csharpId), other.Name, SourceText.From(other.Source), filePath: csharpPath);
            }

            if (editorConfig is not null)
            {
                var configPath = Path.Combine(fake._directory, ".editorconfig");
                File.WriteAllText(configPath, editorConfig);
                var configInfo = DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    ".editorconfig",
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(editorConfig), VersionStamp.Create(), configPath)),
                    filePath: configPath);

                solution = solution.AddAnalyzerConfigDocuments([configInfo]);
            }

            // A real file, because the store derives the folder for .visualMCP from the solution path.
            File.WriteAllText(fake.PathOf("Probe.sln"), string.Empty);

            fake._solution = solution;
            return fake;
        }

        public string PathOf(string name) => Path.Combine(_directory, name);

        public string Read(string name) => File.ReadAllText(PathOf(name));

        public Solution? CurrentSolution => _solution;

        public string? LoadedSolutionPath => null;

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(_solution);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => PathOf("Probe.sln");

        public bool TryUpdateDocument(DocumentId documentId, SourceText text)
        {
            _solution = _solution.WithDocumentText(documentId, text);
            return true;
        }

        public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text)
        {
            if (_solution.GetProject(projectId) is null) return null;

            var documentId = DocumentId.CreateNewId(projectId);
            _solution = _solution.AddDocument(documentId, name, text, filePath: filePath);
            return documentId;
        }

        public bool TryRemoveDocument(DocumentId documentId)
        {
            if (_solution.GetDocument(documentId) is null) return false;

            _solution = _solution.RemoveDocument(documentId);
            return true;
        }

        public void Dispose()
        {
            _workspace.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best effort */ }
        }

        private static List<MetadataReference> References()
        {
            var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
            return trusted
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => Path.GetFileName(path) is var file &&
                               (file.StartsWith("System.", StringComparison.Ordinal) ||
                                file is "netstandard.dll" or "mscorlib.dll" or
                                    "Microsoft.VisualBasic.dll" or "Microsoft.VisualBasic.Core.dll"))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToList();
        }
    }
}
