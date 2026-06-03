using Microsoft.Extensions.Logging;

namespace VisualMCP.Logging;

sealed class FileLoggerProvider : ILoggerProvider
{
    readonly TextWriter _writer;

    public FileLoggerProvider(string path)
    {
        _writer = OpenResilient(path);
        _writer.WriteLine($"\n--- VisualMCP started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) ---");
    }

    // Logging must never prevent the server from starting. Open the shared log
    // tolerating concurrent instances; if it is held exclusively by a stale
    // process, fall back to a per-process file, and ultimately to a no-op.
    static TextWriter OpenResilient(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return CreateShared(path);
        }
        catch
        {
            try
            {
                var alt = Path.Combine(Path.GetDirectoryName(path)!, $"debug.{Environment.ProcessId}.log");
                return CreateShared(alt);
            }
            catch
            {
                return TextWriter.Null;
            }
        }
    }

    static TextWriter CreateShared(string path)
    {
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        return TextWriter.Synchronized(new StreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true });
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();
}

sealed class FileLogger(string category, TextWriter writer) : ILogger
{
    [ThreadStatic]
    private static Stack<object?>? _scopes;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        _scopes ??= new Stack<object?>();
        _scopes.Push(state);
        return new ScopeDisposable();
    }

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, ex);

        // Detect tool invocations for prominent logging
        if (message.Contains("tools/call") && message.Contains("request handler called"))
        {
            var toolName = TryExtractToolNameFromScope();
            var callLine = toolName is not null
                ? $">>> TOOL CALL: {toolName}"
                : ">>> TOOL CALL (name unavailable)";
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [TOOL ] {callLine}");
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {category}: {message}";
        if (ex is not null)
            line += $"\n  {ex}";
        writer.WriteLine(line);
    }

    private static string? TryExtractToolNameFromScope()
    {
        if (_scopes is null) return null;
        foreach (var scope in _scopes)
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Key.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Contains("name", StringComparison.OrdinalIgnoreCase))
                        return kv.Value?.ToString();
                }
            }
            else if (scope is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }
        return null;
    }

    private sealed class ScopeDisposable : IDisposable
    {
        public void Dispose() { _scopes?.TryPop(out _); }
    }
}
