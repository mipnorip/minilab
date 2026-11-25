using System.Text;
using Microsoft.Extensions.Logging;

namespace EJL.Util;

/// <summary>Минимальный логгер в файл data/ejl.log (для WinExe-публикации без консоли).</summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly FileLogger _logger = new(path);

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose() { }
}

public sealed class FileLogger(string path) : ILogger
{
    private readonly object _gate = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" [").Append(logLevel.ToString().ToUpperInvariant()).Append("] ")
                .Append(formatter(state, exception));
            if (exception is not null)
                line.AppendLine().Append(exception);
            lock (_gate)
            {
                File.AppendAllText(path, line.ToString() + Environment.NewLine);
            }
        }
        catch { /* лог не должен ронять приложение */ }
    }
}
