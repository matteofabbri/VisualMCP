using System.Diagnostics;
using System.Text;

namespace Snitch.Runtime;

/// <summary>One request of a <c>.http</c> file, with the placeholders still unresolved.</summary>
public sealed record HttpFileRequest(
    int Index,
    string? Name,
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    int Line);

/// <summary>A parsed <c>.http</c> file: its variables and its requests in file order.</summary>
public sealed record HttpFileDocument(
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<HttpFileRequest> Requests);

/// <summary>
/// Runs a REST-client request file (<c>.http</c>/<c>.rest</c>, the Rider and VS Code format): <c>@name = value</c>
/// variables, <c>###</c> separators, an optional <c># @name</c> per request, a request line, headers and a body.
/// Every request is sent in order and its status, timing and body are reported — one call for a whole
/// request file instead of one <c>http_invoke</c> per request.
/// </summary>
public interface IHttpFileService
{
    Task<HttpFileRunResult> RunAsync(string path, bool failFast, int timeoutSeconds, int maxBodyChars);
}

public sealed class HttpFileService : IHttpFileService
{
    private const int MaxRedirects = 5;

    public async Task<HttpFileRunResult> RunAsync(string path, bool failFast, int timeoutSeconds, int maxBodyChars)
    {
        if (string.IsNullOrWhiteSpace(path)) return new HttpFileRunResult { Error = "A .http file path is required." };
        if (!File.Exists(path)) return new HttpFileRunResult { Error = $"File not found: {path}" };

        var text = await File.ReadAllTextAsync(path);
        var document = Parse(text);
        if (document.Requests.Count == 0)
            return new HttpFileRunResult
            {
                Error = $"No request found in {Path.GetFileName(path)}: expected a 'METHOD url' line under a ### separator.",
                Path = path,
            };

        var timeout = Math.Clamp(timeoutSeconds < 1 ? 30 : timeoutSeconds, 1, 300);
        var bodyLimit = Math.Clamp(maxBodyChars < 200 ? 4000 : maxBodyChars, 200, 200_000);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var results = new List<HttpFileRequestResult>();
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var ran = 0;
        var failed = 0;

        foreach (var request in document.Requests)
        {
            var url = Substitute(request.Url, document.Variables, unresolved);
            var body = request.Body is null ? null : Substitute(request.Body, document.Variables, unresolved);
            var headers = request.Headers.ToDictionary(
                entry => Substitute(entry.Key, document.Variables, unresolved),
                entry => Substitute(entry.Value, document.Variables, unresolved),
                StringComparer.OrdinalIgnoreCase);

            var (result, succeeded) = await SendAsync(client, request, url, body, headers, timeout, bodyLimit);
            results.Add(result);
            ran++;
            if (!succeeded) failed++;
            if (!succeeded && failFast) break;
        }

        return new HttpFileRunResult
        {
            Path = path,
            File = Path.GetFileName(path),
            VariableCount = document.Variables.Count,
            RequestCount = document.Requests.Count,
            Ran = ran,
            Failed = failed,
            Skipped = document.Requests.Count - ran,
            Succeeded = failed == 0,
            StoppedEarly = failFast && ran < document.Requests.Count,
            UnresolvedVariables = unresolved.ToList(),
            Requests = results,
            Note = failFast && ran < document.Requests.Count
                ? "failFast stopped the run at the first failure; the remaining requests were not sent."
                : null,
        };
    }

    /// <summary>Parses the file: public so the format is testable without sending anything.</summary>
    public static HttpFileDocument Parse(string text)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requests = new List<HttpFileRequest>();

        string? pendingName = null;
        string? method = null;
        string? url = null;
        int requestLine = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = new StringBuilder();
        var inBody = false;

