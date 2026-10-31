using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class GenerifyTool
{
    [McpServerTool(Name = "generify"), Description(
        "Rider's 'Generify': turn a concrete type used inside a method or a type declaration into a type parameter. " +
        "Only the positions where a type parameter may legally stand are abstracted — a static member access " +
        "(int.Parse), an attribute, a nameof argument and a const declaration keep the concrete type. " +
        "For a METHOD: a type parameter that appears in no parameter cannot be inferred, so every call site gets the " +
        "concrete type as an explicit type argument (refused when the method already had type parameters that call " +
        "sites leave implicit). For a TYPE: every reference to the type spells the concrete arguments " +
        "(Cache → Cache<int>). constraints is keyed by type parameter name (or usage) and holds the constraint text, " +
        "e.g. {\"T\": \"class\"} or {\"T\": \"IComparable<T>\"}; a body that needs one refuses until it is passed. " +
        "Whole edit gated on the compiler. Preview by default.")]
    public static Task<object> Generify(
        IGenerifyService svc,
        [Description("File that declares the method or the type (absolute or relative).")] string filePath,
        [Description("Method to make generic (pass this or typeName, not both).")] string? methodName = null,
        [Description("Type to make generic (pass this or methodName, not both).")] string? typeName = null,
        [Description("Containing type name, when the method or type name is ambiguous.")] string? containingType = null,
        [Description("The concrete types to abstract, as written in the file, e.g. [\"int\"] or [\"string\",\"int\"].")] string[]? usages = null,
        [Description("Optional type parameter names keyed by usage, e.g. {\"int\": \"TValue\"}. Default T, T1, ...")] Dictionary<string, string>? typeParameterNames = null,
        [Description("Optional constraints keyed by type parameter name or usage, e.g. {\"T\": \"class\"}.")] Dictionary<string, string>? constraints = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.Generify(filePath, methodName, typeName, containingType, usages, typeParameterNames, constraints, dryRun);
}
