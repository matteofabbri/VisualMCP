using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Snitch.Metacode.Indexing;
using VisualMCP.Formatting;

namespace VisualMCP.Tools.Metacode;

[McpServerToolType]
public static class MetacodeGenerateTool
{
    [McpServerTool(Name = "metacode_generate"), Description(
        "Generate NEW code from metacode: C# in, C# out. The source is one of — text (canonical pseudocode by " +
        "default, or C# with language='cs'), a symbol stored in the solution's metacode index, or a path (the " +
        "stored tree of that file when the index has it, otherwise the file itself). This is the inverse of " +
        "metacode_translate: the model is the source of truth and C# is the first target, so the same tree can " +
        "later be emitted to another language without re-reading the original source. Everything the model cannot " +
        "express in C# (a Python import, a comprehension with several for clauses, '**', a set or dictionary " +
        "literal) is lowered to the closest C# that compiles, with a '// TODO(metacode)' comment in the code and a " +
        "diagnostic listed after it — never dropped silently. Use it to draft a method from pseudocode you wrote, or " +
        "to round-trip a stored declaration back to readable C#. The output is real code, not JSON.")]
    public static object Generate(
        IMetacodeIndexService index,
        [Description("Canonical pseudocode (default), or C# when language='cs'.")]
        string? text = null,
        [Description("For text: 'pseudo' (default) or 'cs'. C# is parsed with Roslyn's syntax only — no solution needed.")]
        string? language = null,
        [Description("A symbol stored in the metacode index: 'Divide', 'Calculator.Divide' or a declaration key.")]
        string? symbol = null,
        [Description("A file: its stored tree when the index has it, otherwise the file is translated first.")]
        string? path = null,
        [Description("The target language. Only 'csharp' today.")]
        string target = "csharp",
        [Description("Write the using directives above the code (default true). The namespaces the code needs are added.")]
        bool includeUsings = true,
        [Description("Extra usings, comma-separated, e.g. 'System.Text, MyApp.Core'.")]
        string? usings = null)
    {
        if (!target.Trim().Equals("csharp", StringComparison.OrdinalIgnoreCase))
            return ToolText.Fail(
                $"metacode_generate: target '{target}' is not implemented. The model is language-neutral, but C# is " +
                "the only emitter today; generating another language means adding its emitter next to CSharpEmitter.");

        var result = index.Generate(symbol, path, text, language, includeUsings, usings);
        if (result.Error is not null)
            return ToolText.Fail($"metacode_generate failed: {result.Error}");

        var text2 = new StringBuilder();
        text2.Append("// ").Append(result.Code.Length == 0 ? "no code" : "C# generated from " + result.Source)
             .Append(" · target ").Append(result.Target)
             .Append(" · ").Append(MetacodeToolHelpers.LineCount(result.Code)).Append(" line(s)");

        if (result.Usings.Count > 0)
            text2.Append(" · usings ").Append(string.Join(", ", result.Usings));

        text2.Append('\n').Append('\n').Append(result.Code);
        if (!result.Code.EndsWith('\n')) text2.Append('\n');

        if (result.Diagnostics.Count > 0)
        {
            text2.Append('\n').Append("// diagnostics (").Append(result.Diagnostics.Count)
                 .Append(") — every approximation is also a comment where it happens\n");
            foreach (var diagnostic in result.Diagnostics)
                text2.Append("// ").Append(diagnostic.Replace("\r", string.Empty).Replace('\n', ' ')).Append('\n');
        }

        return ToolText.Ok(text2.ToString());
    }
}
