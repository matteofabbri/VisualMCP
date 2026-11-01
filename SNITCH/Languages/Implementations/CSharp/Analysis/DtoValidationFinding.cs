namespace Snitch.Languages.CSharp.Analysis;

/// <summary>
/// A type whose shape is a data carrier (no behaviour, public settable properties) and what validation it
/// carries. <see cref="Unvalidated"/> can be empty — a fully validated DTO is still a shape the scan saw.
/// </summary>
public sealed record DtoValidationFinding(
    string Type,
    string File,
    int Line,
    IReadOnlyList<string> Unvalidated,
    IReadOnlyList<string> Validated);
