using System.Text.RegularExpressions;

namespace Snitch.Languages.FSharp;

/// <summary>What kind of declaration the F# scan can prove.</summary>
public enum FSharpSymbolKind
{
    Module,
    Type,
    Function,
    Value,
    Member,
    UnionCase,
    Field,
}

/// <summary>One declaration, with the module or type that contains it.</summary>
public sealed record FSharpSymbolDeclaration(
    string Name,
    FSharpSymbolKind Kind,
    int Line,
    int Column,
    int Offset,
    string Container,
    bool Public);

/// <summary>One occurrence of a name. <see cref="Declaration"/> is the index of what it resolves to, or -1.</summary>
public sealed record FSharpSymbolUse(string Name, int Line, int Column, int Offset, string Kind, int Declaration);

/// <summary>The symbol facts a scan of one F# file produces.</summary>
public sealed record FSharpFileSymbols(
    string Path,
    IReadOnlyList<FSharpSymbolDeclaration> Declarations,
    IReadOnlyList<FSharpSymbolUse> Uses,
    IReadOnlyList<string> Opens,
    IReadOnlyList<string> Modules,
    int Lines);

/// <summary>
/// A lexical symbol table for F#. F# has no Roslyn model, so this is a name table over the masked source:
/// the declarations an indentation-aware line scan can prove (modules, namespaces, types, union cases,
/// record fields, <c>let</c> bindings and members) and every identifier occurrence with its position. A
/// name resolves to the declaration of the same name in its own file, nearest first; across files it
/// resolves only through an <c>open</c> the file declares, because that is the one link a token can prove.
/// <para>
/// It is not a compiler. Shadowing inside a match arm, a name reached through a module alias, a type
/// parameter, a computation-expression builder and anything the compiled assembly exports are outside what
/// the scan can see, and the answers say so. The declarations and their positions are exact.
/// </para>
/// </summary>
public static class FSharpSymbols
{
    private static readonly RegexOptions Options = RegexOptions.Compiled;

