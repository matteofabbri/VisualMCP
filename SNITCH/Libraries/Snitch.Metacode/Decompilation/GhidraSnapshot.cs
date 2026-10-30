using System.Text.Json;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// One function as Ghidra's <c>ExportNativeGraph.java</c> writes it: its address, name, prototype,
/// size, decompiled C and the callees it was seen calling.
/// </summary>
public sealed record GhidraFunction(
    string Name,
    string Entry,
    string Signature,
    int Size,
    string Decompiled,
    IReadOnlyList<string> Calls)
{
    public bool HasBody => !string.IsNullOrWhiteSpace(Decompiled);
}

/// <summary>
/// The snapshot Ghidra headless produces: the image it analyzed, its processor/language id, the entry
/// point, the image base and every recovered function. The shape is the one the previous native
/// pipeline used (see the <c>ExportNativeGraph.java</c> script in this folder), so a snapshot captured
/// before this service existed still parses.
/// </summary>
public sealed record GhidraSnapshot(
    string Image,
    string Language,
    string Entry,
    string ImageBase,
    IReadOnlyList<GhidraFunction> Functions,
    string? BinaryPath = null)
{
    /// <summary>
    /// A function by name or by address, with or without the <c>0x</c> prefix, case-insensitively —
    /// the way a caller knows a function before it knows what Ghidra called it.
    /// </summary>
    public GhidraFunction? FindFunction(string nameOrAddress)
    {
        if (string.IsNullOrWhiteSpace(nameOrAddress))
            return null;

        var wanted = nameOrAddress.Trim();
        var address = wanted.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? wanted[2..] : wanted;

        return Functions.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase))
               ?? Functions.FirstOrDefault(f => string.Equals(f.Entry, address, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a snapshot. Throws <see cref="JsonException"/> when the text is not one.</summary>
    public static GhidraSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A Ghidra snapshot is a JSON object.");

        var functions = new List<GhidraFunction>();
        if (!root.TryGetProperty("functions", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException("A Ghidra snapshot has a \"functions\" array; this document does not.");

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            functions.Add(new GhidraFunction(
                Text(element, "name") ?? "?",
                Text(element, "entry") ?? "?",
                Text(element, "signature") ?? string.Empty,
                Integer(element, "size"),
                Text(element, "decompiled") ?? string.Empty,
                Strings(element, "calls")));
        }

        return new GhidraSnapshot(
            Text(root, "image") ?? "?",
            Text(root, "language") ?? "?",
            Text(root, "entry") ?? string.Empty,
            Text(root, "imageBase") ?? string.Empty,
            functions);
    }

    /// <summary>Whether a JSON document is a Ghidra snapshot and not some other tree.</summary>
    public static bool LooksLikeSnapshot(string text) =>
        text.Contains("\"imageBase\"", StringComparison.Ordinal) && text.Contains("\"functions\"", StringComparison.Ordinal);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }
}
