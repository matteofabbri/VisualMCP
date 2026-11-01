using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ChangeSignatureTool
{
    [McpServerTool, Description(
        "Change a method's parameter list: reorder, remove, or add parameters. " +
        "Updates the declaration and every call site — in the same file and across the solution. " +
        "Returns a 'callSites' list of before/after pairs plus a per-file line diff, in dryRun too. " +
        "REFUSES, writing nothing, when a use is not a plain call (method group / delegate conversion) or " +
        "when the change would add compiler errors. Equivalent to ReSharper 'Change Signature'. A Visual " +
        "Basic method takes the same operation: its call sites may omit the parentheses, an added default is " +
        "emitted as 'Optional', and an 'AddressOf' use refuses the change like a method group.\n\n" +
        "newParameterOrder: array of existing parameter names in the desired order. " +
        "Omit a name to remove it (only safe if the parameter has a default value or you also supply a defaultForRemoved). " +
        "additionalParams: array of objects {name, type, defaultValue} to append.")]
    public static Task<object> ChangeSignature(
        IChangeSignatureService svc,
        [Description("Method name to modify")] string methodName,
        [Description("Containing type name to disambiguate")] string? containingType = null,
        [Description("New order of existing parameters — array of parameter names, e.g. [\"b\",\"a\"]. Omit a name to remove it.")] string[]? newParameterOrder = null,
        [Description("New parameters to add at the end — array of JSON objects with fields: name, type, defaultValue (optional). E.g. [{\"name\":\"timeout\",\"type\":\"int\",\"defaultValue\":\"30\"}]")] ChangeSignatureService.NewParameter[]? additionalParams = null,
        [Description("For removed required parameters: literal expression to insert at every call site, keyed by parameter name. E.g. {\"count\": \"0\"}")] Dictionary<string, string>? defaultForRemoved = null,
        [Description("Dry run — show changes without writing to disk (default: false)")] bool dryRun = false)
        => svc.ChangeSignature(methodName, containingType, newParameterOrder, additionalParams, defaultForRemoved, dryRun);
}