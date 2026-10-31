namespace Snitch.Storage.Schema;

/// <summary>One document the metacode index read, with its freshness token and what the model found in it.</summary>
public sealed class MetacodeDocumentRow
{
    public string Path { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Mtime { get; set; }
    public string Language { get; set; } = string.Empty;
    public int Declarations { get; set; }
    public int Nodes { get; set; }
    public int Diagnostics { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>One declaration's canonical pseudocode, with the structural facts computed over the model.</summary>
public sealed class MetacodeTreeRow
{
    public string DeclarationKey { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Col { get; set; }
    public int EndLine { get; set; }
    public int Cyclomatic { get; set; }
    public int Cognitive { get; set; }
    public int MaxNesting { get; set; }
    public int Statements { get; set; }
    public int Branches { get; set; }
    public int Expressions { get; set; }

    /// <summary>The pseudocode, gzip-compressed: a type's text repeats every member, which has its own row too.</summary>
    public byte[] Text { get; set; } = [];

    public string DocHash { get; set; } = string.Empty;
}

/// <summary>The file as its own language writes it — the half of the record that exists even with no frontend.</summary>
public sealed class SourceDocumentRow
{
    public string Path { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public long Mtime { get; set; }
    public int Chars { get; set; }
    public int Lines { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset IndexedAt { get; set; }
}
