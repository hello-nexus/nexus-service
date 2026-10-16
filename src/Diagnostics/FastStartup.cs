#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Diagnostics;

/// <summary>Effective Windows Fast Startup state, derived from the same raw values BootTypeProbe logs.</summary>
internal readonly record struct FastStartupState(bool Supported, bool Enabled, bool PolicyManaged, bool HibernateAvailable)
{
    internal static readonly FastStartupState Unsupported = new(false, false, false, false);

    /// <summary>
    /// A set policy overrides the user value. HibernateEnabled is the live setting; the default only
    /// applies while it is unset. Fast Startup does nothing without hibernation.
    /// </summary>
    internal static FastStartupState Derive(int? hiberboot, int? policyHiberboot, int? hibernate, int? hibernateDefault)
    {
        var hibernateAvailable = (hibernate ?? hibernateDefault) == 1;
        var enabled = (policyHiberboot ?? hiberboot) == 1 && hibernateAvailable;
        return new FastStartupState(true, enabled, policyHiberboot is not null, hibernateAvailable);
    }
}

internal static class FastStartup
{
#if WINDOWS
    internal static FastStartupState Read() => FastStartupState.Derive(
        BootTypeProbe.ReadDword(BootTypeProbe.HiberbootKey, "HiberbootEnabled"),
        BootTypeProbe.ReadDword(BootTypeProbe.HiberbootPolicyKey, "HiberbootEnabled"),
        BootTypeProbe.ReadDword(BootTypeProbe.PowerKey, "HibernateEnabled"),
        BootTypeProbe.ReadDword(BootTypeProbe.PowerKey, "HibernateEnabledDefault"));

    internal static bool Write(bool enabled)
    {
        using var key = Registry.LocalMachine.OpenSubKey(BootTypeProbe.HiberbootKey, writable: true);
        if (key is null) return false;
        key.SetValue("HiberbootEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
        return true;
    }
#else
    internal static FastStartupState Read() => FastStartupState.Unsupported;

    internal static bool Write(bool enabled) => false;
#endif
}
