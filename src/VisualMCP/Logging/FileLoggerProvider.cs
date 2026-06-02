using Microsoft.Extensions.Logging;

namespace VisualMCP.Logging;

sealed class FileLoggerProvider : ILoggerProvider
{
    readonly StreamWriter _writer;

    public FileLoggerProvider(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine($"\n--- VisualMCP started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();
}

sealed class FileLogger(string category, StreamWriter writer) : ILogger
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
