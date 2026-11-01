using System;
using System.Linq;

namespace Nexus.Service.Telemetry;

/// <summary>Faults whose source is a task, thread or log entry owned by the runtime or a bundled library, so no Nexus call site can observe or prevent them. Reporting them only buries real rows.</summary>
internal static class ErrorNoise
{
    private const string SerialEventLoopFrame = "System.IO.Ports.SerialStream.EventLoopRunner";
    private const string LhmMemoryRetryFrame = "LibreHardwareMonitor.Hardware.Memory.MemoryGroup";
    private const string KestrelCategory = "Microsoft.AspNetCore.Server.Kestrel";

    /// <summary>An unobserved task that only carries the serial event loop outliving its closed port, or the memory-group retry loop of the bundled hardware monitor.</summary>
    internal static bool IsUnobservedLibraryTeardown(AggregateException ex)
    {
        var inner = ex.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(e => IsLibraryTeardown(e, e.StackTrace));
    }

    internal static bool IsLibraryTeardown(Exception e, string? stack)
    {
        if (stack is null)
            return false;
        // The loop keeps waiting on a handle that Close or a removed USB device already released.
        if (e is ObjectDisposedException && stack.Contains(SerialEventLoopFrame, StringComparison.Ordinal))
            return true;
        return e is NullReferenceException && stack.Contains(LhmMemoryRetryFrame, StringComparison.Ordinal);
    }

    /// <summary>Kestrel logs at Error when a client leaves mid-response: a download cut short of its Content-Length, or a TLS write to a connection the peer already closed.</summary>
    internal static bool IsKestrelClientDisconnect(string category, Exception ex)
    {
        if (!category.StartsWith(KestrelCategory, StringComparison.Ordinal))
            return false;
        return ex switch
        {
            InvalidOperationException => ex.Message.StartsWith("Response Content-Length mismatch: too few bytes written", StringComparison.Ordinal),
            System.IO.IOException => ex.Message.StartsWith("The encryption operation failed", StringComparison.Ordinal),
            _ => false,
        };
    }
}
