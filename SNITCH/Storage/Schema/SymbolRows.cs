namespace Snitch.Storage.Schema;

/// <summary>One declaration in the symbol index: where it is, what it is, and how it is spelled.</summary>
public sealed class SymbolRow
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Accessibility { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string ContainerKey { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsExtension { get; set; }
    public int Arity { get; set; }
    public string? Summary { get; set; }
    public string Project { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Start { get; set; }
    public int Length { get; set; }
    public int Line { get; set; }
    public int Col { get; set; }
    public int EndLine { get; set; }
    public int EndCol { get; set; }
    public string DocHash { get; set; } = string.Empty;
}

/// <summary>One use of a symbol, with how it is used (call, read, write, instantiate, …) and where.</summary>
public sealed class SymbolUsageRow
{
    public int Id { get; set; }
    public string SymbolKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ContainerKey { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Start { get; set; }
    public int Length { get; set; }
    public int Line { get; set; }
    public int Col { get; set; }
    public string DocHash { get; set; } = string.Empty;
}

/// <summary>The fingerprint of one indexed document, so a build can skip the unchanged ones.</summary>
public sealed class SymbolDocumentRow
{
    public string Path { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public int Declarations { get; set; }
    public int Usages { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>The metacode tree of one declaration, next to the symbol it belongs to.</summary>
public sealed class SymbolMetacodeRow
{
    public string SymbolKey { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public byte[] Text { get; set; } = [];
    public string DocHash { get; set; } = string.Empty;
}
