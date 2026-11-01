using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Telemetry;

/// <summary>Forwards Error/Critical log entries that carry an exception (unhandled request exceptions, crashed hosted services) to <see cref="ErrorReporter"/>. Observes only; the entry still reaches every other provider.</summary>
internal sealed class ErrorLoggerProvider : ILoggerProvider
{
    [ThreadStatic] private static bool t_inLog;

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

        private static bool IsMarked(Exception ex)
        {
            try { return ex.Data.Contains(ErrorKinds.ReportedMarker); }
            catch { return false; }
        }

        // Kestrel's request-scoped entries carry the request id as a structured value.
        private static string? TraceIdOf<TState>(TState state)
        {
            if (state is System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    if (key == "TraceIdentifier")
                        return value as string;
                }
            }
            return null;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || exception is null)
                return;
            if (t_inLog || IsMarked(exception))
                return;
            // A peer's malformed TLS handshake: SChannel returns a status SslStream cannot map, and Kestrel logs it at Error.
            if (_category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal)
                && exception.GetType().FullName == "System.Net.InternalException")
            {
                return;
            }
            // A client that left mid-response: Kestrel logs the cut-short download and the dead-socket TLS write at Error.
            if (ErrorNoise.IsKestrelClientDisconnect(_category, exception, ErrorNoise.IsCutShortResponse(exception) && ClientAbortLedger.Take(TraceIdOf(state))))
            {
                return;
            }
            t_inLog = true;
            try
            {
                _owner.Reporter()?.Report(exception, ErrorKinds.Logged, _category);
            }
            finally
            {
                t_inLog = false;
            }
        }
    }
}
