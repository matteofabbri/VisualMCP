namespace Snitch.Platform;

/// <summary>What <see cref="IFixLineEndingsService.FixAsync"/> changed, or would change.</summary>
/// <remarks>
/// Every member is nullable so a refusal leaves the run members unset; the MCP serializer drops a null
/// member, so the answer keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record FixLineEndingsResult
{
    /// <summary>The refactoring name, so a caller can tell this apart: <c>fix_line_endings</c>.</summary>
    public string? Refactoring { get; init; }

    /// <summary>The scope that ran: <c>file</c>, <c>directory</c>, <c>project</c> or <c>solution</c>.</summary>
    public string? Scope { get; init; }

    /// <summary>The style that was applied, or <c>auto</c>.</summary>
    public string? Style { get; init; }

    /// <summary>The operating system this ran on.</summary>
    public string? Host { get; init; }

    /// <summary>Whether the call was a preview.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Whether the files were written.</summary>
    public bool? Applied { get; init; }

    /// <summary>How many files were scanned.</summary>
    public int? Scanned { get; init; }

    /// <summary>How many files already used the target style.</summary>
    public int? AlreadyCorrect { get; init; }

    /// <summary>How many files were rewritten, or would be.</summary>
    public int? FilesChanged { get; init; }

    /// <summary>The files that changed, capped at 200.</summary>
    public IReadOnlyList<LineEndingChange>? Changes { get; init; }

    /// <summary>The files that could not be read, capped at 20.</summary>
    public IReadOnlyList<LineEndingSkip>? Skipped { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>The projects a selection error could choose from.</summary>
    public IReadOnlyList<string>? Projects { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>One file whose line endings were rewritten.</summary>
/// <param name="File">The absolute path of the file.</param>
/// <param name="Was">The style the file had.</param>
/// <param name="Now">The style it has now.</param>
/// <param name="CrlfLines">How many CRLF line breaks the old text held.</param>
/// <param name="LfLines">How many LF line breaks the old text held.</param>
public sealed record LineEndingChange(string File, string Was, string Now, int CrlfLines, int LfLines);

/// <summary>One file that was skipped.</summary>
/// <param name="File">The absolute path of the file.</param>
/// <param name="Reason">Why it could not be read.</param>
public sealed record LineEndingSkip(string File, string Reason);
