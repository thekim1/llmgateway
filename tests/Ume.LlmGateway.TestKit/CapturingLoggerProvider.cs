using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Ume.LlmGateway.TestKit;

/// <summary>One captured log entry; <see cref="ToString"/> is <c>category|event name|message</c>, then the exception on its own lines.</summary>
public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)
{
    public override string ToString() =>
        $"{Category}|{EventId.Name}|{Message}" + (Exception is null ? string.Empty : "\n" + Exception);
}

/// <summary>Keeps every log entry of a host, at every level, so tests can check what is (and is not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries;

    /// <summary>All entries, one <see cref="LogEntry.ToString"/> per line.</summary>
    public string Text => string.Join("\n", _entries);

    /// <summary>Whether an entry of <paramref name="category"/> contains <paramref name="text"/> in its message.</summary>
    public bool Logged(string category, string text) =>
        _entries.Any(e => e.Category == category && e.Message.Contains(text, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
