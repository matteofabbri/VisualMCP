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
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {category}: {formatter(state, ex)}";
        if (ex is not null)
            line += $"\n  {ex}";
        writer.WriteLine(line);
    }
}
