using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class TransformParametersTool
{
    [McpServerTool(Name = "transform_parameters"), Description(
        "Rider's 'Transform parameters' — the Change Signature dialog operations that are more than a reorder, " +
        "applied to the declaration and every call site at once. operation=wrap replaces a consecutive block of " +
        "parameters with one parameter object (a nested class with a property per parameter; typeName names it, " +
        "defaulting to <Method>Arguments); make-optional adds a default (defaultValue for all, or defaults keyed " +
        "by parameter) and refuses a required parameter after an optional one; make-required removes defaults, " +
        "refusing when a call site omits the argument; change-type retypes the selected parameters (newType) and " +
        "lets the compiler decide whether it holds; replace-with-property turns the parameters into properties of " +
        "the containing type and each argument into an assignment on the receiver before the call. Every operation " +
        "is gated on the compiler and refuses, writing nothing, when a use is not a plain call or the rewrite " +
        "would not compile. dryRun defaults to true.")]
    public static Task<object> TransformParameters(
        ITransformParametersService svc,
        [Description("The method whose parameters change.")] string methodName,
        [Description("wrap | make-optional | make-required | change-type | replace-with-property")] string operation,
        [Description("Names of the parameters the operation applies to.")] string[] parameters,
        [Description("Containing type name, when the method name is ambiguous.")] string? containingType = null,
        [Description("For wrap: the parameter object's class name; defaults to <Method>Arguments.")] string? typeName = null,
        [Description("For change-type: the new parameter type, as source text (e.g. 'IReadOnlyList<int>').")] string? newType = null,
        [Description("For make-optional: one default expression applied to every selected parameter.")] string? defaultValue = null,
        [Description("For make-optional: default expressions keyed by parameter name, overriding defaultValue. E.g. {\"count\": \"0\"}")] Dictionary<string, string>? defaults = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
        => svc.TransformParameters(
            methodName, operation, parameters, containingType, typeName, newType, defaultValue, defaults, dryRun);
}
