using System.Text;

namespace Snitch.Languages.JavaScript;

/// <summary>What kind of declaration a lexical scan can prove.</summary>
public enum JsSymbolKind
{
    Function,
    Class,
    Method,
    Field,
    Variable,
    Parameter,
    Interface,
    TypeAlias,
    Enum,
    Import,
}

/// <summary>One declaration, with the names it is exported under and the scope that contains it.</summary>
public sealed record JsSymbolDeclaration(
    string Name,
    JsSymbolKind Kind,
    int Line,
    int Column,
    int Offset,
    IReadOnlyList<string> ExportedAs,
    string Container);

/// <summary>One occurrence of a name. <see cref="Declaration"/> is the index of what it resolves to, or -1.</summary>
public sealed record JsSymbolUse(string Name, int Line, int Column, int Offset, string Kind, int Declaration);

/// <summary>One local binding an import introduces, with the name it takes from the module.</summary>
public sealed record JsImportBinding(string Local, string Imported, string Module, int Line, int Column);

/// <summary>The symbol facts a scan of one file produces.</summary>
public sealed record JsFileSymbols(
    string Path,
    IReadOnlyList<JsSymbolDeclaration> Declarations,
    IReadOnlyList<JsSymbolUse> Uses,
    IReadOnlyList<JsImportBinding> Imports,
    IReadOnlyList<string> Exports,
    int Lines,
    bool TypeScript,
    bool Jsx);

/// <summary>
/// A lexical symbol table for JavaScript, TypeScript, JSX and TSX: a tokeniser over the masked source, a
/// scope for every brace block, and a declaration for every name a declaration position introduces
/// (<c>function</c>, <c>class</c>, a declarator, a parameter, a class member, an import binding). Every
/// identifier in a read position is resolved to the declaration in the innermost enclosing scope that
/// carries its name; an imported name resolves through its binding, and the caller links it to the other
/// module's export.
/// <para>
/// It is not a type model. A member reached through an object it did not type, a computed member name, a
/// destructuring default and <c>eval</c> are outside what a token can prove, and the answer says so. What
/// it does prove — which declaration a name refers to, in which file and at which line — is what a
/// dead-export report, a reference list or a rename needs.
/// </para>
/// </summary>
public static class JavaScriptSymbols
{
    private enum TokenKind
    {
        Identifier,
        Keyword,
        String,
        Number,
        Punctuation,
        JsxTag,
    }

    private sealed record Token(TokenKind Kind, string Text, int Offset, int Line, int Column);

    private sealed class Scope
    {
        public int Id;
        public Scope? Parent;
        public string Kind = "block";
        public string? Owner;
        public readonly List<int> Declarations = new();
    }

    private sealed class Declaration
    {
        public string Name = string.Empty;
        public JsSymbolKind Kind;
        public int Line;
        public int Column;
        public int Offset;
        public string Container = "module";
        public readonly List<string> ExportedAs = new();
    }

    private static readonly HashSet<string> HardKeywords = new(StringComparer.Ordinal)
    {
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "let", "new", "null", "return", "static", "super", "switch", "this", "throw", "true",
        "try", "typeof", "var", "void", "while", "with", "yield",
    };

    /// <summary>Words that are identifiers in JavaScript and are never a reference in the scan.</summary>
    private static readonly HashSet<string> SoftKeywords = new(StringComparer.Ordinal)
    {
        "as", "async", "from", "get", "set", "of", "undefined", "type", "interface", "enum", "declare",
        "namespace", "abstract", "readonly", "satisfies", "keyof", "infer", "asserts", "implements",
        "private", "protected", "public", "override", "accessor", "arguments",
    };

