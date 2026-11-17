using Microsoft.Extensions.Logging;

namespace UnifiMcp.Tests;

/// <summary>
/// Collects every formatted log message (and its exception text) so tests can read what the operator would see. Each
/// <see cref="PublicHarness"/> adds one as a provider; the host's own level filters still apply.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Line)> _entries = new();
    public IReadOnlyList<string> Lines => [.. _entries.Select(e => e.Line)];
    /// <summary>The same lines with their level.</summary>
    public IReadOnlyList<(LogLevel Level, string Line)> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);
    public void Dispose() { }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<(LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            entries.Enqueue((logLevel, exception is null ? line : $"{line}\n[{category}] {exception}"));
        }
    }
}
