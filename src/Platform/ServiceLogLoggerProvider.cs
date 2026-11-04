using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Nexus.Service.Platform;

/// <summary>
/// Routes Microsoft.Extensions.Logging output through <see cref="ServiceLog"/> so framework and
/// ILogger&lt;T&gt; entries land in nexus-service.log as one canonical line instead of the console
/// provider's two-line, untimestamped, untagged block.
/// </summary>
internal sealed class ServiceLogLoggerProvider : ILoggerProvider
{
    /// <summary>Swaps the console provider for this one. Other registered providers (such as ErrorLoggerProvider) are kept.</summary>
    public static void ReplaceConsoleProvider(ILoggingBuilder logging)
    {
        foreach (var d in logging.Services
            .Where(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(ConsoleLoggerProvider))
            .ToList())
        {
            logging.Services.Remove(d);
        }
        logging.AddProvider(new ServiceLogLoggerProvider());
    }

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, LogLineFormat.Kebab(categoryName));

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly string _tag;

        public Logger(string category, string tag)
        {
            _category = category;
            _tag = tag;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var text = LogLineFormat.Fold(formatter(state, exception));
            // A message that already carries its own [tag] keeps it; the category tag would double it.
            var message = LogLineFormat.TryTakeTag(text, out _, out _) ? text : $"[{_tag}] {text}";
            if (logLevel >= LogLevel.Warning)
            {
                // The kebab tag keeps only the last segment, so the full category rides along where it matters.
                message += $" cat={_category}";
                if (eventId.Id != 0)
                    message += $" event={eventId.Id}";
            }
            if (exception is not null)
                message += $" ex={LogLineFormat.Fold(exception.ToString())}";
            if (logLevel >= LogLevel.Error) ServiceLog.Error(message);
            else if (logLevel == LogLevel.Warning) ServiceLog.Warn(message);
            else ServiceLog.Info(message);
        }
    }
}
