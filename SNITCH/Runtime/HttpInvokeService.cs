using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Snitch.Runtime;

/// <summary>Sends an HTTP request to any URL and returns status, body, content-type and timing.</summary>
public interface IHttpInvokeService
{
    Task<object> HttpInvoke(string method, string url, string? body, string? headersJson, int timeoutSeconds);
}

public sealed class HttpInvokeService : IHttpInvokeService
{
    // Shared client with a permissive handler for local dev/self-signed certs.
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }) { Timeout = Timeout.InfiniteTimeSpan };  // per-request CancellationToken controls timeout

    public async Task<object> HttpInvoke(
        string method,
        string url,
        string? body = null,
        string? headersJson = null,
        int timeoutSeconds = 30)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)));
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);

            // Apply custom headers.
            if (!string.IsNullOrWhiteSpace(headersJson))
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (headers is not null)
                    foreach (var (k, v) in headers)
                        request.Headers.TryAddWithoutValidation(k, v);
            }

            // Attach body for methods that normally carry one.
            if (!string.IsNullOrWhiteSpace(body))
            {
                var trimmed = body.TrimStart();
                var mediaType = trimmed.StartsWith('{') || trimmed.StartsWith('[')
                    ? "application/json"
                    : "text/plain";
                request.Content = new StringContent(body, Encoding.UTF8, mediaType);

                // If caller also passed a Content-Type header, honour it.
                if (!string.IsNullOrWhiteSpace(headersJson))
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    if (headers is not null && headers.TryGetValue("Content-Type", out var ct))
                        request.Content.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue(ct.Split(';')[0].Trim());
                }
            }

            using var response = await _http.SendAsync(request, cts.Token);
            sw.Stop();

            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
            var contentType   = response.Content.Headers.ContentType?.ToString() ?? "unknown";

            return new
            {
                statusCode  = (int)response.StatusCode,
                statusText  = response.StatusCode.ToString(),
                contentType,
                elapsedMs   = sw.ElapsedMilliseconds,
                body        = Truncate(responseBody),
                headers     = response.Headers
                                      .Concat(response.Content.Headers)
                                      .ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
            };
        }
        catch (OperationCanceledException)
        {
            return new { error = $"Request timed out after {timeoutSeconds}s.", elapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, elapsedMs = sw.ElapsedMilliseconds };
        }
    }

    private static string Truncate(string s, int max = 32_000) =>
        s.Length <= max ? s : s[..max] + $"\n…(truncated, {s.Length - max} more chars)";
}
