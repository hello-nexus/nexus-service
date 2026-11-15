using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nexus.Service.Devices;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Diagnostic only: names the vendor app likely holding a device whose open was denied. Its process
/// lists feed no conflict detection, kill, pause or recovery path.
/// </summary>
public static class AccessDeniedHolder
{
    /// <summary>Background services that hold the device with no app window running.</summary>
    private static readonly Dictionary<string, string[]> ServiceProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lian-li-l-connect"] = new[] { "L-Connect-Service", "L-Connect-Service-Watcher" },
    };

    public static string? Find(string handlerId) => Find(handlerId, IsProcessRunning);

    internal static string? Find(string handlerId, Func<string, bool> processRunning)
    {
        var appId = DeviceControlPolicy.ConflictAppFor(handlerId);
        if (appId is null)
        {
            return null;
        }
        var names = new List<string>();
        if (ServiceProcesses.TryGetValue(appId, out var services))
        {
            names.AddRange(services);
        }
        foreach (var def in ConflictAppCatalog.All)
        {
            if (string.Equals(def.Id, appId, StringComparison.OrdinalIgnoreCase))
            {
                names.AddRange(def.ProcessNames);
            }
        }
        foreach (var name in names)
        {
            if (processRunning(name))
            {
                return appId;
            }
        }
        return null;
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var procs = Process.GetProcessesByName(name);
            foreach (var p in procs)
            {
                p.Dispose();
            }
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