    private static readonly Regex ModuleDeclaration = new(@"^(?:namespace|module)\s+(?<name>[\w.]+)", Options);
    private static readonly Regex TypeDeclaration = new(@"^type\s+(?:private\s+|internal\s+|public\s+)*(?<name>[A-Za-z_][\w']*)", Options);
    private static readonly Regex AndType = new(@"^and\s+(?<name>[A-Za-z_][\w']*)", Options);
    private static readonly Regex LetBinding = new(
        @"^(?:let|and)\s+(?:rec\s+|inline\s+|private\s+|internal\s+|public\s+|mutable\s+|mutable\s+)*(?<name>[A-Za-z_][\w']*)\s*(?:<[^>]*>)?\s*(?:\(|:|=|[\w']|$)",
        Options);
    private static readonly Regex MemberDeclaration = new(
        @"^(?:static\s+|abstract\s+|override\s+|default\s+|member\s+|abstract\s+member\s+|static\s+member\s+|override\s+member\s+)*(?:this\.|x\.|_\.|self\.)?(?<name>[A-Za-z_][\w']*)\s*(?:\(|:|=|<)",
        Options);
    private static readonly Regex MemberKeyword = new(
        @"^(?:member|static\s+member|abstract\s+member|override\s+member|abstract|override|default)\b", Options);
    private static readonly Regex UnionBar = new(@"^\|", Options);
    private static readonly Regex RecordField = new(@"(?<name>[A-Z][\w']*)\s*:\s*[A-Za-z_(]", Options);
    private static readonly Regex OpenDeclaration = new(@"^open\s+(?:type\s+)?(?<name>[\w.]+)", Options);
    private static readonly Regex Identifier = new(@"[A-Za-z_][\w']*", Options);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "and", "as", "assert", "base", "begin", "class", "default", "delegate", "do", "done",
        "downcast", "downto", "elif", "else", "end", "exception", "extern", "false", "finally", "fixed",
        "for", "fun", "function", "global", "if", "in", "inherit", "inline", "interface", "internal",
        "lazy", "let", "match", "member", "module", "mutable", "namespace", "new", "not", "null", "of",
        "open", "or", "override", "private", "public", "rec", "return", "select", "static", "struct",
        "then", "to", "true", "try", "type", "upcast", "use", "val", "void", "when", "while", "with",
        "yield", "async", "task", "seq", "printf", "printfn", "failwith", "raise", "nameof", "typeof",
        "typedefof", "sizeof", "box", "unbox", "ignore", "int", "string", "float", "bool", "decimal",
        "unit", "obj", "exn", "byte", "char", "list", "array", "option", "result", "ref",
    };

    /// <summary>Reads one file. The extension decides nothing here: F# syntax is F# in every file.</summary>
    public static FSharpFileSymbols Scan(string text, string filePath)
    {
        text ??= string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var masked = FSharpAnalyzer.MaskedLines(text);

        var declarations = new List<Declaration>();
        var uses = new List<(string Name, int Line, int Column, int Offset, bool Member)>();
        var opens = new List<string>();
        var modules = new List<string>();
        var declared = new HashSet<(int Line, int Column)>();

        var currentModule = "file";
        var currentType = (Name: (string?)null, Indent: -1);

        for (var index = 0; index < lines.Length && index < masked.Length; index++)
        {
            var maskedLine = masked[index];
            var trimmed = maskedLine.TrimStart();
            if (trimmed.Length == 0) continue;

            var indentation = maskedLine.Length - trimmed.Length;
            var lineNumber = index + 1;

            var moduleMatch = ModuleDeclaration.Match(trimmed);
            if (moduleMatch.Success)
            {
                var name = moduleMatch.Groups["name"].Value;
                modules.Add(name);
                currentModule = name;
                currentType = (null, -1);
                Declare(name, FSharpSymbolKind.Module, lineNumber,
                    indentation + moduleMatch.Groups["name"].Index + 1, index * 10_000 + indentation,
                    name.Contains('.') ? name[..name.LastIndexOf('.')] : "file",
                    isPublic: !trimmed.Contains("private", StringComparison.Ordinal));
                continue;
            }

            var openMatch = OpenDeclaration.Match(trimmed);
            if (openMatch.Success)
            {
                var name = openMatch.Groups["name"].Value;
                if (!opens.Contains(name)) opens.Add(name);
                continue;
            }

            if (currentType.Name is not null && indentation <= currentType.Indent && !UnionBar.IsMatch(trimmed))
                currentType = (null, -1);

            var typeMatch = TypeDeclaration.Match(trimmed);
            if (typeMatch.Success)
            {
                var name = typeMatch.Groups["name"].Value;
                Declare(name, FSharpSymbolKind.Type, lineNumber,
                    indentation + typeMatch.Groups["name"].Index + 1, index * 10_000 + indentation,
                    currentModule, isPublic: !trimmed.Contains("private", StringComparison.Ordinal));
                currentType = (name, indentation);
            }
            else
            {
                var andType = AndType.Match(trimmed);
                if (andType.Success && currentType.Name is not null)
                {
                    Declare(andType.Groups["name"].Value, FSharpSymbolKind.Type, lineNumber,
                        indentation + andType.Groups["name"].Index + 1, index * 10_000 + indentation,
                        currentModule, isPublic: true);
                }
            }

            // A union case is the name after `=` on the type line and after every `|` in the block; a
            // record field is an uppercase name followed by `:`.
            if (currentType.Name is not null)
            {
                var caseSource = typeMatch.Success
                    ? trimmed[(trimmed.IndexOf('=') + 1)..]
                    : UnionBar.IsMatch(trimmed) ? trimmed[1..] : null;
                var caseColumn = typeMatch.Success
                    ? indentation + trimmed.IndexOf('=') + 2
                    : indentation + 2;

                if (caseSource is not null)
                {
                    foreach (var segment in caseSource.Split('|'))
                    {
                        var caseName = Identifier.Match(segment);
                        if (!caseName.Success || !char.IsUpper(caseName.Value[0])) continue;
                        Declare(caseName.Value, FSharpSymbolKind.UnionCase, lineNumber,
                            caseColumn + caseName.Index, index * 10_000 + indentation,
                            currentType.Name, isPublic: true);
                    }
                }
                else if (!MemberKeyword.IsMatch(trimmed) && !LetBinding.IsMatch(trimmed))
                {
                    foreach (Match field in RecordField.Matches(trimmed))
                        Declare(field.Groups["name"].Value, FSharpSymbolKind.Field, lineNumber,
                            indentation + field.Groups["name"].Index + 1, index * 10_000 + indentation,
                            currentType.Name, isPublic: true);
                }
            }

            if (MemberKeyword.IsMatch(trimmed))
            {
                var member = MemberDeclaration.Match(trimmed);
                if (member.Success && member.Groups["name"].Value != "member")
                    Declare(member.Groups["name"].Value, FSharpSymbolKind.Member, lineNumber,
                        indentation + member.Groups["name"].Index + 1, index * 10_000 + indentation,
                        currentType.Name ?? currentModule, isPublic: true);
            }

            var binding = LetBinding.Match(trimmed);
            if (binding.Success)
            {
                var name = binding.Groups["name"].Value;
                var kind = char.IsUpper(name[0]) && indentation == 0 ? FSharpSymbolKind.Value : FSharpSymbolKind.Function;
                Declare(name, kind, lineNumber, indentation + binding.Groups["name"].Index + 1,
                    index * 10_000 + indentation, currentModule,
                    isPublic: !trimmed.Contains("private", StringComparison.Ordinal));
            }

            // Every identifier on the line that is not a declaration position is an occurrence.
            foreach (Match identifier in Identifier.Matches(maskedLine))
            {
                var name = identifier.Value;
                if (Keywords.Contains(name)) continue;
                if (declared.Contains((lineNumber, identifier.Index + 1))) continue;

                var previousIndex = identifier.Index - 1;
                while (previousIndex >= 0 && maskedLine[previousIndex] == ' ') previousIndex--;
                var isMember = previousIndex >= 0 && maskedLine[previousIndex] == '.';
                uses.Add((name, lineNumber, identifier.Index + 1, index * 10_000 + identifier.Index, isMember));
            }
        }

        // ── resolution: the nearest declaration before the use in the same file ───────
        var useList = new List<FSharpUseBuilder>(uses.Count);
        foreach (var (name, line, column, offset, member) in uses)
        {
            var resolved = -1;
            var bestOffset = -1;
            for (var position = 0; position < declarations.Count; position++)
            {
                if (!string.Equals(declarations[position].Name, name, StringComparison.Ordinal)) continue;
                if (declarations[position].Offset > offset) continue;
                if (declarations[position].Offset <= bestOffset) continue;
                resolved = position;
                bestOffset = declarations[position].Offset;
            }

            if (resolved < 0)
                resolved = declarations.FindIndex(declaration => string.Equals(declaration.Name, name, StringComparison.Ordinal));

            useList.Add(new FSharpUseBuilder(name, line, column, offset, member ? "member" : "read", resolved));
        }

        return new FSharpFileSymbols(
            filePath,
            declarations.Select(declaration => declaration.ToSymbol()).ToList(),
            useList.Select(use => use.ToUse()).ToList(),
            opens,
            modules,
            lines.Length);

        void Declare(string name, FSharpSymbolKind kind, int line, int column, int offset, string container, bool isPublic)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            declared.Add((line, column));
            declarations.Add(new Declaration(name, kind, line, column, offset, container, isPublic));
        }
    }

    private sealed record Declaration(
        string Name, FSharpSymbolKind Kind, int Line, int Column, int Offset, string Container, bool Public)
    {
        public FSharpSymbolDeclaration ToSymbol() => new(Name, Kind, Line, Column, Offset, Container, Public);
    }

    private sealed record FSharpUseBuilder(string Name, int Line, int Column, int Offset, string Kind, int Declaration)
    {
        public FSharpSymbolUse ToUse() => new(Name, Line, Column, Offset, Kind, Declaration);
    }
}
