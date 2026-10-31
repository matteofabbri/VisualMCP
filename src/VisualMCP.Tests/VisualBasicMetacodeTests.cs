using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Generation;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The Visual Basic frontend: the constructs whose spelling differs from C# — modules, <c>If()</c>,
/// <c>Select Case</c>, <c>!name</c>, <c>With</c> — lowered into the same model every other language uses,
/// so one pseudocode shape covers both.
/// </summary>
public sealed class VisualBasicMetacodeTests
{
    private static MetacodeDocument Translate(string source, string path = "Sample.vb") =>
        MetacodeTranslator.Default.Translate(MetacodeSource.FromVisualBasic(source, path));

    private static string Render(string source, string path = "Sample.vb") =>
        new PseudocodeRenderer().Render(Translate(source, path));

    [Fact]
    public void AModuleIsLoweredIntoTheSharedModel()
    {
        var text = Render("""
            Public Module Demo
                Public Function Sum(n As Integer) As Integer
                    Dim total As Integer = 0
                    For i = 0 To n - 1
                        total += i
                    Next
                    Return total
                End Function
            End Module
            """);

        Assert.Contains("CLASS Demo", text);
        Assert.Contains("STATIC", text);
        Assert.Contains("PUBLIC STATIC METHOD Sum(n : int) : int", text);
        Assert.Contains("DECLARE total : int = 0", text);
        // The control-flow pass turns the counted loop into the language-neutral initializer + while.
        Assert.Contains("i = 0", text);
        Assert.Contains("WHILE i <= n - 1 DO", text);
        Assert.Contains("i += 1", text);
        Assert.Contains("total += i", text);
        Assert.Contains("RETURN total", text);
    }

    [Fact]
    public void TheSameAlgorithmInVisualBasicAndCSharpRendersIdentically()
    {
        var visualBasic = Render("""
            Public Class Wallet
                Public Sub Add(customer As Customer, amount As Money)
                    If amount Is Nothing Then
                        Return
                    End If
                    customer.Total += amount
                End Sub
            End Class
            """, "Wallet.vb");

        var csharp = new PseudocodeRenderer().Render(MetacodeTranslator.Default.Translate(
            MetacodeSource.FromCSharp("""
                public class Wallet
                {
                    public void Add(Customer customer, Money amount)
                    {
                        if (amount == null) return;
                        customer.Total += amount;
                    }
                }
                """, "Wallet.cs")));

        Assert.Equal(csharp, visualBasic);
    }

    [Fact]
    public void APredefinedTypeIsSpelledTheSameInBothLanguages()
    {
        var visualBasic = Render("""
            Public Module Demo
                Public Function Increment(value As Long) As Long
                    Return value + 1
                End Function
            End Module
            """, "Demo.vb");

        var csharp = new PseudocodeRenderer().Render(MetacodeTranslator.Default.Translate(
            MetacodeSource.FromCSharp("""
                public static class Demo
                {
                    public static long Increment(long value)
                    {
                        return value + 1;
                    }
                }
                """, "Demo.cs")));

        Assert.Equal(csharp, visualBasic);
        Assert.Contains("PUBLIC STATIC METHOD Increment(value : long) : long", visualBasic);
    }

    [Fact]
    public void AVisualBasicTreeCanBeEmittedAsCSharp()
    {
        var document = Translate("""
            Public Module Demo
                Public Function Increment(value As Integer) As Integer
                    Return value + 1
                End Function
            End Module
            """, "Demo.vb");

        var emitted = new CSharpEmitter(CSharpEmitOptions.Default with { IncludeUsings = false }).Emit(document);

        Assert.Contains("public static class Demo", emitted.Code);
        Assert.Contains("public static int Increment(int value)", emitted.Code);
        Assert.Contains("return value + 1;", emitted.Code);
    }

    [Fact]
    public void AnExplicitPropertyKeepsItsAccessors()
    {
        var text = Render("""
            Public Class C
                Private _count As Integer
                Public ReadOnly Property Count As Integer
                    Get
                        Return _count
                    End Get
                End Property
            End Class
            """);

        Assert.Contains("PROPERTY Count : int", text);
        Assert.Contains("RETURN _count", text);
        Assert.Contains("END GET", text);
        Assert.DoesNotContain("accessors:", text);
    }

