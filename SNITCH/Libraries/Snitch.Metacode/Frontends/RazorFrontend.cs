using System.Text;
using Snitch.Languages.Razor;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads a Razor page (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>) into the shared model.
/// A page is a class that renders: <c>@model</c> and <c>@inject</c> become properties, the members of
/// <c>@code</c>/<c>@functions</c> are lowered by <see cref="CSharpFrontend"/>, and the markup body
/// becomes the body of a <c>Render</c> method in which <c>@if</c>/<c>@foreach</c>/<c>@for</c>/
/// <c>@while</c>/<c>@lock</c>/<c>@using</c> are control flow and everything HTML between them is kept
/// verbatim as an opaque <c>RAW[razor]</c> statement.
/// </summary>
/// <remarks>
/// The scanner is not the Razor compiler: it separates markup from C# and records positions, which is
/// enough to rebuild the skeleton of a page but not to resolve it. What it cannot model — a
/// <c>@switch</c> whose cases are markup, a <c>try/catch</c> whose <c>catch</c> is not an <c>@</c>
/// block, a brace-less <c>@if</c> body — is kept as one opaque statement with the source text, not
/// dropped.
/// </remarks>
public sealed class RazorFrontend : ILanguageFrontend
{
    public string Name => "Razor";

    public MetacodeLanguage Language => MetacodeLanguage.Razor;

    public bool CanHandle(MetacodeSource source) =>
        source.Language == MetacodeLanguage.Razor
        || (source.Language is null && source.Path is not null && IsRazorPath(source.Path));

    public MetacodeDocument Translate(MetacodeSource source, MetacodeOptions options)
    {
        var path = source.Path ?? source.Name + ".cshtml";
        var text = source.Text;
        var page = RazorScanner.Scan(path, text);
        var diagnostics = new List<MetacodeDiagnostic>();
        var declarations = new List<MetacodeDeclaration>();

        AddDirectives(page, declarations);
        AddModel(page, declarations);
        AddInjections(page, declarations);

        var builder = new RazorBodyBuilder(page, text, options, diagnostics);
        declarations.Add(new MetacodeFunction(
            "Render",
            MetacodeFunctionKind.Method,
            Body: builder.Build())
        {
            Modifiers = MetacodeModifiers.Public,
            Origin = Origin(page, 1),
        });

        AddCodeBlocks(page, text, options, declarations, diagnostics);

        var module = new MetacodeModule(source.Name, declarations) { Origin = Origin(page, 1) };
        return new MetacodeDocument(source.Name, MetacodeLanguage.Razor, new[] { module }, diagnostics);
    }

    // ── Declarations ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every directive is kept as its source text: <c>@page</c>, <c>@using</c>, <c>@layout</c>,
    /// <c>@attribute</c> and the tag-helper directives configure the page, they do not declare a
    /// member of it. <c>@model</c> and <c>@inject</c> are excluded because they become properties.
    /// </summary>
    private static void AddDirectives(RazorDocument page, List<MetacodeDeclaration> declarations)
    {
        foreach (var directive in page.Directives)
        {
            if (directive.Name is "model" or "inject") continue;

            var text = "@" + (directive.Value.Length == 0 ? directive.Name : directive.Name + " " + directive.Value);
            declarations.Add(new MetacodeOpaqueDeclaration(MetacodeLanguage.Razor, text, "a Razor directive")
            {
                Origin = Origin(page, directive.Line, directive.Column),
            });
        }
    }

    /// <summary><c>@model T</c> is the type of the page's <c>Model</c> property.</summary>
    private static void AddModel(RazorDocument page, List<MetacodeDeclaration> declarations)
    {
        if (string.IsNullOrWhiteSpace(page.ModelType)) return;

        declarations.Add(new MetacodeProperty(
            "Model",
            new MetacodeTypeRef(page.ModelType.Trim()),
            MetacodeModifiers.Public,
            Getter: new MetacodeAccessor())
        {
            Origin = Origin(page, 1),
        });
    }

    /// <summary><c>@inject T Name</c> is a service the page holds as a property.</summary>
    private static void AddInjections(RazorDocument page, List<MetacodeDeclaration> declarations)
    {
        foreach (var injection in page.Injections)
        {
            var name = injection.PropertyName.Length > 0
                ? injection.PropertyName
                : LastSegment(injection.ServiceType);

            declarations.Add(new MetacodeProperty(
                name,
                new MetacodeTypeRef(injection.ServiceType),
                MetacodeModifiers.Public,
                Getter: new MetacodeAccessor())
            {
                Origin = Origin(page, 1),
            });
        }
    }