    /// <summary>Reads one file. <paramref name="filePath"/> decides TypeScript and JSX, as it does for analyze_js.</summary>
    public static JsFileSymbols Scan(string text, string filePath)
    {
        text ??= string.Empty;
        var isTypeScript = JavaScriptAnalyzer.IsTypeScript(filePath);
        var isJsx = filePath.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
                    || filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

        var masked = JavaScriptAnalyzer.Mask(text);
        var lineStarts = LineStarts(text);
        var tokens = Tokenize(masked, text, lineStarts, isJsx);

        var declarations = new List<Declaration>();
        var uses = new List<(string Name, int TokenIndex, int Scope)>();
        var imports = new List<JsImportBinding>();
        var exports = new List<string>();
        var consumed = new HashSet<int>();
        var match = MatchBrackets(tokens);

        var scopes = new List<Scope> { new() { Kind = "module" } };
        var scopeStack = new Stack<Scope>();
        scopeStack.Push(scopes[0]);
        var exportedAliases = new List<(string Name, string Exported)>();

        var exportNext = false;
        var exportDefault = false;

        // ── declarations ──────────────────────────────────────────────────────────────
        Declaration AddDeclaration(string name, JsSymbolKind kind, Token token, Scope owner)
        {
            var container = "module";
            for (var current = owner; current is not null; current = current.Parent)
                if (!string.IsNullOrEmpty(current.Owner))
                {
                    container = current.Owner!;
                    break;
                }

            var declaration = new Declaration
            {
                Name = name,
                Kind = kind,
                Line = token.Line,
                Column = token.Column,
                Offset = token.Offset,
                Container = container,
            };

            declarations.Add(declaration);
            owner.Declarations.Add(declarations.Count - 1);
            return declaration;
        }

        void DeclareExported(string name, JsSymbolKind kind, Token token, Scope owner)
        {
            var declaration = AddDeclaration(name, kind, token, owner);
            if (!exportNext) return;

            var exported = exportDefault ? "default" : declaration.Name;
            declaration.ExportedAs.Add(exported);
            if (!exports.Contains(exported, StringComparer.Ordinal)) exports.Add(exported);
            exportNext = false;
            exportDefault = false;
        }

        /// <summary>
        /// `const a = 1, b = 2`, `const { a, b: c } = o`, `let [x, y] = …` — one declaration per bound
        /// name, the initializer skipped so a name inside it is not read as a binding.
        /// </summary>
        void ParseVariables(int keywordIndex)
        {
            var cursor = keywordIndex + 1;
            while (cursor < tokens.Count)
            {
                if (tokens[cursor].Kind == TokenKind.Punctuation && tokens[cursor].Text is "{" or "[")
                {
                    var end = match[cursor] > cursor ? match[cursor] : cursor;
                    foreach (var (name, nameToken) in PatternNames(tokens, cursor, end, consumed))
                        DeclareExported(name, JsSymbolKind.Variable, nameToken, scopeStack.Peek());

                    cursor = end + 1;
                }
                else if (tokens[cursor].Kind == TokenKind.Identifier)
                {
                    var name = tokens[cursor];
                    consumed.Add(cursor);
                    DeclareExported(name.Text, JsSymbolKind.Variable, name, scopeStack.Peek());
                    cursor++;
                }
                else
                {
                    return;
                }

                // A type annotation and an initializer are not declarations: skip to the next declarator.
                var depth = 0;
                var next = false;
                while (cursor < tokens.Count)
                {
                    var token = tokens[cursor];
                    if (token.Kind == TokenKind.Punctuation)
                    {
                        if (token.Text is "(" or "[" or "{") depth++;
                        else if (token.Text is ")" or "]" or "}") depth--;
                        else if (depth == 0 && token.Text == ",") { cursor++; next = true; break; }
                        else if (depth == 0 && token.Text == ";") return;
                    }

                    if (depth < 0) return;
                    cursor++;
                }

                if (!next) return;
            }
        }

        // ── the walk: scopes, declarations and uses in one pass ───────────────────────
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var scope = scopeStack.Peek();

            if (token.Kind == TokenKind.Punctuation)
            {
                if (token.Text == "{")
                {
                    var (kind, owner, parameterOpen, parameterClose) = ClassifyBrace(tokens, match, i);
                    var child = new Scope { Id = scopes.Count, Parent = scope, Kind = kind, Owner = owner };
                    scopes.Add(child);
                    scopeStack.Push(child);

                    if (parameterOpen >= 0 && parameterClose > parameterOpen)
                        foreach (var (name, parameter) in ParameterNames(tokens, match, parameterOpen, parameterClose, consumed))
                            AddDeclaration(name, JsSymbolKind.Parameter, parameter, child);

                    continue;
                }

                if (token.Text == "}")
                {
                    if (scopeStack.Count > 1) scopeStack.Pop();
                    continue;
                }

                if (token.Text == "=>")
                {
                    var previous = PreviousIndex(tokens, i);
                    var next = NextIndex(tokens, i);
                    if (previous >= 0 && tokens[previous].Text == ")" &&
                        (next < 0 || tokens[next].Text != "{"))
                    {
                        var open = match[previous];
                        foreach (var (name, parameter) in ParameterNames(tokens, match, open, previous, consumed))
                            AddDeclaration(name, JsSymbolKind.Parameter, parameter, scope);
                    }
                    else if (previous >= 0 && tokens[previous].Kind == TokenKind.Identifier && !consumed.Contains(previous))
                    {
                        consumed.Add(previous);
                        AddDeclaration(tokens[previous].Text, JsSymbolKind.Parameter, tokens[previous], scope);
                    }

                    continue;
                }

                continue;
            }

            if (token.Kind == TokenKind.JsxTag)
            {
                // A component tag (`<Counter …>`) is a reference to the name in scope; a host element
                // (`<div>`) is not.
                if (token.Text.Length > 0 && char.IsUpper(token.Text[0]))
                    uses.Add((token.Text, i, scope.Id));
                continue;
            }

            if (token.Kind == TokenKind.Keyword)
            {
                switch (token.Text)
                {
                    case "import":
                    {
                        var before = imports.Count;
                        i = ParseImport(i, tokens, match, consumed, imports);
                        // An import binding is a declaration of the module scope, so a use of the local
                        // name resolves to it and the caller can link it to the other module's export.
                        foreach (var binding in imports.Skip(before))
                        {
                            var bindingToken = new Token(TokenKind.Identifier, binding.Local, 0, binding.Line, binding.Column);
                            AddDeclaration(binding.Local, JsSymbolKind.Import, bindingToken, scope);
                        }

                        break;
                    }

                    case "export":
                        i = ParseExport(i, tokens, match, consumed, exports, exportedAliases,
                            ref exportNext, ref exportDefault);
                        break;

                    case "function":
                    {
                        var nameIndex = NextIndex(tokens, i);
                        if (nameIndex >= 0 && tokens[nameIndex].Text == "*") nameIndex = NextIndex(tokens, nameIndex);
                        if (nameIndex >= 0 && tokens[nameIndex].Kind == TokenKind.Identifier)
                        {
                            consumed.Add(nameIndex);
                            DeclareExported(tokens[nameIndex].Text, JsSymbolKind.Function, tokens[nameIndex], scope);
                        }

                        break;
                    }

                    case "class":
                    {
                        var nameIndex = NextIndex(tokens, i);
                        if (nameIndex >= 0 && tokens[nameIndex].Kind == TokenKind.Identifier)
                        {
                            consumed.Add(nameIndex);
                            DeclareExported(tokens[nameIndex].Text, JsSymbolKind.Class, tokens[nameIndex], scope);
                        }

                        break;
                    }

                    case "const" or "let" or "var":
                        ParseVariables(i);
                        break;
                }

                continue;
            }

            if (token.Kind != TokenKind.Identifier) continue;
            if (consumed.Contains(i)) continue;

            // A class member is declared by its position in the class body.
            if (scope.Kind == "class" && IsMemberPosition(tokens, i))
            {
                var next = NextIndex(tokens, i);
                var isMethod = next >= 0 && tokens[next].Text == "(";
                var isField = next >= 0 && tokens[next].Text is "=" or ";" or ":" or "?";
                if (isMethod || isField)
                {
                    var previous = PreviousIndex(tokens, i);
                    var name = previous >= 0 && tokens[previous].Text == "#" ? "#" + token.Text : token.Text;
                    consumed.Add(i);
                    AddDeclaration(name, isMethod ? JsSymbolKind.Method : JsSymbolKind.Field, token, scope);
                    continue;
                }
            }

            // A TypeScript type, interface, enum or namespace declaration.
            if (isTypeScript)
            {
                var next = NextIndex(tokens, i);
                if (next >= 0 && tokens[next].Kind == TokenKind.Identifier &&
                    token.Text is "interface" or "enum" or "type" or "namespace")
                {
                    consumed.Add(i);
                    consumed.Add(next);
                    DeclareExported(tokens[next].Text,
                        token.Text switch
                        {
                            "interface" => JsSymbolKind.Interface,
                            "enum" => JsSymbolKind.Enum,
                            _ => JsSymbolKind.TypeAlias,
                        },
                        tokens[next], scope);
                    continue;
                }
            }

            // CommonJS: `exports.name = …` and `module.exports.name = …` publish a name.
            if (token.Text is "exports" or "module" && TryCommonJsExport(tokens, i, out var exportedName, out var exportedToken))
            {
                _ = exportedToken;
                if (exportedName is not null) exportedAliases.Add((exportedName, exportedName));
                continue;
            }

            if (SoftKeywords.Contains(token.Text)) continue;
            if (token.Text is "exports" or "module") continue;

            // A private member read through `this.#count` is the `#count` declaration of the class.
            var previousIndex = PreviousIndex(tokens, i);
            var useName = previousIndex >= 0 && tokens[previousIndex].Text == "#"
                ? "#" + token.Text
                : token.Text;
            uses.Add((useName, i, scope.Id));
        }

