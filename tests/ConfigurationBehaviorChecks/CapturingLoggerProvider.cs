namespace ConfigurationBehaviorChecks;

[ProviderAlias("Capture")]
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(string Category, LogLevel Level, string Message)> Events { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Events);

    public void Dispose() { }

    private sealed class CapturingLogger(
        string category,
        List<(string Category, LogLevel Level, string Message)> events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) events.Add((category, logLevel, formatter(state, exception)));
        }
    }
}