    [Fact]
    public void ASelectCaseBecomesASwitchWithEveryClauseShape()
    {
        var text = Render("""
            Public Class C
                Public Function Label(value As Integer) As String
                    Select Case value
                        Case 1, 2
                            Return "small"
                        Case 3 To 9
                            Return "medium"
                        Case Is > 9
                            Return "large"
                        Case Else
                            Return "other"
                    End Select
                End Function
            End Class
            """);

        Assert.Contains("SWITCH value", text);
        Assert.Contains("CASE 1, 2", text);
        Assert.Contains("CASE PATTERN (3 To 9)", text);
        Assert.Contains("CASE PATTERN (Is > 9)", text);
        Assert.Contains("DEFAULT", text);
    }

    [Fact]
    public void TheThreeConditionalFormsKeepTheirMeaning()
    {
        var text = Render("""
            Public Class C
                Public Function Pick(a As Object, b As Object, c As Boolean) As Object
                    Dim coalesced = If(a, b)
                    Dim chosen = If(c, a, b)
                    If c Then
                        Return coalesced
                    End If
                    Return chosen
                End Function
            End Class
            """);

        Assert.Contains("DECLARE coalesced = a ?? b", text);
        Assert.Contains("DECLARE chosen = c ? a : b", text);
        Assert.Contains("IF c THEN", text);
    }

    [Fact]
    public void ADescendingForLoopComparesDownwards()
    {
        var text = Render("""
            Public Class C
                Public Sub Countdown()
                    For i = 10 To 0 Step -1
                        Trace.WriteLine(i)
                    Next
                End Sub
            End Class
            """);

        Assert.Contains("WHILE i >= 0 DO", text);
        Assert.Contains("i += -1", text);
    }

    [Fact]
    public void ADictionaryAccessIsAnIndexByString()
    {
        var text = Render("""
            Public Class C
                Public Function Read(values As Object) As Object
                    Return values!Name
                End Function
            End Class
            """);

        Assert.Contains("RETURN values[\"Name\"]", text);
    }

    [Fact]
    public void AWithBlockIsKeptAndReported()
    {
        var document = Translate("""
            Public Class C
                Public Sub Move(target As Point)
                    With target
                        .X = 1
                    End With
                End Sub
            End Class
            """);

        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement
            && diagnostic.Message.Contains("With"));

        var text = new PseudocodeRenderer().Render(document);
        Assert.Contains("RAW[vb] With target", text);
        Assert.Contains(".X = 1", text);
    }

    [Fact]
    public void AParseErrorIsADiagnosticRatherThanAnException()
    {
        var document = Translate("Public Class C\n    Public Sub Broken(\nEnd Class\n");

        Assert.Contains(document.Diagnostics, diagnostic =>
            diagnostic.Code == MetacodeDiagnosticCodes.SyntaxError
            && diagnostic.Severity == MetacodeDiagnosticSeverity.Error);
    }

    [Fact]
    public void TheRenderingReadsBackAsTheSamePseudocode()
    {
        var first = Render("""
            Public Module Demo
                Public Function Total(values As Integer()) As Integer
                    Dim total As Integer = 0
                    For Each value In values
                        If value > 0 Then
                            total += value
                        End If
                    Next
                    Return total
                End Function
            End Module
            """);

        var reloaded = new PseudocodeRenderer().Render(
            MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(first, "Sample.mc")));

        Assert.Equal(first, reloaded);
    }

    [Fact]
    public void VisualBasicIsRecognisedByLanguageAndByExtension()
    {
        var frontend = new VisualBasicFrontend();

        Assert.True(frontend.CanHandle(MetacodeSource.FromVisualBasic("Public Class C\nEnd Class", "C.vb")));
        Assert.False(frontend.CanHandle(MetacodeSource.FromCSharp("class C { }", "C.cs")));

        var path = Path.Combine(Path.GetTempPath(), "vmcp-vb-frontend-" + Guid.NewGuid().ToString("N") + ".vb");
        File.WriteAllText(path, "Public Class C\nEnd Class\n");
        try
        {
            var source = MetacodeSource.FromFile(path);
            Assert.Equal(MetacodeLanguage.VisualBasic, source.Language);

            var document = MetacodeTranslator.Default.Translate(source);
            Assert.Equal(MetacodeLanguage.VisualBasic, document.Language);
            Assert.Empty(document.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
