using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Snitch.Languages.CSharp.ContextActions;

public sealed partial class ContextActionService
{
    private enum StringTarget
    {
        Regular,
        Verbatim,
        Raw,
    }

    /// <summary>
    /// ReSharper's string-literal conversions. The value is taken from the semantic model, so the escapes
    /// are re-encoded from the string the compiler sees instead of from the source spelling: converting
    /// <c>"a\nb"</c> to raw produces a raw literal that holds a real newline, which is the point of the
    /// conversion.
    /// </summary>
    private Outcome ConvertString(Target target, StringTarget wanted)
    {
        if (target.Root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>()
                .Any(node => LineOf(node) == target.Line))
        {
            return Outcome.Fail("The literal on this line is interpolated: conversion is supported for plain literals only.");
        }

        var literal = target.Root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression) && LineOf(node) == target.Line)
            .OrderBy(node => node.Span.Length)
            .FirstOrDefault();

        if (literal is null)
            return Outcome.Fail($"No string literal on line {target.Line}.");

        var constant = target.Model.GetConstantValue(literal);
        if (!constant.HasValue || constant.Value is not string value)
            return Outcome.Fail("The literal has no compile-time value.");

        var spelling = literal.Token.Text;
        var form = spelling.StartsWith("\"\"\"", StringComparison.Ordinal) ? StringTarget.Raw
            : spelling.StartsWith("@", StringComparison.Ordinal) ? StringTarget.Verbatim
            : StringTarget.Regular;

        if (form == wanted)
            return Outcome.Fail($"The literal on line {target.Line} is already {Describe(wanted)}.");

        var newLine = NewLineOf(target.Text.ToString());
        var encoded = wanted switch
        {
            StringTarget.Raw => ToRaw(value, IndentationAt(target.Text, target.Line), newLine),
            StringTarget.Verbatim => ToVerbatim(value),
            _ => ToRegular(value),
        };

        var text = target.Text.ToString()
            .Remove(literal.SpanStart, literal.Span.Length)
            .Insert(literal.SpanStart, encoded);

        return new Outcome(text, [$"{Describe(form)} → {Describe(wanted)}"]);
    }

    /// <summary>
    /// A raw literal: the delimiter grows past the longest run of quotes in the value, and a multi-line
    /// value keeps its lines by indenting them to the closing delimiter, which is what the lexer strips.
    /// </summary>
    private static string ToRaw(string value, string indentation, string newLine)
    {
        var quotes = 3;
        var run = 0;
        foreach (var character in value)
        {
            if (character == '"')
            {
                run++;
                quotes = Math.Max(quotes, run + 1);
            }
            else
            {
                run = 0;
            }
        }

        var delimiter = new string('"', quotes);
        if (!value.Contains('\n', StringComparison.Ordinal))
            return delimiter + value + delimiter;

        var body = string.Join(
            newLine,
            value.Split('\n').Select(line => indentation + line.TrimEnd('\r')));

        return delimiter + newLine + body + newLine + indentation + delimiter;
    }

    /// <summary><c>@"…"</c>: a quote is doubled, and the value's own newlines stay newlines.</summary>
    private static string ToVerbatim(string value) =>
        "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary><c>"…"</c>: every character that is not printable ASCII is escaped.</summary>
    private static string ToRegular(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (char.IsControl(character))
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string Describe(StringTarget target) => target switch
    {
        StringTarget.Raw => "a raw string",
        StringTarget.Verbatim => "a verbatim string",
        _ => "a regular string",
    };
}
