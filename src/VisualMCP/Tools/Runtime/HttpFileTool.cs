using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Runtime;

namespace VisualMCP.Tools.Runtime;

[McpServerToolType]
public static class HttpFileTool
{
    [McpServerTool(Name = "http_file"), Description(
        "Read and edit a REST-client request file (.http/.rest, the Rider and VS Code format) as data " +
        "instead of text. action=inspect parses the file and reports its variables (with their lines), its " +
        "requests and the rules it breaks: HTTP0001 a block with no 'METHOD url' line, HTTP0002 an unknown " +
        "method, HTTP0003 a '{{placeholder}}' nothing declares, HTTP0004 a variable declared twice, " +
        "HTTP0005 two requests with the same name, HTTP0006 a line that is neither a header nor body (the " +
        "blank line before it is missing), HTTP0007 no request at all — and the parser is the same one " +
        "run_http_file executes, so what it validates is what will be sent. action=append adds a " +
        "request block; action=set-variable upserts '@name = value' with the other variables. Both edits " +
        "keep the file's line endings and are previewed unless dryRun=false.")]
    public static Task<HttpFileResult> HttpFile(
        IHttpFileDocumentService httpFile,
        [Description("inspect (parse and validate), append (add a request) or set-variable (upsert @name = value).")]
        string action,
        [Description("Path of the .http/.rest file (absolute, or relative to the solution).")] string path,
        [Description("For set-variable: the variable name (leading '@' optional). For append: the request's '# @name'.")]
        string? name = null,
        [Description("For set-variable: the value, written verbatim after '@name = '.")] string? value = null,
        [Description("For append: the HTTP method (GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS, TRACE).")]
        string? method = null,
        [Description("For append: the request URL, placeholders included (e.g. '{{baseUrl}}/orders').")]
        string? url = null,
        [Description("For append: the headers, one 'Name: value' per line.")] string? headers = null,
        [Description("For append: the request body, written after a blank line.")] string? body = null,
        [Description("Preview an edit without writing the file (default true; ignored by inspect).")]
        bool dryRun = true)
    {
        if (string.IsNullOrWhiteSpace(action))
            return Task.FromResult(new HttpFileResult { Error = "An action is required: inspect, append or set-variable." });

        return action.Trim().ToLowerInvariant() switch
        {
            "inspect" => Task.FromResult(httpFile.Inspect(path)),
            "append" => httpFile.AppendAsync(path, name, method ?? string.Empty, url ?? string.Empty, ParseHeaders(headers), body, dryRun),
            "set-variable" => httpFile.SetVariableAsync(path, name ?? string.Empty, value ?? string.Empty, dryRun),
            _ => Task.FromResult(new HttpFileResult
            {
                Error = $"Unknown action '{action}': use inspect, append or set-variable.",
            }),
        };
    }

    /// <summary>One <c>Name: value</c> per line; a line without a colon is ignored.</summary>
    private static IReadOnlyDictionary<string, string>? ParseHeaders(string? headers)
    {
        if (string.IsNullOrWhiteSpace(headers)) return null;

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headers.Replace("\r\n", "\n").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var header = line[..colon].Trim();
            if (header.Length == 0) continue;
            parsed[header] = line[(colon + 1)..].Trim();
        }

        return parsed.Count == 0 ? null : parsed;
    }
}
