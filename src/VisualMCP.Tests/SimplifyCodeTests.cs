using Snitch.Languages.CSharp.Style;
using Snitch.Storage;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The style-suggestion engine over real temp solutions: what it rewrites, what it leaves alone, and
/// what the compiler gate refuses.
/// </summary>
public sealed class SimplifyCodeTests
{
    private const string Header = "using System.Collections.Generic;";

    /// <summary>
    /// One case per rule: the source, the id to run, and the text the rewrite must produce. Each case
    /// is a self-contained class so the file compiles on its own in the fake workspace.
    /// </summary>
    public static IEnumerable<object[]> RuleCases()
    {
        // IDE0001 — a fully qualified name that the usings already make short.
        yield return Case("IDE0001", """
            using System.Collections.Generic;

            internal class C
            {
                public System.Collections.Generic.List<int> F() => new List<int>();
            }
            """, "public List<int> F()");

        // IDE0002 — Type.StaticMember where the member is reachable unqualified.
        yield return Case("IDE0002", """
            internal class C
            {
                public static int Value = 1;

                public int F() => C.Value;
            }
            """, "public int F() => Value;");

        // IDE0003 — this.Member with nothing shadowing it.
        yield return Case("IDE0003", """
            internal class C
            {
                private int _x;

                public int F() => this._x;
            }
            """, "public int F() => _x;");

        // IDE0004 — a cast to the operand's own type.
        yield return Case("IDE0004", """
            internal class C
            {
                public int F(int x) => (int)x;
            }
            """, "public int F(int x) => x;");

        // IDE0049 — framework type names to keywords.
        yield return Case("IDE0049", """
            internal class C
            {
                public System.Int32 F() => 1;
            }
            """, "public int F() => 1;");

        // IDE0031 — null propagation.
        yield return Case("IDE0031", """
            internal class C
            {
                public string? F(string? s) => s != null ? s.Trim() : null;
            }
            """, "s?.Trim()");

        // IDE0029 — a conditional that is a coalesce.
        yield return Case("IDE0029", """
            internal class C
            {
                public string F(string? s) => s != null ? s : "x";
            }
            """, "s ?? \"x\"");

        // IDE0034 — default(T) to default.
        yield return Case("IDE0034", """
            internal class C
            {
                public int F() => default(int);
            }
            """, "public int F() => default;");

        // IDE0041 — a reference equality check to a pattern.
        yield return Case("IDE0041", """
            internal class C
            {
                public bool F(object? o) => o == null;
            }
            """, "o is null");

        // IDE0047 — parentheses around a primary expression.
        yield return Case("IDE0047", """
            internal class C
            {
                public int F(string s) => (s).Length;
            }
            """, "s.Length");

        // IDE0054 — compound assignment.
        yield return Case("IDE0054", """
            internal class C
            {
                public int F(int x)
                {
                    x = x + 1;
                    return x;
                }
            }
            """, "x += 1;");

        // IDE0056 — index operator.
        yield return Case("IDE0056", """
            internal class C
            {
                public int F(int[] a) => a[a.Length - 1];
            }
            """, "a[^1]");

        // IDE0057 — range operator.
        yield return Case("IDE0057", """
            internal class C
            {
                public string A(string s) => s.Substring(1);

                public string B(string s) => s.Substring(0, 2);
            }
            """, "s[1..]", "public string B(string s) => s[..2];");

        // IDE0071 — interpolation of a ToString() call.
        yield return Case("IDE0071", """
            internal class C
            {
                public string F(int x) => $"{x.ToString()}";
            }
            """, "$\"{x}\"");

        // IDE0074 — null check plus assignment is ??=.
        yield return Case("IDE0074", """
            internal class C
            {
                public string F(string? s)
                {
                    if (s == null) s = "x";
                    return s;
                }
            }
            """, "s ??= \"x\";");

        // IDE0083 — negated type check is the not pattern.
        yield return Case("IDE0083", """
            internal class C
            {
                public bool F(object o) => !(o is string);
            }
            """, "o is not string");

        // IDE0100 — comparison with a boolean literal.
        yield return Case("IDE0100", """
            internal class C
            {
                public bool F(bool b) => b == true;
            }
            """, "public bool F(bool b) => b;");

        // IDE0075 — a conditional returning boolean literals.
        yield return Case("IDE0075", """
            internal class C
            {
                public bool F(bool b) => b ? true : false;
            }
            """, "public bool F(bool b) => b;");

        // IDE0200 — a lambda that only forwards its arguments.
        yield return Case("IDE0200", """
            using System;

            internal class C
            {
                private static int Twice(int x) => x * 2;

                public Func<int, int> F() => x => Twice(x);
            }
            """, "public Func<int, int> F() => Twice;");

        // IDE0280 — a literal parameter name becomes nameof.
        yield return Case("IDE0280", """
            using System;

            internal class C
            {
                public void F(string name)
                {
                    if (name == null) throw new System.ArgumentNullException("name");
                }
            }
            """, "nameof(name)");

        // IDE0340 — nameof of a constructed generic type.
        yield return Case("IDE0340", """
            using System.Collections.Generic;

            internal class C
            {
                public string F() => nameof(List<int>);
            }
            """, "nameof(List<>)");

        // Arrays: IDE0300 for the elements, IDE0301 for the empty case.
        yield return Case("IDE0300", """
            internal class C
            {
                public int[] F() => new[] { 1, 2 };
            }
            """, "public int[] F() => [1, 2];");

        yield return Case("IDE0301", """
            internal class C
            {
                public int[] F() => new int[0];
            }
            """, "public int[] F() => [];");

        // IDE0033 — a tuple element accessed through ItemN when the tuple names it.
        yield return Case("IDE0033", """
            internal class C
            {
                public int F()
                {
                    (int A, int B) pair = (1, 2);
                    return pair.Item1;
                }
            }
            """, "return pair.A;");

        // IDE0037 — an inferred member name spelled out.
        yield return Case("IDE0037", """
            internal class C
            {
                public object F(string name) => new { name = name };
            }
            """, "new { name }");

        // IDE0044 — a private field the compiler can prove is never reassigned.
        yield return Case("IDE0044", """
            internal class C
            {
                private int _value = 3;

                public int F() => _value;
            }
            """, "private readonly int _value = 3;");

        // IDE0082 — typeof(X).Name is nameof(X); a keyword type must be qualified to compile.
        yield return Case("IDE0082", """
            internal class C
            {
                public string F() => typeof(string).Name;

                public string G() => typeof(System.Uri).Name;
            }
            """, "nameof(System.String)", "public string G() => nameof(System.Uri);");

        // IDE0150 — a type check against object is a null check.
        yield return Case("IDE0150", """
            internal class C
            {
                public bool F(object value) => value is object;
            }
            """, "value != null");

        // IDE0170 — a nested property pattern flattened.
        yield return Case("IDE0170", """
            internal sealed class Inner
            {
                public int Value { get; set; }
            }

            internal sealed class Outer
            {
                public Inner Inner { get; set; } = new Inner();

                public bool F(Outer other) => other is { Inner: { Value: 1 } };
            }
            """, "other is { Inner.Value: 1 }");

        // IDE0230 — a UTF-8 literal where a span is wanted.
        yield return Case("IDE0230", """
            using System;

            internal class C
            {
                public ReadOnlySpan<byte> F() => System.Text.Encoding.UTF8.GetBytes("hi");
            }
            """, "\"hi\"u8");

        // IDE0039 — a lambda assigned to a local becomes a local function named after the variable.
        yield return Case("IDE0039", """
            using System;

            internal class C
            {
                public Func<int, int> F()
                {
                    Func<int, int> doubler = x => x * 2;
                    return doubler;
                }
            }
            """, "int Doubler(int x) => x * 2;", "Func<int, int> doubler = Doubler;");

        // IDE0079 — a SuppressMessage for a diagnostic the member cannot produce.
        yield return Case("IDE0079", """
            using System.Diagnostics.CodeAnalysis;

            internal class C
            {
                [SuppressMessage("Style", "CS0168", Justification = "never fires here")]
                public int F() => 1;
            }
            """, "public int F() => 1;");

        // IDE0250 — a struct whose fields are all readonly.
        yield return Case("IDE0250", """
            internal struct Point
            {
                public readonly int X;

                public Point(int x) => X = x;
            }
            """, "internal readonly struct Point");

        // IDE0251 — a struct member that only reads.
        yield return Case("IDE0251", """
            internal struct Counter
            {
                public int Value => 1;
            }
            """, "public readonly int Value => 1;");

        // IDE0302 — stackalloc with an initializer.
        yield return Case("IDE0302", """
            using System;

            internal class C
            {
                public int F()
                {
                    Span<int> values = stackalloc int[] { 1, 2 };
                    return values.Length;
                }
            }
            """, "Span<int> values = [1, 2];");

        // IDE0019 — as + null check becomes a pattern variable, and the declaration disappears.
        yield return Case("IDE0019", """
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
            """, "if (input is string text)", "return text.Length;");

        // IDE0020 — is check plus cast in the block.
        yield return Case("IDE0020", """
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
            """, "if (input is string t)", "return t.Length;");

        // IDE0038 — is check plus cast in the condition.
        yield return Case("IDE0038", """
            internal class C
            {
                public bool F(object input, string other) => input is string && ((string)input).Length > 2 && other != null;
            }
            """, "input is string t && t.Length > 2");

        // IDE0016 — a guard folded into the store it protects, which is the shape the IDE's fix has.
        yield return Case("IDE0016", """
            internal class C
            {
                private string _name;

                public C(string name)
                {
                    if (name == null) throw new System.ArgumentNullException(nameof(name));
                    _name = name;
                }
            }
            """, "_name = name ?? throw new System.ArgumentNullException(nameof(name));");

        // IDE0018 — an out variable declared just before the call.
        yield return Case("IDE0018", """
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
            """, "if (int.TryParse(text, out int value))");

        // IDE0063 — a straight-line using block (no return: that would move the dispose later).
        yield return Case("IDE0063", """
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
            """, "using var stream = File.OpenWrite(path);");

        // IDE0180 — the classic three-statement swap.
        yield return Case("IDE0180", """
            internal class C
            {
                public void F(ref int a, ref int b)
                {
                    var temp = a;
                    a = b;
                    b = temp;
                }
            }
            """, "(a, b) = (b, a);");

        // IDE0021 — a constructor whose body is the one assignment.
        yield return Case("IDE0021", """
            internal class C
            {
                public int Age { get; }

                public C(int age)
                {
                    Age = age;
                }
            }
            """, "public C(int age) => Age = age;");

        // IDE0022 — a method whose body is a single return.
        yield return Case("IDE0022", """
            internal class C
            {
                public int F()
                {
                    return 1;
                }
            }
            """, "public int F() => 1;");

        // IDE0023 — a conversion operator body.
        yield return Case("IDE0023", """
            internal class C
            {
                public int Value;

                public static implicit operator int(C source)
                {
                    return source.Value;
                }
            }
            """, "public static implicit operator int(C source) => source.Value;");

        // IDE0024 — an arithmetic operator body.
        yield return Case("IDE0024", """
            internal class C
            {
                public int Value;

                public static C operator +(C left, C right)
                {
                    return left;
                }
            }
            """, "public static C operator +(C left, C right) => left;");

        // IDE0025 — a get-only property.
        yield return Case("IDE0025", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get
                    {
                        return _value;
                    }
                }
            }
            """, "public int Value => _value;");

        // IDE0026 — an indexer with a single getter.
        yield return Case("IDE0026", """
            internal class C
            {
                private readonly int[] _items = { 1, 2, 3 };

