using System.Text.Encodings.Web;
using System.Text.Json;

namespace Snitch.Storage;

/// <summary>One tool call as it was received: when, which tool, and with what arguments.</summary>
public sealed record CommandLogEntry(long Id, DateTimeOffset OccurredAt, string Tool, string? Arguments);

/// <summary>
/// Turns the arguments of an MCP tool call into the single string stored in the log: JSON, capped so a
/// pasted file cannot bloat the database, and with the values of anything that looks like a credential
/// replaced — a log is not a place to keep secrets, and the arguments of
/// <c>gh_secret</c>/<c>http_invoke</c> often are exactly that.
/// </summary>
public static class CommandLogFormatter
{
    private const int MaxLength = 1000;

    private static readonly string[] SecretMarkers =
        ["secret", "token", "password", "passwd", "apikey", "api_key", "authorization", "credential", "privatekey"];

    /// <summary>Readable output: the log is read by people, and it never reaches an HTML page.</summary>
    private static readonly JsonSerializerOptions LogJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(string? tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return "(no args)";

        try
        {
            var redacted = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
            foreach (var (key, value) in arguments)
                redacted[key] = IsSecret(tool, key) ? "***" : Render(value);

            var json = JsonSerializer.Serialize(redacted, LogJson);
            return json.Length > MaxLength ? json[..MaxLength] + "…(truncated)" : json;
        }
        catch (Exception)
        {
            return "(unserializable args)";
        }
    }

    /// <summary>A key whose value must never be written down verbatim.</summary>
    public static bool IsSecret(string? tool, string key)
    {
        if (SecretMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;

        // The secret tools pass their payload as "value": the key alone says nothing, the tool does.
        return key.Equals("value", StringComparison.OrdinalIgnoreCase) &&
               tool is not null &&
               (tool.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                tool.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    private static object? Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Clip(value.GetString()),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Take(20).Select(Render).ToList(),
        _ => Clip(value.ToString()),
    };

    private static string? Clip(string? text) =>
        text is null ? null : text.Length <= 200 ? text : text[..200] + "…";
}
