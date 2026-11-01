using System.Text;

namespace Snitch.Languages.Xaml;

/// <summary>One attribute inside a start tag, with the exact span its value occupies (quotes excluded).</summary>
public sealed record XamlTextAttribute(string Name, int NameStart, int ValueStart, int ValueLength, char Quote)
{
    /// <summary>The value as written, without the quotes.</summary>
    public string Value(string text) => text.Substring(ValueStart, ValueLength);
}

/// <summary>
/// One tag of a XAML file: where it starts and ends in the text and the attributes it carries. The
/// scanner is quote-aware and steps over comments, CDATA and processing instructions, which is what a
/// text splice needs: an element's offset cannot be found with a search when a comment above it
/// contains the same tag.
/// </summary>
public sealed record XamlTextTag(string Name, bool IsClosing, bool IsSelfClosing, int Start, int End)
{
    /// <summary>Where an attribute is inserted: before the <c>/&gt;</c> of a self-closing tag, before the <c>&gt;</c>.</summary>
    public int AttributeInsertionOffset => IsSelfClosing ? End - 1 : End;
}

/// <summary>A quote-aware tag scanner over XAML text, plus the line/offset arithmetic the editor needs.</summary>
public static class XamlTextIndex
{
    public static List<XamlTextTag> Scan(string text)
    {
        var tags = new List<XamlTextTag>();
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf('<', index);
            if (open < 0) break;

            if (Skips(text, open, "<!--", "-->", out var commentEnd) ||
                Skips(text, open, "<![CDATA[", "]]>", out commentEnd) ||
                Skips(text, open, "<?", "?>", out commentEnd))
            {
                index = commentEnd;
                continue;
            }

            if (string.CompareOrdinal(text, open, "<!", 0, 2) == 0)
            {
                var declarationEnd = text.IndexOf('>', open);
                index = declarationEnd < 0 ? text.Length : declarationEnd + 1;
                continue;
            }

            var closing = open + 1 < text.Length && text[open + 1] == '/';
            var nameStart = closing ? open + 2 : open + 1;

            var nameEnd = nameStart;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('>' or '/'))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                index = open + 1;
                continue;
            }

            var name = text[nameStart..nameEnd];
            var cursor = nameEnd;
            char quote = '\0';
            var tagEnd = -1;

            while (cursor < text.Length)
            {
                var character = text[cursor];

                if (quote != '\0')
                {
                    if (character == quote) quote = '\0';
                    cursor++;
                    continue;
                }

                if (character is '\'' or '"')
                {
                    quote = character;
                    cursor++;
                    continue;
                }

                if (character == '>')
                {
                    tagEnd = cursor;
                    break;
                }

                cursor++;
            }

            if (tagEnd < 0) break;

            var selfClosing = !closing && tagEnd > open && text[tagEnd - 1] == '/';
            tags.Add(new XamlTextTag(name, closing, selfClosing, open, tagEnd));
            index = tagEnd + 1;
        }

        return tags;
    }

    private static bool Skips(string text, int start, string open, string close, out int end)
    {
        end = -1;
        if (string.CompareOrdinal(text, start, open, 0, open.Length) != 0) return false;

        var closeAt = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        end = closeAt < 0 ? text.Length : closeAt + close.Length;
        return true;
    }

    /// <summary>The attributes of one start tag, in document order.</summary>
    public static List<XamlTextAttribute> Attributes(string text, XamlTextTag tag)
    {
        var attributes = new List<XamlTextAttribute>();
        if (tag.IsClosing) return attributes;

        var nameLength = tag.Name.Length;
        var cursor = tag.Start + 1 + nameLength;

        while (cursor < tag.AttributeInsertionOffset)
        {
            while (cursor < tag.AttributeInsertionOffset && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= tag.AttributeInsertionOffset) break;
            if (text[cursor] == '/') break;

            var nameStart = cursor;
            while (cursor < tag.AttributeInsertionOffset &&
                   !char.IsWhiteSpace(text[cursor]) && text[cursor] != '=')
                cursor++;

            var name = text[nameStart..cursor];
            while (cursor < tag.AttributeInsertionOffset && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= tag.AttributeInsertionOffset || text[cursor] != '=')
            {
                if (name.Length > 0) attributes.Add(new XamlTextAttribute(name, nameStart, cursor, 0, '\0'));
                continue;
            }

            cursor++; // '='
            while (cursor < tag.AttributeInsertionOffset && char.IsWhiteSpace(text[cursor])) cursor++;
            if (cursor >= tag.AttributeInsertionOffset) break;

            var quote = text[cursor];
            if (quote is not ('\'' or '"'))
            {
                var valueStart = cursor;
                while (cursor < tag.AttributeInsertionOffset && !char.IsWhiteSpace(text[cursor]) && text[cursor] != '/')
                    cursor++;
                attributes.Add(new XamlTextAttribute(name, nameStart, valueStart, cursor - valueStart, '\0'));
                continue;
            }

            cursor++; // opening quote
            var start = cursor;
            while (cursor < tag.AttributeInsertionOffset && text[cursor] != quote) cursor++;

            attributes.Add(new XamlTextAttribute(name, nameStart, start, cursor - start, quote));
            cursor++; // closing quote
        }

        return attributes;
    }

    /// <summary>Finds the tag that starts at the position an element reports through <c>IXmlLineInfo</c>.</summary>
    public static XamlTextTag? TagAt(List<XamlTextTag> tags, int offset) =>
        tags.FirstOrDefault(tag => tag.Start == offset);

    /// <summary>The zero-based offset of a 1-based line/column pair.</summary>
    public static int OffsetOf(string text, int line, int column)
    {
        if (line <= 1) return Math.Clamp(column - 1, 0, text.Length);

        var current = 1;
        var index = 0;
        while (index < text.Length && current < line)
        {
            if (text[index] == '\n') current++;
            index++;
        }

        return Math.Clamp(index + Math.Max(0, column - 1), 0, text.Length);
    }

    /// <summary>The whitespace that starts the line the offset is on.</summary>
    public static string IndentAt(string text, int offset)
    {
        var start = offset;
        while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;

        var end = start;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
        return text[start..end];
    }

    /// <summary>The line ending the file already uses, so an insertion does not mix CRLF and LF.</summary>
    public static string NewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// The indentation unit of a file: the smallest increase between two consecutive TAG lines, so the
    /// alignment of a multi-line start tag's attributes is not mistaken for the unit.
    /// </summary>
    public static string IndentUnit(string text)
    {
        var indents = new List<string>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart()[0] != '<') continue;

            indents.Add(line[..(line.Length - line.TrimStart().Length)]);
        }

        string? smallest = null;
        foreach (var indent in indents)
        {
            if (indent.Length == 0) continue;
            if (smallest is null || indent.Length < smallest.Length) smallest = indent;
        }

        return smallest ?? "  ";
    }

    /// <summary>A canonical signature: elements, attributes and non-whitespace text, in document order.</summary>
    public static string Signature(string text)
    {
        var builder = new StringBuilder();
        var tags = Scan(text);

        // A line diff of tags is enough to prove that an edit did not change the document's shape:
        // the same tags with the same attributes in the same order.
        foreach (var tag in tags)
        {
            builder.Append(tag.IsClosing ? "/" : tag.IsSelfClosing ? "~" : string.Empty)
                .Append(tag.Name);

            foreach (var attribute in Attributes(text, tag))
                builder.Append(' ').Append(attribute.Name).Append("=\"").Append(attribute.Value(text)).Append('"');

            builder.Append('\u0001');
        }

        return builder.ToString();
    }
}
