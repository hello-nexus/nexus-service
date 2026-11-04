using System;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Conflicts;

/// <summary>
/// The one Windows Dynamic Lighting setting that decides whether Windows drives
/// the same HID LampArray devices as Nexus; its other settings decide nothing
/// while this one is off. The value name is the one
/// <c>C:\Windows\System32\SettingsHandlers_Lighting.dll</c> carries, in the
/// interactive user's hive - a LocalSystem service reaches it through
/// <see cref="Nexus.Service.Lifecycle.ConsoleUserSid"/>, never HKCU.
/// </summary>
public static class WindowsDynamicLighting
{
    /// <summary>Catalog id of the conflict entry this setting surfaces as (<see cref="ConflictAppDefinition.IsSetting"/>).</summary>
    public const string ConflictId = "windows-dynamic-lighting";

    private const string LightingPath = @"Software\Microsoft\Lighting";
    private const string AmbientEnabledValue = "AmbientLightingEnabled";

    /// <summary>HID Lighting And Illumination page, LampArray usage - what Windows itself selects on.</summary>
    private const int LampArrayUsagePage = 0x59;
    private const int LampArrayUsage = 0x01;

    /// <summary>Whether this platform can carry the setting at all.</summary>
    public static bool IsSupported() => OperatingSystem.IsWindows();

    /// <summary>A missing value reads as ON: Windows writes it only once the page is touched, and it defaults to on.</summary>
    public static bool DwordIsOn(object? value) => value is not int number || number != 0;

    /// <summary>
    /// A conflict only while Windows can actually drive something: the setting
    /// reads on AND at least one LampArray is present.
    /// </summary>
    public static bool IsActiveConflict(WindowsDynamicLightingState state) =>
        state.Available && state.Enabled && state.DeviceCount > 0;

    /// <summary>One-line state for the startup diagnostics and the support bundle; a fresh full read, since both are rare.</summary>
    public static string Describe()
    {
        if (!IsSupported()) return "unsupported on this platform";
        try
        {
            var s = Read();
            return $"available={s.Available} enabled={s.Enabled} lampArrays={s.DeviceCount}";
        }
        catch (Exception ex)
        {
            return $"read failed: {ex.Message}";
        }
    }

    /// <summary>How many of a HID enumeration are LampArrays - the usage is all that separates one from a mouse.</summary>
    public static int CountLampArrays(IEnumerable<HidDeviceInfo> devices)
    {
        var count = 0;
        foreach (var hid in devices)
        {
            if (hid.UsagePage == LampArrayUsagePage && hid.Usage == LampArrayUsage) count++;
        }
        return count;
    }

#if WINDOWS
    /// <summary>
    /// Current state, or <see cref="WindowsDynamicLightingState.Available"/>
    /// false when the console user's Lighting key cannot be read (no console
    /// user, unloaded profile, or a Windows build without the feature).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Read() => ReadState(cachedCount: false);

    /// <summary>
    /// For the conflict watcher's poll: the registry value is read live, but the
    /// HID walk behind the device count is reused for <see cref="DeviceCountTtl"/>
    /// and skipped entirely while the setting is off (DeviceCount stays 0).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState ReadCached() => ReadState(cachedCount: true);

    /// <summary>Available and Enabled only, read live with no LampArray count and no HID walk: for callers that check or act on the registry value alone (turn-off, and the re-read after it).</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState ReadEnabled() => ReadState(cachedCount: false, countDevices: false);

    internal static readonly TimeSpan DeviceCountTtl = TimeSpan.FromSeconds(30);
    private static readonly object CountLock = new();
    private static long _countAtMs = long.MinValue / 2;
    private static int _countCached;

    [SupportedOSPlatform("windows")]
    private static int LampArrayCountCached()
    {
        lock (CountLock)
        {
            var now = Environment.TickCount64;
            if (now - _countAtMs < (long)DeviceCountTtl.TotalMilliseconds) return _countCached;
            _countCached = PresentLampArrayCount();
            _countAtMs = now;
            return _countCached;
        }
    }

    [SupportedOSPlatform("windows")]
    private static WindowsDynamicLightingState ReadState(bool cachedCount, bool countDevices = true)
    {
        var state = new WindowsDynamicLightingState();
        try
        {
            // A PC without the Lighting key reads as unavailable, quietly: the
            // poll would otherwise log the same warning every scan.
            var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath, quietIfHiveMissing: true);
            if (sid is null) return state;

            using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}");
            if (root is null) return state;

            state.Available = true;
            state.Enabled = DwordIsOn(root.GetValue(AmbientEnabledValue));
            if (!countDevices) return state;
            // Windows keeps a Devices entry for every LampArray it has ever
            // seen, so counting subkeys reports hardware that is not plugged in.
            if (!cachedCount)
            {
                state.DeviceCount = PresentLampArrayCount();
                return state;
            }
            // Off to on is when a count matters again, and the one cached while off is stale.
            if (state.Enabled && !_wasEnabled) InvalidateDeviceCount();
            _wasEnabled = state.Enabled;
            state.DeviceCount = state.Enabled ? LampArrayCountCached() : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting read failed: {ex.Message}");
        }
        return state;
    }

    private static volatile bool _wasEnabled;

    private static void InvalidateDeviceCount()
    {
        lock (CountLock) _countAtMs = long.MinValue / 2;
    }

    /// <summary>
    /// Turns Windows' own lighting on or off, and returns the state re-read
    /// afterwards. <paramref name="countDevices"/> false skips the HID walk
    /// behind <see cref="WindowsDynamicLightingState.DeviceCount"/>, for a
    /// caller that only needs Available and Enabled.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Write(bool enabled, bool countDevices = true)
    {
        try
        {
            var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath, quietIfHiveMissing: true);
            if (sid is null) return new WindowsDynamicLightingState();

            using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}", writable: true);
            // A null open here is the access-denied case, which otherwise
            // reaches the user as a toggle that simply does not move.
            if (root is null) Console.Error.WriteLine($"[conflicts] dynamic lighting: cannot open {LightingPath} for write");
            root?.SetValue(AmbientEnabledValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting write failed: {ex.Message}");
        }

        InvalidateDeviceCount();
        return ReadState(cachedCount: false, countDevices);
    }

    /// <summary>Connected LampArrays. Walks every HID interface, opening each one query-only.</summary>
    [SupportedOSPlatform("windows")]
    private static int PresentLampArrayCount()
    {
        try
        {
            return CountLampArrays(new WindowsHidEnumerator().FindAllWithoutSerials());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting device scan failed: {ex.Message}");
            return 0;
        }
    }
#else
    public static WindowsDynamicLightingState Read() => new();

    public static WindowsDynamicLightingState ReadCached() => new();

    public static WindowsDynamicLightingState ReadEnabled() => new();

    public static WindowsDynamicLightingState Write(bool enabled, bool countDevices = true) => new();
#endif
}
