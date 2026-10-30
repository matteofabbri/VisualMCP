using System.Text.Json;

namespace VisualMCP.Formatting;

/// <summary>
/// The few Snitch services that still return anonymous objects give a tool no members to name
/// at compile time. These shapes bind the runtime JSON onto records: the engine's property
/// names are matched case-insensitively, and a missing member simply stays default. Every
/// service with a typed result (<c>IIoService</c> and the refactoring services) is consumed
/// directly and needs no shape here.
/// </summary>
internal static class EngineJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static T As<T>(object value) where T : class, new()
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value), Options) ?? new T();
}

/// <summary>Shape of <c>ISearchTextService.Run</c>.</summary>
internal sealed class EngineTextSearch
{
    public string? Error { get; set; }
    public string? Pattern { get; set; }
    public string? Path { get; set; }
    public string? Glob { get; set; }
    public bool Recursive { get; set; }
    public int FilesScanned { get; set; }
    public int FilesWithMatches { get; set; }
    public int MatchCount { get; set; }
    public bool Truncated { get; set; }
    public List<EngineTextMatch>? Matches { get; set; }
}

internal sealed class EngineTextMatch
{
    public string? File { get; set; }
    public int Line { get; set; }
    public string? Text { get; set; }
    public List<EngineTextLine>? Context { get; set; }
}

internal sealed class EngineTextLine
{
    public int Line { get; set; }
    public string? Text { get; set; }
}
