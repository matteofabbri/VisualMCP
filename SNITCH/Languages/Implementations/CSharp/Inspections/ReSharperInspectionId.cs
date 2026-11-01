namespace Snitch.Languages.CSharp.Inspections;

/// <summary>
/// One configurable inspection id declared by a ReSharper highlighting class: the id and language group
/// come from <c>ConfigurableSeverityHighlighting</c>, the analyzer from the <c>ElementProblemAnalyzer</c>
/// that names the highlighting, and the description from the tooltip resource the attribute points at.
/// </summary>
/// <param name="Id">The ReSharper inspection id, e.g. <c>InvertIf</c>.</param>
/// <param name="Languages">The language group the attribute declares, e.g. <c>CSHARP</c>.</param>
/// <param name="Category">The markup-model category (<c>AttributeId</c>), e.g. <c>ReSharper Dead Code</c>, when set.</param>
/// <param name="HighlightingType">The highlighting class that declares the id.</param>
/// <param name="AnalyzerType">The <c>ElementProblemAnalyzer</c> that produces the highlighting, when the metadata links it.</param>
/// <param name="Description">The tooltip text resolved from the assembly resources, when it is available.</param>
/// <param name="Assembly">The assembly the id was read from.</param>
public sealed record ReSharperInspectionId(
    string Id,
    string Languages,
    string? Category,
    string HighlightingType,
    string? AnalyzerType,
    string? Description,
    string Assembly);
