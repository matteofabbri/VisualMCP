using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Generation;

namespace VisualMCP.Tools.CSharp.Generation;

/// <summary>
/// The two generators from the context-action tail: a DTO from a JSON sample and a fluent builder.
/// Both are compiler-gated and preview by default.
/// </summary>
[McpServerToolType]
public static class GenerationTools
{
    [McpServerTool(Name = "generate_dto_from_json"), Description(
        "Generate a C# DTO from a JSON sample: every property is inferred from the data (integers → long, " +
        "decimals → double, arrays → List<T>, nested objects → a nested record, null → a nullable object), " +
        "PascalCase names, init-only properties, into the project you name. Compiler-gated: a class that would " +
        "not compile is reported and nothing is written. Previewed unless dryRun=false.")]
    public static Task<object> GenerateDtoFromJson(
        IDtoFromJsonService generator,
        [Description("The JSON sample (one object).")] string json,
        [Description("The type name to generate, e.g. OrderDto.")] string typeName,
        [Description("The project to add the file to (omit when the solution has one).")] string? projectName = null,
        [Description("The folder for the new file (default: the project directory).")] string? directory = null,
        [Description("Namespace for the type (default: the project's root namespace).")] string? namespaceName = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => generator.GenerateAsync(json, typeName, projectName, directory, namespaceName, dryRun);

    [McpServerTool(Name = "generate_builder"), Description(
        "Generate a fluent builder for a type: one With<Property> per public settable property and a Build(). " +
        "Requires a public parameterless constructor and at least one settable property. Compiler-gated, " +
        "previewed unless dryRun=false.")]
    public static Task<object> GenerateBuilder(
        IBuilderGeneratorService generator,
        [Description("The type to build (simple or fully-qualified name).")] string typeName,
        [Description("The project that declares it, when the name is ambiguous.")] string? projectName = null,
        [Description("Builder class name (default: <Type>Builder).")] string? builderName = null,
        [Description("Preview only (default: false).")] bool dryRun = false)
        => generator.GenerateAsync(typeName, projectName, builderName, dryRun);
}
