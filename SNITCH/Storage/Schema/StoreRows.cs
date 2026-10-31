namespace Snitch.Storage.Schema;

/// <summary>One fact in the <c>context</c> table: an expensive answer (the last inspection report) kept across restarts.</summary>
public sealed class ContextFactRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Stamp { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One received tool call, as the command log records it.</summary>
public sealed class CommandLogRow
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Date { get; set; } = string.Empty;
    public string Time { get; set; } = string.Empty;
    public string Tool { get; set; } = string.Empty;
    public string? Arguments { get; set; }
}

/// <summary>One file write this server performed, with the pre-write text kept compressed so it can be reverted.</summary>
public sealed class AppliedEditRow
{
    public long Id { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public string Tool { get; set; } = string.Empty;
    public string? Rules { get; set; }
    public string File { get; set; } = string.Empty;
    public string BeforeHash { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public int BeforeChars { get; set; }
    public int AfterChars { get; set; }
    public byte[]? BeforeText { get; set; }
}

/// <summary>One resolved NuGet package, per project and target framework, as the build actually restores it.</summary>
public sealed class PackageFactRow
{
    public string Project { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Tfm { get; set; } = string.Empty;
    public bool Direct { get; set; }
}

/// <summary>One edge of the resolved package graph: which package pulls which one in.</summary>
public sealed class PackageDependencyRow
{
    public string Project { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string ParentVersion { get; set; } = string.Empty;
    public string Tfm { get; set; } = string.Empty;
    public string ChildId { get; set; } = string.Empty;
    public string ChildVersion { get; set; } = string.Empty;
}

/// <summary>Where the source of a decompiled assembly was written, keyed on its module version id.</summary>
public sealed class DecompiledArtifactRow
{
    public string Mvid { get; set; } = string.Empty;
    public string Assembly { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public int Types { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
