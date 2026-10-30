using System.Text;
using System.Text.Json;

namespace Snitch.Debugging;

/// <summary>Serialises DAP packets onto a stream with the <c>Content-Length</c> header framing.</summary>
public static class DapProtocol
{
    /// <summary>Writes one JSON body as a framed DAP message.</summary>
    public static async Task WriteAsync(Stream stream, string json, CancellationToken cancellationToken = default)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed message body. Returns null at end of stream; throws when the framing is
    /// corrupt (a header without Content-Length, a body shorter than declared).
    /// </summary>
    public static async Task<string?> ReadBodyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var line = new StringBuilder();

        while (true)
        {
            var value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (value < 0) return null;

            if (value != '\n')
            {
                line.Append((char)value);
                if (line.Length > 8192)
                    throw new InvalidDataException("A DAP header line exceeded 8 KB; the stream is not DAP.");
                continue;
            }

            var text = line.ToString().TrimEnd('\r');
            line.Clear();

            if (text.Length == 0)
            {
                if (headers.Count == 0) continue; // a blank line before the first header
                break;
            }

            var colon = text.IndexOf(':');
            if (colon > 0)
                headers[text[..colon].Trim()] = text[(colon + 1)..].Trim();
        }

        if (!headers.TryGetValue("Content-Length", out var raw) ||
            !int.TryParse(raw, out var length) || length < 0)
            throw new InvalidDataException("A DAP message arrived without a valid Content-Length header.");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                .ConfigureAwait(false);
            if (count <= 0)
                throw new EndOfStreamException("The DAP stream ended in the middle of a message body.");
            read += count;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var one = new byte[1];
        var count = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
        return count <= 0 ? -1 : one[0];
    }
}

/// <summary>The adapter answered a request with success=false, or the transport broke mid-request.</summary>
public sealed class DebugAdapterException : Exception
{
    public DebugAdapterException(string message) : base(message) { }
    public DebugAdapterException(string message, Exception inner) : base(message, inner) { }
}
