using System;
using System.Threading;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Panel.Streams;

/// <summary>Why a panel's secondary monitor is not showing, or that it is.</summary>
public static class SecondaryMonitorStates
{
    public const string Starting = "starting";
    public const string Active = "active";
    public const string DriverMissing = "driver-missing";
    public const string Failed = "failed";
}

/// <summary>Creates a Windows monitor whose desktop pixels the service can read back.</summary>
public interface IVirtualMonitorHost
{
    /// <summary>The driver package ships with this install, so the setting is worth offering.</summary>
    bool IsAvailable { get; }

    /// <summary>Null when the monitor cannot be created; <paramref name="failureState"/> then says why.</summary>
    IVirtualMonitor? Create(int width, int height, CancellationToken ct, out string failureState);
}

/// <summary>A live virtual monitor. Disposing removes it from Windows.</summary>
public interface IVirtualMonitor : IDisposable
{
    /// <summary>Copies a BGRA desktop frame newer than the last one read into <paramref name="destination"/>; false when none arrived in time.</summary>
    bool TryReadFrame(byte[] destination, int timeoutMs, CancellationToken ct);

    /// <summary>Injects one touch pointer event at monitor pixel coordinates.</summary>
    bool InjectTouch(uint pointerId, TouchPhase phase, int x, int y);

    /// <summary>False once the monitor is gone from under us (its host exited, the device was removed).</summary>
    bool IsAlive { get; }
}

public sealed class NullVirtualMonitorHost : IVirtualMonitorHost
{
    public bool IsAvailable => false;

    public IVirtualMonitor? Create(int width, int height, CancellationToken ct, out string failureState)
    {
        failureState = SecondaryMonitorStates.Failed;
        return null;
    }
}
