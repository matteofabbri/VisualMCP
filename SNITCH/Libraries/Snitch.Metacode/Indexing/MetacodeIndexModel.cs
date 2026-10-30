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
    string Text);

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
    /// The document as its own language writes it — the native half of the record. Null for a document
    /// with no file behind it: a type that came out of a decompiled assembly.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Whether a frontend read this document into the model. False means the store keeps the native
    /// source only, because the language has no reader yet (Visual Basic today): the file is recorded as
    /// present and unreadable, instead of being skipped as if the solution did not contain it.
    /// </summary>
    public bool HasReader { get; init; } = true;
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
}

/// <summary>One binary whose code the store holds: the file name, the MVID its rows are keyed on, and how many types it contributed.</summary>
public sealed record DecompiledAssemblyRow(string Assembly, string Mvid, int Types);

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
    string Risk);

/// <summary>What a generation produced: the code, the diagnostics of both directions, and the usings it needs.</summary>
public sealed record MetacodeGenerateResult(
    string Code,
    string Source,
    string Target,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Usings,
    string? Error = null);
