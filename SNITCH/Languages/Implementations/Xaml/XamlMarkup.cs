using System.Text;

namespace Snitch.Languages.Xaml;

/// <summary>
/// One parsed markup extension: <c>{Binding Path=Name, Mode=TwoWay}</c> becomes
/// Type="Binding", Named["Path"]="Name", Named["Mode"]="TwoWay". A positional argument
/// (<c>{Binding Name}</c>) is what <see cref="Positional"/> holds.
/// </summary>
public sealed record XamlMarkupExtension(string Type, Dictionary<string, string> Named, List<string> Positional)
{
    public string? Get(string name) => Named.TryGetValue(name, out var value) ? value : null;

    /// <summary>The first positional argument, or the named one — how <c>Path</c> is written both ways.</summary>
    public string? Value(string name) => Get(name) ?? (Positional.Count > 0 ? Positional[0] : null);

    /// <summary>True for the extensions that produce a binding, which are the ones a type check can answer.</summary>
    public bool IsBinding => Type is "Binding" or "x:Bind" or "CompiledBinding" or "TemplateBinding";
}

/// <summary>
/// A parser for XAML markup extensions — the <c>{…}</c> values the previous layer matched with a
/// regex, which is why <c>Path=Foo</c> used to be collected as an <c>ElementName</c>. It understands
/// nested braces (<c>{Binding RelativeSource={RelativeSource Self}}</c>), quoted values, <c>\,</c>
/// escapes and a literal suffix, so a value either parses into named/positional arguments or is
/// reported as not being an extension at all.
/// </summary>
public static class XamlMarkup
{
    /// <summary>True when the value is (or starts with) a markup extension.</summary>
    public static bool IsExtension(string? value) => value is not null && value.TrimStart().StartsWith('{');

    /// <summary>
    /// Parses <paramref name="value"/> as a markup extension. False when it is not one, or when the
    /// braces do not close: an unparseable value is left alone rather than guessed at.
    /// </summary>
    public static bool TryParse(string? value, out XamlMarkupExtension extension)
    {
        extension = null!;
        if (value is null) return false;

        var text = value.Trim();
        if (text.Length < 2 || text[0] != '{') return false;

        var depth = 0;
        var end = -1;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\') { index++; continue; }
            if (character == '{') depth++;
            else if (character == '}')
            {
                depth--;
                if (depth == 0) { end = index; break; }
                if (depth < 0) return false;
            }
        }

        if (end < 0) return false;

        var body = text.Substring(1, end - 1).Trim();
        if (body.Length == 0) return false;

        var typeLength = 0;
        while (typeLength < body.Length &&
               (char.IsLetterOrDigit(body[typeLength]) || body[typeLength] is ':' or '.' or '_'))
            typeLength++;

        if (typeLength == 0) return false;

        var type = body[..typeLength];
        var arguments = body[typeLength..];

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();

        foreach (var part in SplitArguments(arguments))
        {
            if (part.Length == 0) continue;

            var equals = TopLevelEquals(part);
            if (equals < 0)
            {
                positional.Add(Unquote(Unescape(part)));
                continue;
            }

            var key = part[..equals].Trim();
            var argument = Unquote(Unescape(part[(equals + 1)..].Trim()));
            if (key.Length > 0) named[key] = argument;
        }

        extension = new XamlMarkupExtension(type, named, positional);
        return true;
    }

    /// <summary>
    /// The path a binding reads. <c>{Binding}</c> has none (it binds the whole DataContext), and
    /// <c>Path=.</c> is the same nothing spelled out; both come back as "." so a caller can tell
    /// "self" from "no path".
    /// </summary>
    public static string? PathOf(XamlMarkupExtension extension)
    {
        var path = extension.Value("Path");
        if (string.IsNullOrWhiteSpace(path)) return null;

        path = path.Trim();
        return path is "." or "/" ? "." : path;
    }

    /// <summary>
    /// Splits a binding path into the member names a type check can follow: <c>Customer.Name</c> →
    /// Customer, Name. An attached or indexer segment (<c>(Grid.Row)</c>, <c>Items[0]</c>) is returned
    /// as a segment with <see cref="PathSegment.Checkable"/> false, so the caller stops instead of
    /// reporting a false positive.
    /// </summary>
    public static List<PathSegment> Segments(string? path)
    {
        var segments = new List<PathSegment>();
        if (string.IsNullOrWhiteSpace(path) || path == ".") return segments;

        var current = new StringBuilder();
        var depth = 0;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();
            if (text.Length == 0) return;

            if (text.StartsWith('(') || text.Contains('[') || text.Contains(')'))
                segments.Add(new PathSegment(text, Checkable: false));
            else if (text is "Parent" or "TemplatedParent" or "Self")
                segments.Add(new PathSegment(text, Checkable: false));
            else
                segments.Add(new PathSegment(text, Checkable: true));
        }

        foreach (var character in path)
        {
            switch (character)
            {
                case '[' or '(':
                    depth++;
                    current.Append(character);
                    break;

                case ']' or ')':
                    depth--;
                    current.Append(character);
                    break;

                case '.' when depth == 0:
                    Flush();
                    break;

                case '/' when depth == 0:
                    // A leading "/" (current item of a collection) carries no member to check.
                    Flush();
                    break;

                default:
                    current.Append(character);
                    break;
            }
        }

        Flush();
        return segments;
    }

    /// <summary>One step of a binding path, and whether a type check can be attempted on it.</summary>
    public sealed record PathSegment(string Name, bool Checkable);

    // ── parsing helpers ──────────────────────────────────────────────────────
    private static IEnumerable<string> SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var current = new StringBuilder();
        var depth = 0;
        var quote = '\0';

        foreach (var character in arguments)
        {
            if (character == '\\')
            {
                current.Append(character);
                continue;
            }

            if (quote != '\0')
            {
                current.Append(character);
                if (character == quote) quote = '\0';
                continue;
            }

            switch (character)
            {
                case '\'' or '"':
                    quote = character;
                    current.Append(character);
                    continue;
                case '{':
                    depth++;
                    current.Append(character);
                    continue;
                case '}':
                    depth--;
                    current.Append(character);
                    continue;
                case ',' when depth == 0:
                    yield return current.ToString().Trim();
                    current.Clear();
                    continue;
                default:
                    current.Append(character);
                    continue;
            }
        }

        if (current.Length > 0) yield return current.ToString().Trim();
    }

    private static int TopLevelEquals(string part)
    {
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < part.Length; index++)
        {
            var character = part[index];
            if (character == '\\') { index++; continue; }

            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }

            switch (character)
            {
                case '\'' or '"':
                    quote = character;
                    continue;
                case '{':
                    depth++;
                    continue;
                case '}':
                    depth--;
                    continue;
                case '=' when depth == 0:
                    return index;
            }
        }

        return -1;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')))
            return value[1..^1];
        return value;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] is ',' or '{' or '}' or '\\')
            {
                builder.Append(value[++index]);
                continue;
            }

            builder.Append(value[index]);
        }

        return builder.ToString();
    }
}