        // ── resolution: the innermost scope carrying the name ─────────────────────────
        // A token can be recognised as a declaration only after it has been walked (an arrow parameter is
        // consumed when its `=>` is reached), so the occurrences that turn out to be declarations go.
        uses.RemoveAll(use => consumed.Contains(use.TokenIndex));

        var useList = new List<JsSymbolUse>(uses.Count);
        foreach (var (name, tokenIndex, scopeId) in uses)
        {
            var resolved = -1;
            for (var id = scopeId; id >= 0 && resolved < 0;)
            {
                var owner = scopes[id];
                for (var index = owner.Declarations.Count - 1; index >= 0; index--)
                {
                    var candidate = owner.Declarations[index];
                    if (!string.Equals(declarations[candidate].Name, name, StringComparison.Ordinal)) continue;
                    resolved = candidate;
                    break;
                }

                id = owner.Parent?.Id ?? -1;
            }

            var token = tokens[tokenIndex];
            useList.Add(new JsSymbolUse(name, token.Line, token.Column, token.Offset,
                UseKind(tokens, tokenIndex), resolved));
        }

        // An alias only counts when a declaration carries the name.
        foreach (var (name, exported) in exportedAliases)
        {
            var declaration = declarations.FirstOrDefault(candidate => candidate.Name == name);
            if (declaration is null) continue;
            if (!declaration.ExportedAs.Contains(exported, StringComparer.Ordinal))
                declaration.ExportedAs.Add(exported);
            if (!exports.Contains(exported, StringComparer.Ordinal)) exports.Add(exported);
        }

