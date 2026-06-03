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
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, ex);

        // Tool-call lines (category "VisualMCP.ToolCalls", emitted by
        // LoggingDelegatingTool) get a prominent tag so they stand out in the log.
        var tag = category == "VisualMCP.ToolCalls" ? "TOOL " : $"{level,-5}";

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {category}: {message}";
        if (ex is not null)
            line += $"\n  {ex}";
        writer.WriteLine(line);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
