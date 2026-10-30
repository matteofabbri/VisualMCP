using System.Text.Json;

namespace Snitch.Profiling.Model;

/// <summary>
/// The one reader/writer of the heap-snapshot JSON produced by <c>profile_heap writeJson=true</c>.
/// Shared by the comparison service and the memory-growth answer so both see exactly the same file
/// and the same error message.
/// </summary>
internal static class HeapSnapshotJson
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static HeapSnapshot Read(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HeapSnapshot>(json, ReadOptions)
                ?? throw new InvalidDataException("The file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{path}' is not a heap snapshot JSON. Write one with profile_heap writeJson=true. {ex.Message}", ex);
        }
    }

    public static string Write(HeapSnapshot snapshot) => JsonSerializer.Serialize(snapshot, WriteOptions);
}
