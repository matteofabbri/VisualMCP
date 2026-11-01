using System.Text;

namespace Snitch.Runtime;

/// <summary>One variable a <c>.http</c> file declares, with the line it is written on.</summary>
public sealed record HttpFileVariable(string Name, string Value, int Line);

/// <summary>One <c>###</c> block that carries a request line, as the file writes it (placeholders unresolved).</summary>
public sealed record HttpFileBlock(
    int Index,
    string? Name,
    string Method,
    string Url,
    int Line,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>A problem the <c>.http</c> format can be checked for without sending anything.</summary>
public sealed record HttpFileDiagnostic(string RuleId, string Severity, string Message, int Line);

/// <summary>The parsed form of a <c>.http</c> file: its variables, its requests and what is wrong with its syntax.</summary>
public sealed record HttpFileParseResult(
    IReadOnlyList<HttpFileVariable> Variables,
    IReadOnlyList<HttpFileBlock> Blocks,
    IReadOnlyList<HttpFileDiagnostic> Diagnostics);

/// <summary>One request as the inspector reports it, with the placeholders nothing declares.</summary>
public sealed record HttpFileRequestView(
    int Index,
    string? Name,
    string Method,
    string Url,
    int Line,
    int HeaderCount,
    bool HasBody,
    IReadOnlyList<string> UndefinedVariables);

/// <summary>A variable as the inspector reports it, with its declaration line.</summary>
public sealed record HttpFileVariableView(string Name, string Value, int Line);

/// <summary>
/// The answer of every <c>http_file</c> action. An inspection fills the counts, the lists and the
/// diagnostics; an edit fills <see cref="Applied"/>, <see cref="Changed"/>, <see cref="Line"/> and
/// <see cref="Preview"/>. Every member is nullable so a refusal leaves the others unset and the MCP
/// serializer drops them.
/// </summary>
public sealed record HttpFileResult
{
    /// <summary>The file the call addressed.</summary>
    public string? Path { get; init; }

    /// <summary>The file name, without its directory.</summary>
    public string? File { get; init; }

    /// <summary>The action that ran: <c>inspect</c>, <c>append</c> or <c>set-variable</c>.</summary>
    public string? Action { get; init; }

    /// <summary>How many variables the file declares.</summary>
    public int? VariableCount { get; init; }

    /// <summary>How many requests the file holds.</summary>
    public int? RequestCount { get; init; }

    /// <summary>The variables, in the order the file declares them.</summary>
    public IReadOnlyList<HttpFileVariableView>? Variables { get; init; }

    /// <summary>The requests, in file order.</summary>
    public IReadOnlyList<HttpFileRequestView>? Requests { get; init; }

    /// <summary>Every rule that fired, ordered by line.</summary>
    public IReadOnlyList<HttpFileDiagnostic>? Diagnostics { get; init; }

    /// <summary>Whether the file has no <c>error</c>-severity diagnostic.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether an edit was written (false on a dry run or a refusal).</summary>
    public bool? Applied { get; init; }

    /// <summary>Whether the file's text changed.</summary>
    public bool? Changed { get; init; }

    /// <summary>The line an edit wrote or replaced, 1-based.</summary>
    public int? Line { get; init; }

    /// <summary>The text an edit wrote (the preview when <c>dryRun</c> is true).</summary>
    public string? Preview { get; init; }

    /// <summary>A sentence the model can act on.</summary>
    public string? Note { get; init; }

    /// <summary>What went wrong with the call itself.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Read and edit a REST-client request file (<c>.http</c>/<c>.rest</c>) as data instead of as text.
/// <c>run_http_file</c> and this service read the same parser, so what the inspector validates is exactly
/// what the executor will send.
/// </summary>
public interface IHttpFileDocumentService
{
    /// <summary>Parses the file and returns its variables, requests and rule findings.</summary>
    HttpFileResult Inspect(string path);

    /// <summary>Appends a request block; the file is written unless <paramref name="dryRun"/> is true.</summary>
    Task<HttpFileResult> AppendAsync(
        string path,
        string? name,
        string method,
        string url,
        IReadOnlyDictionary<string, string>? headers,
        string? body,
        bool dryRun);

    /// <summary>Adds or updates a <c>@name = value</c> declaration at the top of the file.</summary>
    Task<HttpFileResult> SetVariableAsync(string path, string name, string value, bool dryRun);
}

public sealed class HttpFileDocumentService : IHttpFileDocumentService
{
    public HttpFileResult Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new HttpFileResult { Error = "A .http file path is required." };
        if (!File.Exists(path)) return new HttpFileResult { Path = path, Error = $"File not found: {path}" };

        var parsed = HttpFileParser.Parse(File.ReadAllText(path));
        var declared = new HashSet<string>(parsed.Variables.Select(variable => variable.Name), StringComparer.OrdinalIgnoreCase);
        var requests = parsed.Blocks
            .Select(block => new HttpFileRequestView(
                block.Index,
                block.Name,
                block.Method,
                block.Url,
                block.Line,
                block.Headers.Count,
                block.Body is not null,
                HttpFileParser.Placeholders(block)
                    .Where(name => !declared.Contains(name) && !name.StartsWith('$'))
                    .ToList()))
            .ToList();

        var ordered = parsed.Diagnostics
            .Select((diagnostic, index) => (diagnostic, index))
            .OrderBy(entry => entry.diagnostic.Line)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.diagnostic)
            .ToList();

        return new HttpFileResult
        {
            Path = path,
            File = Path.GetFileName(path),
            Action = "inspect",
            VariableCount = parsed.Variables.Count,
            RequestCount = parsed.Blocks.Count,
            Variables = parsed.Variables.Select(v => new HttpFileVariableView(v.Name, v.Value, v.Line)).ToList(),
            Requests = requests,
            Diagnostics = ordered,
            Succeeded = ordered.All(diagnostic => !string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal)),
            Note = ordered.Count == 0
                ? "No rule fired: every block names a method and a URL, and every placeholder is declared."
                : $"{ordered.Count} finding(s); the 'error' ones are what run_http_file would fail on.",
        };
    }

    public async Task<HttpFileResult> AppendAsync(
        string path,
        string? name,
        string method,
        string url,
        IReadOnlyDictionary<string, string>? headers,
        string? body,
        bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(path)) return new HttpFileResult { Error = "A .http file path is required." };
        if (!File.Exists(path)) return new HttpFileResult { Path = path, Error = $"File not found: {path}" };

        var verb = (method ?? string.Empty).Trim().ToUpperInvariant();
        if (verb.Length == 0) return new HttpFileResult { Path = path, Action = "append", Error = "A method is required." };
        if (!HttpFileParser.KnownMethods.Contains(verb))
        {
            return new HttpFileResult
            {
                Path = path,
                Action = "append",
                Error = $"'{verb}' is not one of {string.Join(", ", HttpFileParser.KnownMethods.OrderBy(m => m))}.",
            };
        }

        if (string.IsNullOrWhiteSpace(url))
            return new HttpFileResult { Path = path, Action = "append", Error = "A URL is required." };

        var text = await File.ReadAllTextAsync(path);
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var newline = crlf ? "\r\n" : "\n";

        var block = new StringBuilder();
        block.Append(newline).Append("### ").Append(string.IsNullOrWhiteSpace(name) ? $"{verb} {url}" : name!.Trim()).Append(newline);
        if (!string.IsNullOrWhiteSpace(name))
            block.Append("# @name ").Append(name!.Trim()).Append(newline);
        block.Append(verb).Append(' ').Append(url.Trim()).Append(newline);
        if (headers is not null)
        {
            foreach (var (header, value) in headers)
                block.Append(header).Append(": ").Append(value).Append(newline);
        }

        if (!string.IsNullOrWhiteSpace(body))
            block.Append(newline).Append(body!.ReplaceLineEndings(newline).TrimEnd('\r', '\n')).Append(newline);

        var trimmed = text.TrimEnd('\r', '\n');
        var updated = trimmed.Length == 0 ? block.ToString().TrimStart('\r', '\n') : trimmed + block;
        var line = trimmed.Length == 0 ? 1 : CountLines(trimmed) + 2;

        if (!dryRun) await WriteAsync(path, updated);

        return new HttpFileResult
        {
            Path = path,
            File = Path.GetFileName(path),
            Action = "append",
            Applied = !dryRun,
            Changed = !string.Equals(updated, text, StringComparison.Ordinal),
            Line = line,
            Preview = block.ToString().Trim('\r', '\n'),
            Note = dryRun ? "Preview only; pass dryRun=false to append the block." : "The block was appended.",
        };
    }

    public async Task<HttpFileResult> SetVariableAsync(string path, string name, string value, bool dryRun)
    {
        if (string.IsNullOrWhiteSpace(path)) return new HttpFileResult { Error = "A .http file path is required." };
        if (!File.Exists(path)) return new HttpFileResult { Path = path, Error = $"File not found: {path}" };

        var variable = (name ?? string.Empty).Trim().TrimStart('@');
        if (variable.Length == 0)
            return new HttpFileResult { Path = path, Action = "set-variable", Error = "A variable name is required." };
        if (variable.Any(char.IsWhiteSpace))
        {
            return new HttpFileResult
            {
                Path = path,
                Action = "set-variable",
                Error = $"'{variable}' is not a variable name: it contains whitespace.",
            };
        }

        var text = await File.ReadAllTextAsync(path);
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var newline = crlf ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var declaration = $"@{variable} = {value}";

        var existing = -1;
        var lastVariable = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith('@')) continue;
            var equals = trimmed.IndexOf('=');
            if (equals <= 1) continue;
            lastVariable = index;
            if (string.Equals(trimmed[1..equals].Trim(), variable, StringComparison.OrdinalIgnoreCase))
            {
                existing = index;
                break;
            }
        }

        int line;
        if (existing >= 0)
        {
            lines[existing] = declaration;
            line = existing + 1;
        }
        else
        {
            var at = lastVariable >= 0 ? lastVariable + 1 : 0;
            lines.Insert(at, declaration);
            line = at + 1;
        }

        var updated = string.Join(newline, lines);
        if (!dryRun) await WriteAsync(path, updated);

        return new HttpFileResult
        {
            Path = path,
            File = Path.GetFileName(path),
            Action = "set-variable",
            Applied = !dryRun,
            Changed = !string.Equals(updated, text, StringComparison.Ordinal),
            Line = line,
            Preview = declaration,
            Note = existing >= 0
                ? (dryRun ? "Preview: the existing declaration is replaced." : "The existing declaration was replaced.")
                : (dryRun ? "Preview: the declaration is inserted with the other variables." : "The declaration was inserted."),
        };
    }

    private static int CountLines(string text) => text.Count(character => character == '\n') + 1;

    private static Task WriteAsync(string path, string text) =>
        File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

