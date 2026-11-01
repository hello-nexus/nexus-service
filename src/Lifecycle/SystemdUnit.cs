using System;
using System.IO;

namespace Nexus.Service.Lifecycle;

/// <summary>Whether this process was started by the nexus.service systemd unit. INVOCATION_ID cannot tell: every process under a systemd user service (a terminal, the desktop shell that launches a nexus:// handler) inherits it.</summary>
internal static class SystemdUnit
{
    internal const string NexusUnitName = "nexus.service";

    /// <summary>True when any line of <c>/proc/self/cgroup</c> (unified "0::/path" or hybrid "N:controllers:/path") names a cgroup ending in the unit.</summary>
    internal static bool IsNexusUnit(string[] cgroupLines)
    {
        foreach (var line in cgroupLines)
        {
            var colon = line.IndexOf(':');
            var second = colon < 0 ? -1 : line.IndexOf(':', colon + 1);
            if (second < 0)
            {
                continue;
            }
            var path = line[(second + 1)..].TrimEnd();
            if (path.EndsWith("/" + NexusUnitName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool CurrentProcessIsNexusUnit()
    {
        try
        {
            return OperatingSystem.IsLinux() && IsNexusUnit(File.ReadAllLines("/proc/self/cgroup"));
        }
        catch
        {
            return false;
        }
    }
}
