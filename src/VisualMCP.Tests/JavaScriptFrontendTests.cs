using Snitch.Metacode;
using Snitch.Metacode.Frontends;
using Snitch.Metacode.Rendering;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// The JavaScript reader: a module with imports, a top-level constant, an exported function and an
/// exported class, then a loop that runs at load time. The point of the file is the split the model
/// needs — declarations stay declarations, everything a script executes at the top level lives in
/// <c>__module__</c> — plus the constructs that make JavaScript what it is: a destructured loop head, an
/// arrow function, a template literal, private class members and an accessor pair.
/// </summary>
public sealed class JavaScriptFrontendTests
{
    private const string Source = """
        import { readFile } from 'node:fs/promises';
        import path from 'path';

        const LIMIT = 10;

        export function classify(value) {
            if (value > LIMIT) {
                return 'big';
            }
            return value === 0 ? 'zero' : 'small';
        }

        export class Counter {
            #count = 0;
            static label = 'counter';

            constructor(start = 0) {
                this.#count = start;
            }

            get value() {
                return this.#count;
            }

            set value(next) {
                this.#count = next;
            }

            increment(step) {
                this.#count += step;
                return this;
            }
        }

        for (const item of [1, 2, 3]) {
            console.log(`item ${item}`);
        }
        """;

    [Fact]
    public void A_module_becomes_its_declarations_plus_one_entry_point()
    {
        var document = new JavaScriptFrontend().Translate(Source, "Sample.js");

        Assert.Equal(MetacodeLanguage.JavaScript, document.Language);
        Assert.Equal("Sample", Assert.Single(document.Modules).Name);

        var declarations = document.Modules[0].Declarations;

        // The top-level constant is a member of the module, not a statement of it.
        var limit = Assert.IsType<MetacodeField>(declarations.Single(d => d is MetacodeField));
        Assert.Equal("LIMIT", limit.Name);
        Assert.True(limit.Modifiers.HasFlag(MetacodeModifiers.Const));

        var classify = Assert.IsType<MetacodeFunction>(
            declarations.Single(d => d is MetacodeFunction function && function.Name == "classify"));
        Assert.Equal("classify", classify.Name);
        Assert.Equal(MetacodeFunctionKind.Function, classify.Kind);
        Assert.Single(classify.ParameterList);
        Assert.Equal("value", classify.ParameterList[0].Name);

        // The executable part of the file — the imports and the loop — is the body of __module__.
        var entry = Assert.IsType<MetacodeFunction>(declarations[0]);
        Assert.Equal("__module__", entry.Name);
        Assert.Equal(2, entry.Body!.Statements.OfType<MetacodeImport>().Count());
        Assert.Single(entry.Body.Statements.OfType<MetacodeForEach>());
    }

    [Fact]
    public void A_class_keeps_its_fields_methods_and_accessors()
    {
        var document = new JavaScriptFrontend().Translate(Source, "Sample.js");
        var type = Assert.IsType<MetacodeTypeDeclaration>(
            document.Modules[0].Declarations.Single(d => d is MetacodeTypeDeclaration));

        Assert.Equal("Counter", type.Name);
        Assert.Equal(MetacodeTypeKind.Class, type.Kind);

        var priv = Assert.Single(type.Members.OfType<MetacodeField>(), field => field.Name == "#count");
        Assert.Equal("0", Assert.IsType<MetacodeLiteral>(priv.Initializer).Text);

        var label = Assert.Single(type.Members.OfType<MetacodeField>(), field => field.Name == "label");
        Assert.True(label.Modifiers.HasFlag(MetacodeModifiers.Static));

        var constructor = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Kind == MetacodeFunctionKind.Constructor);
        Assert.Equal("0", Assert.IsType<MetacodeLiteral>(constructor.ParameterList[0].DefaultValue).Text);

