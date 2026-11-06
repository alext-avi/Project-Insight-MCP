using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Keeps every formatted log message and exception the app writes.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            lock (owner.Lines)
                owner.Lines.Add($"{category} {level}: {format(state, ex)} {ex}");
        }
    }
}
