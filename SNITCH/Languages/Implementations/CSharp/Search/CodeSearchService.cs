using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Snitch.Workspace;

namespace Snitch.Languages.CSharp.Search;

/// <summary>
/// The "find it, then show it" merge: one call answers with the code that a text search would have
/// forced the caller to read afterwards.
/// </summary>
/// <remarks>
/// The measured sequence this replaces is <c>search_text → read_file</c> (106 adjacent pairs in a day of
/// tool calls) and the <c>read_file → read_file</c> runs that follow it. Instead of returning matching
/// lines and leaving the body a second round-trip away, every match is resolved to the declaration that
/// contains it — through Roslyn when the file belongs to the loaded solution, through a line window when
/// it does not — so the answer is the method, not the line.
/// </remarks>
public interface ICodeSearchService
{
    /// <summary>
    /// Searches <paramref name="path"/> and returns the enclosing declaration of every match, inside a
    /// total character budget. Never throws: an invalid pattern or a missing path comes back in
    /// <see cref="CodeSearchResult.Error"/>.
    /// </summary>
    Task<CodeSearchResult> RunAsync(
        string pattern,
        string path,
        string glob,
        bool ignoreCase,
        int maxMatches,
        int maxChars,
        int contextLines,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One match resolved to code. <see cref="Kind"/> is null for a file that is not part of the solution:
/// the block is then a plain window of lines around the match, which is all a log or a config can give.
/// </summary>
public sealed record CodeSearchHit(
    string File,
    string? Kind,
    string? Container,
    string? Signature,
    int Line,
    int EndLine,
    IReadOnlyList<int> Matches,
    string Source);

/// <summary>What a code search produced. <see cref="LeftOut"/> names what the budget pushed out.</summary>
public sealed record CodeSearchResult(
    string Pattern,
    string Root,
    string Glob,
    int FilesScanned,
    int FilesWithMatches,
    int MatchCount,
    int DeclarationCount,
    bool Truncated,
    IReadOnlyList<CodeSearchHit> Hits,
    IReadOnlyList<string> LeftOut,
    int Characters,
    string? Error = null)
{
    public static CodeSearchResult Failed(string pattern, string root, string error) =>
        new(pattern, root, "*", 0, 0, 0, 0, false, Array.Empty<CodeSearchHit>(), Array.Empty<string>(), 0, error);
}

/// <summary>
/// The implementation. The file walk mirrors <c>SearchTextService</c> (same noise folders, same size
/// cap), because a hit list is only useful if it covers the same files a plain search would.
/// </summary>
public sealed class CodeSearchService : ICodeSearchService
{
    private static readonly string[] NoiseSegments =
    [
        $"{System.IO.Path.DirectorySeparatorChar}.git{System.IO.Path.DirectorySeparatorChar}",
        $"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}",
        $"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}",
        $"{System.IO.Path.DirectorySeparatorChar}node_modules{System.IO.Path.DirectorySeparatorChar}",
        $"{System.IO.Path.DirectorySeparatorChar}.vs{System.IO.Path.DirectorySeparatorChar}",
    ];

    private const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>How many declarations one file may contribute before the rest of its matches are collapsed.</summary>
    private const int MaxHitsPerFile = 20;

    private readonly IRoslynWorkspace _workspace;

    public CodeSearchService(IRoslynWorkspace workspace) => _workspace = workspace;

    public async Task<CodeSearchResult> RunAsync(
        string pattern,
        string path,
        string glob,
        bool ignoreCase,
        int maxMatches,
        int maxChars,
        int contextLines,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return CodeSearchResult.Failed(pattern, path, "A search pattern (regex) is required.");
        if (string.IsNullOrWhiteSpace(path))
            return CodeSearchResult.Failed(pattern, path, "A path (file or directory) is required.");

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        catch (Exception exception)
        {
            return CodeSearchResult.Failed(pattern, path, $"Invalid regex '{pattern}': {exception.Message}");
        }

        maxMatches = Math.Clamp(maxMatches, 1, 2000);
        contextLines = Math.Clamp(contextLines, 0, 20);
        var budget = Math.Clamp(maxChars, 2_000, 180_000);

        List<string> files;
        try
        {
            files = Enumerate(path, string.IsNullOrWhiteSpace(glob) ? "*" : glob);
        }
        catch (Exception exception)
        {
            return CodeSearchResult.Failed(pattern, path, $"Failed to enumerate '{path}': {exception.Message}");
        }

        var solution = await LoadSolutionAsync(cancellationToken).ConfigureAwait(false);
        var documents = IndexDocuments(solution);

        var hits = new List<CodeSearchHit>();
        var leftOut = new List<string>();
        var filesScanned = 0;
        var filesWithMatches = 0;
        var matchCount = 0;
        var truncated = false;
        var characters = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchCount >= maxMatches)
            {
                truncated = true;
                break;
            }

            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            filesScanned++;

            var matchedLines = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                matchedLines.Add(i + 1);
                if (++matchCount >= maxMatches)
                {
                    truncated = true;
                    break;
                }
            }

            if (matchedLines.Count == 0) continue;
            filesWithMatches++;

            var fileHits = documents.TryGetValue(FullPath(file), out var document)
                ? await SolutionHitsAsync(document, matchedLines, cancellationToken).ConfigureAwait(false)
                : WindowHits(file, lines, matchedLines, contextLines);

            foreach (var hit in fileHits)
            {
                if (hits.Count(h => h.File.Equals(hit.File, StringComparison.OrdinalIgnoreCase)) >= MaxHitsPerFile)
                {
                    leftOut.Add($"{file}:{hit.Line} (too many declarations in one file — narrow the pattern or pass the file path)");
                    continue;
                }

                var remaining = budget - characters;
                if (remaining <= 200)
                {
                    leftOut.Add($"{file}:{hit.Line} {hit.Kind ?? "lines"} {hit.Signature ?? string.Empty}".TrimEnd());
                    continue;
                }

                var source = hit.Source.Length <= remaining
                    ? hit.Source
                    : hit.Source[..Math.Max(0, remaining)] + "\n… truncated by the output budget";

                hits.Add(hit with { Source = source });
                characters += source.Length;
            }
        }

