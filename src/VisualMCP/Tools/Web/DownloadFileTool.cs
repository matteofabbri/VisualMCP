using System.ComponentModel;
using ModelContextProtocol.Server;
using VisualMCP.Implementation.Web;

namespace VisualMCP.Tools.Web;

[McpServerToolType]
public static class DownloadFileTool
{
    [McpServerTool(Name = "download_file"), Description(
        "Download a file from an http/https URL (follows redirects, sets a User-Agent) and either save it to a " +
        "path or return a text preview, reporting status, content-type and size. Use this INSTEAD OF a shell " +
        "'Invoke-WebRequest'/'curl' to fetch a dataset, asset or release artifact and verify it.")]
    public static Task<object> DownloadFile(
        [Description("The http/https URL to download.")] string url,
        [Description("Optional: file path to save to. If omitted, a text preview of the content is returned instead of saving.")] string? outputPath = null,
        [Description("Optional: User-Agent header (some hosts require a browser-like UA).")] string? userAgent = null,
        [Description("Timeout in seconds (default: 120, max: 600).")] int timeoutSeconds = 120,
        [Description("Characters of text preview to return (0 to skip; default: 2000, max: 20000).")] int maxPreviewChars = 2000)
        => DownloadFileImpl.RunAsync(url, outputPath, userAgent, timeoutSeconds, maxPreviewChars);
}
