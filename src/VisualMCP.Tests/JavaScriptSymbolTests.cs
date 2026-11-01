using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.JavaScript;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// js_symbols: the lexical symbol table over JavaScript, TypeScript and JSX. The cases are the questions
/// a symbol table exists to answer — where a name is declared, what a name refers to, which module imports
/// it, and what nothing reads — plus the shapes that decide the answer (a parameter shadowing a module
/// function, a destructured binding, a class member, a JSX component tag).
/// </summary>
public sealed class JavaScriptSymbolTests
{
    private const string Module = """
        import { readFile } from 'node:fs/promises';

        export const LIMIT = 10;

        export function classify(value) {
            if (value > LIMIT) {
                return 'big';
            }
            return value === 0 ? 'zero' : 'small';
        }

        export class Counter {
            #count = 0;

            constructor(start = 0) {
                this.#count = start;
            }

            increment(step) {
                this.#count += step;
                return this;
            }
        }

        export function unusedHelper(a) {
            const { first, second: renamed } = a;
            return first + renamed;
        }
        """;

    [Fact]
    public void A_module_declares_its_functions_classes_and_bindings()
    {
        var symbols = JavaScriptSymbols.Scan(Module, "Sample.js");

        var classify = Assert.Single(symbols.Declarations, declaration => declaration.Name == "classify");
        Assert.Equal(JsSymbolKind.Function, classify.Kind);
        Assert.Equal(new[] { "classify" }, classify.ExportedAs);
        Assert.Equal("module", classify.Container);

        var limit = Assert.Single(symbols.Declarations, declaration => declaration.Name == "LIMIT");
        Assert.Equal(JsSymbolKind.Variable, limit.Kind);
        Assert.Contains("LIMIT", symbols.Exports);

        var counter = Assert.Single(symbols.Declarations, declaration => declaration.Name == "Counter");
        Assert.Equal(JsSymbolKind.Class, counter.Kind);

        // Class members, including a private one, belong to the class scope.
        var increment = Assert.Single(symbols.Declarations, declaration => declaration.Name == "increment");
        Assert.Equal(JsSymbolKind.Method, increment.Kind);
        Assert.Equal("Counter", increment.Container);

        var count = Assert.Single(symbols.Declarations, declaration => declaration.Name == "#count");
        Assert.Equal(JsSymbolKind.Field, count.Kind);

        // The parameter of constructor and increment is a declaration of the body scope.
        Assert.Contains(symbols.Declarations, declaration => declaration.Name == "start" && declaration.Kind == JsSymbolKind.Parameter);
        Assert.Contains(symbols.Declarations, declaration => declaration.Name == "step" && declaration.Kind == JsSymbolKind.Parameter);

        // The import binding is a declaration of the module scope.
        var import = Assert.Single(symbols.Declarations, declaration => declaration.Kind == JsSymbolKind.Import);
        Assert.Equal("readFile", import.Name);
        Assert.Equal("node:fs/promises", Assert.Single(symbols.Imports).Module);
    }

    [Fact]
    public void A_use_resolves_to_the_declaration_in_its_scope()
    {
        const string source = """
            function read(x) {
                return x + 1;
            }

            function outer(flag) {
                const read = (value) => value * 2;
                return read(flag);
            }
            """;

        var symbols = JavaScriptSymbols.Scan(source, "scope.js");

        // `read` inside outer resolves to the local const, not to the module function.
        var local = symbols.Declarations.Single(declaration => declaration.Name == "read" && declaration.Kind == JsSymbolKind.Variable);
        var call = Assert.Single(symbols.Uses, occurrence => occurrence.Name == "read" && occurrence.Offset > local.Offset);
        Assert.Equal(IndexOf(symbols.Declarations, local), call.Declaration);
        Assert.Equal("call", call.Kind);

        // The arrow parameter is a declaration; its body reads it, and the outer parameter is its own.
        var value = Assert.Single(symbols.Declarations, declaration => declaration.Name == "value");
        Assert.Equal(JsSymbolKind.Parameter, value.Kind);
        Assert.Equal(IndexOf(symbols.Declarations, value),
            Assert.Single(symbols.Uses, occurrence => occurrence.Name == "value").Declaration);

        var flag = Assert.Single(symbols.Declarations, declaration => declaration.Name == "flag");
        Assert.Equal(IndexOf(symbols.Declarations, flag),
            Assert.Single(symbols.Uses, occurrence => occurrence.Name == "flag").Declaration);
    }

