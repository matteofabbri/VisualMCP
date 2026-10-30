using System.Text.RegularExpressions;

namespace Snitch.Languages.Razor;

/// <summary>
/// A Razor-aware scanner: it separates markup from C# so the rules can look at HTML elements and at
/// the expressions that feed them. It is deliberately not the Razor compiler — it recognises
/// directives, expressions, code blocks, control-flow blocks and an HTML element tree, and records
/// every node with its position. Markup inside <c>@{ … }</c> or <c>@code { … }</c> is C#-only as far
/// as this scanner is concerned and is not descended into.
/// </summary>
public static class RazorScanner
{
    public static RazorDocument Scan(string filePath, string text)
    {
        var scanner = new Scanner(filePath, text);
        scanner.Run();
        return scanner.Document;
    }

    private sealed class Scanner
    {
        private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr",
        };

        // Elements whose end tag HTML lets the parser omit; their absence is not a defect.
        private static readonly HashSet<string> OptionalEndTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "li", "dt", "dd", "option", "optgroup", "thead", "tbody", "tfoot", "tr", "td", "th",
        };

        private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset",
            "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
            "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
        };

        private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
        {
            "if", "else", "foreach", "for", "while", "switch", "do", "try", "catch", "finally",
            "lock", "using",
        };

        // Razor directives are case-sensitive; @Model is an expression, @model is a directive.
        private static readonly HashSet<string> Directives = new(StringComparer.Ordinal)
        {
            "page", "model", "using", "inject", "inherits", "layout", "namespace",
            "addTagHelper", "removeTagHelper", "tagHelperPrefix", "attribute", "implements",
            "typeparam", "rendermode", "preservewhitespace",
        };

        private static readonly Regex HandlerPattern = new(
            @"\b(On(?:Get|Post|Put|Delete|Patch|Head|Options)(?:Async)?)\s*\(",
            RegexOptions.Compiled);

        private static readonly Regex ForeachPattern = new(
            @"foreach\s*\(\s*(?:[^()]*?)\b(\w+)\s+in\b",
            RegexOptions.Compiled);

        private enum RawKind
        {
            None,
            Script,
            Style,
        }

        private readonly string _text;
        private readonly int _n;
        private readonly RazorDocument _document;
        private readonly Stack<RazorElement> _open = new();
        private int _i;
        private RawKind _rawKind;

        public Scanner(string filePath, string text)
        {
            _text = text;
            _n = text.Length;
            var lineStarts = RazorText.LineStarts(text);
            _document = new RazorDocument
            {
                FilePath = filePath,
                Text = text,
                LineStarts = lineStarts,
                LineCount = lineStarts.Length,
            };
        }

        public RazorDocument Document => _document;

        public void Run()
        {
            while (_i < _n)
            {
                var c = _text[_i];
                if (c == '@')
                {
                    ReadRazor();
                    continue;
                }
                if (c == '<' && TryReadTag())
                    continue;
                _i++;
            }

            while (_open.Count > 0)
            {
                var element = _open.Pop();
                element.EndOffset = _n;
                element.EndLine = _document.LineCount;
                if (!OptionalEndTags.Contains(element.Name))
                    AddIssue("RZ2001", $"<{element.Name}> is never closed.", element.Offset);
            }
        }

        // ── Markup ──────────────────────────────────────────────────────────────

        private bool TryReadTag()
        {
            var next = Peek(1);

            if (next == '!')
            {
                if (Peek(2) == '-' && Peek(3) == '-')
                {
                    var end = _text.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        AddIssue("RZ2015", "HTML comment is never closed.", _i);
                        _i = _n;
                    }
                    else
                    {
                        _i = end + 3;
                    }
                    return true;
                }

                SkipToChar('>', "Tag is never closed.");
                return true;
            }

            if (next == '/')
            {
                ReadEndTag();
                return true;
            }

            if (next == '?')
            {
                SkipToChar('>', "Processing instruction is never closed.");
                return true;
            }

            if (!char.IsLetter(next))
                return false;

            ReadStartTag();
            return true;
        }

        private void ReadStartTag()
        {
            var start = _i;
            _i++;
            var name = ReadTagName();
            var (line, column) = Position(start);
            var element = new RazorElement { Name = name, Offset = start, Line = line, Column = column };
            var selfClosing = false;
            var terminated = false;

            while (_i < _n)
            {
                SkipWhitespace();
                if (_i >= _n) break;

                var c = _text[_i];
                if (c == '>')
                {
                    _i++;
                    terminated = true;
                    break;
                }
                if (c == '/' && Peek(1) == '>')
                {
                    selfClosing = true;
                    terminated = true;
                    _i += 2;
                    break;
                }
                // A nested '<' means the start tag was never terminated; let the markup loop retry.
                if (c == '<')
                    break;

                ReadAttribute(element);
            }

            element.SelfClosing = selfClosing;
            element.IsVoid = VoidElements.Contains(name);
            element.EndOffset = _i;
            element.EndLine = Position(_i).Line;

            if (!terminated)
                AddIssue("RZ2015", $"The <{name}> start tag is never closed.", start);

            CloseImplicitly(name, start);
            Attach(element);

            if (selfClosing || element.IsVoid)
                return;

            _open.Push(element);
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("style", StringComparison.OrdinalIgnoreCase))
                ScanRawText(element);
        }

        private void ReadAttribute(RazorElement element)
        {
            var start = _i;
            var name = ReadAttributeName();
            if (name.Length == 0)
            {
                _i++;
                return;
            }

            SkipWhitespace();
            var hasValue = false;
            string? value = null;
            var hasRazor = false;

            if (_i < _n && _text[_i] == '=')
            {
                _i++;
                SkipWhitespace();
                if (_i < _n)
                    (value, hasRazor) = ReadAttributeValue();
                hasValue = true;
            }

            var (line, column) = Position(start);
            element.Attributes.Add(new RazorAttribute(name, value, hasValue, hasRazor, line, column, start));
        }

        private (string Value, bool HasRazor) ReadAttributeValue()
        {
            var start = _i;
            var hasRazor = false;
            var quote = _text[_i];

            if (quote is '"' or '\'')
            {
                _i++;
                var valueStart = _i;
                while (_i < _n)
                {
                    var c = _text[_i];
                    if (c == quote)
                    {
                        var value = _text[valueStart.._i];
                        _i++;
                        return (value, hasRazor);
                    }
                    if (c == '@')
                    {
                        if (Peek(1) == '@')
                        {
                            _i += 2;
                            continue;
                        }
                        hasRazor = true;
                        SkipRazorInValue();
                        continue;
                    }
                    _i++;
                }

                AddIssue("RZ2015", "Attribute value is never closed.", start);
                return (_text[valueStart.._n], hasRazor);
            }

            var unquotedStart = _i;
            while (_i < _n)
            {
                var c = _text[_i];
                if (char.IsWhiteSpace(c) || c == '>') break;
                if (c == '/' && Peek(1) == '>') break;
                if (c == '@')
                {
                    if (Peek(1) == '@')
                    {
                        _i += 2;
                        continue;
                    }
                    hasRazor = true;
                    SkipRazorInValue();
                    continue;
                }
                _i++;
            }
            return (_text[unquotedStart.._i], hasRazor);
        }

        private void SkipRazorInValue()
        {
            _i++; // '@'
            if (_i >= _n) return;

            if (_text[_i] == '(')
            {
                _i = SkipBalanced(_i, report: false);
                return;
            }
            if (_text[_i] == '*')
            {
                var end = _text.IndexOf("*@", _i + 1, StringComparison.Ordinal);
                _i = end < 0 ? _n : end + 2;
                return;
            }
            if (char.IsLetter(_text[_i]) || _text[_i] == '_')
            {
                ReadChain();
                return;
            }
            _i++;
        }

        private void ReadEndTag()
        {
            var start = _i;
            _i += 2;
            var name = ReadTagName();
            SkipWhitespace();
            if (_i < _n && _text[_i] == '>')
            {
                _i++;
            }
            else
            {
                AddIssue("RZ2015", $"The </{name}> end tag is malformed.", start);
                if (_i < _n) _i++;
            }

            if (name.Length == 0) return;

            if (_open.Count == 0)
            {
                AddIssue("RZ2002", $"</{name}> has no matching start tag.", start);
                return;
            }

            if (_open.Peek().Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var closed = _open.Pop();
                closed.EndOffset = _i;
                closed.EndLine = Position(_i).Line;
                return;
            }

            // The closing tag may belong to an ancestor: everything above it was never closed.
            var matchIndex = -1;
            var index = 0;
            foreach (var element in _open)
            {
                if (element.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = index;
                    break;
                }
                index++;
            }

            if (matchIndex < 0)
            {
                AddIssue("RZ2002", $"</{name}> does not match the open <{_open.Peek().Name}>.", start);
                return;
            }

            while (_open.Count > matchIndex)
            {
                var unclosed = _open.Pop();
                unclosed.EndOffset = start;
                unclosed.EndLine = Position(start).Line;
                if (!OptionalEndTags.Contains(unclosed.Name))
                    AddIssue("RZ2001", $"<{unclosed.Name}> is never closed.", unclosed.Offset);
            }

            var matched = _open.Pop();
            matched.EndOffset = _i;
            matched.EndLine = Position(_i).Line;
        }

        private void ScanRawText(RazorElement element)
        {
            var start = element.EndOffset;
            var previous = _rawKind;
            _rawKind = element.Name.Equals("script", StringComparison.OrdinalIgnoreCase)
                ? RawKind.Script
                : RawKind.Style;

            while (_i < _n)
            {
                if (_text[_i] == '<' && Peek(1) == '/' && MatchesTagNameAt(_i + 2, element.Name))
                    break;
                if (_text[_i] == '@')
                {
                    ReadRazor();
                    continue;
                }
                _i++;
            }

            element.RawText = _text[start.._i];
            _rawKind = previous;
        }

        private bool MatchesTagNameAt(int offset, string name)
        {
            if (offset + name.Length > _n) return false;
            if (string.Compare(_text, offset, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            var after = offset + name.Length;
            return after >= _n || char.IsWhiteSpace(_text[after]) || _text[after] == '>';
        }

        private void CloseImplicitly(string incomingName, int offset)
        {
            while (_open.Count > 0)
            {
                var top = _open.Peek();
                if (!OptionalEndTags.Contains(top.Name) || !ClosesImplicitly(top.Name, incomingName))
                    break;

                _open.Pop();
                top.EndOffset = offset;
                top.EndLine = Position(offset).Line;
            }
        }

        private static bool ClosesImplicitly(string open, string incoming)
        {
            if (open.Equals(incoming, StringComparison.OrdinalIgnoreCase))
                return OptionalEndTags.Contains(open);
            if (open.Equals("p", StringComparison.OrdinalIgnoreCase))
                return BlockElements.Contains(incoming);
            if (open.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                open.Equals("th", StringComparison.OrdinalIgnoreCase))
                return incoming is "td" or "th" or "tr";
            if (open.Equals("dt", StringComparison.OrdinalIgnoreCase) ||
                open.Equals("dd", StringComparison.OrdinalIgnoreCase))
                return incoming is "dt" or "dd";
            if (open.Equals("option", StringComparison.OrdinalIgnoreCase))
                return incoming is "option" or "optgroup";
            if (open is "thead" or "tbody" or "tfoot")
                return incoming is "thead" or "tbody" or "tfoot";
            return false;
        }

        private void Attach(RazorElement element)
        {
            if (_open.Count > 0)
            {
                var parent = _open.Peek();
                element.Parent = parent;
                parent.Children.Add(element);
            }
            else
            {
                _document.Elements.Add(element);
            }
        }

        // ── Razor ───────────────────────────────────────────────────────────────

        private void ReadRazor()
        {
            var at = _i;
            _i++;
            if (_i >= _n) return;

            var c = _text[_i];
            if (c == '@')
            {
                _i++;
                return;
            }
            if (c == '*')
            {
                ReadRazorComment(at);
                return;
            }
            if (c == '{')
            {
                ReadCSharpBlock(at, "block", null);
                return;
            }
            if (c == '(')
            {
                ReadExplicitExpression(at);
                return;
            }
            if (c == ':')
            {
                SkipToLineEnd();
                return;
            }
            if (!char.IsLetter(c) && c != '_')
            {
                _i++;
                return;
            }

            var identifierStart = _i;
            var identifier = ReadIdentifier();

            if (AtLineStart(at))
            {
                if (identifier is "code" or "functions")
                {
                    var afterName = _i;
                    SkipWhitespace();
                    if (_i < _n && _text[_i] == '{')
                    {
                        ReadCSharpBlock(at, identifier, null);
                        return;
                    }
                    _i = afterName;
                }

                if (identifier == "section")
                {
                    ReadSection(at);
                    return;
                }

                if (Directives.Contains(identifier))
                {
                    var afterName = _i;
                    if (identifier == "using")
                    {
                        SkipWhitespace();
                        if (_i < _n && _text[_i] == '(')
                        {
                            // "@using (Html.BeginForm()) { … }" is a statement, not a directive.
                            _i = afterName;
                            ReadControlBlock(at, identifier);
                            return;
                        }
                    }
                    _i = afterName;
                    ReadDirective(identifier, at);
                    return;
                }
            }

            if (ControlKeywords.Contains(identifier))
            {
                ReadControlBlock(at, identifier);
                return;
            }

            _i = identifierStart;
            ReadImplicitExpression(at);
        }

        private void ReadRazorComment(int at)
        {
            var end = _text.IndexOf("*@", _i + 1, StringComparison.Ordinal);
            if (end < 0)
            {
                AddIssue("RZ2015", "Razor comment is never closed.", at);
                _i = _n;
            }
            else
            {
                _i = end + 2;
            }
        }

        private void ReadExplicitExpression(int at)
        {
            var open = _i;
            var end = SkipBalanced(open, report: true);
            var innerEnd = end > open + 1 && _text[end - 1] == ')' ? end - 1 : end;
            AddExpression(_text[(open + 1)..innerEnd], at, explicitExpression: true);
            _i = end;
        }

        private void ReadImplicitExpression(int at)
        {
            var start = _i;
            if (_i >= _n) return;

            var identifierStart = _i;
            var word = ReadIdentifier();
            if (word == "await")
            {
                SkipWhitespace();
            }
            else
            {
                _i = identifierStart;
            }

            ReadChain();
            if (_i <= start) return;

            var text = _text[start.._i].Trim();
            if (text.Length > 0)
                AddExpression(text, at, explicitExpression: false);
        }

        private void ReadChain()
        {
            if (!char.IsLetter(Peek(0)) && Peek(0) != '_')
                return;

            ReadIdentifier();
            while (_i < _n)
            {
                var c = _text[_i];
                if (c == '.' && (char.IsLetter(Peek(1)) || Peek(1) == '_'))
                {
                    _i++;
                    ReadIdentifier();
                    continue;
                }
                if (c == '?' && Peek(1) == '.')
                {
                    _i += 2;
                    if (char.IsLetter(Peek(0)) || Peek(0) == '_')
                    {
                        ReadIdentifier();
                        continue;
                    }
                    break;
                }
                if (c == '!' && Peek(1) != '=')
                {
                    _i++;
                    continue;
                }
                if (c is '(' or '[')
                {
                    _i = SkipBalanced(_i, report: false);
                    continue;
                }
                break;
            }
        }

        private void AddExpression(string text, int at, bool explicitExpression)
        {
            var (line, column) = Position(at);
            _document.Expressions.Add(new RazorExpression(
                text.Trim(), at, line, column, _rawKind == RawKind.Script, explicitExpression));
        }

        private void ReadCSharpBlock(int at, string kind, string? name)
        {
            var open = _i;
            var end = SkipBalanced(open, report: true);
            var innerEnd = end > open + 1 && _text[end - 1] == '}' ? end - 1 : end;
            var inner = _text[(open + 1)..innerEnd];
            var (line, _) = Position(at);
            var (endLine, _) = Position(Math.Max(at, end - 1));

            _document.CodeBlocks.Add(new RazorCodeBlock(kind, name, inner, inner, at, end, line, endLine));
            _i = end;
            CollectHandlers(inner);
        }

        private void ReadControlBlock(int at, string keyword)
        {
            SkipWhitespace();
            if (_i < _n && _text[_i] == '(')
                _i = SkipBalanced(_i, report: true);
            SkipWhitespace();
            if (_i < _n && _text[_i] == '{')
                _i++;

            var header = _text[at.._i].Trim();
            var (line, _) = Position(at);
            _document.CodeBlocks.Add(new RazorCodeBlock(
                "control", keyword, string.Empty, header, at, _i, line, Position(_i).Line));

            RegisterLoopVariables(header);
        }

        private void ReadSection(int at)
        {
            SkipWhitespace();
            var name = ReadIdentifier();
            SkipWhitespace();
            var hasBrace = _i < _n && _text[_i] == '{';
            if (hasBrace) _i++;
            else AddIssue("RZ2015", $"@section {name} has no body.", at);

            if (name.Length > 0)
                _document.Sections.Add(name);

            var (line, _) = Position(at);
            _document.CodeBlocks.Add(new RazorCodeBlock(
                "section", name, string.Empty, _text[at.._i].Trim(), at, _i, line, Position(_i).Line));
        }

        private void ReadDirective(string name, int at)
        {
            while (_i < _n && (_text[_i] == ' ' || _text[_i] == '\t'))
                _i++;

            var end = _text.IndexOf('\n', _i);
            if (end < 0) end = _n;

            var value = _text[_i..end].TrimEnd('\r').Trim();
            _i = end;

            var (line, column) = Position(at);
            _document.Directives.Add(new RazorDirective(name, value, line, column, at));

            switch (name)
            {
                case "page":
                    _document.Route = Unquote(value);
                    break;
                case "model":
                    _document.ModelType = Unquote(value);
                    break;
                case "inherits":
                    _document.Inherits = Unquote(value);
                    break;
                case "layout":
                    _document.LayoutDirective = Unquote(value);
                    break;
                case "namespace":
                    _document.Namespace = Unquote(value);
                    break;
                case "using":
                    _document.Usings.Add(value.TrimEnd(';'));
                    break;
                case "inject":
                    AddInjection(value);
                    break;
                case "attribute":
                    if (value.Contains("IgnoreAntiforgeryToken", StringComparison.Ordinal))
                        _document.IgnoresAntiForgery = true;
                    break;
                case "addTagHelper":
                case "removeTagHelper":
                case "tagHelperPrefix":
                    _document.TagHelpers.Add($"{name} {value}".Trim());
                    break;
            }
        }

        private void AddInjection(string value)
        {
            var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            if (parts.Length == 1)
                _document.Injections.Add(new RazorInjection(parts[0], string.Empty));
            else
                _document.Injections.Add(new RazorInjection(
                    string.Join(' ', parts[..^1]), parts[^1]));
        }

        private void CollectHandlers(string text)
        {
            foreach (Match match in HandlerPattern.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!_document.Handlers.Contains(name, StringComparer.Ordinal))
                    _document.Handlers.Add(name);
            }
        }

        private void RegisterLoopVariables(string header)
        {
            var match = ForeachPattern.Match(header);
            if (!match.Success) return;

            var variable = match.Groups[1].Value;
            if (variable is not ("in" or "var"))
                _document.LoopVariables.Add(variable);
        }

        // ── C# skipping ─────────────────────────────────────────────────────────

        /// <summary>
        /// Skips a balanced C# construct starting at <paramref name="start"/>, honouring strings,
        /// chars, comments and nested brackets. Returns the index just past the closing character.
        /// </summary>
        private int SkipBalanced(int start, bool report)
        {
            var open = _text[start];
            var close = open switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
            if (close == '\0') return start + 1;

            var depth = 0;
            var i = start;
            while (i < _n)
            {
                var c = _text[i];

                if (c == '/' && PeekAt(i, 1) == '/')
                {
                    var newline = _text.IndexOf('\n', i + 2);
                    i = newline < 0 ? _n : newline;
                    continue;
                }
                if (c == '/' && PeekAt(i, 1) == '*')
                {
                    var end = _text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? _n : end + 2;
                    continue;
                }
                if (c == '@' && PeekAt(i, 1) == '"')
                {
                    i = SkipVerbatimString(i + 1);
                    continue;
                }
                if (c == '$' && PeekAt(i, 1) == '@' && PeekAt(i, 2) == '"')
                {
                    i = SkipInterpolatedString(i + 2);
                    continue;
                }
                if (c == '@' && PeekAt(i, 1) == '$' && PeekAt(i, 2) == '"')
                {
                    i = SkipInterpolatedString(i + 2);
                    continue;
                }
                if (c == '$' && PeekAt(i, 1) == '"')
                {
                    i = SkipInterpolatedString(i + 1);
                    continue;
                }
                if (c == '"')
                {
                    i = SkipQuotedString(i);
                    continue;
                }
                if (c == '\'')
                {
                    i = SkipCharLiteral(i);
                    continue;
                }

                if (c == open)
                {
                    depth++;
                }
                else if (c == close)
                {
                    depth--;
                    if (depth == 0)
                        return i + 1;
                }
                i++;
            }

            if (report)
                AddIssue("RZ2015", $"The C# block starting with '{open}' is never closed.", start);
            return _n;
        }

        private int SkipQuotedString(int i)
        {
            i++;
            while (i < _n)
            {
                if (_text[i] == '\\') { i += 2; continue; }
                if (_text[i] == '"') return i + 1;
                i++;
            }
            return _n;
        }

        private int SkipVerbatimString(int i)
        {
            i++;
            while (i < _n)
            {
                if (_text[i] != '"') { i++; continue; }
                if (PeekAt(i, 1) == '"') { i += 2; continue; }
                return i + 1;
            }
            return _n;
        }

        private int SkipInterpolatedString(int quoteIndex)
        {
            var i = quoteIndex + 1;
            while (i < _n)
            {
                var c = _text[i];
                if (c == '\\') { i += 2; continue; }
                if (c == '"') return i + 1;
                if (c == '{')
                {
                    if (PeekAt(i, 1) == '{') { i += 2; continue; }
                    i = SkipBalanced(i, report: false);
                    continue;
                }
                i++;
            }
            return _n;
        }

        private int SkipCharLiteral(int i)
        {
            i++;
            while (i < _n)
            {
                if (_text[i] == '\\') { i += 2; continue; }
                if (_text[i] == '\'') return i + 1;
                i++;
            }
            return _n;
        }

        // ── Lexing helpers ──────────────────────────────────────────────────────

        private char Peek(int offset) => _i + offset < _n ? _text[_i + offset] : '\0';

        private char PeekAt(int index, int offset) =>
            index + offset < _n ? _text[index + offset] : '\0';

        private string ReadIdentifier()
        {
            var start = _i;
            if (_i < _n && (char.IsLetter(_text[_i]) || _text[_i] == '_'))
                _i++;
            else
                return string.Empty;

            while (_i < _n && (char.IsLetterOrDigit(_text[_i]) || _text[_i] == '_'))
                _i++;
            return _text[start.._i];
        }

        private string ReadTagName()
        {
            var start = _i;
            while (_i < _n)
            {
                var c = _text[_i];
                if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or ':' or '.'))
                    break;
                _i++;
            }
            return _text[start.._i];
        }

        private string ReadAttributeName()
        {
            var start = _i;
            while (_i < _n)
            {
                var c = _text[_i];
                if (char.IsWhiteSpace(c) || c is '=' or '>' or '"' or '\'' or '<' or '/')
                    break;
                _i++;
            }
            return _text[start.._i];
        }

        private void SkipWhitespace()
        {
            while (_i < _n && char.IsWhiteSpace(_text[_i]))
                _i++;
        }

        private void SkipToLineEnd()
        {
            while (_i < _n && _text[_i] != '\n')
                _i++;
        }

        private void SkipToChar(char c, string message)
        {
            var end = _text.IndexOf(c, _i);
            if (end < 0)
            {
                AddIssue("RZ2015", message, _i);
                _i = _n;
            }
            else
            {
                _i = end + 1;
            }
        }

        private bool AtLineStart(int offset)
        {
            for (var i = offset - 1; i >= 0; i--)
            {
                if (_text[i] is ' ' or '\t') continue;
                return _text[i] is '\n' or '\r';
            }
            return true;
        }

        private static string? Unquote(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[^1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[^1] == '\'')))
                return trimmed[1..^1];
            return trimmed;
        }

        private (int Line, int Column) Position(int offset) => RazorText.Position(_document.LineStarts, offset);

        private void AddIssue(string ruleId, string message, int offset)
        {
            var (line, column) = Position(offset);
            _document.SyntaxIssues.Add(new RazorSyntaxIssue(ruleId, message, line, column, offset));
        }
    }
}
