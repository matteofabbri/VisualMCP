using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Analysis;
using Snitch.Languages.CSharp.Generation;

namespace VisualMCP.Tools.CSharp.Generation;

[McpServerToolType]
public static class XmlDtoTool
{
    [McpServerTool(Name = "generate_dto_from_xml"), Description(
        "Generate a C# DTO from an XML SAMPLE — the counterpart of generate_dto_from_json. Attributes and child " +
        "elements become properties; an element that repeats becomes a List<T>; an element with children " +
        "becomes a nested class; a leaf's type is inferred from its text (bool, int, long, decimal, DateTime, " +
        "Guid, otherwise string); 'item-id' becomes 'ItemId'. Compiler-gated (the file is added to the project " +
        "and compiled before anything is written) and previewed unless dryRun=false.")]
    public static Task<XmlDtoResult> GenerateDtoFromXml(
        IXmlDtoService dtos,
        [Description("The XML sample (one document with a root element).")] string xml,
        [Description("The type name to generate; defaults to the root element's name in PascalCase.")]
        string? typeName = null,
        [Description("The project to add the file to (default: the solution's single project).")]
        string? projectName = null,
        [Description("The namespace; defaults to the project's root namespace.")] string? namespaceName = null,
        [Description("The folder; defaults to the project's directory.")] string? directory = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => dtos.GenerateAsync(xml, typeName, projectName, namespaceName, directory, dryRun);
}

[McpServerToolType]
public static class ComparerGeneratorTool
{
    [McpServerTool(Name = "generate_comparer"), Description(
        "Generate an IComparer<T> for a type from the members it already has: every public instance property " +
        "whose type is a string, a number, an enum, a Guid, a date or a comparable struct is compared in " +
        "declaration order, with a Default singleton, null handling (null sorts first) and an early return per " +
        "key. Compiler-gated and previewed unless dryRun=false. Edit the order afterwards when the business key " +
        "is not the declaration order.")]
    public static Task<ComparerResult> GenerateComparer(
        IComparerGeneratorService comparers,
        [Description("The type to order (simple or fully-qualified name).")] string typeName,
        [Description("The project that declares the type, when the name is ambiguous.")] string? projectName = null,
        [Description("The folder; defaults to the declaring project's directory.")] string? directory = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => comparers.GenerateAsync(typeName, projectName, directory, dryRun);
}

[McpServerToolType]
public static class RegexValidationTool
{
    [McpServerTool(Name = "find_regex_issues"), Description(
        "Validate the regular expressions a solution writes: every CONSTANT pattern handed to a Regex " +
        "constructor, to a Regex static method, or to a [RegularExpression] attribute is compiled by the real " +
        ".NET parser, so a pattern a running program would reject is reported here with file:line, the pattern " +
        "and the parser's message. A pattern built at run time cannot be checked and the answer says how many " +
        "constants were compiled. Read-only.")]
    public static Task<RegexValidationResult> FindRegexIssues(
        IRegexValidationService regexes,
        [Description("Only this project (exact name from the loaded solution).")] string? projectName = null,
        [Description("Maximum findings to return (default 200).")] int maxResults = 200)
        => regexes.FindAsync(projectName, maxResults);
}
