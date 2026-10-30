namespace VisualMCP.Dashboard;

/// <summary>What one table of the solution store is, in words a reader of the dashboard needs.</summary>
public sealed record DashboardCatalogueEntry(string Table, string Title, string Area, string Description);

/// <summary>
/// The tables the solution store is known to contain, with a title, an area and a sentence
/// that says what the rows mean. It is only a presentation layer: the dashboard discovers
/// tables from <c>sqlite_master</c>, so a table added by a future feature still gets a page —
/// it just shows up under "Other tables" until it earns an entry here.
/// </summary>
public static class DashboardCatalogue
{
    public static IReadOnlyList<DashboardCatalogueEntry> Entries { get; } =
    [
        new("command_log", "Command log", "Activity",
            "Every tool call the server received: when, which tool, and the arguments it was given. " +
            "Written as the call arrives, so a call that fails or is cancelled is still here."),

        new("applied_edit", "Edit history", "Activity",
            "Every file this server wrote: tool, rules, before/after hashes and sizes. The pre-write text " +
            "is kept compressed, which is what makes an edit revertible."),

        new("context", "Context store", "Activity",
            "Facts the server caches between restarts (the last inspection report, the dashboard port). " +
            "Each row is a key, its value and an opaque freshness stamp."),

        new("board_item", "Board items", "Board",
            "The kanban cards: column, priority, labels, assignee. Position is the order inside the column."),

        new("board_comment", "Board comments", "Board",
            "Append-only comments attached to a board item."),

        new("board_history", "Board history", "Board",
            "Every field change of a board item, in order — the card's own audit trail."),

        new("si_symbol", "Symbol declarations", "Indexes",
            "Declarations indexed by the symbol index: name, signature, kind, accessibility, container, " +
            "project and the exact source span."),

        new("si_usage", "Symbol usages", "Indexes",
            "The typed uses of each indexed symbol, with the file and span where they occur."),

        new("si_document", "Indexed documents (symbols)", "Indexes",
            "One row per file the symbol index has processed, with its content hash and counts."),

        new("si_metacode", "Metacode per symbol", "Indexes",
            "The metacode tree of a declaration stored against the symbol key the index knows it by, so a " +
            "lookup returns the pseudocode of exactly the declaration it found."),

        new("source_document", "Source documents (native)", "Indexes",
            "The other half of the record: one row per source file of the solution, in its own language — " +
            "text, language tag, content hash, size. Written for every document, including those whose " +
            "language has no metacode frontend yet: those exist here and nowhere else."),

        new("mc_document", "Document metacode", "Indexes",
            "One row per document a frontend read: its content hash, timestamp, language and the counts of " +
            "the declarations, nodes and diagnostics the translation produced."),

        new("mc_tree", "Metacode trees", "Indexes",
            "One row per declaration: its canonical pseudocode (compressed) and the structural facts the " +
            "model gives it — cyclomatic and cognitive complexity, nesting, statements, branches."),

        new("ti_document", "Indexed documents (text)", "Indexes",
            "One row per file in the full-text index, with length, modification time and line count."),

        new("ti_row", "Text index map", "Indexes",
            "The path/line to FTS row mapping of the text index."),

        new("ti_line", "Text index (FTS5)", "Indexes",
            "The searchable text index: one row per line, with the raw line and the identifier-split copy " +
            "that FTS5 actually matches."),

        new("package_fact", "NuGet packages", "Packages",
            "The resolved package graph as the build sees it, one row per package and target framework, " +
            "direct or transitive."),

        new("package_dependency", "NuGet dependencies", "Packages",
            "The edges of that graph: which package pulls which one in, and at which version."),

        new("decompiled_artifact", "Decompiled artifacts", "Artifacts",
            "Assemblies decompiled to source under .visualMCP, keyed on the module version id, so the " +
            "same binary is never decompiled twice."),

        new("native_artifact", "Native binaries", "Artifacts",
            "Native images Ghidra analyzed: the file, its SHA-256, the processor language, entry point and " +
            "image base, how many functions were recovered and where Ghidra lived."),

        new("native_function", "Native functions", "Artifacts",
            "One row per recovered function: its name, entry address, prototype, Ghidra's decompiled C, the " +
            "canonical pseudocode the model gives it, and its cyclomatic/cognitive complexity."),

        new("native_call", "Native call edges", "Artifacts",
            "Who calls whom in a recovered native image, by entry address — the graph Ghidra saw."),
    ];

    public static DashboardCatalogueEntry? Find(string? table) =>
        table is null
            ? null
            : Entries.FirstOrDefault(e => string.Equals(e.Table, table, StringComparison.OrdinalIgnoreCase));

    public static string Title(string table) => Find(table)?.Title ?? Humanize(table);

    public static string Description(string table) =>
        Find(table)?.Description ?? "A table found in the solution store; no description has been written for it yet.";

    public static string Area(string table) => Find(table)?.Area ?? "Other";

    /// <summary><c>board_item</c> → <c>Board item</c>, for a table nobody has described.</summary>
    private static string Humanize(string table)
    {
        var words = table.Replace('_', ' ').Trim();
        return words.Length == 0 ? table : char.ToUpperInvariant(words[0]) + words[1..];
    }
}