    /// <summary>
    /// The members of <c>@code</c>/<c>@functions</c>, lowered by the C# frontend. The block is wrapped
    /// in a class whose first line sits on the page line the block starts on, so every origin the C#
    /// frontend reports points at the page's own lines.
    /// </summary>
    private static void AddCodeBlocks(
        RazorDocument page,
        string text,
        MetacodeOptions options,
        List<MetacodeDeclaration> declarations,
        List<MetacodeDiagnostic> diagnostics)
    {
        foreach (var block in page.CodeBlocks)
        {
            if (block.Kind is not ("code" or "functions")) continue;
            if (string.IsNullOrWhiteSpace(block.Text)) continue;

            var innerStart = Math.Max(0, block.EndOffset - 1 - block.Text.Length);
            var padding = new string('\n', Math.Max(0, LineAt(text, innerStart) - 1));
            var wrapper = padding + "class __RazorPage { " + block.Text + "\n}";

            var lowered = new CSharpFrontend().Translate(MetacodeSource.FromCSharp(wrapper, page.FilePath), options);
            foreach (var diagnostic in lowered.Diagnostics)
                diagnostics.Add(diagnostic);

            var members = lowered.Modules
                .SelectMany(module => module.Declarations)
                .OfType<MetacodeTypeDeclaration>()
                .SelectMany(type => type.Members)
                .ToList();

            if (members.Count == 0)
            {
                diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedDeclaration,
                    MetacodeDiagnosticSeverity.Warning,
                    $"The @{block.Kind} block has no member the C# frontend can read.",
                    Origin(page, block.Line)));
                continue;
            }

