using ModelContextProtocol.Server;
using System.ComponentModel;
using Snitch.Languages.CSharp.Analysis;

namespace VisualMCP.Tools.CSharp.Analysis;

[McpServerToolType]
public static class FindMissingValidationTool
{
    [McpServerTool(Name = "find_missing_validation"), Description(
        "The missing contract at the public edge, on both sides of it. METHODS: a public method that takes a " +
        "reference-type (or string) parameter and never checks it — the null that blows up three frames deep; a " +
        "guard counts when it is in the method (`if (x is null) throw`, `ArgumentNullException.ThrowIfNull(x)`, " +
        "`x ?? throw`). DTOs: a public class or record that is only data (no behaviour, at least two public " +
        "settable properties) with properties that carry no System.ComponentModel.DataAnnotations attribute — " +
        "the payload nobody validates on the way in. Complements the security rules: those find dangerous APIs, " +
        "this finds the missing contract.")]
    public static Task<object> FindMissingValidation(
        IFindMissingValidationService svc,
        [Description("Optional: restrict to a single project by name")] string? projectName = null,
        [Description("How many methods and DTO types to return (default 100)")] int top = 100,
        [Description("Also report DTO-shaped types whose properties have no DataAnnotations validation (default true)")] bool includeDtos = true)
        => svc.Find(projectName, top, includeDtos);
}
