using System.Text;

namespace Snitch.Metacode.Decompilation;

/// <summary>
/// Turns the C that Ghidra's decompiler prints into the canonical pseudocode this library reads back.
/// <para>
/// This is the "normalize first" choice, and it is deliberate: <see cref="Frontends.CppFrontend"/>
/// reads clang's <c>-ast-dump=json</c>, not C text, so handing it Ghidra's output would mean running
/// clang on code that is not valid C++ (<c>undefined8</c>, <c>FUN_140001008</c>, <c>LAB_...</c> are
/// Ghidra's own names). Normalizing instead reuses <see cref="Frontends.PseudocodeFrontend"/> — the
/// documented door for "a language this library has no frontend for" — so there stays one reader for
/// one format. The normalizer is a real C reader, not a text replacement: it parses declarations,
/// statements and expressions, so a cast stays a cast and <c>p-&gt;f</c> becomes <c>p.f</c> instead of
/// a string edit that would silently change what the code says.
/// </para>
/// <para>
/// What is dropped, and why: comments (Ghidra's <c>/* WARNING */</c> notes are about the decompiler,
/// not the program), calling conventions (<c>__fastcall</c> is not in the model), and the size of a
/// local array, which stays inside its type name (<c>DECLARE auStackY_88 : undefined1[32]</c>). A
/// construct with no equivalent — an assignment inside an expression, an inline assembly line — becomes
/// a <c>RAW[cpp]</c> line with the reason instead of a guess.
/// </para>
/// </summary>
internal static class GhidraC
{
    /// <summary>The pseudocode of one function, plus what the reader could not place.</summary>
    public sealed record Result(string Pseudocode, IReadOnlyList<string> Warnings);

    public static Result Parse(GhidraFunction function)
    {
        var warnings = new List<string>();
        var tokens = CTokenizer.Tokenize(function.Decompiled ?? string.Empty);
        if (tokens.Count == 0)
        {
            return new Result(
                $"FUNCTION {function.Name}()\nEND FUNCTION\n",
                new[] { "the decompiler produced no C for this function" });
        }

        var parser = new CFunctionParser(tokens, warnings);
        return new Result(parser.Parse(function.Name), warnings);
    }

    // ------------------------------------------------------------------ tokens

    internal enum CTokenKind
    {
        Identifier,
        Number,
        String,
        Character,
        Punctuation,
    }

    internal readonly record struct CToken(string Text, CTokenKind Kind)
    {
        public bool Is(string text) => Text == text;

        public bool IsIdentifier => Kind == CTokenKind.Identifier;
    }

    internal static class CTokenizer
    {
        private static readonly string[] MultiCharacter =
        {
            "<<=", ">>=", "...", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=",
            "&&", "||", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "::",
        };

        public static List<CToken> Tokenize(string text)
        {
            var tokens = new List<CToken>(text.Length / 4);
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                // Comments are the decompiler talking about itself; they are not part of the program.
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? text.Length : close + 2;
                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    var newline = text.IndexOf('\n', i);
                    i = newline < 0 ? text.Length : newline + 1;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    var start = i;
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (text[i] == c)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    var end = Math.Min(i, text.Length);
                    tokens.Add(new CToken(text[start..end], c == '"' ? CTokenKind.String : CTokenKind.Character));
                    continue;
                }

