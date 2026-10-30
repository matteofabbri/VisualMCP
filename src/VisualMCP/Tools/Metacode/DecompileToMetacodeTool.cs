using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Snitch.Metacode.Decompilation;
using Snitch.Metacode.Rendering;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class DecompileToMetacodeTool
{
    [McpServerTool(Name = "decompile_to_metacode"), Description(
        "Decompile a .NET assembly with ILSpy and print the result as CANONICAL PSEUDOCODE: the C# ILSpy produces is " +
        "lowered by the C# frontend into the same model metacode_translate uses, so a decompiled type has the shape of a " +
        "type read from source (declarations, members, statements, expressions) and every node keeps the line it came " +
        "from. One type when typeFullName is given; otherwise a PAGE of the assembly's types selected by nameFilter / " +
        "namespaceFilter and paged with skip/maxTypes — start narrow on a large binary, because each type is decompiled " +
        "and translated on its own and the output grows fast. A type the model cannot read costs that type only: it is " +
        "listed under 'errors' and the rest still come back. Use decompile_type/decompile_assembly when you want the C# " +
        "text instead, and metacode_translate on a source file when there is no binary to decompile. No solution is " +
        "required: this reads the assembly from disk.")]
    public static CallToolResult DecompileToMetacode(
        IMetacodeDecompileService decompile,
        [Description("Path to the .dll/.exe to decompile.")] string assemblyPath,
        [Description("Exactly one type, fully qualified (e.g. 'Namespace.TypeName'). Omit to decompile a page of the assembly.")]
        string? typeFullName = null,
        [Description("Only types whose full name contains this text (case-insensitive). Ignored when typeFullName is given.")]
        string? nameFilter = null,
        [Description("Only types in this namespace or a sub-namespace (prefix match, case-insensitive). Ignored when typeFullName is given.")]
        string? namespaceFilter = null,
        [Description("Skip this many matching types (paging). Default 0.")] int skip = 0,
        [Description("Maximum types to decompile. 0 = all (default; use a bound on large assemblies).")] int maxTypes = 0,
        [Description("Prefix every declaration and statement with '// @file:line', so a line of pseudocode names the ILSpy output it came from.")]
        bool showOrigin = false)
    {
        try
        {
            var single = !string.IsNullOrWhiteSpace(typeFullName);
            var result = single
                ? decompile.DecompileTypeToMetacode(assemblyPath, typeFullName!)
                : decompile.DecompileAssemblyToMetacode(assemblyPath, nameFilter, namespaceFilter, skip, maxTypes);

            if (result.IsError) return ToolText.Fail($"{result.Assembly}: {result.Error}");

            var rendered = new PseudocodeRenderer(PseudocodeOptions.Default with { ShowOrigin = showOrigin })
                .Render(result.Document);

            var header = new StringBuilder();
            header.Append(result.Assembly);
            if (single) header.Append(" · ").Append(result.Types.Count > 0 ? result.Types[0].FullName : typeFullName);
            header.Append(" · ").Append(result.Types.Count).Append(" type(s)");
            if (!single)
                header.Append(" of ").Append(result.Matched).Append(" matched / ").Append(result.TotalTypes).Append(" total");
            if (result.Hidden > 0) header.Append(" · ").Append(result.Hidden).Append(" hidden (no C# produced)");
            if (result.Errors.Count > 0) header.Append(" · ").Append(result.Errors.Count).Append(" error(s) reported");
            header.Append(" · ").Append(result.Document.Diagnostics.Count).Append(" diagnostic(s)");
            if (result.Types.Count > 0)
                header.Append(" · ").Append(result.Types.Sum(t => t.Functions)).Append(" function(s), ")
                      .Append(result.Types.Sum(t => t.Members)).Append(" member(s)");

            var text = new StringBuilder();
            text.Append(header).Append("\n\n");
            text.Append(rendered.Length == 0
                ? "// no pseudocode: the selected types produced no declarations (compiler-generated or hidden)."
                : rendered);

            var diagnostics = MetacodeToolHelpers.DiagnosticsBlock(result.Document);
            if (diagnostics.Length > 0) text.Append('\n').Append(diagnostics);

            if (result.Errors.Count > 0)
            {
                text.Append("// errors (").Append(result.Errors.Count).Append(" reported; the engine caps the list)\n");
                foreach (var error in result.Errors)
                    text.Append("// ").Append(error.Replace("\r", string.Empty).Replace('\n', ' ')).Append('\n');
            }

            // Paging hint. The page is Types + Hidden + the types that failed, and a failure past the
            // engine's error cap is not counted in Errors — so with maxTypes the next offset is exact,
            // without it the hint would be a lower bound and is replaced by that advice.
            if (!single)
            {
                if (result.Errors.Count > 0)
                {
                    text.Append("// some types in this page failed, so the next offset is not the sum of what is shown: ")
                        .Append(maxTypes > 0
                            ? "continue with skip=" + (skip + maxTypes)
                            : "pass maxTypes to page with an exact offset, or narrow with nameFilter")
                        .Append('\n');
                }
                else
                {
                    var returned = skip + result.Types.Count + result.Hidden;
                    if (result.Matched > returned)
                        text.Append("// ").Append(result.Matched - returned)
                            .Append(" more type(s) match: continue with skip=").Append(returned).Append('\n');
                }
            }

            return ToolText.Ok(text.ToString());
        }
        catch (Exception ex)
        {
            return ToolText.Fail($"decompile_to_metacode failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
