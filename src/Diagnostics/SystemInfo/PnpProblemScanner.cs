using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Nexus.Service.Platform.Windows;

namespace Nexus.Service.Diagnostics.SystemInfo;

public sealed record PnpProblemDevice(string Name, string DeviceId, int ProblemCode, string ProblemText);

public sealed record PnpProblemSnapshot(bool Supported, IReadOnlyList<PnpProblemDevice> Devices)
{
    public static readonly PnpProblemSnapshot Unsupported = new(false, Array.Empty<PnpProblemDevice>());
}

/// <summary>
/// Scans present devnodes for a Device Manager problem code (excluding the
/// user-chosen CM_PROB_DISABLED) through cfgmgr32. Windows-only;
/// self-gates on <see cref="OperatingSystem.IsWindows"/>. Result is cached for
/// 5 minutes - this is a diagnostics-page read, not a live poll.
/// </summary>
public sealed class PnpProblemScanner
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Floor below which a forceRefresh request is served from cache anyway, so
    // a stuck client retry loop cannot make this rescan continuously.
    private static readonly TimeSpan ForceRefreshFloor = TimeSpan.FromSeconds(5);
    private const int CmProbDisabled = 22;

    private readonly object _gate = new();
    private PnpProblemSnapshot _cached = PnpProblemSnapshot.Unsupported;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public PnpProblemSnapshot Snapshot(bool forceRefresh = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PnpProblemSnapshot.Unsupported;
        }

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var honorForce = forceRefresh && now - _cachedAtUtc >= ForceRefreshFloor;
            if (!honorForce && now - _cachedAtUtc < CacheTtl)
            {
                return _cached;
            }

            _cached = Scan();
            _cachedAtUtc = now;
            return _cached;
        }
    }

    [SupportedOSPlatform("windows")]
    private static PnpProblemSnapshot Scan()
    {
        try
        {
            var present = WindowsDevnodes.EnumeratePresent();
            return new PnpProblemSnapshot(true, Filter(present.Select(d => (d.Name, d.InstanceId, d.ProblemCode))));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pnp-problems] scan failed: {ex.Message}");
            return new PnpProblemSnapshot(true, Array.Empty<PnpProblemDevice>());
        }
    }

    /// <summary>Keeps devnodes with a problem code, dropping the user-chosen disabled state.</summary>
    internal static IReadOnlyList<PnpProblemDevice> Filter(IEnumerable<(string Name, string DeviceId, uint ProblemCode)> devices)
    {
        var result = new List<PnpProblemDevice>();
        foreach (var (name, deviceId, code) in devices)
        {
            if (code == 0 || code == CmProbDisabled) continue;
            result.Add(new PnpProblemDevice(name, deviceId, (int)code, ProblemCodeName((int)code)));
        }
        return result;
    }

    // Common Device Manager (cfgmgr32.h CM_PROB_*) codes. Unknown codes fall
    // back to a generic CM_PROB_<n> label rather than dropping the device.
    internal static string ProblemCodeName(int code) => code switch
    {
        1 => "CM_PROB_NOT_CONFIGURED",
        3 => "CM_PROB_OUT_OF_MEMORY",
        10 => "CM_PROB_FAILED_START",
        12 => "CM_PROB_NORMAL_CONFLICT",
        14 => "CM_PROB_NEED_RESTART",
        18 => "CM_PROB_REINSTALL",
        21 => "CM_PROB_WILL_BE_REMOVED",
        22 => "CM_PROB_DISABLED",
        24 => "CM_PROB_DEVICE_NOT_THERE",
        28 => "CM_PROB_FAILED_INSTALL",
        31 => "CM_PROB_FAILED_ADD",
        37 => "CM_PROB_FAILED_DRIVER_ENTRY",
        39 => "CM_PROB_DRIVER_FAILED_LOAD",
        43 => "CM_PROB_FAILED_POST_START",
        45 => "CM_PROB_PHANTOM",
        _ => $"CM_PROB_{code}",
    };
}
