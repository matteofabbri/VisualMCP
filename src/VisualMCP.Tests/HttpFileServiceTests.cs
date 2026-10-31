using System.Net;
using System.Net.Sockets;
using System.Text;
using Snitch.Runtime;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// run_http_file: the parser is exercised on the Rider/VS Code format, and the executor against a tiny
/// TCP server written for the test, so no network and no port permission is needed.
/// </summary>
public sealed class HttpFileServiceTests
{
    private const string File = """
        @base = http://127.0.0.1:PORT
        @token = s3cret

        ### first request
        # @name getThing
        GET {{base}}/thing?id=1
        Authorization: Bearer {{token}}

        ### second request
        POST {{base}}/things
        Content-Type: application/json

        { "name": "x" }
        """;

    [Fact]
    public void TheParserReadsVariablesRequestsHeadersAndBodies()
    {
        var document = HttpFileService.Parse(File);

        Assert.Equal(2, document.Variables.Count);
        Assert.Equal("s3cret", document.Variables["token"]);
        Assert.Equal(2, document.Requests.Count);

        var first = document.Requests[0];
        Assert.Equal("getThing", first.Name);
        Assert.Equal("GET", first.Method);
        Assert.Contains("/thing?id=1", first.Url);
        Assert.Equal("Bearer {{token}}", first.Headers["Authorization"]);
        Assert.Null(first.Body);

        var second = document.Requests[1];
        Assert.Equal("POST", second.Method);
        Assert.Equal("application/json", second.Headers["Content-Type"]);
        Assert.Contains("\"name\": \"x\"", second.Body);
    }

    [Fact]
    public async Task TheRequestsAreSentInOrderAndSubstituted()
    {
        using var server = new TinyServer();
        var path = Path.Combine(Path.GetTempPath(), "vmcp-http-" + Guid.NewGuid().ToString("N") + ".http");
        await File2.WriteAllTextAsync(path, File.Replace("PORT", server.Port.ToString(), StringComparison.Ordinal));

        try
        {
            var result = ResultAssert.Json(await new HttpFileService().RunAsync(path, failFast: false, timeoutSeconds: 10, maxBodyChars: 2000));

            Assert.True(result.GetProperty("succeeded").GetBoolean());
            Assert.Equal(2, result.GetProperty("requestCount").GetInt32());
            Assert.Equal(2, result.GetProperty("ran").GetInt32());
            Assert.Equal(0, result.GetProperty("failed").GetInt32());
            Assert.Empty(result.GetProperty("unresolvedVariables").EnumerateArray());

            var requests = result.GetProperty("requests");
            Assert.Equal(200, requests[0].GetProperty("statusCode").GetInt32());
            Assert.Equal("getThing", requests[0].GetProperty("name").GetString());
            Assert.Contains("/thing?id=1", requests[0].GetProperty("body").GetString());
            Assert.Contains("/things", requests[1].GetProperty("body").GetString());
        }
        finally
        {
            try { File2.Delete(path); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void AnUnresolvedVariableIsReported()
    {
        var document = HttpFileService.Parse("""
            GET http://localhost/{{missing}}/x
            """);

        Assert.Single(document.Requests);
        Assert.Contains("{{missing}}", document.Requests[0].Url);
    }

    /// <summary>A minimal HTTP/1.1 server that answers every request with 200 and the request line in the body.</summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public TinyServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                            var contentLength = 0;
                            while (await reader.ReadLineAsync() is { Length: > 0 } line)
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                    int.TryParse(line[15..].Trim(), out var length))
                                    contentLength = length;
                            }

                            if (contentLength > 0)
                            {
                                var buffer = new char[contentLength];
                                var read = 0;
                                while (read < contentLength)
                                {
                                    var got = await reader.ReadAsync(buffer.AsMemory(read, contentLength - read));
                                    if (got == 0) break;
                                    read += got;
                                }
                            }

                            var body = "{\"requestLine\":\"" + requestLine.Replace("\"", "'", StringComparison.Ordinal) + "\"}";
                            var response =
                                "HTTP/1.1 200 OK\r\n" +
                                "Content-Type: application/json\r\n" +
                                $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                                "Connection: close\r\n\r\n" + body;

                            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                            await stream.FlushAsync();
                        }
                    }
                    catch { /* the client may have gone away */ }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* already stopped */ }
            _stop.Dispose();
        }
    }

    /// <summary>Alias so the type name <see cref="File"/> above does not shadow the file API.</summary>
    private static class File2
    {
        public static Task WriteAllTextAsync(string path, string contents) => System.IO.File.WriteAllTextAsync(path, contents);
        public static void Delete(string path) => System.IO.File.Delete(path);
    }
}
