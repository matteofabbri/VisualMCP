using System.ComponentModel;
using ModelContextProtocol.Server;
using Snitch.Runtime;

namespace VisualMCP.Tools.Runtime;

[McpServerToolType]
public static class RunHttpFileTool
{
    [McpServerTool(Name = "run_http_file"), Description(
        "Execute a REST-client request file (.http/.rest, the Rider and VS Code format) and report each " +
        "request: status, content type, duration and body. Supports @name = value variables with {{name}} " +
        "substitution, ### separators, an optional '# @name' per request, headers and a body — one call for " +
        "a whole request file instead of one http_invoke per request. Variables that stay unresolved are " +
        "listed, and failFast stops at the first non-2xx response.")]
    public static Task<HttpFileRunResult> RunHttpFile(
        IHttpFileService httpFile,
        [Description("Path of the .http/.rest file (absolute, or relative to the solution).")] string path,
        [Description("Stop at the first failed request (default false: every request is sent).")] bool failFast = false,
        [Description("Timeout per request in seconds (default 30, max 300).")] int timeoutSeconds = 30,
        [Description("Characters of each response body kept in the answer (default 4000).")] int maxBodyChars = 4000)
        => httpFile.RunAsync(path, failFast, timeoutSeconds, maxBodyChars);
}