    [Fact]
    public void A_destructured_binding_is_a_declaration_and_the_key_is_not()
    {
        const string source = """
            const { first, second: renamed, third = 3 } = input;
            console.log(first, renamed, third);
            """;

        var symbols = JavaScriptSymbols.Scan(source, "bind.js");
        var names = symbols.Declarations.Select(declaration => declaration.Name).ToList();

        Assert.Contains("first", names);
        Assert.Contains("renamed", names);
        Assert.Contains("third", names);
        Assert.DoesNotContain("second", names);

        // `input` is read; it is not a declaration of the pattern.
        Assert.Contains(symbols.Uses, occurrence => occurrence.Name == "input" && occurrence.Declaration == -1);
    }

    [Fact]
    public void A_typescript_interface_is_a_declaration()
    {
        const string source = """
            export interface Order {
                id: string;
                total: number;
            }

            export type OrderId = string;
            """;

        var symbols = JavaScriptSymbols.Scan(source, "orders.ts");

        Assert.True(symbols.TypeScript);
        Assert.Equal(JsSymbolKind.Interface,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "Order").Kind);
        Assert.Equal(JsSymbolKind.TypeAlias,
            Assert.Single(symbols.Declarations, declaration => declaration.Name == "OrderId").Kind);
    }

    [Fact]
    public void A_jsx_component_tag_is_a_reference()
    {
        const string source = """
            import { Counter } from './Counter';

            export function App() {
                return <Counter step={2} />;
            }
            """;

        var symbols = JavaScriptSymbols.Scan(source, "App.jsx");

        Assert.True(symbols.Jsx);
        var binding = Assert.Single(symbols.Declarations, declaration => declaration.Kind == JsSymbolKind.Import);
        Assert.Equal("Counter", binding.Name);

        var tag = Assert.Single(symbols.Uses, occurrence => occurrence.Name == "Counter");
        Assert.Equal(IndexOf(symbols.Declarations, binding), tag.Declaration);
    }

    [Fact]
    public async Task The_service_follows_an_import_to_the_exporting_module()
    {
        using var files = new Scripts(
            ("app.js", """
                export function classify(value) {
                    return value > 0 ? 'big' : 'small';
                }

                export const LIMIT = 5;
                """),
            ("main.js", """
                import { classify, LIMIT } from './app.js';

                console.log(classify(LIMIT));
                """));

        var result = await Service.SymbolsAsync("refs", "classify", null, files.Root, false, 100);
        var json = Json(result);

        Assert.Equal("refs", json.GetProperty("action").GetString());
        var symbol = Assert.Single(json.GetProperty("symbols").EnumerateArray());
        Assert.Equal("app.js", symbol.GetProperty("file").GetString());

        var references = symbol.GetProperty("references").EnumerateArray().ToList();
        var external = Assert.Single(references, reference => reference.GetProperty("file").GetString() == "main.js");
        Assert.Equal("call", external.GetProperty("kind").GetString());
        Assert.Contains("./app.js", external.GetProperty("via").GetString());
    }

    [Fact]
    public async Task The_service_finds_a_name_and_reports_a_dead_one()
    {
        using var files = new Scripts(
            ("app.js", """
                export function used() { return 1; }

                function dead() { return 2; }

                console.log(used());
                """));

        var found = Json(await Service.SymbolsAsync("find", "used", null, files.Root, false, 100));
        var match = Assert.Single(found.GetProperty("matches").EnumerateArray());
        Assert.Equal(1, match.GetProperty("internalReferences").GetInt32());

        var unused = Json(await Service.SymbolsAsync("unused", null, null, files.Root, false, 100));
        var dead = Assert.Single(unused.GetProperty("candidates").EnumerateArray());
        Assert.Equal("dead", dead.GetProperty("name").GetString());
        Assert.False(dead.GetProperty("exported").GetBoolean());

        // An export nothing imports is also a candidate, and the answer says why.
        const string only = """
            export function orphan() { return 1; }
            """;
        using var single = new Scripts(("only.js", only));
        var singleUnused = Json(await Service.SymbolsAsync("unused", null, null, single.Root, false, 100));
        var orphan = Assert.Single(singleUnused.GetProperty("candidates").EnumerateArray());
        Assert.True(orphan.GetProperty("exported").GetBoolean());
    }

    [Fact]
    public async Task The_outline_reports_the_declarations_of_one_file()
    {
        using var files = new Scripts(("app.js", """
            export const LIMIT = 10;

            export function classify(value) {
                return value > LIMIT;
            }
            """));

        var outline = Json(await Service.SymbolsAsync("outline", null, "app.js", files.Root, false, 100));

        Assert.Equal("app.js", outline.GetProperty("file").GetString());
        var names = outline.GetProperty("declarations").EnumerateArray()
            .Select(declaration => declaration.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("LIMIT", names);
        Assert.Contains("classify", names);
        Assert.Contains("LIMIT", outline.GetProperty("exports").EnumerateArray().Select(value => value.GetString()!));
    }

    [Fact]
    public async Task A_missing_name_is_a_written_refusal()
    {
        using var files = new Scripts(("app.js", "export const LIMIT = 1;"));

        var refs = Json(await Service.SymbolsAsync("refs", "nope", null, files.Root, false, 100));
        Assert.Contains("No declaration named 'nope'", refs.GetProperty("error").GetString());

        var outline = Json(await Service.SymbolsAsync("outline", null, "nope.js", files.Root, false, 100));
        Assert.Contains("not a script of the scan", outline.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Rename_rewrites_the_declaration_the_uses_and_the_import_binding()
    {
        using var files = new Scripts(
            ("app.js", """
                export function classify(value) {
                    return value > 0 ? 'big' : 'small';
                }
                """),
            ("main.js", """
                import { classify } from './app.js';

                console.log(classify(1));
                """));

        var result = await Service.SymbolsAsync("rename", "classify", null, files.Root, false, 100,
            newName: "categorize", dryRun: false);

        Assert.True(Json(result).GetProperty("applied").GetBoolean());
        Assert.Contains("function categorize", File.ReadAllText(Path.Combine(files.Root, "app.js")));

        var main = File.ReadAllText(Path.Combine(files.Root, "main.js"));
        Assert.Contains("import { categorize } from './app.js';", main);
        Assert.Contains("console.log(categorize(1));", main);
    }

    [Fact]
    public async Task A_tsconfig_alias_is_followed_to_the_declaring_module()
    {
        using var files = new Scripts(
            ("tsconfig.json", """
                {
                    // TypeScript allows comments and a trailing comma in its own config.
                    "compilerOptions": {
                        "baseUrl": ".",
                        "paths": { "@/*": ["./*"] },
                    },
                }
                """),
            ("app.js", "export function classify(value) { return value > 0; }"),
            ("main.js", """
                import { classify } from '@/app';

                console.log(classify(1));
                """));

        var result = await Service.SymbolsAsync("refs", "classify", null, files.Root, false, 100);
        var symbol = Assert.Single(Json(result).GetProperty("symbols").EnumerateArray());

        var external = Assert.Single(symbol.GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("file").GetString() == "main.js");
        Assert.Equal("call", external.GetProperty("kind").GetString());
        Assert.Contains("@/app", external.GetProperty("via").GetString());
    }

    [Fact]
    public async Task A_package_import_resolves_through_node_modules_and_a_missing_one_does_not()
    {
        using var files = new Scripts(
            ("node_modules/greeting/package.json", """{ "main": "lib/index.js" }"""),
            ("node_modules/greeting/lib/index.js", "export function greet(name) { return 'hi ' + name; }"),
            ("main.js", """
                import { greet } from 'greeting';
                import { nothing } from 'nothing-here';

                console.log(greet('x'), nothing);
                """));

        var outline = Json(await Service.SymbolsAsync("outline", null, "main.js", files.Root, false, 100));
        var imports = outline.GetProperty("imports").EnumerateArray().ToList();

        Assert.True(Assert.Single(imports, entry => entry.GetProperty("module").GetString() == "greeting")
            .GetProperty("resolved").GetBoolean());
        Assert.False(Assert.Single(imports, entry => entry.GetProperty("module").GetString() == "nothing-here")
            .GetProperty("resolved").GetBoolean());
    }

    private static JavaScriptSymbolService Service => new(new NullWorkspace());

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);

    private static int IndexOf(IReadOnlyList<JsSymbolDeclaration> declarations, JsSymbolDeclaration declaration)
    {
        for (var index = 0; index < declarations.Count; index++)
            if (ReferenceEquals(declarations[index], declaration)) return index;
        return -1;
    }

    /// <summary>A temp folder of script files, which the service scans from disk.</summary>
    private sealed class Scripts : IDisposable
    {
        public Scripts(params (string Name, string Source)[] files)
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-js-symbols-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var (name, source) in files)
            {
                // A name may carry a path, which is how a test builds node_modules or a src folder.
                var path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, source);
            }
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>No solution: the service is driven through its path argument, which needs no Roslyn.</summary>
    private sealed class NullWorkspace : IRoslynWorkspace
    {
        public Solution? CurrentSolution => null;
        public string? LoadedSolutionPath => null;

        public Task<Solution?> EnsureSolutionLoadedAsync(CancellationToken ct = default) =>
            Task.FromResult<Solution?>(null);

        public Task<SolutionLoadResult> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? DiscoverSolutionPath() => null;

        public bool TryUpdateDocument(DocumentId documentId, SourceText text) => false;

        public DocumentId? TryAddDocument(ProjectId projectId, string name, string filePath, SourceText text) => null;

        public bool TryRemoveDocument(DocumentId documentId) => false;

        public void Dispose() { }
    }
}
