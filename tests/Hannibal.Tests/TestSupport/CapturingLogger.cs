using Microsoft.Extensions.Logging;

namespace Hannibal.Tests.TestSupport;

/// <summary>
/// A single captured log call.
/// </summary>
public sealed record LogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);

/// <summary>
/// An <see cref="ILogger{TCategoryName}"/> that records every call so tests can
/// assert that a failure was actually reported and not swallowed silently.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception));
    }
}
