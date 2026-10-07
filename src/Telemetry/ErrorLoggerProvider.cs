using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Telemetry;

/// <summary>Forwards Error/Critical log entries that carry an exception (unhandled request exceptions, crashed hosted services) to <see cref="ErrorReporter"/>. Observes only; the entry still reaches every other provider.</summary>
internal sealed class ErrorLoggerProvider : ILoggerProvider
{
    private readonly IServiceProvider _services;

    public ErrorLoggerProvider(IServiceProvider services) => _services = services;

    public ILogger CreateLogger(string categoryName) => new ErrorLogger(this, categoryName);

    public void Dispose()
    {
    }

    // Resolved lazily: the reporter depends on services that may themselves log during construction.
    internal ErrorReporter? Reporter()
    {
        try { return _services.GetService<ErrorReporter>(); }
        catch { return null; }
    }

    private sealed class ErrorLogger : ILogger
    {
        private readonly ErrorLoggerProvider _owner;
        private readonly string _category;

        public ErrorLogger(ErrorLoggerProvider owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || exception is null)
                return;
            _owner.Reporter()?.Report(exception, ErrorKinds.Logged, _category);
        }
    }
}
