namespace Snitch.Runtime;

/// <summary>What <see cref="IHttpInvokeService.HttpInvoke"/> received.</summary>
/// <remarks>
/// Every member is nullable so a refusal leaves the response members unset; the MCP serializer drops
/// a null member, so the answer keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record HttpInvokeResult
{
    /// <summary>The HTTP status code the host answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The status name, e.g. <c>OK</c>.</summary>
    public string? StatusText { get; init; }

    /// <summary>The response's content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>How long the request took, in milliseconds.</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>The response body, truncated at 32 000 characters.</summary>
    public string? Body { get; init; }

    /// <summary>The response and content headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What running a <c>.http</c> file did.</summary>
/// <remarks>
/// Every member is nullable so a refusal leaves the run members unset; the MCP serializer drops a
/// null member, so the answer keeps the shape it had as an anonymous object.
/// </remarks>
public sealed record HttpFileRunResult
{
    /// <summary>The file that was run.</summary>
    public string? Path { get; init; }

    /// <summary>The file name, without its directory.</summary>
    public string? File { get; init; }

    /// <summary>How many variables the file declares.</summary>
    public int? VariableCount { get; init; }

    /// <summary>How many requests the file holds.</summary>
    public int? RequestCount { get; init; }

    /// <summary>How many requests were sent.</summary>
    public int? Ran { get; init; }

    /// <summary>How many of them failed.</summary>
    public int? Failed { get; init; }

    /// <summary>How many were not sent.</summary>
    public int? Skipped { get; init; }

    /// <summary>Whether every request that ran succeeded.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether <c>failFast</c> stopped the run early.</summary>
    public bool? StoppedEarly { get; init; }

    /// <summary>The placeholders nothing defined.</summary>
    public IReadOnlyList<string>? UnresolvedVariables { get; init; }

    /// <summary>One answer per request, in file order.</summary>
    public IReadOnlyList<HttpFileRequestResult>? Requests { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>What one request of a <c>.http</c> file answered.</summary>
public sealed record HttpFileRequestResult
{
    /// <summary>The request's position in the file, 1-based.</summary>
    public int? Index { get; init; }

    /// <summary>The request's <c># @name</c>, when it has one.</summary>
    public string? Name { get; init; }

    /// <summary>The line the request starts on.</summary>
    public int? Line { get; init; }

    /// <summary>The HTTP method that was sent.</summary>
    public string? Method { get; init; }

    /// <summary>The URL that was sent, after variable substitution.</summary>
    public string? Url { get; init; }

    /// <summary>The HTTP status code the host answered.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The status name, e.g. <c>OK</c>.</summary>
    public string? StatusText { get; init; }

    /// <summary>The response's content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>How long the request took, in milliseconds.</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>Whether the request answered with a 2xx.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>The response body, truncated at the requested limit.</summary>
    public string? Body { get; init; }

    /// <summary>Why the request could not be completed.</summary>
    public string? Error { get; init; }
}
