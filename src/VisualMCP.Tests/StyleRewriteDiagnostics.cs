using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Style;
using Snitch.Languages.Style;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Why a statement-level rewrite did not fire. Each case runs the matcher directly over every node and
/// fails with the id and the source instead of the service's generic "filesChanged was 0".
/// </summary>
public sealed class StyleRewriteDiagnostics
{
    private static (SyntaxTree Tree, SemanticModel Model) Parse(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        var metadata = references
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var file &&
                           (file.StartsWith("System.", StringComparison.Ordinal) ||
                            file is "netstandard.dll" or "mscorlib.dll"))
            .Select(path => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create("probe", [tree], metadata,
            new CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        return (tree, compilation.GetSemanticModel(tree));
    }

    private static void AssertFires(StyleRewrite rewrite, string source)
    {
        var (tree, model) = Parse(source);
        var matches = tree.GetRoot().DescendantNodes()
            .Select(node => rewrite.TryMatch(node, model, default))
            .OfType<StyleMatch>()
            .ToList();

        Assert.True(matches.Count > 0, $"{rewrite.Rule.Id} did not match:\n{source}");
    }

    [Fact]
    public void Ide0019_MatchesTheAsDeclarationBeforeTheIf()
    {
        AssertFires(new AsWithNullCheckRewrite(), """
            internal class C
            {
                public int F(object input)
                {
                    string text = input as string;
                    if (text != null)
                    {
                        return text.Length;
                    }

                    return 0;
                }
            }
            """);
    }

    [Fact]
    public void Ide0020_MatchesTheIfWithCastInside()
    {
        AssertFires(new IsWithCastRewrite(), """
            internal class C
            {
                public int F(object input)
                {
                    if (input is string)
                    {
                        return ((string)input).Length;
                    }

                    return 0;
                }
            }
            """);
    }

    [Fact]
    public void Ide0038_MatchesTheConditionConjunction()
    {
        AssertFires(new IsWithCastRewrite(), """
            internal class C
            {
                public bool F(object input) => input is string && ((string)input).Length > 2;
            }
            """);
    }

    [Fact]
    public void Ide0016_MatchesTheGuardAndTheStoreItProtects()
    {
        AssertFires(new ThrowExpressionRewrite(), """
            internal class C
            {
                private string _name;

                public C(string name)
                {
                    if (name == null) throw new System.ArgumentNullException(nameof(name));
                    _name = name;
                }
            }
            """);
    }

    [Fact]
    public void Ide0018_MatchesTheOutDeclaration()
    {
        AssertFires(new InlineVariableDeclarationRewrite(), """
            internal class C
            {
                public bool F(string text)
                {
                    int value;
                    if (int.TryParse(text, out value))
                    {
                        return value > 0;
                    }

                    return false;
                }
            }
            """);
    }

    [Fact]
    public void Ide0063_MatchesTheUsingBlock()
    {
        AssertFires(new SimpleUsingRewrite(), """
            using System.IO;

            internal class C
            {
                public void F(string path, byte[] payload)
                {
                    using (var stream = File.OpenWrite(path))
                    {
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
            }
            """);
    }

    [Fact]
    public void Ide0180_MatchesTheThreeStatementSwap()
    {
        AssertFires(new TupleSwapRewrite(), """
            internal class C
            {
                public void F(ref int a, ref int b)
                {
                    var temp = a;
                    a = b;
                    b = temp;
                }
            }
            """);
    }
}