        return new CodeSearchResult(
            pattern, path, string.IsNullOrWhiteSpace(glob) ? "*" : glob,
            filesScanned, filesWithMatches, matchCount, hits.Count, truncated, hits, leftOut, characters);
    }

    private async Task<Solution?> LoadSolutionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _workspace.EnsureSolutionLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No solution is not a failure for this tool: the files are still searched from disk.
            return null;
        }
    }

    private static List<string> Enumerate(string path, string glob)
    {
        if (File.Exists(path)) return [System.IO.Path.GetFullPath(path)];
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Path not found: {path}");

        return Directory
            .EnumerateFiles(path, glob, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
            })
            .Where(file => !NoiseSegments.Any(segment => file.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static Dictionary<string, Document> IndexDocuments(Solution? solution)
    {
        var index = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        if (solution is null) return index;

        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            if (document.FilePath is { } path)
                index[FullPath(path)] = document;
        }

        return index;
    }

    private static string FullPath(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }

    /// <summary>
    /// Every match line mapped to the declaration that contains it, de-duplicated so a method that
    /// matches five times is printed once with its five line numbers.
    /// </summary>
    private static async Task<List<CodeSearchHit>> SolutionHitsAsync(
        Document document, List<int> matchedLines, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (root is null) return [];

        var order = new List<MemberDeclarationSyntax>();
        var linesBySpan = new Dictionary<int, List<int>>();

        foreach (var line in matchedLines)
        {
            var declaration = DeclarationAt(root, text.Lines, line);
            if (declaration is null) continue;

            if (!linesBySpan.TryGetValue(declaration.SpanStart, out var lines))
            {
                lines = [];
                linesBySpan[declaration.SpanStart] = lines;
                order.Add(declaration);
            }

            lines.Add(line);
        }

        var hits = new List<CodeSearchHit>(order.Count);
        foreach (var declaration in order)
        {
            var span = declaration.GetLocation().GetLineSpan();
            var start = IncludeDocComments(text.Lines, span.StartLinePosition.Line);
            var end = Math.Min(span.EndLinePosition.Line, text.Lines.Count - 1);
            if (end < start) continue;

            var source = string.Join("\n",
                text.Lines.Skip(start).Take(end - start + 1).Select(line => line.ToString()));

            hits.Add(new CodeSearchHit(
                document.FilePath ?? document.Name,
                KindOf(declaration),
                ContainerOf(declaration),
                SignatureOf(text, declaration, span.StartLinePosition.Line),
                start + 1,
                end + 1,
                linesBySpan[declaration.SpanStart],
                source));
        }

        return hits;
    }

    private static List<CodeSearchHit> WindowHits(string file, string[] lines, List<int> matchedLines, int contextLines)
    {
        var hits = new List<CodeSearchHit>(matchedLines.Count);
        var used = new HashSet<int>();

        foreach (var line in matchedLines)
        {
            var from = Math.Max(0, line - 1 - contextLines);
            var to = Math.Min(lines.Length - 1, line - 1 + contextLines);
            if (!used.Add(from)) continue;

            var source = string.Join("\n", lines.Skip(from).Take(to - from + 1));
            hits.Add(new CodeSearchHit(file, null, null, null, from + 1, to + 1, [line], source));
        }

        return hits;
    }

    private static MemberDeclarationSyntax? DeclarationAt(SyntaxNode root, TextLineCollection lines, int line)
    {
        if (line < 1 || line > lines.Count) return null;

        var start = lines[line - 1].Start;
        var token = root.FindToken(Math.Min(start, root.FullSpan.End));
        return token.Parent?
            .AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration is not BaseNamespaceDeclarationSyntax);
    }

    /// <summary>Starts the block at the <c>///</c> lines immediately above the declaration, when there are any.</summary>
    private static int IncludeDocComments(TextLineCollection lines, int start)
    {
        var current = start - 1;
        while (current >= 0)
        {
            var text = lines[current].ToString().TrimStart();
            if (!text.StartsWith("///", StringComparison.Ordinal)) break;
            start = current;
            current--;
        }

        return start;
    }

    private static string SignatureOf(SourceText text, MemberDeclarationSyntax declaration, int startLine)
    {
        var line = text.Lines[startLine].ToString().Trim();
        if (line.Length == 0) line = declaration.Kind().ToString();

        // Keep the header only: a one-line method body would otherwise be the whole signature.
        var brace = line.IndexOf('{');
        if (brace > 0) line = line[..brace].TrimEnd();

        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private static string? ContainerOf(MemberDeclarationSyntax declaration)
    {
        var names = declaration.Ancestors()
            .Where(node => node is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax)
            .Reverse()
            .Select(node => node switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.Text,
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                _ => string.Empty,
            })
            .Where(name => name.Length > 0)
            .ToList();

        return names.Count == 0 ? null : string.Join(".", names);
    }

    private static string KindOf(MemberDeclarationSyntax declaration) => declaration switch
    {
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        FieldDeclarationSyntax => "field",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        EnumMemberDeclarationSyntax => "enum-member",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        RecordDeclarationSyntax => "record",
        EnumDeclarationSyntax => "enum",
        DelegateDeclarationSyntax => "delegate",
        OperatorDeclarationSyntax => "operator",
        ConversionOperatorDeclarationSyntax => "operator",
        _ => "declaration",
    };
}