        // `get value` and `set value` are two accessors of ONE member, which is what the model's
        // property is: the second accessor fills the first member in instead of adding another.
        var value = Assert.Single(type.Members.OfType<MetacodeProperty>());
        Assert.Equal("value", value.Name);
        Assert.NotNull(value.Getter?.Body);
        Assert.NotNull(value.Setter?.Body);
        Assert.Equal("next", value.Setter!.ParameterName);

        var increment = Assert.Single(type.Members.OfType<MetacodeFunction>(), m => m.Name == "increment");
        var assignment = Assert.IsType<MetacodeAssignment>(increment.Body!.Statements[0]);
        Assert.Equal(MetacodeAssignmentOperator.Add, assignment.Operator);
    }

    [Fact]
    public void The_loop_head_and_the_template_literal_are_lowered_not_quoted()
    {
        var document = new JavaScriptFrontend().Translate(Source, "Sample.js");
        var entry = (MetacodeFunction)document.Modules[0].Declarations[0];

        var loop = Assert.IsType<MetacodeForEach>(entry.Body!.Statements.OfType<MetacodeForEach>().Single());
        Assert.Equal("item", loop.VariableName);
        Assert.False(loop.IsAwait);
        Assert.IsType<MetacodeCollection>(loop.Iterable);

        var call = Assert.IsType<MetacodeExpressionStatement>(loop.Body.Statements[0]);
        var invocation = Assert.IsType<MetacodeInvocation>(call.Expression);
        var template = Assert.IsType<MetacodeInterpolatedString>(invocation.ArgumentList[0].Value);

        // The parts come from the file: the literal chunk as the author wrote it, then the expression.
        Assert.Equal("item ", template.Parts[0].Text);
        Assert.Equal("item", Assert.IsType<MetacodeIdentifier>(template.Parts[1].Expression).Name);
    }

    [Fact]
    public void The_canonical_text_reads_like_the_other_languages()
    {
        var document = MetacodeTranslator.Default.Translate(MetacodeSource.FromJavaScript(Source, "Sample.js"));
        var text = new PseudocodeRenderer().Render(document);

        Assert.Contains("MODULE Sample", text);
        Assert.Contains("CLASS Counter", text);
        Assert.Contains("FUNCTION classify", text);
        Assert.Contains("FOREACH item IN", text);
        Assert.Contains("FROM 'node:fs/promises' IMPORT readFile", text);

        // An arrow-free file still produces no diagnostics beyond the informational re-export note,
        // which this file does not have: every construct here has a node.
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void The_same_algorithm_prints_the_same_body_as_csharp()
    {
        // The operational definition of "one model": the same loop, written twice, must produce the same
        // pseudocode below the header.
        const string js = """
            function sum(n) {
                let total = 0;
                for (let i = 0; i < n; i++) {
                    if (i % 2 === 0) {
                        total += i;
                    }
                }
                return total;
            }
            """;

        // `var` on the C# side, because a declared type is a difference between the languages and not one
        // between the trees: with it the two bodies must be identical, line for line.
        const string cs = """
            class Math {
                int Sum(int n)
                {
                    var total = 0;
                    for (var i = 0; i < n; i++)
                    {
                        if (i % 2 == 0)
                        {
                            total += i;
                        }
                    }
                    return total;
                }
            }
            """;

        Assert.Equal(BodyLines(MetacodeSource.FromJavaScript(js, "sum.js")), BodyLines(MetacodeSource.FromCSharp(cs, "Math.cs")));
        return;

        // The container lines differ by design (a C# method declares its types and sits in a class), so the
        // comparison is the statements, whitespace-insensitive.
        static List<string> BodyLines(MetacodeSource source) => MetacodeTranslator.Default
            .Render(source)
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !IsContainerLine(line))
            .ToList();

        static bool IsContainerLine(string line) =>
            line.StartsWith("MODULE ", StringComparison.Ordinal)
            || line.StartsWith("CLASS ", StringComparison.Ordinal)
            || line.StartsWith("METHOD ", StringComparison.Ordinal)
            || line.StartsWith("FUNCTION ", StringComparison.Ordinal)
            || line is "END MODULE" or "END CLASS" or "END METHOD" or "END FUNCTION";
    }

    [Fact]
    public void A_class_keeps_the_type_it_extends()
    {
        var document = new JavaScriptFrontend().Translate(
            "class Base {}\nclass Derived extends Base {}\n",
            "types.js");

        var derived = document.Modules[0].Declarations
            .OfType<MetacodeTypeDeclaration>()
            .Single(type => type.Name == "Derived");

        Assert.Equal("Base", Assert.Single(derived.BaseTypes!).Name);
    }

    [Fact]
    public void A_commonjs_script_is_read_when_the_module_form_is_not_valid()
    {
        // A top-level `return` and a `with` block are not ECMAScript modules; the reader falls back to the
        // classic-script grammar instead of reporting a file that Node runs every day.
        const string source = """
            function make(o) {
                with (o) {
                    return value;
                }
            }
            """;

        var document = new JavaScriptFrontend().Translate(source, "legacy.cjs");

        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.SyntaxError);

        var function = Assert.IsType<MetacodeFunction>(document.Modules[0].Declarations[0]);
        Assert.IsType<MetacodeOpaqueStatement>(function.Body!.Statements[0]);
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == MetacodeDiagnosticCodes.UnsupportedStatement);
    }

    [Fact]
    public void Jsx_and_typescript_annotations_are_reported_where_they_are()
    {
        // Neither is ECMAScript: the reader must say so and name the line, not fail silently or produce a
        // tree that pretends the code was read.
        var jsx = new JavaScriptFrontend().Translate("const App = () => <div>hi</div>;\n", "App.jsx");
        Assert.Empty(jsx.Modules);
        var error = Assert.Single(jsx.Diagnostics);
        Assert.Equal(MetacodeDiagnosticCodes.SyntaxError, error.Code);
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);

        var typed = new JavaScriptFrontend().Translate("function add(a: number) { return a; }\n", "typed.js");
        Assert.Empty(typed.Modules);
        Assert.Equal(MetacodeDiagnosticCodes.SyntaxError, Assert.Single(typed.Diagnostics).Code);
    }

    [Fact]
    public void ADotJs_path_selects_the_reader_by_extension()
    {
        var path = Path.Combine(Path.GetTempPath(), "vmcp-js-frontend-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, "function twice(x) { return x * 2; }\n");

        try
        {
            var source = MetacodeSource.FromFile(path);
            Assert.Equal(MetacodeLanguage.JavaScript, source.Language);

            var document = MetacodeTranslator.Default.Translate(source);
            Assert.Equal(MetacodeLanguage.JavaScript, document.Language);
            Assert.Empty(document.Diagnostics);
            Assert.Equal("js", PseudocodeRenderer.LanguageTag(document.Language));
            Assert.Equal(MetacodeLanguage.JavaScript, PseudocodeRenderer.LanguageFromTag("js"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_comment_is_kept_as_the_statement_it_precedes()
    {
        const string source = """
            function run() {
                // the first value
                let a = 1;
                return a;
            }
            """;

        var document = new JavaScriptFrontend().Translate(source, "run.js");
        var function = Assert.IsType<MetacodeFunction>(document.Modules[0].Declarations[0]);

        Assert.Equal("the first value", Assert.IsType<MetacodeComment>(function.Body!.Statements[0]).Text);
        Assert.IsType<MetacodeLocalVariable>(function.Body.Statements[1]);
    }

    [Fact]
    public void Every_declaration_keeps_the_line_it_came_from()
    {
        var document = new JavaScriptFrontend().Translate(Source, "Sample.js");
        var classify = document.Modules[0].Declarations.Single(d => d is MetacodeFunction function && function.Name == "classify");

        Assert.Equal(MetacodeLanguage.JavaScript, classify.Origin.Language);
        Assert.Equal("Sample.js", classify.Origin.Document);
        Assert.Equal(6, classify.Origin.StartLine);   // `export function classify(value) {`
        Assert.Equal(8, classify.Origin.StartColumn); // the node starts at `function`, after `export `
    }
}
