using System.Text.Json;

namespace Snitch.Debugging;

/// <summary>
/// One decoded Debug Adapter Protocol packet — a response to a request, an event, or a request
/// (which this client does not receive, but the adapter's own tests emit).
/// </summary>
public sealed class DapMessage
{
    /// <summary>The adapter's sequence number.</summary>
    public long Seq { get; init; }

    /// <summary>"response", "event" or "request".</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>For a request/response: the command.</summary>
    public string? Command { get; init; }

    /// <summary>For a response: whether the adapter accepted the request.</summary>
    public bool? Success { get; init; }

    /// <summary>For a failed response: the adapter's message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>For a response: the seq of the request it answers.</summary>
    public long? RequestSeq { get; init; }

    /// <summary>For an event: the event name.</summary>
    public string? Event { get; init; }

    /// <summary>The body payload, or JSON null when the packet has none.</summary>
    public JsonElement Body { get; init; }

    public bool IsResponse => Type == "response";
    public bool IsEvent => Type == "event";

    /// <summary>Reads one string property from a string-valued body property, or null.</summary>
    public string? BodyString(string name)
    {
        if (Body.ValueKind != JsonValueKind.Object) return null;
        if (!Body.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    public static DapMessage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var body = root.TryGetProperty("body", out var payload) ? payload.Clone() : default;

        return new DapMessage
        {
            Seq = Long(root, "seq"),
            Type = String(root, "type") ?? string.Empty,
            Command = String(root, "command"),
            Success = root.TryGetProperty("success", out var success) &&
                      success.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? success.GetBoolean()
                : null,
            ErrorMessage = String(root, "message"),
            RequestSeq = root.TryGetProperty("request_seq", out var requestSeq) &&
                         requestSeq.ValueKind == JsonValueKind.Number
                ? requestSeq.GetInt64()
                : null,
            Event = String(root, "event"),
            Body = body,
        };
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
