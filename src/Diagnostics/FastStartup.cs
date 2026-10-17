#if WINDOWS
using System;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Nexus.Service.Platform;
#endif

namespace Nexus.Service.Diagnostics;

/// <summary>Effective Windows Fast Startup state, derived from the same raw values BootTypeProbe logs.</summary>
internal readonly record struct FastStartupState(bool Supported, bool Enabled, bool PolicyManaged, bool HibernateAvailable)
{
    internal static readonly FastStartupState Unsupported = new(false, false, false, false);

    /// <summary>
    /// Policy 1 forces Fast Startup on; policy 0 defers to the local setting, so only 1 counts as managed.
    /// HibernateEnabled is the live setting; the default only applies while it is unset.
    /// Fast Startup does nothing without hibernation.
    /// </summary>
    internal static FastStartupState Derive(int? hiberboot, int? policyHiberboot, int? hibernate, int? hibernateDefault)
    {
        var hibernateAvailable = (hibernate ?? hibernateDefault) == 1;
        var policyManaged = policyHiberboot == 1;
        var enabled = (policyManaged || hiberboot == 1) && hibernateAvailable;
        return new FastStartupState(true, enabled, policyManaged, hibernateAvailable);
    }
}

internal static class FastStartup
{
#if WINDOWS
    internal static FastStartupState Read()
    {
        try
        {
            return FastStartupState.Derive(
                BootTypeProbe.ReadDword(BootTypeProbe.HiberbootKey, "HiberbootEnabled"),
                BootTypeProbe.ReadDword(BootTypeProbe.HiberbootPolicyKey, "HiberbootEnabled"),
                BootTypeProbe.ReadDword(BootTypeProbe.PowerKey, "HibernateEnabled"),
                BootTypeProbe.ReadDword(BootTypeProbe.PowerKey, "HibernateEnabledDefault"));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            ServiceLog.Warn($"[service-control] fast startup read failed: {ex.Message}");
            // Unreadable state reports hibernation unavailable so the toggle stays locked.
            return new FastStartupState(true, false, false, false);
        }
    }

    internal static bool Write(bool enabled)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BootTypeProbe.HiberbootKey, writable: true);
            if (key is null) return false;
            key.SetValue("HiberbootEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            ServiceLog.Warn($"[service-control] fast startup write failed: {ex.Message}");
            return false;
        }
    }
#else
    internal static FastStartupState Read() => FastStartupState.Unsupported;

    internal static bool Write(bool enabled) => false;
#endif
}
