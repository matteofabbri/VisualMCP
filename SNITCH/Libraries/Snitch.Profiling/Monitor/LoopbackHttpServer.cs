using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Snitch.Profiling.Monitor;

/// <summary>A parsed HTTP request: method, decoded path and query string values.</summary>
internal sealed record HttpRequestInfo(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query);

/// <summary>
/// What a route answers: an in-memory body, or a file that is streamed (a dump is too large to
/// read into memory). Content-Length is computed for both.
/// </summary>
internal sealed record HttpResponseInfo(
    int StatusCode,
    string ContentType,
    byte[]? Body = null,
    string? FilePath = null,
    string? DownloadName = null);

/// <summary>
/// A minimal HTTP/1.1 server bound to one loopback endpoint. It exists so the native monitor is a
/// first-class part of this library without an ASP.NET Core framework reference: the profiling
/// library is host-agnostic and is also loaded by tests and by the engine. One request per
/// connection, <c>Connection: close</c>, no chunked encoding — enough for a probe and for the
/// documented endpoints.
/// </summary>
internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private const int MaxHeaderLineLength = 8 * 1024;
    private const int MaxHeaderLines = 64;

    private readonly TcpListener _listener;
    private readonly Func<HttpRequestInfo, CancellationToken, Task<HttpResponseInfo>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public LoopbackHttpServer(
        IPAddress address,
        int port,
        Func<HttpRequestInfo, CancellationToken, Task<HttpResponseInfo>> handler)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
    }

    /// <summary>The actual port, which is how a caller that passed port 0 learns the address.</summary>
    public int Port { get; private set; }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // The listener was stopped while accept was pending.
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream? stream = null;
            try
            {
                stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;

                if (requestLine.Length > MaxHeaderLineLength)
                {
                    await WriteAsync(stream, "GET", new HttpResponseInfo(414, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("Request line too long."))).ConfigureAwait(false);
                    return;
                }

                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                {
                    await WriteAsync(stream, "GET", Error(400, "Malformed request line.")).ConfigureAwait(false);
                    return;
                }

                var method = parts[0].ToUpperInvariant();
                var target = parts[1];

                for (var i = 0; i < MaxHeaderLines; i++)
                {
                    var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(line))
                        break;
                }

                if (method is not ("GET" or "HEAD"))
                {
                    await WriteAsync(stream, method, Error(405, "Only GET and HEAD are supported.")).ConfigureAwait(false);
                    return;
                }

                var (path, query) = ParseTarget(target);
                HttpResponseInfo response;
                try
                {
                    response = await _handler(new HttpRequestInfo(method, path, query), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    response = Error(500, ex.Message);
                }

                await WriteAsync(stream, method, response).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A probe that disconnected mid-request is not an error worth reporting.
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string method, HttpResponseInfo response)
    {
        long contentLength;
        if (response.FilePath is not null)
        {
            contentLength = new FileInfo(response.FilePath).Length;
        }
        else
        {
            contentLength = response.Body?.LongLength ?? 0;
        }

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(StatusText(response.StatusCode)).Append("\r\n");
        head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(contentLength).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n");
        if (response.DownloadName is not null)
            head.Append("Content-Disposition: attachment; filename=\"").Append(response.DownloadName).Append("\"\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (method == "HEAD")
            return;

        if (response.FilePath is not null)
        {
            await using var file = new FileStream(response.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, useAsync: true);
            await file.CopyToAsync(stream).ConfigureAwait(false);
        }
        else if (response.Body is { Length: > 0 } body)
        {
            await stream.WriteAsync(body).ConfigureAwait(false);
        }
    }

    private static (string Path, IReadOnlyDictionary<string, string> Query) ParseTarget(string target)
    {
        var questionMark = target.IndexOf('?');
        var rawPath = questionMark < 0 ? target : target[..questionMark];
        var rawQuery = questionMark < 0 ? "" : target[(questionMark + 1)..];

        var path = Uri.UnescapeDataString(rawPath);
        if (!path.StartsWith('/'))
            path = "/" + path;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? "" : pair[(equals + 1)..];
            if (key.Length == 0)
                continue;
            query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return (path, query);
    }

    private static HttpResponseInfo Error(int status, string message) =>
        new(status, "application/json; charset=utf-8",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { error = message }));

    private static string StatusText(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        414 => "URI Too Long",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "OK",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* the loop ends with the listener */ }
        }
        _cts.Dispose();
    }
}
