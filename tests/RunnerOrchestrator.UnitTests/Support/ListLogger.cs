using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace RunnerOrchestrator.UnitTests.Support;

/// <summary>Captures every formatted message and exception text, to search for secrets.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public string AllText => string.Join('\n', Entries);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
