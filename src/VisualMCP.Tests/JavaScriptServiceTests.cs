using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Snitch.Languages.JavaScript;
using Snitch.Workspace;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// js_files: the module graph a scan can prove. The cases are the two resolutions that decide an edge — a
/// tsconfig path alias and a package in node_modules — because a graph that follows only './' reports an
/// alias-based application as a set of unrelated files.
/// </summary>
public sealed class JavaScriptServiceTests
{
    [Fact]
    public async Task The_module_graph_follows_a_tsconfig_alias()
    {
        using var files = new Scripts(
            ("tsconfig.json", """
                {
                    "compilerOptions": {
                        "baseUrl": ".",
                        "paths": { "@/*": ["src/*"] },
                    },
                }
                """),
            ("src/order.js", "export function total() { return 1; }"),
            ("src/main.js", """
                import { total } from '@/order';

                console.log(total());
                """));

        var json = Json(await new JavaScriptService(new NullWorkspace()).InventoryAsync(files.Root, 100));

        Assert.Equal(2, json.GetProperty("fileCount").GetInt32());
        var edge = Assert.Single(json.GetProperty("moduleGraph").GetProperty("edges").EnumerateArray());
        Assert.Equal("src/main.js", edge.GetProperty("from").GetString());
        Assert.Equal("src/order.js", edge.GetProperty("to").GetString());

        var main = FileEntry(json, "src/main.js");
        Assert.Equal(1, main.GetProperty("resolvedImports").GetInt32());
        Assert.Equal(0, main.GetProperty("externalImports").GetInt32());
    }

    [Fact]
    public async Task A_package_outside_the_scan_is_counted_and_is_not_an_edge()
    {
        using var files = new Scripts(
            ("node_modules/greeting/package.json", """{ "main": "lib/index.js" }"""),
            ("node_modules/greeting/lib/index.js", "export function greet(name) { return 'hi ' + name; }"),
            ("app.js", "export function classify(value) { return value > 0; }"),
            ("main.js", """
                import { greet } from 'greeting';
                import { classify } from './app.js';

                console.log(greet('x'), classify(1));
                """));

        var json = Json(await new JavaScriptService(new NullWorkspace()).InventoryAsync(files.Root, 100));

        // node_modules is pruned from the scan, so only the two application files are nodes.
        Assert.Equal(2, json.GetProperty("fileCount").GetInt32());
        var edge = Assert.Single(json.GetProperty("moduleGraph").GetProperty("edges").EnumerateArray());
        Assert.Equal("main.js", edge.GetProperty("from").GetString());
        Assert.Equal("app.js", edge.GetProperty("to").GetString());

        var main = FileEntry(json, "main.js");
        Assert.Equal(2, main.GetProperty("resolvedImports").GetInt32());
        Assert.Equal(1, main.GetProperty("externalImports").GetInt32());
        Assert.Equal(0, main.GetProperty("unresolvedImports").GetInt32());
    }

    private static JsonElement FileEntry(JsonElement inventory, string name) =>
        Assert.Single(inventory.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("file").GetString() == name);

    private static JsonElement Json(object result) => JsonSerializer.SerializeToElement(result);

    /// <summary>A temp folder of script files, which the service scans from disk.</summary>
    private sealed class Scripts : IDisposable
    {
        public Scripts(params (string Name, string Source)[] files)
        {
            Root = Path.Combine(Path.GetTempPath(), "vmcp-js-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var (name, source) in files)
            {
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
