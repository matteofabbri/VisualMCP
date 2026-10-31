namespace Snitch.Storage.Schema;

/// <summary>One native binary whose decompilation the store keeps, keyed on the SHA-256 of the file.</summary>
public sealed class NativeArtifactRow
{
    public long Id { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string Binary { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string ImageBase { get; set; } = string.Empty;
    public int Functions { get; set; }
    public long SizeBytes { get; set; }
    public string? Ghidra { get; set; }
    public string? Headless { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One function Ghidra recovered: its C, the canonical pseudocode, and the complexity both views agree on.</summary>
public sealed class NativeFunctionRow
{
    public long Artifact { get; set; }
    public int Ordinal { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public int Size { get; set; }
    public string Decompiled { get; set; } = string.Empty;
    public string Pseudocode { get; set; } = string.Empty;
    public int Cyclomatic { get; set; }
    public int Cognitive { get; set; }
    public int MaxNesting { get; set; }
    public int Statements { get; set; }
    public int Branches { get; set; }
}

/// <summary>One call edge of a recovered program, by entry address.</summary>
public sealed class NativeCallRow
{
    public long Artifact { get; set; }
    public string Caller { get; set; } = string.Empty;
    public string Callee { get; set; } = string.Empty;
}
