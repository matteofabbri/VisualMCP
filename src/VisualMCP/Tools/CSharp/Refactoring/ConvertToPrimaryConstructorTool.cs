using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.CSharp.Refactoring;

namespace VisualMCP.Tools.CSharp.Refactoring;

[McpServerToolType]
public static class ConvertToPrimaryConstructorTool
{
    [McpServerTool(Name = "convert_to_primary_constructor"), Description(
        "Convert a class/struct constructor into a primary constructor (Rider's 'Convert to primary constructor') for " +
        "the case that is a pure syntax move: one constructor whose body only assigns parameters to fields, and each of " +
        "those fields is declared here without an initializer. The parameters move to the type header and the " +
        "assignments become field initializers, so behaviour is unchanged. Anything else — extra statements, a field " +
        "assigned twice, an initializer plus an assignment, several constructors — is refused. GATED on the compiler. " +
        "dryRun defaults to true.")]
    public static Task<object> ConvertToPrimaryConstructor(
        IConvertToPrimaryConstructorService svc,
        [Description("File that declares the type (absolute or relative).")] string filePath,
        [Description("Type to convert.")] string typeName,
        [Description("true (default) previews only; pass false to write the file.")] bool dryRun = true)
        => svc.ConvertToPrimaryConstructor(filePath, typeName, dryRun);
}