                public int this[int index]
                {
                    get
                    {
                        return _items[index];
                    }
                }
            }
            """, "public int this[int index] => _items[index];");

        // IDE0027 — accessors of a property that keeps its get and set.
        yield return Case("IDE0027", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get
                    {
                        return _value;
                    }

                    set
                    {
                        _value = value;
                    }
                }
            }
            """, "get => _value;");

        // IDE0053 — a lambda whose block body is one statement.
        yield return Case("IDE0053", """
            internal class C
            {
                public System.Func<int, int> Twice() => value => { return value * 2; };
            }
            """, "value => value * 2");

        // IDE0061 — a local function whose body is one statement.
        yield return Case("IDE0061", """
            internal class C
            {
                public int F()
                {
                    int Twice(int value)
                    {
                        return value * 2;
                    }

                    return Twice(2);
                }
            }
            """, "int Twice(int value) => value * 2;");

        // IDE0045 — both branches assign different values to the same target.
        yield return Case("IDE0045", """
            internal class C
            {
                public int F(bool flag)
                {
                    int value;
                    if (flag)
                    {
                        value = 1;
                    }
                    else
                    {
                        value = 2;
                    }

                    return value;
                }
            }
            """, "value = flag ? 1 : 2;");

        // IDE0046 — both branches return.
        yield return Case("IDE0046", """
            internal class C
            {
                public int F(bool flag)
                {
                    if (flag)
                    {
                        return 1;
                    }
                    else
                    {
                        return 2;
                    }
                }
            }
            """, "return flag ? 1 : 2;");

        // IDE0017 — consecutive property assignments after the creation.
        yield return Case("IDE0017", """
            internal class C
            {
                public int A { get; set; }

                public int B { get; set; }

                public C Create()
                {
                    var created = new C();
                    created.A = 1;
                    created.B = 2;
                    return created;
                }
            }
            """, "var created = new C { A = 1, B = 2 };");

        // IDE0042 — the tuple is read once per element, and never again.
        yield return Case("IDE0042", """
            internal class C
            {
                public (int Low, int High) Range() => (1, 2);

                public int F()
                {
                    var range = Range();
                    var low = range.Item1;
                    var high = range.Item2;
                    return low + high;
                }
            }
            """, "var (low, high) = Range();");

        // IDE0066 — every section returns, and the switch is the whole body.
        yield return Case("IDE0066", """
            internal class C
            {
                public string F(int value)
                {
                    switch (value)
                    {
                        case 1:
                            return "one";
                        default:
                            return "many";
                    }
                }
            }
            """, "return value switch", "1 => \"one\"", "_ => \"many\"");

        // IDE0062 — a local function that uses only its own parameter.
        yield return Case("IDE0062", """
            internal class C
            {
                public int F()
                {
                    int Twice(int value)
                    {
                        return value * 2;
                    }

                    return Twice(2);
                }
            }
            """, "static int Twice(int value)");

        // IDE0070 — the hand-written hash fold becomes HashCode.Combine.
        yield return Case("IDE0070", """
            internal class C
            {
                private int _left;
                private int _right;

                public override int GetHashCode()
                {
                    var hash = 17;
                    hash = hash * 31 + _left.GetHashCode();
                    hash = hash * 31 + _right.GetHashCode();
                    return hash;
                }
            }
            """, "System.HashCode.Combine(_left, _right)");

        // IDE0260 — the null-conditional read of a constant becomes a property pattern.
        yield return Case("IDE0260", """
            internal class C
            {
                public bool F(object candidate) => (candidate as string)?.Length == 0;
            }
            """, "candidate is string { Length: 0 }");

        // IDE0320 — a lambda that captures nothing.
        yield return Case("IDE0320", """
            internal class C
            {
                public System.Func<int> Value() => () => 1;
            }
            """, "=> static () => 1");

        // IDE0380 — `unsafe` on a member with nothing unsafe inside.
        yield return Case("IDE0380", """
            internal class C
            {
                public unsafe int F() => 1;
            }
            """, "public int F() => 1;");

        // IDE0035 — the statements after the return can never run.
        yield return Case("IDE0035", """
            internal class C
            {
                public int F()
                {
                    return 1;
                    var x = 2;
                    return x;
                }
            }
            """, "return 1;");

        // IDE0010 — the switch handles one member of three, and there is no default.
        yield return Case("IDE0010", """
            internal enum Color
            {
                Red,
                Green,
                Blue,
            }

            internal class C
            {
                public int F(Color color)
                {
                    switch (color)
                    {
                        case Color.Red:
                            return 1;
                    }

                    return 0;
                }
            }
            """, "case Color.Green:", "case Color.Blue:");

        // IDE0072 — the missing members take the discard's own expression.
        yield return Case("IDE0072", """
            internal enum Color
            {
                Red,
                Green,
                Blue,
            }

            internal class C
            {
                public int F(Color color) => color switch
                {
                    Color.Red => 1,
                    _ => 0,
                };
            }
            """, "Color.Green => 0", "Color.Blue => 0");

        // IDE0120 — Where + a predicate operator folds into one call.
        yield return Case("IDE0120", """
            using System.Linq;

            internal class C
            {
                public bool F(int[] values) => values.Where(v => v > 0).Any();
            }
            """, "values.Any(v => v > 0)");

        // IDE0121 — a type test followed by a cast is OfType.
        yield return Case("IDE0121", """
            using System.Linq;

            internal class C
            {
                public string[] F(object[] values) => values.Where(o => o is string).Cast<string>().ToArray();
            }
            """, "values.OfType<string>()");

        // IDE0080 — the value is already known non-null, so `!` says nothing.
        yield return Case("IDE0080", """
            internal class C
            {
                public int F(string value) => value!.Length;
            }
            """, "value.Length");

        // IDE0058 — the call returns a value nobody reads.
        yield return Case("IDE0058", """
            internal class C
            {
                private int _value;

                public void F()
                {
                    _value.ToString();
                }
            }
            """, "_ = _value.ToString();");

        // IDE0240 — the project already enables nullable, so the directive changes nothing.
        yield return Case("IDE0240", """
            #nullable enable

            internal class C
            {
                public string F() => "x";
            }
            """, "internal class C");

        // IDE0390 — an async method that never awaits.
        yield return Case("IDE0390", """
            using System.Threading.Tasks;

            internal class C
            {
                public async Task<int> F()
                {
                    return 42;
                }
            }
            """, "public Task<int> F()", "System.Threading.Tasks.Task.FromResult(42)");

        // IDE0391 — the same fix on an override, which callers await through the base contract.
        yield return Case("IDE0391", """
            using System.Threading.Tasks;

            internal abstract class Base
            {
                public abstract Task<int> F();
            }

            internal class Derived : Base
            {
                public override async Task<int> F()
                {
                    return 1;
                }
            }
            """, "public override Task<int> F()", "Task.FromResult(1)");

        // IDE0059 — the first store is overwritten before anything reads it.
        yield return Case("IDE0059", """
            internal class C
            {
                public int F(int a, int b)
                {
                    int value = 0;
                    value = a;
                    value = b;
                    return value;
                }
            }
            """, "value = b;");
    }

    private static object[] Case(string id, string source, params string[] expected) => [id, source, expected];

    [Fact]
    public async Task SimplifyCode_RecordsWhatItWroteInTheSolutionHistory()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                public int F(int x) => (int)x;
            }
            """));
        using var store = new VisualMcpStore(workspace);

        var service = new SimplifyCodeService(workspace, store);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0004", dryRun: false);
        ResultAssert.Succeeded(result);

        var entry = Assert.Single(store.RecentEdits(10, workspace.PathOf("C.cs")));
        Assert.Equal("simplify_code", entry.Tool);
        Assert.Equal("IDE0004", entry.Rules);
        Assert.True(entry.ContentChanged);
        Assert.Contains("=> x;", workspace.Read("C.cs"));

        // A preview writes nothing, so it records nothing either.
        var preview = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0004", dryRun: true);
        ResultAssert.Succeeded(preview);
        Assert.Single(store.RecentEdits(10, workspace.PathOf("C.cs")));
    }

    [Fact]
    public async Task IDE0073_inserts_the_header_once_and_only_when_configured()
    {
        using var workspace = FakeWorkspace.CreateWithEditorConfig("""
            [*.cs]
            file_header_template = Copyright Contoso\nAll rights reserved
            """, ("C.cs", "internal class C { }"));

        var service = new SimplifyCodeService(workspace);

        // Without the option in the file's chain nothing happens.
        using var unconfigured = FakeWorkspace.Create(("C.cs", "internal class C { }"));
        var skipped = await new SimplifyCodeService(unconfigured)
            .SimplifyAsync("document", unconfigured.PathOf("C.cs"), null, "IDE0073", dryRun: true);
        Assert.Equal(0, ResultAssert.Json(skipped).GetProperty("totalEdits").GetInt32());

        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0073", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.StartsWith("// Copyright Contoso", text, StringComparison.Ordinal);
        Assert.Contains("// All rights reserved", text, StringComparison.Ordinal);
        Assert.Contains("internal class C { }", text, StringComparison.Ordinal);

        // A second pass sees the header and leaves the file alone.
        var again = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0073", dryRun: false);
        Assert.Equal(0, ResultAssert.Json(again).GetProperty("filesChanged").GetInt32());
        Assert.Equal(text, workspace.Read("C.cs"));
    }

    [Fact]
    public async Task IDE0079_removes_the_attribute_and_keeps_one_the_diagnostic_still_needs()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Diagnostics.CodeAnalysis;

            internal class C
            {
                [SuppressMessage("Style", "CS0168", Justification = "nothing to suppress")]
                public int F() => 1;

                [SuppressMessage("Compiler", "CS0168", Justification = "an unused variable is declared")]
                public int G()
                {
                    int unused;
                    return 2;
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0079", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");

        // The suppression over F suppressed nothing, so it is gone...
        Assert.Equal(1, text.Split("SuppressMessage").Length - 1);

        // ...and the one over G still has a CS0168 to suppress, so it stays.
        var unused = text.IndexOf("public int G()", StringComparison.Ordinal);
        Assert.Contains("SuppressMessage", text[..unused], StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuleCases))]
    public async Task SimplifyCode_AppliesTheRule(string id, string source, string[] expected)
    {
        using var workspace = FakeWorkspace.Create(("C.cs", source));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, id, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.True(json.GetProperty("refused").GetArrayLength() == 0, json.GetRawText());
        Assert.Equal(1, json.GetProperty("filesChanged").GetInt32());
        Assert.All(json.GetProperty("edits").EnumerateArray(),
            edit => Assert.Equal(id, edit.GetProperty("rule").GetString()));

        var text = workspace.Read("C.cs");
        foreach (var fragment in expected)
            Assert.Contains(fragment, text);
    }

    [Fact]
    public async Task SimplifyCode_EveryCaseInTheTableRunsWithAllRulesOn()
    {
        // Running the whole catalogue over every case must not lose any of them: this is what
        // "rules=all" does on a real project.
        foreach (var data in RuleCases())
        {
            var id = (string)data[0];
            var source = (string)data[1];
            using var workspace = FakeWorkspace.Create(("C.cs", source));

            var service = new SimplifyCodeService(workspace);
            try
            {
                var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: true);
                ResultAssert.Succeeded(result);
            }
            catch (Exception exception)
            {
                throw new Xunit.Sdk.XunitException($"{id} failed with all rules on: {exception}");
            }
        }
    }

    [Fact]
    public void SimplifyCode_DoesNotClaimRulesItCannotRewrite()
    {
        var supported = SimplifyCodeService.Rules.Select(rule => rule.Id).ToList();

        // Every id in Microsoft's table is either supported or listed with a reason — never missing.
        Assert.Contains("IDE0028", supported);
        Assert.Contains("IDE0041", supported);
        Assert.DoesNotContain("IDE1005", supported);
        Assert.Contains(SimplifyCodeService.NotRewritten, entry => entry.Id == "IDE1005");

        var all = supported.Concat(SimplifyCodeService.NotRewritten.Select(entry => entry.Id)).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SimplifyCode_ExplainsAnIdItDoesNotRewrite()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE1006", dryRun: true);

        var json = ResultAssert.Json(result);
        Assert.Contains("not rewritable", json.GetProperty("error").GetString());
        Assert.Contains(json.GetProperty("notRewritten").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "IDE1006" &&
                     entry.GetProperty("reason").GetString()!.Contains("naming"));
    }

    [Fact]
    public async Task SimplifyCode_RewritesEveryMemberOfAFileInOneRun()
    {
        // Expression rewrites rebuild the whole tree, so a statement match found before them pointed at
        // a node that no longer existed and its splice silently did nothing: the same edit was then
        // reported twice, once per pass, and the file needed the extra pass to converge.
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public C(int value)
                {
                    _value = value;
                }

                public int Value
                {
                    get
                    {
                        return _value;
                    }

                    set
                    {
                        _value = value;
                    }
                }

                public int Twice()
                {
                    return _value * 2;
                }

                public int Run()
                {
                    int Square(int value)
                    {
                        return value * value;
                    }

                    return Square(_value);
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("public C(int value) => _value = value;", text);
        Assert.Contains("get => _value;", text);
        Assert.Contains("set => _value = value;", text);
        Assert.Contains("public int Twice() => _value * 2;", text);

        // The local function gets both suggestions: the expression body first, then `static`.
        Assert.Contains("static int Square(int value) => value * value;", text);

        // One edit per change: a rule reported twice means a pass replaced nothing.
        var json = ResultAssert.Json(result);
        Assert.Equal(6, json.GetProperty("totalEdits").GetInt32());
        Assert.Equal(6, json.GetProperty("edits").GetArrayLength());
    }

    [Fact]
    public async Task SimplifyCode_RemovesAPrivateMemberNothingReferences()
    {
        // The answer is outside the document: the rule asks the whole solution through the symbol finder.
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int Unused() => 1;

                public int Used() => Helper();

                private int Helper() => 2;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0051", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("Unused", text);
        Assert.Contains("private int Helper() => 2;", text);
        Assert.Contains("public int Used() => Helper();", text);
    }

    [Fact]
    public async Task SimplifyCode_RemovesAFieldThatIsWrittenButNeverRead()
    {
        // The classifier says every reference is a store, and the removal has to take the store with it
        // or the file stops compiling.
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _unread;

                public void Set(int value)
                {
                    _unread = value;
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0052", dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.DoesNotContain("_unread", text);
        Assert.Contains("public void Set(int value)", text);
        Assert.Contains("}", text);
    }

    [Fact]
    public async Task SimplifyCode_RemovesAnUnusedParameterAcrossFiles()
    {
        // The declaration and the call site live in different documents of the same partial type: one
        // suggestion, two files, compiled and written together.
        using var workspace = FakeWorkspace.Create(
            ("Helper.cs", """
                internal partial class Helper
                {
                    public int Value = 2;

                    private int Twice(int value, int unused)
                    {
                        return value * 2;
                    }
                }
                """),
            ("Caller.cs", """
                internal partial class Helper
                {
                    public int Run() => Twice(Value, 99);
                }
                """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("Helper.cs"), null, "IDE0060", dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(2, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(1, json.GetProperty("totalEdits").GetInt32());

        Assert.Contains("private int Twice(int value)", workspace.Read("Helper.cs"));
        Assert.Contains("Twice(Value)", workspace.Read("Caller.cs"));
        Assert.DoesNotContain("unused", workspace.Read("Helper.cs"));
        Assert.DoesNotContain("99", workspace.Read("Caller.cs"));
    }

    [Fact]
    public async Task SimplifyCode_DoesNotMakeAMutableFieldReadonly()
    {
        // The readonly guess used to be left to the compiler, which then refused the whole file: a
        // field written from a setter made every other suggestion in it disappear.
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public int Value
                {
                    get => _value;
                    set => _value = value;
                }
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0044", dryRun: true);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(0, json.GetProperty("refused").GetArrayLength());
    }

    [Fact]
    public async Task SimplifyCode_AddsReadonlyToAFieldOnlyWrittenInTheConstructor()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            internal class C
            {
                private int _value;

                public C(int value)
                {
                    _value = value;
                }

                public int Value => _value;
            }
            """));

        var service = new SimplifyCodeService(workspace);
        await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0044", dryRun: false);

        Assert.Contains("private readonly int _value;", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_SimplifiesTheCollectionCreationFromTheQuickFixPopup()
    {
        // The exact shape of the IDE0028 popup: a coalesce whose right side is a new empty collection.
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}

            namespace Probe;

            internal class C
            {
                public List<int> Rows(List<int>? rows) => rows ?? new List<int>();
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("rows ?? []", text);
        Assert.DoesNotContain("new List<int>()", text);

        var json = ResultAssert.Json(result);
        Assert.Equal(1, json.GetProperty("totalEdits").GetInt32());
        Assert.Equal("IDE0028", json.GetProperty("edits")[0].GetProperty("rule").GetString());
        Assert.Equal("new List<int>()", json.GetProperty("edits")[0].GetProperty("before").GetString());
        Assert.Equal("[]", json.GetProperty("edits")[0].GetProperty("after").GetString());
    }

    [Fact]
    public async Task SimplifyCode_KeepsTheElementsAndTheirSpacing()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}

            namespace Probe;

            internal class C
            {
                public List<int> Values() => new List<int> { 1, 2, 3 };

                public int[] Bytes() => new[] { 4, 5 };

                public string[] Empty() => new string[0];
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("solution", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var text = workspace.Read("C.cs");
        Assert.Contains("=> [1, 2, 3];", text);
        Assert.Contains("=> [4, 5];", text);
        Assert.Contains("=> [];", text);
    }

    [Fact]
    public async Task SimplifyCode_SimplifiesTargetTypedNew()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", """
            using System.Text;

            namespace Probe;

            internal class C
            {
                public StringBuilder Buffer() => new StringBuilder(64);
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0090", dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Contains("=> new(64);", workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_RefusesARewriteTheCompilerRejects()
    {
        // With 'var' there is no target type: "var rows = []" does not compile, so the gate must win
        // and the file must stay exactly as it was.
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}

            namespace Probe;

            internal class C
            {
                public int Count()
                {
                    var rows = new List<int>();
                    return rows.Count + 1;
                }
            }
            """));

        var before = workspace.Read("C.cs");

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("filesChanged").GetInt32());
        Assert.Equal(1, json.GetProperty("refused").GetArrayLength());
        Assert.Contains("compiler errors", json.GetProperty("refused")[0].GetProperty("reason").GetString());
        Assert.Equal(before, workspace.Read("C.cs"));
    }

    [Fact]
    public async Task SimplifyCode_LeavesTypesOutsideTheCuratedListAlone()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}

            namespace Probe;

            internal class C
            {
                public Dictionary<string, int> Map() => new Dictionary<string, int>();

                public List<int> Custom() => new List<int>();
            }
            """));

        var service = new SimplifyCodeService(workspace);
        ResultAssert.Succeeded(await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false));

        var text = workspace.Read("C.cs");
        // A dictionary is not a collection-expression target: it must not become [], only target-typed
        // new may apply to it (IDE0090), which is what "new()" here means.
        Assert.DoesNotContain("Map() => []", text);
        Assert.Contains("Dictionary<string, int> Map() => new();", text);
        Assert.Contains("Custom() => [];", text);
    }

    [Fact]
    public async Task SimplifyCode_ProjectScopeRewritesEveryFile()
    {
        using var workspace = FakeWorkspace.Create(
            ("A.cs", $$"""
                {{Header}}

                namespace Probe;

                internal class A
                {
                    public List<string> Names() => new List<string>();
                }
                """),
            ("B.cs", $$"""
                {{Header}}

                namespace Probe;

                internal class B
                {
                    public List<string> Tags() => new List<string>();
                }
                """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("project", null, "Probe", null, dryRun: false);
        ResultAssert.Succeeded(result);

        Assert.Equal(2, ResultAssert.Json(result).GetProperty("filesChanged").GetInt32());
        Assert.Contains("=> [];", workspace.Read("A.cs"));
        Assert.Contains("=> [];", workspace.Read("B.cs"));
    }

    [Fact]
    public async Task SimplifyCode_RuleFilterAppliesOnlyThatRule()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}
            using System.Text;

            namespace Probe;

            internal class C
            {
                public StringBuilder Buffer() => new StringBuilder();

                public List<int> Rows() => new List<int>();
            }
            """));

        var service = new SimplifyCodeService(workspace);
        ResultAssert.Succeeded(await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, "IDE0028", dryRun: false));

        var text = workspace.Read("C.cs");
        Assert.Contains("=> [];", text);
        Assert.Contains("new StringBuilder()", text);
    }

    [Fact]
    public async Task SimplifyCode_RespectsTheTargetTypeOfTheCollectionExpression()
    {
        // IEnumerable<int> can hold a collection expression; object cannot — and asking for it would
        // make the gate refuse the whole file, losing the good suggestion next to it.
        using var workspace = FakeWorkspace.Create(("C.cs", $$"""
            {{Header}}

            namespace Probe;

            internal class C
            {
                public IEnumerable<int> Items() => new List<int>();

                public object Boxed() => new List<int>();
            }
            """));

        var service = new SimplifyCodeService(workspace);
        var result = await service.SimplifyAsync("document", workspace.PathOf("C.cs"), null, null, dryRun: false);
        ResultAssert.Succeeded(result);

        var json = ResultAssert.Json(result);
        Assert.Equal(0, json.GetProperty("refused").GetArrayLength());
        Assert.Equal(1, json.GetProperty("totalEdits").GetInt32());

        var text = workspace.Read("C.cs");
        Assert.Contains("IEnumerable<int> Items() => [];", text);
        Assert.Contains("object Boxed() => new List<int>();", text);
    }

    [Fact]
    public async Task SimplifyCode_RejectsAnUnknownRuleAndUnknownScope()
    {
        using var workspace = FakeWorkspace.Create(("C.cs", "internal class C { }"));

        var service = new SimplifyCodeService(workspace);

        Assert.Contains("not rewritable", ResultAssert.Error(
            await service.SimplifyAsync("solution", null, null, "IDE9999", dryRun: true)));

        Assert.Contains("scope must be", ResultAssert.Error(
            await service.SimplifyAsync("everything", null, null, null, dryRun: true)));
    }
}
