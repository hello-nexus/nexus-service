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
    private const string LightingPath = @"Software\Microsoft\Lighting";
    private const string AmbientEnabledValue = "AmbientLightingEnabled";

    /// <summary>HID Lighting And Illumination page, LampArray usage - what Windows itself selects on.</summary>
    private const int LampArrayUsagePage = 0x59;
    private const int LampArrayUsage = 0x01;

    /// <summary>Whether this platform can carry the setting at all.</summary>
    public static bool IsSupported() => OperatingSystem.IsWindows();

    /// <summary>A missing value reads as ON: Windows writes it only once the page is touched, and it defaults to on.</summary>
    public static bool DwordIsOn(object? value) => value is not int number || number != 0;

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
    public static WindowsDynamicLightingState Read()
    {
        var state = new WindowsDynamicLightingState();
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath);
        if (sid is null) return state;

        try
        {
            using var root = Registry.Users.OpenSubKey($@"{sid}\{LightingPath}");
            if (root is null) return state;

            state.Available = true;
            state.Enabled = DwordIsOn(root.GetValue(AmbientEnabledValue));
            // Windows keeps a Devices entry for every LampArray it has ever
            // seen, so counting subkeys reports hardware that is not plugged in.
            state.DeviceCount = PresentLampArrayCount();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting read failed: {ex.Message}");
        }
        return state;
    }

    /// <summary>Turns Windows' own lighting on or off, and returns the state re-read afterwards.</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsDynamicLightingState Write(bool enabled)
    {
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(LightingPath);
        if (sid is null) return new WindowsDynamicLightingState();

        try
        {
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

        return Read();
    }

    /// <summary>Connected LampArrays. Walks every HID interface, opening each one query-only.</summary>
    [SupportedOSPlatform("windows")]
    private static int PresentLampArrayCount()
    {
        try
        {
            return CountLampArrays(new WindowsHidEnumerator().FindAll());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] dynamic lighting device scan failed: {ex.Message}");
            return 0;
        }
    }
#else
    public static WindowsDynamicLightingState Read() => new();

    public static WindowsDynamicLightingState Write(bool enabled) => new();
#endif
}
