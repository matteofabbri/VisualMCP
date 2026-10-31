namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// The ReSharper inspection catalogue of one Rider installation, read from the assemblies' metadata.
/// </summary>
/// <param name="Found">Whether a <c>lib/ReSharperHost</c> folder was found at <paramref name="Path"/>.</param>
/// <param name="Path">The Rider installation directory that was read.</param>
/// <param name="Error">Why the catalogue could not be read, when <paramref name="Found"/> is false.</param>
/// <param name="AssembliesScanned">How many assemblies actually carried inspection metadata.</param>
/// <param name="DecodeFailures">Attributes whose argument blob could not be decoded; the scan continues past them.</param>
/// <param name="Ids">The ids, one entry per id, de-duplicated across assemblies.</param>
/// <param name="DecodeFailureSample">The first few attributes that failed to decode, as <c>AttributeType@Assembly</c>.</param>
/// <param name="ResourceErrors">Why a tooltip resource could not be read, when it could not.</param>
public sealed record ReSharperCatalogue(
    bool Found,
    string? Path,
    string? Error,
    int AssembliesScanned,
    int DecodeFailures,
    IReadOnlyList<ReSharperInspectionId> Ids,
    IReadOnlyList<string> DecodeFailureSample,
    IReadOnlyList<string> ResourceErrors)
{
    /// <summary>The catalogue of an installation whose engine folder is missing.</summary>
    public static ReSharperCatalogue NotFound(string path, string error) => new(false, path, error, 0, 0, [], [], []);
}
