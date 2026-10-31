using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Languages.VB.Style;

namespace VisualMCP.Tools.VB;

[McpServerToolType]
public static class SimplifyVbCodeTool
{
    [McpServerTool(Name = "simplify_vb_code"), Description(
        "The Visual Basic counterpart of simplify_code: apply the IDE's mechanical style suggestions to .vb files at the " +
        "scope you pick. 41 rule ids from the VB column of Microsoft's code-style table — IDE0001 simplify name; IDE0002 " +
        "simplify member access; IDE0003 remove 'Me.'; IDE0004 unnecessary cast; IDE0049 System.Int32 → Integer; IDE0041 " +
        "x = Nothing → x Is Nothing; IDE0029/IDE0030 null check → If(a, b); IDE0031 null propagation → ?.; IDE0047 remove " +
        "parentheses; IDE0054 compound assignment; IDE0100 x = True → x; IDE0075 If(x, True, False) → x; IDE0084 " +
        "Not (o Is other) ⇄ o IsNot other (only when the .editorconfig sets visual_basic_style_prefer_isnot_expression, in " +
        "the direction it asks for); IDE0081 remove 'ByVal'; IDE0009 add 'Me.' (only when dotnet_style_qualification_for_* " +
        "asks for it); IDE0045/IDE0046 an If/Else folded to If(c, a, b); IDE0010 a Select Case completed over its enum; " +
        "IDE0017 object initializers; IDE0140 'Dim x As New C'; IDE0033 tuple element names; IDE0082 GetType(T).Name → " +
        "NameOf(T); IDE0280 NameOf for a paramName literal; IDE0071 simplify interpolation; IDE0120/IDE0121 the two LINQ " +
        "chains; IDE0070 HashCode.Combine; IDE0036 modifier order; IDE0040 accessibility; IDE0044 ReadOnly; IDE0051 an " +
        "unused private member; IDE0052 an unread private field; IDE0035 unreachable code; IDE0059 a dead store; IDE0060 an " +
        "unused parameter and its argument at every call site; IDE0032 an auto-property; IDE2000/IDE2003/IDE0073 the layout " +
        "rules. Every rewrite is gated on the compiler (a file whose change " +
        "would add errors is reported under 'refused' and left untouched) and every write is recorded in the edit " +
        "history. The 8 ids it cannot rewrite are answered with the reason, not ignored: five are constructs VB does not " +
        "have (collection expressions, inferred anonymous member names, discards, '??=', throw expressions), and IDE0050, " +
        "IDE0079 and IDE0130 are a shape change, a suppression audit and a project-layout decision. dryRun defaults to " +
        "true. Separate from simplify_code on purpose: the engine " +
        "registers one style service per language, so the caller names the language it wants instead of passing a switch " +
        "that would make half of simplify_code's rule list inapplicable.")]
    public static async Task<object> SimplifyVb(
        IServiceProvider services,
        [Description("document (needs filePath) | project (needs projectName) | solution")] string scope = "solution",
        [Description("The .vb file to simplify, for scope=document (absolute or relative).")] string? filePath = null,
        [Description("Visual Basic project to simplify, for scope=project.")] string? projectName = null,
        [Description("VB rule ids to apply, e.g. 'IDE0001,IDE0081', or omit for all supported.")] string? rules = null,
        [Description("true (default) previews only; pass false to write the files.")] bool dryRun = true)
    {
        // AddSnitchVisualBasic() is an opt-in extension (the shared AddSnitch stays C#-only), so this is the one
        // capability whose absence is a normal state rather than a broken installation.
        var simplify = ToolServices.Find<IVbSimplifyCodeService>(services);
        if (simplify is null)
            return ToolServices.Missing<IVbSimplifyCodeService>(
                "The Visual Basic style engine", "services.AddSnitchVisualBasic()");

        return await simplify.SimplifyAsync(scope, filePath, projectName, rules, dryRun);
    }
}
