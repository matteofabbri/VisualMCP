namespace Snitch.Web;

/// <summary>What <see cref="IDownloadFileService.RunAsync"/> downloaded or refused.</summary>
/// <remarks>
/// Every member is nullable so a branch leaves the others unset; the MCP serializer drops a null
/// member, and each answer therefore keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record DownloadFileResult
{
    /// <summary>The URL that was asked for.</summary>
    public string? Url { get; init; }

    /// <summary>The URL the request ended on, after redirects.</summary>
    public string? FinalUrl { get; init; }

    /// <summary>The HTTP status code the host answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The HTTP reason phrase, when the response carried one.</summary>
    public string? Status { get; init; }

    /// <summary>The response's content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Whether the resource was downloaded.</summary>
    public bool? Downloaded { get; init; }

    /// <summary>How many bytes the resource holds, when it was downloaded.</summary>
    public long? Bytes { get; init; }

    /// <summary>The size in megabytes, rounded to three decimals.</summary>
    public double? SizeMB { get; init; }

    /// <summary>The absolute path the file was saved to, when one was asked for.</summary>
    public string? SavedTo { get; init; }

    /// <summary>The first characters of the content, when a preview was asked for.</summary>
    public string? Preview { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}