/// <summary>
/// The <c>.http</c>/<c>.rest</c> format as one parser: <c>@name = value</c> variables, <c>###</c> separators,
/// an optional <c># @name</c> per request, a <c>METHOD url</c> request line, headers and a body.
/// </summary>
public static class HttpFileParser
{
    /// <summary>The methods a request line may name.</summary>
    public static readonly HashSet<string> KnownMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE",
    };

    /// <summary>Parses the file into variables, request blocks and rule findings.</summary>
    public static HttpFileParseResult Parse(string text)
    {
        var variables = new List<HttpFileVariable>();
        var blocks = new List<HttpFileBlock>();
        var diagnostics = new List<HttpFileDiagnostic>();
        var requestNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var declared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mentions = new List<(string Text, int Line)>();

        string? pendingName = null;
        var pendingNameLine = 0;
        string? method = null;
        string? url = null;
        var requestLine = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = new StringBuilder();
        var inBody = false;
        var blockMentions = new List<(string Text, int Line)>();
        var unseen = new List<(string Text, int Line)>();

        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        void Flush()
        {
            if (method is not null && url is not null)
            {
                if (!KnownMethods.Contains(method))
                    diagnostics.Add(new HttpFileDiagnostic("HTTP0002", "warning", $"'{method}' is not a known HTTP method.", requestLine));

                if (pendingName is not null)
                {
                    if (requestNames.TryGetValue(pendingName, out var firstLine))
                    {
                        diagnostics.Add(new HttpFileDiagnostic(
                            "HTTP0005",
                            "warning",
                            $"Two requests are named '{pendingName}' (the first on line {firstLine}).",
                            pendingNameLine));
                    }
                    else
                    {
                        requestNames[pendingName] = pendingNameLine;
                    }
                }

                mentions.AddRange(blockMentions);
                blocks.Add(new HttpFileBlock(
                    blocks.Count + 1,
                    pendingName,
                    method,
                    url,
                    requestLine,
                    new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                    body.Length > 0 ? body.ToString().TrimEnd('\r', '\n') : null));
            }
            else
            {
                // Content nothing could read as a request line: the file's most common mistake.
                foreach (var (lineText, lineNumber) in unseen)
                {
                    diagnostics.Add(new HttpFileDiagnostic(
                        "HTTP0001",
                        "error",
                        $"'{lineText}' is not a request line; a block starts with 'METHOD url'.",
                        lineNumber));
                }
            }

            pendingName = null;
            pendingNameLine = 0;
            method = null;
            url = null;
            requestLine = 0;
            headers.Clear();
            body.Clear();
            inBody = false;
            blockMentions.Clear();
            unseen.Clear();
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            var lineNumber = index + 1;

            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (inBody)
            {
                body.Append(line).Append('\n');
                if (trimmed.Length > 0) blockMentions.Add((line, lineNumber));
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (url is not null) inBody = true;
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var directive = trimmed.TrimStart('#').Trim();
                if (directive.StartsWith("@name", StringComparison.OrdinalIgnoreCase))
                {
                    pendingName = directive[5..].Trim();
                    pendingNameLine = lineNumber;
                }
                else if (directive.StartsWith("name", StringComparison.OrdinalIgnoreCase) && method is null)
                {
                    pendingName = directive[4..].Trim();
                    pendingNameLine = lineNumber;
                }

                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (url is null && trimmed.StartsWith('@'))
            {
                var equals = trimmed.IndexOf('=');
                if (equals > 1)
                {
                    var name = trimmed[1..equals].Trim();
                    var value = trimmed[(equals + 1)..].Trim();
                    if (value.Length > 0)
                    {
                        if (declared.TryGetValue(name, out var firstLine))
                        {
                            diagnostics.Add(new HttpFileDiagnostic(
                                "HTTP0004",
                                "warning",
                                $"'{name}' is declared twice (the first declaration is on line {firstLine}).",
                                lineNumber));
                        }
                        else
                        {
                            declared[name] = lineNumber;
                        }

                        variables.Add(new HttpFileVariable(name, value, lineNumber));
                        // A variable may itself be built from another one.
                        mentions.Add((value, lineNumber));
                    }

                    continue;
                }
            }

            if (url is null)
            {
                var (parsedMethod, parsedUrl) = ParseRequestLine(trimmed);
                if (parsedUrl is null)
                {
                    unseen.Add((trimmed, lineNumber));
                    continue;
                }

                method = parsedMethod;
                url = parsedUrl;
                requestLine = lineNumber;
                blockMentions.Add((url, lineNumber));
                unseen.Clear();
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon > 0)
            {
                var name = trimmed[..colon].Trim();
                var value = trimmed[(colon + 1)..].Trim();
                headers[name] = value;
                blockMentions.Add((value, lineNumber));
            }
            else
            {
                diagnostics.Add(new HttpFileDiagnostic(
                    "HTTP0006",
                    "warning",
                    $"'{trimmed}' is neither a header ('Name: value') nor body: a blank line before the body is missing.",
                    lineNumber));
            }
        }

        Flush();

        if (blocks.Count == 0 && diagnostics.All(diagnostic => diagnostic.RuleId != "HTTP0001"))
            diagnostics.Add(new HttpFileDiagnostic("HTTP0007", "error", "The file declares no request.", 1));

        foreach (var (mention, lineNumber) in mentions)
        {
            foreach (var name in Placeholders(mention))
            {
                if (declared.ContainsKey(name) || name.StartsWith('$')) continue;
                diagnostics.Add(new HttpFileDiagnostic(
                    "HTTP0003",
                    "error",
                    $"'{name}' is used but no '@{name} = value' declares it.",
                    lineNumber));
            }
        }

        var deduped = diagnostics
            .GroupBy(diagnostic => (diagnostic.RuleId, diagnostic.Line, diagnostic.Message))
            .Select(group => group.First())
            .OrderBy(diagnostic => diagnostic.Line)
            .ToList();

        return new HttpFileParseResult(variables, blocks, deduped);
    }

    /// <summary>Every <c>{{name}}</c> a request block mentions, in the order it appears, deduplicated.</summary>
    public static IReadOnlyList<string> Placeholders(HttpFileBlock block)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string value)
        {
            foreach (var name in Placeholders(value))
            {
                if (seen.Add(name)) names.Add(name);
            }
        }

        Add(block.Url);
        foreach (var (_, value) in block.Headers) Add(value);
        if (block.Body is not null) Add(block.Body);
        return names;
    }

    private static IEnumerable<string> Placeholders(string value)
    {
        if (!value.Contains("{{", StringComparison.Ordinal)) yield break;

        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("{{", index, StringComparison.Ordinal);
            if (start < 0) yield break;
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0) yield break;
            var name = value[(start + 2)..end].Trim();
            if (name.Length > 0) yield return name;
            index = end + 2;
        }
    }

    private static (string Method, string? Url) ParseRequestLine(string line)
    {
        var space = line.IndexOf(' ');
        if (space <= 0) return (string.Empty, null);

        var first = line[..space].ToUpperInvariant();
        if (KnownMethods.Contains(first))
            return (first, line[(space + 1)..].Trim());

        // "GET http://…" only: a bare URL has no space, so this is the fallback for "GET  http://…".
        return first.All(char.IsLetter) ? (first, line[(space + 1)..].Trim()) : (string.Empty, null);
    }
}
