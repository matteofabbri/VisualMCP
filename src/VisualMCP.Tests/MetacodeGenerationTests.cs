using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Snitch.Languages.CSharp.Analysis.Complexity;
using Snitch.Metacode;
using Snitch.Metacode.Analysis;
using Snitch.Metacode.Generation;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The C# emitter: metacode in, C# out. The acceptance criterion is a fixpoint — translate the source,
/// emit C#, parse the C#, translate again, and require the same canonical pseudocode. Anything the model
/// cannot say is allowed to disappear, as long as it disappears the same way twice.
/// </summary>
public sealed class MetacodeGenerationTests
{
    // ── round-trip corpus ─────────────────────────────────────────────────────────────

    public static TheoryData<string, string> Snippets => new()
    {
        { "types and members", """
            namespace Demo.Types;

            public class Widget
            {
                private const int Limit = 10;
                public static readonly string Name = "widget";
                private int _count;

                public int Count { get; set; } = 1;
                public int Doubled => _count * 2;
                public string this[int index] => Name + index.ToString();
                public event System.EventHandler? Changed;

                public Widget() : this(0) { }
                public Widget(int count) { _count = count; }
                ~Widget() { }
            }
            """ },

        { "control flow", """
            public static class Flow
            {
                public static int Classify(int value)
                {
                    if (value < 0)
                    {
                        return -1;
                    }
                    else if (value == 0)
                    {
                        return 0;
                    }
                    else
                    {
                        return 1;
                    }
                }

                public static int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (i % 2 == 0 && i > 2)
                        {
                            continue;
                        }
                        total += i;
                    }

                    var j = 0;
                    while (j < n)
                    {
                        j++;
                    }

                    do
                    {
                        j--;
                    }
                    while (j > 0);

                    return total + j;
                }
            }
            """ },

        { "switch", """
            public static class Choices
            {
                public static string Name(int value)
                {
                    switch (value)
                    {
                        case 1:
                        case 2:
                            return "small";
                        case 3:
                            break;
                        default:
                            return "large";
                    }

                    return value switch
                    {
                        0 => "zero",
                        _ => "other",
                    };
                }
            }
            """ },

        { "exceptions and resources", """
            public static class Resources
            {
                public static void Run(object gate, System.IDisposable resource)
                {
                    try
                    {
                        lock (gate)
                        {
                            using (resource)
                            {
                                throw new System.InvalidOperationException("boom");
                            }
                        }
                    }
                    catch (System.InvalidOperationException ex) when (ex.Message.Length > 0)
                    {
                        System.Console.WriteLine(ex.Message);
                    }
                    catch (System.Exception)
                    {
                        throw;
                    }
                    finally
                    {
                        System.Console.WriteLine("done");
                    }
                }
            }
            """ },

        { "expressions", """
            public static class Expressions
            {
                public static string Describe(int value, string? text)
                {
                    var items = new System.Collections.Generic.List<int> { 1, 2, 3 };
                    int[] numbers = [4, 5, 6];
                    var array = new[] { 7, 8 };
                    var slice = numbers[1..^1];
                    var label = text ?? "none";
                    var flag = value > 0 ? "positive" : "other";
                    var boxed = (object)value;
                    var isText = text is string;
                    var converted = text as string;
                    var lambda = (int x) => x * 2;
                    var mapped = items[0];
                    return $"{label}: {flag} {boxed} {isText} {converted} {lambda(mapped)} {array.Length} {slice.Length}";
                }
            }
            """ },

        { "generics and signatures", """
            public interface IStore<T>
            {
                T Get(int index);
                void Put(int index, T value);
            }

            public sealed class Store<T> : IStore<T> where T : class, new()
            {
                private readonly System.Collections.Generic.Dictionary<int, T> _items = new();

                public T Get(int index) => _items[index];

                public void Put(int index, T value) => _items[index] = value;

                public static TResult Map<TResult>(T value, System.Func<T, TResult> map) => map(value);

                public static void Swap(ref int left, out int right, in int extra, params int[] rest)
                {
                    right = extra;
                    left = right;
                }
            }
            """ },

        { "comments and local functions", """
            public static class Comments
            {
                public static int Count()
                {
                    // the total starts at zero
                    var total = 0;

                    // a local function
                    int Twice(int value)
                    {
                        return value * 2;
                    }

                    total = Twice(total + 1);
                    return total;
                }
            }
            """ },

        { "top-level statements", """
            var answer = 42;
            System.Console.WriteLine(answer);

            public sealed class Marker
            {
            }
            """ },

        { "top-level statements and a namespace", """
            var answer = 42;
            System.Console.WriteLine(answer);

            namespace Demo
            {
                public sealed class Marker
                {
                }
            }
            """ },

        { "nested types and modifiers", """
            public abstract partial class Outer
            {
                protected internal static int Counter;

                internal abstract void Step();

                public virtual int Value() => 1;

                public static Outer operator +(Outer left, Outer right) => left;

                public partial class Inner
                {
                    public int Depth { get; private set; }
                }
            }
            """ },

        { "async lambda", """
            public static class Async
            {
                public static System.Threading.Tasks.Task<int> Run(System.Func<System.Threading.Tasks.Task<int>> factory)
                {
                    var bound = (System.Func<System.Threading.Tasks.Task<int>>)(() => factory());
                    return bound();
                }
            }
            """ },

        { "with, anonymous objects, patterns and spreads", """
            public sealed record Person
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }

            public static class Shapes
            {
                public static Person Rename(Person person, string name) => person with { Name = name };

                public static object Describe(int x) => new { x, Twice = x * 2 };

                public static bool Allowed(string name) => name is not ("admin" or "root");

                public static bool Present(object value) => value is { };

                public static bool InRange(int value) => value is > 0 and < 10;

                public static int[] Combine(int[] values) => [.. values, 42];

                public static string Classify(int value)
                {
                    switch (value)
                    {
                        case > 0:
                            return "positive";
                        case 0:
                            return "zero";
                        default:
                            return "negative";
                    }
                }
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void The_emitter_round_trips_a_file(string name, string source)
    {
        var first = Translate(source);
        var emitted = new CSharpEmitter().Emit(first);

        var errors = SyntaxErrors(emitted.Code);
        Assert.True(
            errors.Count == 0,
            $"{name}: the emitted C# does not parse:\n{string.Join("\n", errors)}\n--- emitted ---\n{emitted.Code}\n--- diagnostics ---\n{string.Join("\n", emitted.Diagnostics)}");

        var second = Translate(emitted.Code);
        Assert.Equal(Render(first), Render(second));
    }

    [Fact]
    public void A_stored_declaration_can_be_read_back_and_re_emitted()
    {
        // The store keeps each declaration as canonical pseudocode; generation from the store wraps that
        // text in a module, reads it with the pseudocode frontend and emits C# — this is that round-trip.
        const string source = """
            public sealed class Counter
            {
                private int _value;

                public int Next(int step)
                {
                    if (step < 0)
                    {
                        throw new System.ArgumentOutOfRangeException(nameof(step));
                    }

                    _value += step;
                    return _value;
                }
            }
            """;

        var document = Translate(source);
        var type = document.Modules.SelectMany(module => module.Declarations).OfType<MetacodeTypeDeclaration>().Single();
        var method = type.Members.OfType<MetacodeFunction>().Single();

        var stored = new PseudocodeRenderer().Render(method);
        var parsed = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode(
            "MODULE stored\n" + stored.TrimEnd() + "\nEND MODULE\n"));

        var declaration = parsed.Modules.SelectMany(module => module.Declarations).Single();
        var result = new CSharpEmitter(CSharpEmitOptions.Default with { IncludeUsings = false }).Emit(declaration);

        Assert.Empty(SyntaxErrors("public sealed class Host\n{\n" + result.Code + "\n}\n"));
        Assert.Contains("int Next(int step)", result.Code);
        Assert.Contains("_value += step;", result.Code);
    }

    [Fact]
    public void A_module_of_free_functions_is_wrapped_in_a_static_class()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode("""
            MODULE py
            FUNCTION total(values)
            FOREACH value IN values DO
            total = total + value
            END FOREACH
            RETURN total
            END FUNCTION
            END MODULE
            """));

        var result = new CSharpEmitter().Emit(document);

        Assert.Empty(SyntaxErrors(result.Code));
        Assert.Contains("static partial class", result.Code);
        Assert.Contains("foreach (var value in values)", result.Code);
    }

    [Fact]
    public void A_namespace_of_free_functions_gets_a_container_class()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode("""
            NAMESPACE Demo
            FUNCTION run()
            RETURN 1
            END FUNCTION
            END NAMESPACE
            """));

        var result = new CSharpEmitter().Emit(document);

        Assert.Empty(SyntaxErrors(result.Code));
        Assert.Contains("namespace Demo", result.Code);
        Assert.Contains("static partial class Demo", result.Code);
    }

    [Fact]
    public void A_python_import_is_a_comment_and_a_diagnostic()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode("""
            MODULE py
            FUNCTION run()
            IMPORT os
            RETURN 1
            END FUNCTION
            END MODULE
            """));

        var result = new CSharpEmitter().Emit(document);

        Assert.Contains("// TODO(metacode): import os", result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_async_lambda_gets_the_async_modifier_the_model_cannot_carry()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromPseudocode("""
            MODULE py
            FUNCTION run(task)
            DECLARE bound = () => AWAIT task()
            RETURN bound
            END FUNCTION
            END MODULE
            """));

        var result = new CSharpEmitter(CSharpEmitOptions.Default with { IncludeUsings = false }).Emit(document);
        Assert.Contains("async () =>", result.Code);
    }

    // ── complexity: the model against the C# analyzer ──────────────────────────────────

    public static TheoryData<string, string> ComplexitySnippets => new()
    {
        { "if / else-if / else at top level", """
            public static class A
            {
                public static int Classify(int value)
                {
                    if (value < 0) return -1;
                    else if (value == 0) return 0;
                    else return 1;
                }
            }
            """ },

        { "else-if inside a loop", """
            public static class B
            {
                public static int Scan(int[] values)
                {
                    var total = 0;
                    foreach (var value in values)
                    {
                        if (value < 0) total--;
                        else if (value == 0) total += 0;
                        else total++;
                    }

                    return total;
                }
            }
            """ },

        { "loops and logical runs", """
            public static class C
            {
                public static int Count(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (i > 2 && i < 10 || i == 0)
                        {
                            total += i;
                        }
                    }

                    var j = n;
                    while (j > 0 && j != 3)
                    {
                        j--;
                    }

                    do
                    {
                        j++;
                    }
                    while (j < 2);

                    return total + j;
                }
            }
            """ },

        { "switch statement and expression", """
            public static class D
            {
                public static string Name(int value)
                {
                    switch (value)
                    {
                        case 1:
                        case 2:
                            return "small";
                        case 3:
                            return "middle";
                        default:
                            break;
                    }

                    return value switch
                    {
                        0 => "zero",
                        4 when value > 3 => "four",
                        _ => "other",
                    };
                }
            }
            """ },

        { "try, catch and filters", """
            public static class E
            {
                public static int Read(string text)
                {
                    try
                    {
                        return int.Parse(text);
                    }
                    catch (System.FormatException) when (text.Length > 0)
                    {
                        return -1;
                    }
                    catch (System.ArgumentNullException)
                    {
                        return -2;
                    }
                    finally
                    {
                        System.Console.WriteLine("done");
                    }
                }
            }
            """ },

        { "ternary, coalesce and recursion", """
            public static class F
            {
                public static int? Pick(int? left, int? right) => left ?? right;

                public static int Absolute(int value)
                {
                    var text = value.ToString() ?? string.Empty;
                    var sign = text.Length > 0 ? 1 : 0;
                    return sign > 0 ? Absolute(value - 1) : 0;
                }
            }
            """ },
    };

    [Theory]
    [MemberData(nameof(ComplexitySnippets))]
    public void The_metacode_complexity_matches_the_CSharp_analyzer(string name, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var model = MetacodeTranslator.Default.Translate(MetacodeSource.FromSyntaxTree(tree));

        // The C# analyzer detects recursion through symbols, so it needs a semantic model; without one
        // its cognitive score silently drops the self-call and the comparison would be unfair.
        var compilation = CSharpCompilation.Create(
            "Snippet",
            new[] { tree },
            TrustedReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semantic = compilation.GetSemanticModel(tree);

        var functions = Functions(model).ToDictionary(function => function.Name, StringComparer.Ordinal);
        var methods = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var expected = ComplexityAnalyzer.Analyze(method, semantic);
            Assert.True(
                functions.TryGetValue(method.Identifier.Text, out var function),
                $"{name}: '{method.Identifier.Text}' is missing from the metacode");

            var actual = MetacodeComplexityAnalyzer.Analyze(function!);

            Assert.True(
                expected.Cyclomatic == actual.Cyclomatic,
                $"{name}: '{method.Identifier.Text}' cyclomatic — Roslyn {expected.Cyclomatic}, metacode {actual.Cyclomatic}");

            Assert.True(
                expected.Cognitive == actual.Cognitive,
                $"{name}: '{method.Identifier.Text}' cognitive — Roslyn {expected.Cognitive}, metacode {actual.Cognitive}");
        }
    }

    [Fact]
    public void A_type_and_a_field_have_no_complexity_of_their_own()
    {
        const string source = """
            public sealed class Plain
            {
                public int Value;

                public int Read() => Value;
            }
            """;

        var model = MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(source));
        var declarations = model.Modules.SelectMany(module => module.Declarations).OfType<MetacodeTypeDeclaration>().Single();

        var method = declarations.Members.OfType<MetacodeFunction>().Single();
        Assert.Equal(1, MetacodeComplexityAnalyzer.Analyze(method).Cyclomatic);
        Assert.Equal(1, MetacodeComplexityAnalyzer.Analyze(method).Statements);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────

    private static MetacodeDocument Translate(string source) =>
        MetacodeTranslator.Default.Translate(MetacodeSource.FromCSharp(source, "Snippet.cs"));

    private static string Render(MetacodeDocument document) => new PseudocodeRenderer().Render(document).Trim();

    private static List<string> SyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Preview));
        return tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToList();
    }

    private static List<MetadataReference> TrustedReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
        return trusted
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileName(path) is var file &&
                           (file.StartsWith("System.", StringComparison.Ordinal) ||
                            file is "netstandard.dll" or "mscorlib.dll"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }

    private static IEnumerable<MetacodeFunction> Functions(MetacodeDocument document) =>
        document.Modules.SelectMany(module => Declarations(module.Declarations));

    private static IEnumerable<MetacodeFunction> Declarations(IReadOnlyList<MetacodeDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case MetacodeFunction function:
                    yield return function;
                    break;
                case MetacodeTypeDeclaration type:
                    foreach (var member in Declarations(type.Members))
                        yield return member;
                    break;
            }
        }
    }
}
