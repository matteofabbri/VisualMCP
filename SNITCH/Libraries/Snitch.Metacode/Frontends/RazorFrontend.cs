using System.Text;
using Snitch.Languages.Razor;

namespace Snitch.Metacode.Frontends;

/// <summary>
/// Reads a Razor page (<c>.cshtml</c>) or a Blazor component (<c>.razor</c>) into the shared model.
/// A page is a class that renders: <c>@model</c> and <c>@inject</c> become properties, the members of
/// <c>@code</c>/<c>@functions</c> are lowered by <see cref="CSharpFrontend"/>, an <c>@section</c> becomes
/// a method of its own, and the markup body becomes the body of a <c>Render</c> method.
/// </summary>
/// <remarks>
/// Every Razor construct the model can express is a node of the model: <c>@if</c>/<c>@else</c>,
/// <c>@foreach</c>, <c>@for</c>, <c>@while</c>, <c>@do</c>/<c>while</c>, <c>@switch</c> with its
/// <c>case</c>/<c>default</c> sections, <c>@try</c>/<c>catch</c>/<c>finally</c>, <c>@lock</c>,
/// <c>@using</c>, a <c>@{ … }</c> code block and a body with or without braces. Only the HTML between
/// them stays <c>RAW[razor]</c>, and only because markup is not an algorithm. The scanner is not the
/// Razor compiler, so a construct whose header or body cannot be read stays one opaque statement with
/// its source text and a diagnostic — never dropped.
/// </remarks>
public sealed class RazorFrontend : ILanguageFrontend
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "foreach", "for", "while", "switch", "do", "try", "lock", "using",
    };

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

        var sections = ReadSections(page, text);

        AddDirectives(page, declarations);
        AddModel(page, declarations);
        AddInjections(page, declarations);

        var builder = new RazorBodyBuilder(
            page, text, options, diagnostics,
            0, text.Length, sections, isSectionBody: false);

        declarations.Add(new MetacodeFunction(
            "Render",
            MetacodeFunctionKind.Method,
            Body: builder.Build())
        {
            Modifiers = MetacodeModifiers.Public,
            Origin = Origin(page, 1),
        });

        AddCodeBlocks(page, text, options, declarations, diagnostics);
        AddSections(page, text, options, diagnostics, declarations, sections);

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

            var innerStart = InnerStart(block);
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

    /// <summary>
    /// An <c>@section Name { … }</c> is a named fragment a layout renders, so it becomes a method whose
    /// body is the markup and the control flow it contains. Modelling it separately keeps it out of
    /// <c>Render</c>, which is where the page body — not the section — belongs.
    /// </summary>
    private static void AddSections(
        RazorDocument page,
        string text,
        MetacodeOptions options,
        List<MetacodeDiagnostic> diagnostics,
        List<MetacodeDeclaration> declarations,
        IReadOnlyList<SectionBlock> sections)
    {
        foreach (var section in sections)
        {
            var builder = new RazorBodyBuilder(
                page, text, options, diagnostics,
                section.ContentStart, section.ContentEnd, sections, isSectionBody: true);

            declarations.Add(new MetacodeFunction(
                "Section_" + section.Name,
                MetacodeFunctionKind.Method,
                Body: builder.Build())
            {
                Modifiers = MetacodeModifiers.Public,
                Origin = Origin(page, section.Line),
            });
        }
    }

    // ── Sections ───────────────────────────────────────────────────────────────

    /// <summary>One <c>@section Name { … }</c>: the name and the offset range of the body.</summary>
    private sealed record SectionBlock(string Name, int Offset, int ContentStart, int ContentEnd, int Line);

    private static List<SectionBlock> ReadSections(RazorDocument page, string text)
    {
        var sections = new List<SectionBlock>();

        foreach (var block in page.CodeBlocks)
        {
            if (block.Kind != "section") continue;
            if (block.Name is not { Length: > 0 } name) continue;

            // ReadSection stops just past the opening brace (or at the name when there is none).
            var open = block.EndOffset - 1;
            if (open < 0 || open >= text.Length || text[open] != '{') continue;

            var close = MatchBrace(text, open);
            if (close < 0) continue;

            sections.Add(new SectionBlock(name, block.Offset, open + 1, close - 1, block.Line));
        }

        return sections;
    }

    // ── The render body ────────────────────────────────────────────────────────

    /// <summary>The content of a braced body, or of the single statement that stands in for one.</summary>
    private readonly record struct BodyRange(int Start, int End, int After);

    /// <summary>
    /// One Razor control construct and every branch it owns: an <c>else</c>/<c>else if</c> chain, the
    /// <c>case</c> sections of a <c>switch</c>, the <c>catch</c>/<c>finally</c> clauses of a
    /// <c>try</c>, and the <c>while</c> of a <c>do</c>.
    /// </summary>
    private sealed class ControlBlock
    {
        public required string Keyword { get; init; }
        public string? Header { get; init; }
        public required int Offset { get; init; }
        public required int Line { get; init; }
        public required BodyRange Body { get; init; }

        /// <summary>Where the construct ends; an <c>else</c> chain extends it through <see cref="Else"/>.</summary>
        public int ConstructEnd { get; set; }

        /// <summary>The attached <c>else</c> or <c>else if</c>, if the construct has one.</summary>
        public ControlBlock? Else { get; set; }

        public List<SwitchSection> Sections { get; } = [];

        public List<CatchClause> Catches { get; } = [];

        public BodyRange? Finally { get; set; }

        public List<Entry> Children { get; } = [];

        /// <summary>Where the whole construct ends, <c>else</c> branches included.</summary>
        public int End => Else?.End ?? ConstructEnd;
    }

    private sealed class SwitchSection
    {
        public required int Offset { get; init; }
        public required int Line { get; init; }
        public required BodyRange Body { get; init; }
        public required IReadOnlyList<string> Labels { get; init; }
        public bool IsDefault { get; init; }

        /// <summary>The section ends with a <c>break;</c> (or <c>continue;</c>) of its own.</summary>
        public bool EndsWithBreak { get; init; }
    }

    private sealed class CatchClause
    {
        public required int Offset { get; init; }
        public required int Line { get; init; }
        public required BodyRange Body { get; init; }
        public string? ExceptionType { get; init; }
        public string? VariableName { get; init; }
        public string? Filter { get; init; }
    }

    /// <summary>
    /// One item of a body: a control construct or a lowered <c>@{ … }</c> code block. Both occupy a
    /// range of the page, and emission walks them in source order.
    /// </summary>
    private sealed class Entry
    {
        public required int Offset { get; init; }
        public required int End { get; init; }
        public ControlBlock? Control { get; init; }
        public MetacodeBlock? Code { get; init; }
    }

    /// <summary>Turns the page body into statements: control flow from Razor, markup kept as text.</summary>
    private sealed class RazorBodyBuilder
    {
        private readonly RazorDocument _page;
        private readonly string _text;
        private readonly MetacodeOptions _options;
        private readonly List<MetacodeDiagnostic> _diagnostics;
        private readonly int _start;
        private readonly int _end;
        private readonly IReadOnlyList<SectionBlock> _sections;
        private readonly bool _isSectionBody;
        private readonly List<Entry> _entries = [];
        private readonly List<Entry> _roots = [];
        private List<(int Start, int End)>? _skips;

        public RazorBodyBuilder(
            RazorDocument page,
            string text,
            MetacodeOptions options,
            List<MetacodeDiagnostic> diagnostics,
            int start,
            int end,
            IReadOnlyList<SectionBlock> sections,
            bool isSectionBody)
        {
            _page = page;
            _text = text;
            _options = options;
            _diagnostics = diagnostics;
            _start = start;
            _end = end;
            _sections = sections;
            _isSectionBody = isSectionBody;

            AddControlBlocks();
            AddBodyCodeBlocks();
            AttachElseChains();
            BuildTree();
        }

        public MetacodeBlock Build() => Emit(_roots, _start, _end);

        private void AddControlBlocks()
        {
            foreach (var block in _page.CodeBlocks.Where(block => block.Kind == "control"))
            {
                if (!BelongsHere(block.Offset)) continue;

                var parsed = ParseControl(_text, block.Offset);
                if (parsed is not null)
                {
                    _entries.Add(new Entry { Offset = parsed.Offset, End = parsed.End, Control = parsed });
                    continue;
                }

                // The scanner saw a Razor control construct, but not one this frontend can read: the
                // text stays a RAW markup segment, and this is what says so.
                _diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedStatement,
                    MetacodeDiagnosticSeverity.Warning,
                    $"a Razor @{block.Name} construct is kept as markup: its body is not a braced block, or its header could not be read",
                    Origin(_page, block.Line)));
            }
        }

        /// <summary>
        /// A top-level <c>@{ … }</c> block is C# the page runs where it stands, so its statements are
        /// lowered by the C# frontend and spliced into the body at that point.
        /// </summary>
        private void AddBodyCodeBlocks()
        {
            foreach (var block in _page.CodeBlocks.Where(block => block.Kind == "block"))
            {
                if (!BelongsHere(block.Offset)) continue;
                if (string.IsNullOrWhiteSpace(block.Text)) continue;

                var statements = LowerStatements(block.Text, InnerStart(block));
                if (statements is null)
                {
                    _diagnostics.Add(new MetacodeDiagnostic(
                        MetacodeDiagnosticCodes.UnsupportedStatement,
                        MetacodeDiagnosticSeverity.Warning,
                        "a Razor @{ … } block has no statement the C# frontend can read",
                        Origin(_page, block.Line)));
                    continue;
                }

                _entries.Add(new Entry { Offset = block.Offset, End = block.EndOffset, Code = statements });
            }
        }

        /// <summary>
        /// A block belongs to this builder when it sits in its range and, for the page body, outside
        /// every <c>@section</c> — those are emitted by the section's own builder.
        /// </summary>
        private bool BelongsHere(int offset)
        {
            if (offset < _start || offset >= _end) return false;
            if (_isSectionBody) return true;
            return !_sections.Any(section => offset >= section.ContentStart && offset < section.ContentEnd);
        }

        /// <summary>
        /// An <c>else</c> is written after the closing brace without an <c>@</c>, so the scanner does
        /// not report it: it is read from the text and attached to the <c>if</c> it belongs to.
        /// </summary>
        private void AttachElseChains()
        {
            foreach (var block in _entries.Select(entry => entry.Control).Where(block => block?.Keyword == "if").ToList())
            {
                if (block is null) continue;

                var branch = ParseElse(_text, block.Body.After);
                if (branch is null) continue;

                block.Else = branch;
                while (branch.Keyword == "else if")
                {
                    var next = ParseElse(_text, branch.Body.After);
                    branch.Else = next;
                    if (next is null) break;
                    branch = next;
                }
            }
        }

        /// <summary>
        /// Nesting follows the ranges: the innermost body that contains a construct's start is its
        /// parent. An <c>else if</c> is a container of its own, so its body nests inside it.
        /// </summary>
        private void BuildTree()
        {
            foreach (var entry in _entries.OrderBy(entry => entry.Offset))
            {
                var parent = InnermostContainer(entry);
                if (parent is null) _roots.Add(entry);
                else parent.Children.Add(entry);
            }
        }

        private ControlBlock? InnermostContainer(Entry entry)
        {
            ControlBlock? best = null;
            var bestStart = -1;

            foreach (var candidate in _entries)
            {
                var block = candidate.Control;
                if (block is null) continue;

                foreach (var (owner, start, end) in Containers(block))
                {
                    if (ReferenceEquals(owner, entry.Control)) continue;
                    if (entry.Offset < start || entry.Offset >= end) continue;
                    if (start < bestStart) continue;

                    best = owner;
                    bestStart = start;
                }
            }

            return best;
        }

        /// <summary>Every body a construct owns: its own, each <c>else</c>, each case, each catch.</summary>
        private static IEnumerable<(ControlBlock Owner, int Start, int End)> Containers(ControlBlock block)
        {
            yield return (block, block.Body.Start, block.Body.End);

            for (var branch = block.Else; branch is not null; branch = branch.Else)
                yield return (branch, branch.Body.Start, branch.Body.End);

            foreach (var section in block.Sections)
                yield return (block, section.Body.Start, section.Body.End);

            foreach (var @catch in block.Catches)
                yield return (block, @catch.Body.Start, @catch.Body.End);

            if (block.Finally is { } final)
                yield return (block, final.Start, final.End);
        }

        private MetacodeBlock Emit(IReadOnlyList<Entry> entries, int start, int end)
        {
            var statements = new List<MetacodeStatement>();
            var cursor = start;

            foreach (var entry in entries)
            {
                if (entry.Offset < start || entry.End > end) continue;
                if (entry.Offset > cursor)
                    AddMarkup(statements, cursor, entry.Offset);

                if (entry.Code is not null)
                    statements.AddRange(entry.Code.Statements);
                else
                    statements.Add(Statement(entry.Control!));

                cursor = Math.Max(cursor, entry.End);
            }

            if (end > cursor)
                AddMarkup(statements, cursor, end);

            return new MetacodeBlock(statements) { Origin = Origin(_page, LineAt(_text, Math.Max(0, start))) };
        }

        /// <summary>
        /// Markup between two constructs is one opaque statement, trimmed but never re-shaped. The
        /// directives, the <c>@code</c>/<c>@functions</c> blocks and the sections are cut out: they are
        /// printed as declarations of their own, and repeating them inside <c>Render</c> would say the
        /// page runs them twice.
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

        /// <summary>
        /// Directive lines, <c>@code</c>/<c>@functions</c> blocks, <c>@{ … }</c> blocks and sections:
        /// none of them is part of the markup the page renders here.
        /// </summary>
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

            foreach (var block in _page.CodeBlocks.Where(block => block.Kind is "code" or "functions" or "block"))
                skips.Add((Math.Max(0, block.Offset), block.EndOffset));

            foreach (var section in _sections)
            {
                // A section's own body is what this builder is emitting, not something to cut out of it.
                if (_isSectionBody && section.ContentStart == _start && section.ContentEnd == _end) continue;
                skips.Add((section.Offset, Math.Min(_text.Length, section.ContentEnd + 1)));
            }

            _skips = skips.OrderBy(skip => skip.Start).ToList();
            return _skips;
        }

        private MetacodeStatement Statement(ControlBlock block) => block.Keyword switch
        {
            "switch" => SwitchStatement(block),
            "try" => TryStatement(block),
            "do" => DoStatement(block),
            _ => BranchStatement(block),
        };

        /// <summary><c>@if</c>, <c>@foreach</c>, <c>@for</c>, <c>@while</c>, <c>@lock</c>, <c>@using</c>.</summary>
        private MetacodeStatement BranchStatement(ControlBlock block)
        {
            var body = Emit(block.Children, block.Body.Start, block.Body.End);
            var lowered = LowerHeader(block);

            if (lowered is null)
            {
                _diagnostics.Add(new MetacodeDiagnostic(
                    MetacodeDiagnosticCodes.UnsupportedStatement,
                    MetacodeDiagnosticSeverity.Warning,
                    $"a Razor @{block.Keyword} block is kept as text: its header is not a C# construct this frontend can lower",
                    Origin(_page, block.Line)));

                return new MetacodeOpaqueStatement(
                    MetacodeLanguage.Razor,
                    _text[block.Offset..block.End],
                    $"a Razor @{block.Keyword} block is not modelled") { Origin = Origin(_page, block.Line) };
            }

            var statement = WithBody(lowered, body);
            return block.Else is null ? statement : WithElse(statement, Branch(block.Else));
        }

        /// <summary>
        /// <c>@switch (x) { case …: markup break; default: markup }</c>. A <c>case</c>/<c>default</c>
        /// is a label at the start of a line; the markup between two sections is the section's body,
        /// and the <c>break;</c> that closes it is the statement it is.
        /// </summary>
        private MetacodeStatement SwitchStatement(ControlBlock block)
        {
            var subject = block.Header is null ? null : LowerExpression(block.Header, block.Line, "a switch subject");
            if (subject is null) return Unmodelled(block, "a switch without a subject expression is kept as text");

            var cases = new List<MetacodeSwitchCase>();

            foreach (var section in block.Sections)
            {
                var labels = new List<MetacodeExpression>();
                foreach (var label in section.Labels)
                    labels.Add(LowerExpression(label, section.Line, "a case label"));

                var body = Emit(block.Children, section.Body.Start, section.Body.End);
                var statements = body.Statements.ToList();

                if (section.EndsWithBreak)
                    statements.Add(new MetacodeBreak { Origin = Origin(_page, section.Line) });

                cases.Add(new MetacodeSwitchCase(
                    labels,
                    new MetacodeBlock(statements) { Origin = body.Origin },
                    section.IsDefault) { Origin = Origin(_page, section.Line) });
            }

            return new MetacodeSwitch(subject, cases) { Origin = Origin(_page, block.Line) };
        }

        /// <summary>
        /// <c>@try { … } catch (T e) when (f) { … } finally { … }</c>. The <c>catch</c>/<c>finally</c>
        /// keywords carry no <c>@</c>, so they are read from the text after the try body.
        /// </summary>
        private MetacodeStatement TryStatement(ControlBlock block)
        {
            var body = Emit(block.Children, block.Body.Start, block.Body.End);

            var catches = block.Catches.Select(@catch => new MetacodeCatch(
                    @catch.ExceptionType is null ? null : new MetacodeTypeRef(@catch.ExceptionType),
                    @catch.VariableName,
                    Emit(block.Children, @catch.Body.Start, @catch.Body.End),
                    @catch.Filter is null ? null : LowerExpression(@catch.Filter, @catch.Line, "a catch filter"))
                { Origin = Origin(_page, @catch.Line) })
                .ToList();

            var final = block.Finally is { } finallyBody
                ? Emit(block.Children, finallyBody.Start, finallyBody.End)
                : null;

            return new MetacodeTry(body, catches, final) { Origin = Origin(_page, block.Line) };
        }

        /// <summary>
        /// <c>@do { … } while (condition);</c> — the test comes last, which is exactly what a
        /// <c>REPEAT … UNTIL</c> prints.
        /// </summary>
        private MetacodeStatement DoStatement(ControlBlock block)
        {
            var body = Emit(block.Children, block.Body.Start, block.Body.End);
            var condition = block.Header is null
                ? null
                : LowerExpression(block.Header, block.Line, "a do/while condition");

            if (condition is null) return Unmodelled(block, "a Razor @do block without a condition is kept as text");

            return new MetacodeDoWhile(body, condition) { Origin = Origin(_page, block.Line) };
        }

        private MetacodeStatement Unmodelled(ControlBlock block, string message)
        {
            _diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.UnsupportedStatement,
                MetacodeDiagnosticSeverity.Warning,
                message,
                Origin(_page, block.Line)));

            return new MetacodeOpaqueStatement(
                MetacodeLanguage.Razor,
                _text[block.Offset..block.End],
                message) { Origin = Origin(_page, block.Line) };
        }

        /// <summary>The body of an <c>else</c> branch: markup for a plain <c>else</c>, a nested IF for <c>else if</c>.</summary>
        private MetacodeBlock Branch(ControlBlock branch) => branch.Keyword == "else"
            ? Emit(branch.Children, branch.Body.Start, branch.Body.End)
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

            var lowered = LowerCSharp(header, block.Line, "void __M() {", "}");

            return lowered.FirstOrDefault(statement =>
                statement is MetacodeIf or MetacodeWhile or MetacodeForEach or MetacodeFor or MetacodeLock or MetacodeUsing);
        }

        /// <summary>An expression from the page, lowered by the C# frontend on the page's own line.</summary>
        private MetacodeExpression LowerExpression(string text, int line, string what)
        {
            var lowered = LowerCSharp("var __e = (" + text + ");", line, "void __M() {", "}");

            var local = lowered.OfType<MetacodeLocalVariable>().FirstOrDefault(variable => variable.Name == "__e");
            if (local?.Initializer is not null) return local.Initializer;

            _diagnostics.Add(new MetacodeDiagnostic(
                MetacodeDiagnosticCodes.UnsupportedExpression,
                MetacodeDiagnosticSeverity.Warning,
                $"{what} is kept as text: the C# frontend could not read it",
                Origin(_page, line)));

            return new MetacodeOpaqueExpression(MetacodeLanguage.Razor, text, what) { Origin = Origin(_page, line) };
        }

        /// <summary>Lowers a <c>@{ … }</c> block into the statements it contains.</summary>
        private MetacodeBlock? LowerStatements(string text, int innerStart)
        {
            var lowered = LowerCSharp(text, LineAt(_text, innerStart), "void __M() {", "}");
            return lowered.Count == 0 ? null : new MetacodeBlock(lowered);
        }

        /// <summary>
        /// Wraps C# taken from the page in a method whose first line sits on the page line it came
        /// from, so every origin the C# frontend reports points at the page's own line, and returns
        /// the statements of that method.
        /// </summary>
        private IReadOnlyList<MetacodeStatement> LowerCSharp(string snippet, int line, string open, string close)
        {
            var padding = new string('\n', Math.Max(0, line - 1));
            var wrapper = padding + "class __R { " + open + " " + snippet + " " + close + " }";

            var lowered = new CSharpFrontend().Translate(
                MetacodeSource.FromCSharp(wrapper, _page.FilePath),
                _options with { IncludeComments = false });

            return lowered.Modules
                .SelectMany(module => module.Declarations)
                .OfType<MetacodeTypeDeclaration>()
                .SelectMany(type => type.Members)
                .OfType<MetacodeFunction>()
                .SelectMany(function => function.Body?.Statements ?? Array.Empty<MetacodeStatement>())
                .ToList();
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
    }

    // ── Parsing a control construct ────────────────────────────────────────────

    /// <summary>
    /// <c>@if (c)</c>, <c>@foreach (… in …)</c>, <c>@for (…)</c>, <c>@while (…)</c>, <c>@lock (…)</c>,
    /// <c>@using (…)</c>, <c>@switch (…)</c>, <c>@try</c> and <c>@do</c>, each with a braced or a
    /// brace-less body. Returns null for anything that is not one of them, or whose header is unreadable.
    /// </summary>
    private static ControlBlock? ParseControl(string text, int at)
    {
        if (at < 0 || at + 1 >= text.Length || text[at] != '@') return null;

        var start = at + 1;
        var i = start;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        if (i == start) return null;

        var keyword = text[start..i];
        if (!ControlKeywords.Contains(keyword)) return null;

        var line = LineAt(text, at);

        switch (keyword)
        {
            case "switch":
                return ParseSwitch(text, at, keyword, i, line);
            case "try":
                return ParseTry(text, at, keyword, i, line);
            case "do":
                return ParseDo(text, at, keyword, i, line);
        }

        i = SkipWhitespace(text, i);
        string? header = null;
        if (i < text.Length && text[i] == '(')
        {
            var close = MatchParen(text, i);
            if (close < 0) return null;
            header = text[(i + 1)..(close - 1)].Trim();
            i = close;
        }

        if (!TryParseBody(text, i, out var body, out var after)) return null;

        return new ControlBlock
        {
            Keyword = keyword,
            Header = header,
            Offset = at,
            Line = line,
            Body = body,
            ConstructEnd = after,
        };
    }

    private static ControlBlock? ParseSwitch(string text, int at, string keyword, int afterKeyword, int line)
    {
        var i = SkipWhitespace(text, afterKeyword);
        if (i >= text.Length || text[i] != '(') return null;

        var close = MatchParen(text, i);
        if (close < 0) return null;

        var header = text[(i + 1)..(close - 1)].Trim();
        i = SkipWhitespace(text, close);
        if (i >= text.Length || text[i] != '{') return null;

        var end = MatchBrace(text, i);
        if (end < 0) return null;

        var block = new ControlBlock
        {
            Keyword = keyword,
            Header = header,
            Offset = at,
            Line = line,
            Body = new BodyRange(i + 1, end - 1, end),
            ConstructEnd = end,
        };

        foreach (var section in ParseSwitchSections(text, block.Body.Start, block.Body.End))
            block.Sections.Add(section);

        return block;
    }

    /// <summary>
    /// The <c>case</c>/<c>default</c> labels of a switch body and the markup each one owns. A label
    /// starts a line — that is what makes it a label and not the word inside a sentence — and the
    /// body of a section runs to the next label. Consecutive labels with nothing but whitespace
    /// between them belong to the same section.
    /// </summary>
    private static List<SwitchSection> ParseSwitchSections(string text, int start, int end)
    {
        var labels = new List<(int Offset, int AfterColon, string? Label, bool IsDefault, int Line)>();
        var i = start;

        while (i < end)
        {
            var lineEnd = Math.Min(EndOfLine(text, i), end);
            var p = i;
            while (p < lineEnd && (text[p] == ' ' || text[p] == '\t')) p++;

            if (p < lineEnd && (IsWordAt(text, p, "case") || IsWordAt(text, p, "default")))
            {
                var colon = FindLabelColon(text, p, lineEnd);
                if (colon > 0)
                {
                    var isDefault = IsWordAt(text, p, "default");
                    var label = isDefault ? null : text[(p + 4)..colon].Trim();
                    labels.Add((p, colon + 1, label, isDefault, LineAt(text, p)));
                }
            }

            if (lineEnd >= end) break;
            i = lineEnd + 1;
        }

        var sections = new List<SwitchSection>();
        for (var index = 0; index < labels.Count; index++)
        {
            var current = labels[index];
            var ownLabels = new List<string>();
            var isDefault = current.IsDefault;
            var bodyStart = current.AfterColon;

            if (current.Label is { Length: > 0 } own) ownLabels.Add(own);

            var next = index + 1;
            while (next < labels.Count && IsWhitespaceOnly(text, bodyStart, labels[next].Offset))
            {
                if (labels[next].Label is { Length: > 0 } sibling) ownLabels.Add(sibling);
                isDefault |= labels[next].IsDefault;
                bodyStart = labels[next].AfterColon;
                index = next;
                next++;
            }

            var bodyEnd = next < labels.Count ? labels[next].Offset : end;
            var (trimmedEnd, endsWithBreak) = StripTrailingBreak(text, bodyStart, bodyEnd);

            sections.Add(new SwitchSection
            {
                Offset = current.Offset,
                Line = current.Line,
                Body = new BodyRange(bodyStart, trimmedEnd, trimmedEnd),
                Labels = ownLabels,
                IsDefault = isDefault,
                EndsWithBreak = endsWithBreak,
            });
        }

        return sections;
    }

    /// <summary>
    /// A section ends with <c>break;</c> (or <c>continue;</c>) on its own line: that is a C# statement,
    /// not markup, so it is cut out of the body and emitted as the statement it is.
    /// </summary>
    private static (int End, bool Found) StripTrailingBreak(string text, int start, int end)
    {
        var i = end;
        while (i > start && char.IsWhiteSpace(text[i - 1])) i--;
        if (i <= start || text[i - 1] != ';') return (end, false);

        i--;
        while (i > start && char.IsWhiteSpace(text[i - 1])) i--;

        var wordEnd = i;
        while (i > start && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) i--;
        var word = text[i..wordEnd];
        if (word is not ("break" or "continue")) return (end, false);

        var lineStart = i;
        while (lineStart > start && text[lineStart - 1] != '\n') lineStart--;
        if (!IsWhitespaceOnly(text, lineStart, i)) return (end, false);

        return (i, true);
    }

    /// <summary><c>@try { … } catch (T e) when (f) { … } finally { … }</c>.</summary>
    private static ControlBlock? ParseTry(string text, int at, string keyword, int afterKeyword, int line)
    {
        if (!TryParseBody(text, SkipWhitespace(text, afterKeyword), out var body, out var after)) return null;

        var block = new ControlBlock
        {
            Keyword = keyword,
            Offset = at,
            Line = line,
            Body = body,
            ConstructEnd = after,
        };

        var i = after;
        while (true)
        {
            var next = SkipWhitespace(text, i);

            if (IsWordAt(text, next, "catch"))
            {
                var j = SkipWhitespace(text, next + 5);
                string? declaration = null;

                if (j < text.Length && text[j] == '(')
                {
                    var close = MatchParen(text, j);
                    if (close < 0) return null;
                    declaration = text[(j + 1)..(close - 1)].Trim();
                    j = close;
                }

                j = SkipWhitespace(text, j);
                string? filter = null;

                if (IsWordAt(text, j, "when"))
                {
                    j = SkipWhitespace(text, j + 4);
                    if (j >= text.Length || text[j] != '(') return null;
                    var close = MatchParen(text, j);
                    if (close < 0) return null;
                    filter = text[(j + 1)..(close - 1)].Trim();
                    j = close;
                }

                if (!TryParseBody(text, j, out var catchBody, out var catchAfter)) return null;

                var (type, name) = SplitCatchDeclaration(declaration);
                block.Catches.Add(new CatchClause
                {
                    Offset = next,
                    Line = LineAt(text, next),
                    Body = catchBody,
                    ExceptionType = type,
                    VariableName = name,
                    Filter = filter,
                });

                i = catchAfter;
                continue;
            }

            if (IsWordAt(text, next, "finally"))
            {
                if (!TryParseBody(text, SkipWhitespace(text, next + 7), out var finallyBody, out var finallyAfter)) return null;
                block.Finally = finallyBody;
                i = finallyAfter;
            }

            break;
        }

        block.ConstructEnd = i;
        return block;
    }

    /// <summary><c>@do { … } while (condition);</c>.</summary>
    private static ControlBlock? ParseDo(string text, int at, string keyword, int afterKeyword, int line)
    {
        if (!TryParseBody(text, SkipWhitespace(text, afterKeyword), out var body, out var after)) return null;

        var i = SkipWhitespace(text, after);
        if (!IsWordAt(text, i, "while")) return null;

        i = SkipWhitespace(text, i + 5);
        if (i >= text.Length || text[i] != '(') return null;

        var close = MatchParen(text, i);
        if (close < 0) return null;

        var condition = text[(i + 1)..(close - 1)].Trim();
        i = SkipWhitespace(text, close);
        if (i < text.Length && text[i] == ';') i++;

        return new ControlBlock
        {
            Keyword = keyword,
            Header = condition,
            Offset = at,
            Line = line,
            Body = body,
            ConstructEnd = i,
        };
    }

    /// <summary><c>else { … }</c> / <c>else if (c) { … }</c> read after a construct's body.</summary>
    private static ControlBlock? ParseElse(string text, int after)
    {
        var i = SkipWhitespace(text, after);
        if (!IsWordAt(text, i, "else")) return null;

        var at = i;
        i += 4;
        var keyword = "else";
        string? header = null;

        var next = SkipWhitespace(text, i);
        if (IsWordAt(text, next, "if"))
        {
            keyword = "else if";
            i = SkipWhitespace(text, next + 2);
            if (i >= text.Length || text[i] != '(') return null;

            var close = MatchParen(text, i);
            if (close < 0) return null;
            header = text[(i + 1)..(close - 1)].Trim();
            i = close;
        }
        else
        {
            i = next;
        }

        if (!TryParseBody(text, i, out var body, out var bodyAfter)) return null;

        return new ControlBlock
        {
            Keyword = keyword,
            Header = header,
            Offset = at,
            Line = LineAt(text, at),
            Body = body,
            ConstructEnd = bodyAfter,
        };
    }

    /// <summary>
    /// A braced body, or the single statement (a markup element, a nested construct or a C# statement)
    /// that stands in for one.
    /// </summary>
    private static bool TryParseBody(string text, int i, out BodyRange body, out int after)
    {
        i = SkipWhitespace(text, i);
        body = default;
        after = i;

        if (i >= text.Length) return false;

        if (text[i] == '{')
        {
            var close = MatchBrace(text, i);
            if (close < 0) return false;

            body = new BodyRange(i + 1, close - 1, close);
            after = close;
            return true;
        }

        var end = SingleStatementEnd(text, i);
        if (end <= i) return false;

        body = new BodyRange(i, end, end);
        after = end;
        return true;
    }

    /// <summary>
    /// Where a body written without braces ends: the markup element it is, the construct it starts, or
    /// the C# statement up to its semicolon.
    /// </summary>
    private static int SingleStatementEnd(string text, int i)
    {
        if (i >= text.Length) return i;

        if (text[i] == '<') return MarkupEnd(text, i);

        if (text[i] == '@')
        {
            if (i + 1 < text.Length && text[i + 1] != ':' && ControlKeywordAt(text, i))
                return ParseControl(text, i)?.End ?? EndOfLine(text, i);

            return EndOfLine(text, i);
        }

        return CSharpStatementEnd(text, i);
    }

    private static bool ControlKeywordAt(string text, int at)
    {
        var i = at + 1;
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        return i > start && ControlKeywords.Contains(text[start..i]);
    }

    private static int CSharpStatementEnd(string text, int i)
    {
        var depth = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is '"' or '\'')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                return EndOfLine(text, i);

            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ';' && depth <= 0) return i + 1;
            else if (c == '\n' && depth <= 0) return i;

            i++;
        }

        return text.Length;
    }

    /// <summary>Where the markup element that starts at <paramref name="at"/> ends, closing tag included.</summary>
    private static int MarkupEnd(string text, int at)
    {
        var name = TagNameAt(text, at + 1);
        if (name.Length == 0) return EndOfLine(text, at);

        var tagEnd = IndexAfterTagEnd(text, at + 1);
        if (tagEnd <= 0) return EndOfLine(text, at);
        if (IsSelfClosing(text, at + 1, tagEnd) || VoidElements.Contains(name)) return tagEnd;

        var depth = 1;
        var i = tagEnd;

        while (i < text.Length)
        {
            var next = text.IndexOf('<', i);
            if (next < 0) return text.Length;

            if (next + 1 < text.Length && text[next + 1] == '/')
            {
                var closing = TagNameAt(text, next + 2);
                var end = IndexAfterTagEnd(text, next + 2);
                if (end <= 0) return text.Length;

                if (closing.Equals(name, StringComparison.OrdinalIgnoreCase) && --depth == 0) return end;
                i = end;
                continue;
            }

            var opening = TagNameAt(text, next + 1);
            if (opening.Length > 0)
            {
                var end = IndexAfterTagEnd(text, next + 1);
                if (end <= 0) return text.Length;

                if (opening.Equals(name, StringComparison.OrdinalIgnoreCase) && !IsSelfClosing(text, next + 1, end))
                    depth++;

                i = end;
                continue;
            }

            i = next + 1;
        }

        return text.Length;
    }

    private static string TagNameAt(string text, int i)
    {
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_' or ':' or '.')) i++;
        return i > start ? text[start..i] : string.Empty;
    }

    /// <summary>Index just past the <c>&gt;</c> that closes the start tag beginning at <paramref name="i"/>.</summary>
    private static int IndexAfterTagEnd(string text, int i)
    {
        while (i < text.Length)
        {
            var c = text[i];

            if (c is '"' or '\'')
            {
                i = SkipQuoted(text, i);
                continue;
            }

            if (c == '@' && i + 1 < text.Length && text[i + 1] == '(')
            {
                var close = MatchParen(text, i + 1);
                if (close < 0) return -1;
                i = close;
                continue;
            }

            if (c == '>') return i + 1;
            if (c == '<') return -1;
            i++;
        }

        return -1;
    }

    private static bool IsSelfClosing(string text, int from, int tagEnd)
    {
        var i = tagEnd - 2;
        while (i > from && char.IsWhiteSpace(text[i])) i--;
        return i >= from && text[i] == '/';
    }

    private static int FindLabelColon(string text, int from, int lineEnd)
    {
        var depth = 0;

        for (var i = from; i < lineEnd; i++)
        {
            var c = text[i];

            if (c is '"' or '\'')
            {
                i = SkipQuoted(text, i) - 1;
                continue;
            }

            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ':' && depth == 0) return i;
        }

        return -1;
    }

    private static bool IsWhitespaceOnly(string text, int start, int end)
    {
        for (var i = start; i < end && i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return false;
        }

        return true;
    }

    private static (string? Type, string? Name) SplitCatchDeclaration(string? declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration)) return (null, null);

        var parts = declaration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (parts[0], null),
            _ => (string.Join(' ', parts[..^1]), parts[^1]),
        };
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

    private static int EndOfLine(string text, int from)
    {
        var end = text.IndexOf('\n', from);
        return end < 0 ? text.Length : end;
    }

    /// <summary>The offset where a code block's own text begins, i.e. just past its opening brace.</summary>
    private static int InnerStart(RazorCodeBlock block) => Math.Max(0, block.EndOffset - 1 - block.Text.Length);

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