                if (char.IsDigit(c))
                {
                    var start = i;
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'
                                               || (text[i] == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]))))
                        i++;
                    tokens.Add(new CToken(text[start..i], CTokenKind.Number));
                    continue;
                }

                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var start = i;
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
                        i++;

                    // `L"text"`, `L'\0'`, `u8"text"`: a wide literal is one token, not a name and a literal.
                    if (i - start <= 2 && i < text.Length && text[i] is '"' or '\''
                        && text[start..i] is "L" or "u" or "U" or "u8")
                    {
                        var quote = text[i];
                        i++;
                        while (i < text.Length)
                        {
                            if (text[i] == '\\')
                            {
                                i += 2;
                                continue;
                            }

                            if (text[i] == quote)
                            {
                                i++;
                                break;
                            }

                            i++;
                        }

                        var end = Math.Min(i, text.Length);
                        tokens.Add(new CToken(text[start..end], quote == '"' ? CTokenKind.String : CTokenKind.Character));
                        continue;
                    }

                    tokens.Add(new CToken(text[start..i], CTokenKind.Identifier));
                    continue;
                }

                var matched = false;
                foreach (var op in MultiCharacter)
                {
                    if (i + op.Length > text.Length || string.CompareOrdinal(text, i, op, 0, op.Length) != 0)
                        continue;

                    tokens.Add(new CToken(op, CTokenKind.Punctuation));
                    i += op.Length;
                    matched = true;
                    break;
                }

                if (matched)
                    continue;

                tokens.Add(new CToken(c.ToString(), CTokenKind.Punctuation));
                i++;
            }

            return tokens;
        }
    }

    // ------------------------------------------------------------------ the function

    private sealed class CFunctionParser
    {
        private static readonly HashSet<string> CallingConventions = new(StringComparer.Ordinal)
        {
            "__fastcall", "__cdecl", "__stdcall", "__thiscall", "__vectorcall", "__thunk", "WEAK", "static", "extern", "inline",
        };

        private readonly List<CToken> _tokens;
        private readonly List<string> _warnings;
        private readonly StringBuilder _out = new();
        private int _index;

        public CFunctionParser(List<CToken> tokens, List<string> warnings)
        {
            _tokens = tokens;
            _warnings = warnings;
        }

        public string Name { get; private set; } = "?";

        public string ReturnType { get; private set; } = "void";

        public bool HasBody { get; private set; }

        public string Parse(string fallbackName)
        {
            var open = FindOpenBrace();

            // No body: the decompiler gave a signature and nothing else.
            if (open < 0)
            {
                _warnings.Add("the decompiler printed no function body");
                return $"FUNCTION {fallbackName}()\nEND FUNCTION\n";
            }

            var signature = _tokens.GetRange(0, open);
            var (name, returnType, parameters) = ReadSignature(signature, fallbackName);
            Name = name;
            ReturnType = returnType;
            HasBody = true;

            Line($"FUNCTION {Name}({parameters})"
                 + (returnType.Length > 0 && returnType != "void" ? " : " + returnType : string.Empty));
            _index = open + 1;
            ParseBlock(1);
            Line("END FUNCTION");
            return _out.ToString();
        }

        private (string Name, string ReturnType, string Parameters) ReadSignature(List<CToken> signature, string fallback)
        {
            var open = signature.FindIndex(t => t.Is("("));
            if (open < 0)
                return (fallback, "void", string.Empty);

            var close = Matching(signature, open);
            var nameIndex = open - 1;
            while (nameIndex >= 0 && !signature[nameIndex].IsIdentifier)
                nameIndex--;

            var name = nameIndex >= 0 ? signature[nameIndex].Text : fallback;
            var returnTokens = nameIndex > 0
                ? signature.GetRange(0, nameIndex).Where(t => !CallingConventions.Contains(t.Text)).ToList()
                : new List<CToken>();
            var returnType = TypeText(returnTokens);

            var parameters = new List<string>();
            foreach (var part in SplitTopLevel(signature.GetRange(open + 1, Math.Max(0, close - open - 1)), ","))
            {
                if (part.Count == 0)
                    continue;

                // `void` alone means no parameters; `...` is a variadic tail.
                if (part.Count == 1 && part[0].Is("void"))
                    continue;

                var last = part[^1];
                if (!last.IsIdentifier)
                {
                    parameters.Add(part.Count == 1 ? part[0].Text : TypeText(part));
                    continue;
                }

                var type = TypeText(part.GetRange(0, part.Count - 1));
                parameters.Add(last.Text + (type.Length > 0 ? " : " + type : string.Empty));
            }

            return (name, returnType.Length == 0 ? "void" : returnType, string.Join(", ", parameters));
        }

        // -------------------------------------------------------------- statements

        /// <summary>Parses statements until the brace that closes this block.</summary>
        private void ParseBlock(int depth)
        {
            while (_index < _tokens.Count)
            {
                if (Peek().Is("}"))
                {
                    _index++;
                    return;
                }

                ParseStatement(depth);
            }
        }

        private void ParseStatement(int depth)
        {
            if (_index >= _tokens.Count)
                return;

            var token = Peek();
            switch (token.Text)
            {
                case "{":
                    _index++;
                    ParseBlock(depth);
                    return;

                case ";":
                    _index++;
                    return;

                case "if":
                    ParseIf(depth);
                    return;

                case "while":
                    ParseWhile(depth);
                    return;

                case "do":
                    ParseDo(depth);
                    return;

                case "for":
                    ParseFor(depth);
                    return;

                case "switch":
                    ParseSwitch(depth);
                    return;

                case "return":
                {
                    _index++;
                    var value = ReadToSemicolon();
                    Line(value.Count == 0 ? "RETURN" : "RETURN " + Expression(value), depth);
                    return;
                }

                case "break":
                    SkipToSemicolon();
                    Line("BREAK", depth);
                    return;

                case "continue":
                    SkipToSemicolon();
                    Line("CONTINUE", depth);
                    return;

                case "goto":
                {
                    _index++;
                    var label = _index < _tokens.Count ? _tokens[_index++].Text : "?";
                    SkipToSemicolon();
                    Line("GOTO " + label, depth);
                    return;
                }
            }

            // `LAB_1400019a0:` names the statement that follows it.
            if (token.IsIdentifier && Next().Is(":"))
            {
                _index += 2;
                Line("LABEL " + token.Text, depth);
                ParseStatement(depth + 1);
                Line("END LABEL", depth);
                return;
            }

            var statement = ReadToSemicolon();
            if (statement.Count == 0)
                return;

            EmitSimpleStatement(statement, depth);
        }

        private void ParseIf(int depth)
        {
            _index++;
            var condition = ReadCondition();
            Line("IF " + Condition(condition) + " THEN", depth);

            ParseBody(depth + 1);
            SkipStraySemicolon();

            if (_index < _tokens.Count && Peek().Is("else"))
            {
                _index++;
                Line("ELSE", depth);
                if (Peek().Is("if"))
                    ParseIf(depth + 1);
                else
                    ParseBody(depth + 1);
            }

            Line("END IF", depth);
        }

        private void ParseWhile(int depth)
        {
            _index++;
            var condition = ReadCondition();
            Line("WHILE " + Condition(condition) + " DO", depth);
            ParseBody(depth + 1);
            Line("END WHILE", depth);
        }

        private void ParseDo(int depth)
        {
            _index++;
            Line("REPEAT", depth);
            ParseBody(depth + 1);

            if (_index < _tokens.Count && Peek().Is("while"))
            {
                _index++;
                var condition = ReadCondition();
                SkipToSemicolon();
                Line("UNTIL " + Condition(condition), depth);
            }
            else
            {
                Line("UNTIL true", depth);
                _warnings.Add("a do/while without its condition");
            }
        }

        private void ParseFor(int depth)
        {
            _index++;
            if (!Peek().Is("("))
            {
                var rest = ReadToSemicolon();
                Opaque(depth, rest, "for without a parenthesised header");
                return;
            }

            var close = Matching(_tokens, _index);
            var parts = SplitTopLevel(_tokens.GetRange(_index + 1, Math.Max(0, close - _index - 1)), ";");
            _index = Math.Min(close + 1, _tokens.Count);

            var initializer = parts.Count > 0 ? InlineStatement(parts[0]) : string.Empty;
            var condition = parts.Count > 1 ? Expression(parts[1]) : string.Empty;
            var increment = parts.Count > 2 ? InlineStatement(parts[2]) : string.Empty;

            Line($"FOR {initializer}; {condition}; {increment} DO", depth);
            ParseBody(depth + 1);
            Line("END FOR", depth);
        }

        private void ParseSwitch(int depth)
        {
            _index++;
            var subject = ReadCondition();
            Line("SWITCH " + Condition(subject), depth);

            // The case loop below owns the body; all it needs is the opening brace.
            if (_index >= _tokens.Count || !Peek().Is("{"))
            {
                var rest = ReadToSemicolon();
                Opaque(depth, rest, "switch without a braced body");
                Line("END SWITCH", depth);
                return;
            }

            _index++;
            var labels = new List<string>();
            var isDefault = false;
            var hasBody = false;

            void Flush()
            {
                if (labels.Count == 0 && !isDefault)
                    return;

                Line(isDefault ? "DEFAULT" : "CASE " + string.Join(", ", labels), depth + 1);
                labels = new List<string>();
                isDefault = false;
                hasBody = false;
            }

            while (_index < _tokens.Count && !Peek().Is("}"))
            {
                if (Peek().Is("case"))
                {
                    // Consecutive labels share one body: `case 0: case 1: ...` is one case with two labels.
                    if (hasBody)
                        Flush();
                    _index++;
                    var label = ReadUntil(":");
                    if (label.Count > 0)
                        labels.Add(Expression(label));
                    hasBody = false;
                    continue;
                }

                if (Peek().Is("default") && Next().Is(":"))
                {
                    if (hasBody)
                        Flush();
                    _index += 2;
                    isDefault = true;
                    hasBody = false;
                    continue;
                }

                hasBody = true;
                ParseStatement(depth + 2);
            }

            Flush();
            if (_index < _tokens.Count && Peek().Is("}"))
                _index++;
            Line("END SWITCH", depth);
        }

        private void EmitSimpleStatement(List<CToken> statement, int depth)
        {
            if (TryDeclaration(statement, depth))
                return;

            var assignment = FindTopLevelAssignment(statement);
            if (assignment >= 0)
            {
                var left = statement.GetRange(0, assignment);
                var right = statement.GetRange(assignment + 1, statement.Count - assignment - 1);
                Line(Expression(left) + " " + statement[assignment].Text + " " + Expression(right), depth);
                return;
            }

            Line(Expression(statement), depth);
        }

        /// <summary>A local declaration: <c>type name [size];</c> or <c>type name = value;</c>.</summary>
        private bool TryDeclaration(List<CToken> statement, int depth)
        {
            var equals = FindTopLevelAssignment(statement);
            var head = equals < 0 ? statement : statement.GetRange(0, equals);

            // `undefined1 auStackY_88 [32]`: the size is part of the type the model keeps.
            var size = string.Empty;
            while (head.Count > 1 && head[^1].Is("]"))
            {
                var open = LastTopLevelOpen(head, "[");
                if (open < 0)
                    break;

                size = "[" + Join(head.GetRange(open + 1, head.Count - open - 2)) + "]" + size;
                head = head.GetRange(0, open);
            }

            if (head.Count < 2 || !head[^1].IsIdentifier)
                return false;

            // A type word has to be there: `*DAT_180007658 = 0;` is a store through a pointer, not a
            // declaration of a variable named DAT_180007658.
            var hasTypeWord = false;
            for (var i = 0; i < head.Count - 1; i++)
            {
                var token = head[i];
                if (token.IsIdentifier)
                    hasTypeWord = true;
                else if (!token.Is("*") && !token.Is("&") && !token.Is("const") && !token.Is("volatile"))
                    return false;
            }

            if (!hasTypeWord)
                return false;

            var name = head[^1].Text;
            var type = TypeText(head.GetRange(0, head.Count - 1)) + size;
            var initializer = equals < 0
                ? null
                : Expression(statement.GetRange(equals + 1, statement.Count - equals - 1));
            Line("DECLARE " + name + (type.Length > 0 ? " : " + type : string.Empty)
                 + (initializer is null ? string.Empty : " = " + initializer), depth);
            return true;
        }

        private string InlineStatement(List<CToken> tokens)
        {
            if (tokens.Count == 0)
                return string.Empty;

            if (TryInlineDeclaration(tokens, out var declaration))
                return declaration;

            var assignment = FindTopLevelAssignment(tokens);
            if (assignment >= 0)
            {
                return Expression(tokens.GetRange(0, assignment)) + " " + tokens[assignment].Text + " "
                       + Expression(tokens.GetRange(assignment + 1, tokens.Count - assignment - 1));
            }

            return Expression(tokens);
        }

        private bool TryInlineDeclaration(List<CToken> tokens, out string declaration)
        {
            declaration = string.Empty;
            var equals = FindTopLevelAssignment(tokens);
            var head = equals < 0 ? tokens : tokens.GetRange(0, equals);
            if (head.Count < 2 || !head[^1].IsIdentifier)
                return false;

            var hasTypeWord = false;
            for (var i = 0; i < head.Count - 1; i++)
            {
                var token = head[i];
                if (token.IsIdentifier)
                    hasTypeWord = true;
                else if (!token.Is("*") && !token.Is("&"))
                    return false;
            }

            if (!hasTypeWord)
                return false;

            var type = TypeText(head.GetRange(0, head.Count - 1));
            declaration = "DECLARE " + head[^1].Text + (type.Length > 0 ? " : " + type : string.Empty)
                          + (equals < 0
                              ? string.Empty
                              : " = " + Expression(tokens.GetRange(equals + 1, tokens.Count - equals - 1)));
            return true;
        }

        // -------------------------------------------------------------- helpers

        private string Condition(List<CToken> tokens)
        {
            var text = Expression(tokens);
            return text.Length == 0 ? "true" : text;
        }

        /// <summary>
        /// The single boundary every emitted expression goes through. If any part of it had to become
        /// opaque, the whole expression becomes one <c>RAW</c>: a marker inside a bracket would be read
        /// back as text that swallows the closing bracket, so the fragment never survives on its own.
        /// </summary>
        private string Expression(List<CToken> tokens)
        {
            if (tokens.Count == 0)
                return string.Empty;

            var text = new CExpression(tokens, _warnings).Parse();
            if (text.StartsWith("RAW[cpp] ", StringComparison.Ordinal))
                return text;

            return text.Contains("RAW[cpp] ", StringComparison.Ordinal)
                ? "RAW[cpp] " + Join(tokens)
                : text;
        }

        /// <summary>The parenthesised expression after <c>if</c>/<c>while</c>/<c>switch</c>.</summary>
        private List<CToken> ReadCondition()
        {
            if (!Peek().Is("("))
                return ReadToSemicolon();

            var close = Matching(_tokens, _index);
            var condition = _tokens.GetRange(_index + 1, Math.Max(0, close - _index - 1));
            _index = Math.Min(close + 1, _tokens.Count);
            return condition;
        }

        private List<CToken> ReadToSemicolon()
        {
            var start = _index;
            var depth = 0;
            while (_index < _tokens.Count)
            {
                var token = Peek();
                if (token.Is("(") || token.Is("[") || token.Is("{"))
                    depth++;
                else if (token.Is(")") || token.Is("]") || token.Is("}"))
                {
                    depth--;
                    if (depth < 0)
                        break;
                }
                else if (token.Is(";") && depth == 0)
                {
                    var slice = _tokens.GetRange(start, _index - start);
                    _index++;
                    return slice;
                }

                _index++;
            }

            return _tokens.GetRange(start, Math.Max(0, _index - start));
        }

        private List<CToken> ReadUntil(string terminator)
        {
            var start = _index;
            var depth = 0;
            while (_index < _tokens.Count)
            {
                var token = Peek();
                if (token.Is("(") || token.Is("[") || token.Is("{"))
                    depth++;
                else if (token.Is(")") || token.Is("]") || token.Is("}"))
                    depth--;
                else if (token.Is(terminator) && depth == 0)
                {
                    var slice = _tokens.GetRange(start, _index - start);
                    _index++;
                    return slice;
                }

                _index++;
            }

            return _tokens.GetRange(start, Math.Max(0, _index - start));
        }

        private void SkipToSemicolon()
        {
            while (_index < _tokens.Count && !Peek().Is(";"))
                _index++;
            if (_index < _tokens.Count)
                _index++;
        }

        private void SkipStraySemicolon()
        {
            if (_index < _tokens.Count && Peek().Is(";"))
                _index++;
        }

        /// <summary>
        /// The body of a control-flow statement: a braced block, or exactly one statement. Parsing the
        /// enclosing block after an unbraced body would swallow the statements that follow the
        /// statement into its body — the bug that turns `if (x) goto L; if (y) break;` into two nested ifs.
        /// </summary>
        private void ParseBody(int depth)
        {
            if (_index < _tokens.Count && Peek().Is("{"))
            {
                _index++;
                ParseBlock(depth);
                return;
            }

            ParseStatement(depth);
        }

        private void Opaque(int depth, List<CToken> text, string reason)
        {
            Line("RAW[cpp] " + Join(text) + " // " + reason, depth);
            _warnings.Add(reason);
        }

        private CToken Peek() => _index < _tokens.Count ? _tokens[_index] : new CToken(string.Empty, CTokenKind.Punctuation);

        private CToken Next() => _index + 1 < _tokens.Count ? _tokens[_index + 1] : new CToken(string.Empty, CTokenKind.Punctuation);

        private int FindOpenBrace()
        {
            var depth = 0;
            for (var i = 0; i < _tokens.Count; i++)
            {
                var token = _tokens[i];
                if (token.Is("(") || token.Is("["))
                    depth++;
                else if (token.Is(")") || token.Is("]"))
                    depth--;
                else if (depth == 0 && token.Is("{"))
                    return i;
            }

            return -1;
        }

        private void Line(string text, int depth = 0) =>
            _out.Append(new string(' ', depth * 2)).Append(text).Append('\n');

        private static string Join(List<CToken> tokens) => string.Join(" ", tokens.Select(t => t.Text));

        private static int LastTopLevelOpen(List<CToken> tokens, string open)
        {
            var depth = 0;
            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                if (tokens[i].Is("]"))
                    depth++;
                else if (tokens[i].Is(open))
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static int FindTopLevelAssignment(List<CToken> tokens)
        {
            var depth = 0;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Is("(") || token.Is("[") || token.Is("{"))
                    depth++;
                else if (token.Is(")") || token.Is("]") || token.Is("}"))
                    depth--;
                else if (depth == 0 && token.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=")
                    return i;
            }

            return -1;
        }

        internal static int Matching(List<CToken> tokens, int open)
        {
            var depth = 0;
            for (var i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Is("(") || tokens[i].Is("[") || tokens[i].Is("{"))
                    depth++;
                else if (tokens[i].Is(")") || tokens[i].Is("]") || tokens[i].Is("}"))
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return Math.Max(0, tokens.Count - 1);
        }

        internal static List<List<CToken>> SplitTopLevel(List<CToken> tokens, string separator)
        {
            var parts = new List<List<CToken>>();
            var current = new List<CToken>();
            var depth = 0;
            foreach (var token in tokens)
            {
                if (token.Is("(") || token.Is("[") || token.Is("{"))
                    depth++;
                else if (token.Is(")") || token.Is("]") || token.Is("}"))
                    depth--;

                if (depth == 0 && token.Is(separator))
                {
                    parts.Add(current);
                    current = new List<CToken>();
                    continue;
                }

                current.Add(token);
            }

            parts.Add(current);
            return parts;
        }

        /// <summary>
        /// A type as the pseudocode spells it: the words joined with <c>_</c> (so
        /// <c>unsigned long long</c> is one name) and one <c>*</c> per pointer level, separated so
        /// <c>char **</c> does not arrive as the <c>**</c> power operator.
        /// </summary>
        internal static string TypeText(List<CToken> tokens)
        {
            var words = new List<string>();
            var pointers = 0;
            foreach (var token in tokens)
            {
                if (token.IsIdentifier)
                    words.Add(token.Text);
                else if (token.Is("*"))
                    pointers++;
            }

            if (words.Count == 0)
                return string.Empty;

            return string.Join("_", words) + string.Concat(Enumerable.Repeat(" *", pointers));
        }
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>
    /// A Pratt reader over C's precedence table that emits the pseudocode spelling of the expression.
    /// It never drops a cast and never turns <c>-&gt;</c> into a string lookalike: <c>p-&gt;f</c>
    /// becomes <c>p.f</c>, <c>(uint)*p</c> becomes <c>CAST(uint, *p)</c>.
    /// </summary>
    private sealed class CExpression
    {
        private static readonly Dictionary<string, (int Precedence, string Text, bool RightAssociative)> BinaryOperators = new(StringComparer.Ordinal)
        {
            ["||"] = (5, "OR", false),
            ["&&"] = (6, "AND", false),
            ["|"] = (7, "|", false),
            ["^"] = (8, "^", false),
            ["&"] = (9, "&", false),
            ["=="] = (10, "==", false),
            ["!="] = (10, "!=", false),
            ["<"] = (11, "<", false),
            ["<="] = (11, "<=", false),
            [">"] = (11, ">", false),
            [">="] = (11, ">=", false),
            ["<<"] = (12, "<<", false),
            [">>"] = (12, ">>", false),
            ["+"] = (13, "+", false),
            ["-"] = (13, "-", false),
            ["*"] = (14, "*", false),
            ["/"] = (14, "/", false),
            ["%"] = (14, "%", false),
        };

        private readonly List<CToken> _tokens;
        private readonly List<string> _warnings;
        private int _index;

        public CExpression(List<CToken> tokens, List<string> warnings)
        {
            _tokens = tokens;
            _warnings = warnings;
        }

        private readonly record struct Emit(string Text, int Precedence);

        public string Parse()
        {
            if (_tokens.Count == 0)
                return string.Empty;

            var expression = Expression(0);
            if (_index < _tokens.Count)
            {
                // A construct this reader does not model (C's comma operator, for instance): keep the
                // whole thing as opaque text instead of silently dropping the tail. The reason is
                // carried by the frontend's diagnostic, not by the text: `//` inside an expression would
                // be read back as part of it.
                _warnings.Add("an expression the C reader could not place: " + string.Join(" ", _tokens.Select(t => t.Text)));
                return "RAW[cpp] " + string.Join(" ", _tokens.Select(t => t.Text));
            }

            return expression.Text;
        }

        private Emit Expression(int minimum)
        {
            var left = Unary();

            while (_index < _tokens.Count)
            {
                var token = _tokens[_index];

                if (token.Is("?") && 3 >= minimum)
                {
                    _index++;
                    var whenTrue = Expression(4);
                    if (_index < _tokens.Count && _tokens[_index].Is(":"))
                        _index++;
                    var whenFalse = Expression(3);
                    left = new Emit(Wrap(left, 4) + " ? " + Wrap(whenTrue, 4) + " : " + Wrap(whenFalse, 3), 3);
                    continue;
                }

                if (!BinaryOperators.TryGetValue(token.Text, out var op) || op.Precedence < minimum)
                    break;

                _index++;
                var right = Expression(op.RightAssociative ? op.Precedence : op.Precedence + 1);
                left = new Emit(
                    Wrap(left, op.Precedence) + " " + op.Text + " " + Wrap(right, op.Precedence + 1),
                    op.Precedence);
            }

            return left;
        }

        private Emit Unary()
        {
            if (_index >= _tokens.Count)
                return new Emit(string.Empty, 17);

            var token = _tokens[_index];
            switch (token.Text)
            {
                case "!":
                    _index++;
                    return new Emit("NOT " + Wrap(Unary(), 16), 16);

                case "~":
                    _index++;
                    return new Emit("~" + Wrap(Unary(), 16), 16);

                case "-":
                case "+":
                    _index++;
                    return new Emit(token.Text + Wrap(Unary(), 17), 16);

                case "*":
                    _index++;
                    return new Emit("*" + Wrap(Unary(), 17), 16);

                case "&":
                    _index++;
                    return new Emit("&" + Wrap(Unary(), 17), 16);

                case "++":
                case "--":
                    _index++;
                    return new Emit(token.Text + Wrap(Unary(), 17), 16);

                case "sizeof":
                {
                    _index++;
                    var operand = Unary();
                    return new Emit("sizeof(" + operand.Text + ")", 17);
                }

                case "(" when IsCast(_index, out var typeEnd):
                {
                    var type = CFunctionParser.TypeText(_tokens.GetRange(_index + 1, typeEnd - _index - 1));
                    _index = typeEnd + 1;
                    var operand = Unary();
                    return new Emit("CAST(" + type + ", " + operand.Text + ")", 16);
                }
            }

            return Postfix(Primary());
        }

        private Emit Postfix(Emit expression)
        {
            while (_index < _tokens.Count)
            {
                var token = _tokens[_index];
                if (token.Is("("))
                {
                    var close = CFunctionParser.Matching(_tokens, _index);
                    var arguments = CFunctionParser
                        .SplitTopLevel(_tokens.GetRange(_index + 1, Math.Max(0, close - _index - 1)), ",")
                        .Where(part => part.Count > 0)
                        .Select(ExpressionOf);
                    _index = Math.Min(close + 1, _tokens.Count);
                    expression = new Emit(Wrap(expression, 17) + "(" + string.Join(", ", arguments) + ")", 17);
                    continue;
                }

                if (token.Is("["))
                {
                    var close = CFunctionParser.Matching(_tokens, _index);
                    var indices = CFunctionParser
                        .SplitTopLevel(_tokens.GetRange(_index + 1, Math.Max(0, close - _index - 1)), ",")
                        .Select(ExpressionOf);
                    _index = Math.Min(close + 1, _tokens.Count);
                    expression = new Emit(Wrap(expression, 17) + "[" + string.Join(", ", indices) + "]", 17);
                    continue;
                }

                if (token.Is(".") || token.Is("->"))
                {
                    _index++;
                    var member = _index < _tokens.Count ? _tokens[_index++].Text : "?";
                    expression = new Emit(Wrap(expression, 17) + "." + member, 17);
                    continue;
                }

                if (token.Is("++") || token.Is("--"))
                {
                    _index++;
                    expression = new Emit(Wrap(expression, 17) + token.Text, 17);
                    continue;
                }

                break;
            }

            return expression;
        }

        private Emit Primary()
        {
            if (_index >= _tokens.Count)
                return new Emit(string.Empty, 17);

            var token = _tokens[_index];
            if (token.Kind is CTokenKind.Number or CTokenKind.String or CTokenKind.Character or CTokenKind.Identifier)
            {
                _index++;
                // A wide literal (`L"text"`, `L'\0'`) is the same literal to this model: the prefix says
                // how it is encoded, and dropping it keeps the text readable back as a literal.
                return new Emit(WidePrefix(token.Text), 17);
            }

            if (token.Is("("))
            {
                var close = CFunctionParser.Matching(_tokens, _index);
                var inner = ExpressionOf(_tokens.GetRange(_index + 1, Math.Max(0, close - _index - 1)));
                _index = Math.Min(close + 1, _tokens.Count);
                return new Emit("(" + inner + ")", 17);
            }

            _index++;
            _warnings.Add("an unreadable expression fragment: " + token.Text);
            return new Emit(token.Text, 17);
        }

        private string ExpressionOf(List<CToken> tokens) =>
            tokens.Count == 0 ? string.Empty : new CExpression(tokens, _warnings).Parse();

        /// <summary>
        /// Whether the <c>(</c> at <paramref name="open"/> starts a cast. A cast holds type words and
        /// <c>*</c>/<c>&amp;</c>/<c>[size]</c> only, and an expression has to follow it — which is what
        /// tells <c>(uint)*p</c> from <c>(a + b) * c</c>.
        /// </summary>
        private bool IsCast(int open, out int close)
        {
            close = CFunctionParser.Matching(_tokens, open);
            if (close <= open + 1 || close >= _tokens.Count - 1)
                return false;

            // A type starts with a word; `(*p)` and `(&x)` are dereferences and addresses, not casts.
            if (!_tokens[open + 1].IsIdentifier)
                return false;

            var hasWord = false;
            for (var i = open + 1; i < close; i++)
            {
                var token = _tokens[i];
                if (token.IsIdentifier)
                {
                    hasWord = true;
                    continue;
                }

                if (token.Is("*") || token.Is("&") || token.Is("[") || token.Is("]") || token.Kind == CTokenKind.Number)
                    continue;

                return false;
            }

            if (!hasWord)
                return false;

            var next = _tokens[close + 1];
            return next.Kind is CTokenKind.Identifier or CTokenKind.Number or CTokenKind.String or CTokenKind.Character
                   || next.Text is "(" or "*" or "&" or "-" or "+" or "!" or "~" or "++" or "--" or "sizeof";
        }

        private static string Wrap(Emit expression, int minimum) =>
            expression.Precedence < minimum ? "(" + expression.Text + ")" : expression.Text;

        private static string WidePrefix(string text) =>
            text.Length > 1 && text[0] is 'L' or 'u' or 'U' && text[1] is '"' or '\''
                ? text[1..]
                : text.StartsWith("u8", StringComparison.Ordinal) && text.Length > 2 && text[2] is '"' or '\''
                    ? text[2..]
                    : text;
    }
}
