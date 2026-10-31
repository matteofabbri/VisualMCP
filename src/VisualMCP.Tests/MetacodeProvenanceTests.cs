using Microsoft.Data.Sqlite;
using Snitch.Metacode;
using Snitch.Metacode.Coding;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Indexing;
using Snitch.Storage;
using Snitch.Storage.Schema;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// Where stored metacode CAME FROM. Every row of the AST (mc_document/mc_tree/mc_node) carries the file
/// — or the binary — the content came from, plus a flag saying whether it was read or decompiled; the
/// solution's own text files that no frontend reads are in the AST too, as <c>file</c> nodes; and a
/// decompilation pulls in the folder it came from, not just one binary.
/// </summary>
public sealed class MetacodeProvenanceTests
{
    private const string Counter = """
        namespace Demo;

        public static class Counter
        {
            public static int Sum(int n)
            {
                var total = 0;
                for (var i = 0; i < n; i++)
                {
                    total += i;
                }

                return total;
            }
        }
        """;

    private const string SnapshotJson = """
        {
         "image": "version.dll",
         "language": "x86:LE:64:default",
         "entry": "1800010f0",
         "imageBase": "180000000",
         "functions": [
          {
           "entry": "180001124",
           "name": "FUN_180001124",
           "signature": "undefined8 __fastcall FUN_180001124(undefined8 param_1, int param_2)",
           "size": 124,
           "decompiled": "\r\nundefined8 FUN_180001124(undefined8 param_1,int param_2)\r\n\r\n{\r\n  if (param_2 == 0) {\r\n    return 1;\r\n  }\r\n  return 2;\r\n}\r\n\r\n",
           "calls": [ "EXTERNAL:00000029" ]
          },
          {
           "entry": "1800011b0",
           "name": "FUN_1800011b0",
           "signature": "bool __fastcall FUN_1800011b0(void)",
           "size": 52,
           "decompiled": "\r\nbool FUN_1800011b0(void)\r\n\r\n{\r\n  return 1;\r\n}\r\n\r\n",
           "calls": []
          }
         ]
        }
        """;

    private static (FakeWorkspace Workspace, VisualMcpStore Store, MetacodeIndexService Index) Create()
    {
        var workspace = FakeWorkspace.Create(("Counter.cs", Counter));
        var store = new VisualMcpStore(workspace);
        return (workspace, store, new MetacodeIndexService(workspace, store));
    }

    private static MetacodeCodingService Coding(
        FakeWorkspace workspace, MetacodeIndexService index, VisualMcpStore store, string binary)
    {
        return new MetacodeCodingService(
            workspace, index, new DecompileService(), new FakeGhidra(Analysis(binary)), new NativeCodeStore(store));
    }

    private static GhidraAnalysis Analysis(string binary) =>
        new(
            binary,
            GhidraSnapshot.Parse(SnapshotJson) with { BinaryPath = binary },
            Path.Combine(Path.GetTempPath(), "vmcp-functions.json"),
            null,
            null,
            "fake-analyzeHeadless",
            null,
            TimeSpan.FromSeconds(2),
            ExitCode: 0,
            TimedOut: false);

    // ── the solution's files, all of them ──────────────────────────────────────────────

