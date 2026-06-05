using System.Net.Http;

namespace VisualMCP.Implementation.Web;

internal static class DownloadFileImpl
{
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VisualMCP/1.0";

    internal static async Task<object> RunAsync(string url, string? outputPath, string? userAgent, int timeoutSeconds, int maxPreviewChars)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new { error = $"Invalid URL: {url}" };
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new { error = $"Only http/https URLs are supported (got '{uri.Scheme}')." };

        if (timeoutSeconds < 1) timeoutSeconds = 1;
        if (timeoutSeconds > 600) timeoutSeconds = 600;
        if (maxPreviewChars < 0) maxPreviewChars = 0;
        if (maxPreviewChars > 20000) maxPreviewChars = 20000;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var contentType = response.Content.Headers.ContentType?.ToString();

            if (!response.IsSuccessStatusCode)
                return new { url, statusCode = (int)response.StatusCode, status = response.ReasonPhrase, contentType, downloaded = false };

            long bytes;
            string? savedTo = null;
            string? preview = null;

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var full = Path.GetFullPath(outputPath);
                var dirName = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

                await using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None))
                await using (var src = await response.Content.ReadAsStreamAsync())
                    await src.CopyToAsync(fs);

                bytes = new FileInfo(full).Length;
                savedTo = full;
                if (maxPreviewChars > 0) preview = ReadPreview(full, maxPreviewChars);
            }
            else
            {
                var data = await response.Content.ReadAsByteArrayAsync();
                bytes = data.Length;
                if (maxPreviewChars > 0)
                {
                    var text = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, maxPreviewChars * 4));
                    preview = text.Length > maxPreviewChars ? text[..maxPreviewChars] + "…(truncated)" : text;
                }
            }

            return new
            {
                url,
                finalUrl = response.RequestMessage?.RequestUri?.ToString(),
                statusCode = (int)response.StatusCode,
                contentType,
                downloaded = true,
                bytes,
                sizeMB = Math.Round(bytes / 1024d / 1024d, 3),
                savedTo,
                preview,
            };
        }
        catch (TaskCanceledException)
        {
            return new { url, error = $"Download timed out after {timeoutSeconds}s." };
        }
        catch (Exception ex)
        {
            return new { url, error = $"Download failed: {ex.Message}" };
        }
    }

    private static string? ReadPreview(string file, int maxChars)
    {
        try
        {
            var buf = new char[maxChars];
            using var reader = new StreamReader(file, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var n = reader.Read(buf, 0, maxChars);
            var s = new string(buf, 0, n);
            return reader.Peek() >= 0 ? s + "…(truncated)" : s;
        }
        catch { return null; }
    }
}
