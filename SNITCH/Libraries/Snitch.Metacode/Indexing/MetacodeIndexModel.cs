using System.Text.Json;

namespace Snitch.Metacode.Indexing;

/// <summary>
/// One declaration of the solution's metacode, as it is stored: the canonical pseudocode of that
/// declaration (a type nests its members' text), the file and span it came from, and the structural
/// facts computed over the model — cyclomatic and cognitive complexity, nesting, statement, branch and
/// expression counts. The facts are columns, not prose, so a report is a SQL query.
/// </summary>
public sealed record MetacodeTreeRow(
    string Key,
    string Path,
    string Kind,
    string Name,
    int Line,
    int Column,
    int EndLine,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    int Branches,
    int Expressions,
    string Text)
{
    /// <summary>
    /// The file this content came from: <see cref="Path"/> for a file the pass read, the binary for a
    /// type or function that was decompiled. Empty on the rows a translation produces — the store writes
    /// the document's value into every row it owns.
    /// </summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>True when this content was decompiled from a binary rather than read from a source file.</summary>
    public bool Decompiled { get; init; }
}

/// <summary>What one file contributed, computed before anything is written.</summary>
public sealed record MetacodeDocumentFacts(
    string Path,
    string Project,
    string Hash,
    long Mtime,
    string Language,
    IReadOnlyList<MetacodeTreeRow> Rows,
    int Diagnostics)
{
    /// <summary>
    /// Every node of every declaration in <see cref="Rows"/>, in pre-order per declaration: the complete
    /// AST, stored in <c>mc_node</c> next to the declaration's rendered text. Empty only for a document
    /// no frontend read.
    /// </summary>
    public IReadOnlyList<MetacodeAstNode> Nodes { get; init; } = [];

    /// <summary>
    /// The document as its own language writes it — the native half of the record. Null for a document
    /// with no file behind it: a type that came out of a decompiled assembly.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Whether a frontend read this document into the model. False means the file is stored as a single
    /// <c>file</c> node: the file is recorded as present in the AST and visibly not code, instead of being
    /// skipped as if the solution did not contain it.
    /// </summary>
    public bool HasReader { get; init; } = true;

    /// <summary>
    /// The file the content came from. Null means <see cref="Path"/> itself: a document read from disk.
    /// A decompiled assembly sets it to the binary, because <see cref="Path"/> is then a
    /// <c>assembly://</c>/<c>native://</c> key that is not a file at all.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>True when this document was decompiled from a binary rather than read from a source file.</summary>
    public bool Decompiled { get; init; }
}

/// <summary>The freshness token of a file: its content hash and the timestamp it had when indexed.</summary>
public sealed record MetacodeDocumentState(string Hash, long Mtime)
{
    /// <summary>
    /// Whether <c>source_document</c> holds the native half of this document. A document with metacode and
    /// no source row is not complete — that is the state of every file in a store written before that table
    /// existed — so the incremental pass re-reads it once instead of skipping it forever.
    /// </summary>
    public bool HasSource { get; init; }
}

/// <summary>What a build or a refresh did, in the shape the tool and the startup log report.</summary>
public sealed record MetacodeBuildReport(
    int Scanned,
    int Indexed,
    int Skipped,
    int Removed,
    int Failed,
    IReadOnlyList<string> Failures,
    int Declarations,
    int Functions,
    int Types,
    int Diagnostics,
    long Milliseconds,
    string? Database,
    string? Solution,
    string? Error = null)
{
    /// <summary>How many documents the store now holds in total (not just the ones this pass touched).</summary>
    public int Documents { get; init; }

    /// <summary>How many source documents the store holds in their native language, read or not.</summary>
    public int Sources { get; init; }

    /// <summary>How many were stored natively only, because no frontend reads their language.</summary>
    public int Unsupported { get; init; }

    /// <summary>How many AST nodes the store holds in total (the <c>mc_node</c> rows).</summary>
    public int Nodes { get; init; }
}

/// <summary>One binary whose code the store holds: the file name, the identity its rows are keyed on, and how many documents it contributed.</summary>
public sealed record DecompiledAssemblyRow(string Assembly, string Mvid, int Types)
{
    /// <summary><c>managed</c> for an ILSpy decompilation, <c>native</c> for a Ghidra one.</summary>
    public string Kind { get; init; } = "managed";
}

/// <summary>How much metacode the solution store holds, and how fresh it is.</summary>
public sealed record MetacodeIndexStatus(
    int Documents,
    int Declarations,
    int Functions,
    int Types,
    int MaxCyclomatic,
    int AverageCyclomatic,
    string? Newest,
    string? Database,
    string? Solution)
{
    /// <summary>
    /// How many source documents the store holds in their native language. It is the other side of
    /// <c>Documents</c>: a document no frontend reads appears here and not there.
    /// </summary>
    public int Sources { get; init; }

    /// <summary>How many nodes of the AST the store holds (the <c>mc_node</c> rows), across every document.</summary>
    public int Nodes { get; init; }

    /// <summary>How many documents were decompiled from a binary rather than read from a source file.</summary>
    public int Decompiled { get; init; }

    /// <summary>
    /// How many documents no frontend reads — text files that are in the store, and in the AST, as a
    /// single <c>file</c> node.
    /// </summary>
    public int Files { get; init; }
}

/// <summary>One row of the complexity report: a callable and the score the metacode gives it.</summary>
public sealed record MetacodeComplexityRow(
    string Key,
    string Name,
    string Kind,
    string Path,
    int Line,
    int Cyclomatic,
    int Cognitive,
    int MaxNesting,
    int Statements,
    int Branches,
    string Risk)
{
    /// <summary>The file or binary this callable came from (see <see cref="MetacodeTreeRow.SourcePath"/>).</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>True when the callable was decompiled rather than read from source.</summary>
    public bool Decompiled { get; init; }
}

/// <summary>What a generation produced: the code, the diagnostics of both directions, and the usings it needs.</summary>
public sealed record MetacodeGenerateResult(
    string Code,
    string Source,
    string Target,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Usings,
    string? Error = null);

/// <summary>One node of a stored AST, nested the way the model holds it.</summary>
/// <remarks>
/// <see cref="Slot"/> is the parent's property that holds this node and <see cref="Ordinal"/> its index
/// inside it, so the nested shape loses nothing of the flat rows it was built from. <see cref="Data"/> is
/// the node's own payload, already parsed from the stored JSON.
/// </remarks>
public sealed record MetacodeAstTreeNode(
    int Id,
    string Kind,
    string Slot,
    int Ordinal,
    int Line,
    int Column,
    JsonElement Data,
    IReadOnlyList<MetacodeAstTreeNode> Children);

/// <summary>One declaration's complete AST as the store holds it: the nested tree plus what it costs.</summary>
public sealed record MetacodeAstTree(
    string Path,
    string Key,
    string Kind,
    string Name,
    int Line,
    int Nodes,
    int MaxDepth,
    IReadOnlyList<MetacodeAstTreeNode> Root)
{
    /// <summary>The file or binary this tree came from (see <see cref="MetacodeTreeRow.SourcePath"/>).</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>True when the tree was decompiled from a binary rather than read from a source file.</summary>
    public bool Decompiled { get; init; }
}

/// <summary>The answer to "show me the AST": the declarations that matched, their trees, and whether the cap cut them.</summary>
public sealed record MetacodeAstResult(
    string? Path,
    IReadOnlyList<MetacodeAstTree> Declarations,
    int Nodes,
    bool Truncated,
    string? Error = null);
