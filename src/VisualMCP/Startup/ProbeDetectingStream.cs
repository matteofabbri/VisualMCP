namespace VisualMCP.Startup;

/// <summary>
/// Read-only wrapper around the server's stdin that looks at the first JSON-RPC message
/// before the protocol layer sees it.
/// <para>
/// ZCode spawns one extra process per session whose only job is to call
/// <c>server/discover</c> and <c>tools/list</c>. That process performs the eager solution
/// load like any other, paying the full cost (Sperone: 4 solutions) for a connection that
/// will never call a tool. Its first message is the tell: a real client starts with
/// <c>initialize</c>, the probe starts with <c>server/discover</c>.
/// </para>
/// </summary>
internal sealed class ProbeDetectingStream(Stream inner) : Stream
{
    private const int MaxScanBytes = 8192;

    private readonly byte[] _prefix = new byte[MaxScanBytes];
    private int _prefixLength;
    private bool _decided;

    private volatile bool _firstMessageSeen;
    private volatile bool _isDiscoveryProbe;

    /// <summary>True once at least one message has arrived from the client.</summary>
    public bool FirstMessageSeen => _firstMessageSeen;

    /// <summary>True when the first message was <c>server/discover</c> (a discovery probe).</summary>
    public bool IsDiscoveryProbe => _isDiscoveryProbe;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = inner.ReadAsync(buffer, cancellationToken);
        return read.IsCompletedSuccessfully
            ? new ValueTask<int>(InspectAndReturn(read.Result, buffer))
            : AwaitReadAsync(read, buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async ValueTask<int> AwaitReadAsync(ValueTask<int> pending, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = await pending.ConfigureAwait(false);
        return InspectAndReturn(read, buffer);
    }

    private int InspectAndReturn(int read, ReadOnlyMemory<byte> buffer)
    {
        if (read > 0) Inspect(buffer.Span[..read]);
        return read;
    }

    private void Inspect(ReadOnlySpan<byte> chunk)
    {
        _firstMessageSeen = true;
        if (_decided) return;

        var copy = Math.Min(chunk.Length, MaxScanBytes - _prefixLength);
        chunk[..copy].CopyTo(_prefix.AsSpan(_prefixLength));
        _prefixLength += copy;

        var complete = _prefix.AsSpan(0, _prefixLength).IndexOf((byte)'\n');
        if (complete < 0 && _prefixLength < MaxScanBytes) return;

        // The first message decides: only server/discover marks the discovery probe.
        var firstMessage = complete >= 0
            ? _prefix.AsSpan(0, complete)
            : _prefix.AsSpan(0, _prefixLength);
        _isDiscoveryProbe = firstMessage.IndexOf("server/discover"u8) >= 0;
        _decided = true;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
