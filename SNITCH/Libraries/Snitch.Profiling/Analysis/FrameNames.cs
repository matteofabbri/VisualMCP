using System.Text.RegularExpressions;

namespace Snitch.Profiling.Analysis;

/// <summary>
/// Method names in ETLX/TraceEvent carry a module prefix ("Module!Namespace.Type.Method(args)")
/// and the signature. The module is redundant when comparing methods, and the signature makes
/// overloads aggregate noisily, so both are removed. Compiler-generated local functions and
/// lambdas get a readable name too: "Program.&lt;&lt;Main&gt;$&gt;g__Spin|0_0" becomes
/// "Program.Spin", and a lambda becomes "Type.&lt;lambda&gt;".
/// </summary>
internal static class FrameNames
{
    private static readonly Regex LocalFunction = new(@"g__([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "(unknown)";

        var name = raw;
        var bang = name.IndexOf('!');
        if (bang >= 0 && bang < name.Length - 1)
            name = name[(bang + 1)..];

        if (name.Length > 0 && name[^1] == ')')
        {
            var depth = 0;
            for (var i = name.Length - 1; i >= 0; i--)
            {
                if (name[i] == ')') depth++;
                else if (name[i] == '(')
                {
                    depth--;
                    if (depth == 0)
                    {
                        name = name[..i];
                        break;
                    }
                }
            }
        }

        name = CleanGeneratedName(name);

        return string.IsNullOrWhiteSpace(name) ? raw : name;
    }

    private static string CleanGeneratedName(string name)
    {
        var local = LocalFunction.Match(name);
        if (local.Success)
        {
            var owner = Owner(name);
            return owner.Length > 0 ? $"{owner}.{local.Groups[1].Value}" : local.Groups[1].Value;
        }

        if (name.Contains("b__", StringComparison.Ordinal))
        {
            var owner = Owner(name);
            return owner.Length > 0 ? $"{owner}.<lambda>" : "<lambda>";
        }

        if (name.Contains("<Main>$", StringComparison.Ordinal))
        {
            // The compiler's entry point for top-level statements.
            var owner = Owner(name);
            return owner.Length > 0 ? $"{owner}.Main" : "Main";
        }

        if (name.Contains("DisplayClass", StringComparison.Ordinal))
        {
            var owner = Owner(name);
            return owner.Length > 0 ? $"{owner}.<closure>" : "<closure>";
        }

        return name;
    }

    /// <summary>The declaring type part of a compiler-generated name, before the '&lt;' or '+' marker.</summary>
    private static string Owner(string name)
    {
        var angle = name.IndexOf('<');
        var plus = name.IndexOf('+');
        var end = angle > 0 ? angle : plus > 0 ? plus : name.Length;
        return name[..end].TrimEnd('.', '+');
    }

    /// <summary>The last readable segment of a method name, for finding titles.</summary>
    public static string ShortName(string fullName)
    {
        var text = fullName;
        var generic = text.IndexOf('<');
        var isGenerated = text.Contains("<lambda>", StringComparison.Ordinal) ||
                          text.Contains("<closure>", StringComparison.Ordinal);
        if (generic > 0 && !isGenerated)
            text = text[..generic];

        text = text.TrimEnd('.', '+');
        var lastDot = text.LastIndexOf('.');
        return lastDot >= 0 && lastDot < text.Length - 1 ? text[(lastDot + 1)..] : text;
    }
}