    [Fact]
    public async Task A_file_no_frontend_reads_is_in_the_AST_as_a_file_node()
    {
        var (workspace, store, index) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var notes = workspace.PathOf("notes.md");
            File.WriteAllText(notes, "# Notes\n\nfirst\nsecond\n");

            var report = await index.BuildAsync();
            Assert.Null(report.Error);
            Assert.True(report.Unsupported >= 1, "notes.md should be stored natively only");

            // The tree row says what the file is, and where it came from.
            var row = Assert.Single(index.Trees(path: notes));
            Assert.Equal("file", row.Kind);
            Assert.Equal("notes.md", row.Name);
            Assert.Equal(notes, row.SourcePath);
            Assert.False(row.Decompiled);

            // The AST has it too: one File node carrying the file's own description.
            var tree = Assert.Single(index.Ast(path: notes).Declarations);
            Assert.Equal("file", tree.Kind);
            Assert.Equal(notes, tree.SourcePath);
            Assert.False(tree.Decompiled);

            var node = Assert.Single(tree.Root);
            Assert.Equal("File", node.Kind);
            Assert.Equal("notes.md", node.Data.GetProperty("name").GetString());
            Assert.Equal("md", node.Data.GetProperty("language").GetString());
            Assert.Equal(notes, node.Data.GetProperty("path").GetString());

            // The text itself is the other half of the record, tagged with the language it was read as.
            Assert.Equal(("md", "# Notes\n\nfirst\nsecond\n"), index.Source(notes)!.Value);

            var status = index.Status();

            // Three files have no frontend in this workspace: notes.md and the two project files the
            // fake solution carries, each of them one `file` node.
            Assert.Equal(3, status.Files);
            Assert.Equal(0, status.Decompiled);
        }
    }

    [Fact]
    public async Task Code_read_from_source_records_its_file_and_is_not_flagged_decompiled()
    {
        var (workspace, store, index) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            await index.BuildAsync();
            var path = workspace.PathOf("Counter.cs");

            Assert.All(index.Trees(path: path),
                row =>
                {
                    Assert.Equal(path, row.SourcePath);
                    Assert.False(row.Decompiled);
                });

            var tree = Assert.Single(index.Ast(symbol: "Sum").Declarations);
            Assert.Equal(path, tree.SourcePath);
            Assert.False(tree.Decompiled);

            // The columns are really there, not only in the projection: the raw AST table has them.
            Assert.True(
                CountInStore(store, "SELECT COUNT(*) FROM mc_node WHERE decompiled = 0 AND source_path = $path;", path) > 0);
        }
    }

    // ── decompiled code, in the same AST ───────────────────────────────────────────────

    [Fact]
    public async Task A_decompiled_type_records_the_binary_and_is_flagged()
    {
        var (workspace, store, index) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var assembly = typeof(MetacodeTranslator).Assembly.Location;
            var decompilation = new DecompileService()
                .DecompileTypeToMetacode(assembly, "Snitch.Metacode.MetacodeTranslator");
            Assert.Null(decompilation.Error);

            await index.IndexDecompilationAsync(decompilation);

            // The class and its members all match the name; the declaration itself is the class row.
            var row = Assert.Single(index.Trees(symbol: "MetacodeTranslator"), candidate => candidate.Kind == "class");
            Assert.True(row.Decompiled);
            Assert.StartsWith(MetacodeIndexStore.DecompiledPrefix, row.Path);
            Assert.Equal(Path.GetFullPath(assembly), Path.GetFullPath(row.SourcePath));

            var tree = Assert.Single(
                index.Ast(symbol: "class:Snitch.Metacode.MetacodeTranslator").Declarations);
            Assert.True(tree.Decompiled);
            Assert.Equal(Path.GetFullPath(assembly), Path.GetFullPath(tree.SourcePath));

            // The flag and the binary are on every AST row, not only on the declaration.
            var total = CountInStore(store, "SELECT COUNT(*) FROM mc_node;", assembly);
            var flagged = CountInStore(store, "SELECT COUNT(*) FROM mc_node WHERE decompiled = 1;", assembly);
            var byPath = CountInStore(store, "SELECT COUNT(*) FROM mc_node WHERE source_path = $path;", assembly);
            Assert.True(flagged > 0 && byPath > 0,
                $"nodes={total}, decompiled={flagged}, bySourcePath={byPath}, sourcePath={row.SourcePath}");

            var stored = Assert.Single(index.DecompiledAssemblies());
            Assert.Equal("managed", stored.Kind);
            Assert.Equal(Path.GetFileName(assembly), stored.Assembly);
        }
    }

    [Fact]
    public async Task A_native_binary_is_stored_in_the_AST_under_the_native_prefix()
    {
        var (workspace, store, index) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var binary = Path.Combine(workspace.RootDirectory, "version.dll");
            File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02]);

            var coding = Coding(workspace, index, store, binary);
            var result = await coding.DecompileAsync(binary, siblings: false);

            Assert.False(result.IsError, result.Error);
            Assert.Contains("metacode AST", result.Summary);

            // Ghidra's functions are declarations of a document of their own, in the same tables.
            var row = Assert.Single(index.Trees(symbol: "FUN_180001124"));
            Assert.Equal(MetacodeIndexStore.NativePrefix + "version.dll", row.Path);
            Assert.True(row.Decompiled);
            Assert.Equal(Path.GetFullPath(binary), Path.GetFullPath(row.SourcePath));

            var tree = Assert.Single(index.Ast(symbol: "FUN_180001124").Declarations);
            Assert.True(tree.Decompiled);
            Assert.NotEmpty(tree.Root);

            var stored = Assert.Single(index.DecompiledAssemblies());
            Assert.Equal("version.dll", stored.Assembly);
            Assert.Equal("native", stored.Kind);
        }
    }

    // ── the folder the binary came from ────────────────────────────────────────────────

    [Fact]
    public async Task Decompiling_a_binary_sweeps_its_folder_into_the_same_AST()
    {
        var (workspace, store, index) = Create();
        using (workspace)
        using (store)
        using (index)
        {
            var folder = Path.Combine(workspace.RootDirectory, "lib");
            Directory.CreateDirectory(folder);

            // Two copies of the same small assembly, and a text file next to them.
            var primary = Path.Combine(folder, "Primary.dll");
            File.Copy(typeof(MetacodeTranslator).Assembly.Location, primary);
            File.Copy(typeof(MetacodeTranslator).Assembly.Location, Path.Combine(folder, "Sibling.dll"));
            File.WriteAllText(Path.Combine(folder, "readme.md"), "# lib\n");

            var coding = Coding(workspace, index, store, primary);
            var result = await coding.DecompileAsync(primary, maxSiblings: 10);

            Assert.False(result.IsError, result.Error);
            Assert.NotNull(result.Sweep);
            Assert.Equal(2, result.Sweep!.Text + result.Sweep.Managed);

            // The binary asked for, its sibling, and the text file: all three are in the store now.
            var paths = index.Trees(symbol: "MetacodeTranslator").Select(row => row.Path).ToHashSet();
            Assert.Contains(MetacodeIndexStore.DecompiledPrefix + "Primary.dll/Snitch.Metacode.MetacodeTranslator", paths);
            Assert.Contains(MetacodeIndexStore.DecompiledPrefix + "Sibling.dll/Snitch.Metacode.MetacodeTranslator", paths);

            var readme = Assert.Single(index.Trees(path: Path.Combine(folder, "readme.md")));
            Assert.Equal("file", readme.Kind);
            Assert.False(readme.Decompiled);

            // A second call is idempotent: the binaries are already stored, so the sweep decompiles nothing.
            var again = await coding.DecompileAsync(primary, maxSiblings: 10);
            Assert.NotNull(again.Sweep);
            Assert.Equal(0, again.Sweep!.Managed);
        }
    }

    // ── the schema grows in place ──────────────────────────────────────────────────────

    [Fact]
    public void An_older_store_gains_the_provenance_columns_without_losing_its_rows()
    {
        var database = Path.Combine(Path.GetTempPath(), "vmcp-schema-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={database}");
            connection.Open();

            // The shape a store written before the provenance columns has.
            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE "mc_document" (
                        "path" TEXT NOT NULL CONSTRAINT "PK_mc_document" PRIMARY KEY,
                        "project" TEXT NOT NULL,
                        "hash" TEXT NOT NULL,
                        "mtime" INTEGER NOT NULL,
                        "language" TEXT NOT NULL,
                        "declarations" INTEGER NOT NULL,
                        "nodes" INTEGER NOT NULL,
                        "diagnostics" INTEGER NOT NULL,
                        "indexed_at" TEXT NOT NULL
                    );
                    INSERT INTO "mc_document"
                        ("path", "project", "hash", "mtime", "language", "declarations", "nodes", "diagnostics", "indexed_at")
                    VALUES ('D:\old\Counter.cs', 'Probe', 'H', 0, 'cs', 3, 9, 0, '2026-01-01T00:00:00+00:00');
                    """;
                create.ExecuteNonQuery();
            }

            VisualMcpSchema.Ensure(connection);

            using var read = connection.CreateCommand();
            read.CommandText = "SELECT source_path, decompiled FROM mc_document WHERE path = 'D:\\old\\Counter.cs';";
            using var reader = read.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(string.Empty, reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
        }
        finally
        {
            try { File.Delete(database); } catch (IOException) { /* best effort */ }
        }
    }

    private static int CountInStore(VisualMcpStore store, string sql, string path)
    {
        using var connection = new SqliteConnection($"Data Source={store.EnsureDatabase()}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>A Ghidra service that replays a recorded snapshot: no external process, no machine dependency.</summary>
    private sealed class FakeGhidra : IGhidraService
    {
        private readonly GhidraAnalysis _analysis;

        public FakeGhidra(GhidraAnalysis analysis) => _analysis = analysis;

        public string? HeadlessPath => "fake-analyzeHeadless";

        public bool IsAvailable => true;

        public Task<GhidraAnalysis> AnalyzeAsync(
            string binaryPath, GhidraOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_analysis with { BinaryPath = binaryPath });
    }
}