        void Flush()
        {
            if (method is not null && url is not null)
                requests.Add(new HttpFileRequest(
                    requests.Count + 1,
                    pendingName,
                    method,
                    url,
                    new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                    body.Length > 0 ? body.ToString().TrimEnd('\r', '\n') : null,
                    requestLine));

            pendingName = null;
            method = null;
            url = null;
            requestLine = 0;
            headers.Clear();
            body.Clear();
            inBody = false;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (inBody)
            {
                body.Append(line).Append('\n');
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
                    pendingName = directive[5..].Trim();
                else if (directive.StartsWith("name", StringComparison.OrdinalIgnoreCase) && method is null)
                    pendingName = directive[4..].Trim();
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            // A variable definition before any request line of this block.
            if (url is null && trimmed.StartsWith('@'))
            {
                var equals = trimmed.IndexOf('=');
                if (equals > 1)
                {
                    var name = trimmed[1..equals].Trim();
                    var value = trimmed[(equals + 1)..].Trim();
                    if (value.Length > 0) variables[name] = value;
                    continue;
                }
            }

            if (url is null)
            {
                var (parsedMethod, parsedUrl) = ParseRequestLine(trimmed);
                if (parsedUrl is null) continue; // not a request line yet

                method = parsedMethod;
                url = parsedUrl;
                requestLine = index + 1;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon > 0)
            {
                var name = trimmed[..colon].Trim();
                var value = trimmed[(colon + 1)..].Trim();
                headers[name] = value;
            }
        }

        Flush();
        return new HttpFileDocument(variables, requests);
    }

    /// <summary><c>METHOD url</c> when the first token is a known method, otherwise <c>GET url</c>.</summary>
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

    private static readonly HashSet<string> KnownMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE",
    };

    private static string Substitute(string value, IReadOnlyDictionary<string, string> variables, ISet<string> unresolved)
    {
        if (!value.Contains("{{", StringComparison.Ordinal)) return value;

        var result = value;
        for (var pass = 0; pass < 3; pass++)
        {
            var replaced = false;
            foreach (var (name, variable) in variables)
            {
                var token = "{{" + name + "}}";
                if (!result.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                result = result.Replace(token, variable, StringComparison.OrdinalIgnoreCase);
                replaced = true;
            }

            if (!replaced) break;
        }

        foreach (var start in AllIndexes(result, "{{"))
        {
            var end = result.IndexOf("}}", start, StringComparison.Ordinal);
            if (end > start + 2) unresolved.Add(result[(start + 2)..end]);
        }

        return result;
    }

    private static IEnumerable<int> AllIndexes(string text, string token)
    {
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(token, index, StringComparison.Ordinal);
            if (found < 0) yield break;
            yield return found;
            index = found + token.Length;
        }
    }

    private static async Task<(HttpFileRequestResult Result, bool Succeeded)> SendAsync(
        HttpClient client,
        HttpFileRequest request,
        string url,
        string? body,
        Dictionary<string, string> headers,
        int timeoutSeconds,
        int bodyLimit)
    {
        var stopwatch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), url);

            string? contentType = null;
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    contentType = value;
            }

            if (body is not null)
            {
                var mediaType = contentType
                                ?? (body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')
                                    ? "application/json"
                                    : "text/plain");
                message.Content = new StringContent(body, Encoding.UTF8, mediaType.Split(';')[0].Trim());
                if (contentType is not null && contentType.Contains(';', StringComparison.Ordinal))
                    message.Content.Headers.ContentType =
                        System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            }

            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                if (!message.Headers.TryAddWithoutValidation(name, value) && message.Content is not null)
                    message.Content.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(message, cancellation.Token);
            var responseBody = await response.Content.ReadAsStringAsync(cancellation.Token);
            stopwatch.Stop();

            var succeeded = (int)response.StatusCode is >= 200 and < 300;
            var result = new HttpFileRequestResult
            {
                Index = request.Index,
                Name = request.Name,
                Line = request.Line,
                Method = request.Method,
                Url = url,
                StatusCode = (int)response.StatusCode,
                StatusText = response.StatusCode.ToString(),
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "unknown",
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Succeeded = succeeded,
                Body = Truncate(responseBody, bodyLimit),
            };

            return (result, succeeded);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return (new HttpFileRequestResult
            {
                Index = request.Index,
                Name = request.Name,
                Line = request.Line,
                Method = request.Method,
                Url = url,
                Succeeded = false,
                Error = $"Timed out after {timeoutSeconds}s.",
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return (new HttpFileRequestResult
            {
                Index = request.Index,
                Name = request.Name,
                Line = request.Line,
                Method = request.Method,
                Url = url,
                Succeeded = false,
                Error = ex.Message,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
            }, false);
        }
    }

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + $"…(truncated, {value.Length - limit} more chars)";
}
