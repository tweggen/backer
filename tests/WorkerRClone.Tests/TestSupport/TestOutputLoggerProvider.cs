using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace WorkerRClone.Tests.TestSupport;

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that forwards log lines to the xUnit test output,
/// so the opt-in live check can actually report what happened.
/// </summary>
public sealed class TestOutputLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public TestOutputLoggerProvider(ITestOutputHelper output) => _output = output;

    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(_output, categoryName);

    public void Dispose()
    {
    }

    private sealed class TestOutputLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly string _category;

        public TestOutputLogger(ITestOutputHelper output, string category)
        {
            _output = output;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                var shortCategory = _category.Split('.')[^1];
                _output.WriteLine($"[{logLevel}] {shortCategory}: {formatter(state, exception)}");
                if (exception is not null)
                {
                    _output.WriteLine($"    {exception.GetType().Name}: {exception.Message}");
                }
            }
            catch (InvalidOperationException)
            {
                // Test already finished - nothing sensible to do with the line.
            }
        }
    }
}