            declarations.AddRange(members);
        }
    }

    // ── The render body ────────────────────────────────────────────────────────

    /// <summary>
    /// One Razor control construct: <c>@keyword (header) { then }</c>, plus the <c>else</c> /
    /// <c>else if</c> chain that follows it. The scanner finds the construct; its <c>else</c> is not an
    /// <c>@</c> block, so it is read here from the text after the closing brace.
    /// </summary>
    private sealed class ControlBlock
    {
        public required string Keyword { get; init; }
        public string? Header { get; init; }
        public required int Offset { get; init; }
        public required int BodyStart { get; init; }
        public required int BodyEnd { get; init; }
        public required int Line { get; init; }

        /// <summary>The attached <c>else</c> or <c>else if</c>, if the construct has one.</summary>
        public ControlBlock? Else { get; set; }

        public List<ControlBlock> Children { get; } = [];

        /// <summary>Where the whole construct ends, <c>else</c> branches included.</summary>
        public int ChainEnd => Else is null ? BodyEnd : Else.ChainEnd;
    }

    /// <summary>Turns the page body into statements: control flow from Razor, markup kept as text.</summary>
    private sealed class RazorBodyBuilder
    {
        private readonly RazorDocument _page;
        private readonly string _text;
        private readonly MetacodeOptions _options;
        private readonly List<MetacodeDiagnostic> _diagnostics;
        private readonly List<ControlBlock> _blocks = [];
        private List<(int Start, int End)>? _skips;

        public RazorBodyBuilder(RazorDocument page, string text, MetacodeOptions options, List<MetacodeDiagnostic> diagnostics)
        {
            _page = page;
            _text = text;
            _options = options;
            _diagnostics = diagnostics;

            foreach (var block in page.CodeBlocks.Where(block => block.Kind == "control"))
            {
                var parsed = ParseControl(text, block.Offset);
                if (parsed is not null)
                {
                    _blocks.Add(parsed);
                    continue;
                }

                // The scanner saw a Razor control construct, but not one this frontend can read: the
                // text stays a RAW markup segment, and this is what says so.
                diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedStatement,
                    MetacodeDiagnosticSeverity.Warning,
                    $"a Razor @{block.Name} construct is kept as markup: its body is not a braced block, or its header could not be read",
                    Origin(page, block.Line)));
            }

            AttachElseChains();
            BuildTree();
        }

        public MetacodeBlock Build() => Emit(Roots, 0, _text.Length);

        private List<ControlBlock> Roots { get; } = [];

        /// <summary>
        /// An <c>else</c> is written after the closing brace without an <c>@</c>, so the scanner does
        /// not report it: it is read from the text and attached to the <c>if</c> it belongs to.
        /// </summary>
        private void AttachElseChains()
        {
            foreach (var block in _blocks.Where(block => block.Keyword == "if").ToList())
            {
                var branch = ParseElse(_text, block.BodyEnd);
                if (branch is null) continue;

                block.Else = branch;
                while (branch.Keyword == "else if")
                {
                    var next = ParseElse(_text, branch.BodyEnd);
                    branch.Else = next;
                    if (next is null) break;
                    branch = next;
                }
            }
        }

        /// <summary>
        /// Nesting follows the braces: the innermost body that contains a construct starts is its
        /// parent. An <c>else if</c> is a container of its own, so its body nests inside it.
        /// </summary>
        private void BuildTree()
        {
            foreach (var block in _blocks.OrderBy(block => block.Offset))
            {
                var parent = InnermostContainer(block.Offset);
                if (parent is null) Roots.Add(block);
                else parent.Children.Add(block);
            }
        }

        private ControlBlock? InnermostContainer(int offset)
        {
            ControlBlock? best = null;
            var bestStart = -1;

            foreach (var block in _blocks)
            {
                foreach (var (owner, start, end) in Branches(block))
                {
                    if (start >= offset || offset >= end) continue;
                    if (start <= bestStart) continue;

                    best = owner;
                    bestStart = start;
                }
            }

            return best;
        }

        /// <summary>Every body a construct owns: its own, and one per <c>else</c> branch.</summary>
        private static IEnumerable<(ControlBlock Owner, int Start, int End)> Branches(ControlBlock block)
        {
            yield return (block, block.BodyStart, block.BodyEnd);

            for (var branch = block.Else; branch is not null; branch = branch.Else)
                yield return (branch, branch.BodyStart, branch.BodyEnd);
        }

        private MetacodeBlock Emit(IReadOnlyList<ControlBlock> children, int start, int end)
        {
            var statements = new List<MetacodeStatement>();
            var cursor = start;

            foreach (var child in children)
            {
                if (child.Offset > cursor)
                    AddMarkup(statements, cursor, child.Offset);

                statements.Add(Statement(child));
                cursor = Math.Max(cursor, child.ChainEnd);
            }

            if (end > cursor)
                AddMarkup(statements, cursor, end);

            return new MetacodeBlock(statements) { Origin = Origin(_page, LineAt(_text, Math.Max(0, start))) };
        }

        /// <summary>
        /// Markup between two constructs is one opaque statement, trimmed but never re-shaped. The
        /// directives and the <c>@code</c>/<c>@functions</c> blocks are cut out: they are printed as
        /// declarations, and repeating them inside <c>Render</c> would say the page runs them twice.
        /// </summary>
        private void AddMarkup(List<MetacodeStatement> statements, int start, int end)
        {
            var snippet = MarkupText(start, end);
            if (snippet.Length == 0) return;

            statements.Add(new MetacodeOpaqueStatement(MetacodeLanguage.Razor, snippet, "HTML markup")
            {
                Origin = Origin(_page, LineAt(_text, start)),
            });
        }

        /// <summary>The text of <c>[start, end)</c> with the declaration-only spans removed.</summary>
        private string MarkupText(int start, int end)
        {
            var skips = Skips();
            if (skips.Count == 0) return _text[start..end].Trim();

            var builder = new StringBuilder();
            var position = start;

            foreach (var (from, to) in skips)
            {
                if (to <= position || from >= end) continue;

                var cut = Math.Max(position, from);
                if (cut > position)
                    builder.Append(_text[position..cut]);

                position = Math.Min(end, to);
            }

            if (position < end)
                builder.Append(_text[position..end]);

            return builder.ToString().Trim();
        }

        /// <summary>Directive lines and <c>@code</c>/<c>@functions</c> blocks: not part of the body.</summary>
        private IReadOnlyList<(int Start, int End)> Skips()
        {
            if (_skips is not null) return _skips;

            var skips = new List<(int Start, int End)>();
            foreach (var directive in _page.Directives)
            {
                var start = Math.Max(0, directive.Offset);
                var line = directive.Line;
                var end = line < _page.LineStarts.Length ? _page.LineStarts[line] : _text.Length;
                skips.Add((start, end));
            }

            foreach (var block in _page.CodeBlocks.Where(block => block.Kind is "code" or "functions"))
                skips.Add((Math.Max(0, block.Offset), block.EndOffset));

            _skips = skips.OrderBy(skip => skip.Start).ToList();
            return _skips;
        }

        private MetacodeStatement Statement(ControlBlock block)
        {
            var body = Emit(block.Children, block.BodyStart + 1, block.BodyEnd - 1);
            var lowered = LowerHeader(block);

            if (lowered is null)
            {
                _diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedStatement,
                    MetacodeDiagnosticSeverity.Warning,
                    $"a Razor @{block.Keyword} block is kept as text: its header is not a C# construct this frontend can lower, or its body is not a braced block",
                    Origin(_page, block.Line)));

                return new MetacodeOpaqueStatement(
                    MetacodeLanguage.Razor,
                    _text[block.Offset..block.ChainEnd],
                    $"a Razor @{block.Keyword} block is not modelled") { Origin = Origin(_page, block.Line) };
            }

            var statement = WithBody(lowered, body);
            return block.Else is null ? statement : WithElse(statement, Branch(block.Else));
        }

        /// <summary>The body of an <c>else</c> branch: markup for a plain <c>else</c>, a nested IF for <c>else if</c>.</summary>
        private MetacodeBlock Branch(ControlBlock branch) => branch.Keyword == "else"
            ? Emit(branch.Children, branch.BodyStart + 1, branch.BodyEnd - 1)
            : MetacodeBlock.Of(Statement(branch));

        /// <summary>
        /// The header is C#, so the C# frontend parses it: the construct is lowered with an empty body
        /// and the markup is spliced in. An <c>else if</c> has no standalone C# form, so it is lowered
        /// as the <c>if</c> it is and folded back into the chain.
        /// </summary>
        private MetacodeStatement? LowerHeader(ControlBlock block)
        {
            var keyword = block.Keyword == "else if" ? "if" : block.Keyword;
            var header = block.Header is null ? null : keyword + " (" + block.Header + ") { }";
            if (header is null) return null;

            var wrapper = new string('\n', Math.Max(0, block.Line - 1)) + "class __R { void __M() { " + header + " } }";
            var lowered = new CSharpFrontend().Translate(
                MetacodeSource.FromCSharp(wrapper, _page.FilePath),
                _options with { IncludeComments = false });

            return lowered.Modules
                .SelectMany(module => module.Declarations)
                .OfType<MetacodeTypeDeclaration>()
                .SelectMany(type => type.Members)
                .OfType<MetacodeFunction>()
                .SelectMany(function => function.Body?.Statements ?? Array.Empty<MetacodeStatement>())
                .FirstOrDefault(statement => statement is MetacodeIf or MetacodeWhile or MetacodeForEach or MetacodeFor or MetacodeLock or MetacodeUsing);
        }

        private static MetacodeStatement WithBody(MetacodeStatement statement, MetacodeBlock body) => statement switch
        {
            MetacodeIf @if => @if with { Then = body },
            MetacodeWhile @while => @while with { Body = body },
            MetacodeForEach forEach => forEach with { Body = body },
            MetacodeFor @for => @for with { Body = body },
            MetacodeLock @lock => @lock with { Body = body },
            MetacodeUsing @using => @using with { Body = body },
            _ => statement,
        };

        private static MetacodeStatement WithElse(MetacodeStatement statement, MetacodeBlock @else) =>
            statement is MetacodeIf @if ? @if with { Else = @else } : statement;

        /// <summary>
        /// <c>@if (c) { … }</c>, <c>@foreach (… in …)</c>, <c>@for (…)</c>, <c>@while (…)</c>,
        /// <c>@lock (…)</c>, <c>@using (…)</c>. Returns null for a construct whose body is not a braced
        /// block this scanner can find, or whose keyword has markup inside the header (a <c>@switch</c>
        /// over markup cases).
        /// </summary>
        private static ControlBlock? ParseControl(string text, int at)
        {
            var i = at + 1;
            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            if (i == start) return null;

            var keyword = text[start..i];

            string? header = null;
            i = SkipWhitespace(text, i);
            if (i < text.Length && text[i] == '(')
            {
                var close = MatchParen(text, i);
                if (close < 0) return null;
                header = text[(i + 1)..close];
                i = close + 1;
            }

            return Braced(text, keyword, header, at, i);
        }

        /// <summary><c>else { … }</c> / <c>else if (c) { … }</c> read after a construct's closing brace.</summary>
        private static ControlBlock? ParseElse(string text, int after)
        {
            var i = SkipWhitespace(text, after);
            if (!IsWordAt(text, i, "else")) return null;

            var at = i;
            i += 4;
            var keyword = "else";

            var next = SkipWhitespace(text, i);
            if (IsWordAt(text, next, "if"))
            {
                keyword = "else if";
                i = next + 2;
            }
            else
            {
                i = next;
            }

            string? header = null;
            i = SkipWhitespace(text, i);
            if (i < text.Length && text[i] == '(')
            {
                var close = MatchParen(text, i);
                if (close < 0) return null;
                header = text[(i + 1)..close];
                i = close + 1;
            }

            return Braced(text, keyword, header, at, i);
        }

        private static ControlBlock? Braced(string text, string keyword, string? header, int at, int i)
        {
            i = SkipWhitespace(text, i);
            if (i >= text.Length || text[i] != '{') return null;

            var bodyEnd = MatchBrace(text, i);
            if (bodyEnd < 0) return null;

            return new ControlBlock
            {
                Keyword = keyword,
                Header = header?.Trim(),
                Offset = at,
                BodyStart = i,
                BodyEnd = bodyEnd,
                Line = LineAt(text, at),
            };
        }
    }

    // ── Scanning helpers ───────────────────────────────────────────────────────

    /// <summary>Index just past the <c>)</c> matching the <c>(</c> at <paramref name="open"/>.</summary>
    private static int MatchParen(string text, int open) => Match(text, open, '(', ')');

    /// <summary>
    /// Index just past the <c>}</c> matching the <c>{</c> at <paramref name="open"/>. A
    /// <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> body and an <c>@(…)</c> expression are skipped, because
    /// their braces are not the block's.
    /// </summary>
    private static int MatchBrace(string text, int open)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '<')
            {
                if (IsWordAt(text, i + 1, "script"))
                {
                    var close = text.IndexOf("</script", i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) return -1;
                    i = close + "</script".Length;
                    continue;
                }
                if (IsWordAt(text, i + 1, "style"))
                {
                    var close = text.IndexOf("</style", i, StringComparison.OrdinalIgnoreCase);
                    if (close < 0) return -1;
                    i = close + "</style".Length;
                    continue;
                }
            }

            // Quotes are deliberately not treated as delimiters: this scans markup, where an
            // apostrophe in prose ("don't") is far more common than a brace inside a quoted value,
            // and treating the apostrophe as an opening quote would swallow the closing brace.
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '(')
            {
                var close = MatchParen(text, i + 1);
                if (close < 0) return -1;
                i = close;
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i + 1;
            }

            i++;
        }

        return -1;
    }

    private static int Match(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipQuoted(text, i) - 1;
                continue;
            }

            if (c == opening) depth++;
            else if (c == closing)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }

        return -1;
    }

    /// <summary>Index just past the closing quote; escapes and doubled quotes are honoured.</summary>
    private static int SkipQuoted(string text, int quote)
    {
        var delimiter = text[quote];
        var i = quote + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (text[i] == delimiter)
            {
                // A doubled quote is an escaped quote in a C# string, not the end.
                if (i + 1 < text.Length && text[i + 1] == delimiter)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }

        return text.Length;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
            i++;
        return i;
    }

    private static bool IsWordAt(string text, int index, string word)
    {
        if (index < 0 || index + word.Length > text.Length) return false;
        if (string.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        var after = index + word.Length;
        return after >= text.Length || !(char.IsLetterOrDigit(text[after]) || text[after] == '_');
    }

    private static int LineAt(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static bool IsRazorPath(string path) =>
        path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

    private static string LastSegment(string typeName)
    {
        var trimmed = typeName.Trim();
        var cut = trimmed.LastIndexOfAny(['.', '<', '?']);
        return cut >= 0 && cut + 1 < trimmed.Length ? trimmed[(cut + 1)..].TrimEnd('>') : trimmed;
    }

    private static MetacodeOrigin Origin(RazorDocument page, int line, int column = 1) =>
        new(MetacodeLanguage.Razor, page.FilePath, line, column, line, column);
}
