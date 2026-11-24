using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DNSManagerPro.Infrastructure;

/// <summary>Minimal daily-rolling file logger (keeps 14 days). Avoids a heavy logging dependency.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOld();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, n => new FileLogger(n, this));

    public void Dispose() => _loggers.Clear();

    private void Write(string line)
    {
        var path = Path.Combine(_directory, $"log-{DateTime.Now:yyyyMMdd}.txt");
        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never crash the app.
            }
        }
    }

    private void PruneOld()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, "log-*.txt"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14))
                {
                    File.Delete(f);
                }
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var msg = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff} [{logLevel,-11}] {category}: {formatter(state, exception)}");
            if (exception is not null)
            {
                msg += Environment.NewLine + exception;
            }

            owner.Write(msg);
        }
    }
}
