using Microsoft.Extensions.Logging;

namespace Nexus.Service.Platform;

/// <summary>
/// Routes Microsoft.Extensions.Logging output through <see cref="ServiceLog"/> so framework and
/// ILogger&lt;T&gt; entries land in nexus-service.log as one canonical line instead of the console
/// provider's two-line, untimestamped, untagged block.
/// </summary>
internal sealed class ServiceLogLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(LogLineFormat.Kebab(categoryName));

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _tag;

        public Logger(string tag) => _tag = tag;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var text = LogLineFormat.Fold(formatter(state, exception));
            if (exception is not null)
                text = $"{text} ex={LogLineFormat.Fold(exception.ToString())}";
            var message = $"[{_tag}] {text}";
            if (logLevel >= LogLevel.Error) ServiceLog.Error(message);
            else if (logLevel == LogLevel.Warning) ServiceLog.Warn(message);
            else ServiceLog.Info(message);
        }
    }
}
