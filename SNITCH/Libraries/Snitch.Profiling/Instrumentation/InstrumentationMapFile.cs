using System.Text.Json;

namespace Snitch.Profiling.Instrumentation;

/// <summary>One method id and the name it stands for. The probe event carries only the id.</summary>
public sealed record InstrumentationMethodMap(int Id, string Name, int MetadataToken);

/// <summary>The sidecar the instrumenter writes next to the rewritten assembly.</summary>
public sealed record InstrumentationMapFile(
    string Provider,
    string Assembly,
    string Output,
    IReadOnlyList<InstrumentationMethodMap> Methods)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static InstrumentationMapFile? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<InstrumentationMapFile>(File.ReadAllText(path), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