        var result = declarations
            .Select(declaration => new JsSymbolDeclaration(
                declaration.Name,
                declaration.Kind,
                declaration.Line,
                declaration.Column,
                declaration.Offset,
                declaration.ExportedAs.ToArray(),
                declaration.Container))
            .ToList();

        return new JsFileSymbols(filePath, result, useList, imports, exports, lineStarts.Length, isTypeScript, isJsx);
    }

    // ── imports and exports ──────────────────────────────────────────────────────────

    private static int ParseImport(
        int index, List<Token> tokens, int[] match, HashSet<int> consumed, List<JsImportBinding> imports)
    {
        consumed.Add(index);
        var cursor = index + 1;
        if (cursor >= tokens.Count) return index;

        // A side-effect import: `import './style.css'`.
        if (tokens[cursor].Kind == TokenKind.String) return cursor;

        // `import type { X } from '…'` is a type-only binding; it still declares the name locally.
        if (tokens[cursor].Text is "type" or "typeof") consumed.Add(cursor++);

        var bindings = new List<(string Local, string Imported, Token Token)>();

        // Default binding.
        if (cursor < tokens.Count && tokens[cursor].Kind == TokenKind.Identifier)
        {
            bindings.Add((tokens[cursor].Text, "default", tokens[cursor]));
            consumed.Add(cursor++);
            if (cursor < tokens.Count && tokens[cursor].Text == ",") cursor++;
        }

        // Namespace binding: `* as ns`.
        if (cursor < tokens.Count && tokens[cursor].Text == "*")
        {
            consumed.Add(cursor++);
            if (cursor < tokens.Count && tokens[cursor].Text == "as") consumed.Add(cursor++);
            if (cursor < tokens.Count && tokens[cursor].Kind == TokenKind.Identifier)
            {
                bindings.Add((tokens[cursor].Text, "*", tokens[cursor]));
                consumed.Add(cursor++);
            }
        }

        // Named bindings: `{ a, b as c }`.
        if (cursor < tokens.Count && tokens[cursor].Text == "{")
        {
            var end = match[cursor] > cursor ? match[cursor] : tokens.Count - 1;
            var position = cursor + 1;
            while (position < end)
            {
                if (tokens[position].Kind == TokenKind.Identifier)
                {
                    var imported = tokens[position];
                    consumed.Add(position);
                    var local = imported;
                    if (position + 2 < end && tokens[position + 1].Text == "as" &&
                        tokens[position + 2].Kind == TokenKind.Identifier)
                    {
                        consumed.Add(position + 1);
                        consumed.Add(position + 2);
                        local = tokens[position + 2];
                        position += 2;
                    }

                    bindings.Add((local.Text, imported.Text, local));
                }

                position++;
            }

            cursor = end + 1;
        }

        if (cursor < tokens.Count && tokens[cursor].Text == "from") consumed.Add(cursor++);
        var module = cursor < tokens.Count && tokens[cursor].Kind == TokenKind.String
            ? Unquote(tokens[cursor].Text)
            : string.Empty;

        foreach (var (local, imported, token) in bindings)
            imports.Add(new JsImportBinding(local, imported, module, token.Line, token.Column));

        return cursor;
    }

    private static int ParseExport(
        int index, List<Token> tokens, int[] match, HashSet<int> consumed, List<string> exports,
        List<(string Name, string Exported)> aliases, ref bool exportNext, ref bool exportDefault)
    {
        consumed.Add(index);
        var next = NextIndex(tokens, index);
        if (next < 0) return index;

        if (tokens[next].Text == "default")
        {
            consumed.Add(next);
            exportNext = true;
            exportDefault = true;

            // `export default foo;` names a local; anything else is the anonymous default export.
            var value = NextIndex(tokens, next);
            var after = value >= 0 ? NextIndex(tokens, value) : -1;
            if (value >= 0 && tokens[value].Kind == TokenKind.Identifier &&
                (after < 0 || tokens[after].Text is ";" or "}"))
            {
                aliases.Add((tokens[value].Text, "default"));
                consumed.Add(value);
                exportNext = false;
                exportDefault = false;
                return value;
            }

            return next;
        }

        if (tokens[next].Text == "*")
        {
            if (!exports.Contains("*", StringComparer.Ordinal)) exports.Add("*");
            var cursor = next;
            while (cursor < tokens.Count && tokens[cursor].Text != ";" && tokens[cursor].Text != "}") cursor++;
            return cursor;
        }

        if (tokens[next].Text == "{")
        {
            var end = match[next] > next ? match[next] : tokens.Count - 1;
            var position = next + 1;
            while (position < end)
            {
                if (tokens[position].Kind == TokenKind.Identifier)
                {
                    var local = tokens[position];
                    consumed.Add(position);
                    var exported = local.Text;
                    if (position + 2 < end && tokens[position + 1].Text == "as" &&
                        tokens[position + 2].Kind == TokenKind.Identifier)
                    {
                        consumed.Add(position + 1);
                        consumed.Add(position + 2);
                        exported = tokens[position + 2].Text;
                        position += 2;
                    }

                    aliases.Add((local.Text, exported));
                }

                position++;
            }

            return end;
        }

        exportNext = true;
        return index;
    }

    /// <summary>True when the identifier at <paramref name="index"/> is the property a CommonJS export publishes.</summary>
    private static bool TryCommonJsExport(List<Token> tokens, int index, out string? name, out int tokenIndex)
    {
        name = null;
        tokenIndex = -1;

        if (tokens[index].Text == "exports")
        {
            var dot = NextIndex(tokens, index);
            var property = dot >= 0 && tokens[dot].Text == "." ? NextIndex(tokens, dot) : -1;
            if (property < 0 || tokens[property].Kind != TokenKind.Identifier) return false;
            name = tokens[property].Text;
            tokenIndex = property;
            return true;
        }

        // module.exports.name
        var first = NextIndex(tokens, index);
        if (first < 0 || tokens[first].Text != ".") return false;
        var second = NextIndex(tokens, first);
        if (second < 0 || tokens[second].Text != "exports") return false;
        var third = NextIndex(tokens, second);
        if (third < 0 || tokens[third].Text != ".") return false;
        var last = NextIndex(tokens, third);
        if (last < 0 || tokens[last].Kind != TokenKind.Identifier) return false;
        name = tokens[last].Text;
        tokenIndex = last;
        return true;
    }

    // ── scopes ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The names a destructuring pattern binds: the key before a `:` is skipped, the name after it is the
    /// binding, and the right-hand side of a default (`= name`) is an expression, not a binding.
    /// </summary>
    private static IEnumerable<(string Name, Token Token)> PatternNames(
        List<Token> tokens, int open, int close, HashSet<int> consumed)
    {
        for (var index = open + 1; index < close && index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind != TokenKind.Identifier) continue;

            var previous = PreviousIndex(tokens, index);
            var next = NextIndex(tokens, index);
            if (previous >= 0 && tokens[previous].Text == "=") continue;
            if (next >= 0 && tokens[next].Text == ":") continue;

            consumed.Add(index);
            yield return (token.Text, token);
        }
    }

    /// <summary>
    /// What a `{` opens: a class body (a `class` header before it), a function body (a parameter list
    /// before it, or `=> … {`), or a plain block.
    /// </summary>
    private static (string Kind, string? Owner, int ParameterOpen, int ParameterClose) ClassifyBrace(
        List<Token> tokens, int[] match, int brace)
    {
        var previous = PreviousIndex(tokens, brace);
        if (previous >= 0 && tokens[previous].Text == ")")
        {
            var open = match[previous];
            var isControl = open > 0 && tokens[open - 1].Text is "if" or "for" or "while" or "switch" or "with";
            if (!isControl && open >= 0)
                return ("function", OwnerOf(tokens, open), open, previous);

            return ("block", null, -1, -1);
        }

        if (ClassHeaderBefore(tokens, brace))
        {
            var cursor = brace - 1;
            string? name = null;
            while (cursor >= 0 && tokens[cursor].Text != "class") cursor--;
            if (cursor >= 0)
            {
                var identifier = NextIndex(tokens, cursor);
                if (identifier >= 0 && tokens[identifier].Kind == TokenKind.Identifier) name = tokens[identifier].Text;
            }

            return ("class", name, -1, -1);
        }

        return ("block", null, -1, -1);
    }

    private static string? OwnerOf(List<Token> tokens, int parameterOpen)
    {
        var before = parameterOpen - 1;
        while (before >= 0 && tokens[before].Text is "async" or "static" or "get" or "set" or "*" or "#" or "readonly")
            before--;
        if (before < 0) return null;
        return tokens[before].Kind == TokenKind.Identifier ? tokens[before].Text : null;
    }

    private static bool ClassHeaderBefore(List<Token> tokens, int brace)
    {
        var depth = 0;
        for (var index = brace - 1; index >= 0 && index > brace - 60; index--)
        {
            var text = tokens[index].Text;
            if (text is ")" or "]" or "}") depth++;
            else if (text is "(" or "[" or "{")
            {
                if (depth == 0) return false;
                depth--;
            }
            else if (depth == 0 && text == "class") return true;
            else if (depth == 0 && text is ";" or "=>") return false;
        }

        return false;
    }

    /// <summary>
    /// The parameter names of a signature, skipping a TypeScript type (<c>a: number</c>), a default value
    /// and the key of a destructured property (<c>{ a: b }</c> binds b).
    /// </summary>
    private static IEnumerable<(string Name, Token Token)> ParameterNames(
        List<Token> tokens, int[] match, int open, int close, HashSet<int> consumed)
    {
        for (var index = open + 1; index < close; index++)
        {
            var token = tokens[index];
            if (token.Kind != TokenKind.Identifier) continue;

            var previous = PreviousIndex(tokens, index);
            var next = NextIndex(tokens, index);

            if (previous >= 0 && tokens[previous].Text is ":" or "=") continue;
            if (next >= 0 && tokens[next].Text == ":" &&
                previous >= 0 && tokens[previous].Text is "{" or ",")
                continue;
            if (SoftKeywords.Contains(token.Text)) continue;

            consumed.Add(index);
            yield return (token.Text, token);
        }
    }

    /// <summary>True when the identifier sits where a class member starts.</summary>
    private static bool IsMemberPosition(List<Token> tokens, int index)
    {
        var previous = PreviousIndex(tokens, index);
        if (previous < 0) return false;
        return tokens[previous].Text is "{" or "}" or ";" or "*" or "#" or "static" or "async" or "get" or "set"
            or "accessor" or "readonly" or "public" or "private" or "protected" or "override" or "declare";
    }

    // ── tokenizer ────────────────────────────────────────────────────────────────────

    private static List<Token> Tokenize(string masked, string raw, int[] lineStarts, bool jsx)
    {
        var tokens = new List<Token>();
        var index = 0;
        var jsxDepth = 0;
        var jsxText = false;
        var jsxExpression = 0;

        while (index < masked.Length)
        {
            var character = masked[index];

            if (jsxText)
            {
                if (character == '<')
                {
                    var tag = ReadJsxTag(masked, index);
                    index = tag.Next;
                    if (tag.Closing)
                    {
                        jsxDepth = Math.Max(0, jsxDepth - 1);
                        jsxText = jsxDepth > 0;
                    }
                    else if (tag.SelfClosing)
                    {
                        jsxText = jsxDepth > 0;
                    }
                    else
                    {
                        jsxDepth++;
                        jsxText = true;
                    }

                    var (tagLine, tagColumn) = Position(lineStarts, tag.Start);
                    tokens.Add(new Token(TokenKind.JsxTag, tag.Name, tag.Start, tagLine, tagColumn));
                    continue;
                }

                if (character == '{')
                {
                    jsxText = false;
                    jsxExpression = 1;
                    var (line, column) = Position(lineStarts, index);
                    tokens.Add(new Token(TokenKind.Punctuation, "{", index, line, column));
                    index++;
                    continue;
                }

                index++;
                continue;
            }

            if (char.IsWhiteSpace(character)) { index++; continue; }

            if (character is '\'' or '"' or '`')
            {
                var start = index;
                var delimiter = character;
                index++;
                while (index < masked.Length && masked[index] != delimiter) index++;
                var end = Math.Min(index + 1, masked.Length);
                var (line, column) = Position(lineStarts, start);
                var value = raw.Substring(start, Math.Min(end, raw.Length) - start);
                tokens.Add(new Token(TokenKind.String, value, start, line, column));
                index = end;
                continue;
            }

            if (IsIdentifierStart(character))
            {
                var start = index;
                while (index < masked.Length && IsIdentifierPart(masked[index])) index++;
                var text = masked[start..index];
                var (line, column) = Position(lineStarts, start);
                tokens.Add(new Token(HardKeywords.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier,
                    text, start, line, column));
                continue;
            }

            if (char.IsDigit(character))
            {
                var start = index;
                while (index < masked.Length && (char.IsLetterOrDigit(masked[index]) || masked[index] is '.' or '_'))
                    index++;
                var (line, column) = Position(lineStarts, start);
                tokens.Add(new Token(TokenKind.Number, masked[start..index], start, line, column));
                continue;
            }

            // A regular-expression literal where an expression can start is skipped whole, so its text is
            // never read as an identifier.
            if (character == '/' && IsExpressionStart(tokens))
            {
                var start = index;
                index++;
                while (index < masked.Length && masked[index] != '/' && masked[index] != '\n')
                {
                    if (masked[index] == '\\') index++;
                    if (index < masked.Length && masked[index] == '[')
                    {
                        index++;
                        while (index < masked.Length && masked[index] != ']' && masked[index] != '\n') index++;
                    }

                    index++;
                }

                if (index < masked.Length && masked[index] == '/') index++;
                var (line, column) = Position(lineStarts, start);
                tokens.Add(new Token(TokenKind.Punctuation, "/regex/", start, line, column));
                continue;
            }

            // A JSX element where an expression can start.
            if (jsx && character == '<' && (IsExpressionStart(tokens) || IsClosingTag(masked, index)))
            {
                var tag = ReadJsxTag(masked, index);
                if (tag.Closing)
                {
                    jsxDepth = Math.Max(0, jsxDepth - 1);
                    jsxText = jsxDepth > 0;
                }
                else if (!tag.SelfClosing)
                {
                    jsxDepth++;
                    jsxText = true;
                }

                var (line, column) = Position(lineStarts, tag.Start);
                tokens.Add(new Token(TokenKind.JsxTag, tag.Name, tag.Start, line, column));
                index = tag.Next;
                continue;
            }

            var length = PunctuatorLength(masked, index);
            var punctuation = masked.Substring(index, length);
            var (punctuationLine, punctuationColumn) = Position(lineStarts, index);
            tokens.Add(new Token(TokenKind.Punctuation, punctuation, index, punctuationLine, punctuationColumn));
            index += length;

            if (jsxExpression <= 0) continue;
            if (punctuation == "{") jsxExpression++;
            else if (punctuation == "}")
            {
                jsxExpression--;
                if (jsxExpression == 0) jsxText = jsxDepth > 0;
            }
        }

        return tokens;
    }

    private sealed record JsxTag(string Name, bool Closing, bool SelfClosing, int Start, int Next);

    private static JsxTag ReadJsxTag(string masked, int start)
    {
        var index = start + 1;
        var closing = index < masked.Length && masked[index] == '/';
        if (closing) index++;

        var name = new StringBuilder();
        while (index < masked.Length &&
               (IsIdentifierPart(masked[index]) || masked[index] is '.' or ':' or '-'))
        {
            name.Append(masked[index]);
            index++;
        }

        var depth = 0;
        var selfClosing = false;
        while (index < masked.Length)
        {
            var character = masked[index];
            if (character is '\'' or '"')
            {
                var delimiter = character;
                index++;
                while (index < masked.Length && masked[index] != delimiter) index++;
                index++;
                continue;
            }

            if (character == '{') { depth++; index++; continue; }
            if (character == '}') { depth--; index++; continue; }
            if (character == '/' && depth == 0 && index + 1 < masked.Length && masked[index + 1] == '>')
            {
                selfClosing = true;
                index += 2;
                break;
            }

            if (character == '>' && depth == 0)
            {
                index++;
                break;
            }

            index++;
        }

        return new JsxTag(name.ToString(), closing, selfClosing, start, index);
    }

    private static bool IsClosingTag(string masked, int index) =>
        index + 1 < masked.Length && masked[index + 1] == '/';

    private static bool IsExpressionStart(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        var previous = tokens[^1];
        if (previous.Kind is TokenKind.Identifier or TokenKind.Number or TokenKind.String or TokenKind.JsxTag)
            return false;
        return previous.Text is not (")" or "]" or "}" or "++" or "--" or "/regex/");
    }

    private static int PunctuatorLength(string text, int index)
    {
        if (index + 2 < text.Length)
        {
            var three = text.Substring(index, 3);
            if (three is "===" or "!==" or "..." or "**=" or "&&=" or "||=" or "??=") return 3;
        }

        if (index + 1 < text.Length)
        {
            var two = text.Substring(index, 2);
            if (two is "=>" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "**" or "++" or "--"
                or "+=" or "-=" or "*=" or "/=" or "%=" or "?." or "<<" or ">>" or "|=" or "&=")
                return 2;
        }

        return 1;
    }

    // ── small helpers ─────────────────────────────────────────────────────────────────

    private static int[] MatchBrackets(List<Token> tokens)
    {
        var match = new int[tokens.Count];
        Array.Fill(match, -1);
        var stack = new Stack<int>();

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TokenKind.Punctuation) continue;
            var text = tokens[index].Text;
            if (text is "(" or "[" or "{") stack.Push(index);
            else if (text is ")" or "]" or "}")
            {
                if (stack.Count == 0) continue;
                var open = stack.Pop();
                match[open] = index;
                match[index] = open;
            }
        }

        return match;
    }

    private static int NextIndex(List<Token> tokens, int index) => index + 1 < tokens.Count ? index + 1 : -1;

    private static int PreviousIndex(List<Token> tokens, int index) => index - 1 >= 0 ? index - 1 : -1;

    private static string UseKind(List<Token> tokens, int index)
    {
        var next = NextIndex(tokens, index);
        var previous = PreviousIndex(tokens, index);

        if (next >= 0 && tokens[next].Text == "(") return "call";
        if (next >= 0 && tokens[next].Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--") return "write";
        if (previous >= 0 && tokens[previous].Text is "++" or "--") return "write";
        if (previous >= 0 && tokens[previous].Text == "new") return "instantiate";
        return "read";
    }

    private static string Unquote(string literal) => literal.Length >= 2 ? literal[1..^1] : literal;

    private static bool IsIdentifierStart(char character) =>
        char.IsLetter(character) || character is '_' or '$';

    private static bool IsIdentifierPart(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '$';

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n')
                starts.Add(index + 1);
        return starts.ToArray();
    }

    private static (int Line, int Column) Position(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        if (index < 0) index = ~index - 1;
        if (index < 0) index = 0;
        return (index + 1, offset - lineStarts[index] + 1);
    }
}
