using Microsoft.Extensions.Logging;

namespace WorkerGit.Tests.TestSupport;

/**
 * One captured log call - mirrors tests/Hannibal.Tests/TestSupport/CapturingLogger.cs
 * (kept local rather than shared across test projects). Used by the Gate E
 * guard tests to assert AC9: a tripped guard logs at Warning under its own,
 * distinct EventId.
 */
public sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }
}
